using System.Diagnostics;
using System.Text;

namespace SpawnDev.NexStar.Simulation;

/// <summary>
/// A hand control with a body: it moves. Gotos run at a speed in the direction its policy picks (the short way, or deliberately NOT),
/// fixed slews run at a rate table, tracking drifts the azimuth. Time runs <see cref="Scale"/> times faster than the clock so a sweep
/// across the sky takes seconds. Speaks the real protocol over the real <see cref="NexStarCommandChannel"/>.
/// It records the TRUE unwrapped azimuth so a test can tell whether the cable would have wrapped, which the service can only estimate.
/// This is a stand-in used to test the guard's reactions; the direction a real 127SLT takes is something only the real mount can tell
/// (see the read-only hardware tests and the plan).
/// </summary>
public sealed class SimulatedMount : INexStarTransport
{
    public enum GotoPolicy
    {
        /// <summary>Takes the short way round.</summary>
        Shortest,
        /// <summary>Always turns azimuth upward, whatever is shorter: what the guard must notice and stop.</summary>
        AlwaysIncreasing,
    }

    private readonly NexStarCommandChannel _channel = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastAdvance;
    private readonly object _lock = new();

    /// <summary>Simulated seconds per real second.</summary>
    public double Scale { get; }

    public GotoPolicy Policy { get; set; } = GotoPolicy.Shortest;

    /// <summary>Goto speed in simulated degrees per second.</summary>
    public double GotoSpeed { get; set; } = 3.0;

    /// <summary>Fixed slew rate 0..9 in simulated degrees per second.</summary>
    public double[] FixedRates { get; } = NexStarProtocol.MeasuredFixedRateDegreesPerSecond.ToArray();

    /// <summary>Azimuth drift while tracking, simulated degrees per second (0.004 is the sidereal worst case).</summary>
    public double TrackingDrift { get; set; }

    /// <summary>The true azimuth including every turn: the cable's real position when it started at 0.</summary>
    public double TrueAzimuth { get; private set; }

    public double Altitude { get; private set; }

    /// <summary>The furthest the true azimuth has been from where it started.</summary>
    public double MaxExcursion { get; private set; }

    /// <summary>What a sync has added to every azimuth reading: the hand control's zero moves, the motors do not.</summary>
    public double ReportedAzimuthOffset { get; private set; }

    public bool GotoActive { get; private set; }
    public byte Tracking { get; private set; }
    public List<int> SlewRatesSent { get; } = new();
    public List<string> Commands { get; } = new();

    private double? _gotoTargetAz;
    private double _gotoTargetAlt;
    private double _gotoDirection;
    private double _slewAz;   // simulated deg/s
    private double _slewAlt;

    public bool Connected { get; private set; } = true;
    public event Action<INexStarTransport> OnDisconnect = default!;

    public SimulatedMount(double scale, double startAzimuth, double startAltitude = 30)
    {
        Scale = scale;
        TrueAzimuth = startAzimuth;
        Altitude = startAltitude;
    }

    private void Advance()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = (now - _lastAdvance) * Scale;
        _lastAdvance = now;
        if (dt <= 0) return;
        if (GotoActive && _gotoTargetAz is double target)
        {
            var step = GotoSpeed * dt;
            var remainingAz = TargetRemaining(target);
            var moveAz = Math.Min(step, Math.Abs(remainingAz));
            TrueAzimuth += Math.Sign(remainingAz) * moveAz;
            var remainingAlt = _gotoTargetAlt - Altitude;
            var moveAlt = Math.Min(step, Math.Abs(remainingAlt));
            Altitude += Math.Sign(remainingAlt) * moveAlt;
            if (Math.Abs(TargetRemaining(target)) < 1e-9 && Math.Abs(_gotoTargetAlt - Altitude) < 1e-9) GotoActive = false;
        }
        TrueAzimuth += _slewAz * dt;
        Altitude = Math.Clamp(Altitude + _slewAlt * dt, -90, 90);
        if (Tracking == 1) TrueAzimuth += TrackingDrift * dt;
        MaxExcursion = Math.Max(MaxExcursion, Math.Abs(TrueAzimuth));
    }

    /// <summary>The signed travel still to do to the goto target in the direction chosen when it started.</summary>
    private double TargetRemaining(double targetAz)
    {
        var mod = ((TrueAzimuth % 360) + 360) % 360;
        var forward = ((targetAz - mod) % 360 + 360) % 360; // 0..360 upward
        if (forward < 1e-9 || forward > 360 - 1e-9) return 0; // there
        return _gotoDirection > 0 ? forward : forward - 360;
    }

    private byte[]? Respond(byte[] cmd)
    {
        Advance();
        Commands.Add(((char)cmd[0]).ToString());
        static byte[] Hash(params byte[] payload) => payload.Append((byte)'#').ToArray();
        static byte[] Text(string s) => Hash(Encoding.ASCII.GetBytes(s));
        var az = (((TrueAzimuth + ReportedAzimuthOffset) % 360) + 360) % 360;
        switch ((char)cmd[0])
        {
            case 'V': return Hash(5, 35);
            case 'm': return Hash(7);
            case 'J': return Hash(0);
            case 't': return Hash(Tracking);
            case 'T': Tracking = cmd[1]; return Hash();
            case 'S':
            case 's': ReportedAzimuthOffset += 4; return Hash(); // a sync: the readings shift by 4 degrees, nothing moves
            case 'K': return Hash(cmd[1]);
            case 'L': return Text(GotoActive ? "1" : "0");
            case 'M': GotoActive = false; _gotoTargetAz = null; return Hash();
            case 'Z': return Text($"{NexStarProtocol.DecimalDegreesToNex(az)},{NexStarProtocol.DecimalDegreesToNex(Altitude)}");
            case 'z': return Text($"{NexStarProtocol.DecimalDegreesToPreciseNex(az)},{NexStarProtocol.DecimalDegreesToPreciseNex(Altitude)}");
            case 'E': return Text("8000,0000");
            case 'e': return Text("80000000,00000000");
            case 'w': return Hash(40, 44, 55, 0, 73, 59, 9, 1);
            case 'h': return Hash(11, 4, 35, 9, 24, 26, 0xFC, 1);
            case 'B':
            case 'b':
            {
                var parts = Encoding.ASCII.GetString(cmd, 1, cmd.Length - 1).Split(',');
                double a, b;
                if (cmd[0] == 'b') { NexStarProtocol.TryPreciseNexToDecimalDegrees(parts[0], out a); NexStarProtocol.TryPreciseNexToDecimalDegrees(parts[1], out b); }
                else { NexStarProtocol.TryNexToDecimalDegrees(parts[0], out a); NexStarProtocol.TryNexToDecimalDegrees(parts[1], out b); }
                if (b > 180) b -= 360;
                _gotoTargetAz = ((a - ReportedAzimuthOffset) % 360 + 360) % 360;
                a = _gotoTargetAz.Value;
                _gotoTargetAlt = b;
                var mod = ((TrueAzimuth % 360) + 360) % 360;
                var shortest = ((a - mod) % 360 + 540) % 360 - 180;
                _gotoDirection = Policy == GotoPolicy.Shortest ? (shortest >= 0 ? 1 : -1) : 1;
                GotoActive = true;
                return Hash();
            }
            case 'P':
            {
                // P len dest id rate ...: fixed slews 36/37 on azimuth (0x10) or altitude (0x11)
                if (cmd[3] is 36 or 37)
                {
                    var rate = cmd[4];
                    SlewRatesSent.Add(rate);
                    var speed = FixedRates[rate] * (cmd[3] == 36 ? 1 : -1);
                    if (cmd[2] == 0x10) _slewAz = speed; else _slewAlt = speed;
                }
                return Hash();
            }
            default: return Hash();
        }
    }

    public async Task<byte[]?> SendCommandAsync(byte[] command, int timeoutMs = NexStarCommandChannel.DefaultTimeoutMs)
    {
        if (!Connected) return null;
        return await _channel.ExchangeAsync(command, bytes =>
        {
            byte[]? response;
            lock (_lock) response = Respond(bytes);
            if (response != null)
            {
                _ = Task.Run(async () =>
                {
                    for (int i = 0; i < response.Length; i += 3)
                    {
                        await Task.Delay(1);
                        _channel.OnBytes(response.AsSpan(i, Math.Min(3, response.Length - i)));
                    }
                });
            }
            return Task.CompletedTask;
        }, Math.Min(timeoutMs, 400));
    }

    public Task CloseAsync() { Connected = false; return Task.CompletedTask; }
    public Task Forget() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The cable is pulled out.</summary>
    public void Unplug()
    {
        Connected = false;
        OnDisconnect?.Invoke(this);
    }

    /// <summary>The true unwrapped azimuth right now (advances the clock).</summary>
    public double TrueNow()
    {
        lock (_lock) { Advance(); return TrueAzimuth; }
    }
}
