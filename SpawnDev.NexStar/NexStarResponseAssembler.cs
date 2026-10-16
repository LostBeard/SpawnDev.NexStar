namespace SpawnDev.NexStar;

/// <summary>Outcome of feeding bytes to a <see cref="NexStarResponseAssembler"/>.</summary>
public enum NexStarFrameStatus
{
    /// <summary>More bytes are needed.</summary>
    Incomplete,
    /// <summary>A whole response arrived; the payload (without the '#') is available.</summary>
    Complete,
    /// <summary>
    /// A pass-through (P) command got no answer from the device: the hand control returns one extra
    /// byte before the '#', and any returned data is garbage (Celestron protocol, "Pass-through Commands
    /// with No Response"). The extra byte and the '#' are consumed.
    /// </summary>
    DeviceError,
    /// <summary>The bytes do not follow the protocol (no '#' where one belongs).</summary>
    Malformed,
}

/// <summary>
/// Cuts the hand control's byte stream into one response per command.
/// <para>
/// A response is NOT "everything up to the first '#'". Binary payloads (version, time, location) can contain
/// 0x23, which is '#': a mount at version 5.35, or a clock at second 35, has one in the middle of the
/// payload. Every command has a fixed payload length (<see cref="NexStarProtocol.ExpectedResponseLength"/>),
/// so the response ends at that length plus one terminator byte.
/// </para>
/// Pure and transport-free so it is unit-tested directly and shared by WebSerial, WebUSB and desktop transports.
/// </summary>
public sealed class NexStarResponseAssembler
{
    private readonly List<byte> _buffer = new();
    private int _expectedPayload = -1;

    /// <summary>Bytes thrown away because they arrived after a response was complete.</summary>
    public int DiscardedBytes { get; private set; }

    /// <summary>
    /// Starts a new response. Drops anything buffered.
    /// </summary>
    /// <param name="expectedPayloadLength">Payload bytes before the '#', or -1 if the command's length is not known (the response then ends at the first '#').</param>
    public void Begin(int expectedPayloadLength)
    {
        _buffer.Clear();
        _expectedPayload = expectedPayloadLength;
    }

    /// <summary>Adds received bytes and reports whether a response is now complete.</summary>
    public NexStarFrameStatus Append(ReadOnlySpan<byte> data, out byte[]? payload)
    {
        payload = null;
        foreach (var b in data) _buffer.Add(b);

        if (_expectedPayload < 0)
        {
            var hash = _buffer.IndexOf((byte)NexStarProtocol.Terminator);
            if (hash < 0) return NexStarFrameStatus.Incomplete;
            payload = _buffer.GetRange(0, hash).ToArray();
            Finish(hash + 1);
            return NexStarFrameStatus.Complete;
        }

        var n = _expectedPayload;
        if (_buffer.Count < n + 1) return NexStarFrameStatus.Incomplete;
        if (_buffer[n] == (byte)NexStarProtocol.Terminator)
        {
            payload = _buffer.GetRange(0, n).ToArray();
            Finish(n + 1);
            return NexStarFrameStatus.Complete;
        }

        // Not a '#' where one belongs: the "no answer" form is one extra byte, then the '#'.
        if (_buffer.Count < n + 2) return NexStarFrameStatus.Incomplete;
        var status = _buffer[n + 1] == (byte)NexStarProtocol.Terminator
            ? NexStarFrameStatus.DeviceError
            : NexStarFrameStatus.Malformed;
        Finish(n + 2);
        return status;
    }

    private void Finish(int consumed)
    {
        DiscardedBytes += _buffer.Count - consumed;
        _buffer.Clear();
    }
}
