namespace SpawnDev.NexStar.Tests;

/// <summary>
/// A protocol simulator standing in for a hand control, for the service-level tests that need a mount to fail
/// in ways the real one will not on demand (silence, garbage, unplugging). It answers with the real framing,
/// delivered in small pieces over the real <see cref="NexStarCommandChannel"/>, so everything above the wire
/// (assembler, channel, service, parsing) is production code. It is NOT a substitute for the hardware tests,
/// which run the same service against the real mount.
/// Defaults reproduce what the real 127SLT sends: version 5.35 (minor is 0x23 = '#'), model 7, unaligned.
/// </summary>
internal sealed class SimulatedHandControl : INexStarTransport
{
    private readonly NexStarCommandChannel _channel = new();

    public bool Connected { get; private set; } = true;
    public event Action<INexStarTransport> OnDisconnect = default!;

    /// <summary>Every command the service sent, in order.</summary>
    public List<byte[]> Received { get; } = new();

    /// <summary>The longest wait the simulator allows a command, so "nothing answers" tests finish quickly.</summary>
    public int MaxTimeoutMs { get; set; } = 250;

    // Mutable mount state
    public byte[] Version = { 5, 35 };
    public byte Model = 7;
    public byte Aligned = 0;
    public byte Tracking = 0;
    public string AzAlt = "571E,0000";
    public string RaDec = "8000,0000";
    public byte[] Location = { 40, 44, 55, 0, 73, 59, 9, 1 };
    public byte[] Time = { 11, 4, 35, 9, 24, 26, 0xFC, 1 };

    /// <summary>If set, answers every command itself; return null to stay silent.</summary>
    public Func<byte[], byte[]?>? Override;

    /// <summary>Bytes to answer a command with, '#' included, or null for silence.</summary>
    private byte[]? Respond(byte[] command)
    {
        if (Override != null) return Override(command);
        static byte[] WithHash(params byte[] payload) => payload.Append((byte)'#').ToArray();
        static byte[] Text(string s) => WithHash(System.Text.Encoding.ASCII.GetBytes(s));
        switch ((char)command[0])
        {
            case 'V': return WithHash(Version);
            case 'm': return WithHash(Model);
            case 'J': return WithHash(Aligned);
            case 't': return WithHash(Tracking);
            case 'K': return WithHash(command[1]);
            case 'L': return Text("0");
            case 'Z': return Text(AzAlt);
            case 'E': return Text(RaDec);
            case 'w': return WithHash(Location);
            case 'h': return WithHash(Time);
            case 'T': Tracking = command[1]; return WithHash();
            default: return WithHash(); // goto, sync, slew, set time/location, cancel: acknowledged
        }
    }

    public async Task<byte[]?> SendCommandAsync(byte[] command, int timeoutMs = NexStarCommandChannel.DefaultTimeoutMs)
    {
        if (!Connected) return null;
        Received.Add(command);
        return await _channel.ExchangeAsync(command, bytes =>
        {
            var response = Respond(bytes);
            if (response != null)
            {
                _ = Task.Run(async () =>
                {
                    // like a serial port: a byte or two at a time
                    for (int i = 0; i < response.Length; i += 2)
                    {
                        await Task.Delay(1);
                        _channel.OnBytes(response.AsSpan(i, Math.Min(2, response.Length - i)));
                    }
                });
            }
            return Task.CompletedTask;
        }, Math.Min(timeoutMs, MaxTimeoutMs));
    }

    public Task CloseAsync()
    {
        Connected = false;
        return Task.CompletedTask;
    }

    /// <summary>The cable is pulled.</summary>
    public void Unplug()
    {
        Connected = false;
        OnDisconnect?.Invoke(this);
    }

    public Task Forget() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
