namespace SpawnDev.NexStar
{
    /// <summary>
    /// Static utility class for lunar calculations using simplified Meeus algorithms
    /// </summary>
    public static class LunarMath
    {
        private const double DEG_TO_RAD = Math.PI / 180.0;
        private const double RAD_TO_DEG = 180.0 / Math.PI;

        /// <summary>
        /// Moon's mean angular size in degrees
        /// </summary>
        public const double MoonAngularSize = 0.5;

        /// <summary>
        /// Calculate the Moon's geocentric position (RA/Dec) for a given UTC time
        /// Using simplified Meeus algorithm
        /// </summary>
        public static RaDecCoordinates GetMoonPosition(DateTime utc)
        {
            double jd = AstronomyMath.DateTimeToJulianDate(utc);
            double T = (jd - 2451545.0) / 36525.0; // Julian centuries from J2000.0

            // Moon's mean longitude
            double L0 = NormalizeAngle(218.3164477 + 481267.88123421 * T
                - 0.0015786 * T * T + T * T * T / 538841.0 - T * T * T * T / 65194000.0);

            // Moon's mean elongation
            double D = NormalizeAngle(297.8501921 + 445267.1114034 * T
                - 0.0018819 * T * T + T * T * T / 545868.0 - T * T * T * T / 113065000.0);

            // Sun's mean anomaly
            double M = NormalizeAngle(357.5291092 + 35999.0502909 * T
                - 0.0001536 * T * T + T * T * T / 24490000.0);

            // Moon's mean anomaly
            double Mprime = NormalizeAngle(134.9633964 + 477198.8675055 * T
                + 0.0087414 * T * T + T * T * T / 69699.0 - T * T * T * T / 14712000.0);

            // Moon's argument of latitude
            double F = NormalizeAngle(93.2720950 + 483202.0175233 * T
                - 0.0036539 * T * T - T * T * T / 3526000.0 + T * T * T * T / 863310000.0);

            // Longitude corrections (simplified - main terms only)
            double sumL = 0;
            sumL += 6288774 * Sin(Mprime);
            sumL += 1274027 * Sin(2 * D - Mprime);
            sumL += 658314 * Sin(2 * D);
            sumL += 213618 * Sin(2 * Mprime);
            sumL += -185116 * Sin(M);
            sumL += -114332 * Sin(2 * F);
            sumL += 58793 * Sin(2 * D - 2 * Mprime);
            sumL += 57066 * Sin(2 * D - M - Mprime);
            sumL += 53322 * Sin(2 * D + Mprime);
            sumL += 45758 * Sin(2 * D - M);
            sumL += -40923 * Sin(M - Mprime);
            sumL += -34720 * Sin(D);
            sumL += -30383 * Sin(M + Mprime);
            sumL += 15327 * Sin(2 * D - 2 * F);
            sumL += -12528 * Sin(Mprime + 2 * F);
            sumL += 10980 * Sin(Mprime - 2 * F);

            // Latitude corrections (simplified)
            double sumB = 0;
            sumB += 5128122 * Sin(F);
            sumB += 280602 * Sin(Mprime + F);
            sumB += 277693 * Sin(Mprime - F);
            sumB += 173237 * Sin(2 * D - F);
            sumB += 55413 * Sin(2 * D - Mprime + F);
            sumB += 46271 * Sin(2 * D - Mprime - F);
            sumB += 32573 * Sin(2 * D + F);
            sumB += 17198 * Sin(2 * Mprime + F);
            sumB += 9266 * Sin(2 * D + Mprime - F);
            sumB += 8822 * Sin(2 * Mprime - F);

            // Ecliptic longitude and latitude
            double lambda = L0 + sumL / 1000000.0;
            double beta = sumB / 1000000.0;

            // Mean obliquity of the ecliptic
            double epsilon = 23.439291 - 0.0130042 * T;

            // Convert ecliptic to equatorial
            double lambdaRad = lambda * DEG_TO_RAD;
            double betaRad = beta * DEG_TO_RAD;
            double epsilonRad = epsilon * DEG_TO_RAD;

            double sinLambda = Math.Sin(lambdaRad);
            double cosLambda = Math.Cos(lambdaRad);
            double sinBeta = Math.Sin(betaRad);
            double cosBeta = Math.Cos(betaRad);
            double sinEpsilon = Math.Sin(epsilonRad);
            double cosEpsilon = Math.Cos(epsilonRad);

            // Right Ascension
            double y = sinLambda * cosEpsilon - Math.Tan(betaRad) * sinEpsilon;
            double x = cosLambda;
            double ra = Math.Atan2(y, x) * RAD_TO_DEG;
            if (ra < 0) ra += 360;

            // Declination
            double sinDec = sinBeta * cosEpsilon + cosBeta * sinEpsilon * sinLambda;
            double dec = Math.Asin(sinDec) * RAD_TO_DEG;

            return new RaDecCoordinates { RightAscension = ra, Declination = dec };
        }

        /// <summary>
        /// Calculate moon phase (0 = new moon, 0.5 = full moon, 1 = new moon again)
        /// </summary>
        public static double GetMoonPhase(DateTime utc)
        {
            double jd = AstronomyMath.DateTimeToJulianDate(utc);
            double T = (jd - 2451545.0) / 36525.0;

            // Moon's mean elongation from Sun
            double D = NormalizeAngle(297.8501921 + 445267.1114034 * T
                - 0.0018819 * T * T + T * T * T / 545868.0);

            // Phase angle (0-360)
            // 0° = new moon, 180° = full moon
            return D / 360.0;
        }

        /// <summary>
        /// Calculate moon illumination percentage (0-100)
        /// </summary>
        public static double GetMoonIllumination(DateTime utc)
        {
            double phase = GetMoonPhase(utc);
            // Illumination follows a cosine curve
            // 0 at new moon (phase=0), 100 at full moon (phase=0.5)
            return (1.0 - Math.Cos(phase * 2 * Math.PI)) / 2.0 * 100.0;
        }

        /// <summary>
        /// Get the name of the current moon phase
        /// </summary>
        public static string GetMoonPhaseName(DateTime utc)
        {
            double phase = GetMoonPhase(utc);

            if (phase < 0.0625 || phase >= 0.9375) return "New Moon";
            if (phase < 0.1875) return "Waxing Crescent";
            if (phase < 0.3125) return "First Quarter";
            if (phase < 0.4375) return "Waxing Gibbous";
            if (phase < 0.5625) return "Full Moon";
            if (phase < 0.6875) return "Waning Gibbous";
            if (phase < 0.8125) return "Last Quarter";
            return "Waning Crescent";
        }

        /// <summary>
        /// Get emoji for current moon phase
        /// </summary>
        public static string GetMoonPhaseEmoji(DateTime utc)
        {
            double phase = GetMoonPhase(utc);

            if (phase < 0.0625 || phase >= 0.9375) return "🌑";
            if (phase < 0.1875) return "🌒";
            if (phase < 0.3125) return "🌓";
            if (phase < 0.4375) return "🌔";
            if (phase < 0.5625) return "🌕";
            if (phase < 0.6875) return "🌖";
            if (phase < 0.8125) return "🌗";
            return "🌘";
        }

        /// <summary>
        /// Calculate approximate moonrise and moonset times for a given date and location
        /// Returns null times if moon doesn't rise/set on that day
        /// </summary>
        public static (DateTime? Rise, DateTime? Set, DateTime? Transit) GetMoonRiseSetTransit(
            double latitude, double longitude, DateTime date)
        {
            // Start at midnight local (approximation - use UTC midnight for simplicity)
            var midnight = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc);

            DateTime? riseTime = null;
            DateTime? setTime = null;
            DateTime? transitTime = null;
            double maxAlt = -90;

            // Sample every 10 minutes to find rise/set crossings
            double? prevAlt = null;
            for (int minutes = 0; minutes <= 24 * 60; minutes += 10)
            {
                var time = midnight.AddMinutes(minutes);
                var moonPos = GetMoonPosition(time);
                var horizPos = AstronomyMath.EquatorialToHorizontal(
                    moonPos.RightAscension, moonPos.Declination,
                    latitude, longitude, time);

                double alt = horizPos.Altitude;

                if (alt > maxAlt)
                {
                    maxAlt = alt;
                    transitTime = time;
                }

                if (prevAlt.HasValue)
                {
                    // Check for horizon crossing
                    if (prevAlt < 0 && alt >= 0 && riseTime == null)
                    {
                        // Interpolate rise time
                        double fraction = -prevAlt.Value / (alt - prevAlt.Value);
                        riseTime = time.AddMinutes(-10 * (1 - fraction));
                    }
                    else if (prevAlt >= 0 && alt < 0 && setTime == null)
                    {
                        // Interpolate set time
                        double fraction = prevAlt.Value / (prevAlt.Value - alt);
                        setTime = time.AddMinutes(-10 * (1 - fraction));
                    }
                }

                prevAlt = alt;
            }

            return (riseTime, setTime, transitTime);
        }

        /// <summary>
        /// Get moon's current horizontal position
        /// </summary>
        public static AzAltCoordinates GetMoonAzAlt(double latitude, double longitude, DateTime utc)
        {
            var moonPos = GetMoonPosition(utc);
            return AstronomyMath.EquatorialToHorizontal(
                moonPos.RightAscension, moonPos.Declination,
                latitude, longitude, utc);
        }

        /// <summary>
        /// Calculate the Moon's libration (apparent wobble)
        /// Returns selenographic longitude and latitude of the sub-Earth point
        /// </summary>
        public static (double LibrationLongitude, double LibrationLatitude) GetLibration(DateTime utc)
        {
            double jd = AstronomyMath.DateTimeToJulianDate(utc);
            double T = (jd - 2451545.0) / 36525.0;

            // Simplified libration calculation
            double F = NormalizeAngle(93.2720950 + 483202.0175233 * T);
            double Omega = NormalizeAngle(125.0445479 - 1934.1362891 * T);

            // Optical libration in longitude (simplified)
            double libLon = 6.29 * Math.Sin(F * DEG_TO_RAD);

            // Optical libration in latitude (simplified)
            double libLat = 6.68 * Math.Sin((F + Omega) * DEG_TO_RAD);

            return (libLon, libLat);
        }

        /// <summary>
        /// Check if a lunar feature is currently visible (rough approximation)
        /// Based on terminator position and libration
        /// </summary>
        public static bool IsFeatureVisible(double featureLon, double featureLat, DateTime utc)
        {
            double phase = GetMoonPhase(utc);
            var (libLon, libLat) = GetLibration(utc);

            // Terminator longitude (where the sun is rising/setting on the moon)
            // At new moon (phase=0), terminator is at 90° (sunrise at eastern limb)
            // At first quarter (phase=0.25), terminator is at 0° (center)
            // At full moon (phase=0.5), terminator is at -90° (sunset at western limb)
            double terminatorLon = 90 - (phase * 360);
            if (terminatorLon < -180) terminatorLon += 360;
            if (terminatorLon > 180) terminatorLon -= 360;

            // Adjust feature position for libration
            double adjustedLon = featureLon - libLon;
            double adjustedLat = featureLat - libLat;

            // Feature is visible if it's on the illuminated side and not too close to limb
            // During waxing (phase < 0.5): features with lon < terminatorLon are visible
            // During waning (phase > 0.5): features with lon > terminatorLon are visible
            bool illuminated;
            if (phase < 0.5)
            {
                illuminated = adjustedLon < terminatorLon + 5; // 5° margin for visibility near terminator
            }
            else
            {
                illuminated = adjustedLon > terminatorLon - 5;
            }

            // Check if not too close to limb (beyond ~85° won't be visible)
            bool notAtLimb = Math.Abs(adjustedLon) < 85 && Math.Abs(adjustedLat) < 85;

            return illuminated && notAtLimb;
        }

        private static double NormalizeAngle(double angle)
        {
            angle = angle % 360.0;
            if (angle < 0) angle += 360.0;
            return angle;
        }

        private static double Sin(double degrees) => Math.Sin(degrees * DEG_TO_RAD);
        private static double Cos(double degrees) => Math.Cos(degrees * DEG_TO_RAD);
    }
}
