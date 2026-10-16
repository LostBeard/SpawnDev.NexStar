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
        Saturn = 5,
        Uranus = 6,
        Neptune = 7
    }

    /// <summary>
    /// Planetary positions and rise/set calculations from the JPL "Keplerian Elements for Approximate Positions of the Major Planets"
    /// (E. M. Standish, Table 1, valid 1800 AD to 2050 AD, elements and rates referred to the mean ecliptic and equinox of J2000).
    /// Documented accuracy of that table in ecliptic longitude: Mercury 15 arcseconds, Venus 20, Earth 20, Mars 40, Jupiter 400,
    /// Saturn 600, Uranus 50, Neptune 10. That is far inside an eyepiece field, so it suits finding and pointing, not occultation timing.
    /// Positions are geocentric and astrometric (light-time corrected); nutation (under 17 arcseconds) and aberration (20 arcseconds)
    /// are not applied.
    /// </summary>
    public static class SolarSystemMath
    {
        private const double DEG_TO_RAD = Math.PI / 180.0;
        private const double RAD_TO_DEG = 180.0 / Math.PI;

        /// <summary>Days for light to cross one astronomical unit.</summary>
        private const double LightDaysPerAu = 0.0057755183;

        /// <summary>Mean obliquity of the ecliptic at J2000.0 in degrees (the frame rotation from the J2000 ecliptic to the J2000 equator).</summary>
        private const double ObliquityJ2000 = 23.43928;

        // Table 1: a (au), e (rad), I (deg), L (deg), long.peri (deg), long.node (deg), then each one's rate per Julian century.
        private readonly record struct Elements(
            double A, double ADot, double E, double EDot, double I, double IDot,
            double L, double LDot, double Peri, double PeriDot, double Node, double NodeDot);

        private static readonly Elements[] PlanetElements =
        {
            default, // Sun is -(Earth/Moon barycenter), below
            new(0.38709927, 0.00000037, 0.20563593, 0.00001906, 7.00497902, -0.00594749, 252.25032350, 149472.67411175, 77.45779628, 0.16047689, 48.33076593, -0.12534081),   // Mercury
            new(0.72333566, 0.00000390, 0.00677672, -0.00004107, 3.39467605, -0.00078890, 181.97909950, 58517.81538729, 131.60246718, 0.00268329, 76.67984255, -0.27769418), // Venus
            new(1.52371034, 0.00001847, 0.09339410, 0.00007882, 1.84969142, -0.00813131, -4.55343205, 19140.30268499, -23.94362959, 0.44441088, 49.55953891, -0.29257343),  // Mars
            new(5.20288700, -0.00011607, 0.04838624, -0.00013253, 1.30439695, -0.00183714, 34.39644051, 3034.74612775, 14.72847983, 0.21252668, 100.47390909, 0.20469106),   // Jupiter
            new(9.53667594, -0.00125060, 0.05386179, -0.00050991, 2.48599187, 0.00193609, 49.95424423, 1222.49362201, 92.59887831, -0.41897216, 113.66242448, -0.28867794),  // Saturn
            new(19.18916464, -0.00196176, 0.04725744, -0.00004397, 0.77263783, -0.00242939, 313.23810451, 428.48202785, 170.95427630, 0.40805281, 74.01692503, 0.04240589), // Uranus
            new(30.06992276, 0.00026291, 0.00859048, 0.00005105, 1.77004347, 0.00035372, -55.12002969, 218.45945325, 44.96476227, -0.32241464, 131.78422574, -0.00508664), // Neptune
        };

        private static readonly Elements EarthMoonBarycenter =
            new(1.00000261, 0.00000562, 0.01671123, -0.00004392, -0.00001531, -0.01294668, 100.46457166, 35999.37244981, 102.93768193, 0.32327364, 0.0, 0.0);

        /// <summary>
        /// Display name for each solar system object.
        /// </summary>
        public static string GetName(SolarSystemObject obj) => obj switch
        {
            SolarSystemObject.Sun => "Sun",
            SolarSystemObject.Mercury => "Mercury",
            SolarSystemObject.Venus => "Venus",
            SolarSystemObject.Mars => "Mars",
            SolarSystemObject.Jupiter => "Jupiter",
            SolarSystemObject.Saturn => "Saturn",
            SolarSystemObject.Uranus => "Uranus",
            SolarSystemObject.Neptune => "Neptune",
            _ => "Unknown"
        };

        /// <summary>
        /// Astronomical symbol for each solar system object.
        /// </summary>
        public static string GetSymbol(SolarSystemObject obj) => obj switch
        {
            SolarSystemObject.Sun => "☉",
            SolarSystemObject.Mercury => "☿",
            SolarSystemObject.Venus => "♀",
            SolarSystemObject.Mars => "♂",
            SolarSystemObject.Jupiter => "♃",
            SolarSystemObject.Saturn => "♄",
            SolarSystemObject.Uranus => "♅",
            SolarSystemObject.Neptune => "♆",
            _ => "•"
        };

        /// <summary>
        /// The planets: everything a telescope can be sent to. The Sun is deliberately not in this list. Pointing a telescope at the Sun
        /// without a solar filter destroys optics, cameras and eyes, so the Sun must never be a goto target of this library or an app built on it.
        /// <see cref="GetPosition"/> still gives the Sun's position, to avoid it and for twilight and display.
        /// </summary>
        public static IReadOnlyList<SolarSystemObject> Planets { get; } = new[]
        {
            SolarSystemObject.Mercury,
            SolarSystemObject.Venus,
            SolarSystemObject.Mars,
            SolarSystemObject.Jupiter,
            SolarSystemObject.Saturn,
            SolarSystemObject.Uranus,
            SolarSystemObject.Neptune
        };

        /// <summary>
        /// Geocentric astrometric position (RA/Dec of the mean equator and equinox of the date at <paramref name="utc"/>,
        /// which is what hour angle and altitude are computed from). Use <see cref="GetPositionJ2000"/> for catalog-style J2000 coordinates.
        /// </summary>
        public static RaDecCoordinates GetPosition(SolarSystemObject obj, DateTime utc)
        {
            var j2000 = GetPositionJ2000(obj, utc);
            var (ra, dec) = Precession.J2000ToDate(j2000.RightAscension, j2000.Declination, utc);
            return new RaDecCoordinates(ra, dec);
        }

        /// <summary>
        /// Geocentric astrometric position in the J2000.0 equatorial frame, light-time corrected.
        /// </summary>
        public static RaDecCoordinates GetPositionJ2000(SolarSystemObject obj, DateTime utc)
        {
            var t = Precession.CenturiesTT(utc);
            var earth = HeliocentricJ2000(EarthMoonBarycenter, t);
            Vec3 geo;
            if (obj == SolarSystemObject.Sun)
            {
                geo = -earth;
                // light left the Sun about 8 minutes ago, but the Sun does not move in this model: nothing to correct
            }
            else
            {
                var elements = PlanetElements[(int)obj];
                geo = HeliocentricJ2000(elements, t) - earth;
                // light-time: where the planet was when the light we see now left it (twice is converged to well under an arcsecond)
                for (int i = 0; i < 2; i++)
                {
                    var tau = geo.Length * LightDaysPerAu / 36525.0;
                    geo = HeliocentricJ2000(elements, t - tau) - earth;
                }
            }
            var ra = Math.Atan2(geo.Y, geo.X) * RAD_TO_DEG;
            if (ra < 0) ra += 360.0;
            var dec = Math.Atan2(geo.Z, Math.Sqrt(geo.X * geo.X + geo.Y * geo.Y)) * RAD_TO_DEG;
            return new RaDecCoordinates(ra, dec);
        }

        /// <summary>
        /// Distance from the Earth in astronomical units.
        /// </summary>
        public static double GetDistanceAu(SolarSystemObject obj, DateTime utc)
        {
            var t = Precession.CenturiesTT(utc);
            var earth = HeliocentricJ2000(EarthMoonBarycenter, t);
            return obj == SolarSystemObject.Sun
                ? earth.Length
                : (HeliocentricJ2000(PlanetElements[(int)obj], t) - earth).Length;
        }

        /// <summary>
        /// The Earth-Moon barycenter's heliocentric position in au in the ecliptic of the date (mean equinox of the date), the frame the
        /// lunar series and the lunar libration formulas use. The barycenter is 4,700 km from the Earth, 0.00003 au: the Sun's direction from
        /// the Moon is unaffected at the arcsecond level.
        /// </summary>
        internal static (double X, double Y, double Z) EarthHeliocentricEclipticOfDate(DateTime utc)
        {
            var t = Precession.CenturiesTT(utc);
            var v = HeliocentricJ2000(EarthMoonBarycenter, t);      // J2000 equatorial
            var p = Precession.J2000ToDateMatrix(t);                 // to the equator and equinox of the date
            double x = p[0, 0] * v.X + p[0, 1] * v.Y + p[0, 2] * v.Z;
            double y = p[1, 0] * v.X + p[1, 1] * v.Y + p[1, 2] * v.Z;
            double z = p[2, 0] * v.X + p[2, 1] * v.Y + p[2, 2] * v.Z;
            var eps = AstronomyMath.MeanObliquityDegrees(t) * DEG_TO_RAD; // to the ecliptic of the date
            return (x, Math.Cos(eps) * y + Math.Sin(eps) * z, -Math.Sin(eps) * y + Math.Cos(eps) * z);
        }

        private readonly record struct Vec3(double X, double Y, double Z)
        {
            public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
            public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
            public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);
        }

        /// <summary>Heliocentric position (au) in the J2000 equatorial frame, from the JPL procedure.</summary>
        private static Vec3 HeliocentricJ2000(Elements el, double t)
        {
            var a = el.A + el.ADot * t;
            var e = el.E + el.EDot * t;
            var inc = (el.I + el.IDot * t) * DEG_TO_RAD;
            var L = el.L + el.LDot * t;
            var peri = el.Peri + el.PeriDot * t;
            var node = (el.Node + el.NodeDot * t) * DEG_TO_RAD;

            var omega = (peri - (el.Node + el.NodeDot * t)) * DEG_TO_RAD; // argument of perihelion
            var M = Wrap180(L - peri) * DEG_TO_RAD;                        // mean anomaly

            // Kepler's equation M = E - e sin E, by Newton's method (converges in a few steps for the eccentricities here, all under 0.21)
            var E = M + e * Math.Sin(M);
            for (int i = 0; i < 30; i++)
            {
                var dE = (M - (E - e * Math.Sin(E))) / (1 - e * Math.Cos(E));
                E += dE;
                if (Math.Abs(dE) < 1e-12) break;
            }

            // position in the orbital plane, x' toward perihelion
            var xp = a * (Math.Cos(E) - e);
            var yp = a * Math.Sqrt(1 - e * e) * Math.Sin(E);

            double cw = Math.Cos(omega), sw = Math.Sin(omega);
            double cn = Math.Cos(node), sn = Math.Sin(node);
            double ci = Math.Cos(inc), si = Math.Sin(inc);

            // J2000 ecliptic frame
            var xe = (cw * cn - sw * sn * ci) * xp + (-sw * cn - cw * sn * ci) * yp;
            var ye = (cw * sn + sw * cn * ci) * xp + (-sw * sn + cw * cn * ci) * yp;
            var ze = (sw * si) * xp + (cw * si) * yp;

            // J2000 equatorial frame
            var eps = ObliquityJ2000 * DEG_TO_RAD;
            return new Vec3(xe, Math.Cos(eps) * ye - Math.Sin(eps) * ze, Math.Sin(eps) * ye + Math.Cos(eps) * ze);
        }

        private static double Wrap180(double degrees)
        {
            degrees %= 360.0;
            if (degrees > 180) degrees -= 360;
            if (degrees < -180) degrees += 360;
            return degrees;
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
    }
}
