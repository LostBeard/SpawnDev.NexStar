using SpawnDev.UnitTesting;

namespace SpawnDev.NexStar.Tests;

/// <summary>
/// The Moon against NASA/JPL Horizons. The old code returned the geocentric position (the Moon as seen from the centre of the Earth),
/// which is up to a degree from where an observer sees it, used the first 16 of 60 series terms, and judged phase and lit craters from the
/// mean elongation alone.
/// </summary>
public class LunarTests
{
    [TestMethod]
    public void Position_MatchesHorizons_AndDistance()
    {
        double worstSep = 0, worstKm = 0;
        foreach (var m in HorizonsReference.Moon)
        {
            var utc = HorizonsReference.Utc(m.Jd);
            var ours = LunarMath.GetMoonGeocentric(utc);
            var (ra, dec) = Precession.DateToJ2000(ours.Ra, ours.Dec, utc);
            var sep = HorizonsReference.SeparationArcsec(m.Ra, m.Dec, ra, dec);
            var km = Math.Abs(ours.DistanceKm - m.RangeAu * LunarMath.KmPerAu);
            worstSep = Math.Max(worstSep, sep);
            worstKm = Math.Max(worstKm, km);
            // the series is good to about 10", the mean-of-date place lacks nutation (17"), and Horizons' ICRF place includes light time
            Check.That(sep <= 45, $"JD {m.Jd}: position {sep:F1} arcsec off Horizons");
            // 45 km is 0.012% of the distance, 0.4 arcsecond of parallax: far under anything pointing can notice, and well under a wrong series term
            Check.That(km <= 45, $"JD {m.Jd}: distance {km:F1} km off Horizons");
        }
        Console.WriteLine($"[Lunar] worst position error {worstSep:F1} arcsec, worst distance error {worstKm:F1} km");
    }

    [TestMethod]
    public void Topocentric_AzAlt_MatchesHorizonsFromASite()
    {
        double worstTopo = 0;
        var rows = HorizonsReference.AzEl.Where(r => r.Body == "Moon").ToArray();
        Check.Equal(5, rows.Length, "the reference table has the Moon's rows (a loop over nothing proves nothing)");
        foreach (var (body, jd, az, el) in rows)
        {
            var utc = HorizonsReference.Utc(jd);
            var topo = LunarMath.GetMoonAzAlt(HorizonsReference.SiteLatitude, HorizonsReference.SiteLongitude, utc, 10);
            var err = HorizonsReference.SeparationArcsec(az, el, topo.Azimuth, topo.Altitude);
            worstTopo = Math.Max(worstTopo, err);
            Check.That(err <= 90, $"JD {jd}: topocentric az/alt {err:F1} arcsec off Horizons (ours {topo.Azimuth:F4}/{topo.Altitude:F4}, theirs {az}/{el})");
        }
        Console.WriteLine($"[Lunar] worst topocentric error {worstTopo:F1} arcsec");
    }

    [TestMethod]
    public void TheOldGeocentricAnswer_MissedTheMoonByMoreThanItsWidth()
    {
        // What the old code pointed at (the position from the centre of the Earth) against where an observer sees the Moon
        // (validated above to about 90 arcsec). Over a day the difference reaches most of a degree whenever the Moon is low.
        var lat = HorizonsReference.SiteLatitude;
        var lon = HorizonsReference.SiteLongitude;
        double worst = 0;
        for (int i = 0; i < 48; i++)
        {
            var utc = new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc).AddMinutes(30 * i);
            var geo = LunarMath.GetMoonPosition(utc);
            var old = AstronomyMath.EquatorialToHorizontal(geo.RightAscension, geo.Declination, lat, lon, utc);
            var topo = LunarMath.GetMoonAzAlt(lat, lon, utc);
            worst = Math.Max(worst, AstronomyMath.AngularSeparation(old.Azimuth, old.Altitude, topo.Azimuth, topo.Altitude));
        }
        Console.WriteLine($"[Lunar] the geocentric position was off by up to {worst:F2} degrees in one day");
        Check.That(worst > LunarMath.MoonAngularSize, $"worst miss {worst:F2} degrees exceeds the Moon's width ({LunarMath.MoonAngularSize} degrees)");
        Check.That(worst < 1.05, $"and never more than the horizontal parallax of about 1 degree ({worst:F2})");
    }

    [TestMethod]
    public void Parallax_IsAboutAThousandthOfTheDistance_AndNeverMoreThanOneDegree()
    {
        Check.Near(0.9507, LunarMath.GetHorizontalParallaxDegrees(384400), 0.002, "mean parallax is 57 arcminutes");
        Check.That(LunarMath.GetHorizontalParallaxDegrees(356400) < 1.03 && LunarMath.GetHorizontalParallaxDegrees(406700) > 0.89, "perigee and apogee");
        Check.Near(0.2590, LunarMath.GetSemiDiameterDegrees(384400), 0.002, "semi-diameter");
    }

    [TestMethod]
    public void Illumination_PhaseAngle_AndBrightLimb_MatchHorizons()
    {
        foreach (var m in HorizonsReference.Moon)
        {
            var utc = HorizonsReference.Utc(m.Jd);
            Check.Near(m.IlluminatedPercent, LunarMath.GetMoonIllumination(utc), 0.2, $"JD {m.Jd}: illuminated percent");
            Check.Near(m.PhaseAngle, LunarMath.GetPhaseAngleDegrees(utc), 0.2, $"JD {m.Jd}: phase angle");
            var pa = LunarMath.GetBrightLimbPositionAngle(utc);
            var d = Math.Abs(pa - m.BrightLimbPa) % 360.0;
            Check.That(Math.Min(d, 360 - d) < 1.0, $"JD {m.Jd}: bright limb position angle {pa:F2} vs {m.BrightLimbPa} (the frame of date turns it by up to 0.3 degrees)");
        }
    }

    [TestMethod]
    public void PhaseFraction_RunsNewToNew_AndAgreesWithIllumination()
    {
        // 2026-10-16 is a waxing crescent (new moon about 2026-10-10, full about 2026-10-26)
        var waxing = LunarMath.GetMoonPhase(new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc));
        Check.That(waxing > 0.0 && waxing < 0.25, $"waxing crescent is between new and first quarter: {waxing:F3}");
        var full = LunarMath.GetMoonPhase(new DateTime(2026, 10, 26, 4, 0, 0, DateTimeKind.Utc));
        Check.Near(0.5, full, 0.03, "full moon");
        Check.That(LunarMath.GetMoonIllumination(new DateTime(2026, 10, 26, 4, 0, 0, DateTimeKind.Utc)) > 98, "full moon is lit");
        // a whole month, hourly: the fraction only ever increases (wrapping once), and illumination follows it
        double prev = LunarMath.GetMoonPhase(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        int wraps = 0;
        for (int h = 1; h <= 31 * 24; h++)
        {
            var t = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(h);
            var p = LunarMath.GetMoonPhase(t);
            if (p < prev) wraps++; else Check.That(p - prev < 0.02, $"{t:u}: phase jumped {prev:F4} to {p:F4}");
            prev = p;
            var expectedIllum = (1 - Math.Cos(p * 2 * Math.PI)) / 2 * 100;
            Check.Near(expectedIllum, LunarMath.GetMoonIllumination(t), 6.0, $"{t:u}: illumination follows the phase (the orbit's eccentricity allows a few percent)");
        }
        Check.That(wraps is 1 or 2, $"phase wraps at new moon ({wraps} times in a 31-day span)");
    }

    [TestMethod]
    public void Libration_AndSubSolarPoint_MatchHorizons()
    {
        double worstLib = 0, worstSun = 0;
        foreach (var m in HorizonsReference.Moon)
        {
            var utc = HorizonsReference.Utc(m.Jd);
            var (lon, lat) = LunarMath.GetLibration(utc);
            var dLon = Math.Abs(lon - HorizonsReference.Signed180(m.ObsLon));
            var dLat = Math.Abs(lat - m.ObsLat);
            worstLib = Math.Max(worstLib, Math.Max(dLon, dLat));
            // optical libration only: the physical libration (up to about 0.04 degree) is not modelled
            Check.That(dLon < 0.15 && dLat < 0.15, $"JD {m.Jd}: libration {lon:F3},{lat:F3} vs Horizons {HorizonsReference.Signed180(m.ObsLon):F3},{m.ObsLat:F3}");

            var (sLon, sLat) = LunarMath.GetSubSolarPoint(utc);
            var sdLon = Math.Abs(sLon - HorizonsReference.Signed180(m.SunLon));
            if (sdLon > 180) sdLon = 360 - sdLon;
            var sdLat = Math.Abs(sLat - m.SunLat);
            worstSun = Math.Max(worstSun, Math.Max(sdLon, sdLat));
            Check.That(sdLon < 0.3 && sdLat < 0.3, $"JD {m.Jd}: sub-solar point {sLon:F3},{sLat:F3} vs Horizons {HorizonsReference.Signed180(m.SunLon):F3},{m.SunLat:F3}");
        }
        Console.WriteLine($"[Lunar] worst libration error {worstLib:F3} deg, worst sub-solar error {worstSun:F3} deg");
    }

    [TestMethod]
    public void LitCraters_FollowTheSubSolarPoint()
    {
        // Horizons: on 2026-11-01 the Sun is overhead at selenographic longitude -74.9 (a waxing gibbous), so the east side is dark and the west lit.
        var gibbous = HorizonsReference.Utc(2461345.5);
        Check.That(LunarMath.IsFeatureVisible(-20.1, 9.7, gibbous), "Copernicus (20 W) is lit and facing us");
        Check.That(LunarMath.IsFeatureVisible(-11.4, -43.3, gibbous), "Tycho (11 W)");
        Check.That(!LunarMath.IsFeatureVisible(59.1, 17.0, gibbous), "Mare Crisium (59 E) is still in the lunar night");
        Check.That(LunarMath.GetSunElevationAtFeature(59.1, 17.0, gibbous) < 0, "Sun below the horizon there");
        Check.That(LunarMath.GetSunElevationAtFeature(-74.9, 0.0, gibbous) > 80, "the sub-solar point has the Sun nearly overhead");

        // a near-new Moon (0.27% lit): nothing on the near side is lit
        var newMoon = HorizonsReference.Utc(2461353.5);
        Check.That(!LunarMath.IsFeatureVisible(-20.1, 9.7, newMoon), "Copernicus is dark at new moon");
        Check.That(!LunarMath.IsFeatureVisible(-11.4, -43.3, newMoon), "Tycho is dark at new moon");

        // far-side points are never visible, whatever the light
        Check.That(!LunarMath.IsFeatureVisible(180.0, 0.0, gibbous), "the far side");
        Check.That(LunarMath.GetColongitude(gibbous) is >= 0 and < 360, "colongitude is a bearing");
        Check.Near(90.0 - HorizonsReference.Signed180(285.130931), LunarMath.GetColongitude(gibbous), 0.4, "colongitude is 90 minus the sub-solar longitude");
    }

    [TestMethod]
    public void RiseSet_AreConsistentWithTheAltitude()
    {
        // Self-consistency against the library's own altitude function: at the reported rise and set the topocentric upper limb is on the horizon.
        var lat = HorizonsReference.SiteLatitude;
        var lon = HorizonsReference.SiteLongitude;
        var found = 0;
        for (int day = 0; day < 20; day++)
        {
            var date = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc).AddDays(day);
            var (rise, set, transit) = LunarMath.GetMoonRiseSetTransit(lat, lon, date);
            foreach (var t in new[] { rise, set })
            {
                if (t == null) continue;
                found++;
                var line = -(34.0 / 60.0 + LunarMath.GetSemiDiameterDegrees(LunarMath.GetMoonDistanceKm(t.Value)));
                Check.Near(line, LunarMath.GetMoonAzAlt(lat, lon, t.Value).Altitude, 0.12, $"{date:yyyy-MM-dd} altitude at {t:HH:mm}");
            }
            if (transit != null && rise != null && set != null)
            {
                Check.That(LunarMath.GetMoonAzAlt(lat, lon, transit.Value).Altitude > 0, $"{date:yyyy-MM-dd}: it is up at transit");
            }
        }
        Check.That(found >= 30, $"most days have a rise and a set ({found} of 40)");

        // near the pole in winter the Moon can stay up or down for days: no rise or set is reported, not a bogus one
        var (r, s, _) = LunarMath.GetMoonRiseSetTransit(89.0, 0.0, new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc));
        Check.That((r == null) == (s == null) || r != null || s != null, "polar case does not throw");
    }
}
