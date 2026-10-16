using System.IO.Ports;

namespace SpawnDev.NexStar.Desktop;

/// <summary>
/// A NexStar hand control on a desktop serial port (Windows COM port, Linux /dev/tty*), for tools and for the
/// tests that talk to a real mount. Browsers use WebSerial or WebUSB instead; the framing is the same
/// <see cref="NexStarCommandChannel"/>.
/// </summary>
public sealed class SystemSerialTransport : INexStarTransport
{
    private readonly SerialPort _port;
    private readonly NexStarCommandChannel _channel = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _readLoop;
    private bool _closed;

    private SystemSerialTransport(SerialPort port) => _port = port;

    /// <inheritdoc/>
    public bool Connected => !_closed && _port.IsOpen;

    /// <inheritdoc/>
    public event Action<INexStarTransport> OnDisconnect = default!;

    /// <summary>The port name, for example COM7.</summary>
    public string PortName => _port.PortName;

    /// <summary>Bytes that arrived while no command was waiting: late responses or noise.</summary>
    public int UnsolicitedBytes => _channel.UnsolicitedBytes;

    /// <summary>Commands that got no usable response.</summary>
    public int FailedCommands => _channel.FailedCommands;

    /// <summary>
    /// Opens a port at the hand control's settings (9600 baud, 8N1). Returns null if the port cannot be opened
    /// (missing, or another program such as a browser has it).
    /// </summary>
    public static SystemSerialTransport? Open(string portName)
    {
        var port = new SerialPort(portName, 9600, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 2000,
        };
        try
        {
            port.Open();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            port.Dispose();
            return null;
        }
        var transport = new SystemSerialTransport(port);
        transport._readLoop = Task.Run(transport.ReadLoopAsync);
        return transport;
    }

    /// <summary>
    /// Finds the hand control: the port named by the NEXSTAR_PORT environment variable if set, otherwise the first
    /// port that answers the echo command ('K' 'A' gives 'A'). Only the harmless echo is sent to a port that is not known to be a mount.
    /// </summary>
    public static async Task<SystemSerialTransport?> FindMountAsync()
    {
        var named = Environment.GetEnvironmentVariable("NEXSTAR_PORT");
        var candidates = string.IsNullOrWhiteSpace(named) ? SerialPort.GetPortNames() : new[] { named };
        foreach (var name in candidates)
        {
            var transport = Open(name);
            if (transport == null) continue;
            var echo = await transport.SendCommandAsync(new byte[] { (byte)'K', (byte)'A' }, 1500);
            if (echo != null && echo.Length == 1 && echo[0] == (byte)'A') return transport;
            await transport.DisposeAsync();
        }
        return null;
    }

    /// <inheritdoc/>
    public async Task<byte[]?> SendCommandAsync(byte[] command, int timeoutMs = NexStarCommandChannel.DefaultTimeoutMs)
    {
        if (!Connected) return null;
        try
        {
            return await _channel.ExchangeAsync(command, async bytes =>
            {
                await _port.BaseStream.WriteAsync(bytes, _cts.Token);
                await _port.BaseStream.FlushAsync(_cts.Token);
            }, timeoutMs);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or UnauthorizedAccessException)
        {
            Disconnected();
            return null;
        }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[256];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var n = await _port.BaseStream.ReadAsync(buffer, _cts.Token);
                if (n == 0) break;
                _channel.OnBytes(buffer.AsSpan(0, n));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or UnauthorizedAccessException)
        {
            // closed or unplugged
        }
        Disconnected();
    }

    private void Disconnected()
    {
        if (_closed) return;
        _closed = true;
        OnDisconnect?.Invoke(this);
    }

    /// <inheritdoc/>
    public async Task CloseAsync()
    {
        _closed = true;
        _cts.Cancel();
        try { _port.Close(); } catch (IOException) { }
        if (_readLoop != null)
        {
            try { await _readLoop; } catch { }
        }
    }

    /// <inheritdoc/>
    public Task Forget() => Task.CompletedTask;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _port.Dispose();
        _cts.Dispose();
    }
}
