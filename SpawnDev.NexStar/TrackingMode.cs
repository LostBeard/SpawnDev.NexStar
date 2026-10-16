namespace SpawnDev.NexStar
{
    /// <summary>
    /// Telescope tracking modes for NexStar mounts
    /// </summary>
    public enum TrackingMode
    {
        /// <summary>
        /// Tracking disabled
        /// </summary>
        Off = 0,
        /// <summary>
        /// Alt-Azimuth tracking mode
        /// </summary>
        AltAz = 1,
        /// <summary>
        /// Equatorial tracking for Northern hemisphere
        /// </summary>
        EQNorth = 2,
        /// <summary>
        /// Equatorial tracking for Southern hemisphere
        /// </summary>
        EQSouth = 3
    }
}
