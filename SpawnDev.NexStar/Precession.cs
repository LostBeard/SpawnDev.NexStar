namespace SpawnDev.NexStar
{
    /// <summary>
    /// Time scales and the rotation between the J2000.0 equatorial frame (what every star and deep-sky catalog uses)
    /// and the equator and equinox of a date (what sidereal time and hour angle belong to).
    /// A catalog position that skips this is off by about 0.3 degrees in 2026, and it grows by 50 arcseconds a year.
    /// </summary>
    public static class Precession
    {
        private const double DegToRad = Math.PI / 180.0;
        private const double ArcsecToRad = Math.PI / (180.0 * 3600.0);

        /// <summary>Julian date of J2000.0.</summary>
        public const double J2000 = 2451545.0;

        /// <summary>
        /// TT minus UT in seconds (the Espenak and Meeus polynomial fits, good to about a second for 1986 to 2050 and a few
        /// seconds out to 2150; the long-term parabola outside that range). Needed because planets move: Mercury moves 12 arcseconds
        /// in the 69 seconds between UT and TT today.
        /// </summary>
        public static double DeltaTSeconds(double decimalYear)
        {
            double y = decimalYear;
            if (y >= 2005 && y < 2050)
            {
                var t = y - 2000;
                return 62.92 + 0.32217 * t + 0.005589 * t * t;
            }
            if (y >= 1986 && y < 2005)
            {
                var t = y - 2000;
                return 63.86 + 0.3345 * t - 0.060374 * t * t + 0.0017275 * t * t * t + 0.000651814 * t * t * t * t + 0.00002373599 * t * t * t * t * t;
            }
            if (y >= 2050 && y <= 2150)
            {
                return -20 + 32 * Math.Pow((y - 1820) / 100.0, 2) - 0.5628 * (2150 - y);
            }
            var u = (y - 1820) / 100.0;
            return -20 + 32 * u * u;
        }

        /// <summary>Julian date (TT) for a UTC time: UTC plus the leap-second-era TT offset (ΔT less UT1-UTC, which is under a second).</summary>
        public static double JulianDateTT(DateTime utc)
        {
            var u = Utc(utc);
            var jd = AstronomyMath.DateTimeToJulianDate(u);
            var year = u.Year + (u.DayOfYear - 0.5) / (DateTime.IsLeapYear(u.Year) ? 366.0 : 365.0);
            return jd + DeltaTSeconds(year) / 86400.0;
        }

        private static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t;

        /// <summary>Julian centuries of TT since J2000.0 for a UTC time.</summary>
        public static double CenturiesTT(DateTime utc) => (JulianDateTT(utc) - J2000) / 36525.0;

        /// <summary>
        /// The rotation (row-major 3x3) taking a J2000.0 equatorial vector to the mean equator and equinox of the date
        /// <paramref name="centuriesSinceJ2000"/> Julian centuries after J2000.0 (IAU 1976 precession angles).
        /// </summary>
        public static double[,] J2000ToDateMatrix(double centuriesSinceJ2000)
        {
            var t = centuriesSinceJ2000;
            var zeta = (2306.2181 * t + 0.30188 * t * t + 0.017998 * t * t * t) * ArcsecToRad;
            var z = (2306.2181 * t + 1.09468 * t * t + 0.018203 * t * t * t) * ArcsecToRad;
            var theta = (2004.3109 * t - 0.42665 * t * t - 0.041833 * t * t * t) * ArcsecToRad;
            double cz = Math.Cos(zeta), sz = Math.Sin(zeta);
            double cZ = Math.Cos(z), sZ = Math.Sin(z);
            double ct = Math.Cos(theta), st = Math.Sin(theta);
            return new[,]
            {
                { cZ * ct * cz - sZ * sz, -cZ * ct * sz - sZ * cz, -cZ * st },
                { sZ * ct * cz + cZ * sz, -sZ * ct * sz + cZ * cz, -sZ * st },
                { st * cz,                -st * sz,                ct       },
            };
        }

        /// <summary>
        /// Converts J2000.0 coordinates (degrees) to the mean equator and equinox of the date at <paramref name="utc"/>.
        /// </summary>
        public static (double Ra, double Dec) J2000ToDate(double raDeg, double decDeg, DateTime utc)
            => Rotate(J2000ToDateMatrix(CenturiesTT(utc)), raDeg, decDeg, transpose: false);

        /// <summary>
        /// Converts coordinates of the date at <paramref name="utc"/> back to J2000.0 (degrees).
        /// </summary>
        public static (double Ra, double Dec) DateToJ2000(double raDeg, double decDeg, DateTime utc)
            => Rotate(J2000ToDateMatrix(CenturiesTT(utc)), raDeg, decDeg, transpose: true);

        private static (double Ra, double Dec) Rotate(double[,] m, double raDeg, double decDeg, bool transpose)
        {
            double ra = raDeg * DegToRad, dec = decDeg * DegToRad;
            double x = Math.Cos(dec) * Math.Cos(ra), y = Math.Cos(dec) * Math.Sin(ra), s = Math.Sin(dec);
            double X, Y, Z;
            if (!transpose)
            {
                X = m[0, 0] * x + m[0, 1] * y + m[0, 2] * s;
                Y = m[1, 0] * x + m[1, 1] * y + m[1, 2] * s;
                Z = m[2, 0] * x + m[2, 1] * y + m[2, 2] * s;
            }
            else
            {
                X = m[0, 0] * x + m[1, 0] * y + m[2, 0] * s;
                Y = m[0, 1] * x + m[1, 1] * y + m[2, 1] * s;
                Z = m[0, 2] * x + m[1, 2] * y + m[2, 2] * s;
            }
            var outRa = Math.Atan2(Y, X) / DegToRad;
            if (outRa < 0) outRa += 360.0;
            var outDec = Math.Atan2(Z, Math.Sqrt(X * X + Y * Y)) / DegToRad; // stable at the poles, unlike asin
            return (outRa, outDec);
        }
    }
}
