using SpawnDev.SpawnJS.JSObjects;
using System.Text;

namespace SpawnDev.NexStar;

/// <summary>
/// Prolific PL2303 USB-to-Serial adapter support via Web USB API.
/// </summary>
public class ProlificSerialWebUSB : ProlificSerial
{
    private readonly USBDevice _device;
    private USBEndpoint? _inEndpoint;
    private USBEndpoint? _outEndpoint;
    private bool ComsEnabled = false;
    private CancellationTokenSource? _cancelComsTokenSource;
    private Task? _readingTask;
    private readonly List<byte> _responseBuffer = new();
    private TaskCompletionSource<byte[]>? _pendingResponse;
    private readonly SemaphoreSlim _commandLimiter = new(1, 1);
    private readonly object _bufferLock = new();

    /// <summary>Timeout for post-open connection verification (echo test).</summary>
    private const int VerifyTimeoutMs = 2500;

    /// <summary>Delay after starting read loop before running verification (lets Android/device settle).</summary>
    private const int PostOpenDelayMs = 1000;

    public override bool Connected => ComsEnabled && _device?.Opened == true;

    // PL2303 Standard Requests
    private const byte SET_LINE_CODING = 0x20;
    private const byte GET_LINE_CODING = 0x21;
    private const byte SET_CONTROL_LINE_STATE = 0x22;

    /// <summary>
    /// New ProlificSerial instance for the specified USBDevice.
    /// </summary>
    /// <param name="device"></param>
    public ProlificSerialWebUSB(USBDevice device)
    {
        _device = device;
    }

    /// <summary>
    /// Open the PL2303 device with the specified baud rate.
    /// </summary>
    /// <param name="serialOptions"></param>
    /// <returns></returns>
    public override async Task<bool> OpenAsync(SerialOptions? serialOptions = null)
    {
        if (_device == null)
        {
            JS.Log("ProlificSerialWebUSB: Open failed — device is null.");
            return false;
        }
        if (Connected) return true;
        if (ComsEnabled)
        {
            JS.Log("ProlificSerialWebUSB: Open failed — already enabled.");
            return false;
        }


        try
        {
            ComsEnabled = true;
            serialOptions ??= DefaultSerialOptions;

            await _device.Open();
            await _device.SelectConfiguration(1);

            await _device.ClaimInterface(
                _device.Configuration!.Interfaces[0].InterfaceNumber
            );

            // --- 1. PROLIFIC INITIALIZATION SEQUENCE (From folleon/pl2303-webusb) ---
            await VendorRead(0x8484, 0);
            await VendorWrite(0x0404, 0);
            await VendorRead(0x8484, 0);
            await VendorRead(0x8383, 0);
            await VendorRead(0x8484, 0);
            await VendorWrite(0x0404, 1);
            await VendorRead(0x8484, 0);
            await VendorRead(0x8383, 0);
            await VendorWrite(0, 1);
            await VendorWrite(1, 0);
            await VendorWrite(2, 0x44);

            // --- 2. CONFIGURE SERIAL PORT ---
            // Set Baud Rate etc via Get-Modify-Set
            await SetSerialOptionsAsync(serialOptions);

            // --- 3. POST-CONFIG RESETS (From folleon/pl2303-webusb) ---
            // No flow control
            await VendorWrite(0, 0);
            // Reset upstream data pipes
            await VendorWrite(8, 0);
            await VendorWrite(9, 0);

            // --- 4. ASSERT CONTROL LINES ---
            // Often required for the attached device (hand controller) to wake up.
            await SetControlSignal(true, true);

            // --- 5. ENDPOINT DISCOVERY ---
            USBInterface? iface = _device.Configuration?.Interfaces?.FirstOrDefault();
            if (iface?.Alternates == null || iface.Alternates.Length == 0)
            {
                throw new InvalidOperationException("No interfaces/alternates on device configuration.");
            }
            var alt = iface.Alternates[0];
            if (alt.Endpoints == null)
            {
                throw new InvalidOperationException("No endpoints on interface alternate.");
            }

            _inEndpoint = null;
            _outEndpoint = null;
            foreach (var ep in alt.Endpoints.ToArray())
            {
                if (ep.Type == "bulk")
                {
                    if (ep.Direction == "in") _inEndpoint = ep;
                    else if (ep.Direction == "out") _outEndpoint = ep;
                }
            }

            if (_inEndpoint == null || _outEndpoint == null)
            {
                throw new InvalidOperationException(
                    "PL2303 bulk IN/OUT endpoints not found. Check interface/alternate or OS driver.");
            }

            // --- 6. START READ LOOP ---
            ComsEnabled = true;
            _cancelComsTokenSource = new CancellationTokenSource();
            _readingTask = ReadLoopAsync(_cancelComsTokenSource.Token);

            // Allow read loop and device to settle
            await Task.Delay(PostOpenDelayMs);

            // --- 7. VERIFY CONNECTION ---
            if (!await VerifyConnectionAsync())
            {
                JS.Log("ProlificSerialWebUSB: Connection verification failed (no echo response).");
                await CloseAsync();
                return false;
            }

            StatusChanged();
            ConnectedEvent();
            return true;
        }
        catch (Exception ex)
        {
            JS.Log("ProlificSerialWebUSB Open failed", $"{ex.Message}\n{ex.StackTrace ?? ""}");
            await CloseAsync();
            return false;
        }
    }

    /// <summary>
    /// Verifies that the serial link is working by sending a NexStar echo command (K + char) and checking the response.
    /// </summary>
    private async Task<bool> VerifyConnectionAsync()
    {
        if (!ComsEnabled || _outEndpoint == null) return false;

        byte[] echoCmd = { (byte)'K', (byte)'A' };
        byte[]? response = null;
        try
        {
            // Give it 3 tries to verify with longer delays
            for (int i = 0; i < 3; i++)
            {
                response = await SendCommandAsync(echoCmd, VerifyTimeoutMs);
                if (response != null && response.Length >= 2) break;
                await Task.Delay(250);
            }
        }
        catch (Exception ex)
        {
            JS.Log("ProlificSerialWebUSB VerifyConnection SendCommand threw", ex.Message);
            return false;
        }

        if (response == null || response.Length < 2)
        {
            JS.Log("ProlificSerialWebUSB VerifyConnection: no response or too short.",
                response == null ? "null" : response.Length.ToString());
            return false;
        }

        int lastHash = -1;
        for (int i = 0; i < response.Length; i++)
        {
            if (response[i] == (byte)'#') { lastHash = i; break; }
        }
        if (lastHash < 0)
        {
            JS.Log("ProlificSerialWebUSB VerifyConnection: response did not end with #.");
            return false;
        }

        // Echo returns the character we sent ('A') then '#'.
        if (response[0] != (byte)'A')
        {
            JS.Log("ProlificSerialWebUSB VerifyConnection: echo character mismatch.",
                response[0].ToString("X2"));
            return false;
        }

        return true;
    }
    /// <summary>
    /// Set the SerialOptions for the PL2303 device.
    /// </summary>
    private async Task SetSerialOptionsAsync(SerialOptions serialOptions)
    {
        if (_device == null || !_device.Opened)
        {
            throw new InvalidOperationException("Device not open for SetSerialOptions.");
        }

        // 1. Read current line coding (GET_LINE_CODING = 0x21)
        var getSetup = new USBControlTransferParameters
        {
            RequestType = "class",
            Recipient = "interface",
            Request = GET_LINE_CODING,
            Value = 0,
            Index = 0
        };
        var currentCodingResult = await _device.ControlTransferIn(getSetup, 7);
        if (currentCodingResult == null || currentCodingResult.Data == null || currentCodingResult.Status != "ok")
        {
            throw new InvalidOperationException("Failed to read current line coding.");
        }

        using var dataView = currentCodingResult.Data;
        using var baudRateConfiguration = new DataView(dataView.Buffer);

        // 2. Modify buffer
        var baudRate = serialOptions.BaudRate;
        baudRateConfiguration.SetInt32(0, baudRate, true); // Little-endian
        baudRateConfiguration.SetInt8(4, serialOptions.StopBits switch { 1 => 0, 2 => 2, _ => 0 });
        baudRateConfiguration.SetInt8(5, serialOptions.Parity?.String?.ToLower() switch
        {
            "none" => 0,
            "odd" => 1,
            "even" => 2,
            "mark" => 3,
            "space" => 4,
            _ => 0
        });
        baudRateConfiguration.SetUint8(6, (byte)(serialOptions.DataBits ?? 8));

        // 3. Write back (SET_LINE_CODING = 0x20)
        using var jsBuffer = new Uint8Array(baudRateConfiguration.Buffer);
        var setSetup = new USBControlTransferParameters
        {
            RequestType = "class",
            Recipient = "interface",
            Request = SET_LINE_CODING,
            Value = 0,
            Index = 0
        };
        var outResult = await _device.ControlTransferOut(setSetup, jsBuffer);
        if (outResult?.Status != "ok")
        {
            JS.Log("ProlificSerialWebUSB SetSerialOptions ControlTransferOut status", outResult?.Status ?? "null");
        }
    }

    /// <summary>
    /// Set the DTR and RTS control lines.
    /// </summary>
    /// <param name="dtr">True to assert DTR</param>
    /// <param name="rts">True to assert RTS</param>
    public async Task SetControlSignal(bool dtr, bool rts)
    {
        if (_device == null || !_device.Opened) return;

        int value = 0;
        if (dtr) value |= 0x01; // DTR is bit 0
        if (rts) value |= 0x02; // RTS is bit 1
                                // Note: For PL2303, the value is the state of the control lines. 
                                // Index is usually 0.

        var setup = new USBControlTransferParameters
        {
            RequestType = "class",
            Recipient = "interface",
            Request = SET_CONTROL_LINE_STATE, // 0x22
            Value = value,
            Index = 0
        };

        try
        {
            await _device.ControlTransferOut(setup);
        }
        catch (Exception ex)
        {
            JS.Log($"ProlificSerialWebUSB SetControlSignal({dtr},{rts})", ex.Message);
            // We log but don't fail hard here as some subset chips might behave differently?
            // But usually this IS supported.
        }
    }

    /// <summary>
    /// Write bytes to the PL2303 device.
    /// </summary>
    private async Task WriteBytesAsync(byte[] data)
    {
        if (!ComsEnabled || _outEndpoint == null || _device == null || !_device.Opened)
        {
            throw new InvalidOperationException("Port not open or device disconnected.");
        }
        try
        {
            using var jsBuffer = new Uint8Array(data);
            var result = await _device.TransferOut(_outEndpoint.EndpointNumber, jsBuffer);
            if (result?.Status != null && result.Status != "ok")
            {
                JS.Log("ProlificSerialWebUSB TransferOut status", result.Status);
            }
        }
        catch (Exception ex)
        {
            JS.Log("ProlificSerialWebUSB WriteBytes failed", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Read data from the PL2303 device.
    /// </summary>
    private async Task<byte[]?> ReadAsync(int maxLen = 64)
    {
        if (!ComsEnabled || _inEndpoint == null || _device == null)
        {
            // JS.Log("!! TransferIn() - !ComsEnabled || _inEndpoint == null || _device == null", !ComsEnabled, _inEndpoint == null, _device == null);
            return null;
        }
        if (!_device.Opened)
        {
            // JS.Log("!! TransferIn() - !_device.Opened", _inEndpoint.EndpointNumber);
            return null;
        }

        try
        {
            var result = await _device.TransferIn(_inEndpoint.EndpointNumber, maxLen);

            if (result == null) return null;

            if (result.Status != "ok")
            {
                JS.Log("ProlificSerialWebUSB TransferIn status", result.Status ?? "null");
                return null;
            }

            if (result.Data == null) return null;
            using var dataView = result.Data;
            var bytes = dataView?.ReadBytes();
            return bytes ?? System.Array.Empty<byte>();
        }
        catch (Exception ex)
        {
            JS.Log("ProlificSerialWebUSB ReadAsync", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Background read loop: drains IN endpoint, buffers data, completes pending response on '#'.
    /// </summary>
    private async Task ReadLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && ComsEnabled)
        {
            try
            {
                var data = await ReadAsync(64);
                if (data == null || data.Length == 0)
                {
                    await Task.Delay(10, token);
                    continue;
                }

                DataReceived(data);
                lock (_bufferLock)
                {
                    _responseBuffer.AddRange(data);
                    int terminatorIndex = _responseBuffer.IndexOf((byte)'#');
                    if (terminatorIndex >= 0 && _pendingResponse != null)
                    {
                        var response = _responseBuffer.Take(terminatorIndex + 1).ToArray();
                        _responseBuffer.RemoveRange(0, terminatorIndex + 1);
                        _pendingResponse.TrySetResult(response);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ComsEnabled) break;
                JS.Log("ProlificSerialWebUSB ReadLoop", ex.Message);
                await Task.Delay(50);
            }
        }
    }

    // --- Helpers for Vendor Control Transfers ---

    private async Task VendorWrite(int value, int index)
    {
        var setup = new USBControlTransferParameters
        {
            RequestType = "vendor",
            Recipient = "device",
            Request = 0x01,
            Value = value,
            Index = index
        };
        try
        {
            await _device.ControlTransferOut(setup);
        }
        catch (Exception ex)
        {
            JS.Log($"ProlificSerialWebUSB VendorWrite({value},{index})", ex.Message);
            throw;
        }
    }

    private async Task<USBInTransferResult> VendorRead(int value, int index)
    {
        var setup = new USBControlTransferParameters
        {
            RequestType = "vendor",
            Recipient = "device",
            Request = 0x01,
            Value = value,
            Index = index
        };
        try
        {
            return await _device.ControlTransferIn(setup, 1);
        }
        catch (Exception ex)
        {
            JS.Log($"ProlificSerialWebUSB VendorRead({value:X},{index})", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Stops communication with the device.
    /// </summary>
    public override async Task CloseAsync()
    {
        if (!ComsEnabled && (_device == null || !_device.Opened))
            return;

        ComsEnabled = false;

        var cts = _cancelComsTokenSource;
        _cancelComsTokenSource = null;
        cts?.Cancel();

        var rt = _readingTask;
        _readingTask = null;

        if (rt != null)
        {
            try { await rt; } catch { }
        }

        cts?.Dispose();

        if (_device != null && _device.Opened)
        {
            try { await _device.Close(); } catch { }
        }

        StatusChanged();
        DisconnectedEvent();
    }

    /// <summary>
    /// Sends a command and waits for response terminated by '#'.
    /// </summary>
    public override async Task<byte[]?> SendCommandAsync(byte[] command, int timeoutMs = CommandTimeoutMs)
    {
        if (command == null || command.Length == 0)
        {
            JS.Log("ProlificSerialWebUSB SendCommand: null or empty command.");
            return null;
        }

        if (_device == null || !_device.Opened)
        {
            JS.Log("ProlificSerialWebUSB SendCommand: device not open.");
            MarkDisconnected();
            return null;
        }

        if (!ComsEnabled || _outEndpoint == null)
        {
            JS.Log("ProlificSerialWebUSB SendCommand: not enabled or no out endpoint.");
            return null;
        }

        var haveLock = false;
        try
        {
            await _commandLimiter.WaitAsync();
            haveLock = true;

            if (_device == null || !_device.Opened)
            {
                MarkDisconnected();
                return null;
            }

            lock (_bufferLock)
            {
                _responseBuffer.Clear();
                _pendingResponse = new TaskCompletionSource<byte[]>();
            }

            await WriteBytesAsync(command);

            using var cts = new CancellationTokenSource(timeoutMs);
            cts.Token.Register(() => _pendingResponse?.TrySetCanceled());

            var result = await _pendingResponse.Task;
            return result;
        }
        catch (OperationCanceledException)
        {
            JS.Log("ProlificSerialWebUSB SendCommand: timeout or canceled.");
            return null;
        }
        catch (Exception ex)
        {
            JS.Log("ProlificSerialWebUSB SendCommand error", ex.Message);
            if (_device == null || !_device.Opened)
                MarkDisconnected();
            return null;
        }
        finally
        {
            lock (_bufferLock)
            {
                _pendingResponse = null;
            }
            if (haveLock)
                _commandLimiter.Release();
        }
    }

    private void MarkDisconnected()
    {
        if (!ComsEnabled) return;
        ComsEnabled = false;
        StatusChanged();
        DisconnectedEvent();
    }

    /// <summary>
    /// Sends a string command and returns string response.
    /// </summary>
    public override async Task<string?> SendStringCommandAsync(string data, int timeoutMs = CommandTimeoutMs)
    {
        var response = await SendCommandAsync(Encoding.ASCII.GetBytes(data), timeoutMs);
        if (response == null) return null;
        return Encoding.ASCII.GetString(response).TrimEnd('#');
    }
}