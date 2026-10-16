namespace SpawnDev.NexStar
{
    /// <summary>
    /// Represents a celestial object (star, nebula, galaxy, cluster, etc.)
    /// </summary>
    public class CelestialObject
    {
        /// <summary>
        /// Catalog identifier (e.g., "M1", "NGC7000", "HIP11767")
        /// </summary>
        public string Id { get; set; } = "";

        /// <summary>
        /// Common name (e.g., "Crab Nebula", "Orion Nebula")
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// Right Ascension in degrees (0-360)
        /// </summary>
        public double RightAscension { get; set; }

        /// <summary>
        /// Declination in degrees (-90 to +90)
        /// </summary>
        public double Declination { get; set; }

        /// <summary>
        /// Visual magnitude (lower = brighter)
        /// </summary>
        public double Magnitude { get; set; }

        /// <summary>
        /// Object type (e.g., "Galaxy", "Nebula", "Open Cluster", "Globular Cluster")
        /// </summary>
        public string ObjectType { get; set; } = "";

        /// <summary>
        /// Constellation abbreviation (e.g., "Ori", "Tau", "UMa")
        /// </summary>
        public string Constellation { get; set; } = "";

        /// <summary>
        /// Right Ascension formatted as hours:minutes:seconds
        /// </summary>
        public string FormatRA()
        {
            var hours = RightAscension / 15.0;
            var h = (int)hours;
            var m = (int)((hours - h) * 60);
            var s = ((hours - h) * 60 - m) * 60;
            return $"{h:00}h {m:00}m {s:00.0}s";
        }

        /// <summary>
        /// Declination formatted as degrees:arcminutes:arcseconds
        /// </summary>
        public string FormatDec()
        {
            var sign = Declination >= 0 ? "+" : "-";
            var dec = Math.Abs(Declination);
            var d = (int)dec;
            var m = (int)((dec - d) * 60);
            var s = ((dec - d) * 60 - m) * 60;
            return $"{sign}{d:00}° {m:00}' {s:00.0}\"";
        }

        /// <summary>
        /// Display name (common name if available, otherwise ID)
        /// </summary>
        public string DisplayName => string.IsNullOrEmpty(Name) ? Id : Name;
    }

    /// <summary>
    /// Represents a star with additional stellar properties
    /// </summary>
    public class Star : CelestialObject
    {
        /// <summary>
        /// Bayer/Flamsteed designation (e.g., "Alpha Orionis", "Beta Persei")
        /// </summary>
        public string Designation { get; set; } = "";

        /// <summary>
        /// Hipparcos catalog number
        /// </summary>
        public int HipNumber { get; set; }

        /// <summary>
        /// Spectral class (e.g., "G2V", "M1Ia", "B8V")
        /// </summary>
        public string SpectralClass { get; set; } = "";

        public Star()
        {
            ObjectType = "Star";
        }
    }

    /// <summary>
    /// Represents a Messier catalog object
    /// </summary>
    public class MessierObject : CelestialObject
    {
        /// <summary>
        /// Messier catalog number (1-110)
        /// </summary>
        public int MessierNumber { get; set; }

        /// <summary>
        /// NGC catalog number if applicable
        /// </summary>
        public int? NgcNumber { get; set; }

        /// <summary>
        /// Angular size in arcminutes
        /// </summary>
        public double? Size { get; set; }

        /// <summary>
        /// Distance in light years
        /// </summary>
        public double? Distance { get; set; }

        /// <summary>
        /// Best viewing season
        /// </summary>
        public string Season { get; set; } = "";
    }
}
