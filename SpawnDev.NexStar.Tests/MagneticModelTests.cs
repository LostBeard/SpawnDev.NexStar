using System.Globalization;
using SpawnDev.NexStar.Eyepiece;
using SpawnDev.UnitTesting;

namespace SpawnDev.NexStar.Tests;

/// <summary>The World Magnetic Model against NOAA's own published test values for WMM2025 (declination, inclination and the field components).</summary>
public class MagneticModelTests
{
    private static IEnumerable<double[]> TestRows()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "WMM2025_TEST_VALUES.txt");
        Check.That(File.Exists(path), $"the NOAA test values are with the tests: {path}");
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            yield return line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        }
    }

    /// <summary>A decimal year as a date (the model works from dates).</summary>
    private static DateTime FromDecimalYear(double year)
    {
        int y = (int)Math.Floor(year);
        var start = new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(y + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return start.AddTicks((long)((year - y) * (end - start).Ticks));
    }

    [TestMethod]
    public void EveryNoaaTestValue_IsReproduced()
    {
        var model = WorldMagneticModel.Default;
        int count = 0;
        double worstD = 0, worstI = 0, worstNt = 0;
        foreach (var r in TestRows())
        {
            // year, altitude km, geodetic latitude, longitude, D, I, H, X, Y, Z, F
            var field = model.FieldAt(r[2], r[3], r[1], FromDecimalYear(r[0]));
            double dD = Math.Abs(((field.Declination - r[4] + 540) % 360) - 180), dI = Math.Abs(field.Inclination - r[5]);
            double nt = new[] { Math.Abs(field.Horizontal - r[6]), Math.Abs(field.North - r[7]), Math.Abs(field.East - r[8]), Math.Abs(field.Down - r[9]), Math.Abs(field.Total - r[10]) }.Max();
            worstD = Math.Max(worstD, dD); worstI = Math.Max(worstI, dI); worstNt = Math.Max(worstNt, nt);
            // NOAA's own acceptance: 0.01 degrees, 0.1 nT (at the pole-most points declination is ill-defined, so allow the file's two-decimal rounding)
            Check.That(dD < 0.0051 && dI < 0.0051 && nt < 0.1, $"year {r[0]} alt {r[1]} lat {r[2]} lon {r[3]}: D {field.Declination:F3} vs {r[4]}, I {field.Inclination:F3} vs {r[5]}, worst component {nt:F3} nT");
            count++;
        }
        Check.That(count >= 100, $"only {count} test rows were read");
        Console.WriteLine($"[WMM] {count} NOAA test values: worst D {worstD:F4} deg, I {worstI:F4} deg, field {worstNt:F4} nT");
    }

    [TestMethod]
    public void TheDeclination_AtFamousPlaces_IsWhatTheMapsSay()
    {
        var model = WorldMagneticModel.Default;
        var when = new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc);
        // approximate declinations from NOAA's map for 2026: New York about -12.5, Seattle about +15, London about +1.5, Sydney about +12.5
        Check.Near(-12.5, model.Declination(40.71, -74.0, 0, when), 1.0, "New York");
        Check.Near(15.0, model.Declination(47.6, -122.3, 0, when), 1.5, "Seattle");
        Check.Near(1.5, model.Declination(51.5, -0.1, 0, when), 1.5, "London");
        Check.Near(12.5, model.Declination(-33.9, 151.2, 0, when), 1.5, "Sydney");
        Check.That(model.IsValidAt(when) && !model.IsValidAt(new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc)) && !model.IsValidAt(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)), "valid 2025 to 2030");
    }

    [TestMethod]
    public void TheFieldPointsDown_InTheNorth_AndUp_InTheSouth_AndNeverBlowsUpAtThePoles()
    {
        var model = WorldMagneticModel.Default;
        var when = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        Check.That(model.FieldAt(45, 10, 0, when).Inclination > 50, "dips down in the northern hemisphere");
        Check.That(model.FieldAt(-45, 10, 0, when).Inclination < -40, "and up in the southern");
        foreach (var lat in new[] { 90.0, -90.0, 89.9999999 })
        {
            var f = model.FieldAt(lat, 20, 0, when);
            Check.That(double.IsFinite(f.Declination) && double.IsFinite(f.Total) && f.Total > 40000 && f.Total < 70000, $"finite at latitude {lat}: {f}");
        }
    }

    [TestMethod]
    public void BadInput_IsRefused_AndABadCoefficientFileIsRefused()
    {
        var model = WorldMagneticModel.Default;
        try { model.FieldAt(double.NaN, 0, 0, DateTime.UtcNow); Check.That(false, "NaN accepted"); } catch (ArgumentException) { }
        try { _ = new WorldMagneticModel(""); Check.That(false, "an empty file was accepted"); } catch (FormatException) { }
        try { _ = new WorldMagneticModel("2025.0 WMM\n 40 0 1 2 3 4\n"); Check.That(false, "a degree beyond 12 was accepted"); } catch (FormatException) { }
    }

    [TestMethod]
    public void ACompassBearing_BecomesATrueBearing_ByTheDeclination()
    {
        var model = WorldMagneticModel.Default;
        var when = new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc);
        var declination = model.Declination(40.71, -74.0, 0, when);   // about -12.5: magnetic north is west of true north
        var compass = new CameraPose(100, 30, 0);   // the phone's compass says 100
        var truth = compass.WithDeclination(declination);
        Check.Near(100 + declination, truth.Azimuth, 1e-9, "true azimuth");
        Check.That(truth.Azimuth < 100, "in New York a compass reads east of the true bearing, so the true bearing is smaller");
    }
}
