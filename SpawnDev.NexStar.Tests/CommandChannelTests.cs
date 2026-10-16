using SpawnDev.UnitTesting;
using System.Diagnostics;

namespace SpawnDev.NexStar.Tests;

/// <summary>
/// One command at a time over a byte line: arrival in pieces, timeouts, late bytes, and serialisation.
/// The "line" here is a loopback that delivers bytes the way a serial port does (in arbitrary pieces, later);
/// the same channel runs unchanged over WebSerial, WebUSB and the desktop port, and the hardware tests
/// run it against the real mount.
/// </summary>
public class CommandChannelTests
{
    private static byte[] Ascii(string s) => System.Text.Encoding.ASCII.GetBytes(s);

    [TestMethod]
    public async Task ResponseInPieces_IsReassembled()
    {
        var channel = new NexStarCommandChannel();
        var payload = await channel.ExchangeAsync(Ascii("E"), async command =>
        {
            _ = Task.Run(async () =>
            {
                foreach (var chunk in new[] { "34", "AB", ",", "12CE", "#" })
                {
                    await Task.Delay(5);
                    channel.OnBytes(Ascii(chunk));
                }
            });
            await Task.CompletedTask;
        });
        Check.Bytes(Ascii("34AB,12CE"), payload, "position payload");
    }

    [TestMethod]
    public async Task VersionWithHash_ThroughTheChannel_ThenTheNextCommandIsNotPoisoned()
    {
        var channel = new NexStarCommandChannel();
        var version = await channel.ExchangeAsync(Ascii("V"), _ =>
        {
            channel.OnBytes(new byte[] { 0x05, 0x23 });
            channel.OnBytes(new byte[] { 0x23 });
            return Task.CompletedTask;
        });
        Check.Bytes(new byte[] { 0x05, 0x23 }, version, "version");
        var model = await channel.ExchangeAsync(Ascii("m"), _ =>
        {
            channel.OnBytes(new byte[] { 0x07, 0x23 });
            return Task.CompletedTask;
        });
        Check.Bytes(new byte[] { 0x07 }, model, "the next response is not the leftover # of the version response");
        Check.Equal(0, channel.UnsolicitedBytes, "no stray bytes");
    }

    [TestMethod]
    public async Task Timeout_ReturnsNull_AndLateBytesDoNotAnswerTheNextCommand()
    {
        var channel = new NexStarCommandChannel();
        var timedOut = await channel.ExchangeAsync(Ascii("V"), _ => Task.CompletedTask, timeoutMs: 100);
        Check.That(timedOut == null, "no response means null, not an empty payload");
        Check.Equal(1, channel.FailedCommands, "counted");

        // The slow response to the command that timed out arrives now, while the next command is about to go.
        _ = Task.Run(async () =>
        {
            await Task.Delay(60);
            channel.OnBytes(new byte[] { 0x05, 0x23, 0x23 });
        });
        var model = await channel.ExchangeAsync(Ascii("m"), command =>
        {
            // the real answer to this command
            _ = Task.Run(async () => { await Task.Delay(10); channel.OnBytes(new byte[] { 0x07, 0x23 }); });
            return Task.CompletedTask;
        });
        Check.Bytes(new byte[] { 0x07 }, model, "the late version bytes were discarded during the quiet period, not taken as the model");
        Check.Equal(3, channel.UnsolicitedBytes, "the late bytes were counted as unsolicited");
    }

    [TestMethod]
    public async Task DeviceError_ReturnsNull_WithoutWaitingForTheQuietPeriod()
    {
        var channel = new NexStarCommandChannel();
        var slew = NexStarProtocol.FormatFixedSlewCommand(SlewAxis.RaAzm, SlewDirection.Positive, SlewRate.Rate1);
        var result = await channel.ExchangeAsync(slew, _ =>
        {
            channel.OnBytes(new byte[] { 0xFF, 0x23 }); // "no device" form
            return Task.CompletedTask;
        });
        Check.That(result == null, "a device error is a failure");
        var sw = Stopwatch.StartNew();
        var echo = await channel.ExchangeAsync(new byte[] { (byte)'K', (byte)'A' }, _ =>
        {
            channel.OnBytes(new byte[] { (byte)'A', 0x23 });
            return Task.CompletedTask;
        });
        Check.Bytes(new byte[] { (byte)'A' }, echo, "echo");
        Check.That(sw.ElapsedMilliseconds < NexStarCommandChannel.ResyncQuietMs, $"the line was clean, so no quiet wait ({sw.ElapsedMilliseconds} ms)");
    }

    [TestMethod]
    public async Task ConcurrentCommands_AreSerialised_AndEachGetsItsOwnResponse()
    {
        var channel = new NexStarCommandChannel();
        var inFlight = 0;
        var maxInFlight = 0;
        async Task<byte[]?> Run(string command, byte[] response)
        {
            return await channel.ExchangeAsync(Ascii(command), async _ =>
            {
                var now = Interlocked.Increment(ref inFlight);
                maxInFlight = Math.Max(maxInFlight, now);
                await Task.Delay(15);
                channel.OnBytes(response);
                Interlocked.Decrement(ref inFlight);
            });
        }
        var tasks = new List<Task<byte[]?>>();
        for (int i = 0; i < 20; i++)
        {
            tasks.Add(Run("m", new byte[] { (byte)i, 0x23 }));
            tasks.Add(Run("J", new byte[] { (byte)(100 + i), 0x23 }));
        }
        var results = await Task.WhenAll(tasks);
        Check.Equal(1, maxInFlight, "only one command is ever on the line");
        for (int i = 0; i < 20; i++)
        {
            Check.Bytes(new byte[] { (byte)i }, results[2 * i], $"m #{i}");
            Check.Bytes(new byte[] { (byte)(100 + i) }, results[2 * i + 1], $"J #{i}");
        }
    }

    [TestMethod]
    public async Task WriteFailure_Propagates_AndTheChannelStaysUsable()
    {
        var channel = new NexStarCommandChannel();
        var threw = false;
        try
        {
            await channel.ExchangeAsync(Ascii("V"), _ => throw new IOException("unplugged"));
        }
        catch (IOException) { threw = true; }
        Check.That(threw, "the transport's failure reaches the caller");
        var echo = await channel.ExchangeAsync(new byte[] { (byte)'K', (byte)'Z' }, _ =>
        {
            channel.OnBytes(new byte[] { (byte)'Z', 0x23 });
            return Task.CompletedTask;
        });
        Check.Bytes(new byte[] { (byte)'Z' }, echo, "the limiter was released");
    }
}
