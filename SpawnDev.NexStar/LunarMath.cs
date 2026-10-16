namespace SpawnDev.NexStar
{
    /// <summary>
    /// The Moon: position (geocentric and topocentric), phase, libration and what is lit.
    /// <para>
    /// Position uses the full periodic series of Meeus, "Astronomical Algorithms" (2nd ed.), chapter 47 (Tables 47.A and 47.B, 60 terms each,
    /// from the ELP-2000/82 theory), accurate to about 10 arcseconds in longitude and 4 in latitude; the coefficients were read from
    /// the MIT-licensed implementation in "astronomia" (Sonia Keys 2013, commenthol 2016) and are validated here against NASA/JPL Horizons.
    /// Results are for the mean equator and equinox of the date (no nutation), the frame <see cref="AstronomyMath.EquatorialToHorizontal"/> expects.
    /// </para>
    /// <para>
    /// The topocentric position matters: the Moon is close enough that an observer's view differs from the centre of the Earth's by up to
    /// 0.95 degrees, almost two Moon-widths. Pointing at the geocentric position misses the Moon.
    /// </para>
    /// </summary>
    public static class LunarMath
    {
        private const double DEG_TO_RAD = Math.PI / 180.0;
        private const double RAD_TO_DEG = 180.0 / Math.PI;

        /// <summary>
        /// Moon's mean angular size in degrees
        /// </summary>
        public const double MoonAngularSize = 0.5;

        /// <summary>Equatorial radius of the Earth in km, for the parallax.</summary>
        public const double EarthRadiusKm = 6378.137;

        /// <summary>Mean radius of the Moon in km.</summary>
        public const double MoonRadiusKm = 1737.4;

        /// <summary>One astronomical unit in km.</summary>
        public const double KmPerAu = 149597870.7;

        /// <summary>The Moon's geocentric place.</summary>
        /// <param name="Ra">Right ascension of the date, degrees</param>
        /// <param name="Dec">Declination of the date, degrees</param>
        /// <param name="DistanceKm">Distance between the centres of the Earth and the Moon</param>
        /// <param name="EclipticLongitude">Longitude in the mean ecliptic of the date, degrees</param>
        /// <param name="EclipticLatitude">Latitude, degrees</param>
        public readonly record struct MoonPlace(double Ra, double Dec, double DistanceKm, double EclipticLongitude, double EclipticLatitude);

        // Table 47.A: arguments D, M, M', F and the coefficients of the sum of longitude (units of 1e-6 degree) and distance (1e-3 km).
        private static readonly (int D, int M, int Mp, int F, int L, int R)[] TermsLongitudeDistance =
        {
            (0, 0, 1, 0, 6288774, -20905355), (2, 0, -1, 0, 1274027, -3699111), (2, 0, 0, 0, 658314, -2955968), (0, 0, 2, 0, 213618, -569925),
            (0, 1, 0, 0, -185116, 48888), (0, 0, 0, 2, -114332, -3149), (2, 0, -2, 0, 58793, 246158), (2, -1, -1, 0, 57066, -152138),
            (2, 0, 1, 0, 53322, -170733), (2, -1, 0, 0, 45758, -204586), (0, 1, -1, 0, -40923, -129620), (1, 0, 0, 0, -34720, 108743),
            (0, 1, 1, 0, -30383, 104755), (2, 0, 0, -2, 15327, 10321), (0, 0, 1, 2, -12528, 0), (0, 0, 1, -2, 10980, 79661),
            (4, 0, -1, 0, 10675, -34782), (0, 0, 3, 0, 10034, -23210), (4, 0, -2, 0, 8548, -21636), (2, 1, -1, 0, -7888, 24208),
            (2, 1, 0, 0, -6766, 30824), (1, 0, -1, 0, -5163, -8379), (1, 1, 0, 0, 4987, -16675), (2, -1, 1, 0, 4036, -12831),
            (2, 0, 2, 0, 3994, -10445), (4, 0, 0, 0, 3861, -11650), (2, 0, -3, 0, 3665, 14403), (0, 1, -2, 0, -2689, -7003),
            (2, 0, -1, 2, -2602, 0), (2, -1, -2, 0, 2390, 10056), (1, 0, 1, 0, -2348, 6322), (2, -2, 0, 0, 2236, -9884),
            (0, 1, 2, 0, -2120, 5751), (0, 2, 0, 0, -2069, 0), (2, -2, -1, 0, 2048, -4950), (2, 0, 1, -2, -1773, 4130),
            (2, 0, 0, 2, -1595, 0), (4, -1, -1, 0, 1215, -3958), (0, 0, 2, 2, -1110, 0), (3, 0, -1, 0, -892, 3258),
            (2, 1, 1, 0, -810, 2616), (4, -1, -2, 0, 759, -1897), (0, 2, -1, 0, -713, -2117), (2, 2, -1, 0, -700, 2354),
            (2, 1, -2, 0, 691, 0), (2, -1, 0, -2, 596, 0), (4, 0, 1, 0, 549, -1423), (0, 0, 4, 0, 537, -1117),
            (4, -1, 0, 0, 520, -1571), (1, 0, -2, 0, -487, -1739), (2, 1, 0, -2, -399, 0), (0, 0, 2, -2, -381, -4421),
            (1, 1, 1, 0, 351, 0), (3, 0, -2, 0, -340, 0), (4, 0, -3, 0, 330, 0), (2, -1, 2, 0, 327, 0),
            (0, 2, 1, 0, -323, 1165), (1, 1, -1, 0, 299, 0), (2, 0, 3, 0, 294, 0), (2, 0, -1, -2, 0, 8752),
        };

        // Table 47.B: arguments D, M, M', F and the coefficient of the sum of latitude (units of 1e-6 degree).
        private static readonly (int D, int M, int Mp, int F, int B)[] TermsLatitude =
        {
            (0, 0, 0, 1, 5128122), (0, 0, 1, 1, 280602), (0, 0, 1, -1, 277693), (2, 0, 0, -1, 173237),
            (2, 0, -1, 1, 55413), (2, 0, -1, -1, 46271), (2, 0, 0, 1, 32573), (0, 0, 2, 1, 17198),
            (2, 0, 1, -1, 9266), (0, 0, 2, -1, 8822), (2, -1, 0, -1, 8216), (2, 0, -2, -1, 4324),
            (2, 0, 1, 1, 4200), (2, 1, 0, -1, -3359), (2, -1, -1, 1, 2463), (2, -1, 0, 1, 2211),
            (2, -1, -1, -1, 2065), (0, 1, -1, -1, -1870), (4, 0, -1, -1, 1828), (0, 1, 0, 1, -1794),
            (0, 0, 0, 3, -1749), (0, 1, -1, 1, -1565), (1, 0, 0, 1, -1491), (0, 1, 1, 1, -1475),
            (0, 1, 1, -1, -1410), (0, 1, 0, -1, -1344), (1, 0, 0, -1, -1335), (0, 0, 3, 1, 1107),
            (4, 0, 0, -1, 1021), (4, 0, -1, 1, 833), (0, 0, 1, -3, 777), (4, 0, -2, 1, 671),
            (2, 0, 0, -3, 607), (2, 0, 2, -1, 596), (2, -1, 1, -1, 491), (2, 0, -2, 1, -451),
            (0, 0, 3, -1, 439), (2, 0, 2, 1, 422), (2, 0, -3, -1, 421), (2, 1, -1, 1, -366),
            (2, 1, 0, 1, -351), (4, 0, 0, 1, 331), (2, -1, 1, 1, 315), (2, -2, 0, -1, 302),
            (0, 0, 1, 3, -283), (2, 1, 1, -1, -229), (1, 1, 0, -1, 223), (1, 1, 0, 1, 223),
            (0, 1, -2, -1, -220), (2, 1, -1, -1, -220), (1, 0, 1, 1, -185), (2, -1, -2, -1, 181),
            (0, 1, 2, 1, -177), (4, 0, -2, -1, 176), (4, -1, -1, -1, 166), (1, 0, 1, -1, -164),
            (4, 0, 1, -1, 132), (1, 0, -1, -1, -119), (4, -1, 0, -1, 115), (2, -2, 0, 1, 107),
        };

        private static double Poly(double t, double c0, double c1, double c2, double c3, double c4 = 0)
            => c0 + t * (c1 + t * (c2 + t * (c3 + t * c4)));

        /// <summary>
        /// The Moon's geocentric place at <paramref name="utc"/>.
        /// </summary>
        public static MoonPlace GetMoonGeocentric(DateTime utc)
        {
            var T = Precession.CenturiesTT(utc);

            // Fundamental arguments, degrees (Meeus 47.1-47.5)
            double Lp = Poly(T, 218.3164477, 481267.88123421, -0.0015786, 1.0 / 538841, -1.0 / 65194000);
            double D = Poly(T, 297.8501921, 445267.1114034, -0.0018819, 1.0 / 545868, -1.0 / 113065000);
            double M = Poly(T, 357.5291092, 35999.0502909, -0.0001536, 1.0 / 24490000);
            double Mp = Poly(T, 134.9633964, 477198.8675055, 0.0087414, 1.0 / 69699, -1.0 / 14712000);
            double F = Poly(T, 93.2720950, 483202.0175233, -0.0036539, -1.0 / 3526000, 1.0 / 863310000);
            double A1 = 119.75 + 131.849 * T;
            double A2 = 53.09 + 479264.290 * T;
            double A3 = 313.45 + 481266.484 * T;
            double E = 1 - 0.002516 * T - 0.0000074 * T * T; // the Earth's orbital eccentricity changes: terms with M carry E, with 2M carry E squared

            // Additive terms from Venus, Jupiter and the Earth's flattening (Meeus 47)
            double sumL = 3958 * Sin(A1) + 1962 * Sin(Lp - F) + 318 * Sin(A2);
            double sumR = 0;
            double sumB = -2235 * Sin(Lp) + 382 * Sin(A3) + 175 * Sin(A1 - F) + 175 * Sin(A1 + F) + 127 * Sin(Lp - Mp) - 115 * Sin(Lp + Mp);

            foreach (var (d, m, mp, f, l, r) in TermsLongitudeDistance)
            {
                double arg = d * D + m * M + mp * Mp + f * F;
                double e = ETerm(m, E);
                sumL += l * e * Sin(arg);
                sumR += r * e * Cos(arg);
            }
            foreach (var (d, m, mp, f, b) in TermsLatitude)
            {
                double arg = d * D + m * M + mp * Mp + f * F;
                sumB += b * ETerm(m, E) * Sin(arg);
            }

            double lon = NormalizeAngle(Lp + sumL / 1e6);
            double lat = sumB / 1e6;
            double distance = 385000.56 + sumR / 1e3;

            // To the equator of the date with the mean obliquity (Meeus 13.3, 13.4)
            double eps = AstronomyMath.MeanObliquityDegrees(T) * DEG_TO_RAD;
            double lam = lon * DEG_TO_RAD, bet = lat * DEG_TO_RAD;
            double ra = Math.Atan2(Math.Sin(lam) * Math.Cos(eps) - Math.Tan(bet) * Math.Sin(eps), Math.Cos(lam)) * RAD_TO_DEG;
            double dec = Math.Asin(Math.Sin(bet) * Math.Cos(eps) + Math.Cos(bet) * Math.Sin(eps) * Math.Sin(lam)) * RAD_TO_DEG;
            return new MoonPlace(NormalizeAngle(ra), dec, distance, lon, lat);
        }

        private static double ETerm(int m, double e) => Math.Abs(m) switch { 0 => 1.0, 1 => e, _ => e * e };

        /// <summary>
        /// Calculate the Moon's geocentric position (RA/Dec of the date) for a given UTC time. This is the position as seen from the centre of the Earth;
        /// use <see cref="GetMoonTopocentric"/> or <see cref="GetMoonAzAlt"/> for where it is from a place on the surface.
        /// </summary>
        public static RaDecCoordinates GetMoonPosition(DateTime utc)
        {
            var p = GetMoonGeocentric(utc);
            return new RaDecCoordinates { RightAscension = p.Ra, Declination = p.Dec };
        }

        /// <summary>
        /// The Moon's distance from the centre of the Earth in km.
        /// </summary>
        public static double GetMoonDistanceKm(DateTime utc) => GetMoonGeocentric(utc).DistanceKm;

        /// <summary>
        /// The Moon's equatorial horizontal parallax in degrees (the most its position can shift between the Earth's centre and its surface).
        /// </summary>
        public static double GetHorizontalParallaxDegrees(double distanceKm) => Math.Asin(EarthRadiusKm / distanceKm) * RAD_TO_DEG;

        /// <summary>
        /// The Moon's apparent angular radius in degrees.
        /// </summary>
        public static double GetSemiDiameterDegrees(double distanceKm) => Math.Asin(MoonRadiusKm / distanceKm) * RAD_TO_DEG;

        /// <summary>
        /// The Moon's topocentric position (RA/Dec of the date) for an observer: the geocentric position corrected for parallax (Meeus chapter 40).
        /// </summary>
        /// <param name="latitude">Geodetic latitude in degrees</param>
        /// <param name="longitude">Longitude in degrees, east positive</param>
        /// <param name="utc">UTC time</param>
        /// <param name="elevationMeters">Height above sea level</param>
        public static RaDecCoordinates GetMoonTopocentric(double latitude, double longitude, DateTime utc, double elevationMeters = 0)
        {
            var moon = GetMoonGeocentric(utc);
            var (rhoSin, rhoCos) = ObserverGeocentric(latitude, elevationMeters);
            double sinPi = EarthRadiusKm / moon.DistanceKm;
            double h = AstronomyMath.GetHourAngle(moon.Ra, AstronomyMath.GetLocalSiderealTime(longitude, utc)) * DEG_TO_RAD;
            double dec = moon.Dec * DEG_TO_RAD;
            double dRa = Math.Atan2(-rhoCos * sinPi * Math.Sin(h), Math.Cos(dec) - rhoCos * sinPi * Math.Cos(h));
            double decT = Math.Atan2((Math.Sin(dec) - rhoSin * sinPi) * Math.Cos(dRa), Math.Cos(dec) - rhoCos * sinPi * Math.Cos(h));
            return new RaDecCoordinates { RightAscension = NormalizeAngle(moon.Ra + dRa * RAD_TO_DEG), Declination = decT * RAD_TO_DEG };
        }

        /// <summary>The observer's position as rho sin(phi') and rho cos(phi') in Earth radii (Meeus 11.x, with the Earth's flattening).</summary>
        private static (double Sin, double Cos) ObserverGeocentric(double latitudeDeg, double elevationMeters)
        {
            double phi = latitudeDeg * DEG_TO_RAD;
            double u = Math.Atan(0.99664719 * Math.Tan(phi));
            double hr = elevationMeters / 6378140.0;
            return (0.99664719 * Math.Sin(u) + hr * Math.Sin(phi), Math.Cos(u) + hr * Math.Cos(phi));
        }

        /// <summary>
        /// Get moon's current horizontal position as seen from the observer's location: topocentric (parallax included), geometric
        /// (no refraction, see <see cref="AstronomyMath.RefractionDegrees"/>). Azimuth is from north through east.
        /// </summary>
        public static AzAltCoordinates GetMoonAzAlt(double latitude, double longitude, DateTime utc, double elevationMeters = 0)
        {
            var moonPos = GetMoonTopocentric(latitude, longitude, utc, elevationMeters);
            return AstronomyMath.EquatorialToHorizontal(
                moonPos.RightAscension, moonPos.Declination,
                latitude, longitude, utc);
        }

        #region Phase

        /// <summary>
        /// The Moon's phase as a fraction of the synodic month: 0 = new, 0.25 = first quarter, 0.5 = full, 0.75 = last quarter.
        /// It is the Moon's ecliptic longitude minus the Sun's, over 360 degrees.
        /// </summary>
        public static double GetMoonPhase(DateTime utc)
        {
            var moon = GetMoonGeocentric(utc);
            return NormalizeAngle(moon.EclipticLongitude - SunEclipticLongitude(utc)) / 360.0;
        }

        /// <summary>
        /// The percentage of the Moon's disc that is lit (0-100), from the real Sun-Moon-Earth geometry (Meeus chapter 48).
        /// </summary>
        public static double GetMoonIllumination(DateTime utc) => (1 + Math.Cos(GetPhaseAngleDegrees(utc) * DEG_TO_RAD)) / 2 * 100.0;

        /// <summary>
        /// The phase angle in degrees: the angle Earth-Moon-Sun seen from the Moon. 0 at full Moon, 180 at new.
        /// </summary>
        public static double GetPhaseAngleDegrees(DateTime utc)
        {
            var moon = GetMoonGeocentric(utc);
            var sun = SolarSystemMath.GetPosition(SolarSystemObject.Sun, utc);
            double sunKm = SolarSystemMath.GetDistanceAu(SolarSystemObject.Sun, utc) * KmPerAu;
            double psi = AstronomyMath.AngularSeparation(moon.Ra, moon.Dec, sun.RightAscension, sun.Declination) * DEG_TO_RAD;
            return Math.Atan2(sunKm * Math.Sin(psi), moon.DistanceKm - sunKm * Math.Cos(psi)) * RAD_TO_DEG;
        }

        /// <summary>
        /// The position angle of the Moon's bright limb in degrees, from celestial north through east (Meeus 48.5): the direction on the
        /// disc toward the Sun. Needed to draw the Moon with its terminator the right way up.
        /// </summary>
        public static double GetBrightLimbPositionAngle(DateTime utc)
        {
            var moon = GetMoonGeocentric(utc);
            var sun = SolarSystemMath.GetPosition(SolarSystemObject.Sun, utc);
            double a0 = sun.RightAscension * DEG_TO_RAD, d0 = sun.Declination * DEG_TO_RAD;
            double a = moon.Ra * DEG_TO_RAD, d = moon.Dec * DEG_TO_RAD;
            double chi = Math.Atan2(Math.Cos(d0) * Math.Sin(a0 - a), Math.Sin(d0) * Math.Cos(d) - Math.Cos(d0) * Math.Sin(d) * Math.Cos(a0 - a));
            return NormalizeAngle(chi * RAD_TO_DEG);
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

        private static double SunEclipticLongitude(DateTime utc)
        {
            var sun = SolarSystemMath.GetPosition(SolarSystemObject.Sun, utc);
            double eps = AstronomyMath.MeanObliquityDegrees(Precession.CenturiesTT(utc)) * DEG_TO_RAD;
            double a = sun.RightAscension * DEG_TO_RAD, d = sun.Declination * DEG_TO_RAD;
            return NormalizeAngle(Math.Atan2(Math.Sin(a) * Math.Cos(eps) + Math.Tan(d) * Math.Sin(eps), Math.Cos(a)) * RAD_TO_DEG);
        }

        #endregion

        #region Rise and set

        /// <summary>
        /// Calculate moonrise, moonset and transit times (UTC) for the UTC day containing <paramref name="date"/> at a location.
        /// Rise and set are when the upper limb meets the horizon with standard refraction: the topocentric centre at
        /// minus (34 arcminutes + the Moon's semi-diameter). A time is null when the Moon does not do that on that UTC day.
        /// Sampled every 10 minutes with linear interpolation, good to about a minute.
        /// </summary>
        public static (DateTime? Rise, DateTime? Set, DateTime? Transit) GetMoonRiseSetTransit(
            double latitude, double longitude, DateTime date)
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
                var alt = GetMoonAzAlt(latitude, longitude, time).Altitude;
                if (alt > maxAlt)
                {
                    maxAlt = alt;
                    transitTime = time;
                }

                // altitude relative to the rise/set line
                var line = -(34.0 / 60.0 + GetSemiDiameterDegrees(GetMoonDistanceKm(time)));
                var rel = alt - line;
                if (prevAlt.HasValue)
                {
                    if (prevAlt < 0 && rel >= 0 && riseTime == null)
                    {
                        double fraction = -prevAlt.Value / (rel - prevAlt.Value);
                        riseTime = time.AddMinutes(-10 * (1 - fraction));
                    }
                    else if (prevAlt >= 0 && rel < 0 && setTime == null)
                    {
                        double fraction = prevAlt.Value / (prevAlt.Value - rel);
                        setTime = time.AddMinutes(-10 * (1 - fraction));
                    }
                }
                prevAlt = rel;
            }

            return (riseTime, setTime, transitTime);
        }

        #endregion

        #region Libration and the lit surface

        /// <summary>Inclination of the mean lunar equator to the ecliptic, degrees (Meeus 53).</summary>
        private const double LunarEquatorInclination = 1.54242;

        /// <summary>
        /// The selenographic longitude (east positive, -180..180) and latitude of the point on the Moon that the Earth is directly over:
        /// the optical libration (Meeus chapter 53). The physical libration, a wobble of about 0.04 degree, is not included.
        /// </summary>
        public static (double LibrationLongitude, double LibrationLatitude) GetLibration(DateTime utc)
        {
            var moon = GetMoonGeocentric(utc);
            return SelenographicPoint(moon.EclipticLongitude, moon.EclipticLatitude, Precession.CenturiesTT(utc));
        }

        /// <summary>
        /// The selenographic longitude (east positive, -180..180) and latitude of the sub-solar point: where the Sun is directly overhead on the Moon.
        /// </summary>
        public static (double Longitude, double Latitude) GetSubSolarPoint(DateTime utc)
        {
            var moon = GetMoonGeocentric(utc);
            var earth = SolarSystemMath.EarthHeliocentricEclipticOfDate(utc);
            double lam = moon.EclipticLongitude * DEG_TO_RAD, bet = moon.EclipticLatitude * DEG_TO_RAD;
            double r = moon.DistanceKm / KmPerAu;
            // the Moon as seen from the Sun: the Earth's heliocentric position plus the Moon's geocentric one
            double x = earth.X + r * Math.Cos(bet) * Math.Cos(lam);
            double y = earth.Y + r * Math.Cos(bet) * Math.Sin(lam);
            double z = earth.Z + r * Math.Sin(bet);
            double lamH = Math.Atan2(y, x) * RAD_TO_DEG;
            double betH = Math.Atan2(z, Math.Sqrt(x * x + y * y)) * RAD_TO_DEG;
            return SelenographicPoint(lamH, betH, Precession.CenturiesTT(utc));
        }

        /// <summary>
        /// The selenographic colongitude in degrees: the longitude of the sunrise terminator on the Moon (0 at first quarter, so it increases through the lunar day).
        /// </summary>
        public static double GetColongitude(DateTime utc)
        {
            var (l0, _) = GetSubSolarPoint(utc);
            return NormalizeAngle(90.0 - l0);
        }

        /// <summary>
        /// Meeus 53: the selenographic coordinates of the point of the Moon directly under an observer in the direction of
        /// ecliptic longitude/latitude (the Moon as seen from that observer: the Earth, or the Sun using heliocentric coordinates).
        /// </summary>
        private static (double Lon, double Lat) SelenographicPoint(double lonEclipticDeg, double latEclipticDeg, double T)
        {
            double omega = Poly(T, 125.0445479, -1934.1362891, 0.0020754, 1.0 / 467441, -1.0 / 60616000); // mean ascending node of the lunar orbit
            double F = Poly(T, 93.2720950, 483202.0175233, -0.0036539, -1.0 / 3526000, 1.0 / 863310000);
            double I = LunarEquatorInclination * DEG_TO_RAD;
            double W = (lonEclipticDeg - omega) * DEG_TO_RAD;
            double beta = latEclipticDeg * DEG_TO_RAD;
            double A = Math.Atan2(Math.Sin(W) * Math.Cos(beta) * Math.Cos(I) - Math.Sin(beta) * Math.Sin(I), Math.Cos(W) * Math.Cos(beta)) * RAD_TO_DEG;
            double lon = A - NormalizeAngle(F);
            lon = NormalizeAngle(lon);
            if (lon > 180) lon -= 360;
            double lat = Math.Asin(Math.Max(-1, Math.Min(1, -Math.Sin(W) * Math.Cos(beta) * Math.Sin(I) - Math.Sin(beta) * Math.Cos(I)))) * RAD_TO_DEG;
            return (lon, lat);
        }

        /// <summary>
        /// The Sun's elevation in degrees at a point on the Moon's surface (selenographic longitude east positive, latitude). Positive means the point
        /// is in sunlight; near zero it is on the terminator, where relief casts the long shadows that make craters worth looking at.
        /// </summary>
        public static double GetSunElevationAtFeature(double featureLon, double featureLat, DateTime utc)
        {
            var (l0, b0) = GetSubSolarPoint(utc);
            double lat = featureLat * DEG_TO_RAD, b = b0 * DEG_TO_RAD;
            double sinH = Math.Sin(b) * Math.Sin(lat) + Math.Cos(b) * Math.Cos(lat) * Math.Cos((l0 - featureLon) * DEG_TO_RAD);
            return Math.Asin(Math.Max(-1, Math.Min(1, sinH))) * RAD_TO_DEG;
        }

        /// <summary>
        /// Whether a point on the Moon can be seen from the Earth right now: on the near side as the libration presents it
        /// (within 85 degrees of the sub-Earth point, to leave out what is foreshortened to nothing at the limb).
        /// </summary>
        public static bool IsOnEarthFacingSide(double featureLon, double featureLat, DateTime utc)
        {
            var (lE, bE) = GetLibration(utc);
            double lat = featureLat * DEG_TO_RAD, b = bE * DEG_TO_RAD;
            double cosC = Math.Sin(b) * Math.Sin(lat) + Math.Cos(b) * Math.Cos(lat) * Math.Cos((lE - featureLon) * DEG_TO_RAD);
            return cosC > Math.Cos(85.0 * DEG_TO_RAD);
        }

        /// <summary>
        /// Check if a lunar feature is currently visible: in sunlight and facing the Earth.
        /// </summary>
        public static bool IsFeatureVisible(double featureLon, double featureLat, DateTime utc)
            => IsOnEarthFacingSide(featureLon, featureLat, utc) && GetSunElevationAtFeature(featureLon, featureLat, utc) > 0;

        #endregion

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
