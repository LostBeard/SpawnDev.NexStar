namespace SpawnDev.NexStar
{
    /// <summary>
    /// Static utility class for astronomical calculations
    /// </summary>
    public static class AstronomyMath
    {
        private const double DEG_TO_RAD = Math.PI / 180.0;
        private const double RAD_TO_DEG = 180.0 / Math.PI;
        private const double HOURS_TO_DEG = 15.0;

        /// <summary>
        /// Convert Right Ascension from hours to degrees
        /// </summary>
        public static double RaHoursToDegrees(double raHours) => raHours * HOURS_TO_DEG;

        /// <summary>
        /// Convert Right Ascension from degrees to hours
        /// </summary>
        public static double RaDegreesToHours(double raDeg) => raDeg / HOURS_TO_DEG;

        /// <summary>
        /// Calculate Julian Date from DateTime (UTC)
        /// </summary>
        public static double DateTimeToJulianDate(DateTime utc)
        {
            // A Local time is converted; Unspecified is taken as UTC (callers pass UTC). Milliseconds count: Mercury moves
            // an arcsecond in a few seconds, and a sub-second sidereal-time error is 4 arcseconds.
            if (utc.Kind == DateTimeKind.Local) utc = utc.ToUniversalTime();
            int y = utc.Year;
            int m = utc.Month;
            double d = utc.Day + utc.TimeOfDay.TotalDays;

            if (m <= 2)
            {
                y -= 1;
                m += 12;
            }

            int a = y / 100;
            int b = 2 - a + a / 4;

            return Math.Floor(365.25 * (y + 4716)) + Math.Floor(30.6001 * (m + 1)) + d + b - 1524.5;
        }

        /// <summary>
        /// Calculate Greenwich Mean Sidereal Time in degrees
        /// </summary>
        public static double GetGreenwichSiderealTime(DateTime utc)
        {
            double jd = DateTimeToJulianDate(utc);
            double t = (jd - 2451545.0) / 36525.0;

            // GMST in degrees
            double gmst = 280.46061837 + 360.98564736629 * (jd - 2451545.0) +
                          0.000387933 * t * t - t * t * t / 38710000.0;

            // Normalize to 0-360
            gmst = gmst % 360.0;
            if (gmst < 0) gmst += 360.0;

            return gmst;
        }

        /// <summary>
        /// Calculate Local Sidereal Time in degrees
        /// </summary>
        /// <param name="longitude">Observer longitude in degrees (East positive)</param>
        /// <param name="utc">UTC time</param>
        public static double GetLocalSiderealTime(double longitude, DateTime utc)
        {
            double gmst = GetGreenwichSiderealTime(utc);
            double lst = gmst + longitude;

            // Normalize to 0-360
            lst = lst % 360.0;
            if (lst < 0) lst += 360.0;

            return lst;
        }

        /// <summary>
        /// Calculate Hour Angle in degrees
        /// </summary>
        /// <param name="ra">Right Ascension in degrees</param>
        /// <param name="lst">Local Sidereal Time in degrees</param>
        public static double GetHourAngle(double ra, double lst)
        {
            double ha = lst - ra;
            // Normalize to -180 to +180
            while (ha > 180) ha -= 360;
            while (ha < -180) ha += 360;
            return ha;
        }

        /// <summary>
        /// Convert equatorial coordinates (RA/Dec) to horizontal coordinates (Alt/Az).
        /// The coordinates must be of the DATE (mean equator and equinox at <paramref name="utc"/>), as for planets and the Moon
        /// from this library. Catalog stars and deep-sky objects are J2000: use <see cref="J2000ToHorizontal"/>, or they are
        /// about 0.3 degrees off today. No atmospheric refraction is applied (see <see cref="RefractionDegrees"/>).
        /// </summary>
        /// <param name="ra">Right Ascension in degrees</param>
        /// <param name="dec">Declination in degrees</param>
        /// <param name="latitude">Observer latitude in degrees</param>
        /// <param name="longitude">Observer longitude in degrees (East positive)</param>
        /// <param name="utc">UTC time</param>
        /// <returns>Altitude and Azimuth in degrees; azimuth is from north through east (0 = N, 90 = E), 0 at the exact zenith</returns>
        public static AzAltCoordinates EquatorialToHorizontal(
            double ra, double dec, double latitude, double longitude, DateTime utc)
        {
            double lst = GetLocalSiderealTime(longitude, utc);
            double ha = GetHourAngle(ra, lst);

            // Convert to radians
            double haRad = ha * DEG_TO_RAD;
            double decRad = dec * DEG_TO_RAD;
            double latRad = latitude * DEG_TO_RAD;

            // Azimuth from the south (Meeus 13.5, both terms multiplied by cos(dec) so nothing is divided): atan2 has no
            // 0/0 at the zenith or a pole, unlike the acos form, and no precision cliff near due north or south.
            // y and x are cos(alt) times sin and cos of that azimuth, so hypot(x, y) is cos(alt).
            double y = Math.Sin(haRad) * Math.Cos(decRad);
            double x = Math.Cos(haRad) * Math.Cos(decRad) * Math.Sin(latRad) - Math.Sin(decRad) * Math.Cos(latRad);
            double azimuth = (Math.Abs(x) < 1e-15 && Math.Abs(y) < 1e-15)
                ? 0
                : NormalizeAngle(Math.Atan2(y, x) * RAD_TO_DEG + 180.0);

            // Altitude by atan2(sin, cos): asin loses digits near the zenith (an error of about 2e-8 radians at 90 degrees)
            double sinAlt = Math.Sin(decRad) * Math.Sin(latRad) +
                           Math.Cos(decRad) * Math.Cos(latRad) * Math.Cos(haRad);
            double altitude = Math.Atan2(sinAlt, Math.Sqrt(x * x + y * y)) * RAD_TO_DEG;

            return new AzAltCoordinates { Azimuth = azimuth, Altitude = altitude };
        }

        /// <summary>
        /// Convert J2000.0 equatorial coordinates (every star and deep-sky catalog) to horizontal coordinates:
        /// precesses to the mean equator and equinox of <paramref name="utc"/>, then <see cref="EquatorialToHorizontal"/>.
        /// </summary>
        public static AzAltCoordinates J2000ToHorizontal(
            double raJ2000, double decJ2000, double latitude, double longitude, DateTime utc)
        {
            var (ra, dec) = Precession.J2000ToDate(raJ2000, decJ2000, utc);
            return EquatorialToHorizontal(ra, dec, latitude, longitude, utc);
        }

        /// <summary>
        /// Convert horizontal coordinates (Alt/Az) to equatorial coordinates (RA/Dec) of the date. The inverse of <see cref="EquatorialToHorizontal"/>.
        /// </summary>
        public static RaDecCoordinates HorizontalToEquatorial(
            double az, double alt, double latitude, double longitude, DateTime utc)
        {
            double lst = GetLocalSiderealTime(longitude, utc);

            // Convert to radians
            double azRad = az * DEG_TO_RAD;
            double altRad = alt * DEG_TO_RAD;
            double latRad = latitude * DEG_TO_RAD;

            // Declination
            double sinDec = Math.Sin(altRad) * Math.Sin(latRad) +
                           Math.Cos(altRad) * Math.Cos(latRad) * Math.Cos(azRad);
            double dec = Math.Asin(Math.Max(-1, Math.Min(1, sinDec))) * RAD_TO_DEG;

            // Hour angle by atan2, with the azimuth taken from the south (A_S = A_N - 180): no division, so no NaN at a pole or the zenith
            double azSouth = azRad - Math.PI;
            double y = Math.Sin(azSouth) * Math.Cos(altRad);
            double x = Math.Cos(azSouth) * Math.Cos(altRad) * Math.Sin(latRad) + Math.Sin(altRad) * Math.Cos(latRad);
            double ha = (Math.Abs(x) < 1e-15 && Math.Abs(y) < 1e-15) ? 0 : Math.Atan2(y, x) * RAD_TO_DEG;

            // RA from hour angle
            double ra = NormalizeAngle(lst - ha);

            return new RaDecCoordinates { RightAscension = ra, Declination = dec };
        }

        /// <summary>
        /// Mean obliquity of the ecliptic in degrees (Meeus 22.2, IAU 1976) for <paramref name="centuriesSinceJ2000"/> Julian centuries of TT since J2000.0.
        /// </summary>
        public static double MeanObliquityDegrees(double centuriesSinceJ2000)
        {
            var t = centuriesSinceJ2000;
            return 23.0 + 26.0 / 60.0 + 21.448 / 3600.0 - (46.8150 * t + 0.00059 * t * t - 0.001813 * t * t * t) / 3600.0;
        }

        /// <summary>
        /// Atmospheric refraction in degrees for a geometric (airless) altitude, Saemundsson's formula (Meeus 16.4)
        /// for the standard 1010 hPa and 10 degrees C, scaled for pressure and temperature. Accurate to a few arcseconds above 5 degrees;
        /// zero at and below -1 degrees (where the formula is meaningless).
        /// </summary>
        /// <param name="geometricAltitudeDeg">Altitude of the object without the atmosphere</param>
        /// <param name="pressureHpa">Station pressure in hectopascals</param>
        /// <param name="temperatureC">Air temperature in degrees Celsius</param>
        public static double RefractionDegrees(double geometricAltitudeDeg, double pressureHpa = 1010.0, double temperatureC = 10.0)
        {
            if (geometricAltitudeDeg <= -1.0) return 0;
            var h = Math.Max(geometricAltitudeDeg, -0.9);
            var arcmin = 1.02 / Math.Tan((h + 10.3 / (h + 5.11)) * DEG_TO_RAD);
            return Math.Max(0.0, arcmin / 60.0 * (pressureHpa / 1010.0) * (283.0 / (273.0 + temperatureC)));
        }

        /// <summary>
        /// Check if an object is above the horizon
        /// </summary>
        /// <param name="altitude">Altitude in degrees</param>
        /// <param name="minAltitude">Minimum altitude to consider "visible" (default 0)</param>
        public static bool IsAboveHorizon(double altitude, double minAltitude = 0)
        {
            return altitude > minAltitude;
        }

        /// <summary>
        /// Calculate angular separation between two points on the celestial sphere
        /// </summary>
        /// <param name="ra1">Right Ascension of first point (degrees)</param>
        /// <param name="dec1">Declination of first point (degrees)</param>
        /// <param name="ra2">Right Ascension of second point (degrees)</param>
        /// <param name="dec2">Declination of second point (degrees)</param>
        /// <returns>Angular separation in degrees</returns>
        public static double AngularSeparation(double ra1, double dec1, double ra2, double dec2)
        {
            double dec1Rad = dec1 * DEG_TO_RAD;
            double dec2Rad = dec2 * DEG_TO_RAD;
            double dRa = (ra2 - ra1) * DEG_TO_RAD;

            // The Vincenty form: atan2 of the cross and dot products. acos(dot) cannot resolve a separation under about
            // 0.003 arcseconds-worth of cosine (it is 1 - tiny), and fails the same way near 180 degrees.
            double sinD1 = Math.Sin(dec1Rad), cosD1 = Math.Cos(dec1Rad);
            double sinD2 = Math.Sin(dec2Rad), cosD2 = Math.Cos(dec2Rad);
            double a = cosD2 * Math.Sin(dRa);
            double b = cosD1 * sinD2 - sinD1 * cosD2 * Math.Cos(dRa);
            double dot = sinD1 * sinD2 + cosD1 * cosD2 * Math.Cos(dRa);
            return Math.Atan2(Math.Sqrt(a * a + b * b), dot) * RAD_TO_DEG;
        }

        /// <summary>
        /// Normalize angle to 0-360 degrees
        /// </summary>
        public static double NormalizeAngle(double angle)
        {
            angle = angle % 360.0;
            if (angle < 0) angle += 360.0;
            return angle;
        }

        /// <summary>
        /// Format degrees as degrees, arcminutes, arcseconds
        /// </summary>
        public static string FormatDMS(double degrees)
        {
            string sign = degrees >= 0 ? "" : "-";
            degrees = Math.Abs(degrees);
            int d = (int)degrees;
            int m = (int)((degrees - d) * 60);
            double s = ((degrees - d) * 60 - m) * 60;
            return $"{sign}{d}° {m:00}' {s:00.0}\"";
        }

        /// <summary>
        /// Format hours as hours, minutes, seconds
        /// </summary>
        public static string FormatHMS(double hours)
        {
            int h = (int)hours;
            int m = (int)((hours - h) * 60);
            double s = ((hours - h) * 60 - m) * 60;
            return $"{h:00}h {m:00}m {s:00.0}s";
        }

        /// <summary>
        /// Calculate distance in Kilometers between two points on Earth using the Haversine formula approximation.
        /// </summary>
        public static double CalculateDistanceKm(double lat1, double lon1, double lat2, double lon2)
        {
            // Use AngularSeparation for central arc angle
            // Note: AngularSeparation expects (ra, dec). We map (lon, lat) to (ra, dec).
            double degrees = AngularSeparation(lon1, lat1, lon2, lat2);
            // Earth radius approx 6371km. Circumference = 2 * PI * R.
            // Distance = (degrees / 360) * 40075 km approx.
            // Or degrees * (PI / 180) * 6371;
            return degrees * DEG_TO_RAD * 6371.0;
        }
    }
}
