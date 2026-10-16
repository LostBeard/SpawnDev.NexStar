using SpawnDev.UnitTesting;
using System.Diagnostics;

namespace SpawnDev.NexStar.Tests;

/// <summary>
/// Tests that MOVE the real telescope, to hold it to what was measured on 2026-10-16 on a 127SLT (hand control 5.35) and which the cable guard
/// relies on. Only run with NEXSTAR_ALLOW_MOTION=1; otherwise reported as unsupported. Each moves the mount a few degrees at most (the guarded
/// round trip 60 degrees), never touches altitude, refuses to run while the mount is tracking, and always stops and cancels on the way out.
/// </summary>
public class HardwareMotionTests
{
    private static double Wrap180(double a) { a %= 360; if (a > 180) a -= 360; if (a < -180) a += 360; return a; }

    private static async Task<(NexStarService service, double startAz, double alt)> ConnectAsync(MountLease lease)
    {
        var service = new NexStarService();
        Check.That(await service.AttachTransportAsync(lease.Transport), $"attach: {service.LastError}");
        Check.Equal(TrackingMode.Off, service.CurrentTrackingMode, "the mount must not be tracking for a motion test");
        var p = await service.GetAzAltAsync(precise: true);
        Check.That(p != null, "position");
        service.Cable.SetNeutral(p!.Azimuth, DateTime.UtcNow);
        return (service, p.Azimuth, p.Altitude);
    }

    private static async Task StopEverythingAsync(NexStarService service)
    {
        for (int i = 0; i < 3; i++)
        {
            var a = await service.StopAllSlewAsync();
            var b = await service.CancelGotoAsync();
            if (a && b) break;
        }
    }

    private static async Task SettleAsync(NexStarService service, double seconds = 10)
    {
        var t = Stopwatch.StartNew();
        double? last = null;
        int still = 0;
        while (t.Elapsed.TotalSeconds < seconds)
        {
            var p = await service.GetAzAltAsync(precise: true);
            if (p != null && last != null && Math.Abs(Wrap180(p.Azimuth - last.Value)) < 0.002) { if (++still >= 4) return; } else still = 0;
            last = p?.Azimuth;
            await Task.Delay(120);
        }
    }

    private static async Task WaitGotoAsync(NexStarService service, double seconds)
    {
        await Task.Delay(500);
        var w = Stopwatch.StartNew();
        while (await service.IsGotoInProgressAsync() == true && w.Elapsed.TotalSeconds < seconds) await Task.Delay(100);
        await SettleAsync(service);
    }

    /// <summary>The way the mount first turns for a goto of <paramref name="turn"/> degrees: +1 up, -1 down, 0 if it did not move.</summary>
    private static async Task<int> FirstDirectionAsync(NexStarService service, double baseAz, double alt, double turn)
    {
        var target = (((baseAz + turn) % 360) + 360) % 360;
        Check.That(await service.GotoAzAltAsync(target, alt, precise: true), $"goto {turn} accepted");
        var clock = Stopwatch.StartNew();
        double seen = 0;
        while (clock.Elapsed.TotalSeconds < 6)
        {
            await Task.Delay(40);
            var p = await service.GetAzAltAsync(precise: true);
            if (p == null) continue;
            var d = Wrap180(p.Azimuth - baseAz);
            if (Math.Abs(d) >= 5)
            {
                await StopEverythingAsync(service);
                throw new Exception($"FAILED: the first seconds of a goto of {turn} moved {d:F1} degrees: stopped");
            }
            if (Math.Abs(d) >= 1.2) { seen = d; break; }
        }
        await service.CancelGotoAsync();
        await StopEverythingAsync(service);
        await SettleAsync(service);
        await service.GotoAzAltAsync(baseAz, alt, precise: true);
        await WaitGotoAsync(service, 20);
        return Math.Sign(seen);
    }

    [TestMethod(Timeout = 600_000)]
    public async Task Goto_FirstTurnsTheShortWay_ForEveryStepTheGuardPlans()
    {
        // The guard splits long moves into steps of at most 150 degrees and relies on the mount taking the short way for each. Measured: it does
        // up to +182.5 (up) and -177.5 (down) of turn, a 2.5 degree bias, so 150 leaves 27 degrees of margin.
        await using var lease = await MountLease.AcquireAsync(needsMotion: true);
        var (service, startAz, alt) = await ConnectAsync(lease);
        try
        {
            foreach (var turn in new[] { 10.0, -10, 45, -45, 90, -90, 120, -120, 150, -150 })
            {
                var dir = await FirstDirectionAsync(service, startAz, alt, turn);
                Check.Equal(Math.Sign(turn), dir, $"a goto of {turn:+0;-0} degrees must start the short way");
            }
            Check.That(service.Cable.Options.MaxHopDegrees <= 150, "the guard's hop size stays within what was measured");
        }
        finally
        {
            await StopEverythingAsync(service);
            await service.DeselectPortAsync();
        }
    }

    [TestMethod(Timeout = 300_000)]
    public async Task FixedSlews_PositiveIncreasesAzimuth_AndSpeedsMatchTheMeasuredTable()
    {
        await using var lease = await MountLease.AcquireAsync(needsMotion: true);
        var (service, startAz, alt) = await ConnectAsync(lease);
        try
        {
            foreach (var rate in new[] { SlewRate.Rate5, SlewRate.Rate6, SlewRate.Rate7 })
            {
                foreach (var dir in new[] { SlewDirection.Positive, SlewDirection.Negative })
                {
                    var before = (await service.GetAzAltAsync(precise: true))!.Azimuth;
                    Check.That(await service.SlewFixedAsync(SlewAxis.RaAzm, dir, rate), "slew accepted");
                    var clock = Stopwatch.StartNew();
                    var samples = new List<(double t, double az)>();
                    while (clock.Elapsed.TotalSeconds < 3 && Math.Abs(Wrap180((samples.Count > 0 ? samples[^1].az : before) - before)) < 3)
                    {
                        var p = await service.GetAzAltAsync(precise: true);
                        if (p != null) samples.Add((clock.Elapsed.TotalSeconds, p.Azimuth));
                        await Task.Delay(40);
                    }
                    await service.StopAllSlewAsync();
                    await SettleAsync(service);
                    var after = (await service.GetAzAltAsync(precise: true))!.Azimuth;
                    var turned = Wrap180(after - before);
                    Check.That((dir == SlewDirection.Positive) == (turned > 0), $"{rate} {dir} turned {turned:F3}: positive must increase azimuth");
                    var tail = samples.Where(s => s.t > samples[^1].t * 0.4).ToList();
                    var speed = Math.Abs(Wrap180(tail[^1].az - tail[0].az)) / (tail[^1].t - tail[0].t);
                    var expected = NexStarProtocol.MeasuredFixedRateDegreesPerSecond[(int)rate];
                    Check.That(speed > expected * 0.6 && speed < expected * 1.6, $"{rate} {dir}: {speed:F3} deg/s against the measured table's {expected}");
                }
            }
        }
        finally
        {
            await StopEverythingAsync(service);
            await service.GotoAzAltAsync(startAz, alt, precise: true);
            await WaitGotoAsync(service, 30);
            await service.DeselectPortAsync();
        }
    }

    [TestMethod(Timeout = 600_000)]
    public async Task GuardedGoto_SixtyDegreesAndBack_KeepsAnExactCableCount()
    {
        await using var lease = await MountLease.AcquireAsync(needsMotion: true);
        var (service, startAz, alt) = await ConnectAsync(lease);
        try
        {
            var up = await service.GotoAzAltGuardedAsync((startAz + 60) % 360, alt);
            Check.That(up.Success, $"guarded goto up: {up.Message}");
            Check.Near(60.0, service.Cable.Cumulative, 1.0, "the cable count after 60 degrees");
            var back = await service.GotoAzAltGuardedAsync(startAz, alt);
            Check.That(back.Success, $"guarded goto back: {back.Message}");
            Check.Near(0.0, service.Cable.Cumulative, 1.0, "and back at neutral");
        }
        finally
        {
            await StopEverythingAsync(service);
            await service.DeselectPortAsync();
        }
    }
}
