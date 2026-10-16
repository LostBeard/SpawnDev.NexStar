using System.Globalization;
using System.Reflection;

namespace SpawnDev.NexStar.Eyepiece;

/// <summary>The geomagnetic field at a place and time, in the north-east-down frame of the geodetic horizon.</summary>
/// <param name="Declination">Degrees east of true north that a compass needle points (add it to a compass bearing to get a true bearing)</param>
/// <param name="Inclination">Degrees the field dips below the horizontal</param>
/// <param name="North">nT</param>
/// <param name="East">nT</param>
/// <param name="Down">nT</param>
public readonly record struct MagneticField(double Declination, double Inclination, double North, double East, double Down)
{
    public double Horizontal => Math.Sqrt(North * North + East * East);
    public double Total => Math.Sqrt(North * North + East * East + Down * Down);
}

/// <summary>
/// The NOAA / British Geological Survey World Magnetic Model 2025 (valid 2025.0 to 2030.0): a spherical-harmonic model of the Earth's main magnetic field to
/// degree and order 12 with its yearly change. A phone's compass points at magnetic north; this gives the angle to true north from where the phone is.
/// The coefficients are US Government data (public domain), checked against NOAA's published test values.
/// </summary>
public sealed class WorldMagneticModel
{
    private const double A = 6378.137;                    // WGS84 semi-major axis, km
    private const double F = 1.0 / 298.257223563;         // WGS84 flattening
    private const double ReferenceRadius = 6371.2;        // the geomagnetic reference radius, km
    private const int Degree = 12;

    private readonly double _epoch;
    private readonly double[,] _g = new double[Degree + 1, Degree + 1];
    private readonly double[,] _h = new double[Degree + 1, Degree + 1];
    private readonly double[,] _gDot = new double[Degree + 1, Degree + 1];
    private readonly double[,] _hDot = new double[Degree + 1, Degree + 1];

    private static WorldMagneticModel? _default;

    /// <summary>WMM2025, from the coefficient file built into this library.</summary>
    public static WorldMagneticModel Default => _default ??= LoadEmbedded();

    public double Epoch => _epoch;

    /// <summary>The model is good for five years from its epoch.</summary>
    public bool IsValidAt(DateTime utc) => DecimalYear(utc) >= _epoch && DecimalYear(utc) < _epoch + 5.0;

    public WorldMagneticModel(string coefficientFileText)
    {
        var first = true;
        foreach (var raw in coefficientFileText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("99999", StringComparison.Ordinal)) break;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (first)
            {
                _epoch = double.Parse(parts[0], CultureInfo.InvariantCulture);
                first = false;
                continue;
            }
            int n = int.Parse(parts[0], CultureInfo.InvariantCulture), m = int.Parse(parts[1], CultureInfo.InvariantCulture);
            if (n < 1 || n > Degree || m < 0 || m > n) throw new FormatException($"coefficient line out of range: {line}");
            _g[n, m] = double.Parse(parts[2], CultureInfo.InvariantCulture);
            _h[n, m] = double.Parse(parts[3], CultureInfo.InvariantCulture);
            _gDot[n, m] = double.Parse(parts[4], CultureInfo.InvariantCulture);
            _hDot[n, m] = double.Parse(parts[5], CultureInfo.InvariantCulture);
        }
        if (first) throw new FormatException("the coefficient file is empty");
    }

    private static WorldMagneticModel LoadEmbedded()
    {
        using var stream = typeof(WorldMagneticModel).Assembly.GetManifestResourceStream("WMM2025.COF")
            ?? throw new InvalidOperationException("the WMM2025 coefficient file is not built into this library");
        using var reader = new StreamReader(stream);
        return new WorldMagneticModel(reader.ReadToEnd());
    }

    public static double DecimalYear(DateTime utc)
    {
        var start = new DateTime(utc.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(utc.Year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var u = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
        return utc.Year + (u - start).TotalDays / (end - start).TotalDays;
    }

    /// <summary>The declination in degrees (east positive) at a place: geodetic latitude and longitude in degrees, height above the ellipsoid in km.</summary>
    public double Declination(double latitude, double longitude, double heightKm, DateTime utc) => FieldAt(latitude, longitude, heightKm, utc).Declination;

    public MagneticField FieldAt(double latitude, double longitude, double heightKm, DateTime utc)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || !double.IsFinite(heightKm)) throw new ArgumentException("the place must be finite numbers");
        var lat = Math.Clamp(latitude, -89.99999, 89.99999) * Math.PI / 180.0;
        var lon = longitude * Math.PI / 180.0;
        var dt = DecimalYear(utc) - _epoch;

        // geodetic to geocentric spherical
        double e2 = F * (2 - F), sinLat = Math.Sin(lat), cosLat = Math.Cos(lat);
        double rc = A / Math.Sqrt(1 - e2 * sinLat * sinLat);
        double p = (rc + heightKm) * cosLat, z = (rc * (1 - e2) + heightKm) * sinLat;
        double r = Math.Sqrt(p * p + z * z);
        double latGc = Math.Asin(z / r);
        double s = Math.Sin(latGc), c = Math.Cos(latGc);

        // Schmidt semi-normalised associated Legendre functions of sin(latitude), and their derivatives with respect to latitude
        var P = new double[Degree + 1, Degree + 1];
        var dP = new double[Degree + 1, Degree + 1];
        P[0, 0] = 1;
        for (int n = 1; n <= Degree; n++)
        {
            if (n == 1)
            {
                P[1, 1] = c; dP[1, 1] = -s;
            }
            else
            {
                double k = Math.Sqrt((2.0 * n - 1) / (2.0 * n));
                P[n, n] = k * c * P[n - 1, n - 1];
                dP[n, n] = k * (-s * P[n - 1, n - 1] + c * dP[n - 1, n - 1]);
            }
            for (int m = 0; m < n; m++)
            {
                double denom = Math.Sqrt((double)n * n - (double)m * m);
                double pm2 = n - 2 >= m ? P[n - 2, m] : 0, dpm2 = n - 2 >= m ? dP[n - 2, m] : 0;
                double k2 = Math.Sqrt((double)(n - 1) * (n - 1) - (double)m * m);
                P[n, m] = ((2.0 * n - 1) * s * P[n - 1, m] - k2 * pm2) / denom;
                dP[n, m] = ((2.0 * n - 1) * (c * P[n - 1, m] + s * dP[n - 1, m]) - k2 * dpm2) / denom;
            }
        }

        double xg = 0, yg = 0, zg = 0;
        for (int n = 1; n <= Degree; n++)
        {
            double ratio = Math.Pow(ReferenceRadius / r, n + 2);
            for (int m = 0; m <= n; m++)
            {
                double g = _g[n, m] + dt * _gDot[n, m];
                double h = _h[n, m] + dt * _hDot[n, m];
                double cosM = Math.Cos(m * lon), sinM = Math.Sin(m * lon);
                xg -= ratio * (g * cosM + h * sinM) * dP[n, m];
                yg += ratio * m * (g * sinM - h * cosM) * P[n, m] / c;
                zg -= ratio * (n + 1) * (g * cosM + h * sinM) * P[n, m];
            }
        }

        // rotate from geocentric to geodetic
        double dlat = latGc - lat;
        double x = xg * Math.Cos(dlat) - zg * Math.Sin(dlat);
        double down = xg * Math.Sin(dlat) + zg * Math.Cos(dlat);
        double decl = Math.Atan2(yg, x) * 180.0 / Math.PI;
        double hor = Math.Sqrt(x * x + yg * yg);
        double incl = Math.Atan2(down, hor) * 180.0 / Math.PI;
        return new MagneticField(decl, incl, x, yg, down);
    }
}
