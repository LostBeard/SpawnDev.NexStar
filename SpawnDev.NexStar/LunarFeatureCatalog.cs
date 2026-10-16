namespace SpawnDev.NexStar
{
    /// <summary>
    /// Represents a named feature on the Moon's surface
    /// </summary>
    public class MoonFeature
    {
        /// <summary>
        /// Feature name (e.g., "Tycho", "Mare Tranquillitatis")
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// Feature type (Crater, Mare, Mountain, Mons, Rille, Valley)
        /// </summary>
        public string Type { get; set; } = "";

        /// <summary>
        /// Selenographic latitude in degrees (-90 to +90, North positive)
        /// </summary>
        public double Latitude { get; set; }

        /// <summary>
        /// Selenographic longitude in degrees (-180 to +180, East positive)
        /// </summary>
        public double Longitude { get; set; }

        /// <summary>
        /// Diameter in kilometers (approximate)
        /// </summary>
        public double Diameter { get; set; }

        /// <summary>
        /// Brief description or notable facts
        /// </summary>
        public string Description { get; set; } = "";
    }

    /// <summary>
    /// Static catalog of prominent lunar features
    /// </summary>
    public static class LunarFeatureCatalog
    {
        private static List<MoonFeature>? _features;

        /// <summary>
        /// Get all lunar features
        /// </summary>
        public static List<MoonFeature> Features => _features ??= InitializeFeatures();

        /// <summary>
        /// Get features by type
        /// </summary>
        public static IEnumerable<MoonFeature> GetByType(string type) =>
            Features.Where(f => f.Type.Equals(type, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Get craters only
        /// </summary>
        public static IEnumerable<MoonFeature> Craters => GetByType("Crater");

        /// <summary>
        /// Get maria only
        /// </summary>
        public static IEnumerable<MoonFeature> Maria => GetByType("Mare");

        /// <summary>
        /// Get mountains only
        /// </summary>
        public static IEnumerable<MoonFeature> Mountains => Features.Where(f =>
            f.Type == "Mons" || f.Type == "Mountain" || f.Type == "Montes");

        private static List<MoonFeature> InitializeFeatures()
        {
            return new List<MoonFeature>
            {
                // === MAJOR CRATERS ===
                new MoonFeature { Name = "Tycho", Type = "Crater", Latitude = -43.3, Longitude = -11.2, Diameter = 85, Description = "Prominent ray crater, visible with naked eye" },
                new MoonFeature { Name = "Copernicus", Type = "Crater", Latitude = 9.6, Longitude = -20.1, Diameter = 93, Description = "Large ray crater, shows terraced walls" },
                new MoonFeature { Name = "Aristarchus", Type = "Crater", Latitude = 23.7, Longitude = -47.4, Diameter = 40, Description = "Brightest crater on Moon" },
                new MoonFeature { Name = "Kepler", Type = "Crater", Latitude = 8.1, Longitude = -38.0, Diameter = 32, Description = "Small but prominent ray crater" },
                new MoonFeature { Name = "Plato", Type = "Crater", Latitude = 51.6, Longitude = -9.3, Diameter = 101, Description = "Dark-floored crater near Mare Imbrium" },
                new MoonFeature { Name = "Clavius", Type = "Crater", Latitude = -58.4, Longitude = -14.4, Diameter = 225, Description = "One of the largest craters, has chain of craters inside" },
                new MoonFeature { Name = "Ptolemaeus", Type = "Crater", Latitude = -9.2, Longitude = -1.8, Diameter = 153, Description = "Large walled plain near center of disk" },
                new MoonFeature { Name = "Alphonsus", Type = "Crater", Latitude = -13.4, Longitude = -2.8, Diameter = 118, Description = "Has central peak and dark patches" },
                new MoonFeature { Name = "Arzachel", Type = "Crater", Latitude = -18.2, Longitude = -1.9, Diameter = 97, Description = "Well-preserved crater with central peak" },
                new MoonFeature { Name = "Theophilus", Type = "Crater", Latitude = -11.4, Longitude = 26.4, Diameter = 100, Description = "Overlaps Cyrillus, has prominent central peak" },
                new MoonFeature { Name = "Cyrillus", Type = "Crater", Latitude = -13.2, Longitude = 24.0, Diameter = 98, Description = "Older crater partly overlapped by Theophilus" },
                new MoonFeature { Name = "Catharina", Type = "Crater", Latitude = -18.1, Longitude = 23.4, Diameter = 100, Description = "Southernmost of trio with Theophilus and Cyrillus" },
                new MoonFeature { Name = "Langrenus", Type = "Crater", Latitude = -8.9, Longitude = 61.0, Diameter = 132, Description = "Prominent crater near eastern limb" },
                new MoonFeature { Name = "Petavius", Type = "Crater", Latitude = -25.3, Longitude = 60.4, Diameter = 177, Description = "Large crater with rille system" },
                new MoonFeature { Name = "Grimaldi", Type = "Crater", Latitude = -5.2, Longitude = -68.6, Diameter = 172, Description = "Dark-floored crater near western limb" },
                new MoonFeature { Name = "Eratosthenes", Type = "Crater", Latitude = 14.5, Longitude = -11.3, Diameter = 59, Description = "At the end of Apennine range" },
                new MoonFeature { Name = "Archimedes", Type = "Crater", Latitude = 29.7, Longitude = -4.0, Diameter = 83, Description = "Large lava-flooded crater in Mare Imbrium" },
                new MoonFeature { Name = "Aristillus", Type = "Crater", Latitude = 33.9, Longitude = 1.2, Diameter = 55, Description = "Young crater with ray system" },
                new MoonFeature { Name = "Autolycus", Type = "Crater", Latitude = 30.7, Longitude = 1.5, Diameter = 39, Description = "Near Aristillus in Mare Imbrium" },
                new MoonFeature { Name = "Gassendi", Type = "Crater", Latitude = -17.5, Longitude = -40.1, Diameter = 110, Description = "Floor has rille network, good for observation" },
                new MoonFeature { Name = "Schickard", Type = "Crater", Latitude = -44.4, Longitude = -55.1, Diameter = 227, Description = "Large walled plain with dark patches" },
                new MoonFeature { Name = "Posidonius", Type = "Crater", Latitude = 31.8, Longitude = 29.9, Diameter = 95, Description = "Floor has rilles and small craters" },
                new MoonFeature { Name = "Atlas", Type = "Crater", Latitude = 46.7, Longitude = 44.4, Diameter = 87, Description = "Paired with Hercules" },
                new MoonFeature { Name = "Hercules", Type = "Crater", Latitude = 46.7, Longitude = 39.1, Diameter = 69, Description = "Paired with Atlas" },
                new MoonFeature { Name = "Endymion", Type = "Crater", Latitude = 53.6, Longitude = 56.5, Diameter = 125, Description = "Dark-floored crater near northeast limb" },
                
                // === MARIA (SEAS) ===
                new MoonFeature { Name = "Mare Tranquillitatis", Type = "Mare", Latitude = 8.5, Longitude = 31.4, Diameter = 873, Description = "Apollo 11 landing site, Sea of Tranquility" },
                new MoonFeature { Name = "Mare Serenitatis", Type = "Mare", Latitude = 28.0, Longitude = 17.5, Diameter = 707, Description = "Sea of Serenity, circular mare" },
                new MoonFeature { Name = "Mare Imbrium", Type = "Mare", Latitude = 32.8, Longitude = -15.6, Diameter = 1145, Description = "Sea of Rains, largest maria visible" },
                new MoonFeature { Name = "Mare Crisium", Type = "Mare", Latitude = 17.0, Longitude = 59.1, Diameter = 418, Description = "Sea of Crises, isolated near limb" },
                new MoonFeature { Name = "Mare Fecunditatis", Type = "Mare", Latitude = -7.8, Longitude = 51.3, Diameter = 909, Description = "Sea of Fertility" },
                new MoonFeature { Name = "Mare Nectaris", Type = "Mare", Latitude = -15.2, Longitude = 35.5, Diameter = 333, Description = "Sea of Nectar" },
                new MoonFeature { Name = "Mare Nubium", Type = "Mare", Latitude = -21.3, Longitude = -16.6, Diameter = 715, Description = "Sea of Clouds" },
                new MoonFeature { Name = "Mare Humorum", Type = "Mare", Latitude = -24.4, Longitude = -38.6, Diameter = 389, Description = "Sea of Moisture" },
                new MoonFeature { Name = "Mare Frigoris", Type = "Mare", Latitude = 56.0, Longitude = 1.4, Diameter = 1596, Description = "Sea of Cold, elongated northern mare" },
                new MoonFeature { Name = "Oceanus Procellarum", Type = "Mare", Latitude = 18.4, Longitude = -57.4, Diameter = 2568, Description = "Ocean of Storms, largest mare" },
                new MoonFeature { Name = "Mare Vaporum", Type = "Mare", Latitude = 13.3, Longitude = 3.6, Diameter = 245, Description = "Sea of Vapors" },
                new MoonFeature { Name = "Sinus Iridum", Type = "Mare", Latitude = 44.1, Longitude = -31.5, Diameter = 236, Description = "Bay of Rainbows, scenic bay in Mare Imbrium" },
                
                // === MOUNTAINS ===
                new MoonFeature { Name = "Montes Apenninus", Type = "Montes", Latitude = 18.9, Longitude = -3.7, Diameter = 600, Description = "Apennine Mountains, forms Imbrium basin rim" },
                new MoonFeature { Name = "Montes Alpes", Type = "Montes", Latitude = 46.4, Longitude = -0.8, Diameter = 281, Description = "Alps, contains Vallis Alpes" },
                new MoonFeature { Name = "Montes Caucasus", Type = "Montes", Latitude = 38.4, Longitude = 10.0, Diameter = 445, Description = "Caucasus Mountains" },
                new MoonFeature { Name = "Montes Jura", Type = "Montes", Latitude = 47.1, Longitude = -34.0, Diameter = 422, Description = "Jura Mountains, bounds Sinus Iridum" },
                new MoonFeature { Name = "Mons Piton", Type = "Mons", Latitude = 40.6, Longitude = -0.9, Diameter = 25, Description = "Isolated peak in Mare Imbrium" },
                new MoonFeature { Name = "Mons Pico", Type = "Mons", Latitude = 45.7, Longitude = -8.9, Diameter = 25, Description = "Isolated peak in Mare Imbrium" },
                new MoonFeature { Name = "Montes Teneriffe", Type = "Montes", Latitude = 47.1, Longitude = -11.8, Diameter = 110, Description = "Mountain group in Mare Imbrium" },
                new MoonFeature { Name = "Montes Recti", Type = "Montes", Latitude = 48.0, Longitude = -20.0, Diameter = 90, Description = "Straight Range in Mare Imbrium" },
                
                // === SPECIAL FEATURES ===
                new MoonFeature { Name = "Vallis Alpes", Type = "Valley", Latitude = 48.5, Longitude = 3.2, Diameter = 166, Description = "Alpine Valley, cuts through Montes Alpes" },
                new MoonFeature { Name = "Rupes Recta", Type = "Rille", Latitude = -22.1, Longitude = -7.8, Diameter = 110, Description = "Straight Wall, famous fault line" },
                new MoonFeature { Name = "Hadley Rille", Type = "Rille", Latitude = 25.0, Longitude = 3.0, Diameter = 80, Description = "Apollo 15 landing site, sinuous rille" },
                new MoonFeature { Name = "Hyginus Rille", Type = "Rille", Latitude = 7.8, Longitude = 6.3, Diameter = 220, Description = "Contains crater Hyginus" },
            };
        }
    }
}
