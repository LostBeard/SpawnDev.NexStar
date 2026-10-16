using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpawnDev.NexStar
{
    /// <summary>How close the mount is to wrapping its cables round itself.</summary>
    public enum CableZone
    {
        /// <summary>Plenty of travel left both ways.</summary>
        Safe,
        /// <summary>Within the warning margin of a limit.</summary>
        Warn,
        /// <summary>At or past a limit: further travel in that direction must not happen.</summary>
        Limit,
    }

    /// <summary>The settings of a <see cref="CableGuard"/>.</summary>
    public sealed class CableGuardOptions
    {
        /// <summary>When false the guard still counts turns but never refuses or reroutes anything.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// How far the azimuth may turn either side of the cable-neutral position, in degrees. 200 means 400 degrees of travel in all.
        /// The hand control and power cords end at the rotating part of the mount, so the limit is the cords', not the mount's.
        /// </summary>
        public double LimitDegrees { get; set; } = 200;

        /// <summary>Within this many degrees of a limit the guard warns and slows manual slews.</summary>
        public double WarnMarginDegrees { get; set; } = 30;

        /// <summary>
        /// The fastest the azimuth can turn, degrees per second. Used to decide whether a gap in readings could hide more than half a turn
        /// (after which direction is ambiguous) and whether a jump in the reading is too big to have been motion. Deliberately above any real slew
        /// until the mount's rates are measured.
        /// </summary>
        public double MaxSlewDegreesPerSecond { get; set; } = 5.0;

        /// <summary>
        /// How fast each fixed slew rate turns the azimuth, degrees per second (index 0-9). Defaults to what was measured on a 127SLT.
        /// </summary>
        public IReadOnlyList<double> FixedRateDegreesPerSecond { get; set; } = NexStarProtocol.MeasuredFixedRateDegreesPerSecond;

        /// <summary>Seconds a stop takes to take effect: one poll, the command's round trip and the deceleration.</summary>
        public double StopTimeSeconds { get; set; } = 1.2;

        /// <summary>Degrees kept in hand beyond the stopping distance.</summary>
        public double StopMarginDegrees { get; set; } = 2.0;

        /// <summary>A rate is allowed only while the room left is this many times its stopping distance, so speed falls away before the limit.</summary>
        public double SlowdownFactor { get; set; } = 2.5;

        /// <summary>
        /// Longest single goto step, degrees. Must stay well under 180: the mount then takes the short way round for every step, so the route
        /// is the one planned (see <see cref="CableGuard.PlanGoto"/>).
        /// </summary>
        public double MaxHopDegrees { get; set; } = 150;

        /// <summary>
        /// While the mount tracks (the azimuth drifts continuously, up to 15 degrees an hour), switch tracking off when the cable reaches a limit,
        /// rather than let it wrap. Off means only warn.
        /// </summary>
        public bool StopTrackingAtLimit { get; set; } = true;

        /// <summary>Plan and allow motion even while the cable position is uncertain. Off by default: a guard that does not know where the cord is cannot guard it.</summary>
        public bool AllowWhenUncertain { get; set; }
    }

    /// <summary>What a <see cref="CableGuard"/> remembers, to be stored between sessions.</summary>
    public sealed class CableState
    {
        /// <summary>Format version.</summary>
        public int Version { get; set; } = 1;

        /// <summary>Degrees turned from the cable-neutral position, positive = azimuth increasing. Unlimited: 450 means a turn and a quarter.</summary>
        public double Cumulative { get; set; }

        /// <summary>The last azimuth read (0-360), or null before the first reading.</summary>
        public double? LastAzimuth { get; set; }

        /// <summary>When it was read.</summary>
        public DateTime? LastReadUtc { get; set; }

        /// <summary>True when the counting may have lost track: confirm or reset the neutral position before trusting it.</summary>
        public bool Uncertain { get; set; } = true;

        /// <summary>Why it is uncertain, for the user.</summary>
        public string? UncertainReason { get; set; } = "no cable position recorded yet";

        /// <summary>Serialises the state.</summary>
        public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

        /// <summary>Reads a state; null if the text is not a usable state.</summary>
        public static CableState? FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var s = JsonSerializer.Deserialize<CableState>(json, JsonOptions);
                if (s == null || s.Version != 1 || !double.IsFinite(s.Cumulative)) return null;
                if (s.LastAzimuth is double a && (!double.IsFinite(a) || a < 0 || a >= 360)) return null;
                return s;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    }

    /// <summary>One step of a planned goto: the azimuth to send and the cumulative turn expected when it ends.</summary>
    /// <param name="Azimuth">Azimuth to goto, 0-360</param>
    /// <param name="ExpectedCumulative">The cumulative turn the cable should be at when this step ends</param>
    public readonly record struct GotoHop(double Azimuth, double ExpectedCumulative);

    /// <summary>A planned azimuth move that keeps the cable inside its limits.</summary>
    public sealed class GotoPlan
    {
        /// <summary>False if the move must not happen; <see cref="Reason"/> says why.</summary>
        public bool Allowed { get; init; }

        /// <summary>Why the move is refused, or a note about the route taken.</summary>
        public string? Reason { get; init; }

        /// <summary>The steps, in order. Each is under <see cref="CableGuardOptions.MaxHopDegrees"/> so the mount's short-way choice is the planned way.</summary>
        public IReadOnlyList<GotoHop> Hops { get; init; } = Array.Empty<GotoHop>();

        /// <summary>The signed turn of the whole move, degrees (negative = azimuth decreasing).</summary>
        public double TotalTravel { get; init; }

        /// <summary>True if the plan goes the long way round (or in several steps) because the short way would wrap the cable.</summary>
        public bool Rerouted { get; init; }

        /// <summary>The cumulative turn when the move is done.</summary>
        public double FinalCumulative { get; init; }
    }

    /// <summary>What a manual slew may do.</summary>
    /// <param name="Allowed">False when the slew must not start or must stop</param>
    /// <param name="MaxDegreesPerSecond">The fastest this slew may go (the requested rate, or less near a limit); 0 when not allowed</param>
    /// <param name="Reason">Why it is limited or refused, or null</param>
    public readonly record struct SlewDecision(bool Allowed, double MaxDegreesPerSecond, string? Reason);

    /// <summary>
    /// Keeps count of how many degrees the azimuth axis has turned from the cable-neutral position, so cords do not wrap.
    /// <para>
    /// The hand control and power cord of a NexStar alt-az mount end at the part that turns, but the mount reports azimuth only as 0-360. After a turn and
    /// a bit it reads the same as after a bit: nothing in the mount tells how many times the cord went round. This class unwraps the readings into a running total
    /// (<see cref="Cumulative"/>), keeps it across sessions (<see cref="State"/>), refuses or reroutes motion that would pass the limit, and flags when it may
    /// have lost count.
    /// </para>
    /// Pure and deterministic: time is passed in, there is no I/O, so every rule is unit-tested.
    /// </summary>
    public sealed class CableGuard
    {
        private readonly object _lock = new();
        private bool _rebaselineNext;

        /// <summary>Creates a guard, restoring a saved state if given.</summary>
        public CableGuard(CableGuardOptions? options = null, CableState? state = null)
        {
            Options = options ?? new CableGuardOptions();
            State = state ?? new CableState();
        }

        /// <summary>The settings. Change them between moves, not during one.</summary>
        public CableGuardOptions Options { get; }

        /// <summary>The persistent state: store <see cref="CableState.ToJson"/> whenever <see cref="StateChanged"/> fires.</summary>
        public CableState State { get; }

        /// <summary>
        /// Loads a saved state (see <see cref="CableState.FromJson"/>). The first reading after it re-checks the gap since the last one: after a long
        /// gap the count is marked uncertain, because the mount may have been powered off, which also moves the azimuth zero.
        /// </summary>
        public void Restore(CableState saved)
        {
            lock (_lock)
            {
                State.Version = saved.Version;
                State.Cumulative = saved.Cumulative;
                State.LastAzimuth = saved.LastAzimuth;
                State.LastReadUtc = saved.LastReadUtc;
                State.Uncertain = saved.Uncertain;
                State.UncertainReason = saved.UncertainReason;
                _recent.Clear();
                _slow.Clear();
                _rebaselineNext = false;
                StateChanged?.Invoke();
            }
        }

        /// <summary>Fired when the state changes in a way worth saving or showing (every reading that moved the count, a jump, uncertainty).</summary>
        public event Action? StateChanged;

        /// <summary>Fired when a reading jumped further than the mount could have turned: a sync or alignment moved the zero, not the motors.</summary>
        public event Action<double>? JumpDetected;

        /// <summary>Degrees turned from the neutral position, positive = azimuth increasing.</summary>
        public double Cumulative => State.Cumulative;

        /// <summary>True when the count may be wrong; see <see cref="CableState.UncertainReason"/>.</summary>
        public bool Uncertain => State.Uncertain;

        /// <summary>
        /// A limit is reached this many degrees early. Position is quantised (about 0.0000001 degree even in the precise format) and sits exactly
        /// on the limit after a stop, so "0.0000001 degrees left" must not read as room to move.
        /// </summary>
        public const double LimitEpsilonDegrees = 0.05;

        /// <summary>Degrees of travel left before the limit in the positive direction.</summary>
        public double RoomPositive => Options.LimitDegrees - State.Cumulative;

        /// <summary>Degrees of travel left before the limit in the negative direction.</summary>
        public double RoomNegative => Options.LimitDegrees + State.Cumulative;

        /// <summary>How close to a limit the cable is.</summary>
        public CableZone Zone
        {
            get
            {
                var room = Math.Min(RoomPositive, RoomNegative);
                if (room <= LimitEpsilonDegrees) return CableZone.Limit;
                if (room < Options.WarnMarginDegrees) return CableZone.Warn;
                return CableZone.Safe;
            }
        }

        /// <summary>The number of whole turns away from neutral (negative = the other way).</summary>
        public double Turns => State.Cumulative / 360.0;

        /// <summary>
        /// Takes an azimuth reading (0-360). Counts the turn since the last one the short way round, which is right as long as the mount cannot turn
        /// half a revolution between readings; if it could have, the count is marked uncertain instead.
        /// </summary>
        public void Update(double azimuthDegrees, DateTime utc)
        {
            if (!double.IsFinite(azimuthDegrees)) return;
            var az = Normalize360(azimuthDegrees);
            lock (_lock)
            {
                var s = State;
                if (s.LastAzimuth is not double last || s.LastReadUtc is not DateTime lastTime)
                {
                    s.LastAzimuth = az;
                    s.LastReadUtc = utc;
                    StateChanged?.Invoke();
                    return;
                }
                var gap = Math.Max(0, (utc - lastTime).TotalSeconds);
                var delta = Wrap180(az - last);
                s.LastAzimuth = az;
                s.LastReadUtc = utc;

                if (_rebaselineNext)
                {
                    // the previous command was a sync or alignment: the reading moved with no motion
                    _rebaselineNext = false;
                    StateChanged?.Invoke();
                    return;
                }

                var reach = Options.MaxSlewDegreesPerSecond * gap;
                if (Math.Abs(delta) > reach + 1.0)
                {
                    // further than the motors could have taken it: the zero moved (hand control alignment), the cable did not
                    JumpDetected?.Invoke(delta);
                    StateChanged?.Invoke();
                    return;
                }
                s.Cumulative += delta;
                TrackRate(utc, s.Cumulative);
                if (reach >= 170 && !s.Uncertain)
                {
                    // it could have turned half a revolution or more unseen, and the short way is then not necessarily the way it went
                    s.Uncertain = true;
                    s.UncertainReason = $"no reading for {gap:F0} s: the mount could have turned further than half a revolution";
                }
                StateChanged?.Invoke();
            }
        }

        private readonly Queue<(DateTime Utc, double Cumulative)> _recent = new();

        private readonly Queue<(DateTime Utc, double Cumulative)> _slow = new();

        private void TrackRate(DateTime utc, double cumulative)
        {
            _recent.Enqueue((utc, cumulative));
            while (_recent.Count > 2 && (utc - _recent.Peek().Utc).TotalSeconds > RateWindowSeconds) _recent.Dequeue();
            // one sample every few seconds for the long window: tracking moves the cable about 0.004 degrees a second, which needs minutes to see
            if (_slow.Count == 0 || (utc - _slow.Last().Utc).TotalSeconds >= SlowSampleSeconds) _slow.Enqueue((utc, cumulative));
            while (_slow.Count > 2 && (utc - _slow.Peek().Utc).TotalSeconds > SlowWindowSeconds) _slow.Dequeue();
        }

        /// <summary>Seconds between samples of the slow (tracking drift) rate estimate.</summary>
        public const double SlowSampleSeconds = 5;

        /// <summary>Seconds of readings the slow rate estimate looks back over.</summary>
        public const double SlowWindowSeconds = 600;

        /// <summary>
        /// The slow turn rate in degrees per second over the last several minutes (positive = azimuth increasing), for tracking drift where
        /// <see cref="RateDegreesPerSecond"/> would be lost in rounding; null until at least two minutes of readings.
        /// </summary>
        public double? SlowRateDegreesPerSecond
        {
            get
            {
                lock (_lock)
                {
                    if (_slow.Count < 2) return null;
                    var first = _slow.Peek();
                    var last = _slow.Last();
                    var span = (last.Utc - first.Utc).TotalSeconds;
                    return span >= 120 ? (last.Cumulative - first.Cumulative) / span : null;
                }
            }
        }

        /// <summary>How many seconds of readings the turn-rate estimate looks back over.</summary>
        public const double RateWindowSeconds = 10;

        /// <summary>
        /// How fast the cable is turning right now in degrees per second (positive = azimuth increasing), from the last few seconds of readings;
        /// null until there are at least two seconds of them. Tracking drifts the azimuth by up to 15 degrees an hour; a slew is degrees a second.
        /// </summary>
        public double? RateDegreesPerSecond
        {
            get
            {
                lock (_lock)
                {
                    if (_recent.Count < 2) return null;
                    var first = _recent.Peek();
                    var last = _recent.Last();
                    var span = (last.Utc - first.Utc).TotalSeconds;
                    return span >= 2 ? (last.Cumulative - first.Cumulative) / span : null;
                }
            }
        }

        /// <summary>The distance the azimuth keeps turning after a stop is ordered at a fixed rate, degrees.</summary>
        public double StoppingDistance(SlewRate rate)
        {
            var table = Options.FixedRateDegreesPerSecond;
            var speed = (int)rate >= 0 && (int)rate < table.Count ? table[(int)rate] : table[^1];
            return speed * Options.StopTimeSeconds + Options.StopMarginDegrees;
        }

        /// <summary>
        /// The fastest fixed slew rate the cable may be moved at toward a limit: the highest rate whose stopping distance (speed times the time a stop takes,
        /// plus a margin) is under the room left divided by <see cref="CableGuardOptions.SlowdownFactor"/>. Full speed in the middle, stepping down as the
        /// limit nears, <see cref="SlewRate.Stop"/> when not even a creep fits. Moving away from a limit is free.
        /// </summary>
        /// <param name="positive">True for azimuth increasing</param>
        public SlewRate MaxFixedRate(bool positive)
        {
            lock (_lock)
            {
                if (!Options.Enabled) return SlewRate.Rate9;
                if (State.Uncertain && !Options.AllowWhenUncertain) return SlewRate.Stop;
                var room = (positive ? RoomPositive : RoomNegative) - LimitEpsilonDegrees;
                if (room <= 0) return SlewRate.Stop;
                for (int r = (int)SlewRate.Rate9; r >= (int)SlewRate.Rate1; r--)
                {
                    if (room >= StoppingDistance((SlewRate)r) * Options.SlowdownFactor) return (SlewRate)r;
                }
                return SlewRate.Stop;
            }
        }

        /// <summary>
        /// Call after a sync or alignment command succeeds: the hand control moves its azimuth zero without moving the motors, so the next reading
        /// differs from the last with no turn behind it. That reading is taken as the new baseline, and the count is left alone.
        /// </summary>
        public void ExpectJump()
        {
            lock (_lock) _rebaselineNext = true;
        }

        /// <summary>Says the cable is in its neutral position right now (the cords hang straight): the count restarts at zero and is trusted.</summary>
        public void SetNeutral(double currentAzimuthDegrees, DateTime utc)
        {
            lock (_lock)
            {
                State.Cumulative = 0;
                _recent.Clear();
                _slow.Clear();
                State.LastAzimuth = Normalize360(currentAzimuthDegrees);
                State.LastReadUtc = utc;
                State.Uncertain = false;
                State.UncertainReason = null;
                _rebaselineNext = false;
                StateChanged?.Invoke();
            }
        }

        /// <summary>
        /// Says how far from neutral the cable is right now (from looking at it): the count is set to that and trusted.
        /// </summary>
        public void Confirm(double cumulativeDegrees, double currentAzimuthDegrees, DateTime utc)
        {
            if (!double.IsFinite(cumulativeDegrees)) throw new ArgumentOutOfRangeException(nameof(cumulativeDegrees));
            lock (_lock)
            {
                State.Cumulative = cumulativeDegrees;
                _recent.Clear();
                _slow.Clear();
                State.LastAzimuth = Normalize360(currentAzimuthDegrees);
                State.LastReadUtc = utc;
                State.Uncertain = false;
                State.UncertainReason = null;
                _rebaselineNext = false;
                StateChanged?.Invoke();
            }
        }

        /// <summary>Marks the count as possibly wrong (for example the connection dropped while the mount was moving).</summary>
        public void MarkUncertain(string reason)
        {
            lock (_lock)
            {
                State.Uncertain = true;
                State.UncertainReason = reason;
                StateChanged?.Invoke();
            }
        }

        /// <summary>
        /// Plans a move to an azimuth that never leaves the cable limits.
        /// The mount chooses its own direction for a goto, and the short way is not always the safe way. Both ways to the target are tried (the short
        /// way, and the long way a turn the other way round); the shortest one that stays inside the limits wins. If the short way is the one that
        /// wraps the cable, the plan goes the long way in steps each under <see cref="CableGuardOptions.MaxHopDegrees"/>, so the mount's short-way choice
        /// for each step is the way planned.
        /// </summary>
        /// <param name="targetAzimuth">The azimuth to reach, any angle (wrapped to 0-360)</param>
        public GotoPlan PlanGoto(double targetAzimuth)
        {
            lock (_lock) return PlanTo(Normalize360(targetAzimuth), mustReachCumulative: null);
        }

        /// <summary>
        /// Plans the move back to the cable-neutral position. From a turn and a half out this is the long way round, which is the point.
        /// </summary>
        public GotoPlan PlanUnwind()
        {
            lock (_lock)
            {
                if (State.LastAzimuth is not double a) return Refuse("no azimuth reading yet");
                var neutralAz = Normalize360(a - State.Cumulative);
                return PlanTo(neutralAz, mustReachCumulative: 0);
            }
        }

        private GotoPlan PlanTo(double target, double? mustReachCumulative)
        {
            if (State.LastAzimuth is not double a) return Refuse("no azimuth reading yet");
            if (!Options.Enabled)
            {
                // unguarded: the short way, one step. The mount decides.
                var d = Wrap180(target - a);
                return new GotoPlan { Allowed = true, TotalTravel = d, FinalCumulative = State.Cumulative + d, Hops = new[] { new GotoHop(target, State.Cumulative + d) }, Reason = "cable guard is off" };
            }
            if (State.Uncertain && !Options.AllowWhenUncertain)
                return Refuse($"the cable position is not known ({State.UncertainReason}): confirm or reset it first");

            var c = State.Cumulative;
            var limit = Options.LimitDegrees;
            var shortDelta = Wrap180(target - a);

            // candidate signed travels that end at the target azimuth: the short way and a turn either side of it
            GotoPlan? best = null;
            double bestTravel = double.MaxValue;
            foreach (var k in new[] { -2, -1, 0, 1, 2 })
            {
                var travel = shortDelta + 360.0 * k;
                var end = c + travel;
                if (mustReachCumulative is double want && Math.Abs(end - want) > 1e-6) continue;
                if (Math.Abs(end) > limit + 1e-9) continue;
                if (Math.Abs(travel) >= bestTravel) continue;
                // the path itself must stay inside the limits too: travel is monotonic, so ends inside means the whole way is inside
                // unless the start is already outside, which is only allowed when the move heads back in
                if (Math.Abs(c) > limit + 1e-9 && Math.Abs(end) >= Math.Abs(c)) continue;
                bestTravel = Math.Abs(travel);
                best = BuildPlan(target, a, c, travel, Math.Abs(travel - shortDelta) > 1e-6);
            }
            if (best == null)
                return Refuse(mustReachCumulative.HasValue
                    ? "no route back to neutral inside the limits"
                    : $"azimuth {target:F1} cannot be reached inside the cable limit of +/-{limit:F0} degrees from here");
            return best;
        }

        private GotoPlan BuildPlan(double target, double fromAz, double fromCumulative, double travel, bool rerouted)
        {
            var hopsCount = (int)Math.Max(1, Math.Ceiling(Math.Abs(travel) / Options.MaxHopDegrees));
            var hops = new List<GotoHop>(hopsCount);
            for (int i = 1; i <= hopsCount; i++)
            {
                var cumulative = fromCumulative + travel * i / hopsCount;
                var az = i == hopsCount ? target : Normalize360(fromAz + travel * i / hopsCount);
                hops.Add(new GotoHop(az, cumulative));
            }
            return new GotoPlan
            {
                Allowed = true,
                Hops = hops,
                TotalTravel = travel,
                Rerouted = rerouted,
                FinalCumulative = fromCumulative + travel,
                Reason = rerouted ? $"the short way would wrap the cable: going {(travel > 0 ? "up" : "down")} {Math.Abs(travel):F0} degrees the other way round" : null,
            };
        }

        private static GotoPlan Refuse(string reason) => new() { Allowed = false, Reason = reason };

        /// <summary>
        /// What a manual slew may do. Moving toward a limit slows down inside the warning margin and is refused at the limit; moving away is always allowed.
        /// </summary>
        /// <param name="positive">True for azimuth increasing</param>
        /// <param name="requestedDegreesPerSecond">The rate asked for</param>
        public SlewDecision CheckSlew(bool positive, double requestedDegreesPerSecond)
        {
            lock (_lock)
            {
                if (!Options.Enabled) return new SlewDecision(true, requestedDegreesPerSecond, null);
                if (State.Uncertain && !Options.AllowWhenUncertain)
                    return new SlewDecision(false, 0, $"the cable position is not known ({State.UncertainReason})");
                var room = positive ? RoomPositive : RoomNegative;
                if (room <= LimitEpsilonDegrees) return new SlewDecision(false, 0, $"at the cable limit: turn the other way ({room:F0} degrees)");
                if (room < Options.WarnMarginDegrees)
                {
                    // linear slow-down so the stop lands inside the limit: the last degrees go at a creep
                    var scale = room / Options.WarnMarginDegrees;
                    var rate = Math.Max(Math.Min(requestedDegreesPerSecond, 0.05), requestedDegreesPerSecond * scale * scale);
                    return new SlewDecision(true, Math.Min(requestedDegreesPerSecond, rate), $"{room:F0} degrees from the cable limit");
                }
                return new SlewDecision(true, requestedDegreesPerSecond, null);
            }
        }

        /// <summary>
        /// Seconds until the cable reaches a limit if the azimuth keeps turning at <paramref name="degreesPerSecond"/> (signed, positive = increasing),
        /// as it does while tracking an object; positive infinity if it is not heading for one.
        /// </summary>
        public double SecondsToLimit(double degreesPerSecond)
        {
            lock (_lock)
            {
                if (Math.Abs(degreesPerSecond) < 1e-12) return double.PositiveInfinity;
                var room = degreesPerSecond > 0 ? RoomPositive : RoomNegative;
                return Math.Max(0, room) / Math.Abs(degreesPerSecond);
            }
        }

        private static double Normalize360(double a)
        {
            a %= 360.0;
            if (a < 0) a += 360.0;
            return a >= 360.0 ? 0.0 : a;
        }

        private static double Wrap180(double a)
        {
            a = Normalize360(a);
            return a > 180.0 ? a - 360.0 : a;
        }
    }
}
