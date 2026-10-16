using SpawnDev.NexStar.Simulation;
using SpawnDev.UnitTesting;

namespace SpawnDev.NexStar.Tests;

/// <summary>
/// Tracking by the app's own pointing model: a chain of small gotos that each end by themselves. The mount is the simulator (real speeds), the targets are
/// synthetic and move far faster than the sky so a few seconds of test show the following.
/// </summary>
public class TelescopeTrackingTests
{
    private const double Lat = 40.7484, Lon = -73.9857;

    private sealed class Rig
    {
        public readonly SimulatedMount Mount;
        public readonly NexStarService Service = new();
        public readonly TelescopeController Controller;

        private Rig(SimulatedMount mount)
        {
            Mount = mount;
            Controller = new TelescopeController(Service);
        }

        public static async Task<Rig> StartAsync(double azimuth, double altitude, bool withFrame = true, double scale = 1)
        {
            var mount = new SimulatedMount(scale, startAzimuth: azimuth, startAltitude: altitude);
            var rig = new Rig(mount);
            Check.That(await rig.Service.AttachTransportAsync(mount), "attach");
            rig.Service.Cable.Options.LimitDegrees = 200;
            // the guard's speed limits must cover a simulator that runs faster than the clock
            rig.Service.Cable.Options.MaxSlewDegreesPerSecond = 3.0 * scale * 1.7;
            rig.Service.Cable.Options.FixedRateDegreesPerSecond = NexStarProtocol.MeasuredFixedRateDegreesPerSecond.Select(v => v * scale).ToArray();
            rig.Service.GotoPollInterval = TimeSpan.FromMilliseconds(100);
            rig.Service.Cable.Confirm(azimuth, ((azimuth % 360) + 360) % 360, DateTime.UtcNow);
            if (withFrame) rig.Controller.Pointing.AlignOn(azimuth, altitude, azimuth, altitude);   // the mount's frame is the true frame here
            rig.Controller.TrackingInterval = TimeSpan.FromMilliseconds(400);
            return rig;
        }
    }

    /// <summary>A target that moves at a steady rate in azimuth from where it is when the test starts.</summary>
    private static TargetFunction Moving(double az0, double alt, double degreesPerSecond)
    {
        var t0 = DateTime.UtcNow;
        return utc => (((az0 + degreesPerSecond * (utc - t0).TotalSeconds) % 360 + 360) % 360, alt);
    }

    [TestMethod(Timeout = 120000)]
    public async Task Tracking_FollowsAMovingTarget_ToAFractionOfADegree()
    {
        var rig = await Rig.StartAsync(100, 40);
        var target = Moving(100, 40, 0.05);
        Check.That(await rig.Controller.StartTrackingAsync("test target", target, Lat, Lon) == null, $"started: {rig.Controller.Message}");
        Check.That(rig.Controller.IsTracking && rig.Controller.TrackingName == "test target", "tracking");
        await Task.Delay(7000);
        var expected = target(DateTime.UtcNow)!.Value;
        var at = rig.Mount.TrueNow();
        Check.That(at - 100 > 0.2, $"the mount followed it: azimuth {at:F3}");
        Check.That(Math.Abs(at - expected.Azimuth) < 0.12, $"within 0.12 degrees of the target: mount {at:F3}, target {expected.Azimuth:F3}");
        Check.Near(40, rig.Mount.Altitude, 0.05, "altitude");
        Check.That(rig.Controller.IsTracking, "still tracking");
        await rig.Controller.StopTrackingAsync();
    }

    [TestMethod(Timeout = 120000)]
    public async Task StopTracking_StopsTheMount_AndNothingFollowsAfterwards()
    {
        var rig = await Rig.StartAsync(100, 40);
        Check.That(await rig.Controller.StartTrackingAsync("t", Moving(100, 40, 0.1), Lat, Lon) == null, "started");
        await Task.Delay(3000);
        await rig.Controller.StopTrackingAsync();
        Check.That(!rig.Controller.IsTracking && rig.Controller.TrackingName == null, "stopped");
        await Task.Delay(1200);   // a step in flight would have ended by now
        var a = rig.Mount.TrueNow();
        await Task.Delay(1500);
        Check.Near(a, rig.Mount.TrueNow(), 0.005, "the mount no longer moves");
        Check.That(!rig.Mount.GotoActive, "no goto in flight");
    }

    [TestMethod(Timeout = 120000)]
    public async Task ADisplacedTelescope_IsReacquired_ByAGuardedGoto()
    {
        var rig = await Rig.StartAsync(100, 40);
        Check.That(await rig.Controller.StartTrackingAsync("t", Moving(100, 40, 0.0), Lat, Lon) == null, "started");
        await Task.Delay(1500);
        // the telescope gets knocked 3 degrees away: a goto behind the tracker's back
        await rig.Service.GotoAzAltAsync(103, 40, precise: true);
        await Task.Delay(6500);
        Check.Near(100, rig.Mount.TrueNow(), 0.15, "back on the target");
        Check.That(rig.Controller.IsTracking, "and still tracking");
        await rig.Controller.StopTrackingAsync();
    }

    [TestMethod(Timeout = 120000)]
    public async Task Tracking_IsRefused_WhenItShouldNotStart()
    {
        var rig = await Rig.StartAsync(100, 40);
        var below = await rig.Controller.StartTrackingAsync("t", _ => (100, -3), Lat, Lon);
        Check.That(below != null && below.Contains("horizon"), $"below the horizon: {below}");

        // the Sun, by day: refused. (At night the Sun is far below the horizon and there is nothing to refuse.)
        var noon = new DateTime(2026, 6, 21, 16, 0, 0, DateTimeKind.Utc);
        rig.Controller.Clock = () => noon;
        var onSun = await rig.Controller.StartTrackingAsync("the Sun", utc => { var s = SolarSystemMath.GetAzAlt(SolarSystemObject.Sun, Lat, Lon, utc); return (s.Azimuth, s.Altitude); }, Lat, Lon);
        Check.That(onSun != null && onSun.Contains("Sun"), $"the Sun is refused: {onSun}");
        rig.Controller.Clock = () => DateTime.UtcNow;

        Check.That(await rig.Controller.StartTrackingAsync("t", _ => null, Lat, Lon) != null, "a target with no position");
        Check.That(!rig.Controller.IsTracking, "nothing is tracking");

        var unaligned = await Rig.StartAsync(100, 40, withFrame: false);
        var noFrame = await unaligned.Controller.StartTrackingAsync("t", Moving(100, 40, 0), Lat, Lon);
        Check.That(noFrame != null && noFrame.Contains("Align"), $"no frame: {noFrame}");
        var none = new TelescopeController(new NexStarService());
        Check.Equal("not connected", await none.StartTrackingAsync("t", Moving(100, 40, 0), Lat, Lon), "not connected");
    }

    [TestMethod(Timeout = 120000)]
    public async Task ATargetThatSets_EndsTracking_WithAMessage_AndTheMountStops()
    {
        var rig = await Rig.StartAsync(100, 4);
        var t0 = DateTime.UtcNow;
        var ended = new TaskCompletionSource<string>();
        rig.Controller.TrackingEnded += why => ended.TrySetResult(why);
        // altitude 4 falling 0.9 degrees a second: below the horizon after a few seconds
        Check.That(await rig.Controller.StartTrackingAsync("setting", utc => (100, 4 - 0.9 * (utc - t0).TotalSeconds), Lat, Lon) == null, "started");
        var why = await Task.WhenAny(ended.Task, Task.Delay(15000)) == ended.Task ? await ended.Task : null;
        Check.That(why != null && why.Contains("horizon"), $"it ended by itself: {why}");
        Check.That(!rig.Controller.IsTracking, "no longer tracking");
        Check.That(rig.Controller.Message!.Contains("setting"), $"the message names it: {rig.Controller.Message}");
        await Task.Delay(1500);
        var alt = rig.Mount.Altitude;
        await Task.Delay(1000);
        Check.Near(alt, rig.Mount.Altitude, 0.01, "the mount has stopped");
        Check.That(alt >= -0.5, $"it never went below the horizon: {alt:F2}");
    }

    [TestMethod(Timeout = 120000)]
    public async Task Tracking_StopsAtTheCableLimit_NotPastIt()
    {
        var rig = await Rig.StartAsync(199.0, 40);   // the cable is 199 degrees round of a 200 limit
        var ended = new TaskCompletionSource<string>();
        rig.Controller.TrackingEnded += why => ended.TrySetResult(why);
        Check.That(await rig.Controller.StartTrackingAsync("t", Moving(199.0, 40, 0.5), Lat, Lon) == null, $"started: {rig.Controller.Message}");   // heading further round
        var why = await Task.WhenAny(ended.Task, Task.Delay(20000)) == ended.Task ? await ended.Task : null;
        Check.That(why != null, "tracking ended by itself at the limit");
        await Task.Delay(1500);
        Check.That(rig.Mount.MaxExcursion <= 200.3, $"the cable never went past its limit: furthest {rig.Mount.MaxExcursion:F2}");
        Check.That(rig.Service.Cable.Cumulative <= 200.1, $"counted {rig.Service.Cable.Cumulative:F2}");
    }

    [TestMethod(Timeout = 120000)]
    public async Task AFarTarget_IsReachedTheLongWay_RatherThanWrapTheCable()
    {
        var rig = await Rig.StartAsync(150, 40, scale: 10);   // the cable is 150 degrees round; the target is 100 degrees further, which the short way would take past the limit
        rig.Service.GotoPollInterval = TimeSpan.FromMilliseconds(80);
        Check.That(await rig.Controller.StartTrackingAsync("far", Moving(250, 40, 0), Lat, Lon) == null, $"started: {rig.Controller.Message}");
        await Task.Delay(1000 + (int)(260 / 30.0 * 1000) + 4000);
        Check.That(rig.Mount.MaxExcursion <= 200.3, $"the cable never went past its limit: furthest {rig.Mount.MaxExcursion:F1}");
        Check.That(rig.Controller.IsTracking, $"it is tracking the far target ({rig.Controller.Message})");
        Check.Near(250, ((rig.Mount.TrueNow() % 360) + 360) % 360, 0.3, "at the target");
        Check.That(rig.Service.Cable.Cumulative < -100, $"it went the long way: the cable is at {rig.Service.Cable.Cumulative:F0}");
        await rig.Controller.StopTrackingAsync();
    }

    [TestMethod(Timeout = 120000)]
    public async Task APadButton_TakesOverFromTracking()
    {
        var rig = await Rig.StartAsync(100, 40);
        Check.That(await rig.Controller.StartTrackingAsync("t", Moving(100, 40, 0), Lat, Lon) == null, "started");
        await Task.Delay(800);
        Check.That(await rig.Controller.BeginPadAsync(PadDirection.Up, SlewRate.Rate5) == null, "the pad started");
        Check.That(!rig.Controller.IsTracking, "tracking gave way");
        await rig.Controller.EndPadAsync();
    }

    [TestMethod(Timeout = 120000)]
    public async Task AGotoOfYourOwn_TakesOverFromTracking()
    {
        var rig = await Rig.StartAsync(100, 40);
        Check.That(await rig.Controller.StartTrackingAsync("t", Moving(100, 40, 0), Lat, Lon) == null, "started");
        await Task.Delay(800);
        var result = await rig.Controller.GotoAsync(130, 40, Lat, Lon, new DateTime(2026, 1, 15, 3, 0, 0, DateTimeKind.Utc));
        Check.That(result.Success, $"goto: {result.Message}");
        Check.That(!rig.Controller.IsTracking, "tracking gave way");
        Check.Near(130, rig.Mount.TrueNow(), 0.6, "went to the goto target");
    }
}
