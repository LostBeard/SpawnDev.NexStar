namespace SpawnDev.NexStar
{
    /// <summary>
    /// Telescope mount axes for slewing operations
    /// </summary>
    public enum SlewAxis
    {
        /// <summary>
        /// Right Ascension / Azimuth axis
        /// </summary>
        RaAzm = 0x10,
        /// <summary>
        /// Declination / Altitude axis
        /// </summary>
        DecAlt = 0x11
    }

    /// <summary>
    /// Direction for slewing operations
    /// </summary>
    public enum SlewDirection
    {
        /// <summary>
        /// Positive direction (N/E or increasing RA/Dec)
        /// </summary>
        Positive = 1,
        /// <summary>
        /// Negative direction (S/W or decreasing RA/Dec)
        /// </summary>
        Negative = -1
    }
}
