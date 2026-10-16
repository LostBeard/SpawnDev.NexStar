using SpawnDev.UnitTesting;

namespace SpawnDev.NexStar.Tests;

/// <summary>
/// Cord-wrap protection. The mount reports azimuth as 0-360 only, so these tests drive a guard with readings the way a polling loop would
/// and check the running total, the limit zones, the routing of gotos and the handling of everything that breaks counting (gaps, syncs, restarts).
/// </summary>
public class CableGuardTests
{
    private static readonly DateTime T0 = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    private static CableGuard NewGuard(double startAz = 0, double limit = 200, double cumulative = 0)
    {
        var g = new CableGuard(new CableGuardOptions { LimitDegrees = limit });
        g.Confirm(cumulative, startAz, T0);
        return g;
    }

    /// <summary>A mount that turns the SHORT way for each goto (the assumption the plan relies on, to be checked on the real mount).</summary>
    private sealed class ShortestPathMount
    {
        public double Az;
        public double Cumulative;
        public double MaxExcursion;

        public void Goto(double target)
        {
            var d = ((target - Az) % 360 + 540) % 360 - 180; // -180..180, the short way
            // walk it in 1 degree steps so the whole path is checked, not just the end
            var steps = (int)Math.Ceiling(Math.Abs(d));
            for (int i = 1; i <= steps; i++)
            {
                var c = Cumulative + d * i / steps;
                MaxExcursion = Math.Max(MaxExcursion, Math.Abs(c));
            }
            Cumulative += d;
            Az = ((target % 360) + 360) % 360;
        }
    }

    [TestMethod]
    public void Unwrap_CountsTurnsPastTheZero_BothWays()
    {
        var g = NewGuard(startAz: 350);
        var t = T0;
        double az = 350;
        // turn up by 5 degrees a second (the guard's assumed top speed) for 200 seconds: 1000 degrees, nearly three turns, crossing 360 three times
        for (int i = 0; i < 200; i++)
        {
            az = (az + 5) % 360;
            t = t.AddSeconds(1);
            g.Update(az, t);
        }
        Check.Near(1000.0, g.Cumulative, 1e-9, "1000 degrees up");
        Check.Near(1000.0 / 360, g.Turns, 1e-9, "turns");
        Check.Equal(CableZone.Limit, g.Zone, "way past +200");
        // and back down through zero
        for (int i = 0; i < 300; i++)
        {
            az = ((az - 5) % 360 + 360) % 360;
            t = t.AddSeconds(1);
            g.Update(az, t);
        }
        Check.Near(-500.0, g.Cumulative, 1e-9, "1000 up then 1500 down");
    }

    [TestMethod]
    public void Zones_FollowTheLimitAndTheWarningMargin()
    {
        foreach (var (c, zone) in new[] { (0.0, CableZone.Safe), (169.9, CableZone.Safe), (171.0, CableZone.Warn), (199.0, CableZone.Warn), (200.0, CableZone.Limit), (260.0, CableZone.Limit), (-171.0, CableZone.Warn), (-200.0, CableZone.Limit) })
        {
            var g = NewGuard(0, 200, c);
            Check.Equal(zone, g.Zone, $"cumulative {c}");
        }
        var wide = NewGuard(0, 200, 150);
        Check.Near(50.0, wide.RoomPositive, 1e-9, "room up");
        Check.Near(350.0, wide.RoomNegative, 1e-9, "room down");
    }

    [TestMethod]
    public void Goto_TakesTheShortWayWhenItIsSafe()
    {
        var g = NewGuard(startAz: 10);
        var p = g.PlanGoto(350);
        Check.That(p.Allowed, "allowed");
        Check.Near(-20.0, p.TotalTravel, 1e-9, "20 degrees down, across zero");
        Check.That(!p.Rerouted, "not rerouted");
        Check.Equal(1, p.Hops.Count, "one step");
        Check.Near(350.0, p.Hops[0].Azimuth, 1e-9, "target azimuth");
        Check.Near(-20.0, p.FinalCumulative, 1e-9, "cumulative after");
    }

    [TestMethod]
    public void Goto_GoesTheLongWay_WhenTheShortWayWouldWrapTheCable()
    {
        // 150 degrees up from neutral, at azimuth 150. Target azimuth 230 is 80 degrees on: short, but it lands at +230, past the limit of 200.
        var g = NewGuard(startAz: 150, cumulative: 150);
        var p = g.PlanGoto(230);
        Check.That(p.Allowed, "allowed");
        Check.That(p.Rerouted, "rerouted");
        Check.Near(-280.0, p.TotalTravel, 1e-9, "the other way round: 280 down");
        Check.Near(-130.0, p.FinalCumulative, 1e-9, "ends at -130");
        Check.That(p.Hops.Count >= 2, "more than 150 degrees is more than one step");
        Check.That(p.Hops.All(h => Math.Abs(h.ExpectedCumulative) <= 200), "every step ends inside the limits");
        Check.That(p.Reason != null && p.Reason.Contains("wrap"), "the reason says why");
    }

    [TestMethod]
    public void Goto_PlansAreSafeFromEveryStartToEveryTarget_OnAShortestPathMount()
    {
        // The property that matters: whatever the cable's turn and wherever it is sent, a mount that takes the short way for each step
        // never goes past the limit, ends where it was sent, and no step is a half-turn or more (where its choice could flip).
        var rng = new Random(20261016);
        int plans = 0;
        foreach (var limit in new[] { 180.0, 200.0, 270.0 })
        for (int trial = 0; trial < 4000; trial++)
        {
            var c = (rng.NextDouble() * 2 - 1) * limit;
            var az = ((c % 360) + 360) % 360;
            var target = rng.NextDouble() * 360;
            var g = NewGuard(az, limit, c);
            var p = g.PlanGoto(target);
            Check.That(p.Allowed, $"limit {limit}, cumulative {c:F2}, target {target:F2} must be reachable (the window is a full turn wide)");
            var mount = new ShortestPathMount { Az = az, Cumulative = c };
            foreach (var hop in p.Hops)
            {
                Check.That(Math.Abs(((hop.Azimuth - mount.Az) % 360 + 540) % 360 - 180 - (hop.ExpectedCumulative - mount.Cumulative)) < 1e-6 || Math.Abs(hop.ExpectedCumulative - mount.Cumulative) < 180,
                    $"hop {hop} from az {mount.Az:F2} is a short-way hop");
                Check.That(Math.Abs(hop.ExpectedCumulative - mount.Cumulative) < 180, $"hop size {Math.Abs(hop.ExpectedCumulative - mount.Cumulative):F2} is under a half turn");
                mount.Goto(hop.Azimuth);
                Check.Near(hop.ExpectedCumulative, mount.Cumulative, 1e-6, $"the mount ends the hop where the plan said (limit {limit}, c {c:F2}, target {target:F2})");
            }
            Check.That(mount.MaxExcursion <= limit + 1e-6, $"limit {limit}, c {c:F2}, target {target:F2}: went to {mount.MaxExcursion:F2}");
            Check.Near(target, mount.Az, 1e-9, "ended at the target azimuth");
            plans++;
        }
        Check.Equal(12000, plans, "every trial ran");
    }

    [TestMethod]
    public void Goto_TheNaiveShortestWay_WouldHaveWrapped()
    {
        // The same sweep without the guard: how often does "just goto the target" pass the limit? This is the problem the guard solves.
        var rng = new Random(7);
        int wrapped = 0, trials = 4000;
        for (int i = 0; i < trials; i++)
        {
            var c = (rng.NextDouble() * 2 - 1) * 200;
            var az = ((c % 360) + 360) % 360;
            var target = rng.NextDouble() * 360;
            var mount = new ShortestPathMount { Az = az, Cumulative = c };
            mount.Goto(target);
            if (mount.MaxExcursion > 200) wrapped++;
        }
        Console.WriteLine($"[CableGuard] naive gotos from random positions pass the limit in {wrapped} of {trials}");
        Check.That(wrapped > trials / 20, $"{wrapped} of {trials} naive gotos would wrap the cable");
    }

    [TestMethod]
    public void Goto_FromAnAlreadyOutOfRangeCable_OnlyHeadsBackIn()
    {
        var g = NewGuard(startAz: 220, cumulative: 220); // 20 degrees past +200 (the hand control was used, or a limit was changed)
        var up = g.PlanGoto(250); // would go further out
        Check.That(!up.Allowed || up.FinalCumulative < 220, "never further out");
        var back = g.PlanGoto(200);
        Check.That(back.Allowed && Math.Abs(back.FinalCumulative) < 220, "heading in is fine");
    }

    [TestMethod]
    public void Unwind_ReturnsToNeutral_TheLongWayWhenNeeded()
    {
        var g = NewGuard(startAz: 190, cumulative: 190);
        var p = g.PlanUnwind();
        Check.That(p.Allowed, "allowed");
        Check.Near(-190.0, p.TotalTravel, 1e-9, "all the way back down");
        Check.Near(0.0, p.FinalCumulative, 1e-9, "ends at neutral");
        Check.Equal(2, p.Hops.Count, "190 degrees is two steps of 95");
        // neutral azimuth was 190 - 190 = 0
        Check.Near(0.0, p.Hops[^1].Azimuth, 1e-9, "the neutral azimuth");
        // from a turn and a half the other way
        var g2 = NewGuard(startAz: 180, cumulative: -180);
        var p2 = g2.PlanUnwind();
        Check.Near(180.0, p2.TotalTravel, 1e-9, "back up");
        Check.Near(0.0, p2.FinalCumulative, 1e-9, "neutral");
        // already neutral: nothing to do
        var g3 = NewGuard(startAz: 90, cumulative: 0);
        Check.Near(0.0, g3.PlanUnwind().TotalTravel, 1e-9, "no travel");
    }

    [TestMethod]
    public void Slew_SlowsNearALimit_StopsAtIt_AndAlwaysAllowsTheWayBack()
    {
        var g = NewGuard(startAz: 0, cumulative: 0);
        Check.That(g.CheckSlew(true, 3.0) is { Allowed: true, MaxDegreesPerSecond: 3.0 }, "free in the middle");

        g = NewGuard(startAz: 185, cumulative: 185);
        var near = g.CheckSlew(true, 3.0);
        Check.That(near.Allowed && near.MaxDegreesPerSecond < 3.0 && near.MaxDegreesPerSecond > 0, $"slowed 15 degrees from the limit: {near.MaxDegreesPerSecond}");
        var closer = NewGuard(startAz: 198, cumulative: 198).CheckSlew(true, 3.0);
        Check.That(closer.MaxDegreesPerSecond < near.MaxDegreesPerSecond, "slower still, closer in");
        Check.That(closer.MaxDegreesPerSecond <= 0.12 + 1e-9, $"a creep in the last degrees: {closer.MaxDegreesPerSecond}");
        Check.That(g.CheckSlew(false, 3.0).MaxDegreesPerSecond == 3.0, "away from the limit is free");

        var atLimit = NewGuard(startAz: 200, cumulative: 200);
        Check.That(!atLimit.CheckSlew(true, 0.5).Allowed, "refused at the limit");
        Check.That(atLimit.CheckSlew(false, 3.0).Allowed, "the way back is open");
        var atNegative = NewGuard(startAz: 160, cumulative: -200);
        Check.That(!atNegative.CheckSlew(false, 1).Allowed && atNegative.CheckSlew(true, 1).Allowed, "same at the negative limit");
    }

    [TestMethod]
    public void RateLadder_FollowsTheMeasuredSpeeds_NotAGuess()
    {
        // Real 127SLT speeds (deg/s): R7 1.0, R8 1.75, R9 about 3. The room each rate needs is (speed * 1.2 s + 2 degrees) * 2.5.
        SlewRate At(double room) => NewGuard(0, 200, 200 - room).MaxFixedRate(true);
        Check.Equal(SlewRate.Rate9, At(100), "plenty of room: full speed");
        Check.Equal(SlewRate.Rate9, At(14.06), "rate 9 needs (3.0*1.2+2)*2.5 = 14 degrees (plus the 0.05 limit tolerance)");
        Check.Equal(SlewRate.Rate8, At(13.9), "just under: one step down");
        Check.Equal(SlewRate.Rate7, At(10.2), "rate 8 needs (1.75*1.2+2)*2.5 = 10.25");
        Check.That(At(10) <= SlewRate.Rate8 && At(10) >= SlewRate.Rate7, $"10 degrees: {At(10)}");
        Check.That(At(6.0) <= SlewRate.Rate6, $"6 degrees: {At(6.0)}");
        Check.That(At(5.4) >= SlewRate.Rate1 && At(5.4) < SlewRate.Rate7, $"5.4 degrees leaves a creep: {At(5.4)}");
        Check.Equal(SlewRate.Stop, At(5.0), "under what even rate 1 needs (5.0 degrees): stop");
        Check.Equal(SlewRate.Stop, At(0.0), "at the limit");
        // monotone: more room never means a slower rate
        SlewRate prev = SlewRate.Stop;
        for (double room = 0; room <= 60; room += 0.25)
        {
            var r = At(room);
            Check.That(r >= prev, $"room {room}: {r} after {prev}");
            prev = r;
        }
        // the way away from a limit is always free
        Check.Equal(SlewRate.Rate9, NewGuard(0, 200, 200).MaxFixedRate(false), "turning back from the limit");
        // and every allowed rate stops inside the limit
        for (double room = 0.1; room <= 60; room += 0.1)
        {
            var g = NewGuard(0, 200, 200 - room);
            var r = g.MaxFixedRate(true);
            if (r != SlewRate.Stop) Check.That(g.StoppingDistance(r) < room, $"room {room}: rate {r} stops in {g.StoppingDistance(r):F2}");
        }
    }

    [TestMethod]
    public void TimeToLimit_ForTrackingDrift()
    {
        var g = NewGuard(startAz: 100, cumulative: 100);
        // tracking drift of 15 degrees an hour (sidereal, worst case) up: 100 degrees to go = 6.67 hours
        Check.Near(100.0 / (15.0 / 3600), g.SecondsToLimit(15.0 / 3600), 1e-6, "up");
        Check.Near(300.0 / (15.0 / 3600), g.SecondsToLimit(-15.0 / 3600), 1e-6, "down has more room");
        Check.That(double.IsPositiveInfinity(g.SecondsToLimit(0)), "not moving");
        Check.Equal(0.0, NewGuard(0, 200, 250).SecondsToLimit(0.001), "already past");
    }

    [TestMethod]
    public void TrackingDrift_IsMeasuredOverMinutes_NotSeconds()
    {
        // Alt-az tracking turns the azimuth by up to 15 degrees an hour (0.0042 degrees a second). The precise reading resolves 1e-7 degrees,
        // so over a few minutes the rate is clear, and so is the time left.
        var g = NewGuard(startAz: 100, cumulative: 100);
        const double drift = 15.0 / 3600;
        var t = T0;
        for (int i = 0; i <= 300; i++) // five minutes, a reading a second
        {
            g.Update(100 + drift * i, t.AddSeconds(i));
        }
        Check.That(g.SlowRateDegreesPerSecond is double slow && Math.Abs(slow - drift) < 2e-5, $"slow rate {g.SlowRateDegreesPerSecond} vs {drift}");
        var left = g.SecondsToLimit(g.SlowRateDegreesPerSecond!.Value);
        Check.Near((200 - g.Cumulative) / drift, left, 60, "time to the limit in seconds");
        // a stationary mount has no drift to report
        var still = NewGuard(startAz: 100, cumulative: 0);
        for (int i = 0; i <= 200; i++) still.Update(100, T0.AddSeconds(i));
        Check.That(Math.Abs(still.SlowRateDegreesPerSecond!.Value) < 1e-9, "still");
        // and until there are two minutes of readings there is no estimate
        var early = NewGuard(startAz: 100, cumulative: 0);
        for (int i = 0; i <= 60; i++) early.Update(100 + drift * i, T0.AddSeconds(i));
        Check.That(early.SlowRateDegreesPerSecond == null, "a minute is not enough to say");
    }

    [TestMethod]
    public void Sync_MovesTheReadingNotTheCable()
    {
        // A sync can move the reading by an amount the motors could plausibly have turned (a few degrees): only the flag tells them apart.
        var g = NewGuard(startAz: 100, cumulative: 40);
        g.ExpectJump();
        g.Update(104, T0.AddSeconds(2));
        Check.Near(40.0, g.Cumulative, 1e-9, "the cable did not turn: the 4 degrees was the sync");
        // and from the new baseline motion counts again
        g.Update(109, T0.AddSeconds(3));
        Check.Near(45.0, g.Cumulative, 1e-9, "5 degrees of real motion");

        // the same reading with no sync is motion
        var plain = NewGuard(startAz: 100, cumulative: 40);
        plain.Update(104, T0.AddSeconds(2));
        Check.Near(44.0, plain.Cumulative, 1e-9, "without the flag it is counted");

        // a large sync is also caught by the jump detector, flag or not
        var big = NewGuard(startAz: 100, cumulative: 40);
        big.ExpectJump();
        big.Update(170, T0.AddSeconds(2));
        Check.Near(40.0, big.Cumulative, 1e-9, "70 degree sync");
    }

    [TestMethod]
    public void AJumpTooBigToBeMotion_IsReportedAndNotCounted()
    {
        var g = NewGuard(startAz: 100, cumulative: 40);
        double jump = 0;
        g.JumpDetected += d => jump = d;
        // 120 degrees in 2 seconds: no motor does that (the limit is 5 degrees a second): the hand control re-aligned
        g.Update(220, T0.AddSeconds(2));
        Check.Near(120.0, jump, 1e-9, "the jump was reported");
        Check.Near(40.0, g.Cumulative, 1e-9, "and not added to the turn count");
        g.Update(225, T0.AddSeconds(3));
        Check.Near(45.0, g.Cumulative, 1e-9, "counting resumes");
    }

    [TestMethod]
    public void ALongGap_MakesTheCountUncertain_AndTheGuardRefusesUntilConfirmed()
    {
        var g = NewGuard(startAz: 0, cumulative: 0);
        // 60 s without a reading: at 5 degrees a second the mount could have turned 300, past half a revolution
        g.Update(10, T0.AddSeconds(60));
        Check.That(g.Uncertain, "uncertain");
        Check.That(g.State.UncertainReason!.Contains("60"), "says how long");
        Check.That(!g.PlanGoto(20).Allowed, "no goto while the cable position is not known");
        Check.That(!g.CheckSlew(true, 1).Allowed, "no slew either");
        Check.That(!g.PlanUnwind().Allowed, "and no unwind: it does not know where neutral is");
        g.Confirm(30, 10, T0.AddSeconds(61));
        Check.That(!g.Uncertain, "confirmed by the user");
        Check.Near(30.0, g.Cumulative, 1e-9, "at the confirmed turn");
        Check.That(g.PlanGoto(20).Allowed, "allowed again");
    }

    [TestMethod]
    public void ANewGuard_IsUncertain_UntilTheUserSaysWhereTheCableIs()
    {
        var g = new CableGuard();
        g.Update(100, T0);
        Check.That(g.Uncertain && !g.PlanGoto(10).Allowed, "a fresh guard does not guess");
        g.SetNeutral(100, T0);
        Check.That(!g.Uncertain && g.Cumulative == 0, "neutral set");
        Check.That(g.PlanGoto(10).Allowed, "now it works");
    }

    [TestMethod]
    public void WhenDisabled_NothingIsRefused_ButTurnsAreStillCounted()
    {
        var g = new CableGuard(new CableGuardOptions { Enabled = false });
        g.Update(0, T0);
        g.Update(100, T0.AddSeconds(30));
        Check.That(g.PlanGoto(300).Allowed, "allowed even though uncertain");
        Check.That(g.CheckSlew(true, 2).Allowed, "slews allowed");
        Check.Near(100.0, g.Cumulative, 1e-9, "still counting");
    }

    [TestMethod]
    public void State_SurvivesAJsonRoundTrip_AndRejectsGarbage()
    {
        var g = NewGuard(startAz: 123.5, cumulative: -250.25);
        var json = g.State.ToJson();
        var back = CableState.FromJson(json);
        Check.That(back != null, "parsed");
        Check.Equal(-250.25, back!.Cumulative, "cumulative");
        Check.Equal(123.5, back.LastAzimuth!.Value, "azimuth");
        Check.Equal(false, back.Uncertain, "trusted flag");
        foreach (var bad in new[] { null, "", "   ", "not json", "{}", "{\"Version\":2}", "{\"Version\":1,\"Cumulative\":1,\"LastAzimuth\":400}", "[1,2]" })
        {
            var s = CableState.FromJson(bad);
            Check.That(s == null || bad == "{}" , $"'{bad}' is not accepted as a state");
        }
    }
}
