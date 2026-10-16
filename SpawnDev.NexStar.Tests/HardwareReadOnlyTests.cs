using SpawnDev.NexStar.Desktop;
using SpawnDev.UnitTesting;
using System.Diagnostics;

namespace SpawnDev.NexStar.Tests;

/// <summary>
/// The production service over a REAL hand control on a desktop serial port. READ-ONLY: these tests only ask
/// questions (version, model, alignment, tracking, positions, time, location) and never send a goto, slew,
/// sync, tracking or time/location set, so the mount does not move and its state is not changed.
/// They find the mount by the NEXSTAR_PORT environment variable or by an echo on each serial port, and are
/// reported as unsupported (not passed) when no mount answers or another program holds the port.
/// </summary>
public class HardwareReadOnlyTests
{
    /// <summary>One hardware test at a time across processes: a COM port has a single owner.</summary>
    private sealed class MountLease : IAsyncDisposable
    {
        // A file opened with no sharing is a lock that works across processes and, unlike a Mutex, does not care
        // which thread an async test resumes on. The OS drops it if the process dies.
        private readonly FileStream _lock;
        public SystemSerialTransport Transport { get; }

        private MountLease(FileStream lockFile, SystemSerialTransport transport)
        {
            _lock = lockFile;
            Transport = transport;
        }

        public static async Task<MountLease> AcquireAsync()
        {
            if (Environment.GetEnvironmentVariable("NEXSTAR_HARDWARE") == "0")
                throw new UnsupportedTestException("NEXSTAR_HARDWARE=0");
            var path = Path.Combine(Path.GetTempPath(), "SpawnDev.NexStar.Hardware.lock");
            FileStream? lockFile = null;
            var waited = Stopwatch.StartNew();
            while (lockFile == null)
            {
                try
                {
                    lockFile = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException) when (waited.Elapsed < TimeSpan.FromMinutes(3))
                {
                    await Task.Delay(250);
                }
                catch (IOException)
                {
                    throw new UnsupportedTestException("another test held the mount for more than 3 minutes");
                }
            }
            var transport = await SystemSerialTransport.FindMountAsync();
            if (transport == null)
            {
                await lockFile.DisposeAsync();
                throw new UnsupportedTestException("no NexStar hand control answered on any serial port (is it powered and plugged in, and is a browser holding the port?)");
            }
            return new MountLease(lockFile, transport);
        }

        public async ValueTask DisposeAsync()
        {
            await Transport.DisposeAsync();
            await _lock.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Mount_AnswersEveryReadOnlyQuery_WithSaneValues()
    {
        await using var lease = await MountLease.AcquireAsync();
        var service = new NexStarService();
        Check.That(await service.AttachTransportAsync(lease.Transport), $"attach: {service.LastError}");

        Check.That(service.VersionMajor > 0, $"version {service.Version}");
        Check.That(service.Model != TelescopeModel.Unknown, $"model {service.Model}");
        Check.That(service.CurrentTrackingMode is >= TrackingMode.Off and <= TrackingMode.EQSouth, "tracking mode");

        var z = await service.GetAzAltAsync();
        var zp = await service.GetAzAltAsync(precise: true);
        Check.That(z != null && zp != null, "az/alt read, standard and precise");
        Check.That(z!.Azimuth >= 0 && z.Azimuth < 360 && z.Altitude >= -90 && z.Altitude <= 90, $"az/alt in range: {z}");
        // standard has 19.8 arcsecond units; a still mount reads the same both ways
        Check.Near(z.Azimuth, zp!.Azimuth, 360.0 / 65536 * 2, "standard and precise azimuth agree");
        Check.Near(z.Altitude, zp.Altitude, 360.0 / 65536 * 2, "standard and precise altitude agree");

        var e = await service.GetRaDecAsync();
        var ep = await service.GetRaDecAsync(precise: true);
        Check.That(e != null && ep != null, "ra/dec read, standard and precise");
        Check.That(e!.RightAscension >= 0 && e.RightAscension < 360 && e.Declination >= -90 && e.Declination <= 90, $"ra/dec in range: {e}");

        var t = await service.GetTimeAsync();
        Check.That(t != null && t.TimezoneOffset >= -12 && t.TimezoneOffset <= 14, $"time {t}");
        var loc = await service.GetLocationAsync();
        Check.That(loc != null && Math.Abs(loc.Latitude) <= 90 && Math.Abs(loc.Longitude) <= 180, "location");
        Check.That(await service.EchoAsync('Q') == 'Q', "echo");
        Check.That(await service.IsGotoInProgressAsync() != null, "goto progress answers");
    }

    [TestMethod(Timeout = 120_000)]
    public async Task Clock_ReadAcrossEverySecond_NeverFailsToParse()
    {
        // The mount's clock sends 0x23 ('#') for second 35, and the old read cut that response short and returned
        // null. Poll for a full minute so second 35 is certain to be read from the real mount.
        await using var lease = await MountLease.AcquireAsync();
        var service = new NexStarService();
        Check.That(await service.AttachTransportAsync(lease.Transport), $"attach: {service.LastError}");
        var seen = new HashSet<int>();
        var failures = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(62))
        {
            var t = await service.GetTimeAsync();
            if (t == null) failures++;
            else seen.Add(t.LocalTime.Second);
            await Task.Delay(300);
        }
        Check.Equal(0, failures, "every clock read parsed");
        Check.That(seen.Contains(35), $"second 35 (the 0x23 byte) was read from the real mount; seconds seen: {seen.Count}");
        Check.That(seen.Count >= 55, $"nearly every second of the minute was sampled ({seen.Count})");
    }

    [TestMethod(Timeout = 120_000)]
    public async Task RapidQueries_AllFramesStayIntact()
    {
        await using var lease = await MountLease.AcquireAsync();
        var service = new NexStarService();
        Check.That(await service.AttachTransportAsync(lease.Transport), $"attach: {service.LastError}");
        var versionBefore = service.Version;
        var failures = 0;
        var worst = 0L;
        var total = 0L;
        const int rounds = 100;
        for (int i = 0; i < rounds; i++)
        {
            var sw = Stopwatch.StartNew();
            if (await service.GetAzAltAsync() == null) failures++;
            if (await service.GetAzAltAsync(precise: true) == null) failures++;
            if (await service.GetRaDecAsync() == null) failures++;
            if (await service.GetVersionAsync() == null) failures++;
            if (await service.GetTimeAsync() == null) failures++;
            sw.Stop();
            worst = Math.Max(worst, sw.ElapsedMilliseconds);
            total += sw.ElapsedMilliseconds;
        }
        Check.Equal(0, failures, "no query failed or was mis-framed");
        Check.Equal(versionBefore, service.Version, "the version never changed under the load");
        Check.Equal(0, lease.Transport.UnsolicitedBytes, "no stray bytes were left on the line");
        Console.WriteLine($"[Hardware] {rounds * 5} queries, mean {total / (double)rounds:F0} ms per 5, worst {worst} ms");
    }
}
