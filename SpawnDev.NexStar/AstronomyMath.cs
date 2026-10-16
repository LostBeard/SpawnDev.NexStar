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
            int y = utc.Year;
            int m = utc.Month;
            double d = utc.Day + utc.Hour / 24.0 + utc.Minute / 1440.0 + utc.Second / 86400.0;

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
        /// Convert equatorial coordinates (RA/Dec) to horizontal coordinates (Alt/Az)
        /// </summary>
        /// <param name="ra">Right Ascension in degrees</param>
        /// <param name="dec">Declination in degrees</param>
        /// <param name="latitude">Observer latitude in degrees</param>
        /// <param name="longitude">Observer longitude in degrees (East positive)</param>
        /// <param name="utc">UTC time</param>
        /// <returns>Altitude and Azimuth in degrees</returns>
        public static AzAltCoordinates EquatorialToHorizontal(
            double ra, double dec, double latitude, double longitude, DateTime utc)
        {
            double lst = GetLocalSiderealTime(longitude, utc);
            double ha = GetHourAngle(ra, lst);

            // Convert to radians
            double haRad = ha * DEG_TO_RAD;
            double decRad = dec * DEG_TO_RAD;
            double latRad = latitude * DEG_TO_RAD;

            // Calculate altitude
            double sinAlt = Math.Sin(decRad) * Math.Sin(latRad) +
                           Math.Cos(decRad) * Math.Cos(latRad) * Math.Cos(haRad);
            double altitude = Math.Asin(sinAlt) * RAD_TO_DEG;

            // Calculate azimuth
            double cosAz = (Math.Sin(decRad) - Math.Sin(latRad) * sinAlt) /
                          (Math.Cos(latRad) * Math.Cos(altitude * DEG_TO_RAD));

            // Clamp to avoid NaN from floating point errors
            cosAz = Math.Max(-1, Math.Min(1, cosAz));

            double azimuth = Math.Acos(cosAz) * RAD_TO_DEG;

            // Adjust azimuth based on hour angle
            if (Math.Sin(haRad) > 0)
            {
                azimuth = 360 - azimuth;
            }

            return new AzAltCoordinates { Azimuth = azimuth, Altitude = altitude };
        }

        /// <summary>
        /// Convert horizontal coordinates (Alt/Az) to equatorial coordinates (RA/Dec)
        /// </summary>
        public static RaDecCoordinates HorizontalToEquatorial(
            double az, double alt, double latitude, double longitude, DateTime utc)
        {
            double lst = GetLocalSiderealTime(longitude, utc);

            // Convert to radians
            double azRad = az * DEG_TO_RAD;
            double altRad = alt * DEG_TO_RAD;
            double latRad = latitude * DEG_TO_RAD;

            // Calculate declination
            double sinDec = Math.Sin(altRad) * Math.Sin(latRad) +
                           Math.Cos(altRad) * Math.Cos(latRad) * Math.Cos(azRad);
            double dec = Math.Asin(sinDec) * RAD_TO_DEG;

            // Calculate hour angle
            double cosHa = (Math.Sin(altRad) - Math.Sin(latRad) * sinDec) /
                          (Math.Cos(latRad) * Math.Cos(dec * DEG_TO_RAD));
            cosHa = Math.Max(-1, Math.Min(1, cosHa));
            double ha = Math.Acos(cosHa) * RAD_TO_DEG;

            if (Math.Sin(azRad) > 0)
            {
                ha = 360 - ha;
            }

            // Calculate RA from hour angle
            double ra = lst - ha;
            while (ra < 0) ra += 360;
            while (ra >= 360) ra -= 360;

            return new RaDecCoordinates { RightAscension = ra, Declination = dec };
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
            double ra1Rad = ra1 * DEG_TO_RAD;
            double dec1Rad = dec1 * DEG_TO_RAD;
            double ra2Rad = ra2 * DEG_TO_RAD;
            double dec2Rad = dec2 * DEG_TO_RAD;

            double cosSep = Math.Sin(dec1Rad) * Math.Sin(dec2Rad) +
                           Math.Cos(dec1Rad) * Math.Cos(dec2Rad) * Math.Cos(ra1Rad - ra2Rad);

            cosSep = Math.Max(-1, Math.Min(1, cosSep));
            return Math.Acos(cosSep) * RAD_TO_DEG;
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
