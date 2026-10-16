namespace SpawnDev.NexStar
{
    /// <summary>
    /// Static catalogs of celestial objects for telescope targeting and alignment
    /// </summary>
    public static class CelestialCatalogs
    {
        private static List<MessierObject>? _messier;
        private static List<Star>? _alignmentStars;

        /// <summary>
        /// Complete Messier catalog (110 objects)
        /// </summary>
        public static IReadOnlyList<MessierObject> Messier => _messier ??= InitializeMessier();

        /// <summary>
        /// Bright alignment stars (magnitude less than 2.0)
        /// </summary>
        public static IReadOnlyList<Star> AlignmentStars => _alignmentStars ??= InitializeAlignmentStars();

        private static List<MessierObject> InitializeMessier()
        {
            // Format: M#, Name, RA(deg), Dec(deg), Mag, Type, Constellation, NGC#
            return new List<MessierObject>
            {
                M(1, "Crab Nebula", 83.63, 22.01, 8.4, "Supernova Remnant", "Tau", 1952),
                M(2, "", 323.36, -0.82, 6.3, "Globular Cluster", "Aqr", 7089),
                M(3, "", 205.55, 28.38, 6.2, "Globular Cluster", "CVn", 5272),
                M(4, "", 245.90, -26.53, 5.6, "Globular Cluster", "Sco", 6121),
                M(5, "", 229.64, 2.08, 5.6, "Globular Cluster", "Ser", 5904),
                M(6, "Butterfly Cluster", 265.07, -32.22, 4.2, "Open Cluster", "Sco", 6405),
                M(7, "Ptolemy Cluster", 268.47, -34.79, 3.3, "Open Cluster", "Sco", 6475),
                M(8, "Lagoon Nebula", 271.10, -24.38, 6.0, "Nebula", "Sgr", 6523),
                M(9, "", 259.80, -18.52, 7.7, "Globular Cluster", "Oph", 6333),
                M(10, "", 254.29, -4.10, 6.6, "Globular Cluster", "Oph", 6254),
                M(11, "Wild Duck Cluster", 282.77, -6.27, 6.3, "Open Cluster", "Sct", 6705),
                M(12, "", 251.81, -1.95, 6.7, "Globular Cluster", "Oph", 6218),
                M(13, "Hercules Cluster", 250.42, 36.46, 5.8, "Globular Cluster", "Her", 6205),
                M(14, "", 264.40, -3.25, 7.6, "Globular Cluster", "Oph", 6402),
                M(15, "", 322.49, 12.17, 6.2, "Globular Cluster", "Peg", 7078),
                M(16, "Eagle Nebula", 274.70, -13.79, 6.4, "Nebula", "Ser", 6611),
                M(17, "Omega Nebula", 275.20, -16.17, 7.0, "Nebula", "Sgr", 6618),
                M(18, "", 274.84, -17.13, 7.5, "Open Cluster", "Sgr", 6613),
                M(19, "", 255.66, -26.27, 6.8, "Globular Cluster", "Oph", 6273),
                M(20, "Trifid Nebula", 270.63, -23.03, 9.0, "Nebula", "Sgr", 6514),
                M(21, "", 271.04, -22.49, 6.5, "Open Cluster", "Sgr", 6531),
                M(22, "", 279.10, -23.90, 5.1, "Globular Cluster", "Sgr", 6656),
                M(23, "", 269.27, -19.02, 6.9, "Open Cluster", "Sgr", 6494),
                M(24, "Sagittarius Star Cloud", 274.53, -18.52, 4.6, "Star Cloud", "Sgr", null),
                M(25, "", 277.93, -19.12, 6.5, "Open Cluster", "Sgr", null),
                M(26, "", 281.32, -9.39, 8.0, "Open Cluster", "Sct", 6694),
                M(27, "Dumbbell Nebula", 299.90, 22.72, 7.4, "Planetary Nebula", "Vul", 6853),
                M(28, "", 276.14, -24.87, 6.8, "Globular Cluster", "Sgr", 6626),
                M(29, "", 305.97, 38.51, 7.1, "Open Cluster", "Cyg", 6913),
                M(30, "", 325.09, -23.18, 7.2, "Globular Cluster", "Cap", 7099),
                M(31, "Andromeda Galaxy", 10.68, 41.27, 3.4, "Galaxy", "And", 224),
                M(32, "", 10.67, 40.87, 8.1, "Galaxy", "And", 221),
                M(33, "Triangulum Galaxy", 23.46, 30.66, 5.7, "Galaxy", "Tri", 598),
                M(34, "", 40.52, 42.78, 5.5, "Open Cluster", "Per", 1039),
                M(35, "", 92.25, 24.33, 5.3, "Open Cluster", "Gem", 2168),
                M(36, "", 84.07, 34.14, 6.3, "Open Cluster", "Aur", 1960),
                M(37, "", 88.07, 32.55, 6.2, "Open Cluster", "Aur", 2099),
                M(38, "", 82.17, 35.85, 7.4, "Open Cluster", "Aur", 1912),
                M(39, "", 323.07, 48.44, 5.2, "Open Cluster", "Cyg", 7092),
                M(40, "Winnecke 4", 185.55, 58.08, 8.4, "Double Star", "UMa", null),
                M(41, "", 101.50, -20.76, 4.5, "Open Cluster", "CMa", 2287),
                M(42, "Orion Nebula", 83.82, -5.39, 4.0, "Nebula", "Ori", 1976),
                M(43, "De Mairan's Nebula", 83.88, -5.27, 9.0, "Nebula", "Ori", 1982),
                M(44, "Beehive Cluster", 130.10, 19.67, 3.7, "Open Cluster", "Cnc", 2632),
                M(45, "Pleiades", 56.87, 24.12, 1.6, "Open Cluster", "Tau", null),
                M(46, "", 115.44, -14.82, 6.1, "Open Cluster", "Pup", 2437),
                M(47, "", 114.15, -14.49, 4.2, "Open Cluster", "Pup", 2422),
                M(48, "", 123.43, -5.73, 5.5, "Open Cluster", "Hya", 2548),
                M(49, "", 187.44, 8.00, 8.4, "Galaxy", "Vir", 4472),
                M(50, "", 105.69, -8.34, 5.9, "Open Cluster", "Mon", 2323),
                M(51, "Whirlpool Galaxy", 202.47, 47.20, 8.4, "Galaxy", "CVn", 5194),
                M(52, "", 351.20, 61.59, 7.3, "Open Cluster", "Cas", 7654),
                M(53, "", 198.23, 18.17, 7.6, "Globular Cluster", "Com", 5024),
                M(54, "", 283.76, -30.48, 7.6, "Globular Cluster", "Sgr", 6715),
                M(55, "", 294.99, -30.96, 6.3, "Globular Cluster", "Sgr", 6809),
                M(56, "", 289.15, 30.18, 8.3, "Globular Cluster", "Lyr", 6779),
                M(57, "Ring Nebula", 283.40, 33.03, 8.8, "Planetary Nebula", "Lyr", 6720),
                M(58, "", 189.43, 11.82, 9.7, "Galaxy", "Vir", 4579),
                M(59, "", 190.51, 11.65, 9.6, "Galaxy", "Vir", 4621),
                M(60, "", 190.92, 11.55, 8.8, "Galaxy", "Vir", 4649),
                M(61, "", 185.48, 4.47, 9.7, "Galaxy", "Vir", 4303),
                M(62, "", 255.30, -30.11, 6.5, "Globular Cluster", "Oph", 6266),
                M(63, "Sunflower Galaxy", 198.96, 42.03, 8.6, "Galaxy", "CVn", 5055),
                M(64, "Black Eye Galaxy", 194.18, 21.68, 8.5, "Galaxy", "Com", 4826),
                M(65, "", 169.73, 13.09, 9.3, "Galaxy", "Leo", 3623),
                M(66, "", 170.06, 12.99, 8.9, "Galaxy", "Leo", 3627),
                M(67, "", 132.85, 11.81, 6.1, "Open Cluster", "Cnc", 2682),
                M(68, "", 189.87, -26.74, 7.8, "Globular Cluster", "Hya", 4590),
                M(69, "", 277.85, -32.35, 7.6, "Globular Cluster", "Sgr", 6637),
                M(70, "", 280.80, -32.29, 7.9, "Globular Cluster", "Sgr", 6681),
                M(71, "", 298.44, 18.78, 8.2, "Globular Cluster", "Sge", 6838),
                M(72, "", 313.37, -12.54, 9.3, "Globular Cluster", "Aqr", 6981),
                M(73, "", 314.75, -12.63, 9.0, "Asterism", "Aqr", 6994),
                M(74, "", 24.17, 15.78, 9.4, "Galaxy", "Psc", 628),
                M(75, "", 301.52, -21.92, 8.5, "Globular Cluster", "Sgr", 6864),
                M(76, "Little Dumbbell", 25.58, 51.58, 10.1, "Planetary Nebula", "Per", 650),
                M(77, "", 40.67, -0.01, 8.9, "Galaxy", "Cet", 1068),
                M(78, "", 86.69, 0.05, 8.3, "Nebula", "Ori", 2068),
                M(79, "", 81.04, -24.52, 7.7, "Globular Cluster", "Lep", 1904),
                M(80, "", 244.26, -22.98, 7.3, "Globular Cluster", "Sco", 6093),
                M(81, "Bode's Galaxy", 148.89, 69.07, 6.9, "Galaxy", "UMa", 3031),
                M(82, "Cigar Galaxy", 148.97, 69.68, 8.4, "Galaxy", "UMa", 3034),
                M(83, "Southern Pinwheel", 204.25, -29.87, 7.6, "Galaxy", "Hya", 5236),
                M(84, "", 186.27, 12.89, 9.1, "Galaxy", "Vir", 4374),
                M(85, "", 186.35, 18.19, 9.1, "Galaxy", "Com", 4382),
                M(86, "", 186.55, 12.95, 8.9, "Galaxy", "Vir", 4406),
                M(87, "Virgo A", 187.71, 12.39, 8.6, "Galaxy", "Vir", 4486),
                M(88, "", 187.99, 14.42, 9.6, "Galaxy", "Com", 4501),
                M(89, "", 188.92, 12.56, 9.8, "Galaxy", "Vir", 4552),
                M(90, "", 189.21, 13.16, 9.5, "Galaxy", "Vir", 4569),
                M(91, "", 188.86, 14.50, 10.2, "Galaxy", "Com", 4548),
                M(92, "", 259.28, 43.14, 6.4, "Globular Cluster", "Her", 6341),
                M(93, "", 116.13, -23.86, 6.0, "Open Cluster", "Pup", 2447),
                M(94, "", 192.72, 41.12, 8.2, "Galaxy", "CVn", 4736),
                M(95, "", 160.99, 11.70, 9.7, "Galaxy", "Leo", 3351),
                M(96, "", 161.69, 11.82, 9.2, "Galaxy", "Leo", 3368),
                M(97, "Owl Nebula", 168.70, 55.02, 9.9, "Planetary Nebula", "UMa", 3587),
                M(98, "", 183.45, 14.90, 10.1, "Galaxy", "Com", 4192),
                M(99, "", 184.71, 14.42, 9.9, "Galaxy", "Com", 4254),
                M(100, "", 185.73, 15.82, 9.3, "Galaxy", "Com", 4321),
                M(101, "Pinwheel Galaxy", 210.80, 54.35, 7.9, "Galaxy", "UMa", 5457),
                M(102, "", 226.62, 55.76, 9.9, "Galaxy", "Dra", 5866),
                M(103, "", 23.34, 60.70, 7.4, "Open Cluster", "Cas", 581),
                M(104, "Sombrero Galaxy", 190.00, -11.62, 8.0, "Galaxy", "Vir", 4594),
                M(105, "", 161.96, 12.58, 9.3, "Galaxy", "Leo", 3379),
                M(106, "", 184.74, 47.30, 8.4, "Galaxy", "CVn", 4258),
                M(107, "", 248.13, -13.05, 7.9, "Globular Cluster", "Oph", 6171),
                M(108, "", 167.88, 55.67, 10.0, "Galaxy", "UMa", 3556),
                M(109, "", 179.40, 53.37, 9.8, "Galaxy", "UMa", 3992),
                M(110, "", 10.09, 41.68, 8.5, "Galaxy", "And", 205),
            };
        }

        private static MessierObject M(int num, string name, double ra, double dec, double mag, string type, string con, int? ngc)
        {
            return new MessierObject
            {
                MessierNumber = num,
                Id = $"M{num}",
                Name = name,
                RightAscension = ra,
                Declination = dec,
                Magnitude = mag,
                ObjectType = type,
                Constellation = con,
                NgcNumber = ngc
            };
        }

        private static List<Star> InitializeAlignmentStars()
        {
            // Brightest stars for alignment (mag < 2.0)
            // Format: Name, Designation, RA(deg), Dec(deg), Mag, Constellation, HIP
            return new List<Star>
            {
                S("Sirius", "Alpha CMa", 101.29, -16.72, -1.46, "CMa", 32349),
                S("Canopus", "Alpha Car", 95.99, -52.70, -0.74, "Car", 30438),
                S("Arcturus", "Alpha Boo", 213.92, 19.18, -0.05, "Boo", 69673),
                S("Vega", "Alpha Lyr", 279.23, 38.78, 0.03, "Lyr", 91262),
                S("Capella", "Alpha Aur", 79.17, 45.99, 0.08, "Aur", 24608),
                S("Rigel", "Beta Ori", 78.63, -8.20, 0.13, "Ori", 24436),
                S("Procyon", "Alpha CMi", 114.83, 5.22, 0.34, "CMi", 37279),
                S("Betelgeuse", "Alpha Ori", 88.79, 7.41, 0.42, "Ori", 27989),
                S("Achernar", "Alpha Eri", 24.43, -57.24, 0.46, "Eri", 7588),
                S("Hadar", "Beta Cen", 210.96, -60.37, 0.61, "Cen", 68702),
                S("Altair", "Alpha Aql", 297.70, 8.87, 0.76, "Aql", 97649),
                S("Acrux", "Alpha Cru", 186.65, -63.10, 0.76, "Cru", 60718),
                S("Aldebaran", "Alpha Tau", 68.98, 16.51, 0.85, "Tau", 21421),
                S("Antares", "Alpha Sco", 247.35, -26.43, 0.96, "Sco", 80763),
                S("Spica", "Alpha Vir", 201.30, -11.16, 0.97, "Vir", 65474),
                S("Pollux", "Beta Gem", 116.33, 28.03, 1.14, "Gem", 37826),
                S("Fomalhaut", "Alpha PsA", 344.41, -29.62, 1.16, "PsA", 113368),
                S("Deneb", "Alpha Cyg", 310.36, 45.28, 1.25, "Cyg", 102098),
                S("Mimosa", "Beta Cru", 191.93, -59.69, 1.25, "Cru", 62434),
                S("Regulus", "Alpha Leo", 152.09, 11.97, 1.35, "Leo", 49669),
                S("Adhara", "Epsilon CMa", 104.66, -28.97, 1.50, "CMa", 33579),
                S("Castor", "Alpha Gem", 113.65, 31.89, 1.57, "Gem", 36850),
                S("Shaula", "Lambda Sco", 263.40, -37.10, 1.63, "Sco", 85927),
                S("Bellatrix", "Gamma Ori", 81.28, 6.35, 1.64, "Ori", 25336),
                S("Elnath", "Beta Tau", 81.57, 28.61, 1.65, "Tau", 25428),
                S("Miaplacidus", "Beta Car", 138.30, -69.72, 1.68, "Car", 45238),
                S("Alnilam", "Epsilon Ori", 84.05, -1.20, 1.69, "Ori", 26311),
                S("Alnitak", "Zeta Ori", 85.19, -1.94, 1.77, "Ori", 26727),
                S("Alioth", "Epsilon UMa", 193.51, 55.96, 1.77, "UMa", 62956),
                S("Dubhe", "Alpha UMa", 165.93, 61.75, 1.79, "UMa", 54061),
                S("Mirfak", "Alpha Per", 51.08, 49.86, 1.79, "Per", 15863),
                S("Kaus Australis", "Epsilon Sgr", 276.04, -34.38, 1.80, "Sgr", 90185),
                S("Alkaid", "Eta UMa", 206.89, 49.31, 1.86, "UMa", 67301),
                S("Sargas", "Theta Sco", 264.33, -43.00, 1.87, "Sco", 86228),
                S("Avior", "Epsilon Car", 125.63, -59.51, 1.86, "Car", 41037),
                S("Menkalinan", "Beta Aur", 89.88, 44.95, 1.90, "Aur", 28360),
                S("Atria", "Alpha TrA", 252.17, -69.03, 1.92, "TrA", 82273),
                S("Alhena", "Gamma Gem", 99.43, 16.40, 1.93, "Gem", 31681),
                S("Peacock", "Alpha Pav", 306.41, -56.74, 1.94, "Pav", 100751),
                S("Polaris", "Alpha UMi", 37.95, 89.26, 1.98, "UMi", 11767),
            };
        }

        private static Star S(string name, string des, double ra, double dec, double mag, string con, int hip)
        {
            return new Star
            {
                Name = name,
                Designation = des,
                Id = $"HIP{hip}",
                RightAscension = ra,
                Declination = dec,
                Magnitude = mag,
                Constellation = con,
                HipNumber = hip
            };
        }

        /// <summary>
        /// Get visible alignment stars for a given location and time
        /// </summary>
        public static IEnumerable<Star> GetVisibleAlignmentStars(
            double latitude, double longitude, DateTime utc, double minAltitude = 15)
        {
            foreach (var star in AlignmentStars)
            {
                var altAz = AstronomyMath.J2000ToHorizontal(
                    star.RightAscension, star.Declination, latitude, longitude, utc);
                if (altAz.Altitude >= minAltitude)
                {
                    yield return star;
                }
            }
        }

        /// <summary>
        /// Find pairs of alignment stars with good angular separation
        /// </summary>
        public static IEnumerable<(Star Star1, Star Star2, double Separation)> GetAlignmentStarPairs(
            double latitude, double longitude, DateTime utc,
            double minAltitude = 15, double minSeparation = 40, double maxSeparation = 120)
        {
            var visible = GetVisibleAlignmentStars(latitude, longitude, utc, minAltitude).ToList();

            for (int i = 0; i < visible.Count; i++)
            {
                for (int j = i + 1; j < visible.Count; j++)
                {
                    var sep = AstronomyMath.AngularSeparation(
                        visible[i].RightAscension, visible[i].Declination,
                        visible[j].RightAscension, visible[j].Declination);

                    if (sep >= minSeparation && sep <= maxSeparation)
                    {
                        yield return (visible[i], visible[j], sep);
                    }
                }
            }
        }
    }
}
