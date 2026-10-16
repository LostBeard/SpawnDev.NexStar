namespace SpawnDev.NexStar
{
    /// <summary>
    /// Right Ascension and Declination coordinates
    /// </summary>
    public class RaDecCoordinates
    {
        /// <summary>
        /// Right Ascension in degrees (0-360)
        /// </summary>
        public double RightAscension { get; set; }

        /// <summary>
        /// Declination in degrees (-90 to +90)
        /// </summary>
        public double Declination { get; set; }

        /// <summary>
        /// Right Ascension in hours (0-24)
        /// </summary>
        public double RightAscensionHours => RightAscension / 15.0;

        /// <summary>
        /// Creates a new RaDecCoordinates instance
        /// </summary>
        public RaDecCoordinates() { }

        /// <summary>
        /// Creates a new RaDecCoordinates instance with specified values
        /// </summary>
        /// <param name="ra">Right Ascension in degrees</param>
        /// <param name="dec">Declination in degrees</param>
        public RaDecCoordinates(double ra, double dec)
        {
            RightAscension = ra;
            Declination = dec;
        }

        /// <summary>
        /// Formats RA as HH:MM:SS string
        /// </summary>
        public string FormatRA()
        {
            var totalHours = RightAscensionHours;
            var hours = (int)totalHours;
            var minutes = (int)((totalHours - hours) * 60);
            var seconds = ((totalHours - hours) * 60 - minutes) * 60;
            return $"{hours:D2}h {minutes:D2}m {seconds:F1}s";
        }

        /// <summary>
        /// Formats Dec as DD°MM'SS" string
        /// </summary>
        public string FormatDec()
        {
            var sign = Declination >= 0 ? "+" : "-";
            var absDec = Math.Abs(Declination);
            var degrees = (int)absDec;
            var minutes = (int)((absDec - degrees) * 60);
            var seconds = ((absDec - degrees) * 60 - minutes) * 60;
            return $"{sign}{degrees:D2}° {minutes:D2}' {seconds:F1}\"";
        }

        /// <inheritdoc/>
        public override string ToString() => $"RA: {FormatRA()}, Dec: {FormatDec()}";
    }

    /// <summary>
    /// Azimuth and Altitude coordinates
    /// </summary>
    public class AzAltCoordinates
    {
        /// <summary>
        /// Azimuth in degrees (0-360, 0=North, 90=East)
        /// </summary>
        public double Azimuth { get; set; }

        /// <summary>
        /// Altitude in degrees (-90 to +90)
        /// </summary>
        public double Altitude { get; set; }

        /// <summary>
        /// Creates a new AzAltCoordinates instance
        /// </summary>
        public AzAltCoordinates() { }

        /// <summary>
        /// Creates a new AzAltCoordinates instance with specified values
        /// </summary>
        /// <param name="az">Azimuth in degrees</param>
        /// <param name="alt">Altitude in degrees</param>
        public AzAltCoordinates(double az, double alt)
        {
            Azimuth = az;
            Altitude = alt;
        }

        /// <summary>
        /// Formats azimuth as degrees string with direction
        /// </summary>
        public string FormatAz()
        {
            var direction = Azimuth switch
            {
                >= 337.5 or < 22.5 => "N",
                >= 22.5 and < 67.5 => "NE",
                >= 67.5 and < 112.5 => "E",
                >= 112.5 and < 157.5 => "SE",
                >= 157.5 and < 202.5 => "S",
                >= 202.5 and < 247.5 => "SW",
                >= 247.5 and < 292.5 => "W",
                _ => "NW"
            };
            return $"{Azimuth:F2}° ({direction})";
        }

        /// <summary>
        /// Formats altitude as degrees string
        /// </summary>
        public string FormatAlt()
        {
            return $"{Altitude:F2}°";
        }

        /// <inheritdoc/>
        public override string ToString() => $"Az: {FormatAz()}, Alt: {FormatAlt()}";
    }

    /// <summary>
    /// Geographic location coordinates
    /// </summary>
    public class GeoLocation
    {
        /// <summary>
        /// Latitude in degrees (-90 to +90, positive = North)
        /// </summary>
        public double Latitude { get; set; }

        /// <summary>
        /// Longitude in degrees (-180 to +180, positive = East)
        /// </summary>
        public double Longitude { get; set; }

        /// <summary>
        /// Creates a new GeoLocation instance
        /// </summary>
        public GeoLocation() { }

        /// <summary>
        /// Creates a new GeoLocation instance with specified values
        /// </summary>
        /// <param name="lat">Latitude in degrees</param>
        /// <param name="lon">Longitude in degrees</param>
        public GeoLocation(double lat, double lon)
        {
            Latitude = lat;
            Longitude = lon;
        }

        /// <summary>
        /// Formats latitude as degrees/minutes/seconds with N/S
        /// </summary>
        public string FormatLatitude()
        {
            var dir = Latitude >= 0 ? "N" : "S";
            var absLat = Math.Abs(Latitude);
            var deg = (int)absLat;
            var min = (int)((absLat - deg) * 60);
            var sec = ((absLat - deg) * 60 - min) * 60;
            return $"{deg}° {min}' {sec:F1}\" {dir}";
        }

        /// <summary>
        /// Formats longitude as degrees/minutes/seconds with E/W
        /// </summary>
        public string FormatLongitude()
        {
            var dir = Longitude >= 0 ? "E" : "W";
            var absLon = Math.Abs(Longitude);
            var deg = (int)absLon;
            var min = (int)((absLon - deg) * 60);
            var sec = ((absLon - deg) * 60 - min) * 60;
            return $"{deg}° {min}' {sec:F1}\" {dir}";
        }

        /// <inheritdoc/>
        public override string ToString() => $"{FormatLatitude()}, {FormatLongitude()}";
    }

    /// <summary>
    /// Telescope time information
    /// </summary>
    public class TelescopeTime
    {
        /// <summary>
        /// The local time on the telescope
        /// </summary>
        public DateTime LocalTime { get; set; }

        /// <summary>
        /// Timezone offset from UTC in hours
        /// </summary>
        public int TimezoneOffset { get; set; }

        /// <summary>
        /// Whether daylight saving time is active
        /// </summary>
        public bool DaylightSaving { get; set; }

        /// <summary>
        /// Creates a new TelescopeTime instance
        /// </summary>
        public TelescopeTime() { }

        /// <summary>
        /// Creates a new TelescopeTime instance with specified values
        /// </summary>
        public TelescopeTime(DateTime time, int tzOffset, bool dst)
        {
            LocalTime = time;
            TimezoneOffset = tzOffset;
            DaylightSaving = dst;
        }

        /// <inheritdoc/>
        public override string ToString() => $"{LocalTime:yyyy-MM-dd HH:mm:ss} (UTC{TimezoneOffset:+0;-0;+0}{(DaylightSaving ? " DST" : "")})";
    }
}
