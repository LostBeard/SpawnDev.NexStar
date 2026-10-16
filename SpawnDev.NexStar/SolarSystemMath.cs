namespace SpawnDev.NexStar
{
    /// <summary>
    /// Solar system object identifier for position and visibility calculations.
    /// </summary>
    public enum SolarSystemObject
    {
        Sun = 0,
        Mercury = 1,
        Venus = 2,
        Mars = 3,
        Jupiter = 4,
        Saturn = 5
    }

    /// <summary>
    /// Simplified planetary position and rise/set calculations using orbital elements.
    /// Suitable for visibility and "when can I see it" estimates from the observer's location.
    /// </summary>
    public static class SolarSystemMath
    {
        private const double DEG_TO_RAD = Math.PI / 180.0;
        private const double RAD_TO_DEG = 180.0 / Math.PI;
        private const double J2000 = 2451545.0;

        // Orbital elements at J2000.0: L0 (mean longitude deg), n (deg/day), e, pi (longitude of perihelion deg), a (AU)
        // Sources: Meeus-style simplified elements
        private static readonly (double L0, double n, double e, double pi, double a)[] Elements = new[]
        {
            (280.46646, 0.98564736, 0.01670862, 282.940, 1.0),       // Sun (Earth's orbit)
            (252.25084, 4.09233487, 0.20563593, 77.457, 0.387098),   // Mercury
            (181.97980, 1.60213035, 0.00677192, 131.603, 0.723332),  // Venus
            (355.43327, 0.52403269, 0.09340065, 336.060, 1.523679), // Mars
            (34.35152, 0.08309121, 0.04839266, 14.332, 5.203363),    // Jupiter
            (50.07747, 0.03345973, 0.05550862, 92.834, 9.537070)    // Saturn
        };

        /// <summary>
        /// Display name for each solar system object.
        /// </summary>
        public static string GetName(SolarSystemObject obj)
        {
            return obj switch
            {
                SolarSystemObject.Sun => "Sun",
                SolarSystemObject.Mercury => "Mercury",
                SolarSystemObject.Venus => "Venus",
                SolarSystemObject.Mars => "Mars",
                SolarSystemObject.Jupiter => "Jupiter",
                SolarSystemObject.Saturn => "Saturn",
                _ => "Unknown"
            };
        }

        /// <summary>
        /// Emoji/symbol for each solar system object.
        /// </summary>
        public static string GetSymbol(SolarSystemObject obj)
        {
            return obj switch
            {
                SolarSystemObject.Sun => "☀️",
                SolarSystemObject.Mercury => "☿",
                SolarSystemObject.Venus => "♀",
                SolarSystemObject.Mars => "♂",
                SolarSystemObject.Jupiter => "♃",
                SolarSystemObject.Saturn => "♄",
                _ => "•"
            };
        }

        /// <summary>
        /// All trackable solar system objects (excluding Sun by default for safety; include Sun when needed for visibility).
        /// </summary>
        public static IReadOnlyList<SolarSystemObject> Planets { get; } = new[]
        {
            SolarSystemObject.Mercury,
            SolarSystemObject.Venus,
            SolarSystemObject.Mars,
            SolarSystemObject.Jupiter,
            SolarSystemObject.Saturn
        };

        /// <summary>
        /// Geocentric position (RA/Dec) of the given solar system object at UTC.
        /// </summary>
        public static RaDecCoordinates GetPosition(SolarSystemObject obj, DateTime utc)
        {
            double jd = AstronomyMath.DateTimeToJulianDate(utc);
            double d = jd - J2000;
            double T = d / 36525.0;

            int idx = (int)obj;
            var (L0, n, e, pi, a) = Elements[idx];

            // Mean longitude and mean anomaly (all in degrees)
            double L_mean = Normalize(L0 + n * d);
            double M = Normalize(L_mean - pi);

            // Equation of center (degrees)
            double C = (2 * e - e * e * e / 4) * Sin(M) + (5 * e * e / 4) * Sin(2 * M);
            double L_true = L_mean + C;

            // Heliocentric distance (AU) and position in ecliptic plane (Sin/Cos take degrees)
            double r = a * (1 - e * e) / (1 + e * Cos(M));
            double x_helio = r * Cos(L_true);
            double y_helio = r * Sin(L_true);

            double x_geo;
            double y_geo;

            if (obj == SolarSystemObject.Sun)
            {
                // Geocentric Sun = opposite of Earth position; Earth at (x_e, y_e) so Sun at (-x_e, -y_e)
                // We computed Sun's orbit as Earth's orbit, so (x_helio, y_helio) is Earth's position
                x_geo = -x_helio;
                y_geo = -y_helio;
                return EclipticToEquatorial(x_geo, y_geo, T);
            }

            // Earth's position (index 0 = Sun orbit = Earth)
            var (L0e, ne, ee, pie, _) = Elements[0];
            double L_mean_e = Normalize(L0e + ne * d);
            double Me = Normalize(L_mean_e - pie);
            double Ce = (2 * ee - ee * ee * ee / 4) * Sin(Me) + (5 * ee * ee / 4) * Sin(2 * Me);
            double L_true_e = L_mean_e + Ce;
            double re = 1.0 * (1 - ee * ee) / (1 + ee * Cos(Me));
            double x_earth = re * Cos(L_true_e);
            double y_earth = re * Sin(L_true_e);

            // Geocentric position
            x_geo = x_helio - x_earth;
            y_geo = y_helio - y_earth;

            return EclipticToEquatorial(x_geo, y_geo, T);
        }

        private static RaDecCoordinates EclipticToEquatorial(double x, double y, double T)
        {
            double lambda = Math.Atan2(y, x) * RAD_TO_DEG;
            if (lambda < 0) lambda += 360;
            double beta = 0; // Simplified: all in ecliptic plane

            double epsilon = (23.439291 - 0.0130042 * T) * DEG_TO_RAD;
            double lambdaRad = lambda * DEG_TO_RAD;
            double betaRad = beta * DEG_TO_RAD;

            double sinLambda = Math.Sin(lambdaRad);
            double cosLambda = Math.Cos(lambdaRad);
            double sinBeta = Math.Sin(betaRad);
            double cosBeta = Math.Cos(betaRad);
            double sinEps = Math.Sin(epsilon);
            double cosEps = Math.Cos(epsilon);

            double ra = Math.Atan2(sinLambda * cosEps - Math.Tan(betaRad) * sinEps, cosLambda) * RAD_TO_DEG;
            if (ra < 0) ra += 360;

            double sinDec = sinBeta * cosEps + cosBeta * sinEps * sinLambda;
            double dec = Math.Asin(sinDec) * RAD_TO_DEG;

            return new RaDecCoordinates(ra, dec);
        }

        /// <summary>
        /// Current horizontal position (altitude/azimuth) for the given object at the observer's location.
        /// </summary>
        public static AzAltCoordinates GetAzAlt(SolarSystemObject obj, double latitude, double longitude, DateTime utc)
        {
            var pos = GetPosition(obj, utc);
            return AstronomyMath.EquatorialToHorizontal(
                pos.RightAscension, pos.Declination,
                latitude, longitude, utc);
        }

        /// <summary>
        /// Rise, set, and transit times for the given object on the given date at the observer's location.
        /// Uses 10-minute sampling; returns null when the object does not rise or set (e.g. circumpolar or never up).
        /// </summary>
        public static (DateTime? Rise, DateTime? Set, DateTime? Transit) GetRiseSetTransit(
            SolarSystemObject obj, double latitude, double longitude, DateTime date)
        {
            var midnight = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc);

            DateTime? riseTime = null;
            DateTime? setTime = null;
            DateTime? transitTime = null;
            double maxAlt = -90;

            double? prevAlt = null;
            for (int minutes = 0; minutes <= 24 * 60; minutes += 10)
            {
                var time = midnight.AddMinutes(minutes);
                var horizPos = GetAzAlt(obj, latitude, longitude, time);
                double alt = horizPos.Altitude;

                if (alt > maxAlt)
                {
                    maxAlt = alt;
                    transitTime = time;
                }

                if (prevAlt.HasValue)
                {
                    if (prevAlt < 0 && alt >= 0 && riseTime == null)
                    {
                        double fraction = -prevAlt.Value / (alt - prevAlt.Value);
                        riseTime = time.AddMinutes(-10 * (1 - fraction));
                    }
                    else if (prevAlt >= 0 && alt < 0 && setTime == null)
                    {
                        double fraction = prevAlt.Value / (prevAlt.Value - alt);
                        setTime = time.AddMinutes(-10 * (1 - fraction));
                    }
                }

                prevAlt = alt;
            }

            return (riseTime, setTime, transitTime);
        }

        private static double Normalize(double angle)
        {
            angle = angle % 360.0;
            if (angle < 0) angle += 360.0;
            return angle;
        }

        private static double Sin(double degrees) => Math.Sin(degrees * DEG_TO_RAD);
        private static double Cos(double degrees) => Math.Cos(degrees * DEG_TO_RAD);
    }
}
