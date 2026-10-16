using System.Numerics;
using SpawnDev.NexStar.Sky;
using SpawnDev.UnitTesting;

namespace SpawnDev.NexStar.Tests;

/// <summary>Picking the object under a tap, on the catalogs the app ships.</summary>
public class SkyPickerTests
{
    private static readonly Lazy<Catalogs> Data = new(LoadCatalogs);

    private sealed record Catalogs(CatalogStar[] Stars, Dictionary<uint, StarName> StarNames, DeepSkyObject[] DeepSky, DeepSkyName[] DeepSkyNames);

    private static Catalogs LoadCatalogs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SpawnDev.NexStar.slnx"))) dir = dir.Parent;
        Check.That(dir != null, "found the repository root above the test binaries");
        var data = Path.Combine(dir!.FullName, "SpawnDev.NexStar.App", "wwwroot", "data");
        var stars = File.ReadAllBytes(Path.Combine(data, "stars.bin"));
        var header = StarCatalog.ReadHeader(stars);
        var bright = SkyData.ReadPrefix(stars[..(int)(CatalogFormats.HeaderBytes + (long)header.BrightCount * CatalogFormats.RecordBytes)], header.BrightCount);
        var dsoBytes = File.ReadAllBytes(Path.Combine(data, "dso.bin"));
        var dso = new DeepSkyObject[DeepSkyCatalog.ReadCount(dsoBytes)];
        for (int i = 0; i < dso.Length; i++) dso[i] = DeepSkyCatalog.Read(dsoBytes, i);
        return new Catalogs(bright, SkyData.ParseStarNames(File.ReadAllText(Path.Combine(data, "star-names.json"))), dso,
            SkyData.ParseDeepSkyNames(File.ReadAllText(Path.Combine(data, "dso-names.json"))));
    }

    private static SkyView NewView(double az, double alt, double fov, DateTime? utc = null)
    {
        var v = new SkyView
        {
            Latitude = 40.7484, Longitude = -73.9857,
            Utc = utc ?? new DateTime(2026, 1, 15, 3, 0, 0, DateTimeKind.Utc),
            CenterAzimuth = az, CenterAltitude = alt, FovDegrees = fov, WidthPx = 1280, HeightPx = 800,
        };
        v.Update();
        return v;
    }

    private static SkyObject? Pick(SkyView view, double x, double y, double radius = 14, IReadOnlyList<SkyObject>? solar = null)
    {
        var d = Data.Value;
        return SkyPicker.Pick(view, x, y, radius, d.Stars, d.StarNames, d.DeepSky, d.DeepSkyNames, solar ?? System.Array.Empty<SkyObject>());
    }

    [TestMethod]
    public void ATapOnAFamousStar_PicksThatStar_ByName()
    {
        var d = Data.Value;
        var utc = new DateTime(2026, 1, 15, 3, 0, 0, DateTimeKind.Utc);
        int found = 0;
        foreach (var (name, hip) in new[] { ("Sirius", 32349u), ("Rigel", 24436u), ("Betelgeuse", 27989u), ("Procyon", 37279u), ("Aldebaran", 21421u), ("Capella", 24608u), ("Pollux", 37826u), ("Regulus", 49669u) })
        {
            var star = d.Stars.First(s => s.Hip == hip);
            var probe = NewView(180, 40, 60, utc);
            var (az, alt) = SkyView.ToAzAlt(probe.ToHorizontal(star.Position));
            if (alt < 3) continue;   // below the horizon at this hour
            var view = NewView(az, alt, 40, utc);
            Check.That(view.TryProject(view.ToHorizontal(star.Position), out var sx, out var sy), $"{name} projects");
            var picked = Pick(view, sx + 3, sy - 2);
            Check.That(picked != null && picked.Kind == SkyObjectKind.Star && picked.Name == name, $"a tap 3 px from {name} picked {picked?.Name ?? "nothing"}");
            Check.Near(star.Magnitude, picked!.Magnitude!.Value, 1e-6, $"{name} magnitude");
            Check.Equal(hip, picked.CatalogNumber, $"{name} catalog number");
            found++;
        }
        Check.That(found >= 4, $"only {found} of the stars were up: the check proves nothing");
    }

    [TestMethod]
    public void ATapOnEmptySky_PicksNothing()
    {
        var d = Data.Value;
        var view = NewView(200, 60, 8);
        int empty = 0, tried = 0;
        var rng = new Random(3);
        for (int i = 0; i < 400; i++)
        {
            var x = rng.NextDouble() * 1280; var y = rng.NextDouble() * 800;
            var near = d.Stars.Any(s => view.TryProject(view.ToHorizontal(s.Position), out var px, out var py) && Math.Abs(px - x) < 40 && Math.Abs(py - y) < 40);
            var nearDso = d.DeepSky.Any(o => view.TryProject(view.ToHorizontal(o.Position), out var px, out var py) && Math.Abs(px - x) < 40 + o.MajorAxisArcmin * view.PixelsPerArcminute(view.ToHorizontal(o.Position)) && Math.Abs(py - y) < 40 + o.MajorAxisArcmin * view.PixelsPerArcminute(view.ToHorizontal(o.Position)));
            if (near || nearDso) continue;
            tried++;
            if (Pick(view, x, y) == null) empty++;
        }
        Check.That(tried > 20, $"only {tried} blank spots were found: the check proves nothing");
        Check.Equal(tried, empty, "every tap on blank sky picks nothing");
    }

    [TestMethod]
    public void TheBrighterStar_WinsWhenTwoAreUnderTheTap()
    {
        var view = NewView(180, 40, 30);
        var (right, up, forward) = view.Basis;
        // two stars 4 px apart on screen
        view.TryProject(Vector3.Normalize(forward), out var cx, out var cy);
        var a = view.ToJ2000(Vector3.Normalize(forward));
        var b = view.ToJ2000(view.Unproject(cx + 4, cy));
        var stars = new[]
        {
            new CatalogStar(a, 1.0f, 0, 0, 0, 1001),
            new CatalogStar(b, 5.0f, 0, 0, 0, 1002),
        };
        var names = new Dictionary<uint, StarName> { [1001] = new("Bright", null, "Ori"), [1002] = new("Faint", null, "Ori") };
        // 1.5 px from the faint one, 2.5 px from the bright one
        var picked = SkyPicker.Pick(view, cx + 2.5, cy, 14, stars, names, System.Array.Empty<DeepSkyObject>(), System.Array.Empty<DeepSkyName>(), System.Array.Empty<SkyObject>());
        Check.Equal("Bright", picked?.Name, "the bright star wins at 2.5 vs 1.5 px");
        // right on top of the faint one it is the faint one
        var onFaint = SkyPicker.Pick(view, cx + 4, cy, 14, stars, names, System.Array.Empty<DeepSkyObject>(), System.Array.Empty<DeepSkyName>(), System.Array.Empty<SkyObject>());
        Check.Equal("Faint", onFaint?.Name, "but not when the tap is on the faint one and 4 px from the bright one");
    }

    [TestMethod]
    public void TheSun_IsPickable_AndNeverATarget()
    {
        var utc = new DateTime(2026, 6, 21, 16, 0, 0, DateTimeKind.Utc);   // noon in New York
        var probe = NewView(180, 60, 60, utc);
        var solar = SkyPicker.SolarSystem(probe);
        var sun = solar.Single(o => o.Kind == SkyObjectKind.Sun);
        Check.That(sun.Altitude > 50, $"the Sun is high at local noon in June: {sun.Altitude:F1}");
        Check.That(!sun.CanGoto, "the Sun can never be a goto target");
        var view = NewView(sun.Azimuth, sun.Altitude, 30, utc);
        Check.That(view.TryProject(sun.Direction, out var sx, out var sy), "the Sun projects");
        var picked = Pick(view, sx, sy, 14, solar);
        Check.That(picked != null && picked.Kind == SkyObjectKind.Sun && !picked.CanGoto, $"a tap on the Sun picked {picked?.Name ?? "nothing"}");
        foreach (var o in solar.Where(o => o.Kind != SkyObjectKind.Sun)) Check.That(o.CanGoto, $"{o.Name} can be a target");
    }

    [TestMethod]
    public void TheSolarSystem_HasThePlanetsAndTheMoon_WhereTheEphemerisPutsThem()
    {
        var view = NewView(180, 40, 90, new DateTime(2026, 10, 16, 3, 0, 0, DateTimeKind.Utc));
        var solar = SkyPicker.SolarSystem(view);
        foreach (var name in new[] { "Mercury", "Venus", "Mars", "Jupiter", "Saturn", "Uranus", "Neptune", "Moon", "Sun" })
            Check.That(solar.Any(o => o.Name == name), $"{name} is in the list");
        var jupiter = solar.Single(o => o.Name == "Jupiter");
        var expected = SolarSystemMath.GetAzAlt(SolarSystemObject.Jupiter, view.Latitude, view.Longitude, view.Utc);
        Check.Near(expected.Azimuth, jupiter.Azimuth, 1e-9, "Jupiter azimuth"); Check.Near(expected.Altitude, jupiter.Altitude, 1e-9, "Jupiter altitude");
        var moon = solar.Single(o => o.Kind == SkyObjectKind.Moon);
        var m = LunarMath.GetMoonAzAlt(view.Latitude, view.Longitude, view.Utc);
        Check.Near(m.Azimuth, moon.Azimuth, 1e-9, "Moon azimuth (topocentric)");
        Check.That(moon.Detail!.Contains("% lit"), $"the Moon says how much is lit: {moon.Detail}");
    }

    [TestMethod]
    public void TheAndromedaGalaxy_IsPickedByItsName_OnItsCoreAndInsideItsOutline()
    {
        var d = Data.Value;
        var index = Array.FindIndex(d.DeepSky, o => o.Messier == 31);
        Check.That(index >= 0, "M31 is in the catalog");
        var m31 = d.DeepSky[index];
        Check.That(d.DeepSkyNames[index].Messier == 31, "the names file is in the same order");
        // look at it from wherever it is up on this night
        var utc = new DateTime(2026, 10, 16, 3, 0, 0, DateTimeKind.Utc);
        var probe = NewView(90, 40, 60, utc);
        var (az, alt) = SkyView.ToAzAlt(probe.ToHorizontal(m31.Position));
        Check.That(alt > 10, $"M31 is up at 11 pm in October: {alt:F1}");
        var view = NewView(az, alt, 6, utc);
        view.TryProject(view.ToHorizontal(m31.Position), out var sx, out var sy);
        var core = Pick(view, sx + 1, sy + 1);
        Check.That(core != null && core.Kind == SkyObjectKind.DeepSky && core.Name.StartsWith("M31"), $"the core picked {core?.Name ?? "nothing"}");
        Check.That(core!.Detail!.StartsWith("Galaxy"), $"its type is shown: {core.Detail}");
        // a third of the way along the major axis, well outside the usual tap radius
        var pxPerArcmin = view.PixelsPerArcminute(view.ToHorizontal(m31.Position));
        var offset = m31.MajorAxisArcmin / 3.0 * pxPerArcmin;
        Check.That(offset > 30, $"the test point is outside the tap radius: {offset:F0} px");
        var angle = m31.PositionAngle * Math.PI / 180.0;
        // moving along the major axis toward the zenith-ish direction is fiddly; try both ends and the two across-axis points: at least the axis ends hit
        int hits = 0;
        foreach (var sign in new[] { -1, 1 })
        {
            var dirJ = m31.Position;
            // step along the position angle (north through east) on the sky in J2000, then project
            var (ra, dec) = CatalogFormats.ToRaDec(dirJ);
            var step = sign * m31.MajorAxisArcmin / 3.0 / 60.0;
            var dRa = step * Math.Sin(angle) / Math.Cos(dec * Math.PI / 180.0);
            var dDec = step * Math.Cos(angle);
            var along = CatalogFormats.UnitVector(ra + dRa, dec + dDec);
            if (!view.TryProject(view.ToHorizontal(along), out var ax, out var ay)) continue;
            var picked = Pick(view, ax, ay);
            if (picked != null && picked.Name.StartsWith("M31")) hits++;
        }
        Check.Equal(2, hits, "both points a third of the way out along the major axis pick M31");
    }

    [TestMethod]
    public void EverythingPickedIsAboveTheCatalogLimit_NotInvisibleStars()
    {
        // the star list is sorted brightest first and the picker stops at the chart's magnitude limit: nothing fainter than what is drawn can be picked
        var d = Data.Value;
        var faint = d.Stars.Last();
        Check.That(faint.Magnitude > 6.0, $"the last bright-catalog star is faint: {faint.Magnitude}");
        var probe = NewView(180, 40, 150);
        var (az, alt) = SkyView.ToAzAlt(probe.ToHorizontal(faint.Position));
        var wide = NewView(az, alt, 150);   // limit about 5.1, and the faint star in the middle
        Check.That(wide.MagnitudeLimit < faint.Magnitude - 0.5, $"a wide view shows only the brighter stars: limit {wide.MagnitudeLimit}, star {faint.Magnitude}");
        Check.That(wide.TryProject(wide.ToHorizontal(faint.Position), out var sx, out var sy) && Math.Abs(sx - 640) < 2 && Math.Abs(sy - 400) < 2, "the faint star is at the centre of the view");
        var picked = Pick(wide, sx, sy);
        Check.That(picked == null || picked.CatalogNumber != faint.Hip, "a star too faint for this view is not picked");
    }

    [TestMethod]
    public void ASelectedObject_FollowsTheSky_AsTimePasses()
    {
        var d = Data.Value;
        var vega = d.Stars.First(s => s.Hip == 91262);
        var t0 = new DateTime(2026, 7, 15, 3, 0, 0, DateTimeKind.Utc);
        var view0 = NewView(180, 40, 60, t0);
        var (az0, alt0) = SkyView.ToAzAlt(view0.ToHorizontal(vega.Position));
        var picked = new SkyObject(SkyObjectKind.Star, "Vega", "Star, Lyra", az0, alt0, 0.03, null, 91262, vega.Position);
        var view1 = NewView(180, 40, 60, t0.AddHours(3));
        var moved = SkyPicker.Refresh(picked, view1, SkyPicker.SolarSystem(view1));
        var (az1, alt1) = SkyView.ToAzAlt(view1.ToHorizontal(vega.Position));
        Check.Near(az1, moved.Azimuth, 1e-9, "azimuth after 3 hours"); Check.Near(alt1, moved.Altitude, 1e-9, "altitude after 3 hours");
        Check.That(Math.Abs(az1 - az0) > 20, $"the sky really turned: {az0:F1} -> {az1:F1}");

        // a planet is found again by name in the new solar system
        var solar0 = SkyPicker.SolarSystem(view0);
        var jupiter = solar0.Single(o => o.Name == "Jupiter");
        var solar1 = SkyPicker.SolarSystem(view1);
        var jupiterLater = SkyPicker.Refresh(jupiter, view1, solar1);
        var expected = solar1.Single(o => o.Name == "Jupiter");
        Check.Near(expected.Azimuth, jupiterLater.Azimuth, 1e-9, "Jupiter azimuth after 3 hours");
        Check.That(Math.Abs(jupiterLater.Azimuth - jupiter.Azimuth) > 10, "Jupiter moved across the sky too");
    }
}
