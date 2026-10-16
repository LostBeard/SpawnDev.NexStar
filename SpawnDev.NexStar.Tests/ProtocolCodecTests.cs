using SpawnDev.UnitTesting;
using System.Text;

namespace SpawnDev.NexStar.Tests;

/// <summary>
/// Position, slew, location and time encoding. Reference values come from the Celestron NexStar Communication
/// Protocol document's own worked examples and from bytes captured off a real 127SLT.
/// </summary>
public class ProtocolCodecTests
{
    private const double Unit16 = 360.0 / 65536.0;
    private const double Unit32 = 360.0 / 4294967296.0;

    [TestMethod]
    public void Position_DocumentExamples()
    {
        // The document's example "34AB,12CE": the declination 12CE is 4814 decimal, "4814/65536 = 0.07346 ... 26.4441 degrees"
        Check.That(NexStarProtocol.TryNexToDecimalDegrees("12CE", out var a), "parse 12CE");
        Check.Near(26.4441, a, 0.0001, "12CE (4814 decimal) is 26.4441 degrees");
        // "313197824/4294967296 = 0.0729 or 26.252 degrees"
        Check.That(NexStarProtocol.TryPreciseNexToDecimalDegrees("12AB0500", out var b), "parse 12AB0500");
        Check.Near(26.252, b, 0.001, "12AB0500 is 26.252 degrees");
    }

    [TestMethod]
    public void PreciseFormat_DividesByTwoToTheThirtyTwo()
    {
        // Half a revolution is exactly 180 degrees. Dividing by 0xFFFFFFFF instead gives 180.0000000419.
        Check.That(NexStarProtocol.TryPreciseNexToDecimalDegrees("80000000", out var half), "parse");
        Check.Equal(180.0, half, "80000000 is exactly 180");
        Check.That(NexStarProtocol.TryPreciseNexToDecimalDegrees("00000000", out var zero), "parse 0");
        Check.Equal(0.0, zero, "zero");
        Check.That(NexStarProtocol.TryPreciseNexToDecimalDegrees("FFFFFFFF", out var max), "parse max");
        Check.That(max < 360.0, "the largest value is just under a full turn, never 360");
    }

    [TestMethod]
    public void Position_RealMountResponses()
    {
        // Captured from the mount: unaligned, azimuth relative to power-on, altitude 0.
        Check.That(NexStarProtocol.TryParsePositionResponse(Encoding.ASCII.GetBytes("571E,0000"), out var az, out var alt), "Z");
        Check.Near(0x571E / 65536.0 * 360.0, az, 1e-9, "azimuth is 0x571E of a turn");
        Check.Near(122.5085, az, 0.0005, "which is 122.5085 degrees");
        Check.Equal(0.0, alt, "altitude");
        Check.That(NexStarProtocol.TryParsePositionResponse(Encoding.ASCII.GetBytes("571DC700,00000000"), out var azP, out var altP), "z");
        Check.Near(az, azP, Unit16, "precise and standard agree");
        Check.Equal(0.0, altP, "precise altitude");
    }

    [TestMethod]
    public void Position_NegativeSecondAxis_IsMinusAngle()
    {
        // The hand control sends negative declination/altitude as 360 minus the angle: -45 degrees is E000.
        Check.That(NexStarProtocol.TryParsePositionResponse("34AB,E000", out _, out var dec), "parse");
        Check.Near(-45.0, dec, 1e-9, "E000 is -45");
        Check.That(NexStarProtocol.TryParsePositionResponse("34AB,2000", out _, out var up), "parse +45");
        Check.Near(45.0, up, 1e-9, "2000 is +45");
    }

    [TestMethod]
    public void Position_GarbageIsAFailure_NotZeroZero()
    {
        foreach (var bad in new[] { "", "#", "571E", "571E,", ",0000", "571E,00000", "571DC700,0000", "ZZZZ,0000", "571E,0000,1234", "571E 0000", "+71E,0000", "-71E,0000" })
        {
            Check.That(!NexStarProtocol.TryParsePositionResponse(bad, out var x, out var y), $"'{bad}' must be rejected");
            Check.That(x == 0 && y == 0, $"'{bad}' leaves outputs at zero");
        }
        Check.That(!NexStarProtocol.TryParsePositionResponse((byte[]?)null, out _, out _), "null payload");
    }

    [TestMethod]
    public void AngleFormat_RoundTripsAndWraps()
    {
        // Hostile angles: wrap-around, just under a turn, negative, huge, tiny.
        var angles = new[] { 0.0, 1e-9, 90.0, 123.456, 179.99999, 180.0, 270.0, 359.99999999, 360.0, 360.00001, 720.5, -0.00001, -90.0, -359.9999, -721.25, 1e7 + 0.3 };
        foreach (var angle in angles)
        {
            var h16 = NexStarProtocol.DecimalDegreesToNex(angle);
            var h32 = NexStarProtocol.DecimalDegreesToPreciseNex(angle);
            Check.Equal(4, h16.Length, $"16-bit text of {angle} is 4 digits, got {h16}");
            Check.Equal(8, h32.Length, $"32-bit text of {angle} is 8 digits, got {h32}");
            var expected = angle - 360.0 * Math.Floor(angle / 360.0);
            Check.That(NexStarProtocol.TryNexToDecimalDegrees(h16, out var back16), "parse 16");
            Check.That(NexStarProtocol.TryPreciseNexToDecimalDegrees(h32, out var back32), "parse 32");
            Check.That(Circular(expected, back16) <= Unit16 / 2 + 1e-9, $"{angle} -> {h16} -> {back16}");
            Check.That(Circular(expected, back32) <= Unit32 / 2 + 1e-6, $"{angle} -> {h32} -> {back32}");
        }
        foreach (var bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var threw = false;
            try { NexStarProtocol.DecimalDegreesToNex(bad); } catch (ArgumentOutOfRangeException) { threw = true; }
            Check.That(threw, $"{bad} must not become a position");
        }
    }

    private static double Circular(double a, double b)
    {
        var d = Math.Abs(a - b) % 360.0;
        return Math.Min(d, 360.0 - d);
    }

    [TestMethod]
    public void GotoCommands_AreFormattedAsDocumented()
    {
        Check.Bytes(Encoding.ASCII.GetBytes("R4000,2000"), NexStarProtocol.FormatGotoRaDecCommand(90, 45, false), "GOTO RA/DEC");
        Check.Bytes(Encoding.ASCII.GetBytes("r40000000,20000000"), NexStarProtocol.FormatGotoRaDecCommand(90, 45, true), "GOTO precise RA/DEC");
        Check.Bytes(Encoding.ASCII.GetBytes("B4000,E000"), NexStarProtocol.FormatGotoAzAltCommand(90, -45, false), "GOTO AZM-ALT with negative altitude");
        Check.Bytes(Encoding.ASCII.GetBytes("b40000000,E0000000"), NexStarProtocol.FormatGotoAzAltCommand(90, -45, true), "GOTO precise AZM-ALT");
        Check.Bytes(Encoding.ASCII.GetBytes("S4000,2000"), NexStarProtocol.FormatSyncRaDecCommand(90, 45, false), "Sync");
        Check.Bytes(Encoding.ASCII.GetBytes("s40000000,20000000"), NexStarProtocol.FormatSyncRaDecCommand(90, 45, true), "Sync precise");
    }

    [TestMethod]
    public void VariableSlew_DocumentExample_AndClamp()
    {
        // Document: 150 arcsec/s -> (150*4)\256 = 2 high, (150*4) mod 256 = 88 low.
        Check.Bytes(new byte[] { (byte)'P', 3, 16, 6, 2, 88, 0, 0 }, NexStarProtocol.FormatVariableSlewCommand(SlewAxis.RaAzm, SlewDirection.Positive, 150), "azimuth positive");
        Check.Bytes(new byte[] { (byte)'P', 3, 17, 7, 2, 88, 0, 0 }, NexStarProtocol.FormatVariableSlewCommand(SlewAxis.DecAlt, SlewDirection.Negative, 150), "altitude negative");
        // Two bytes carry rate*4: anything above 16383.75 used to wrap around into a slow slew.
        Check.Bytes(new byte[] { (byte)'P', 3, 16, 6, 255, 255, 0, 0 }, NexStarProtocol.FormatVariableSlewCommand(SlewAxis.RaAzm, SlewDirection.Positive, 20000), "clamped to the maximum");
        Check.Bytes(new byte[] { (byte)'P', 3, 16, 6, 255, 255, 0, 0 }, NexStarProtocol.FormatVariableSlewCommand(SlewAxis.RaAzm, SlewDirection.Positive, double.PositiveInfinity), "infinity clamps");
        Check.Bytes(new byte[] { (byte)'P', 3, 16, 6, 0, 0, 0, 0 }, NexStarProtocol.FormatVariableSlewCommand(SlewAxis.RaAzm, SlewDirection.Positive, -5), "negative rate is zero");
        var threw = false;
        try { NexStarProtocol.FormatVariableSlewCommand(SlewAxis.RaAzm, SlewDirection.Positive, double.NaN); } catch (ArgumentOutOfRangeException) { threw = true; }
        Check.That(threw, "NaN rate is refused");
    }

    [TestMethod]
    public void FixedSlew_DocumentFormat()
    {
        // P 2 16 36 rate 0 0 0: azimuth positive; 37 is negative.
        Check.Bytes(new byte[] { (byte)'P', 2, 16, 36, 9, 0, 0, 0 }, NexStarProtocol.FormatFixedSlewCommand(SlewAxis.RaAzm, SlewDirection.Positive, SlewRate.Rate9), "azimuth positive rate 9");
        Check.Bytes(new byte[] { (byte)'P', 2, 17, 37, 0, 0, 0, 0 }, NexStarProtocol.FormatFixedSlewCommand(SlewAxis.DecAlt, SlewDirection.Negative, SlewRate.Stop), "altitude negative stop");
        var threw = false;
        try { NexStarProtocol.FormatFixedSlewCommand(SlewAxis.RaAzm, SlewDirection.Positive, (SlewRate)10); } catch (ArgumentOutOfRangeException) { threw = true; }
        Check.That(threw, "rate 10 is not a rate");
    }

    [TestMethod]
    public void Location_DocumentExample()
    {
        // Document: 118 20' 17" W, 33 50' 41" N is sent as W 33 50 41 0 118 20 17 1.
        var lat = NexStarProtocol.DMSToDecimal(33, 50, 41, 0);
        var lon = NexStarProtocol.DMSToDecimal(118, 20, 17, 1);
        Check.Bytes(new byte[] { (byte)'W', 33, 50, 41, 0, 118, 20, 17, 1 }, NexStarProtocol.FormatSetLocationCommand(lat, lon), "set location");
        Check.That(NexStarProtocol.TryParseLocationResponse(new byte[] { 33, 50, 41, 0, 118, 20, 17, 1 }, out var loc), "parse");
        Check.Near(lat, loc.Latitude, 1e-9, "latitude");
        Check.Near(lon, loc.Longitude, 1e-9, "longitude");
    }

    [TestMethod]
    public void Location_RoundsToTheNearestSecond_AndNeverCarriesSixty()
    {
        // Truncation made 33.8447222 (33 50' 41.0") come out a second short, and a value just under a minute
        // must carry, not become 59.9999 -> 59 or 60.
        foreach (var value in new[] { 0.0, 33.844722, 35.0, 35.5, 10.5917, 89.99999, 179.9999999, 0.9999999, 12.0166666 })
        {
            var (deg, min, sec, sign) = NexStarProtocol.DecimalToDMS(value);
            Check.That(min < 60 && sec < 60, $"{value} -> {deg} {min} {sec}");
            Check.Near(value, NexStarProtocol.DMSToDecimal(deg, min, sec, sign), 0.5 / 3600 + 1e-9, $"{value} within half a second");
            Check.Equal(0, (int)sign, "positive stays positive");
        }
        Check.Equal(1, (int)NexStarProtocol.DecimalToDMS(-0.5).sign, "negative sets the sign byte");
        foreach (var bad in new[] { 90.0001, -91.0, double.NaN })
        {
            var threw = false;
            try { NexStarProtocol.FormatSetLocationCommand(bad, 0); } catch (ArgumentOutOfRangeException) { threw = true; }
            Check.That(threw, $"latitude {bad} is refused");
        }
        var threwLon = false;
        try { NexStarProtocol.FormatSetLocationCommand(0, 180.1); } catch (ArgumentOutOfRangeException) { threwLon = true; }
        Check.That(threwLon, "longitude 180.1 is refused");
    }

    [TestMethod]
    public void Location_SignBytes_ParseAsWestNorth()
    {
        // The mount's own form: sign bytes are 0 (north, east) and 1 (south, west). A public place, 40 44' 55" N 73 59' 09" W;
        // the minute and second bytes include 44 and 9 so a stray 0x0D or 0x23 in the data is not special.
        Check.That(NexStarProtocol.TryParseLocationResponse(new byte[] { 40, 44, 55, 0, 73, 59, 9, 1 }, out var loc), "parse");
        Check.That(loc.Latitude > 0, "north is positive");
        Check.That(loc.Longitude < 0, "west is negative");
        Check.Near(40 + 44 / 60.0 + 55 / 3600.0, loc.Latitude, 1e-9, "40 44' 55\"");
        Check.Near(-(73 + 59 / 60.0 + 9 / 3600.0), loc.Longitude, 1e-9, "73 59' 09\"");
        // 35 as a field (0x23 = '#'): latitude 35 deg, minute 35
        Check.That(NexStarProtocol.TryParseLocationResponse(new byte[] { 35, 35, 35, 1, 35, 35, 35, 0 }, out var hash), "parse with 35s");
        Check.Near(-(35 + 35 / 60.0 + 35 / 3600.0), hash.Latitude, 1e-9, "south 35 35' 35\"");
        Check.Near(35 + 35 / 60.0 + 35 / 3600.0, hash.Longitude, 1e-9, "east 35 35' 35\"");
    }

    [TestMethod]
    public void Location_InvalidPayloadsAreRejected()
    {
        Check.That(!NexStarProtocol.TryParseLocationResponse(null, out _), "null");
        Check.That(!NexStarProtocol.TryParseLocationResponse(new byte[7], out _), "short");
        Check.That(!NexStarProtocol.TryParseLocationResponse(new byte[9], out _), "long");
        Check.That(!NexStarProtocol.TryParseLocationResponse(new byte[] { 91, 0, 0, 0, 0, 0, 0, 0 }, out _), "latitude 91");
        Check.That(!NexStarProtocol.TryParseLocationResponse(new byte[] { 0, 60, 0, 0, 0, 0, 0, 0 }, out _), "minute 60");
        Check.That(!NexStarProtocol.TryParseLocationResponse(new byte[] { 0, 0, 0, 2, 0, 0, 0, 0 }, out _), "sign 2");
        Check.That(!NexStarProtocol.TryParseLocationResponse(new byte[] { 0, 0, 0, 0, 181, 0, 0, 0 }, out _), "longitude 181");
    }
}
