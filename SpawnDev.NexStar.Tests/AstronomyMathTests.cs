using SpawnDev.UnitTesting;

namespace SpawnDev.NexStar.Tests;

/// <summary>
/// Julian date, sidereal time and the equatorial/horizontal conversion, against the worked examples in
/// Meeus, "Astronomical Algorithms" (2nd ed.), and against edge cases the old acos-based conversion turned into NaN.
/// </summary>
public class AstronomyMathTests
{
    [TestMethod]
    public void JulianDate_MeeusExamples()
    {
        Check.Equal(2451545.0, AstronomyMath.DateTimeToJulianDate(new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)), "J2000.0");
        Check.Near(2436116.31, AstronomyMath.DateTimeToJulianDate(new DateTime(1957, 10, 4, 19, 26, 24, DateTimeKind.Utc)), 1e-9, "Sputnik launch, Meeus 7.a");
        Check.Equal(2446895.5, AstronomyMath.DateTimeToJulianDate(new DateTime(1987, 4, 10, 0, 0, 0, DateTimeKind.Utc)), "1987 April 10, Meeus 7.c");
        Check.Near(2446896.30625, AstronomyMath.DateTimeToJulianDate(new DateTime(1987, 4, 10, 19, 21, 0, DateTimeKind.Utc)), 1e-9, "1987 April 10 19:21");
    }

    [TestMethod]
    public void JulianDate_CountsMilliseconds_AndConvertsLocalTime()
    {
        var utc = new DateTime(2026, 10, 16, 12, 0, 0, 500, DateTimeKind.Utc);
        var whole = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        Check.Near(0.5 / 86400.0, AstronomyMath.DateTimeToJulianDate(utc) - AstronomyMath.DateTimeToJulianDate(whole), 1e-9, "half a second is counted");
        Check.Equal(AstronomyMath.DateTimeToJulianDate(utc), AstronomyMath.DateTimeToJulianDate(utc.ToLocalTime()), "a Local time is the same instant, not read as UTC");
    }

    [TestMethod]
    public void SiderealTime_MeeusExamples()
    {
        // 12.a: 1987 April 10, 0h UT: mean sidereal time at Greenwich 13h 10m 46.3668s
        Check.Near((13 + 10 / 60.0 + 46.3668 / 3600.0) * 15.0, AstronomyMath.GetGreenwichSiderealTime(new DateTime(1987, 4, 10, 0, 0, 0, DateTimeKind.Utc)), 1e-5, "0h UT");
        // 12.b: 19:21:00 UT: 8h 34m 57.0896s
        Check.Near((8 + 34 / 60.0 + 57.0896 / 3600.0) * 15.0, AstronomyMath.GetGreenwichSiderealTime(new DateTime(1987, 4, 10, 19, 21, 0, DateTimeKind.Utc)), 1e-5, "19:21 UT");
        var lst = AstronomyMath.GetLocalSiderealTime(-77.0, new DateTime(1987, 4, 10, 19, 21, 0, DateTimeKind.Utc));
        Check.Near(AstronomyMath.GetGreenwichSiderealTime(new DateTime(1987, 4, 10, 19, 21, 0, DateTimeKind.Utc)) - 77.0, lst, 1e-9, "local = Greenwich + east longitude");
    }

    [TestMethod]
    public void Horizontal_MeeusVenusExample()
    {
        // 13.b: Venus, RA 23h09m16.641s, Dec -6 43' 11.61", at Washington (38 55' 17" N, 77 03' 56" W), 1987 April 10 19:21:00 UT.
        // Meeus: azimuth 68.0337 measured from the SOUTH, altitude 15.1249. From the north that is 248.0337.
        // (Meeus uses apparent sidereal time, 0.0035 degrees from the mean sidereal time used here.)
        var lat = 38 + 55 / 60.0 + 17 / 3600.0;
        var lon = -(77 + 3 / 60.0 + 56 / 3600.0);
        var ra = (23 + 9 / 60.0 + 16.641 / 3600.0) * 15.0;
        var dec = -(6 + 43 / 60.0 + 11.61 / 3600.0);
        var p = AstronomyMath.EquatorialToHorizontal(ra, dec, lat, lon, new DateTime(1987, 4, 10, 19, 21, 0, DateTimeKind.Utc));
        Check.Near(15.1249, p.Altitude, 0.01, "altitude");
        Check.Near(248.0337, p.Azimuth, 0.01, "azimuth from the north");
    }

    [TestMethod]
    public void Horizontal_ZenithAndPoles_AreNotNaN()
    {
        // Object exactly at the zenith: hour angle 0, declination = latitude. The old acos form divided by cos(alt) = 0.
        var utc = new DateTime(2026, 10, 16, 3, 0, 0, DateTimeKind.Utc);
        var lat = 40.0;
        var lon = -75.0;
        var ra = AstronomyMath.GetLocalSiderealTime(lon, utc);
        var zenith = AstronomyMath.EquatorialToHorizontal(ra, lat, lat, lon, utc);
        Check.Near(90.0, zenith.Altitude, 1e-9, "zenith altitude");
        Check.That(double.IsFinite(zenith.Azimuth) && zenith.Azimuth >= 0 && zenith.Azimuth < 360, $"zenith azimuth is a number: {zenith.Azimuth}");

        // Observer at the pole: azimuth is hour angle shifted, altitude is declination
        var pole = AstronomyMath.EquatorialToHorizontal(100.0, 45.0, 90.0, 0.0, utc);
        Check.Near(45.0, pole.Altitude, 1e-9, "altitude at the north pole is the declination");
        Check.That(double.IsFinite(pole.Azimuth), "pole azimuth");
        var southPole = AstronomyMath.EquatorialToHorizontal(100.0, -45.0, -90.0, 0.0, utc);
        Check.Near(45.0, southPole.Altitude, 1e-9, "south pole");
        Check.That(double.IsFinite(southPole.Azimuth), "south pole azimuth");

        // Looking at the celestial pole from the pole
        var star = AstronomyMath.EquatorialToHorizontal(0.0, 90.0, 40.0, lon, utc);
        Check.Near(40.0, star.Altitude, 1e-9, "the celestial pole is at the latitude");
        Check.Near(0.0, Math.Min(star.Azimuth, 360 - star.Azimuth), 1e-9, "due north");
    }

    [TestMethod]
    public void Horizontal_GridRoundTrips_AndIsAlwaysFinite()
    {
        var utc = new DateTime(2026, 10, 16, 3, 0, 0, DateTimeKind.Utc);
        var count = 0;
        for (double lat = -90; lat <= 90; lat += 15)
        for (double dec = -90; dec <= 90; dec += 15)
        for (double ra = 0; ra < 360; ra += 20)
        {
            var h = AstronomyMath.EquatorialToHorizontal(ra, dec, lat, 11.5, utc);
            Check.That(double.IsFinite(h.Azimuth) && double.IsFinite(h.Altitude), $"lat {lat} dec {dec} ra {ra}: {h}");
            Check.That(h.Azimuth >= 0 && h.Azimuth < 360 && h.Altitude >= -90 && h.Altitude <= 90, $"range {h}");
            var back = AstronomyMath.HorizontalToEquatorial(h.Azimuth, h.Altitude, lat, 11.5, utc);
            var sep = AstronomyMath.AngularSeparation(ra, dec, back.RightAscension, back.Declination) * 3600.0;
            Check.That(sep < 0.001, $"lat {lat} dec {dec} ra {ra}: round trip off by {sep} arcsec ({back})");
            count++;
        }
        Check.That(count > 1000, "the grid ran");
    }

    [TestMethod]
    public void Horizontal_CardinalDirections()
    {
        // At the equator an object on the celestial equator crosses due east when rising: azimuth 90, and due west when setting: 270.
        var utc = new DateTime(2026, 10, 16, 3, 0, 0, DateTimeKind.Utc);
        var lst = AstronomyMath.GetLocalSiderealTime(0, utc);
        var rising = AstronomyMath.EquatorialToHorizontal(lst + 90.0 - 0.0, 0.0, 0.0, 0.0, utc);   // hour angle -90: just rising
        Check.Near(0.0, rising.Altitude, 1e-9, "on the horizon");
        Check.Near(90.0, rising.Azimuth, 1e-9, "rising in the east (hour angle negative)");
        var setting = AstronomyMath.EquatorialToHorizontal(lst - 90.0, 0.0, 0.0, 0.0, utc);       // hour angle +90
        Check.Near(270.0, setting.Azimuth, 1e-9, "setting in the west");
        // Northern observer: the south meridian crossing is azimuth 180
        var transit = AstronomyMath.EquatorialToHorizontal(lst, 0.0, 40.0, 0.0, utc);
        Check.Near(180.0, transit.Azimuth, 1e-9, "transit is due south");
        Check.Near(50.0, transit.Altitude, 1e-9, "altitude 90 - latitude on the equator");
    }

    [TestMethod]
    public void J2000ToHorizontal_PrecessesCatalogCoordinates()
    {
        // Sirius (J2000 RA 06h45m08.917s, Dec -16 42' 58.02") is a catalog position. Reading it as "of the date" in 2026 is off by the precession
        // of 26 years: about 0.36 degrees, most of a Moon-width and enough to miss a high-power eyepiece field by a mile.
        var utc = new DateTime(2026, 10, 16, 3, 0, 0, DateTimeKind.Utc);
        double ra = (6 + 45 / 60.0 + 8.917 / 3600.0) * 15.0, dec = -(16 + 42 / 60.0 + 58.02 / 3600.0);
        var right = AstronomyMath.J2000ToHorizontal(ra, dec, HorizonsReference.SiteLatitude, HorizonsReference.SiteLongitude, utc);
        var naive = AstronomyMath.EquatorialToHorizontal(ra, dec, HorizonsReference.SiteLatitude, HorizonsReference.SiteLongitude, utc);
        var miss = AstronomyMath.AngularSeparation(right.Azimuth, right.Altitude, naive.Azimuth, naive.Altitude);
        Check.That(miss > 0.25 && miss < 0.5, $"the unprecessed answer is off by {miss:F3} degrees");

        var (raDate, decDate) = Precession.J2000ToDate(ra, dec, utc);
        var manual = AstronomyMath.EquatorialToHorizontal(raDate, decDate, HorizonsReference.SiteLatitude, HorizonsReference.SiteLongitude, utc);
        Check.Near(manual.Azimuth, right.Azimuth, 1e-9, "azimuth is precession then conversion");
        Check.Near(manual.Altitude, right.Altitude, 1e-9, "altitude");
    }

    [TestMethod]
    public void Refraction_KnownValues()
    {
        // Saemundsson: about 28.9 arcminutes for an object whose true (airless) altitude is 0, about 1.0 at 45 degrees, none overhead.
        Check.Near(28.9 / 60.0, AstronomyMath.RefractionDegrees(0.0), 0.005, "horizon");
        Check.Near(1.0 / 60.0, AstronomyMath.RefractionDegrees(45.0), 0.001, "45 degrees");
        Check.That(AstronomyMath.RefractionDegrees(90.0) < 0.0001, "zenith");
        Check.Equal(0.0, AstronomyMath.RefractionDegrees(-5.0), "below the horizon is not refracted into view");
        Check.That(AstronomyMath.RefractionDegrees(10.0, 900) < AstronomyMath.RefractionDegrees(10.0, 1010), "thinner air refracts less");
        Check.That(AstronomyMath.RefractionDegrees(10.0, 1010, -10) > AstronomyMath.RefractionDegrees(10.0, 1010, 30), "cold air refracts more");
    }
}
