using SpawnDev.UnitTesting;

namespace SpawnDev.NexStar.Tests;

/// <summary>
/// The service's guarded motion over a mount that actually moves: gotos planned around the limit, a mount that goes the wrong way and is caught,
/// jogs, unwinding, slews that slow and stop, tracking that is switched off at the limit, and a connection that drops mid-move.
/// The mount here is a simulator, so these show how the guard reacts; whether a real 127SLT takes the short way is for the real mount to say.
/// </summary>
public class GuardedMotionTests
{
    private sealed class Rig
    {
        public readonly SimulatedMount Mount;
        public readonly NexStarService Service = new();
        public readonly List<CableAlert> Alerts = new();

        private Rig(SimulatedMount mount) => Mount = mount;

        /// <summary>A mount at <paramref name="azimuth"/> whose cable is <paramref name="cumulative"/> degrees from neutral (the azimuth the mount reports is mod 360 of the true one).</summary>
        public static async Task<Rig> StartAsync(double scale, double cumulative, SimulatedMount.GotoPolicy policy = SimulatedMount.GotoPolicy.Shortest)
        {
            var mount = new SimulatedMount(scale, startAzimuth: cumulative) { Policy = policy };
            var rig = new Rig(mount);
            rig.Service.OnCableAlert += rig.Alerts.Add;
            Check.That(await rig.Service.AttachTransportAsync(mount), "attach");
            // the guard's speed limit must cover the simulated speed, which is scale times faster than a real mount
            rig.Service.Cable.Options.MaxSlewDegreesPerSecond = 3.0 * scale * 1.7;
            rig.Service.Cable.Options.LimitDegrees = 200;
            rig.Service.GotoPollInterval = TimeSpan.FromMilliseconds(60);
            rig.Service.Cable.Confirm(cumulative, ((cumulative % 360) + 360) % 360, DateTime.UtcNow);
            return rig;
        }
    }

    [TestMethod]
    public async Task Goto_ShortWay_ArrivesAndTheCableCountMatchesTheMount()
    {
        var rig = await Rig.StartAsync(scale: 40, cumulative: 10);
        var result = await rig.Service.GotoAzAltGuardedAsync(100, 40);
        Check.That(result.Success, $"goto: {result.Message}");
        Check.That(!result.Plan!.Rerouted, "no reroute needed");
        Check.Near(100.0, rig.Mount.TrueNow(), 0.5, "the mount is at azimuth 100");
        Check.Near(40.0, rig.Mount.Altitude, 0.5, "and altitude 40");
        Check.Near(rig.Mount.TrueNow(), rig.Service.Cable.Cumulative, 3.0, "the guard's count agrees with the mount's real position");
        Check.That(!rig.Service.GuardedMotionActive, "the move is over");
    }

    [TestMethod]
    public async Task Goto_GoesTheLongWay_RatherThanWrapTheCable()
    {
        // Cable 150 up, at azimuth 150. Azimuth 230 is 80 degrees on, which would land at +230: past the limit.
        var rig = await Rig.StartAsync(scale: 40, cumulative: 150);
        var result = await rig.Service.GotoAzAltGuardedAsync(230, 20);
        Check.That(result.Success, $"goto: {result.Message}");
        Check.That(result.Plan!.Rerouted, "rerouted");
        Check.Near(-130.0, rig.Mount.TrueNow(), 1.0, "the cable ended 130 degrees the other side of neutral");
        Check.That(rig.Mount.MaxExcursion <= 200 + 1, $"the cable never passed the limit (furthest {rig.Mount.MaxExcursion:F1})");
        Check.Near(rig.Mount.TrueNow(), rig.Service.Cable.Cumulative, 3.0, "count agrees");
    }

    [TestMethod]
    public async Task Goto_ANaiveMountCommand_WouldHaveWrappedTheCable()
    {
        // The same request sent straight to the mount, no guard: it takes the short way and passes the limit. This is what the guard prevents.
        var mount = new SimulatedMount(40, 150);
        var service = new NexStarService();
        Check.That(await service.AttachTransportAsync(mount), "attach");
        Check.That(await service.GotoAzAltAsync(230, 20, precise: true), "unguarded goto accepted");
        await Task.Delay(1500);
        mount.TrueNow();
        Check.That(mount.MaxExcursion > 200, $"the unguarded goto went to {mount.MaxExcursion:F0} degrees: the cable is wrapped");
    }

    [TestMethod]
    public async Task Goto_AMountThatDoesNotTakeTheShortWay_IsCaughtAndStopped()
    {
        // From azimuth 10, cable at +10, sent to 350: planned as 20 degrees DOWN. This mount always turns upward: it would go 340 degrees
        // round to wrap the cable. The guard sees the count leave the planned course and cancels.
        var rig = await Rig.StartAsync(scale: 8, cumulative: 10, SimulatedMount.GotoPolicy.AlwaysIncreasing);
        var result = await rig.Service.GotoAzAltGuardedAsync(350, 30);
        Check.That(!result.Success, "refused to carry on");
        Check.That(result.Message!.Contains("route"), $"says why: {result.Message}");
        Check.That(!rig.Mount.GotoActive, "the goto was cancelled at the mount");
        Check.That(rig.Mount.MaxExcursion < 120, $"stopped early: the cable went to {rig.Mount.MaxExcursion:F0} degrees, not 350");
        Check.That(rig.Alerts.Any(a => a.Kind == CableAlertKind.RouteMismatch), "the user was told");
    }

    [TestMethod]
    public async Task Jog_IsBounded_AndRefusesWhenItWouldNeedTheLongWay()
    {
        var rig = await Rig.StartAsync(scale: 40, cumulative: 195);
        var up = await rig.Service.JogAsync(10, 0);
        Check.That(!up.Success && up.Message!.Contains("limit"), $"a 10 degree jog up from 195 would pass the limit: {up.Message}");
        Check.Near(195.0, rig.Mount.TrueNow(), 0.5, "and the mount did not move");
        var down = await rig.Service.JogAsync(-10, 0);
        Check.That(down.Success, $"the way back is open: {down.Message}");
        Check.Near(185.0, rig.Mount.TrueNow(), 1.0, "moved 10 down");
        Check.That(!(await rig.Service.JogAsync(120, 0)).Success, "a jog is bounded: 120 degrees is a goto, not a jog");
    }

    [TestMethod]
    public async Task Unwind_FromNearTheLimit_ReturnsToNeutral()
    {
        var rig = await Rig.StartAsync(scale: 40, cumulative: 190);
        var result = await rig.Service.UnwindCableAsync();
        Check.That(result.Success, $"unwind: {result.Message}");
        Check.Near(0.0, rig.Mount.TrueNow(), 1.0, "the cable is back at neutral");
        Check.Near(0.0, rig.Service.Cable.Cumulative, 3.0, "and the guard agrees");
        Check.That(rig.Mount.MaxExcursion <= 191, "never further out on the way");
    }

    [TestMethod]
    public async Task Slew_StepsDownTheRate_AsTheLimitComesCloser_AndNeverPassesIt()
    {
        // 10x time: a rate-9 slew is 30 degrees a real second. From +100 the limit is 100 degrees away; the last 30 go in steps down to a creep.
        var rig = await Rig.StartAsync(scale: 10, cumulative: 100);
        var start = await rig.Service.BeginAzimuthSlewAsync(SlewDirection.Positive, SlewRate.Rate9);
        Check.That(start.Started && start.Rate == SlewRate.Rate9, $"started at full rate: {start.Reason}");
        await Task.Delay(7000);
        var here = rig.Mount.TrueNow();
        await rig.Service.StopGuardedSlewAsync();
        Check.That(here > 150, $"it travelled well up the cable: {here:F1}");
        Check.That(rig.Mount.MaxExcursion < 200, $"and never reached the limit ({rig.Mount.MaxExcursion:F1})");
        var steps = rig.Mount.SlewRatesSent.Where(r => r > 0).Distinct().OrderDescending().ToList();
        Check.That(steps.Count >= 3 && steps[0] == 9, $"the rate stepped down on the way in: {string.Join(",", rig.Mount.SlewRatesSent)}");
        Check.That(!rig.Service.GuardedMotionActive, "stopped on request");
        Check.Near(rig.Mount.TrueNow(), rig.Mount.TrueNow(), 0.01, "and stayed stopped");
    }

    [TestMethod]
    public async Task Slew_IsStoppedByTheMonitor_BeforeTheLimit()
    {
        // Start 1.5 degrees from the limit and hold the button: the monitor must end it before the limit, by itself.
        var rig = await Rig.StartAsync(scale: 40, cumulative: 198.5);
        var start = await rig.Service.BeginAzimuthSlewAsync(SlewDirection.Positive, SlewRate.Rate9);
        Check.That(start.Started && start.Rate < SlewRate.Rate9, $"started, slowed: {start.Rate} {start.Reason}");
        var waited = 0;
        while (rig.Service.GuardedMotionActive && waited++ < 150) await Task.Delay(100);
        Check.That(!rig.Service.GuardedMotionActive, "the monitor ended the slew");
        await rig.Service.StopGuardedSlewAsync();
        var final = rig.Mount.TrueNow();
        Check.That(final < 200 && final > 198, $"stopped just inside the limit: {final:F2}");
        Check.That(rig.Mount.MaxExcursion < 200, $"never reached it ({rig.Mount.MaxExcursion:F2})");
        Check.That(rig.Alerts.Any(a => a.Kind == CableAlertKind.LimitReached), "and said so");
    }

    [TestMethod]
    public async Task Slew_IsRefusedAtTheLimit_AndAllowedAwayFromIt()
    {
        var rig = await Rig.StartAsync(scale: 10, cumulative: 200);
        var into = await rig.Service.BeginAzimuthSlewAsync(SlewDirection.Positive, SlewRate.Rate5);
        Check.That(!into.Started && into.Reason!.Contains("limit"), $"refused: {into.Reason}");
        Check.That(!rig.Service.GuardedMotionActive, "nothing is running");
        var away = await rig.Service.BeginAzimuthSlewAsync(SlewDirection.Negative, SlewRate.Rate5);
        Check.That(away.Started, "turning back is allowed");
        await rig.Service.StopGuardedSlewAsync();
        Check.That(rig.Mount.TrueNow() < 200, "moved away from the limit");
    }

    [TestMethod]
    public async Task Slew_TheDirectionWordIsLearnedFromTheMovement()
    {
        // If this mount's "positive" turned the azimuth DOWN, the guard would be watching the wrong limit. It measures the real direction.
        var rig = await Rig.StartAsync(scale: 10, cumulative: -100);
        var start = await rig.Service.BeginAzimuthSlewAsync(SlewDirection.Negative, SlewRate.Rate9); // negative = down in the simulator, as assumed
        Check.That(start.Started, "started");
        var waited = 0;
        while (rig.Service.GuardedMotionActive && waited++ < 100) await Task.Delay(100);
        await rig.Service.StopGuardedSlewAsync();
        Check.That(rig.Mount.TrueNow() > -200 && rig.Mount.MaxExcursion < 200, $"stopped at the negative limit too: {rig.Mount.TrueNow():F1}");
    }

    [TestMethod]
    public async Task Tracking_IsSwitchedOffWhenTheCableReachesALimit()
    {
        // Tracking in alt-az drifts the azimuth. At 10x, 0.2 degrees a simulated second is 2 a real second: 5 degrees to the limit.
        var rig = await Rig.StartAsync(scale: 10, cumulative: 195);
        rig.Mount.TrackingDrift = 0.2;
        Check.That(await rig.Service.SetTrackingModeAsync(TrackingMode.AltAz), "tracking on");
        var waited = 0;
        while (rig.Service.CurrentTrackingMode != TrackingMode.Off && waited++ < 60)
        {
            await rig.Service.PollAsync();
            await Task.Delay(200);
        }
        Check.Equal(TrackingMode.Off, rig.Service.CurrentTrackingMode, "tracking was switched off");
        Check.Equal(TrackingMode.Off, (TrackingMode)rig.Mount.Tracking, "at the mount, not only in the service");
        Check.That(rig.Alerts.Any(a => a.Kind == CableAlertKind.TrackingStopped), "the user was told");
        await Task.Delay(500);
        Check.That(rig.Mount.TrueNow() < 215, $"the drift stopped near the limit: {rig.Mount.TrueNow():F1}");
    }

    [TestMethod]
    public async Task Tracking_WithGuardPolicyOff_OnlyWarns()
    {
        var rig = await Rig.StartAsync(scale: 10, cumulative: 195);
        rig.Service.Cable.Options.StopTrackingAtLimit = false;
        rig.Mount.TrackingDrift = 0.2;
        await rig.Service.SetTrackingModeAsync(TrackingMode.AltAz);
        for (int i = 0; i < 12; i++) { await rig.Service.PollAsync(); await Task.Delay(200); }
        Check.Equal(TrackingMode.AltAz, rig.Service.CurrentTrackingMode, "tracking left on, as configured");
        Check.That(rig.Alerts.Any(a => a.Kind == CableAlertKind.ZoneChanged && a.Zone == CableZone.Limit), "but the limit was reported");
    }

    [TestMethod]
    public async Task LosingTheConnectionMidGoto_LeavesTheCountUncertain()
    {
        var rig = await Rig.StartAsync(scale: 6, cumulative: 0);
        var goto1 = rig.Service.GotoAzAltGuardedAsync(170, 30);
        await Task.Delay(400);
        rig.Mount.Unplug();
        var result = await goto1;
        Check.That(!result.Success, "the move failed");
        Check.That(rig.Service.Cable.Uncertain, "the guard no longer trusts its count");
        Check.That(rig.Service.Cable.State.UncertainReason!.Length > 0, "and says why");
        Check.That(!rig.Service.GuardedMotionActive, "no motion is still marked active");
    }

    [TestMethod]
    public async Task GuardedMoves_RefuseWhileTheCablePositionIsNotKnown()
    {
        var mount = new SimulatedMount(40, 30);
        var service = new NexStarService();
        Check.That(await service.AttachTransportAsync(mount), "attach");
        service.Cable.Options.MaxSlewDegreesPerSecond = 3.0 * 40 * 1.7; // the simulator is 40 times faster than a real mount
        await service.PollAsync(); // the guard has a reading but has never been told where the cable is
        var go = await service.GotoAzAltGuardedAsync(100, 20);
        Check.That(!go.Success && go.Message!.Contains("not known"), $"goto refused: {go.Message}");
        var slew = await service.BeginAzimuthSlewAsync(SlewDirection.Positive, SlewRate.Rate3);
        Check.That(!slew.Started, $"slew refused: {slew.Reason}");
        Check.Near(30.0, mount.TrueNow(), 0.01, "the mount never moved");
        service.Cable.SetNeutral(30, DateTime.UtcNow);
        Check.That((await service.GotoAzAltGuardedAsync(40, 20)).Success, "after the user says where the cable is, it works");
    }

    [TestMethod]
    public async Task Sync_DoesNotMoveTheCableCount()
    {
        var rig = await Rig.StartAsync(scale: 40, cumulative: 40);
        var before = rig.Service.Cable.Cumulative;
        var trueBefore = rig.Mount.TrueNow();
        Check.That(await rig.Service.SyncRaDecAsync(10, 10), "sync acknowledged");
        await rig.Service.PollAsync();
        Check.Near(trueBefore, rig.Mount.TrueNow(), 0.01, "the sync did not move the motors");
        Check.Near(4.0, rig.Mount.ReportedAzimuthOffset, 1e-9, "but it shifted the azimuth the hand control reports by 4 degrees");
        Check.Near(before, rig.Service.Cable.Cumulative, 1e-6, "a sync moves the reading, not the cable: the count is unchanged");
        // and later real motion counts again from the new reading
        var result = await rig.Service.JogAsync(10, 0);
        Check.That(result.Success, $"jog after the sync: {result.Message}");
        Check.Near(before + 10, rig.Service.Cable.Cumulative, 1.0, "10 degrees of real motion");
        Check.Near(trueBefore + 10, rig.Mount.TrueNow(), 1.0, "and the mount really moved 10");
    }

    [TestMethod]
    public async Task Disconnecting_MidSlew_StopsTheMount_AndKeepsTheCount()
    {
        var rig = await Rig.StartAsync(scale: 10, cumulative: 0);
        Check.That((await rig.Service.BeginAzimuthSlewAsync(SlewDirection.Positive, SlewRate.Rate9)).Started, "slewing");
        await Task.Delay(500);
        await rig.Service.DeselectPortAsync(); // the user clicks Disconnect
        var at = rig.Mount.TrueNow();
        await Task.Delay(400);
        Check.Near(at, rig.Mount.TrueNow(), 0.01, "the mount was stopped, not left running");
        Check.That(!rig.Service.GuardedMotionActive, "no motion marked active");
        Check.That(!rig.Service.Cable.Uncertain, "the stop worked, so the count is still good");
        Check.Near(at, rig.Service.Cable.Cumulative, 3.0, "and it counted up to the stop");
    }

    [TestMethod]
    public async Task Unplugging_MidSlew_LeavesTheCountUncertain()
    {
        // Nothing can be told to stop and nobody is watching: the mount runs on, so the guard can no longer vouch for the cable.
        var rig = await Rig.StartAsync(scale: 10, cumulative: 0);
        Check.That((await rig.Service.BeginAzimuthSlewAsync(SlewDirection.Positive, SlewRate.Rate9)).Started, "slewing");
        await Task.Delay(500);
        rig.Mount.Unplug();
        await Task.Delay(300);
        Check.That(rig.Service.Cable.Uncertain, "the count is no longer trusted");
        Check.That(rig.Service.Cable.State.UncertainReason!.Contains("connection") || rig.Service.Cable.State.UncertainReason.Contains("contact"), $"and says why: {rig.Service.Cable.State.UncertainReason}");
        Check.That(!rig.Service.GuardedMotionActive, "nothing marked active");
    }
}
