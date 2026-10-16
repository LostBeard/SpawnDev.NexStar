using SpawnDev.UnitTesting;

namespace SpawnDev.NexStar.Tests;

/// <summary>
/// Cutting the hand control's byte stream into responses. The bytes here are what the real mount sends
/// (captured from a 127SLT, hand control 5.35): its version minor is 35 = 0x23 = '#', and its clock sends
/// 0x23 for second 35, so "a response ends at the first '#'" is wrong.
/// </summary>
public class ProtocolFramingTests
{
    private static NexStarFrameStatus Feed(NexStarResponseAssembler a, out byte[]? payload, params byte[] bytes)
        => a.Append(bytes, out payload);

    [TestMethod]
    public void Version_WithHashInMinor_IsNotCutShort()
    {
        var a = new NexStarResponseAssembler();
        a.Begin(NexStarProtocol.ExpectedResponseLength(new[] { (byte)'V' }));
        // Real response: 05 23 23. The old rule completed here, after the minor byte, leaving the real '#' behind.
        Check.Equal(NexStarFrameStatus.Incomplete, Feed(a, out _, 0x05, 0x23), "05 23 is not a whole response");
        Check.Equal(NexStarFrameStatus.Complete, Feed(a, out var payload, 0x23), "the trailing # completes it");
        Check.Bytes(new byte[] { 0x05, 0x23 }, payload, "version payload");
        Check.Equal(0, a.DiscardedBytes, "nothing left over to poison the next command");
    }

    [TestMethod]
    public void Time_WithHashInSeconds_IsNotCutShort()
    {
        // Real response at second 35: 0B 04 23 09 18 1A FC 01 23
        var a = new NexStarResponseAssembler();
        a.Begin(8);
        Check.Equal(NexStarFrameStatus.Incomplete, Feed(a, out _, 0x0B, 0x04, 0x23), "first three bytes");
        Check.Equal(NexStarFrameStatus.Incomplete, Feed(a, out _, 0x09, 0x18, 0x1A, 0xFC, 0x01), "payload complete but no terminator yet");
        Check.Equal(NexStarFrameStatus.Complete, Feed(a, out var payload, 0x23), "terminator");
        Check.Bytes(new byte[] { 0x0B, 0x04, 0x23, 0x09, 0x18, 0x1A, 0xFC, 0x01 }, payload, "time payload");
    }

    [TestMethod]
    public void EveryByteValue_InAPayload_FramesCorrectly()
    {
        // Location is 8 free bytes; run every value through every position, one byte per chunk (worst-case arrival).
        for (int position = 0; position < 8; position++)
        {
            for (int value = 0; value < 256; value++)
            {
                var payload = new byte[] { 42, 32, 13, 0, 76, 40, 13, 1 };
                payload[position] = (byte)value;
                var a = new NexStarResponseAssembler();
                a.Begin(8);
                byte[]? got = null;
                var status = NexStarFrameStatus.Incomplete;
                foreach (var b in payload.Append((byte)'#'))
                {
                    status = Feed(a, out got, b);
                }
                Check.Equal(NexStarFrameStatus.Complete, status, $"byte {value:X2} at {position}");
                Check.Bytes(payload, got, $"byte {value:X2} at {position}");
            }
        }
    }

    [TestMethod]
    public void PlainAcknowledgement_IsAnEmptyPayload()
    {
        var a = new NexStarResponseAssembler();
        a.Begin(NexStarProtocol.ExpectedResponseLength(new byte[] { (byte)'M' }));
        Check.Equal(NexStarFrameStatus.Complete, Feed(a, out var payload, 0x23), "just #");
        Check.Equal(0, payload!.Length, "empty payload");
    }

    [TestMethod]
    public void PassThroughWithNoAnswer_IsADeviceError_AndTheTerminatorIsConsumed()
    {
        // Protocol document: a P command that gets no answer returns one extra byte before the '#'.
        // Slew command: P 2 16 36 rate 0 0 0 -> expects just '#'.
        var slew = NexStarProtocol.FormatFixedSlewCommand(SlewAxis.RaAzm, SlewDirection.Positive, SlewRate.Rate5);
        var a = new NexStarResponseAssembler();
        a.Begin(NexStarProtocol.ExpectedResponseLength(slew));
        Check.Equal(NexStarFrameStatus.Incomplete, Feed(a, out _, 0x00), "a byte that is not '#' where '#' belongs waits for the terminator");
        Check.Equal(NexStarFrameStatus.DeviceError, Feed(a, out var payload, 0x23), "then the '#'");
        Check.That(payload == null, "no payload for a device error");
        Check.Equal(0, a.DiscardedBytes, "the extra byte and the '#' were consumed, none left to poison the next command");
    }

    [TestMethod]
    public void NoHashWhereOneBelongs_IsMalformed()
    {
        var a = new NexStarResponseAssembler();
        a.Begin(2);
        Check.Equal(NexStarFrameStatus.Incomplete, Feed(a, out _, 0x05, 0x23, 0x41), "three bytes, third is not #");
        Check.Equal(NexStarFrameStatus.Malformed, Feed(a, out _, 0x42), "fourth is not # either");
    }

    [TestMethod]
    public void UnknownCommand_EndsAtTheFirstHash()
    {
        var a = new NexStarResponseAssembler();
        a.Begin(NexStarProtocol.ExpectedResponseLength(new[] { (byte)'?' }));
        Check.Equal(NexStarFrameStatus.Complete, Feed(a, out var payload, 0x31, 0x32, 0x23, 0x99), "ends at first #");
        Check.Bytes(new byte[] { 0x31, 0x32 }, payload, "payload before the #");
        Check.Equal(1, a.DiscardedBytes, "trailing byte counted as discarded");
    }

    [TestMethod]
    public void ExpectedLengths_MatchTheProtocolDocument()
    {
        // Response formats from the Celestron NexStar Communication Protocol (payload bytes before the '#').
        int Len(string c) => NexStarProtocol.ExpectedResponseLength(System.Text.Encoding.ASCII.GetBytes(c));
        Check.Equal("34AB,12CE".Length, Len("E"), "E");
        Check.Equal("12AB,4000".Length, Len("Z"), "Z");
        Check.Equal("34AB0500,12CE0500".Length, Len("e"), "e");
        Check.Equal("12AB0500,40000500".Length, Len("z"), "z");
        Check.Equal(2, Len("V"), "V");
        Check.Equal(1, Len("m"), "m");
        Check.Equal(1, Len("J"), "J");
        Check.Equal(1, Len("t"), "t");
        Check.Equal(1, Len("K"), "K");
        Check.Equal(1, Len("L"), "L");
        Check.Equal(8, Len("w"), "w");
        Check.Equal(8, Len("h"), "h");
        foreach (var c in "RrBbSsWHTM") Check.Equal(0, Len(c.ToString()), $"{c} is acknowledged with just #");
        Check.Equal(-1, NexStarProtocol.ExpectedResponseLength(Array.Empty<byte>()), "empty command");
        // P: the last byte of the command is the response length
        Check.Equal(2, NexStarProtocol.ExpectedResponseLength(NexStarProtocol.FormatPassThroughCommand(1, 16, 254, 0, 0, 0, 2)), "P device version");
        Check.Equal(0, NexStarProtocol.ExpectedResponseLength(NexStarProtocol.FormatVariableSlewCommand(SlewAxis.RaAzm, SlewDirection.Positive, 100)), "P variable slew");
    }
}
