namespace SpawnDev.NexStar
{
    /// <summary>Where a target is in the sky (true azimuth and altitude, degrees) at a moment, or null when it cannot be tracked then.</summary>
    public delegate (double Azimuth, double Altitude)? TargetFunction(DateTime utc);

    /// <summary>
    /// Keeps the telescope on a target by the app's own pointing model, for a mount that has no alignment of its own (or when the app's alignment is
    /// better). Every interval it works out where the target will be a moment from now and goes there with a small goto. Each goto ends by itself, so if
    /// the page dies the mount stops within one step: a held rate slew would run on, and cannot be allowed to when nobody can see the telescope.
    /// A target that drifts far from the telescope (a bump, a lost step) is re-acquired by a full cable-guarded goto.
    /// </summary>
    public sealed partial class TelescopeController
    {
        private CancellationTokenSource? _trackCts;
        private Task? _trackTask;
        private DateTime _trackBeat = DateTime.MinValue;

        /// <summary>The time between steps.</summary>
        public TimeSpan TrackingInterval { get; set; } = TimeSpan.FromMilliseconds(1500);

        /// <summary>A target further than this from the telescope is re-acquired with a guarded goto rather than a small step, degrees.</summary>
        public double ReacquireDegrees { get; set; } = 0.5;

        /// <summary>The clock the tracker reads (a test can run the sky faster).</summary>
        public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

        public bool IsTracking => TrackingName != null && _trackTask is { IsCompleted: false };

        /// <summary>The name of what is being tracked.</summary>
        public string? TrackingName { get; private set; }

        /// <summary>Tracking stopped on its own (the target set, the cable reached its limit, the connection dropped): why.</summary>
        public event Action<string>? TrackingEnded;

        /// <summary>Starts tracking. Returns why not, or null when it started.</summary>
        public async Task<string?> StartTrackingAsync(string name, TargetFunction target, double latitude, double longitude)
        {
            if (!_mount.ComsEnabled) return Message = "not connected";
            if (_pad != null) return Message = "a slew button is held";
            if (!HasFrame) return Message = "the telescope is not aligned: centre an object in the eyepiece and use Align here first";
            if (IsTracking) await StopTrackingAsync();
            var refusal = CheckTarget(target, Clock(), latitude, longitude, out _);
            if (refusal != null) return Message = refusal;

            _trackCts = new CancellationTokenSource();
            var ct = _trackCts.Token;
            TrackingName = name;
            _trackBeat = DateTime.UtcNow;
            Message = null;
            _trackTask = Task.Run(() => TrackLoopAsync(name, target, latitude, longitude, ct));
            return null;
        }

        /// <summary>Stops tracking and the step in flight.</summary>
        public async Task StopTrackingAsync()
        {
            var cts = _trackCts;
            var task = _trackTask;
            _trackCts = null;
            try { cts?.Cancel(); } catch (ObjectDisposedException) { }
            if (task != null) { try { await task; } catch { } }
            cts?.Dispose();
            if (task != null && _mount.ComsEnabled) { try { await _mount.CancelGotoAsync(); } catch { } }
            TrackingName = null;
        }

        /// <summary>Where the target is and whether it may be gone to: above the horizon, clear of the Sun.</summary>
        private string? CheckTarget(TargetFunction target, DateTime utc, double latitude, double longitude, out (double Azimuth, double Altitude) at)
        {
            at = default;
            if (target(utc) is not { } position) return "the target cannot be tracked now";
            at = position;
            if (!double.IsFinite(position.Azimuth) || !double.IsFinite(position.Altitude)) return "the target is not a position";
            if (position.Altitude < 0) return "the target is below the horizon";
            return SunGuard.Refuse(position.Azimuth, position.Altitude, latitude, longitude, utc);
        }

        private async Task TrackLoopAsync(string name, TargetFunction target, double latitude, double longitude, CancellationToken ct)
        {
            string ended = "tracking was stopped";
            int failures = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var started = DateTime.UtcNow;
                    _trackBeat = started;
                    if (!_mount.ComsEnabled) { ended = "the connection was lost"; break; }

                    // where the target will be when this step's goto ends
                    var when = Clock() + TrackingInterval;
                    var refusal = CheckTarget(target, when, latitude, longitude, out var at);
                    if (refusal != null) { ended = refusal; break; }
                    var (mountAz, mountAlt) = _mount.IsAligned ? at : Pointing.ToMount(at.Azimuth, at.Altitude);

                    var here = await _mount.PollAsync();
                    if (here == null) { if (++failures >= 3) { ended = "the mount did not answer"; break; } await Task.Delay(TrackingInterval, ct); continue; }
                    failures = 0;
                    double dAz = Math.Abs(((mountAz - here.Azimuth) % 360 + 540) % 360 - 180), dAlt = Math.Abs(mountAlt - here.Altitude);
                    double distance = Math.Sqrt(dAz * dAz + dAlt * dAlt);

                    if (distance > ReacquireDegrees)
                    {
                        var result = await _mount.GotoAzAltGuardedAsync(mountAz, mountAlt, ct);
                        if (!result.Success) { ended = result.Message ?? "the telescope could not get to the target"; break; }
                    }
                    else
                    {
                        // a small step: the cable must stay inside its limits (a step this small only matters at the limit)
                        var plan = _mount.Cable.PlanGoto(mountAz);
                        if (!plan.Allowed || plan.Rerouted) { ended = plan.Reason ?? "the cable is at its limit: unwind it"; break; }
                        if (!await _mount.GotoAzAltAsync(mountAz, mountAlt, precise: true)) { if (++failures >= 3) { ended = "the mount would not accept a goto"; break; } }
                    }

                    var wait = TrackingInterval - (DateTime.UtcNow - started);
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ended = ex.Message; }
            finally
            {
                if (!ct.IsCancellationRequested)
                {
                    // ended on its own: say so, and let the step in flight finish by itself
                    Message = $"stopped tracking {name}: {ended}";
                    TrackingName = null;
                    TrackingEnded?.Invoke(ended);
                }
            }
        }
    }
}
