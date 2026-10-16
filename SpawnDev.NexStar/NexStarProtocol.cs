using System.Globalization;
using System.Text;

namespace SpawnDev.NexStar
{
    /// <summary>
    /// Celestron NexStar hand control serial protocol: command formatting and response parsing, no I/O.
    /// Reference: "NexStar Communication Protocol" (Celestron). Every parser reports failure instead of
    /// inventing a value: a failed read must never become a position of (0, 0).
    /// </summary>
    public static class NexStarProtocol
    {
        /// <summary>
        /// Command terminator character
        /// </summary>
        public const char Terminator = '#';

        /// <summary>One revolution in the 16-bit position format.</summary>
        private const double Revolution16 = 65536.0;

        /// <summary>One revolution in the precise 32-bit position format (the document divides by 4294967296).</summary>
        private const double Revolution32 = 4294967296.0;

        /// <summary>
        /// How fast each fixed slew rate (index 0-9, 0 = stopped) turns an axis, degrees per second, MEASURED on a Celestron 127SLT (hand control 5.35) on
        /// 2026-10-16 by timing the position over 1-3 second slews: 1 is 0.002 (a quarter of the sidereal rate), 5 is 0.067, 7 is one degree a second.
        /// Rate 9 was only run for 0.7 s, long enough to ramp to 2.4; goto speeds reached 3.06, and the protocol document says 3 degrees per second for GT
        /// mounts, so 9 is entered as 3.0 (the safe side for a stopping distance). Other models differ: measure before relying on it.
        /// </summary>
        public static readonly IReadOnlyList<double> MeasuredFixedRateDegreesPerSecond = new[] { 0.0, 0.002, 0.004, 0.017, 0.033, 0.067, 0.267, 1.0, 1.75, 3.0 };

        /// <summary>
        /// The largest variable slew rate the protocol can carry: the rate times 4 must fit in two bytes.
        /// </summary>
        public const double MaxVariableSlewArcsecPerSec = 65535 / 4.0;

        #region Response framing

        /// <summary>
        /// The number of payload bytes the hand control sends before the terminating '#' for a command,
        /// or -1 if the command is not known (its response then ends at the first '#').
        /// Binary payloads can contain 0x23 ('#'), so framing by "first '#'" is wrong: this mount's version
        /// is 5.35 and 35 is 0x23. See <see cref="NexStarResponseAssembler"/>.
        /// </summary>
        public static int ExpectedResponseLength(byte[] command)
        {
            if (command == null || command.Length == 0) return -1;
            switch ((char)command[0])
            {
                case 'E': case 'Z': return 9;     // "34AB,12CE"
                case 'e': case 'z': return 17;    // "34AB0500,12CE0500"
                case 'V': return 2;               // major, minor
                case 'm': return 1;               // model
                case 'J': return 1;               // aligned
                case 't': return 1;               // tracking mode
                case 'K': return 1;               // echo
                case 'L': return 1;               // ASCII '0' or '1'
                case 'w': return 8;               // location
                case 'h': return 8;               // time
                case 'R': case 'r': case 'B': case 'b':
                case 'S': case 's': case 'W': case 'H':
                case 'T': case 'M':
                    return 0;                     // just '#'
                case 'P':
                    // P msgLen dest cmd d1 d2 d3 respLen
                    return command.Length == 8 ? command[7] : -1;
                default: return -1;
            }
        }

        #endregion

        #region Coordinate Conversion

        /// <summary>
        /// Parses a 16-bit NexStar hexadecimal fraction of a revolution to degrees
        /// </summary>
        /// <param name="hex">Exactly 4 hex digits (e.g., "34AB")</param>
        /// <param name="degrees">Value in degrees (0 to just under 360)</param>
        public static bool TryNexToDecimalDegrees(string? hex, out double degrees)
        {
            degrees = 0;
            if (hex == null || hex.Length != 4) return false;
            if (!uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)) return false;
            degrees = value / Revolution16 * 360.0;
            return true;
        }

        /// <summary>
        /// Parses a 32-bit precise NexStar hexadecimal fraction of a revolution to degrees
        /// </summary>
        /// <param name="hex">Exactly 8 hex digits (e.g., "34AB0500")</param>
        /// <param name="degrees">Value in degrees (0 to just under 360)</param>
        public static bool TryPreciseNexToDecimalDegrees(string? hex, out double degrees)
        {
            degrees = 0;
            if (hex == null || hex.Length != 8) return false;
            if (!uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)) return false;
            degrees = value / Revolution32 * 360.0;
            return true;
        }

        /// <summary>
        /// Converts decimal degrees to 16-bit NexStar hexadecimal format (nearest unit, any angle wraps into a revolution)
        /// </summary>
        /// <returns>4-character hex string</returns>
        public static string DecimalDegreesToNex(double degrees) => ToUnits(degrees, Revolution16).ToString("X4", CultureInfo.InvariantCulture);

        /// <summary>
        /// Converts decimal degrees to 32-bit precise NexStar hexadecimal format (nearest unit, any angle wraps into a revolution)
        /// </summary>
        /// <returns>8-character hex string</returns>
        public static string DecimalDegreesToPreciseNex(double degrees) => ToUnits(degrees, Revolution32).ToString("X8", CultureInfo.InvariantCulture);

        private static ulong ToUnits(double degrees, double unitsPerRevolution)
        {
            if (!double.IsFinite(degrees)) throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "Angle must be finite");
            var fraction = degrees / 360.0;
            fraction -= Math.Floor(fraction); // [0, 1)
            var units = (ulong)Math.Round(fraction * unitsPerRevolution);
            // A fraction just under 1 rounds up to a whole revolution, which is angle 0.
            return units % (ulong)unitsPerRevolution;
        }

        /// <summary>
        /// Parses a position response payload (format "XXXX,YYYY" or "XXXXXXXX,YYYYYYYY", no '#').
        /// The first value is 0 to 360 degrees. The second value (declination or altitude) is -180 to 180:
        /// the hand control sends negative angles as 360 minus the angle.
        /// </summary>
        public static bool TryParsePositionResponse(string? response, out double first, out double second)
        {
            first = second = 0;
            if (string.IsNullOrEmpty(response)) return false;
            var parts = response.TrimEnd(Terminator).Split(',');
            if (parts.Length != 2) return false;
            double a, b;
            if (parts[0].Length == 8 && parts[1].Length == 8)
            {
                if (!TryPreciseNexToDecimalDegrees(parts[0], out a) || !TryPreciseNexToDecimalDegrees(parts[1], out b)) return false;
            }
            else if (parts[0].Length == 4 && parts[1].Length == 4)
            {
                if (!TryNexToDecimalDegrees(parts[0], out a) || !TryNexToDecimalDegrees(parts[1], out b)) return false;
            }
            else return false;
            if (b > 180) b -= 360;
            first = a;
            second = b;
            return true;
        }

        /// <summary>
        /// Parses a position response payload given as bytes (ASCII).
        /// </summary>
        public static bool TryParsePositionResponse(byte[]? payload, out double first, out double second)
        {
            first = second = 0;
            return payload != null && TryParsePositionResponse(Encoding.ASCII.GetString(payload), out first, out second);
        }

        /// <summary>
        /// Formats a GoTo RA/Dec command
        /// </summary>
        /// <param name="ra">Right Ascension in degrees</param>
        /// <param name="dec">Declination in degrees</param>
        /// <param name="precise">Use precise (32-bit) format</param>
        /// <returns>Command bytes including command character</returns>
        public static byte[] FormatGotoRaDecCommand(double ra, double dec, bool precise)
            => FormatPositionCommand(precise ? 'r' : 'R', ra, dec, precise);

        /// <summary>
        /// Formats a GoTo Az/Alt command
        /// </summary>
        /// <param name="az">Azimuth in degrees</param>
        /// <param name="alt">Altitude in degrees</param>
        /// <param name="precise">Use precise (32-bit) format</param>
        /// <returns>Command bytes including command character</returns>
        public static byte[] FormatGotoAzAltCommand(double az, double alt, bool precise)
            => FormatPositionCommand(precise ? 'b' : 'B', az, alt, precise);

        /// <summary>
        /// Formats a Sync RA/Dec command
        /// </summary>
        /// <param name="ra">Right Ascension in degrees</param>
        /// <param name="dec">Declination in degrees</param>
        /// <param name="precise">Use precise (32-bit) format</param>
        /// <returns>Command bytes including command character</returns>
        public static byte[] FormatSyncRaDecCommand(double ra, double dec, bool precise)
            => FormatPositionCommand(precise ? 's' : 'S', ra, dec, precise);

        private static byte[] FormatPositionCommand(char command, double first, double second, bool precise)
        {
            var a = precise ? DecimalDegreesToPreciseNex(first) : DecimalDegreesToNex(first);
            var b = precise ? DecimalDegreesToPreciseNex(second) : DecimalDegreesToNex(second);
            return Encoding.ASCII.GetBytes($"{command}{a},{b}");
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
        /// <param name="rate">Slew rate (0-9, 0 stops)</param>
        /// <returns>Command bytes</returns>
        public static byte[] FormatFixedSlewCommand(SlewAxis axis, SlewDirection direction, SlewRate rate)
        {
            if ((int)rate < 0 || (int)rate > 9) throw new ArgumentOutOfRangeException(nameof(rate), rate, "Fixed slew rate is 0 (stop) to 9");
            byte axisId = (byte)axis;
            byte cmdId = (byte)(direction == SlewDirection.Positive ? 36 : 37); // 36=positive, 37=negative
            return FormatPassThroughCommand(2, axisId, cmdId, (byte)rate, 0, 0, 0);
        }

        /// <summary>
        /// Creates a variable-rate slew command
        /// </summary>
        /// <param name="axis">Axis to slew</param>
        /// <param name="direction">Direction to slew</param>
        /// <param name="rateArcsecPerSec">Rate in arcseconds per second; clamped to 0 .. <see cref="MaxVariableSlewArcsecPerSec"/> (the rate times 4 is two bytes)</param>
        /// <returns>Command bytes</returns>
        public static byte[] FormatVariableSlewCommand(SlewAxis axis, SlewDirection direction, double rateArcsecPerSec)
        {
            if (double.IsNaN(rateArcsecPerSec)) throw new ArgumentOutOfRangeException(nameof(rateArcsecPerSec), "Rate is not a number");
            byte axisId = (byte)axis;
            byte cmdId = (byte)(direction == SlewDirection.Positive ? 6 : 7); // 6=positive, 7=negative

            // Rate is multiplied by 4 and split into high/low bytes
            var iRate = (int)Math.Round(Math.Clamp(rateArcsecPerSec, 0, MaxVariableSlewArcsecPerSec) * 4);
            return FormatPassThroughCommand(3, axisId, cmdId, (byte)(iRate / 256), (byte)(iRate % 256), 0, 0);
        }

        #endregion

        #region Location Conversion

        /// <summary>
        /// Converts decimal degrees to degrees, minutes, seconds and sign (1 = negative), rounded to the nearest second
        /// </summary>
        public static (byte deg, byte min, byte sec, byte sign) DecimalToDMS(double value)
        {
            byte sign = (byte)(value < 0 ? 1 : 0);
            var totalSeconds = (int)Math.Round(Math.Abs(value) * 3600.0);
            return ((byte)(totalSeconds / 3600), (byte)(totalSeconds % 3600 / 60), (byte)(totalSeconds % 60), sign);
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
        /// Formats a set location command (latitude first; sign byte 0 = north/east, 1 = south/west)
        /// </summary>
        public static byte[] FormatSetLocationCommand(double lat, double lon)
        {
            if (!(lat >= -90 && lat <= 90)) throw new ArgumentOutOfRangeException(nameof(lat), lat, "Latitude is -90 to 90");
            if (!(lon >= -180 && lon <= 180)) throw new ArgumentOutOfRangeException(nameof(lon), lon, "Longitude is -180 to 180");
            var (latDeg, latMin, latSec, latSign) = DecimalToDMS(lat);
            var (lonDeg, lonMin, lonSec, lonSign) = DecimalToDMS(lon);

            return new byte[] { (byte)'W', latDeg, latMin, latSec, latSign, lonDeg, lonMin, lonSec, lonSign };
        }

        /// <summary>
        /// Parses a location response payload (8 bytes: lat deg/min/sec/sign, lon deg/min/sec/sign)
        /// </summary>
        public static bool TryParseLocationResponse(byte[]? payload, out GeoLocation location)
        {
            location = new GeoLocation();
            if (payload == null || payload.Length != 8) return false;
            if (payload[0] > 90 || payload[1] > 59 || payload[2] > 59 || payload[3] > 1) return false;
            if (payload[4] > 180 || payload[5] > 59 || payload[6] > 59 || payload[7] > 1) return false;
            location = new GeoLocation(
                DMSToDecimal(payload[0], payload[1], payload[2], payload[3]),
                DMSToDecimal(payload[4], payload[5], payload[6], payload[7]));
            return true;
        }

        #endregion

        #region Time Conversion

        /// <summary>
        /// Formats a set time command. The time is the LOCAL clock time, the offset is the zone's STANDARD
        /// offset from UTC, and <paramref name="dst"/> says whether the hand control should add the daylight
        /// saving hour (the protocol's example sets 3:26 PM on April 6 in Eastern as offset -5 with DST = 1).
        /// Use <see cref="ComputeClock"/> to derive all three from a UTC instant.
        /// </summary>
        public static byte[] FormatSetTimeCommand(DateTime time, int standardOffsetHours, bool dst)
        {
            if (time.Year < 2000 || time.Year > 2255) throw new ArgumentOutOfRangeException(nameof(time), time, "The year is sent as an offset from 2000 in one byte");
            if (standardOffsetHours < -12 || standardOffsetHours > 14) throw new ArgumentOutOfRangeException(nameof(standardOffsetHours), standardOffsetHours, "No time zone is outside -12 to +14 hours");
            return new byte[]
            {
                (byte)'H',
                (byte)time.Hour,
                (byte)time.Minute,
                (byte)time.Second,
                (byte)time.Month,
                (byte)time.Day,
                (byte)(time.Year - 2000),
                unchecked((byte)standardOffsetHours), // negative zones are 256 - zone
                (byte)(dst ? 1 : 0)
            };
        }

        /// <summary>
        /// Parses a time response payload (8 bytes: hour, minute, second, month, day, year-2000, standard offset, DST flag)
        /// </summary>
        public static bool TryParseTimeResponse(byte[]? payload, out TelescopeTime time)
        {
            time = new TelescopeTime();
            if (payload == null || payload.Length != 8) return false;
            if (payload[7] > 1) return false;
            DateTime local;
            try
            {
                local = new DateTime(2000 + payload[5], payload[3], payload[4], payload[0], payload[1], payload[2], DateTimeKind.Unspecified);
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
            time = new TelescopeTime(local, unchecked((sbyte)payload[6]), payload[7] != 0);
            return true;
        }

        /// <summary>
        /// Works out what to send the hand control so that its idea of UTC is exactly <paramref name="utc"/>.
        /// The hand control computes UTC as local time minus the standard offset minus the daylight saving hour,
        /// and takes whole-hour offsets only, so the local fields are derived from UTC with the whole-hour
        /// standard offset (rounded) rather than read from the zone: a half-hour zone then shows a clock
        /// half an hour off on the hand control, but sidereal time, which depends on UTC, stays right.
        /// </summary>
        public static TelescopeTime ComputeClock(DateTime utc, TimeZoneInfo zone)
        {
            var utcInstant = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            var standard = (int)Math.Round(zone.BaseUtcOffset.TotalHours, MidpointRounding.AwayFromZero);
            var dst = zone.IsDaylightSavingTime(utcInstant);
            var local = DateTime.SpecifyKind(utcInstant.AddHours(standard + (dst ? 1 : 0)), DateTimeKind.Unspecified);
            return new TelescopeTime(local, standard, dst);
        }

        #endregion
    }
}
