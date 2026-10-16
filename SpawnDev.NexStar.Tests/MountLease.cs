using SpawnDev.NexStar.Desktop;
using SpawnDev.UnitTesting;
using System.Diagnostics;

namespace SpawnDev.NexStar.Tests;

/// <summary>
/// One hardware test at a time across processes (a COM port has a single owner), holding the real mount for the test.
/// Tests that MOVE the telescope ask for <c>needsMotion</c> and are only run when NEXSTAR_ALLOW_MOTION=1 is set: moving a real telescope
/// is something a person decides, so without it they are reported as unsupported, never as passed.
/// </summary>
internal sealed class MountLease : IAsyncDisposable
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

    public static async Task<MountLease> AcquireAsync(bool needsMotion = false)
    {
        if (Environment.GetEnvironmentVariable("NEXSTAR_HARDWARE") == "0")
            throw new UnsupportedTestException("NEXSTAR_HARDWARE=0");
        if (needsMotion && Environment.GetEnvironmentVariable("NEXSTAR_ALLOW_MOTION") != "1")
            throw new UnsupportedTestException("this test moves the telescope: set NEXSTAR_ALLOW_MOTION=1 with the cords near neutral and the area clear");
        var path = Path.Combine(Path.GetTempPath(), "SpawnDev.NexStar.Hardware.lock");
        FileStream? lockFile = null;
        var waited = Stopwatch.StartNew();
        while (lockFile == null)
        {
            try
            {
                lockFile = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (waited.Elapsed < TimeSpan.FromMinutes(5))
            {
                await Task.Delay(250);
            }
            catch (IOException)
            {
                throw new UnsupportedTestException("another test held the mount for more than 5 minutes");
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
