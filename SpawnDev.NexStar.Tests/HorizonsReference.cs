namespace SpawnDev.NexStar.Tests;

/// <summary>
/// Reference positions from NASA/JPL Horizons (https://ssd.jpl.nasa.gov/api/horizons.api), fetched 2026-10-16. Public data.
/// Times are Julian dates (UT); angles in degrees.
/// </summary>
internal static class HorizonsReference
{
    /// <summary>The UTC time of a Julian date.</summary>
    public static DateTime Utc(double jd) => new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(jd - 2451545.0);

    /// <summary>Geocentric ASTROMETRIC RA/Dec in the J2000 frame (Horizons quantity 1, CENTER 500@399).</summary>
    public static readonly (string Body, double Jd, double Ra, double Dec)[] AstrometricJ2000 =
    {
        ("Sun", 2451545.0, 281.28898, -23.03325),
        ("Sun", 2455362.5, 83.10572, 23.28652),
        ("Sun", 2461329.5, 200.60001, -8.67155),
        ("Sun", 2466000.5, 129.54842, 18.48133),
        ("Sun", 2470000.5, 111.78085, 21.92385),
        ("Mercury", 2451545.0, 272.08522, -24.42038),
        ("Mercury", 2455362.5, 66.78477, 20.62702),
        ("Mercury", 2461329.5, 223.52056, -19.87118),
        ("Mercury", 2466000.5, 110.58846, 19.01082),
        ("Mercury", 2470000.5, 135.21994, 18.28647),
        ("Venus", 2451545.0, 239.90118, -18.45185),
        ("Venus", 2455362.5, 123.26345, 21.97834),
        ("Venus", 2461329.5, 210.05419, -20.18532),
        ("Venus", 2466000.5, 141.12446, 9.07612),
        ("Venus", 2470000.5, 156.36444, 11.12759),
        ("Mars", 2451545.0, 330.52460, -13.18050),
        ("Mars", 2455362.5, 156.27368, 11.14062),
        ("Mars", 2461329.5, 132.61776, 19.02591),
        ("Mars", 2466000.5, 57.15404, 19.01881),
        ("Mars", 2470000.5, 330.16236, -18.03520),
        ("Jupiter", 2451545.0, 23.86983, 8.59590),
        ("Jupiter", 2455362.5, 1.34906, -0.73309),
        ("Jupiter", 2461329.5, 144.31488, 14.86602),
        ("Jupiter", 2466000.5, 156.17308, 10.95607),
        ("Jupiter", 2470000.5, 131.74197, 18.53487),
        ("Saturn", 2451545.0, 38.76600, 12.61628),
        ("Saturn", 2455362.5, 178.99861, 3.02292),
        ("Saturn", 2461329.5, 10.27911, 1.47735),
        ("Saturn", 2466000.5, 176.64837, 3.77763),
        ("Saturn", 2470000.5, 308.51073, -19.27925),
    };

    /// <summary>Geocentric APPARENT RA/Dec of the date (Horizons quantity 2): includes nutation (up to 17") and aberration (up to 20").</summary>
    public static readonly (string Body, double Jd, double Ra, double Dec)[] ApparentOfDate =
    {
        ("Sun", 2451545.0, 281.27838, -23.03243),
        ("Sun", 2455362.5, 83.26340, 23.29380),
        ("Sun", 2461329.5, 200.94778, -8.81048),
        ("Sun", 2466000.5, 130.10174, 18.34311),
        ("Sun", 2470000.5, 112.53066, 21.81635),
        ("Jupiter", 2451545.0, 23.86784, 8.59426),
        ("Jupiter", 2455362.5, 1.48646, -0.67327),
        ("Jupiter", 2461329.5, 144.68097, 14.74600),
        ("Jupiter", 2466000.5, 156.68823, 10.75778),
        ("Jupiter", 2470000.5, 132.45708, 18.34536),
        ("Saturn", 2451545.0, 38.76539, 12.61476),
        ("Saturn", 2455362.5, 179.13733, 2.96295),
        ("Saturn", 2461329.5, 10.63021, 1.62740),
        ("Saturn", 2466000.5, 177.14882, 3.56105),
        ("Saturn", 2470000.5, 309.24340, -19.09898),
        ("Mars", 2451545.0, 330.51680, -13.18248),
        ("Mars", 2455362.5, 156.41490, 11.08659),
        ("Mars", 2461329.5, 132.99970, 18.92590),
        ("Mars", 2466000.5, 57.71858, 19.13645),
        ("Mars", 2470000.5, 330.86213, -17.78628),
    };

    /// <summary>Observer site for <see cref="AzEl"/>: geodetic, east-positive longitude, a public place (Midtown Manhattan).</summary>
    public const double SiteLatitude = 40.7484;
    public const double SiteLongitude = -73.9857;

    /// <summary>Topocentric apparent azimuth (from north, through east) and elevation with refraction OFF (Horizons quantity 4, APPARENT=AIRLESS), site 10 m up.</summary>
    public static readonly (string Body, double Jd, double Az, double El)[] AzEl =
    {
        ("Sun", 2455362.5, 297.718528, 3.851298),
        ("Sun", 2461329.75, 34.071514, -53.714420),
        ("Sun", 2461330.25, 204.541836, 36.975193),
        ("Sun", 2466000.5, 293.114954, 1.525376),
        ("Sun", 2470000.5, 295.775705, 3.766970),
        ("Mercury", 2455362.5, 306.849404, -8.518127),
        ("Mercury", 2461329.75, 351.269539, -69.107542),
        ("Mercury", 2461330.25, 176.574955, 29.004968),
        ("Mercury", 2466000.5, 306.519862, -10.491669),
        ("Mercury", 2470000.5, 278.468078, 18.253864),
        ("Venus", 2455362.5, 272.347722, 32.161558),
        ("Venus", 2461329.75, 26.669365, -67.652816),
        ("Venus", 2461330.25, 191.951188, 28.214308),
        ("Venus", 2466000.5, 278.609124, 3.651345),
        ("Venus", 2470000.5, 258.245137, 29.627996),
        ("Mars", 2455362.5, 233.510779, 48.643398),
        ("Mars", 2461329.75, 74.024084, 10.482468),
        ("Mars", 2461330.25, 283.176279, 13.732412),
        ("Mars", 2466000.5, 356.482734, -30.041682),
        ("Mars", 2470000.5, 88.744610, -29.354610),
        ("Jupiter", 2455362.5, 11.863278, -49.320846),
        ("Jupiter", 2461329.75, 69.839020, -0.593760),
        ("Jupiter", 2461330.25, 272.731345, 19.691427),
        ("Jupiter", 2466000.5, 270.356636, 16.201558),
        ("Jupiter", 2470000.5, 280.814790, 15.822778),
        ("Saturn", 2455362.5, 196.127999, 51.141619),
        ("Saturn", 2461329.75, 222.799536, 42.307026),
        ("Saturn", 2461330.25, 41.239264, -39.259307),
        ("Saturn", 2466000.5, 250.084662, 26.647712),
        ("Saturn", 2470000.5, 103.387104, -13.912711),
        ("Moon", 2455362.5, 272.353817, 26.654966),
        ("Moon", 2461329.75, 274.672410, -51.457069),
        ("Moon", 2461330.25, 135.712828, 5.701382),
        ("Moon", 2466000.5, 167.794631, 27.065181),
        ("Moon", 2470000.5, 358.458728, -34.211928),
    };

    /// <summary>
    /// The Moon, geocentric (Horizons 301 from 500@399): ICRF RA/Dec, illuminated percent, sub-Earth selenographic longitude (0..360, east) and latitude,
    /// sub-solar longitude and latitude, position angle of the sub-solar point (degrees from north through east), range in au, and the Sun-Moon-Earth phase angle.
    /// </summary>
    public static readonly (double Jd, double Ra, double Dec, double IlluminatedPercent, double ObsLon, double ObsLat, double SunLon, double SunLat, double BrightLimbPa, double RangeAu, double PhaseAngle)[] Moon =
    {
        (2451545.0, 222.45893, -10.90338, 23.00638, 5.025824, -6.720739, 242.181075, 0.619964, 110.48, 0.00268998925442, 122.6718),
        (2455362.5, 119.27435, 19.26099, 8.51897, 358.379721, 1.791150, 144.513989, 0.522384, 283.40, 0.00244771799710, 146.0644),
        (2461329.5, 262.34744, -27.86390, 25.90018, 0.900111, 6.017046, 119.744001, -1.284516, 275.44, 0.00270138294857, 118.8187),
        (2461345.5, 119.66671, 23.18091, 59.74317, 3.955929, -3.208598, 285.130931, -1.457223, 101.02, 0.00247266123383, 78.7644),
        (2461353.5, 218.85566, -20.33457, 0.26663, 4.228290, 6.403736, 187.608894, -1.537981, 52.60, 0.00265306627924, 174.0771),
        (2466000.5, 245.40652, -20.28246, 74.98924, 357.878826, -1.526031, 57.840256, 1.126769, 280.01, 0.00244785712258, 60.0115),
        (2470000.5, 37.73167, 15.59945, 32.80775, 5.798677, -0.853952, 255.657213, -1.372989, 72.11, 0.00253413789567, 110.1094),
    };

    /// <summary>Degrees in -180..180 for a longitude given 0..360.</summary>
    public static double Signed180(double degrees) => degrees > 180 ? degrees - 360 : degrees;

    /// <summary>Documented accuracy of the JPL element table, 1800-2050 AD, in ecliptic longitude (arcseconds), widened for the Earth's own error and for Mars' distance error at close approach.</summary>
    public static double ToleranceArcsec(string body) => body switch
    {
        "Sun" => 60,
        "Mercury" => 60,
        "Venus" => 80,
        "Mars" => 160,
        "Jupiter" => 800,
        "Saturn" => 1100,
        _ => 100,
    };

    public static SolarSystemObject Object(string body) => Enum.Parse<SolarSystemObject>(body);

    /// <summary>Angular separation in arcseconds between two points given as longitude/latitude pairs in degrees.</summary>
    public static double SeparationArcsec(double lon1, double lat1, double lon2, double lat2)
        => AstronomyMath.AngularSeparation(lon1, lat1, lon2, lat2) * 3600.0;
}
