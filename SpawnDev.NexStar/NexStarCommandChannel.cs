namespace SpawnDev.NexStar;

/// <summary>
/// One command at a time over any byte transport: writes a command, then waits for exactly its response.
/// Shared by every transport (WebSerial, WebUSB, desktop serial) so framing, timeouts and resynchronisation
/// behave identically and are tested once.
/// </summary>
public sealed class NexStarCommandChannel
{
    /// <summary>
    /// The hand control retries internally and the protocol document says drivers must be prepared to wait
    /// up to 3.5 s for a response, so the default is above that.
    /// </summary>
    public const int DefaultTimeoutMs = 4000;

    /// <summary>
    /// After a command timed out the response may still arrive. Wait this long for the line to go quiet
    /// before the next command, or the late bytes would be taken as that command's response.
    /// </summary>
    public const int ResyncQuietMs = 250;

    private readonly SemaphoreSlim _limiter = new(1, 1);
    private readonly NexStarResponseAssembler _assembler = new();
    private readonly object _lock = new();
    private TaskCompletionSource<byte[]?>? _pending;
    private bool _needsResync;

    /// <summary>Commands that got no usable response (timeout, device error or malformed).</summary>
    public int FailedCommands { get; private set; }

    /// <summary>Bytes received while no command was waiting (late or unsolicited).</summary>
    public int UnsolicitedBytes { get; private set; }

    /// <summary>
    /// Sends a command and returns its payload (the response without the '#'; empty for a plain acknowledgement),
    /// or null on timeout, a device error or a malformed response.
    /// </summary>
    /// <param name="command">Command bytes.</param>
    /// <param name="write">Writes the bytes to the transport.</param>
    /// <param name="timeoutMs">How long to wait for the response.</param>
    public async Task<byte[]?> ExchangeAsync(byte[] command, Func<byte[], Task> write, int timeoutMs = DefaultTimeoutMs)
    {
        await _limiter.WaitAsync();
        try
        {
            if (_needsResync)
            {
                await Task.Delay(ResyncQuietMs);
                _needsResync = false;
            }
            var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                _assembler.Begin(NexStarProtocol.ExpectedResponseLength(command));
                _pending = tcs;
            }
            await write(command);
            using var cts = new CancellationTokenSource(timeoutMs);
            using var registration = cts.Token.Register(() => tcs.TrySetResult(null));
            var payload = await tcs.Task;
            if (payload == null)
            {
                FailedCommands++;
                // A timeout leaves the response possibly still in flight; a device error or malformed frame
                // was already consumed (malformed sets the flag itself in OnBytes).
                if (cts.IsCancellationRequested) _needsResync = true;
            }
            return payload;
        }
        finally
        {
            lock (_lock) { _pending = null; }
            _limiter.Release();
        }
    }

    /// <summary>Feed every byte the transport receives here.</summary>
    public void OnBytes(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            if (_pending == null)
            {
                UnsolicitedBytes += data.Length;
                return;
            }
            var status = _assembler.Append(data, out var payload);
            switch (status)
            {
                case NexStarFrameStatus.Complete:
                    _pending.TrySetResult(payload);
                    break;
                case NexStarFrameStatus.DeviceError:
                case NexStarFrameStatus.Malformed:
                    _needsResync = status == NexStarFrameStatus.Malformed;
                    _pending.TrySetResult(null);
                    break;
            }
        }
    }
}
