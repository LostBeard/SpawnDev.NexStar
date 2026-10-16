namespace SpawnDev.NexStar
{
    /// <summary>
    /// Celestron telescope model identifiers
    /// </summary>
    public enum TelescopeModel
    {
        /// <summary>
        /// Unknown model
        /// </summary>
        Unknown = 0,
        /// <summary>
        /// NexStar GPS Series
        /// </summary>
        NexStarGPS = 1,
        /// <summary>
        /// NexStar i-Series
        /// </summary>
        NexStarI = 3,
        /// <summary>
        /// NexStar i-Series SE
        /// </summary>
        NexStarISE = 4,
        /// <summary>
        /// CGE mount
        /// </summary>
        CGE = 5,
        /// <summary>
        /// Advanced GT mount
        /// </summary>
        AdvancedGT = 6,
        /// <summary>
        /// SLT mount (includes NexStar SLT 127)
        /// </summary>
        SLT = 7,
        /// <summary>
        /// CPC mount
        /// </summary>
        CPC = 9,
        /// <summary>
        /// GT mount
        /// </summary>
        GT = 10,
        /// <summary>
        /// NexStar 4/5 SE
        /// </summary>
        NexStar45SE = 11,
        /// <summary>
        /// NexStar 6/8 SE
        /// </summary>
        NexStar68SE = 12,
        /// <summary>
        /// CGEM mount
        /// </summary>
        CGEM = 14,
        /// <summary>
        /// Advanced VX mount
        /// </summary>
        AdvancedVX = 20,
        /// <summary>
        /// NexStar Evolution
        /// </summary>
        NexStarEvolution = 22
    }

    /// <summary>
    /// Extension methods for TelescopeModel
    /// </summary>
    public static class TelescopeModelExtensions
    {
        /// <summary>
        /// Gets a human-readable name for the telescope model
        /// </summary>
        public static string GetDisplayName(this TelescopeModel model)
        {
            return model switch
            {
                TelescopeModel.NexStarGPS => "NexStar GPS Series",
                TelescopeModel.NexStarI => "NexStar i-Series",
                TelescopeModel.NexStarISE => "NexStar i-Series SE",
                TelescopeModel.CGE => "CGE",
                TelescopeModel.AdvancedGT => "Advanced GT",
                TelescopeModel.SLT => "SLT",
                TelescopeModel.CPC => "CPC",
                TelescopeModel.GT => "GT",
                TelescopeModel.NexStar45SE => "NexStar 4/5 SE",
                TelescopeModel.NexStar68SE => "NexStar 6/8 SE",
                TelescopeModel.CGEM => "CGEM",
                TelescopeModel.AdvancedVX => "Advanced VX",
                TelescopeModel.NexStarEvolution => "NexStar Evolution",
                _ => "Unknown"
            };
        }
    }
}
