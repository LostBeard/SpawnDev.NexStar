using System.Diagnostics;

namespace SpawnDev.NexStar
{
    /// <summary>What kind of cable event the service is reporting.</summary>
    public enum CableAlertKind
    {
        /// <summary>The cable moved into a different <see cref="CableZone"/>.</summary>
        ZoneChanged,
        /// <summary>Tracking will reach a cable limit in the time stated.</summary>
        LimitApproaching,
        /// <summary>Tracking was switched off because the cable reached (or was about to reach) a limit.</summary>
        TrackingStopped,
        /// <summary>A slew was stopped at the cable limit.</summary>
        LimitReached,
        /// <summary>A move was stopped because the mount was not going the way it was planned to.</summary>
        RouteMismatch,
        /// <summary>Contact with the mount was lost during a move; the cable count is now uncertain.</summary>
        ContactLost,
    }

    /// <summary>A cable event for the user: raise a toast, sound an alarm, show the dial in red.</summary>
    public readonly record struct CableAlert(CableAlertKind Kind, string Message, CableZone Zone);

    /// <summary>The outcome of a guarded move.</summary>
    /// <param name="Success">True if the mount ended where it was sent</param>
    /// <param name="Message">What happened, or why not</param>
    /// <param name="Plan">The route that was planned, if one was</param>
    public sealed record GuardedMoveResult(bool Success, string? Message, GotoPlan? Plan);

    /// <summary>The outcome of starting a guarded slew.</summary>
    /// <param name="Started">True if the mount is slewing</param>
    /// <param name="Rate">The rate it was started at (lower than asked near a cable limit)</param>
    /// <param name="Reason">Why it was slowed or refused</param>
    public readonly record struct GuardedSlewStart(bool Started, SlewRate Rate, string? Reason);

    /// <summary>
    /// Motion that obeys the <see cref="Cable"/> guard. Everything that moves the azimuth axis and could be sent while nobody is looking at
    /// the telescope goes through here: a goto is planned so it cannot wrap the cord and is executed one short-way step at a time, each step
    /// checked against the plan, and a slew is slowed and stopped at the limit.
    /// </summary>
    public partial class NexStarService
    {
        private bool _motionActive;
        private CancellationTokenSource? _slewMonitorCts;
        private Task? _slewMonitor;
        private CableZone _lastZone = CableZone.Safe;
        private int _lastWarnBucket = int.MaxValue;
        private bool _azimuthPositiveIsIncreasing = true;

        /// <summary>Fired for anything the user should know about the cable. Raised on the thread that was polling.</summary>
        public event Action<CableAlert>? OnCableAlert;

        /// <summary>How often a goto in progress is checked.</summary>
        public TimeSpan GotoPollInterval { get; set; } = TimeSpan.FromMilliseconds(300);

        /// <summary>How long one goto step may take before it is abandoned. A slow slew across the sky takes minutes, not hours.</summary>
        public TimeSpan GotoTimeout { get; set; } = TimeSpan.FromSeconds(180);

        /// <summary>How close to a step's azimuth the mount must be to count as arrived, degrees.</summary>
        public double ArrivalToleranceDegrees { get; set; } = 1.0;

        /// <summary>How far the cable count may stray from a step's planned course before the move is called off, degrees.</summary>
        public double RouteToleranceDegrees { get; set; } = 3.0;

        /// <summary>True while a guarded move is under way.</summary>
        public bool GuardedMotionActive => _motionActive;

        private void Alert(CableAlertKind kind, string message) => OnCableAlert?.Invoke(new CableAlert(kind, message, Cable.Zone));

        /// <summary>
        /// Reads the position, feeds the cable count and runs the cable checks: zone changes, the tracking watchdog (time left before tracking
        /// drives the cable to a limit, and switching tracking off if it would). Call it about once a second from the app's single poll loop.
        /// </summary>
        public async Task<AzAltCoordinates?> PollAsync()
        {
            var p = await GetAzAltAsync(precise: true);
            if (p != null) await EvaluateCableAsync();
            return p;
        }

        private async Task EvaluateCableAsync()
        {
            var zone = Cable.Zone;
            if (zone != _lastZone)
            {
                _lastZone = zone;
                Alert(CableAlertKind.ZoneChanged, zone switch
                {
                    CableZone.Limit => $"The cable is at its limit ({Cable.Cumulative:F0} degrees from neutral)",
                    CableZone.Warn => $"The cable is {Math.Min(Cable.RoomPositive, Cable.RoomNegative):F0} degrees from its limit",
                    _ => "The cable is clear of its limits",
                });
            }
            if (!Cable.Options.Enabled || CurrentTrackingMode == TrackingMode.Off || _motionActive) return;

            // While tracking the azimuth creeps (up to 15 degrees an hour). The slow estimate needs two minutes of readings.
            var rate = Cable.SlowRateDegreesPerSecond;
            var seconds = rate is double r ? Cable.SecondsToLimit(r) : double.PositiveInfinity;
            if (zone == CableZone.Limit || seconds < 20)
            {
                if (Cable.Options.StopTrackingAtLimit)
                {
                    var stopped = await SetTrackingModeAsync(TrackingMode.Off);
                    Alert(CableAlertKind.TrackingStopped, stopped
                        ? "Tracking was switched off: the cable is at its limit. Unwind it to carry on."
                        : "The cable is at its limit and tracking could NOT be switched off.");
                }
                return;
            }
            var bucket = seconds < 120 ? 2 : seconds < 600 ? 10 : seconds < 1800 ? 30 : int.MaxValue;
            if (bucket < _lastWarnBucket)
                Alert(CableAlertKind.LimitApproaching, $"Tracking will reach the cable limit in about {Math.Ceiling(seconds / 60.0):F0} minutes");
            _lastWarnBucket = bucket;
        }

        /// <summary>
        /// Goes to an azimuth and altitude by a route that cannot wrap the cable (see <see cref="CableGuard.PlanGoto"/>), one short-way step at a time.
        /// After each step the mount's real position is checked against the plan; if it is not turning the way it was planned to, the move is cancelled.
        /// </summary>
        public async Task<GuardedMoveResult> GotoAzAltGuardedAsync(double azimuth, double altitude, CancellationToken cancellationToken = default)
        {
            if (!ComsEnabled) return new GuardedMoveResult(false, "not connected", null);
            if (!double.IsFinite(azimuth) || !(altitude >= -90 && altitude <= 90)) return new GuardedMoveResult(false, "the target is not a position", null);
            if (_motionActive) return new GuardedMoveResult(false, "another move is already under way", null);
            if (await PollAsync() == null) return new GuardedMoveResult(false, "could not read the mount's position", null);
            var plan = Cable.PlanGoto(azimuth);
            return await RunPlanAsync(plan, altitude, cancellationToken);
        }

        /// <summary>
        /// Turns the azimuth back to the cable-neutral position by the way that unwinds the cord, however far round it has gone.
        /// </summary>
        public async Task<GuardedMoveResult> UnwindCableAsync(CancellationToken cancellationToken = default)
        {
            if (!ComsEnabled) return new GuardedMoveResult(false, "not connected", null);
            if (_motionActive) return new GuardedMoveResult(false, "another move is already under way", null);
            var pos = await PollAsync();
            if (pos == null) return new GuardedMoveResult(false, "could not read the mount's position", null);
            return await RunPlanAsync(Cable.PlanUnwind(), pos.Altitude, cancellationToken);
        }

        /// <summary>
        /// A small bounded move: turn the azimuth and altitude by the given amounts. Meant for press-and-hold buttons when the telescope cannot be seen:
        /// each step ends by itself, so if the page dies mid-press the mount stops within one step instead of running on to wrap the cable.
        /// Refused if it would need the long way round (that is what <see cref="GotoAzAltGuardedAsync"/> and <see cref="UnwindCableAsync"/> are for).
        /// </summary>
        public async Task<GuardedMoveResult> JogAsync(double azimuthDegrees, double altitudeDegrees, CancellationToken cancellationToken = default)
        {
            if (!ComsEnabled) return new GuardedMoveResult(false, "not connected", null);
            if (!(Math.Abs(azimuthDegrees) <= 90 && Math.Abs(altitudeDegrees) <= 90)) return new GuardedMoveResult(false, "a jog is at most 90 degrees", null);
            if (_motionActive) return new GuardedMoveResult(false, "another move is already under way", null);
            var pos = await PollAsync();
            if (pos == null) return new GuardedMoveResult(false, "could not read the mount's position", null);
            var plan = Cable.PlanGoto(pos.Azimuth + azimuthDegrees);
            if (plan.Allowed && Math.Abs(plan.TotalTravel - azimuthDegrees) > 1e-6)
                return new GuardedMoveResult(false, $"at the cable limit: that way would need {Math.Abs(plan.TotalTravel):F0} degrees the other way round, not a jog", plan);
            var alt = Math.Clamp(pos.Altitude + altitudeDegrees, -90, 90);
            return await RunPlanAsync(plan, alt, cancellationToken);
        }

        private async Task<GuardedMoveResult> RunPlanAsync(GotoPlan plan, double altitude, CancellationToken ct)
        {
            if (!plan.Allowed) return new GuardedMoveResult(false, plan.Reason, plan);
            _motionActive = true;
            try
            {
                foreach (var hop in plan.Hops)
                {
                    var (ok, message) = await RunHopAsync(hop, altitude, ct);
                    if (!ok) return new GuardedMoveResult(false, message, plan);
                }
                return new GuardedMoveResult(true, plan.Reason, plan);
            }
            catch (OperationCanceledException)
            {
                await CancelGotoAsync();
                await StopAllSlewAsync();
                await PollAsync(); // keep counting through the deceleration
                return new GuardedMoveResult(false, "cancelled", plan);
            }
            finally
            {
                _motionActive = false;
            }
        }

        private async Task<(bool Ok, string? Message)> RunHopAsync(GotoHop hop, double altitude, CancellationToken ct)
        {
            var startCumulative = Cable.Cumulative;
            var lo = Math.Min(startCumulative, hop.ExpectedCumulative) - RouteToleranceDegrees;
            var hi = Math.Max(startCumulative, hop.ExpectedCumulative) + RouteToleranceDegrees;

            if (!await GotoAzAltAsync(hop.Azimuth, altitude, precise: true))
                return (false, "the mount did not accept the goto");

            var clock = Stopwatch.StartNew();
            var silent = 0;
            var notThere = 0;
            await Task.Delay(TimeSpan.FromMilliseconds(600), ct); // a goto takes a moment to show as in progress
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var pos = await PollAsync();
                if (pos == null)
                {
                    if (++silent >= 3)
                    {
                        await CancelGotoAsync();
                        await StopAllSlewAsync();
                        Cable.MarkUncertain("contact with the mount was lost during a goto");
                        Alert(CableAlertKind.ContactLost, "Lost contact with the mount during a goto: check where the cable is before moving again.");
                        return (false, "lost contact with the mount");
                    }
                }
                else
                {
                    silent = 0;
                    // the guard of the guard: the count must stay on the course that was planned for this step
                    if (Cable.Cumulative < lo || Cable.Cumulative > hi)
                    {
                        await CancelGotoAsync();
                        await StopAllSlewAsync();
                        await PollAsync();
                        Alert(CableAlertKind.RouteMismatch, "The mount turned the wrong way round: the move was cancelled.");
                        return (false, $"the mount is not following the planned route (cable at {Cable.Cumulative:F0} degrees, expected between {lo:F0} and {hi:F0})");
                    }
                }

                var inProgress = await IsGotoInProgressAsync();
                if (inProgress == false)
                {
                    pos = await PollAsync();
                    if (pos != null)
                    {
                        var azError = Math.Abs(((pos.Azimuth - hop.Azimuth) % 360 + 540) % 360 - 180);
                        if (azError <= ArrivalToleranceDegrees && Math.Abs(Cable.Cumulative - hop.ExpectedCumulative) <= RouteToleranceDegrees)
                            return (true, null);
                        if (++notThere >= 3)
                        {
                            Cable.MarkUncertain("a goto ended away from where it was sent");
                            Alert(CableAlertKind.RouteMismatch, "A goto ended away from its target: check the cable.");
                            return (false, $"the goto ended {azError:F1} degrees from its target");
                        }
                    }
                }
                if (clock.Elapsed > GotoTimeout)
                {
                    await CancelGotoAsync();
                    await StopAllSlewAsync();
                    Cable.MarkUncertain("a goto did not finish");
                    return (false, "the goto did not finish in time");
                }
                await Task.Delay(GotoPollInterval, ct);
            }
        }

        /// <summary>
        /// Starts turning the azimuth axis at a fixed rate under the cable guard: the rate is lowered near a limit and the slew is stopped
        /// before it, by a monitor that watches the real position. Stop it with <see cref="StopGuardedSlewAsync"/>.
        /// A slew does not end by itself: if the page dies the mount keeps going, so use <see cref="JogAsync"/> when the telescope is out of sight.
        /// </summary>
        public async Task<GuardedSlewStart> BeginAzimuthSlewAsync(SlewDirection direction, SlewRate rate)
        {
            if (!ComsEnabled) return new GuardedSlewStart(false, SlewRate.Stop, "not connected");
            if (_motionActive) return new GuardedSlewStart(false, SlewRate.Stop, "another move is already under way");
            if (rate == SlewRate.Stop) return new GuardedSlewStart(false, SlewRate.Stop, "no rate");
            if (await PollAsync() == null) return new GuardedSlewStart(false, SlewRate.Stop, "could not read the mount's position");
            var increasing = (direction == SlewDirection.Positive) == _azimuthPositiveIsIncreasing;
            var cap = Cable.MaxFixedRate(increasing);
            if (cap == SlewRate.Stop)
                return new GuardedSlewStart(false, SlewRate.Stop, Cable.Uncertain ? $"the cable position is not known ({Cable.State.UncertainReason})" : "at the cable limit: turn the other way");
            var use = (SlewRate)Math.Min((int)rate, (int)cap);
            if (!await SlewFixedAsync(SlewAxis.RaAzm, direction, use)) return new GuardedSlewStart(false, SlewRate.Stop, "the mount did not accept the slew");
            _motionActive = true;
            _slewMonitorCts = new CancellationTokenSource();
            _slewMonitor = MonitorSlewAsync(direction, use, _slewMonitorCts.Token);
            return new GuardedSlewStart(true, use, use < rate ? "slowed near the cable limit" : null);
        }

        /// <summary>Stops a guarded slew (and any other motion) and keeps counting through the stop.</summary>
        public async Task StopGuardedSlewAsync()
        {
            var cts = _slewMonitorCts;
            var task = _slewMonitor;
            _slewMonitorCts = null;
            _slewMonitor = null;
            cts?.Cancel();
            if (task != null) { try { await task; } catch { } }
            await StopAllSlewAsync();
            _motionActive = false;
            await PollAsync();
            cts?.Dispose();
        }

        private async Task MonitorSlewAsync(SlewDirection direction, SlewRate startRate, CancellationToken ct)
        {
            var rate = startRate;
            var expectIncreasing = (direction == SlewDirection.Positive) == _azimuthPositiveIsIncreasing;
            var silent = 0;
            double? prevCumulative = null;
            var prevTime = DateTime.UtcNow;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(150, ct);
                    var pos = await PollAsync();
                    var now = DateTime.UtcNow;
                    if (pos == null)
                    {
                        if (++silent >= 3)
                        {
                            await StopAllSlewAsync();
                            Cable.MarkUncertain("contact with the mount was lost during a slew");
                            Alert(CableAlertKind.ContactLost, "Lost contact with the mount during a slew: check where the cable is before moving again.");
                            return;
                        }
                        continue;
                    }
                    silent = 0;

                    // the direction the cable is really turning, which settles what "positive" means on this mount
                    var increasing = expectIncreasing;
                    double speed = 0;
                    if (prevCumulative is double prev && (now - prevTime).TotalSeconds > 0.05)
                    {
                        var dt = (now - prevTime).TotalSeconds;
                        var v = (Cable.Cumulative - prev) / dt;
                        speed = Math.Abs(v);
                        if (speed > 0.05)
                        {
                            increasing = v > 0;
                            if (increasing != expectIncreasing)
                            {
                                _azimuthPositiveIsIncreasing = !_azimuthPositiveIsIncreasing;
                                expectIncreasing = increasing;
                            }
                        }
                    }
                    prevCumulative = Cable.Cumulative;
                    prevTime = now;

                    var room = increasing ? Cable.RoomPositive : Cable.RoomNegative;
                    var stopDistance = speed * 0.8 + 1.0; // one poll, the command's latency and the deceleration
                    if (room <= stopDistance && Cable.Options.Enabled)
                    {
                        await StopAllSlewAsync();
                        await PollAsync();
                        Alert(CableAlertKind.LimitReached, "The slew was stopped at the cable limit.");
                        return;
                    }
                    var cap = Cable.MaxFixedRate(increasing);
                    if (cap < rate)
                    {
                        rate = cap;
                        await SlewFixedAsync(SlewAxis.RaAzm, direction, rate);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // StopGuardedSlewAsync does the stopping
            }
            finally
            {
                _motionActive = false;
            }
        }
    }
}
