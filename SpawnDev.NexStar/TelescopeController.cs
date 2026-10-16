using SpawnDev.NexStar;

namespace SpawnDev.NexStar
{
    /// <summary>The four directions of the slew pad. Right turns the azimuth up (clockwise seen from above), Up raises the tube.</summary>
    public enum PadDirection { Left, Right, Up, Down }

    /// <summary>
    /// Keeps the Sun out of the eyepiece. Pointing a telescope at the Sun, even briefly, blinds and burns, and a goto does not know where the Sun is.
    /// </summary>
    public static class SunGuard
    {
        /// <summary>How close to the Sun a target may be, degrees.</summary>
        public const double MinimumSeparationDegrees = 12.0;

        /// <summary>The Sun counts as up from a little before it rises until after it has set.</summary>
        public const double SunAltitudeLimit = -4.0;

        /// <summary>A reason the target must not be used, or null if it is clear of the Sun (or the Sun is well down).</summary>
        public static string? Refuse(double targetAzimuth, double targetAltitude, double latitude, double longitude, DateTime utc)
        {
            var sun = SolarSystemMath.GetAzAlt(SolarSystemObject.Sun, latitude, longitude, utc);
            if (sun.Altitude < SunAltitudeLimit) return null;
            var separation = AstronomyMath.AngularSeparation(targetAzimuth, targetAltitude, sun.Azimuth, sun.Altitude);
            return separation < MinimumSeparationDegrees
                ? $"that is {separation:F1} degrees from the Sun: never point the telescope near the Sun"
                : null;
        }
    }

    /// <summary>
    /// What the workspace does with the mount: the one poll loop, gotos in the sky's true frame (through the pointing model when the mount is not
    /// aligned, refusing the Sun), the press-and-hold slew pad with a dead-man watchdog, and aligning the unaligned mount on a centred object.
    /// The cable guard does the safe routing underneath; this adds the sky.
    /// </summary>
    public sealed class TelescopeController
    {
        private readonly NexStarService _mount;
        private readonly object _gate = new();
        private PadDirection? _pad;
        private int _padSequence;
        private readonly SemaphoreSlim _padLock = new(1, 1);
        private DateTime _heartbeat = DateTime.MinValue;
        private DateTime _lastPoll = DateTime.MinValue;
        private CancellationTokenSource? _gotoCts;

        public TelescopeController(NexStarService mount)
        {
            _mount = mount;
        }

        /// <summary>The offsets that stand in for an alignment when the hand control has none.</summary>
        public PointingModel Pointing { get; private set; } = new PointingModel();

        /// <summary>Replaces the pointing model (when one is restored from storage).</summary>
        public void SetPointing(PointingModel model) => Pointing = model;

        /// <summary>How long a held pad button may go without a heartbeat before the slew is stopped.</summary>
        public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromMilliseconds(1500);

        /// <summary>How often the idle poll reads the position.</summary>
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(800);

        /// <summary>The pad direction held right now, if any.</summary>
        public PadDirection? PadHeld => _pad;

        /// <summary>The last thing the controller refused or failed at, for the UI to show.</summary>
        public string? Message { get; private set; }

        /// <summary>Lets the host put its own notice where the controller's messages show.</summary>
        public void SetMessage(string? message) => Message = message;

        /// <summary>True when the mount has an alignment of its own (so its azimuth and altitude are true), or one was set up here.</summary>
        public bool HasFrame => _mount.IsAligned || Pointing.IsSet;

        /// <summary>The mount's azimuth and altitude as the true sky sees them, or null when not connected / not known.</summary>
        public (double Azimuth, double Altitude)? TruePointing()
        {
            var p = _mount.CurrentAzAlt;
            if (p == null || !_mount.ComsEnabled) return null;
            return _mount.IsAligned ? (p.Azimuth, p.Altitude) : Pointing.FromMount(p.Azimuth, p.Altitude);
        }

        /// <summary>
        /// Goes to a place in the sky (true azimuth and altitude) by a route that cannot wrap the cable. Refused when the target is near the Sun or
        /// below the horizon, or when the mount has no alignment and none has been set up here (the target would be in the wrong frame).
        /// </summary>
        public async Task<GuardedMoveResult> GotoAsync(double azimuth, double altitude, double latitude, double longitude, DateTime utc)
        {
            string? Refused(string reason)
            {
                Message = reason;
                return reason;
            }
            if (!_mount.ComsEnabled) return new GuardedMoveResult(false, Refused("not connected"), null);
            if (_pad != null) return new GuardedMoveResult(false, Refused("a slew button is held"), null);
            if (!double.IsFinite(azimuth) || !double.IsFinite(altitude)) return new GuardedMoveResult(false, Refused("the target is not a position"), null);
            if (altitude < 0) return new GuardedMoveResult(false, Refused("the target is below the horizon"), null);
            var sun = SunGuard.Refuse(azimuth, altitude, latitude, longitude, utc);
            if (sun != null) return new GuardedMoveResult(false, Refused(sun), null);
            if (!HasFrame) return new GuardedMoveResult(false, Refused("the telescope is not aligned: centre an object in the eyepiece and use Align here first"), null);

            var (mountAz, mountAlt) = _mount.IsAligned ? (azimuth, altitude) : Pointing.ToMount(azimuth, altitude);
            var cts = new CancellationTokenSource();
            lock (_gate) { _gotoCts?.Cancel(); _gotoCts = cts; }
            try
            {
                var result = await _mount.GotoAzAltGuardedAsync(mountAz, mountAlt, cts.Token);
                Message = result.Success ? null : result.Message;
                return result;
            }
            finally
            {
                lock (_gate) { if (ReferenceEquals(_gotoCts, cts)) _gotoCts = null; }
                cts.Dispose();
            }
        }

        /// <summary>Stops everything: a goto in progress, a held slew.</summary>
        public async Task StopAsync()
        {
            CancellationTokenSource? cts;
            lock (_gate) cts = _gotoCts;
            try { cts?.Cancel(); } catch (ObjectDisposedException) { }
            await EndPadAsync();
            await _mount.CancelGotoAsync();
            await _mount.StopAllSlewAsync();
        }

        /// <summary>
        /// Aligns an unaligned mount on the object that is centred in the eyepiece right now: its true position against where the mount says it points.
        /// </summary>
        public async Task<bool> AlignHereAsync(double trueAzimuth, double trueAltitude)
        {
            if (!_mount.ComsEnabled) { Message = "not connected"; return false; }
            if (_mount.IsAligned) { Message = "the hand control is aligned: its readings are already true"; return false; }
            var p = await _mount.GetAzAltAsync(precise: true);
            if (p == null) { Message = "could not read the mount's position"; return false; }
            Pointing.AlignOn(trueAzimuth, trueAltitude, p.Azimuth, p.Altitude);
            Message = null;
            return true;
        }

        /// <summary>
        /// Starts turning while a pad button is held. Returns why not, or null when it started. The caller must keep calling
        /// <see cref="Heartbeat"/> (every frame is fine) until it calls <see cref="EndPadAsync"/>; if the heartbeat stops (the tab went to the background,
        /// the page froze) <see cref="RunAsync"/> stops the slew, because a slew never ends by itself.
        /// </summary>
        public async Task<string?> BeginPadAsync(PadDirection direction, SlewRate rate)
        {
            if (!_mount.ComsEnabled) return Message = "not connected";
            int id;
            lock (_gate)
            {
                if (_pad != null) return "a pad button is already held";
                _pad = direction;
                id = ++_padSequence;
                _heartbeat = DateTime.UtcNow;
            }
            // starting and stopping take turns: a release that arrives while the slew is still being started waits for it, and then stops it
            await _padLock.WaitAsync();
            try
            {
                lock (_gate)
                {
                    if (_pad == null || _padSequence != id) return null;   // released before it ever started
                }
                string? refusal = null;
                switch (direction)
                {
                    case PadDirection.Left:
                    case PadDirection.Right:
                    {
                        var start = await _mount.BeginAzimuthSlewAsync(direction == PadDirection.Right ? SlewDirection.Positive : SlewDirection.Negative, rate);
                        if (!start.Started) refusal = start.Reason ?? "the slew did not start";
                        break;
                    }
                    default:
                        if (!await _mount.SlewFixedAsync(SlewAxis.DecAlt, direction == PadDirection.Up ? SlewDirection.Positive : SlewDirection.Negative, rate))
                            refusal = "the mount did not accept the slew";
                        break;
                }
                if (refusal != null)
                {
                    lock (_gate) { if (_padSequence == id) _pad = null; }
                    Message = refusal;
                }
                else
                {
                    Message = null;
                }
                return refusal;
            }
            finally
            {
                _padLock.Release();
            }
        }

        /// <summary>The held button is still held.</summary>
        public void Heartbeat() => _heartbeat = DateTime.UtcNow;

        /// <summary>The held button was released (or must be treated as released).</summary>
        public async Task EndPadAsync()
        {
            PadDirection? held;
            lock (_gate) { held = _pad; _pad = null; }
            if (held == null) return;
            await _padLock.WaitAsync();
            try
            {
                if (held is PadDirection.Left or PadDirection.Right) await _mount.StopGuardedSlewAsync();
                else await _mount.StopSlewAsync(SlewAxis.DecAlt);
            }
            finally
            {
                _padLock.Release();
            }
        }

        /// <summary>
        /// The controller's loop: reads the position while nothing else is moving the mount, and stops a held slew whose heartbeat has stopped.
        /// Run it for the life of the page.
        /// </summary>
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(100, cancellationToken);
                    if (_pad != null && DateTime.UtcNow - _heartbeat > HeartbeatTimeout)
                    {
                        Message = "the slew was stopped: the page stopped answering";
                        await EndPadAsync();
                    }
                    if (_mount.ComsEnabled && !_mount.GuardedMotionActive && _pad == null && DateTime.UtcNow - _lastPoll >= PollInterval)
                    {
                        _lastPoll = DateTime.UtcNow;
                        await _mount.PollAsync();
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Message = ex.Message;
                }
            }
        }
    }
}
