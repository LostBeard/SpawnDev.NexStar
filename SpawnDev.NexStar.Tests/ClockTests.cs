using SpawnDev.UnitTesting;

namespace SpawnDev.NexStar.Tests;

/// <summary>
/// The hand control's clock: local time, the zone's STANDARD offset, and a daylight saving flag.
/// The old app sent the summer offset (-4) with the DST flag set, so the mount's idea of UTC was an hour
/// off. The protocol document's example sets 3:26 PM on April 6 (daylight time) in Eastern as offset -5, DST 1.
/// </summary>
public class ClockTests
{
    private static TimeZoneInfo Zone(string iana) => TimeZoneInfo.FindSystemTimeZoneById(iana);

    [TestMethod]
    public void SetTime_DocumentExample()
    {
        // H 15 26 0 4 6 5 251 1
        var cmd = NexStarProtocol.FormatSetTimeCommand(new DateTime(2005, 4, 6, 15, 26, 0), -5, true);
        Check.Bytes(new byte[] { (byte)'H', 15, 26, 0, 4, 6, 5, 251, 1 }, cmd, "protocol document example");
    }

    [TestMethod]
    public void SetTime_RejectsWhatCannotBeSent()
    {
        foreach (var (year, offset) in new[] { (1999, 0), (2256, 0), (2026, -13), (2026, 15) })
        {
            var threw = false;
            try { NexStarProtocol.FormatSetTimeCommand(new DateTime(year, 1, 1), offset, false); } catch (ArgumentOutOfRangeException) { threw = true; }
            Check.That(threw, $"year {year} offset {offset} is refused");
        }
    }

    [TestMethod]
    public void GetTime_RealMountResponse()
    {
        // Captured: 0B 04 23 09 18 1A FC 01 -> 11:04:35 on 2026-09-24, standard offset -4 (as the old app wrote it), DST on.
        Check.That(NexStarProtocol.TryParseTimeResponse(new byte[] { 0x0B, 0x04, 0x23, 0x09, 0x18, 0x1A, 0xFC, 0x01 }, out var t), "parse");
        Check.Equal(new DateTime(2026, 9, 24, 11, 4, 35), t.LocalTime, "local time");
        Check.Equal(-4, t.TimezoneOffset, "offset is signed");
        Check.That(t.DaylightSaving, "DST");
        Check.Equal(new DateTime(2026, 9, 24, 14, 4, 35), t.ToUtc(), "what the hand control takes as UTC: local - (-4) - 1h");
    }

    [TestMethod]
    public void GetTime_PositiveZonesAbove12_AreNotNegated()
    {
        // The old parser made every offset above 12 negative, so +13 (Tonga) and +14 (Kiribati) came back as -243/-242.
        foreach (var zone in new[] { 13, 14, 12, 0, -12 })
        {
            var bytes = new byte[] { 12, 0, 0, 1, 1, 26, unchecked((byte)zone), 0 };
            Check.That(NexStarProtocol.TryParseTimeResponse(bytes, out var t), $"parse zone {zone}");
            Check.Equal(zone, t.TimezoneOffset, $"zone {zone}");
        }
    }

    [TestMethod]
    public void GetTime_InvalidPayloadsAreRejected()
    {
        Check.That(!NexStarProtocol.TryParseTimeResponse(null, out _), "null");
        Check.That(!NexStarProtocol.TryParseTimeResponse(new byte[7], out _), "short");
        Check.That(!NexStarProtocol.TryParseTimeResponse(new byte[] { 12, 0, 0, 0, 1, 26, 0, 0 }, out _), "month 0");
        Check.That(!NexStarProtocol.TryParseTimeResponse(new byte[] { 12, 0, 0, 2, 30, 26, 0, 0 }, out _), "February 30");
        Check.That(!NexStarProtocol.TryParseTimeResponse(new byte[] { 25, 0, 0, 1, 1, 26, 0, 0 }, out _), "hour 25");
        Check.That(!NexStarProtocol.TryParseTimeResponse(new byte[] { 12, 0, 0, 1, 1, 26, 0, 2 }, out _), "DST flag 2");
    }

    [TestMethod]
    public void ComputeClock_EasternSummerAndWinter()
    {
        var eastern = Zone("America/New_York");
        var summer = new DateTime(2026, 7, 1, 14, 0, 0, DateTimeKind.Utc);
        var s = NexStarProtocol.ComputeClock(summer, eastern);
        Check.Equal(-5, s.TimezoneOffset, "summer sends the STANDARD offset -5, not -4");
        Check.That(s.DaylightSaving, "summer DST flag");
        Check.Equal(new DateTime(2026, 7, 1, 10, 0, 0), s.LocalTime, "10:00 local EDT");
        Check.Equal(summer, s.ToUtc(), "the hand control's UTC is the real UTC");

        var winter = new DateTime(2026, 1, 15, 14, 0, 0, DateTimeKind.Utc);
        var w = NexStarProtocol.ComputeClock(winter, eastern);
        Check.Equal(-5, w.TimezoneOffset, "winter standard offset");
        Check.That(!w.DaylightSaving, "winter no DST");
        Check.Equal(new DateTime(2026, 1, 15, 9, 0, 0), w.LocalTime, "09:00 local EST");
        Check.Equal(winter, w.ToUtc(), "winter UTC");
    }

    [TestMethod]
    public void ComputeClock_TheOldBehaviour_WasAnHourOff()
    {
        // What the old SyncTimeAsync sent: the summer offset AND the DST flag.
        var summer = new DateTime(2026, 7, 1, 14, 0, 0, DateTimeKind.Utc);
        var zone = Zone("America/New_York");
        var local = TimeZoneInfo.ConvertTimeFromUtc(summer, zone);
        var oldWay = new TelescopeTime(local, (int)zone.GetUtcOffset(summer).TotalHours, zone.IsDaylightSavingTime(summer));
        Check.Equal(summer.AddHours(-1), oldWay.ToUtc(), "the old pair makes the hand control's UTC an hour EARLY (10:00 local + 4 - 1 = 13:00, not 14:00)");
    }

    [TestMethod]
    public void ComputeClock_UtcStaysExact_InEveryKindOfZone()
    {
        // Half-hour and 45-minute zones and southern DST: the hand control takes whole hours only, so the clock it shows
        // may differ from the wall clock, but its UTC (which sidereal time uses) must be exact.
        var instants = new[]
        {
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 1, 6, 30, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 15, 23, 59, 59, DateTimeKind.Utc),
            new DateTime(2026, 10, 25, 1, 30, 0, DateTimeKind.Utc),
            new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc),
        };
        foreach (var id in new[] { "America/New_York", "Asia/Kolkata", "America/St_Johns", "Asia/Kathmandu", "Australia/Sydney", "Pacific/Auckland", "Pacific/Kiritimati", "Pacific/Pago_Pago", "Europe/London", "UTC" })
        {
            var zone = Zone(id);
            foreach (var utc in instants)
            {
                var clock = NexStarProtocol.ComputeClock(utc, zone);
                Check.Equal(utc, clock.ToUtc(), $"{id} at {utc:u}");
                Check.That(clock.TimezoneOffset >= -12 && clock.TimezoneOffset <= 14, $"{id} offset {clock.TimezoneOffset} can be sent");
                // and the bytes can actually be built
                NexStarProtocol.FormatSetTimeCommand(clock.LocalTime, clock.TimezoneOffset, clock.DaylightSaving);
            }
        }
    }
}
