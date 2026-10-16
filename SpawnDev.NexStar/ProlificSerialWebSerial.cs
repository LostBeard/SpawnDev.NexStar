using SpawnDev.SpawnJS.JSObjects;
using System.Text;

namespace SpawnDev.NexStar;

/// <summary>
/// Prolific PL2303 USB-to-Serial adapter support via Web Serial API.
/// </summary>
public class ProlificSerialWebSerial : ProlificSerial
{
    public override bool Connected => _port?.Connected == true && ComsEnabled;

    private CancellationTokenSource? CancelComsTokenSource = null;
    private readonly SerialPort _port;
    private bool ComsEnabled = false;
    private WritableStreamDefaultWriter? Writer = null;
    private Task? ReadingTask = null;

    /// <summary>
    /// New ProlificSerial instance for the specified SerialPort.
    /// </summary>
    /// <param name="port"></param>
    public ProlificSerialWebSerial(SerialPort port)
    {
        _port = port;
    }
    private void SerialPort_OnDisconnect(Event e)
    {
        _ = CloseAsync();
    }
    /// <summary>
    /// Open the PL2303 device with the specified baud rate.
    /// </summary>
    /// <param name="serialOptions"></param>
    /// <returns></returns>
    public override async Task<bool> OpenAsync(SerialOptions? serialOptions = null)
    {
        serialOptions ??= DefaultSerialOptions;
        if (ComsEnabled || _port == null) return false;
        ComsEnabled = true;
        try
        {
            await _port.Open(serialOptions);
            _port.OnDisconnect += SerialPort_OnDisconnect;
        }
        catch (Exception ex)
        {
            JS.Log("StartComs failed. Cannot open port.", ex.Message);
            ComsEnabled = false;
            return false;
        }

        try
        {
            Writer = _port.Writable.GetWriter();
        }
        catch (Exception ex)
        {
            JS.Log("GetWriter failed", ex.Message);
            try { await _port.Close(); } catch { }
            ComsEnabled = false;
            return false;
        }

        CancelComsTokenSource = new CancellationTokenSource();
        ReadingTask = ReadLoopAsync(_port, CancelComsTokenSource.Token);

        StatusChanged();
        ConnectedEvent();
        return true;
    }

    /// <summary>
    /// Stops communication with the telescope
    /// </summary>
    public override async Task CloseAsync()
    {
        if (!ComsEnabled) return;
        ComsEnabled = false;

        var cts = CancelComsTokenSource;
        CancelComsTokenSource = null;
        cts?.Cancel();

        var rt = ReadingTask;
        ReadingTask = null;

        Writer?.ReleaseLock();
        Writer?.Dispose();
        Writer = null;

        if (rt != null)
        {
            try { await rt; } catch { }
        }

        if (_port != null)
        {
            _port.OnDisconnect -= SerialPort_OnDisconnect;
            if (_port.Connected)
            {
                try { await _port.Close(); } catch { }
            }
        }

        cts?.Dispose();
        StatusChanged();
        DisconnectedEvent();
    }

    /// <summary>
    /// Takes back the permission the user gave this page for the port, so the browser forgets it.
    /// </summary>
    public override async Task Forget()
    {
        try { await _port.Forget(); } catch { }
    }

    /// <summary>
    /// Sends a command and returns its payload (see <see cref="INexStarTransport.SendCommandAsync"/>)
    /// </summary>
    public override async Task<byte[]?> SendCommandAsync(byte[] command, int timeoutMs = CommandTimeoutMs)
    {
        if (Writer == null || !ComsEnabled) return null;
        try
        {
            return await Channel.ExchangeAsync(command, async bytes =>
            {
                await Writer.Ready;
                await Writer.Write(bytes);
            }, timeoutMs);
        }
        catch (Exception ex)
        {
            JS.Log($"SendCommand error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Background read loop
    /// </summary>
    private async Task ReadLoopAsync(SerialPort port, CancellationToken token)
    {
        ReadableStream? readable;
        ReadableStreamDefaultReader? reader = null;
        token.Register(() => reader?.Cancel());
        while (!token.IsCancellationRequested && (readable = port.Readable) != null)
        {
            reader = readable.GetReader();
            try
            {
                while (true)
                {
                    using var readResponse = await reader.Read();
                    if (readResponse.Done) break;

                    var value = readResponse.Value;
                    if (value != null)
                    {
                        var bytes = value.ToArray();
                        DataReceived(bytes);
                        Channel.OnBytes(bytes);
                    }
                }
                reader.ReleaseLock();
            }
            catch
            {
                try { reader.ReleaseLock(); } catch { }
            }
        }
    }
}
