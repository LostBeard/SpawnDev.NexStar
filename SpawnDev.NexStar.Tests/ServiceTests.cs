using SpawnDev.UnitTesting;
using System.Text;

namespace SpawnDev.NexStar.Tests;

/// <summary>
/// NexStarService above the wire: connection, failed reads, command bytes. Runs the production service,
/// channel, framing and parsers over a <see cref="SimulatedHandControl"/>.
/// </summary>
public class ServiceTests
{
    private static async Task<(NexStarService service, SimulatedHandControl mount)> Connected()
    {
        var mount = new SimulatedHandControl();
        var service = new NexStarService();
        Check.That(await service.AttachTransportAsync(mount), "attach");
        mount.Received.Clear();
        return (service, mount);
    }

    [TestMethod]
    public async Task Attach_ReadsVersionTimeAndLocation_ThatContainHashBytes()
    {
        // The simulator sends what the real mount sends: version 5.35 (minor 0x23) and a clock at second 35 (0x23).
        var mount = new SimulatedHandControl();
        var service = new NexStarService();
        Check.That(await service.AttachTransportAsync(mount), "attach");
        Check.Equal("5.35", service.Version, "version with a # byte in it");
        Check.Equal(TelescopeModel.SLT, service.Model, "model 7 is the SLT");
        Check.That(!service.IsAligned, "unaligned");
        Check.That(service.CurrentTime != null, "the clock read at second 35 must not fail");
        Check.Equal(35, service.CurrentTime!.LocalTime.Second, "second 35");
        Check.That(service.Location != null && service.Location.Latitude > 0 && service.Location.Longitude < 0, "location");
        Check.That(service.CurrentAzAlt != null && service.CurrentRaDec != null, "positions read");
    }

    [TestMethod]
    public async Task Attach_ToAPortWhereNothingAnswers_FailsCleanly()
    {
        var mount = new SimulatedHandControl { Override = _ => null };
        var service = new NexStarService();
        var disconnected = 0;
        service.OnDisconnected += _ => disconnected++;
        Check.That(!await service.AttachTransportAsync(mount), "a silent port is not a telescope");
        Check.That(service.LastError != null, "the reason is available");
        Check.That(!service.SerialPortSelected, "not selected");
        Check.That(!mount.Connected, "the transport was closed");
        Check.Equal(1, disconnected, "one disconnect event");
    }

    [TestMethod]
    public async Task FailedReads_AreNullAndLeaveTheCachedStateAlone()
    {
        var (service, mount) = await Connected();
        var before = service.CurrentAzAlt;
        Check.That(before != null, "have a position");
        mount.Override = _ => null; // silence
        Check.That(await service.GetAzAltAsync() == null, "silence is null, not (0, 0)");
        Check.That(await service.GetRaDecAsync() == null, "RA/Dec");
        Check.That(await service.GetTimeAsync() == null, "time");
        Check.That(await service.GetLocationAsync() == null, "location");
        Check.That(await service.GetTrackingModeAsync() == null, "tracking is unknown, not Off");
        Check.That(await service.IsGotoInProgressAsync() == null, "goto progress is unknown, not 'finished'");
        Check.That(await service.GetAlignmentStatusAsync() == null, "alignment is unknown, not 'not aligned'");
        Check.Equal(TelescopeModel.Unknown, await service.GetModelAsync(), "model");
        Check.That(ReferenceEquals(before, service.CurrentAzAlt), "the last good position is still the cached one");

        mount.Override = cmd => cmd[0] == 'Z' ? Encoding.ASCII.GetBytes("ZZZZ,0000#") : null;
        Check.That(await service.GetAzAltAsync() == null, "garbage text is not a position");
        Check.That(ReferenceEquals(before, service.CurrentAzAlt), "garbage did not replace it");
    }

    [TestMethod]
    public async Task Position_NegativeAltitude_IsRead()
    {
        var (service, mount) = await Connected();
        mount.AzAlt = "4000,E000"; // az 90, alt -45
        var p = await service.GetAzAltAsync();
        Check.That(p != null, "read");
        Check.Near(90, p!.Azimuth, 1e-9, "azimuth");
        Check.Near(-45, p.Altitude, 1e-9, "altitude");
    }

    [TestMethod]
    public async Task SyncTime_SendsTheStandardOffset_AndTheDaylightFlag()
    {
        var (service, mount) = await Connected();
        // 14:00 UTC on July 1 in Eastern is 10:00 EDT: the hand control gets 10:00, offset -5 (251), DST 1.
        Check.That(await service.SyncTimeAsync(new DateTime(2026, 7, 1, 14, 0, 0, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById("America/New_York")), "acknowledged");
        Check.Bytes(new byte[] { (byte)'H', 10, 0, 0, 7, 1, 26, 251, 1 }, mount.Received.Single(), "set time command");
    }

    [TestMethod]
    public async Task Goto_OutOfRangeAndNaN_SendNothing()
    {
        var (service, mount) = await Connected();
        Check.That(!await service.GotoAzAltAsync(361, 0), "azimuth 361");
        Check.That(!await service.GotoAzAltAsync(double.NaN, 0), "azimuth NaN");
        Check.That(!await service.GotoAzAltAsync(0, 91), "altitude 91");
        Check.That(!await service.GotoRaDecAsync(0, -91), "dec -91");
        Check.That(!await service.SyncRaDecAsync(-1, 0), "ra -1");
        Check.That(!await service.SetLocationAsync(91, 0), "latitude 91");
        Check.That(!await service.SetLocationAsync(double.NaN, 0), "latitude NaN");
        Check.Equal(0, mount.Received.Count, "no command reached the mount");
        Check.That(await service.GotoAzAltAsync(123.5, 10), "a valid goto goes through");
        Check.Equal(1, mount.Received.Count, "and sends exactly one command");
    }

    [TestMethod]
    public async Task StopAllSlew_IsOnlyTrueWhenBothAxesAcknowledge()
    {
        var (service, mount) = await Connected();
        Check.That(await service.StopAllSlewAsync(), "both acknowledged");
        // altitude axis (0x11) never answers
        mount.Override = cmd => cmd[0] == 'P' && cmd[2] == 0x11 ? null : new byte[] { (byte)'#' };
        Check.That(!await service.StopAllSlewAsync(), "one axis did not acknowledge: the caller must know the mount may still be moving");
        Check.Equal(2, mount.Received.TakeLast(2).Count(c => c[0] == 'P'), "both axes were still asked to stop");
    }

    [TestMethod]
    public async Task Unplugging_ClearsStateAndRaisesEachEventOnce()
    {
        var (service, mount) = await Connected();
        var connectedEvents = 0;
        var disconnectedEvents = 0;
        service.OnDisconnected += _ => disconnectedEvents++;
        mount.Unplug();
        await Task.Delay(50); // the handler runs the disconnect without blocking the unplug
        Check.Equal(1, disconnectedEvents, "disconnected once");
        Check.Equal(0, connectedEvents, "no connect");
        Check.That(!service.SerialPortSelected && service.Transport == null, "no transport");
        Check.That(service.CurrentAzAlt == null && service.Location == null && service.Version == "", "state cleared so the UI cannot show a dead mount's position");
        Check.That(await service.GetAzAltAsync() == null, "queries after the unplug just fail");
    }

    [TestMethod]
    public async Task Tracking_AllModesRoundTrip_AndUnknownByteIsNotOff()
    {
        var (service, mount) = await Connected();
        foreach (var mode in new[] { TrackingMode.AltAz, TrackingMode.EQNorth, TrackingMode.EQSouth, TrackingMode.Off })
        {
            Check.That(await service.SetTrackingModeAsync(mode), $"set {mode}");
            Check.Equal(mode, (await service.GetTrackingModeAsync())!.Value, $"get {mode}");
        }
        mount.Tracking = 9;
        Check.That(await service.GetTrackingModeAsync() == null, "a mode this library does not know is unknown, not Off");
    }
}
