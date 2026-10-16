namespace SpawnDev.NexStar
{
    /// <summary>
    /// Static helper class for NexStar protocol operations
    /// </summary>
    public static class NexStarProtocol
    {
        /// <summary>
        /// Command terminator character
        /// </summary>
        public const char Terminator = '#';

        #region Coordinate Conversion

        /// <summary>
        /// Converts a 16-bit NexStar hexadecimal value to decimal degrees
        /// </summary>
        /// <param name="hex">4-character hex string (e.g., "34AB")</param>
        /// <returns>Value in degrees (0-360)</returns>
        public static double NexToDecimalDegrees(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length < 4)
                return 0;

            var value = Convert.ToUInt32(hex.Substring(0, 4), 16);
            return value / 65536.0 * 360.0;
        }

        /// <summary>
        /// Converts a 32-bit precise NexStar hexadecimal value to decimal degrees
        /// </summary>
        /// <param name="hex">8-character hex string (e.g., "34AB0500")</param>
        /// <returns>Value in degrees (0-360)</returns>
        public static double PreciseNexToDecimalDegrees(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length < 8)
                return 0;

            var value = Convert.ToUInt64(hex.Substring(0, 8), 16);
            return value / (double)0xFFFFFFFF * 360.0;
        }

        /// <summary>
        /// Converts decimal degrees to 16-bit NexStar hexadecimal format
        /// </summary>
        /// <param name="degrees">Value in degrees</param>
        /// <returns>4-character hex string</returns>
        public static string DecimalDegreesToNex(double degrees)
        {
            // Normalize to 0-360 range
            degrees = degrees - 360.0 * Math.Floor(degrees / 360.0);
            if (degrees < 0) degrees += 360.0;

            var factor = degrees / 360.0;
            var value = (ushort)(factor * 65536);
            return value.ToString("X4");
        }

        /// <summary>
        /// Converts decimal degrees to 32-bit precise NexStar hexadecimal format
        /// </summary>
        /// <param name="degrees">Value in degrees</param>
        /// <returns>8-character hex string</returns>
        public static string DecimalDegreesToPreciseNex(double degrees)
        {
            // Normalize to 0-360 range
            degrees = degrees - 360.0 * Math.Floor(degrees / 360.0);
            if (degrees < 0) degrees += 360.0;

            var factor = degrees / 360.0;
            var value = (uint)(factor * 0xFFFFFFFF);
            return value.ToString("X8");
        }

        /// <summary>
        /// Parses a standard RA/Dec or Az/Alt response (format: "XXXX,YYYY#")
        /// </summary>
        /// <param name="response">Response string from telescope</param>
        /// <returns>Tuple of (first value, second value) in degrees</returns>
        public static (double, double) ParsePositionResponse(string response)
        {
            if (string.IsNullOrEmpty(response))
                return (0, 0);

            // Remove terminator if present
            response = response.TrimEnd('#');

            var parts = response.Split(',');
            if (parts.Length < 2)
                return (0, 0);

            double val1, val2;

            if (parts[0].Length >= 8)
            {
                // Precise format (32-bit)
                val1 = PreciseNexToDecimalDegrees(parts[0]);
                val2 = PreciseNexToDecimalDegrees(parts[1]);
            }
            else
            {
                // Standard format (16-bit)
                val1 = NexToDecimalDegrees(parts[0]);
                val2 = NexToDecimalDegrees(parts[1]);
            }

            // Adjust second value for declination/altitude (-90 to +90)
            if (val2 > 270) val2 -= 360;
            else if (val2 > 90) val2 -= 360;

            return (val1, val2);
        }

        /// <summary>
        /// Formats a GoTo RA/Dec command
        /// </summary>
        /// <param name="ra">Right Ascension in degrees</param>
        /// <param name="dec">Declination in degrees</param>
        /// <param name="precise">Use precise (32-bit) format</param>
        /// <returns>Command bytes including command character</returns>
        public static byte[] FormatGotoRaDecCommand(double ra, double dec, bool precise)
        {
            var cmd = precise ? "r" : "R";
            var raHex = precise ? DecimalDegreesToPreciseNex(ra) : DecimalDegreesToNex(ra);
            var decHex = precise ? DecimalDegreesToPreciseNex(dec) : DecimalDegreesToNex(dec);
            var command = $"{cmd}{raHex},{decHex}";
            return System.Text.Encoding.ASCII.GetBytes(command);
        }

        /// <summary>
        /// Formats a GoTo Az/Alt command
        /// </summary>
        /// <param name="az">Azimuth in degrees</param>
        /// <param name="alt">Altitude in degrees</param>
        /// <param name="precise">Use precise (32-bit) format</param>
        /// <returns>Command bytes including command character</returns>
        public static byte[] FormatGotoAzAltCommand(double az, double alt, bool precise)
        {
            var cmd = precise ? "b" : "B";
            var azHex = precise ? DecimalDegreesToPreciseNex(az) : DecimalDegreesToNex(az);
            var altHex = precise ? DecimalDegreesToPreciseNex(alt) : DecimalDegreesToNex(alt);
            var command = $"{cmd}{azHex},{altHex}";
            return System.Text.Encoding.ASCII.GetBytes(command);
        }

        /// <summary>
        /// Formats a Sync RA/Dec command
        /// </summary>
        /// <param name="ra">Right Ascension in degrees</param>
        /// <param name="dec">Declination in degrees</param>
        /// <param name="precise">Use precise (32-bit) format</param>
        /// <returns>Command bytes including command character</returns>
        public static byte[] FormatSyncRaDecCommand(double ra, double dec, bool precise)
        {
            var cmd = precise ? "s" : "S";
            var raHex = precise ? DecimalDegreesToPreciseNex(ra) : DecimalDegreesToNex(ra);
            var decHex = precise ? DecimalDegreesToPreciseNex(dec) : DecimalDegreesToNex(dec);
            var command = $"{cmd}{raHex},{decHex}";
            return System.Text.Encoding.ASCII.GetBytes(command);
        }

        #endregion

        #region Pass-Through Commands

        /// <summary>
        /// Creates a pass-through command for motor control
        /// </summary>
        /// <param name="msgLen">Message length (1-3)</param>
        /// <param name="destId">Destination device ID</param>
        /// <param name="cmdId">Command ID</param>
        /// <param name="data1">Data byte 1</param>
        /// <param name="data2">Data byte 2</param>
        /// <param name="data3">Data byte 3</param>
        /// <param name="resLen">Expected response length</param>
        /// <returns>Command bytes</returns>
        public static byte[] FormatPassThroughCommand(byte msgLen, byte destId, byte cmdId,
            byte data1, byte data2, byte data3, byte resLen)
        {
            return new byte[] { (byte)'P', msgLen, destId, cmdId, data1, data2, data3, resLen };
        }

        /// <summary>
        /// Creates a fixed-rate slew command
        /// </summary>
        /// <param name="axis">Axis to slew</param>
        /// <param name="direction">Direction to slew</param>
        /// <param name="rate">Slew rate (0-9)</param>
        /// <returns>Command bytes</returns>
        public static byte[] FormatFixedSlewCommand(SlewAxis axis, SlewDirection direction, SlewRate rate)
        {
            byte axisId = (byte)axis;
            byte cmdId = (byte)(direction == SlewDirection.Positive ? 36 : 37); // 36=positive, 37=negative
            return FormatPassThroughCommand(2, axisId, cmdId, (byte)rate, 0, 0, 0);
        }

        /// <summary>
        /// Creates a variable-rate slew command
        /// </summary>
        /// <param name="axis">Axis to slew</param>
        /// <param name="direction">Direction to slew</param>
        /// <param name="rateArcsecPerSec">Rate in arcseconds per second</param>
        /// <returns>Command bytes</returns>
        public static byte[] FormatVariableSlewCommand(SlewAxis axis, SlewDirection direction, double rateArcsecPerSec)
        {
            byte axisId = (byte)axis;
            byte cmdId = (byte)(direction == SlewDirection.Positive ? 6 : 7); // 6=positive, 7=negative

            // Rate is multiplied by 4 and split into high/low bytes
            int iRate = (int)(rateArcsecPerSec * 4);
            byte rateH = (byte)(iRate / 256);
            byte rateL = (byte)(iRate % 256);

            return FormatPassThroughCommand(3, axisId, cmdId, rateH, rateL, 0, 0);
        }

        #endregion

        #region Location Conversion

        /// <summary>
        /// Converts decimal degrees to degrees, minutes, seconds and sign
        /// </summary>
        public static (byte deg, byte min, byte sec, byte sign) DecimalToDMS(double value)
        {
            byte sign = (byte)(value < 0 ? 1 : 0);
            value = Math.Abs(value);
            byte deg = (byte)value;
            double remainder = (value - deg) * 60;
            byte min = (byte)remainder;
            byte sec = (byte)((remainder - min) * 60);
            return (deg, min, sec, sign);
        }

        /// <summary>
        /// Converts degrees, minutes, seconds and sign to decimal degrees
        /// </summary>
        public static double DMSToDecimal(byte deg, byte min, byte sec, byte sign)
        {
            double value = deg + min / 60.0 + sec / 3600.0;
            return sign != 0 ? -value : value;
        }

        /// <summary>
        /// Formats a set location command
        /// </summary>
        public static byte[] FormatSetLocationCommand(double lat, double lon)
        {
            var (latDeg, latMin, latSec, latSign) = DecimalToDMS(lat);
            var (lonDeg, lonMin, lonSec, lonSign) = DecimalToDMS(lon);

            return new byte[] { (byte)'W', latDeg, latMin, latSec, latSign, lonDeg, lonMin, lonSec, lonSign };
        }

        /// <summary>
        /// Parses a location response
        /// </summary>
        public static GeoLocation ParseLocationResponse(byte[] response)
        {
            if (response == null || response.Length < 8)
                return new GeoLocation();

            var lat = DMSToDecimal(response[0], response[1], response[2], response[3]);
            var lon = DMSToDecimal(response[4], response[5], response[6], response[7]);

            return new GeoLocation(lat, lon);
        }

        #endregion

        #region Time Conversion

        /// <summary>
        /// Formats a set time command
        /// </summary>
        public static byte[] FormatSetTimeCommand(DateTime time, int tzOffset, bool dst)
        {
            byte tz = (byte)(tzOffset < 0 ? tzOffset + 256 : tzOffset);
            return new byte[]
            {
                (byte)'H',
                (byte)time.Hour,
                (byte)time.Minute,
                (byte)time.Second,
                (byte)time.Month,
                (byte)time.Day,
                (byte)(time.Year - 2000),
                tz,
                (byte)(dst ? 1 : 0)
            };
        }

        /// <summary>
        /// Parses a time response
        /// </summary>
        public static TelescopeTime ParseTimeResponse(byte[] response)
        {
            if (response == null || response.Length < 8)
                return new TelescopeTime();

            int tz = response[6] > 12 ? response[6] - 256 : response[6];

            return new TelescopeTime(
                new DateTime(2000 + response[5], response[3], response[4],
                    response[0], response[1], response[2]),
                tz,
                response[7] != 0
            );
        }

        #endregion
    }
}
