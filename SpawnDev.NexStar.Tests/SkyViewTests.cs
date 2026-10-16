using System.Numerics;
using SpawnDev.NexStar.Sky;
using SpawnDev.UnitTesting;

namespace SpawnDev.NexStar.Tests;

/// <summary>
/// The chart's geometry. The star shader and the HUD both depend on this rotation and projection, so it is tied to the astronomy that was validated against
/// JPL Horizons (<see cref="AstronomyMath.J2000ToHorizontal"/>), and the projection is checked for orientation, centring, edges and invertibility.
/// </summary>
public class SkyViewTests
{
    private static SkyView NewView(double lat = 40.7484, double lon = -73.9857, double az = 180, double alt = 40, double fov = 90, double w = 1280, double h = 800, DateTime? utc = null)
    {
        var v = new SkyView { Latitude = lat, Longitude = lon, CenterAzimuth = az, CenterAltitude = alt, FovDegrees = fov, WidthPx = w, HeightPx = h, Utc = utc ?? new DateTime(2026, 10, 16, 3, 0, 0, DateTimeKind.Utc) };
        v.Update();
        return v;
    }

    [TestMethod]
    public void Rotation_AgreesWithTheValidatedAstronomy_ForRandomStarsSitesAndTimes()
    {
        var rng = new Random(1016);
        double worst = 0;
        for (int i = 0; i < 20000; i++)
        {
            var ra = rng.NextDouble() * 360;
            var dec = Math.Asin(rng.NextDouble() * 2 - 1) * 180 / Math.PI;
            var lat = Math.Asin(rng.NextDouble() * 2 - 1) * 180 / Math.PI;
            var lon = rng.NextDouble() * 360 - 180;
            var utc = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(rng.NextDouble() * 365.25 * 60);
            var view = NewView(lat, lon, utc: utc);
            var h = view.ToHorizontal(CatalogFormats.UnitVector(ra, dec));
            var (az, alt) = SkyView.ToAzAlt(h);
            var expected = AstronomyMath.J2000ToHorizontal(ra, dec, lat, lon, utc);
            var sep = AstronomyMath.AngularSeparation(az, alt, expected.Azimuth, expected.Altitude) * 3600.0;
            worst = Math.Max(worst, sep);
            Check.That(sep < 0.2, $"ra {ra:F3} dec {dec:F3} at lat {lat:F2} lon {lon:F2} {utc:u}: {sep:F3} arcsec from the validated conversion (az {az:F4}/{alt:F4} vs {expected.Azimuth:F4}/{expected.Altitude:F4})");
        }
        Console.WriteLine($"[SkyView] worst difference from AstronomyMath over 20000 random cases: {worst:F4} arcsec");
    }

    [TestMethod]
    public void Rotation_IsARotation_AndTheInverseGoesBack()
    {
        var view = NewView(utc: new DateTime(2031, 3, 20, 12, 0, 0, DateTimeKind.Utc));
        var m = view.EquatorialToHorizontal;
        // rows orthonormal
        for (int a = 0; a < 3; a++)
            for (int b = 0; b < 3; b++)
            {
                var dot = m[a * 3] * m[b * 3] + m[a * 3 + 1] * m[b * 3 + 1] + m[a * 3 + 2] * m[b * 3 + 2];
                Check.Near(a == b ? 1.0 : 0.0, dot, 1e-12, $"rows {a},{b}");
            }
        var rng = new Random(5);
        for (int i = 0; i < 1000; i++)
        {
            var v = Vector3.Normalize(new Vector3((float)rng.NextDouble() - .5f, (float)rng.NextDouble() - .5f, (float)rng.NextDouble() - .5f));
            var back = view.ToJ2000(view.ToHorizontal(v));
            Check.That(Vector3.Distance(v, back) < 1e-6f, $"round trip {v} -> {back}");
        }
    }

    [TestMethod]
    public void Projection_PutsTheCenterInTheMiddle_EastOnTheRight_UpAtTheTop()
    {
        var view = NewView(az: 0, alt: 30, fov: 60);
        Check.That(view.TryProject(SkyView.FromAzAlt(0, 30), out var cx, out var cy), "the centre projects");
        Check.Near(640.0, cx, 1e-6, "centre x");
        Check.Near(400.0, cy, 1e-6, "centre y");
        view.TryProject(SkyView.FromAzAlt(5, 30), out var ex, out var ey);
        Check.That(ex > 640, "looking north, east is to the right");
        view.TryProject(SkyView.FromAzAlt(355, 30), out var wx, out _);
        Check.That(wx < 640, "and west to the left");
        view.TryProject(SkyView.FromAzAlt(0, 35), out _, out var uy);
        Check.That(uy < 400, "higher in the sky is higher on the screen");
        // looking south east is on the LEFT
        var south = NewView(az: 180, alt: 30, fov: 60);
        south.TryProject(SkyView.FromAzAlt(185, 30), out var sx, out _);
        Check.That(sx > 640, "looking south, west (azimuth above 180) is on the right");
        south.TryProject(SkyView.FromAzAlt(175, 30), out var sx2, out _);
        Check.That(sx2 < 640, "and east (azimuth below 180) is on the left");
    }

    [TestMethod]
    public void Projection_TheVerticalFovReachesTheTopAndBottomEdges()
    {
        foreach (var fov in new[] { 0.5, 5.0, 30.0, 90.0, 140.0 })
        {
            var view = NewView(az: 90, alt: 10, fov: fov);
            view.TryProject(SkyView.FromAzAlt(90, 10 + fov / 2), out _, out var top);
            view.TryProject(SkyView.FromAzAlt(90, 10 - fov / 2), out _, out var bottom);
            Check.Near(0.0, top, 1e-4 * 800, $"fov {fov}: half the field above the centre reaches the top edge");
            Check.Near(800.0, bottom, 1e-4 * 800, $"fov {fov}: and half below reaches the bottom");
        }
    }

    [TestMethod]
    public void Projection_IsInvertible_EverywhereOnScreen()
    {
        foreach (var fov in new[] { 0.2, 3.0, 40.0, 120.0 })
        {
            var view = NewView(az: 123, alt: -20, fov: fov);
            for (double sx = 0; sx <= 1280; sx += 160)
                for (double sy = 0; sy <= 800; sy += 100)
                {
                    var h = view.Unproject(sx, sy);
                    Check.That(view.TryProject(h, out var px, out var py), "projects back");
                    // the vectors are 32-bit floats: 6e-8 of a radian is a pixel's fraction that grows with the zoom (230,000 pixels per radian at 0.2 degrees)
                    var tolerance = Math.Max(1e-2, 3e-7 * view.Scale * 2.0 / (1.0 + Math.Max(Vector3.Dot(h, view.Basis.Forward), -0.99)));
                    Check.That(Math.Abs(px - sx) < tolerance && Math.Abs(py - sy) < tolerance, $"fov {fov}: ({sx},{sy}) -> ({px:F4},{py:F4}), tolerance {tolerance:F4}");
                }
        }
        var wide = NewView(fov: 170);
        Check.That(!wide.TryProject(-wide.Basis.Forward, out _, out _), "the point opposite the centre is not on the chart");
    }

    [TestMethod]
    public void Projection_IsConformal_AnglesAndScalesAreTrueInEveryDirection()
    {
        // The stereographic projection preserves angles: two short perpendicular steps on the sky stay perpendicular, and equal, on the screen,
        // wherever they are. (Other projections squash shapes at the edges of a wide chart.)
        var view = NewView(az: 0, alt: 0, fov: 150);
        foreach (var (az, alt) in new[] { (0.0, 10.0), (30.0, 10.0), (60.0, 10.0), (350.0, -40.0), (90.0, 80.0), (200.0, 5.0) })
        {
            var p = SkyView.FromAzAlt(az, alt);
            if (!view.TryProject(p, out var cx, out var cy)) continue;
            var east = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, p));
            var north = Vector3.Cross(p, east);
            const double eps = 0.02 * Math.PI / 180;
            var pe = Vector3.Normalize(p + east * (float)eps);
            var pn = Vector3.Normalize(p + north * (float)eps);
            view.TryProject(pe, out var ex, out var ey);
            view.TryProject(pn, out var nx, out var ny);
            double e0 = ex - cx, e1 = ey - cy, n0 = nx - cx, n1 = ny - cy;
            var lenE = Math.Sqrt(e0 * e0 + e1 * e1);
            var lenN = Math.Sqrt(n0 * n0 + n1 * n1);
            Check.That(Math.Abs(lenE / lenN - 1) < 0.01, $"({az},{alt}): east and north steps are the same length on screen ({lenE:F4} vs {lenN:F4})");
            var cosAngle = (e0 * n0 + e1 * n1) / (lenE * lenN);
            Check.That(Math.Abs(cosAngle) < 0.01, $"({az},{alt}): and perpendicular (cos of the angle between them {cosAngle:F5})");
        }
    }

    [TestMethod]
    public void PixelsPerArcminute_MatchesProjectedSeparation()
    {
        var view = NewView(az: 200, alt: 35, fov: 20);
        var h = SkyView.FromAzAlt(205, 38);
        view.TryProject(h, out var x0, out var y0);
        var h2 = SkyView.FromAzAlt(205, 38 + 1.0 / 60.0); // one arcminute up
        view.TryProject(h2, out var x1, out var y1);
        var measured = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        Check.Near(view.PixelsPerArcminute(h), measured, measured * 0.01, "pixels per arcminute");
    }

    [TestMethod]
    public void Drag_KeepsTheGrabbedStarUnderThePointer()
    {
        var view = NewView(az: 100, alt: 45, fov: 60);
        view.BeginDrag(900, 300);
        var (az0, alt0) = SkyView.ToAzAlt(view.Unproject(900, 300));
        foreach (var (sx, sy) in new[] { (800.0, 350.0), (400.0, 500.0), (1100.0, 100.0), (640.0, 400.0) })
        {
            view.DragTo(sx, sy);
            var (az1, alt1) = SkyView.ToAzAlt(view.Unproject(sx, sy));
            var sep = AstronomyMath.AngularSeparation(az0, alt0, az1, alt1);
            // the view keeps the zenith up (no roll), so a far-off drag cannot be matched exactly: under 0.02 degrees is a fraction of a pixel at this zoom
            Check.That(sep < 0.02, $"after dragging to ({sx},{sy}) the grabbed point is {sep} degrees from the pointer");
        }
    }

    [TestMethod]
    public void Zoom_KeepsThePointUnderThePointer_AndClamps()
    {
        var view = NewView(az: 250, alt: 20, fov: 80);
        var (az0, alt0) = SkyView.ToAzAlt(view.Unproject(1000, 200));
        foreach (var factor in new[] { 0.5, 0.5, 0.7, 1.4, 2.0 })
        {
            view.ZoomAt(1000, 200, factor);
            var (az1, alt1) = SkyView.ToAzAlt(view.Unproject(1000, 200));
            Check.That(AstronomyMath.AngularSeparation(az0, alt0, az1, alt1) < 1e-3, $"zoom {factor}: the point moved {AstronomyMath.AngularSeparation(az0, alt0, az1, alt1)} degrees");
        }
        for (int i = 0; i < 40; i++) view.ZoomAt(640, 400, 0.5);
        Check.Near(SkyView.MinFov, view.FovDegrees, 1e-12, "cannot zoom in past the limit");
        for (int i = 0; i < 40; i++) view.ZoomAt(640, 400, 2.0);
        Check.Near(SkyView.MaxFov, view.FovDegrees, 1e-12, "or out past the whole sky");
    }

    [TestMethod]
    public void LookingStraightUpOrDown_DoesNotBreak()
    {
        foreach (var alt in new[] { 90.0, -90.0, 89.9999 })
        {
            var view = NewView(az: 37, alt: alt, fov: 60);
            Check.That(view.TryProject(SkyView.FromAzAlt(37, alt), out var x, out var y), "projects");
            Check.That(double.IsFinite(x) && double.IsFinite(y) && Math.Abs(x - 640) < 1e-2 && Math.Abs(y - 400) < 1e-2, $"alt {alt}: centre at ({x},{y})");
            var h = view.Unproject(100, 700);
            Check.That(Math.Abs(h.Length() - 1) < 1e-5, "unit vector");
        }
    }

    [TestMethod]
    public void MagnitudeLimit_GrowsAsYouZoomIn_AndStaysInTheCatalog()
    {
        var wide = NewView(fov: 120).MagnitudeLimit;
        var mid = NewView(fov: 30).MagnitudeLimit;
        var narrow = NewView(fov: 2).MagnitudeLimit;
        Check.That(wide < mid && mid < narrow, $"{wide} < {mid} < {narrow}");
        Check.Near(5.6, wide, 0.01, "the naked-eye sky at 120 degrees");
        Check.That(narrow <= 12.0 && NewView(fov: 0.05).MagnitudeLimit == 12.0, "never beyond the catalog");
        Check.That(NewView(fov: 175).MagnitudeLimit >= 4.8, "and a sensible floor");
    }

    [TestMethod]
    public void TheStarsOnScreen_AreWhereAKnownSkyPutsThem()
    {
        // Orion on a winter evening from New York: Betelgeuse and Rigel are on the meridian's south-east side, Betelgeuse above and left of Rigel's ... check the layout.
        var utc = new DateTime(2026, 1, 15, 3, 0, 0, DateTimeKind.Utc); // 22:00 EST
        var view = NewView(az: 175, alt: 40, fov: 70, utc: utc);
        var betelgeuse = view.ToHorizontal(CatalogFormats.UnitVector(88.792939, 7.407064));
        var rigel = view.ToHorizontal(CatalogFormats.UnitVector(78.634467, -8.201638));
        var (azB, altB) = SkyView.ToAzAlt(betelgeuse);
        var (azR, altR) = SkyView.ToAzAlt(rigel);
        // Betelgeuse transits at 90 - 40.7 + 7.4 = 56.7 degrees, Rigel at 90 - 40.7 - 8.2 = 41.1; at 22:00 both are within a few degrees of that
        Check.That(altB > 50 && altB < 57 && altR > 38 && altR < 42, $"Orion is near the meridian at 10 pm in January: Betelgeuse {altB:F1}, Rigel {altR:F1}");
        Check.That(azB > 150 && azB < 210, $"Betelgeuse is near due south: az {azB:F1}");
        Check.That(altB > altR, "Betelgeuse (the shoulder) is higher than Rigel (the foot)");
        view.TryProject(betelgeuse, out var bx, out var by);
        view.TryProject(rigel, out var rx, out var ry);
        Check.That(by < ry, "and so higher on the screen");
        Check.That(bx > 0 && bx < 1280 && by > 0 && by < 800 && rx > 0 && rx < 1280 && ry > 0 && ry < 800, "both are on screen");
    }
}
