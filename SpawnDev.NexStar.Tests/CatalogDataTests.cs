using System.Numerics;
using System.Text.Json;
using SpawnDev.NexStar.Sky;
using SpawnDev.UnitTesting;

namespace SpawnDev.NexStar.Tests;

/// <summary>
/// The sky catalog files the app ships, held to independent ground truth: the positions of famous stars from the IAU Catalog of Star Names
/// (a different compilation from the Hipparcos file the stars were built from), and of famous deep-sky objects.
/// </summary>
public class CatalogDataTests
{
    private static string DataDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SpawnDev.NexStar.slnx"))) dir = dir.Parent;
            Check.That(dir != null, "found the repository root above the test binaries");
            var data = Path.Combine(dir!.FullName, "SpawnDev.NexStar.App", "wwwroot", "data");
            Check.That(Directory.Exists(data), $"the data folder exists: {data}");
            return data;
        }
    }

    private static byte[] Load(string file) => File.ReadAllBytes(Path.Combine(DataDir, file));

    private static double SeparationArcsec(Vector3 v, double raDeg, double decDeg)
    {
        var (ra, dec) = CatalogFormats.ToRaDec(v);
        return AstronomyMath.AngularSeparation(ra, dec, raDeg, decDeg) * 3600.0;
    }

    // Name, HIP, V magnitude, RA and Dec J2000 degrees: from the IAU Catalog of Star Names (IAU-CSN.txt, WGSN)
    private static readonly (string Name, uint Hip, double Mag, double Ra, double Dec)[] FamousStars =
    {
        ("Achernar", 7588, 0.45, 24.428523, -57.236753), ("Acrux", 60718, 1.33, 186.649563, -63.099093), ("Albireo", 95947, 3.37, 292.680351, 27.959692),
        ("Aldebaran", 21421, 0.87, 68.980163, 16.509302), ("Alpheratz", 677, 2.07, 2.096916, 29.090431), ("Altair", 97649, 0.76, 297.695827, 8.868321),
        ("Antares", 80763, 1.06, 247.351915, -26.432003), ("Arcturus", 69673, -0.05, 213.915300, 19.182409), ("Betelgeuse", 27989, 0.45, 88.792939, 7.407064),
        ("Canopus", 30438, -0.62, 95.987958, -52.695661), ("Capella", 24608, 0.08, 79.172328, 45.997991), ("Deneb", 102098, 1.25, 310.357980, 45.280339),
        ("Dubhe", 54061, 1.81, 165.931965, 61.751035), ("Fomalhaut", 113368, 1.17, 344.412693, -29.622237), ("Mizar", 65378, 2.23, 200.981429, 54.925362),
        ("Polaris", 11767, 2.13, 37.954561, 89.264109), ("Pollux", 37826, 1.16, 116.328958, 28.026199), ("Procyon", 37279, 0.40, 114.825493, 5.224993),
        ("Regulus", 49669, 1.36, 152.092962, 11.967209), ("Rigel", 24436, 0.18, 78.634467, -8.201638), ("Sirius", 32349, -1.45, 101.287155, -16.716116),
        ("Spica", 65474, 0.98, 201.298247, -11.161319), ("Vega", 91262, 0.03, 279.234735, 38.783689),
    };

    [TestMethod]
    public void Stars_FileIsWellFormed_SortedBrightestFirst_AndComplete()
    {
        var data = Load("stars.bin");
        var h = StarCatalog.ReadHeader(data);
        Check.That(h.Count > 117000 && h.Count < 119000, $"the Hipparcos catalogue has 118,218 stars, 263 without a position: {h.Count}");
        Check.That(h.BrightCount > 8500 && h.BrightCount < 9300, $"about 8,900 stars are brighter than 6.5: {h.BrightCount}");
        Check.Equal(2000.0f, h.Epoch, "epoch");
        float prev = float.MinValue;
        var hips = new HashSet<uint>();
        for (int i = 0; i < h.Count; i++)
        {
            var s = StarCatalog.Read(data, i);
            Check.That(s.Magnitude >= prev, $"star {i} is out of order: {s.Magnitude} after {prev}");
            prev = s.Magnitude;
            Check.That(Math.Abs(s.Position.Length() - 1f) < 1e-5f, $"star {i} position is not a unit vector: {s.Position.Length()}");
            Check.That(hips.Add(s.Hip), $"HIP {s.Hip} appears twice");
            if (i == h.BrightCount - 1) Check.That(s.Magnitude <= StarCatalog.BrightMagnitudeLimit, "the bright prefix ends inside the limit");
            if (i == h.BrightCount) Check.That(s.Magnitude > StarCatalog.BrightMagnitudeLimit, "and the next star is fainter");
        }
        Check.That(StarCatalog.Read(data, 0).Magnitude < -1.0f && StarCatalog.Read(data, 0).Hip == 32349, "the brightest star is Sirius");
    }

    [TestMethod]
    public void Stars_FamousStarsAreWhereTheIauSaysTheyAre()
    {
        var data = Load("stars.bin");
        var h = StarCatalog.ReadHeader(data);
        var byHip = Enumerable.Range(0, h.Count).Select(i => StarCatalog.Read(data, i)).ToDictionary(s => s.Hip);
        double worst = 0;
        foreach (var (name, hip, mag, ra, dec) in FamousStars)
        {
            Check.That(byHip.TryGetValue(hip, out var s), $"{name} (HIP {hip}) is in the catalog");
            var err = SeparationArcsec(s.Position, ra, dec);
            worst = Math.Max(worst, err);
            // two independent compilations: Sirius moves 1.3 arcsec a year, so a few arcseconds is the proof the epoch was carried to J2000
            Check.That(err < 8, $"{name}: {err:F2} arcsec from the IAU position");
            // Hipparcos lists a close pair as one blended entry where the IAU names one component (Acrux A is 1.33, A+B together 0.77), so only singles are held to it
            if (name is not ("Acrux" or "Albireo" or "Mizar"))
                Check.That(Math.Abs(s.Magnitude - mag) < 0.25, $"{name}: magnitude {s.Magnitude} vs {mag} (variables such as Polaris differ by tenths between compilations; a parse error would be off by whole magnitudes)");
        }
        Console.WriteLine($"[Catalog] worst star position difference from the IAU list: {worst:F2} arcsec over {FamousStars.Length} stars");
    }

    [TestMethod]
    public void Stars_ProperMotionWasCarriedToJ2000()
    {
        // Barnard's star (HIP 87937) moves 10.3 arcsec a year: left at J1991.25 it would be 90 arcsec from its J2000 place, J2000 RA 269.452 Dec +4.693.
        var data = Load("stars.bin");
        var h = StarCatalog.ReadHeader(data);
        var barnard = Enumerable.Range(0, h.Count).Select(i => StarCatalog.Read(data, i)).First(s => s.Hip == 87937);
        Check.That(SeparationArcsec(barnard.Position, 269.4520, 4.6934) < 12, $"Barnard's star: {SeparationArcsec(barnard.Position, 269.4520, 4.6934):F1} arcsec from its J2000 position");
        Check.That(barnard.PmDec > 0 && Math.Abs(barnard.PmDec * 180 / Math.PI * 3600 - 10.3) < 0.3, $"its proper motion in Dec is stored in radians a year: {barnard.PmDec * 180 / Math.PI * 3600:F2} arcsec/yr");
    }

    [TestMethod]
    public void StarNames_ArePresentForTheStarsThatHaveThem()
    {
        using var doc = JsonDocument.Parse(Load("star-names.json"));
        var names = doc.RootElement.EnumerateArray().ToList();
        Check.That(names.Count > 380, $"the IAU list names about 450 stars, the ones with a Hipparcos number: {names.Count}");
        var byName = names.ToDictionary(n => n.GetProperty("name").GetString()!, n => n.GetProperty("hip").GetUInt32());
        foreach (var (name, hip, _, _, _) in FamousStars) Check.Equal(hip, byName[name], $"{name} points at HIP {hip}");
        Check.That(names.Any(n => n.TryGetProperty("bayer", out var b) && b.GetString() == "α"), "Bayer letters came through as Greek");
    }

    [TestMethod]
    public void DeepSky_MessierAndFamousObjects()
    {
        var data = Load("dso.bin");
        var n = DeepSkyCatalog.ReadCount(data);
        using var doc = JsonDocument.Parse(Load("dso-names.json"));
        var info = doc.RootElement.EnumerateArray().ToList();
        Check.Equal(n, info.Count, "the names file is parallel to the binary file");
        Check.That(n > 12000 && n < 14000, $"NGC and IC without the non-existent, duplicates and plain stars: {n}");
        float prev = float.MinValue;
        var messier = new HashSet<int>();
        for (int i = 0; i < n; i++)
        {
            var o = DeepSkyCatalog.Read(data, i);
            Check.That(o.Magnitude >= prev, $"deep-sky object {i} is out of order");
            prev = o.Magnitude;
            Check.That(Math.Abs(o.Position.Length() - 1f) < 1e-5f, "unit vector");
            if (o.Messier != 0) { messier.Add(o.Messier); Check.Equal(o.Messier, info[i].GetProperty("m").GetInt32(), "the Messier number is in both files"); }
        }
        var missing = Enumerable.Range(1, 110).Where(m => !messier.Contains(m)).ToList();
        Check.That(missing.SequenceEqual(new[] { 40 }), $"all Messier objects but M40 (a double star that is in neither NGC nor IC): missing {string.Join(",", missing)}");

        // J2000 positions of famous objects
        (string id, double ra, double dec)[] expected =
        {
            ("NGC 224", 10.6847, 41.2689),     // M31 Andromeda Galaxy
            ("NGC 1976", 83.8221, -5.3911),    // M42 Orion Nebula
            ("NGC 6205", 250.4218, 36.4599),   // M13 Hercules Cluster
            ("NGC 7089", 323.3626, -0.8233),   // M2
            ("NGC 5457", 210.8023, 54.3490),   // M101 Pinwheel
        };
        foreach (var (id, ra, dec) in expected)
        {
            var i = info.FindIndex(x => x.GetProperty("id").GetString() == id);
            Check.That(i >= 0, $"{id} is in the catalog");
            var o = DeepSkyCatalog.Read(data, i);
            Check.That(SeparationArcsec(o.Position, ra, dec) < 40, $"{id}: {SeparationArcsec(o.Position, ra, dec):F1} arcsec from the reference position");
        }
        var m31 = DeepSkyCatalog.Read(data, info.FindIndex(x => x.GetProperty("id").GetString() == "NGC 224"));
        Check.That(m31.Type == DeepSkyType.Galaxy && m31.Messier == 31 && m31.Magnitude < 4.5 && m31.MajorAxisArcmin > 150, $"M31: {m31}");
        Check.That(info.First(x => x.GetProperty("id").GetString() == "NGC 224").GetProperty("name").GetString()!.Contains("Andromeda"), "and its common name");
    }

    [TestMethod]
    public void Constellations_LinesJoinRealStars_AndBoundariesAreContinuous()
    {
        var stars = Load("stars.bin");
        var h = StarCatalog.ReadHeader(stars);
        var positions = Enumerable.Range(0, h.Count).Select(i => StarCatalog.Read(stars, i).Position).ToList();
        var lines = Load("constellation-lines.bin");
        var n = LineCatalog.ReadCount(lines);
        Check.That(n > 600 && n < 800, $"line segments: {n}");
        for (int i = 0; i < n; i++)
        {
            var (a, b) = LineCatalog.Read(lines, i);
            Check.That(positions.Any(p => Vector3.DistanceSquared(p, a) < 1e-10) && positions.Any(p => Vector3.DistanceSquared(p, b) < 1e-10), $"segment {i} joins catalog stars");
        }
        using var doc = JsonDocument.Parse(Load("constellations.json"));
        var cons = doc.RootElement.EnumerateArray().ToList();
        Check.Equal(88, cons.Count, "the 88 IAU constellations");
        var orion = cons.First(c => c.GetProperty("abbr").GetString() == "Ori");
        var oc = orion.GetProperty("center");
        var centre = new Vector3(oc[0].GetSingle(), oc[1].GetSingle(), oc[2].GetSingle());
        Check.That(SeparationArcsec(centre, 83.0, 0.0) < 12 * 3600, "Orion's label sits near RA 5.5h, Dec 0");

        // The IAU boundaries: segments of one outline share end points, so almost every end point is shared by exactly two segments.
        var edges = Load("constellation-edges.bin");
        var m = LineCatalog.ReadCount(edges);
        Check.That(m > 2000, $"boundary segments: {m}");
        var counts = new Dictionary<(int, int, int), int>();
        (int, int, int) Key(Vector3 v) => ((int)Math.Round(v.X * 20000), (int)Math.Round(v.Y * 20000), (int)Math.Round(v.Z * 20000));
        for (int i = 0; i < m; i++)
        {
            var (a, b) = LineCatalog.Read(edges, i);
            Check.That(Math.Abs(a.Length() - 1) < 1e-5 && Math.Abs(b.Length() - 1) < 1e-5, "unit vectors");
            Check.That(Vector3.Distance(a, b) < 0.06, $"segment {i} is short ({Vector3.Distance(a, b)}), so a parallel stays a curve");
            counts[Key(a)] = counts.GetValueOrDefault(Key(a)) + 1;
            counts[Key(b)] = counts.GetValueOrDefault(Key(b)) + 1;
        }
        var shared = counts.Values.Count(c => c >= 2);
        Check.That(shared > counts.Count * 0.9, $"the outlines are connected: {shared} of {counts.Count} end points are shared");
    }
}
