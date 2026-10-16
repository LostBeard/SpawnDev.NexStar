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
    private List<byte> _responseBuffer = new();
    private TaskCompletionSource<byte[]>? _pendingResponse = null;
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

    SemaphoreSlim _commandLimiter = new SemaphoreSlim(1, 1);

    /// <summary>
    /// Sends a command and waits for response terminated by '#'
    /// </summary>
    public override async Task<byte[]?> SendCommandAsync(byte[] command, int timeoutMs = CommandTimeoutMs)
    {
        if (Writer == null || !ComsEnabled) return null;
        var haveLock = false;
        try
        {
            await _commandLimiter.WaitAsync();
            haveLock = true;

            _responseBuffer.Clear();
            _pendingResponse = new TaskCompletionSource<byte[]>();

            await Writer.Ready;
            await Writer.Write(command);

            using var cts = new CancellationTokenSource(timeoutMs);
            cts.Token.Register(() => _pendingResponse?.TrySetCanceled());

            return await _pendingResponse.Task;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            JS.Log($"SendCommand error: {ex.Message}");
            return null;
        }
        finally
        {
            _pendingResponse = null;
            if (haveLock)
            {
                _commandLimiter?.Release();
            }
        }
    }

    /// <summary>
    /// Sends a string command and returns string response
    /// </summary>
    public override async Task<string?> SendStringCommandAsync(string command, int timeoutMs = CommandTimeoutMs)
    {
        var response = await SendCommandAsync(Encoding.ASCII.GetBytes(command), timeoutMs);
        if (response == null) return null;
        return Encoding.ASCII.GetString(response).TrimEnd('#');
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

                        _responseBuffer.AddRange(bytes);

                        // Check for terminator
                        int terminatorIndex = _responseBuffer.IndexOf((byte)'#');
                        if (terminatorIndex >= 0 && _pendingResponse != null)
                        {
                            var response = _responseBuffer.Take(terminatorIndex + 1).ToArray();
                            _responseBuffer.RemoveRange(0, terminatorIndex + 1);
                            _pendingResponse.TrySetResult(response);
                        }
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
