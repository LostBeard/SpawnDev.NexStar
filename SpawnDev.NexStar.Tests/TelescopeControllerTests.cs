using SpawnDev.UnitTesting;

namespace SpawnDev.NexStar.Tests;

/// <summary>
/// What the workspace does with the mount: gotos in the sky's true frame, the Sun kept out, aligning on a centred object, and the held slew pad with its
/// dead-man watchdog. The mount is the simulator (it speaks the real protocol and moves), so these show the controller's decisions; the real 127SLT's
/// altitude direction is not yet measured.
/// </summary>
public class TelescopeControllerTests
{
    // a public place; the sky is checked at a night hour and at local noon
    private const double Lat = 40.7484, Lon = -73.9857;
    private static readonly DateTime Night = new(2026, 1, 15, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Noon = new(2026, 6, 21, 16, 0, 0, DateTimeKind.Utc);

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

        public static async Task<Rig> StartAsync(double scale, double azimuth, double altitude = 30)
        {
            var mount = new SimulatedMount(scale, startAzimuth: azimuth, startAltitude: altitude);
            var rig = new Rig(mount);
            Check.That(await rig.Service.AttachTransportAsync(mount), "attach");
            rig.Service.Cable.Options.MaxSlewDegreesPerSecond = 3.0 * scale * 1.7;
            rig.Service.Cable.Options.FixedRateDegreesPerSecond = NexStarProtocol.MeasuredFixedRateDegreesPerSecond.Select(v => v * scale).ToArray();
            rig.Service.Cable.Options.LimitDegrees = 200;
            rig.Service.GotoPollInterval = TimeSpan.FromMilliseconds(60);
            rig.Service.Cable.Confirm(azimuth, ((azimuth % 360) + 360) % 360, DateTime.UtcNow);
            return rig;
        }
    }

    private static int Count(SimulatedMount m, string command) => m.Commands.Count(c => c == command);

    [TestMethod]
    public async Task Goto_OnAnUnalignedMountWithNoModel_IsRefused_AndTheMountDoesNotMove()
    {
        var rig = await Rig.StartAsync(40, 40);
        var result = await rig.Controller.GotoAsync(120, 45, Lat, Lon, Night);
        Check.That(!result.Success, "refused");
        Check.That(result.Message!.Contains("Align"), $"it says to align: {result.Message}");
        Check.Equal(0, Count(rig.Mount, "B") + Count(rig.Mount, "b"), "no goto command was sent");
        Check.That(!rig.Controller.HasFrame, "no frame");
    }

    [TestMethod]
    public async Task AlignHere_ThenGoto_LandsOnTheTruePosition_ThroughTheModel()
    {
        var rig = await Rig.StartAsync(40, 40, altitude: 30);
        await rig.Service.GetAzAltAsync(precise: true);
        // the object truly at 250 / 40 is centred, while the mount (powered on facing another way) says 40 / 30
        Check.That(await rig.Controller.AlignHereAsync(250, 40), $"aligned: {rig.Controller.Message}");
        Check.That(rig.Controller.HasFrame, "has a frame");
        Check.Near(150, rig.Controller.Pointing.AzimuthOffset, 1e-6, "azimuth offset is 150 (mount minus true, wrapped)");
        var here = rig.Controller.TruePointing()!.Value;
        Check.Near(250, here.Azimuth, 0.05, "the controller reports the true azimuth it is at"); Check.Near(40, here.Altitude, 0.05, "and altitude");

        var result = await rig.Controller.GotoAsync(300, 45, Lat, Lon, Night);
        Check.That(result.Success, $"goto: {result.Message}");
        // 300 true is 300 + 150 = 90 on the mount, and 45 true is 35 on the mount
        Check.Near(90, ((rig.Mount.TrueNow() % 360) + 360) % 360, 0.6, "the mount went to 90");
        Check.Near(35, rig.Mount.Altitude, 0.6, "and altitude 35");
        await rig.Service.PollAsync();
        var after = rig.Controller.TruePointing()!.Value;
        Check.Near(300, after.Azimuth, 0.6, "which the controller reads as true azimuth 300"); Check.Near(45, after.Altitude, 0.6, "and altitude 45");
    }

    [TestMethod]
    public async Task AlignHere_IsRefused_WhenTheHandControlIsNotConnected()
    {
        var service = new NexStarService();
        var controller = new TelescopeController(service);
        Check.That(!await controller.AlignHereAsync(10, 10), "refused");
        Check.That(controller.Message == "not connected", $"message: {controller.Message}");
        var result = await controller.GotoAsync(10, 10, Lat, Lon, Night);
        Check.That(!result.Success && result.Message == "not connected", "goto refused");
    }

    [TestMethod]
    public async Task Goto_NearTheSun_IsRefused_ByDaylight_AndAllowedAwayFromItOrAtNight()
    {
        var rig = await Rig.StartAsync(40, 100);
        rig.Controller.Pointing.AlignOn(100, 30, 100, 30);   // a frame, so only the Sun can be the reason
        var sun = SolarSystemMath.GetAzAlt(SolarSystemObject.Sun, Lat, Lon, Noon);
        Check.That(sun.Altitude > 60, $"the Sun is high at noon: {sun.Altitude:F1}");

        var onSun = await rig.Controller.GotoAsync(sun.Azimuth, sun.Altitude, Lat, Lon, Noon);
        Check.That(!onSun.Success && onSun.Message!.Contains("Sun"), $"on the Sun: {onSun.Message}");
        var nearSun = await rig.Controller.GotoAsync(sun.Azimuth, sun.Altitude - 8, Lat, Lon, Noon);
        Check.That(!nearSun.Success && nearSun.Message!.Contains("Sun"), $"8 degrees from the Sun: {nearSun.Message}");
        Check.Equal(0, Count(rig.Mount, "B") + Count(rig.Mount, "b"), "no goto command was sent near the Sun");

        var away = await rig.Controller.GotoAsync((sun.Azimuth + 120) % 360, 35, Lat, Lon, Noon);
        Check.That(away.Success, $"far from the Sun: {away.Message}");

        // at night the Sun is far below the horizon, so the same direction is fine
        var sunAtNight = SolarSystemMath.GetAzAlt(SolarSystemObject.Sun, Lat, Lon, Night);
        Check.That(sunAtNight.Altitude < -20, $"the Sun is down at night: {sunAtNight.Altitude:F1}");
        var night = await rig.Controller.GotoAsync(sun.Azimuth, 50, Lat, Lon, Night);
        Check.That(night.Success, $"the same direction at night: {night.Message}");
    }

    [TestMethod]
    public void SunGuard_KeepsItsDistance_AndOnlyWhileTheSunIsUp()
    {
        var sun = SolarSystemMath.GetAzAlt(SolarSystemObject.Sun, Lat, Lon, Noon);
        Check.That(SunGuard.Refuse(sun.Azimuth, sun.Altitude, Lat, Lon, Noon) != null, "on the Sun");
        Check.That(SunGuard.Refuse(sun.Azimuth, sun.Altitude - 11, Lat, Lon, Noon) != null, "11 degrees away");
        Check.That(SunGuard.Refuse(sun.Azimuth, sun.Altitude - 13, Lat, Lon, Noon) == null, "13 degrees away is clear");
        Check.That(SunGuard.Refuse(sun.Azimuth, sun.Altitude, Lat, Lon, Night) == null, "the Sun is far below the horizon at night");
        // just after sunset the Sun is still near the horizon: the zone around it stays closed
        var sunset = new DateTime(2026, 6, 22, 0, 40, 0, DateTimeKind.Utc);   // about 8:40 pm EDT
        var s = SolarSystemMath.GetAzAlt(SolarSystemObject.Sun, Lat, Lon, sunset);
        Check.That(s.Altitude > SunGuard.SunAltitudeLimit && s.Altitude < 5, $"the Sun is at the horizon: {s.Altitude:F1}");
        Check.That(SunGuard.Refuse(s.Azimuth, 2, Lat, Lon, sunset) != null, "close to the setting Sun is refused");
    }

    [TestMethod]
    public async Task Goto_BelowTheHorizon_IsRefused()
    {
        var rig = await Rig.StartAsync(40, 100);
        rig.Controller.Pointing.AlignOn(100, 30, 100, 30);
        var r = await rig.Controller.GotoAsync(200, -5, Lat, Lon, Night);
        Check.That(!r.Success && r.Message!.Contains("horizon"), $"below the horizon: {r.Message}");
    }

    [TestMethod]
    public async Task Pad_Right_TurnsAzimuthUp_UntilReleased_ThenTheMountIsStill()
    {
        var rig = await Rig.StartAsync(10, 0);
        var refusal = await rig.Controller.BeginPadAsync(PadDirection.Right, SlewRate.Rate7);
        Check.That(refusal == null, $"started: {refusal}");
        Check.Equal(PadDirection.Right, rig.Controller.PadHeld, "held");
        await Task.Delay(500);
        rig.Controller.Heartbeat();
        var during = rig.Mount.TrueNow();
        Check.That(during > 1.5, $"azimuth went up while held: {during:F2}");
        await rig.Controller.EndPadAsync();
        Check.That(rig.Controller.PadHeld == null, "released");
        var stopped = rig.Mount.TrueNow();
        await Task.Delay(300);
        Check.Near(stopped, rig.Mount.TrueNow(), 0.01, "it is still after the release");
    }

    [TestMethod]
    public async Task Pad_Left_TurnsAzimuthDown()
    {
        var rig = await Rig.StartAsync(10, 20);
        Check.That(await rig.Controller.BeginPadAsync(PadDirection.Left, SlewRate.Rate7) == null, "started");
        await Task.Delay(500);
        await rig.Controller.EndPadAsync();
        Check.That(rig.Mount.TrueNow() < 18.5, $"azimuth went down: {rig.Mount.TrueNow():F2}");
    }

    [TestMethod]
    public async Task Pad_UpAndDown_MoveTheAltitude_AndStop()
    {
        var rig = await Rig.StartAsync(10, 0, altitude: 30);
        Check.That(await rig.Controller.BeginPadAsync(PadDirection.Up, SlewRate.Rate7) == null, "up started");
        await Task.Delay(400);
        await rig.Controller.EndPadAsync();
        var up = rig.Mount.Altitude;
        Check.That(up > 31.5, $"the tube went up: {up:F2}");
        await Task.Delay(250);
        Check.Near(up, rig.Mount.Altitude, 0.01, "and stopped");
        Check.That(await rig.Controller.BeginPadAsync(PadDirection.Down, SlewRate.Rate7) == null, "down started");
        await Task.Delay(400);
        await rig.Controller.EndPadAsync();
        Check.That(rig.Mount.Altitude < up - 1.5, $"the tube came down: {rig.Mount.Altitude:F2}");
    }

    [TestMethod]
    public async Task Pad_WithoutAHeartbeat_IsStoppedByTheWatchdog()
    {
        var rig = await Rig.StartAsync(10, 0);
        rig.Controller.HeartbeatTimeout = TimeSpan.FromMilliseconds(300);
        using var cts = new CancellationTokenSource();
        var loop = rig.Controller.RunAsync(cts.Token);
        Check.That(await rig.Controller.BeginPadAsync(PadDirection.Right, SlewRate.Rate7) == null, "started");
        // the page goes quiet: no heartbeat
        await Task.Delay(1200);
        Check.That(rig.Controller.PadHeld == null, "the watchdog released the pad");
        Check.That(rig.Controller.Message!.Contains("stopped"), $"it says why: {rig.Controller.Message}");
        var a = rig.Mount.TrueNow();
        await Task.Delay(300);
        Check.Near(a, rig.Mount.TrueNow(), 0.01, "the mount is not moving");
        Check.That(a < 15, $"it did not run on: {a:F1}");
        cts.Cancel();
        await loop;
    }

    [TestMethod]
    public async Task Pad_WithAHeartbeat_KeepsGoing()
    {
        var rig = await Rig.StartAsync(10, 0);
        rig.Controller.HeartbeatTimeout = TimeSpan.FromMilliseconds(300);
        using var cts = new CancellationTokenSource();
        var loop = rig.Controller.RunAsync(cts.Token);
        Check.That(await rig.Controller.BeginPadAsync(PadDirection.Right, SlewRate.Rate7) == null, "started");
        for (int i = 0; i < 20; i++) { rig.Controller.Heartbeat(); await Task.Delay(60); }
        Check.Equal(PadDirection.Right, rig.Controller.PadHeld, "still held while the heartbeat continues");
        Check.That(rig.Mount.TrueNow() > 5, $"it kept turning: {rig.Mount.TrueNow():F1}");
        await rig.Controller.EndPadAsync();
        cts.Cancel();
        await loop;
    }

    [TestMethod]
    public async Task Pad_AtTheCableLimit_IsRefused_ForThatWayOnly()
    {
        var rig = await Rig.StartAsync(10, 200);   // at the +200 limit
        var up = await rig.Controller.BeginPadAsync(PadDirection.Right, SlewRate.Rate7);
        Check.That(up != null && up.Contains("limit"), $"further round is refused: {up}");
        Check.That(rig.Controller.PadHeld == null, "nothing held");
        var down = await rig.Controller.BeginPadAsync(PadDirection.Left, SlewRate.Rate5);
        Check.That(down == null, $"the way back is allowed: {down}");
        await rig.Controller.EndPadAsync();
    }

    [TestMethod]
    public async Task OnlyOnePadButton_AtATime()
    {
        var rig = await Rig.StartAsync(10, 0);
        Check.That(await rig.Controller.BeginPadAsync(PadDirection.Right, SlewRate.Rate5) == null, "first");
        var second = await rig.Controller.BeginPadAsync(PadDirection.Up, SlewRate.Rate5);
        Check.That(second != null, "the second is refused");
        Check.Equal(PadDirection.Right, rig.Controller.PadHeld, "the first is still the one held");
        await rig.Controller.EndPadAsync();
    }

    [TestMethod]
    public async Task Stop_CancelsAGotoInProgress_AndTheMountEndsShortOfTheTarget()
    {
        var rig = await Rig.StartAsync(10, 0);
        rig.Controller.Pointing.AlignOn(0, 30, 0, 30);
        var goto1 = rig.Controller.GotoAsync(120, 30, Lat, Lon, Night);
        await Task.Delay(500);
        await rig.Controller.StopAsync();
        var result = await goto1;
        Check.That(!result.Success, $"the goto did not complete: {result.Message}");
        var a = rig.Mount.TrueNow();
        await Task.Delay(300);
        Check.Near(a, rig.Mount.TrueNow(), 0.01, "the mount is still");
        Check.That(a > 1 && a < 100, $"it had started but not arrived: {a:F1}");
    }

    [TestMethod]
    public async Task TheRunLoop_ReadsThePositionWhileIdle()
    {
        var rig = await Rig.StartAsync(10, 0);
        rig.Controller.PollInterval = TimeSpan.FromMilliseconds(150);
        using var cts = new CancellationTokenSource();
        var before = Count(rig.Mount, "z");
        var loop = rig.Controller.RunAsync(cts.Token);
        await Task.Delay(1200);
        cts.Cancel();
        await loop;
        Check.That(Count(rig.Mount, "z") - before >= 4, $"the loop polled {Count(rig.Mount, "z") - before} times in 1.2 s");
    }

    [TestMethod]
    public async Task AReleaseThatArrivesWhileTheSlewIsStarting_StillStopsIt()
    {
        var rig = await Rig.StartAsync(10, 0);
        // press and release in the same breath, as a quick tap does: the stop must not run before the start and leave the mount turning
        var begin = rig.Controller.BeginPadAsync(PadDirection.Right, SlewRate.Rate7);
        var end = rig.Controller.EndPadAsync();
        await Task.WhenAll(begin, end);
        await Task.Delay(200);
        var a = rig.Mount.TrueNow();
        await Task.Delay(400);
        Check.Near(a, rig.Mount.TrueNow(), 0.01, "the mount is not turning");
        Check.That(rig.Controller.PadHeld == null, "nothing is held");
        Check.That(!rig.Service.GuardedMotionActive, "no guarded motion is left running");
    }

    [TestMethod]
    public async Task ManyQuickTaps_NeverLeaveTheMountTurning()
    {
        var rig = await Rig.StartAsync(10, 0);
        for (int i = 0; i < 6; i++)
        {
            var begin = rig.Controller.BeginPadAsync(i % 2 == 0 ? PadDirection.Right : PadDirection.Left, SlewRate.Rate6);
            await Task.Delay(i * 7);
            await rig.Controller.EndPadAsync();
            await begin;
        }
        await Task.Delay(300);
        var a = rig.Mount.TrueNow();
        await Task.Delay(400);
        Check.Near(a, rig.Mount.TrueNow(), 0.01, "the mount is not turning after six quick taps");
        Check.That(rig.Controller.PadHeld == null && !rig.Service.GuardedMotionActive, "nothing held, nothing running");
    }
}
