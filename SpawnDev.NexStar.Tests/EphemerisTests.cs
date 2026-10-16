using SpawnDev.UnitTesting;

namespace SpawnDev.NexStar.Tests;

/// <summary>
/// Planet, Sun and precession accuracy against NASA/JPL Horizons. The old planet code put the Sun 180 degrees off
/// (it used the Sun's geocentric elements as the Earth's orbit), added the equation of centre in the wrong unit and ignored
/// inclination, so these positions were wrong by degrees.
/// </summary>
public class EphemerisTests
{
    [TestMethod]
    public void Planets_MatchHorizons_AstrometricJ2000()
    {
        var worst = new Dictionary<string, double>();
        foreach (var (body, jd, ra, dec) in HorizonsReference.AstrometricJ2000)
        {
            var ours = SolarSystemMath.GetPositionJ2000(HorizonsReference.Object(body), HorizonsReference.Utc(jd));
            var err = HorizonsReference.SeparationArcsec(ra, dec, ours.RightAscension, ours.Declination);
            worst[body] = Math.Max(worst.GetValueOrDefault(body), err);
            Check.That(err <= HorizonsReference.ToleranceArcsec(body), $"{body} at JD {jd}: {err:F1} arcsec off (limit {HorizonsReference.ToleranceArcsec(body)})");
        }
        Console.WriteLine("[Ephemeris] worst J2000 error (arcsec): " + string.Join(", ", worst.Select(kv => $"{kv.Key} {kv.Value:F1}")));
    }

    [TestMethod]
    public void Precession_J2000ToDate_MatchesHorizonsApparent()
    {
        // Horizons' astrometric J2000 position precessed by this library must land on Horizons' apparent position of the date,
        // within nutation (17") + aberration (20") + a little. Fifty years out, a wrong precession would be 0.7 degrees off.
        foreach (var (body, jd, appRa, appDec) in HorizonsReference.ApparentOfDate)
        {
            var astro = HorizonsReference.AstrometricJ2000.Single(a => a.Body == body && a.Jd == jd);
            var (ra, dec) = Precession.J2000ToDate(astro.Ra, astro.Dec, HorizonsReference.Utc(jd));
            var err = HorizonsReference.SeparationArcsec(appRa, appDec, ra, dec);
            Check.That(err <= 60, $"{body} at JD {jd}: {err:F1} arcsec from Horizons' apparent place (nutation + aberration allow about 40)");
        }
    }

    [TestMethod]
    public void Planets_OfDate_MatchHorizonsApparent()
    {
        foreach (var (body, jd, appRa, appDec) in HorizonsReference.ApparentOfDate)
        {
            var ours = SolarSystemMath.GetPosition(HorizonsReference.Object(body), HorizonsReference.Utc(jd));
            var err = HorizonsReference.SeparationArcsec(appRa, appDec, ours.RightAscension, ours.Declination);
            var limit = HorizonsReference.ToleranceArcsec(body) + 60;
            Check.That(err <= limit, $"{body} at JD {jd}: {err:F1} arcsec off Horizons' apparent place (limit {limit})");
        }
    }

    [TestMethod]
    public void Planets_AzAlt_MatchHorizonsFromASite()
    {
        // The whole chain a telescope user depends on: ephemeris, precession, sidereal time, hour angle, altitude and azimuth.
        // Horizons gives azimuth from north through east with refraction off, the same convention as AzAltCoordinates.
        var worst = 0.0;
        foreach (var (body, jd, az, el) in HorizonsReference.AzEl.Where(r => r.Body != "Moon"))
        {
            var ours = SolarSystemMath.GetAzAlt(HorizonsReference.Object(body), HorizonsReference.SiteLatitude, HorizonsReference.SiteLongitude, HorizonsReference.Utc(jd));
            var err = HorizonsReference.SeparationArcsec(az, el, ours.Azimuth, ours.Altitude);
            worst = Math.Max(worst, err);
            var limit = HorizonsReference.ToleranceArcsec(body) + 60;
            Check.That(err <= limit, $"{body} at JD {jd}: az/alt {err:F1} arcsec off Horizons (ours {ours.Azimuth:F4}/{ours.Altitude:F4}, theirs {az}/{el}; limit {limit})");
        }
        Console.WriteLine($"[Ephemeris] worst az/alt error {worst:F1} arcsec");
    }

    [TestMethod]
    public void Sun_IsNeverAGotoTarget_ButItsPositionIsAvailable()
    {
        Check.That(!SolarSystemMath.Planets.Contains(SolarSystemObject.Sun), "the Sun is not in the list of things a telescope can be sent to");
        foreach (var p in SolarSystemMath.Planets) Check.That(p != SolarSystemObject.Sun, "no Sun");
        Check.Equal(7, SolarSystemMath.Planets.Count, "Mercury to Neptune");
        var sun = SolarSystemMath.GetPosition(SolarSystemObject.Sun, new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc));
        Check.Near(200.6, sun.RightAscension, 0.6, "the Sun is where JPL says (mean place of date)");
        Check.Near(-8.7, sun.Declination, 0.3, "declination");
    }

    [TestMethod]
    public void Distances_AreSane()
    {
        var t = new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc);
        Check.Near(0.9955, SolarSystemMath.GetDistanceAu(SolarSystemObject.Sun, t), 0.002, "Sun distance in mid-October is about 0.9955 au");
        Check.That(SolarSystemMath.GetDistanceAu(SolarSystemObject.Jupiter, t) is > 3.9 and < 6.5, "Jupiter 4-6.5 au");
        Check.That(SolarSystemMath.GetDistanceAu(SolarSystemObject.Neptune, t) is > 28.8 and < 31.2, "Neptune about 30 au");
    }

    [TestMethod]
    public void Precession_AtJ2000_IsTheIdentity()
    {
        var m = Precession.J2000ToDateMatrix(0);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                Check.Near(i == j ? 1.0 : 0.0, m[i, j], 1e-15, $"m[{i},{j}]");
    }

    [TestMethod]
    public void Precession_AtTheEquator_MovesByTheKnownRate()
    {
        // Meeus (21.1): at RA 0, Dec 0 the annual precession is 46.124 arcsec in RA and 20.043 arcsec in Dec.
        // Fifty years from J2000: 2306.2" and 1002.2" (second-order terms are about an arcsecond).
        var utc = new DateTime(2050, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var (ra, dec) = Precession.J2000ToDate(0, 0, utc);
        Check.Near(2306.2, ra * 3600.0, 4, "RA shift in arcsec");
        Check.Near(1002.2, dec * 3600.0, 4, "Dec shift in arcsec");
    }

    [TestMethod]
    public void Precession_RoundTrips_AtThePolesAndTheWrap()
    {
        var starts = new[] { (0.0, 0.0), (359.9999999, 0.0), (180.0, 89.9999), (45.0, -89.9999), (0.0, 90.0), (123.456, -33.3), (270.0, 60.0) };
        foreach (var year in new[] { 1900, 1950, 2000, 2026, 2100 })
        {
            var utc = new DateTime(year, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            foreach (var (ra0, dec0) in starts)
            {
                var (ra1, dec1) = Precession.J2000ToDate(ra0, dec0, utc);
                var (ra2, dec2) = Precession.DateToJ2000(ra1, dec1, utc);
                var sep = AstronomyMath.AngularSeparation(ra0, dec0, ra2, dec2) * 3600.0;
                Check.That(sep < 0.001, $"{ra0},{dec0} in {year}: round trip off by {sep} arcsec");
                Check.That(ra1 >= 0 && ra1 < 360, $"RA {ra1} stays in range");
            }
        }
    }

    [TestMethod]
    public void DeltaT_IsInTheRightPlace()
    {
        // The accepted values: 63.8 s in 2000, about 69 s in 2020-2026. The polynomial fit is within a few seconds, which is under
        // 2 arcseconds for Mercury, the fastest planet.
        Check.Near(63.8, Precession.DeltaTSeconds(2000.0), 1.0, "2000");
        Check.Near(69.4, Precession.DeltaTSeconds(2020.0), 4.0, "2020");
        Check.Near(69.2, Precession.DeltaTSeconds(2026.0), 7.0, "2026");
    }
}
