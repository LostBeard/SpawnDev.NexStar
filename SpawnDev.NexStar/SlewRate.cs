namespace SpawnDev.NexStar
{
    /// <summary>
    /// Fixed slew rates matching hand controller speed buttons (0-9)
    /// </summary>
    public enum SlewRate
    {
        /// <summary>
        /// Stop - no movement
        /// </summary>
        Stop = 0,
        /// <summary>
        /// Slowest rate - guide speed
        /// </summary>
        Rate1 = 1,
        /// <summary>
        /// Rate 2
        /// </summary>
        Rate2 = 2,
        /// <summary>
        /// Rate 3
        /// </summary>
        Rate3 = 3,
        /// <summary>
        /// Rate 4
        /// </summary>
        Rate4 = 4,
        /// <summary>
        /// Rate 5
        /// </summary>
        Rate5 = 5,
        /// <summary>
        /// Rate 6
        /// </summary>
        Rate6 = 6,
        /// <summary>
        /// Rate 7
        /// </summary>
        Rate7 = 7,
        /// <summary>
        /// Rate 8
        /// </summary>
        Rate8 = 8,
        /// <summary>
        /// Fastest rate - slew speed
        /// </summary>
        Rate9 = 9
    }
}
