using System.Numerics;
using SpawnDev.NexStar.Sky;
using SpawnDev.NexStar.Vision;
using SpawnDev.UnitTesting;

namespace SpawnDev.NexStar.Tests;

/// <summary>
/// Plate solving on synthetic pictures made from the REAL Hipparcos catalog the app ships: a picture is projected from known sky coordinates with a known
/// rotation, scale and parity, with measurement noise, missed stars and false detections, and the solver must find the truth back (and must find
/// nothing in a picture that is not of the sky).
/// </summary>
public class PlateSolverTests
{
    private const int Width = 1280, Height = 720;
    private static readonly Lazy<CatalogStar[]> Catalog = new(LoadCatalog);

    private static CatalogStar[] LoadCatalog()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SpawnDev.NexStar.slnx"))) dir = dir.Parent;
        var bytes = File.ReadAllBytes(Path.Combine(dir!.FullName, "SpawnDev.NexStar.App", "wwwroot", "data", "stars.bin"));
        var header = StarCatalog.ReadHeader(bytes);
        var stars = new CatalogStar[header.Count];
        for (int i = 0; i < stars.Length; i++) stars[i] = StarCatalog.Read(bytes, i);
        return stars;
    }

    private static Vector3 Unit(double raDeg, double decDeg) => CatalogFormats.UnitVector(raDeg, decDeg);

    /// <summary>The catalog the solver is given: every star brighter than a limit within a radius of where the telescope is thought to point.</summary>
    private static List<CatalogEntry> Cone(Vector3 center, double radiusDeg, float magnitudeLimit)
    {
        double cosR = Math.Cos(radiusDeg * Math.PI / 180);
        var list = new List<CatalogEntry>();
        foreach (var s in Catalog.Value)
            if (s.Magnitude <= magnitudeLimit && Vector3.Dot(s.Position, center) >= cosR) list.Add(new CatalogEntry(s.Position, s.Magnitude, s.Hip));
        return list;
    }

    private sealed record Truth(Vector3 Center, double RaDeg, double DecDeg, double RollDeg, double ArcsecPerPixel, bool Mirrored);

    /// <summary>
    /// Projects the catalog about a centre into a picture. Up is at position angle <paramref name="rollDeg"/>; a mirrored picture is the sky seen in a mirror.
    /// Returns the detections and, for each, the catalog id it really is (0 for a false detection).
    /// </summary>
    private static (List<DetectedStar> Detections, List<uint> Ids, Truth Truth) Render(Random rng, double raDeg, double decDeg, double rollDeg, double arcsecPerPixel, bool mirrored,
        float magnitudeLimit, double noisePx, double dropFraction, int falseDetections)
    {
        var center = Unit(raDeg, decDeg);
        double cx = center.X, cy = center.Y, cz = center.Z;
        // east and north at the centre
        var east = Vector3.Normalize(Vector3.Cross(new Vector3(0, 0, 1), center));
        var north = Vector3.Cross(center, east);
        double sigma = mirrored ? -1 : 1;
        double roll = rollDeg * Math.PI / 180;
        double theta = mirrored ? -roll : Math.PI - roll;   // see PlateSolver: sigma * (Im a - i Re a) must point at the roll
        double scale = arcsecPerPixel * Math.PI / 180 / 3600;
        double aRe = scale * Math.Cos(theta), aIm = scale * Math.Sin(theta), den = aRe * aRe + aIm * aIm;
        var detections = new List<DetectedStar>();
        var ids = new List<uint>();
        foreach (var s in Catalog.Value)
        {
            if (s.Magnitude > magnitudeLimit) continue;
            double d = Vector3.Dot(s.Position, center);
            if (d < 0.5) continue;
            double xi = Vector3.Dot(s.Position, east) / d, eta = Vector3.Dot(s.Position, north) / d;
            // z = w / a
            double zx = (xi * aRe + eta * aIm) / den, zy = (eta * aRe - xi * aIm) / den;
            double x = zx, y = sigma * zy;
            if (Math.Abs(x) > Width / 2.0 - 4 || Math.Abs(y) > Height / 2.0 - 4) continue;
            if (rng.NextDouble() < dropFraction) continue;
            detections.Add(new DetectedStar(x + Gaussian(rng) * noisePx, y + Gaussian(rng) * noisePx, Math.Pow(10, -0.4 * s.Magnitude) * 1e4));
            ids.Add(s.Hip);
        }
        for (int i = 0; i < falseDetections; i++)
        {
            detections.Add(new DetectedStar((rng.NextDouble() - 0.5) * (Width - 20), (rng.NextDouble() - 0.5) * (Height - 20), Math.Pow(10, -0.4 * (8 + rng.NextDouble() * 3)) * 1e4));
            ids.Add(0);
        }
        return (detections, ids, new Truth(center, raDeg, decDeg, rollDeg, arcsecPerPixel, mirrored));
    }

    private static double Gaussian(Random rng) => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());

    /// <summary>The angle between two directions in degrees, from the cross product and dot product in double precision (acos of a float dot product cannot see below about 70 arcseconds).</summary>
    private static double AngleDeg(Vector3 a, Vector3 b)
    {
        double ax = a.X, ay = a.Y, az = a.Z, bx = b.X, by = b.Y, bz = b.Z;
        double cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
        return Math.Atan2(Math.Sqrt(cx * cx + cy * cy + cz * cz), ax * bx + ay * by + az * bz) * 180 / Math.PI;
    }

    private static double RollError(double a, double b) => Math.Abs(((a - b + 540) % 360) - 180);

    private static Vector3 PriorNear(Random rng, Vector3 truth, double maxDeg)
    {
        // a point up to maxDeg away in a random direction
        var east = Vector3.Normalize(Vector3.Cross(new Vector3(0, 0, 1), truth));
        var north = Vector3.Cross(truth, east);
        double r = maxDeg * Math.PI / 180 * Math.Sqrt(rng.NextDouble()), p = rng.NextDouble() * 2 * Math.PI;
        return Vector3.Normalize(truth + east * (float)(Math.Tan(r) * Math.Cos(p)) + north * (float)(Math.Tan(r) * Math.Sin(p)));
    }

    private static PlateSolution? SolveField(Random rng, double ra, double dec, double roll, double scale, bool mirrored, float magLimit, double noise, double drop, int falses, out Truth truth, double priorOffsetDeg = 2.0,
        PlateSolveOptions? options = null)
    {
        var (det, _, t) = Render(rng, ra, dec, roll, scale, mirrored, magLimit, noise, drop, falses);
        truth = t;
        double fovDeg = Math.Max(Width, Height) * scale / 3600;
        var prior = PriorNear(rng, t.Center, priorOffsetDeg);
        var cone = Cone(prior, priorOffsetDeg + fovDeg + 1, magLimit);
        options ??= new PlateSolveOptions { MinArcsecPerPixel = scale * 0.8, MaxArcsecPerPixel = scale * 1.25 };
        return PlateSolver.Solve(det, cone, prior, Width, Height, options);
    }

    private static void CheckSolution(PlateSolution? s, Truth t, double centerArcsec, string what)
    {
        Check.That(s != null, $"{what}: not solved");
        var sep = AngleDeg(s!.Center, t.Center) * 3600;
        Check.That(sep < centerArcsec, $"{what}: centre {sep:F1} arcsec off (allowed {centerArcsec})");
        Check.That(Math.Abs(s.ArcsecPerPixel / t.ArcsecPerPixel - 1) < 0.005, $"{what}: scale {s.ArcsecPerPixel:F3} vs {t.ArcsecPerPixel:F3}");
        Check.That(s.Mirrored == t.Mirrored, $"{what}: parity {s.Mirrored} vs {t.Mirrored}");
        Check.That(RollError(s.RollDegrees, t.RollDeg) < 0.2, $"{what}: roll {s.RollDegrees:F2} vs {t.RollDeg:F2}");
        Check.That(s.RmsPixels < 1.5, $"{what}: rms {s.RmsPixels:F2} px");
    }

    [TestMethod]
    public void AWideField_IsSolvedExactly_OverTheWholeSky_BothParities_AnyRoll()
    {
        var rng = new Random(2026);
        int solved = 0;
        for (int i = 0; i < 40; i++)
        {
            double ra = rng.NextDouble() * 360, dec = Math.Asin(rng.NextDouble() * 1.8 - 0.9) * 180 / Math.PI;
            double roll = rng.NextDouble() * 360;
            bool mirrored = i % 2 == 1;
            var s = SolveField(rng, ra, dec, roll, 30.0, mirrored, 9.0f, 0.3, 0.1, 3, out var truth);
            CheckSolution(s, truth, 15, $"field {i} at RA {ra:F1} Dec {dec:F1} roll {roll:F0} mirrored {mirrored}");
            solved++;
        }
        Check.Equal(40, solved, "every field was solved");
    }

    [TestMethod]
    public void ANarrowField_WithMissedAndFalseStars_IsSolved_WhenItHasEnoughStars_AndNeverSolvedWrong()
    {
        // 6 arcsec per pixel: a field about 2.1 x 1.2 degrees; Hipparcos to magnitude 11 has only a few stars there, so what is promised is: with seven or more real
        // stars in the picture it is solved, with fewer it may be refused, and a solution is never wrong
        var rng = new Random(77);
        int rich = 0, richSolved = 0, poor = 0, poorSolved = 0;
        var byReal = new SortedDictionary<int, (int Tried, int Solved)>();
        for (int i = 0; i < 100; i++)
        {
            double ra = rng.NextDouble() * 360, dec = Math.Asin(rng.NextDouble() * 1.6 - 0.8) * 180 / Math.PI;
            double roll = rng.NextDouble() * 360;
            bool mirrored = i % 3 == 0;
            var (det, ids, truth) = Render(rng, ra, dec, roll, 6.0, mirrored, 11.0f, 0.4, 0.25, 4);
            var prior = PriorNear(rng, truth.Center, 2.0);
            var cone = Cone(prior, 2.0 + 2.5 + 1, 11.0f);
            var s = PlateSolver.SolveSearch(det, cone, prior, 2.2, Width, Height, new PlateSolveOptions { MinArcsecPerPixel = 4.8, MaxArcsecPerPixel = 7.5 });
            int real = ids.Count(id => id != 0);
            if (real >= 7) { rich++; if (s != null) richSolved++; } else { poor++; if (s != null) poorSolved++; }
            byReal[Math.Min(real, 12)] = (byReal.GetValueOrDefault(Math.Min(real, 12)).Tried + 1, byReal.GetValueOrDefault(Math.Min(real, 12)).Solved + (s != null ? 1 : 0));
            if (s != null) CheckSolution(s, truth, 10, $"narrow field {i} ({real} real stars)");
        }
        Console.WriteLine($"[PlateSolver] narrow fields solved, by real stars in the picture: " + string.Join("  ", byReal.Select(k => $"{k.Key}:{k.Value.Solved}/{k.Value.Tried}")));
        Check.That(rich >= 20, $"only {rich} fields had seven or more stars: the check proves nothing");
        Check.That(richSolved >= rich * 0.9, $"only {richSolved} of {rich} fields with seven or more real stars were solved");
    }

    [TestMethod]
    public void APictureThatIsNotOfTheSky_IsNeverSolved()
    {
        var rng = new Random(5);
        int solvedWrongly = 0;
        long coneStars = 0;
        for (int i = 0; i < 60; i++)
        {
            double ra = rng.NextDouble() * 360, dec = Math.Asin(rng.NextDouble() * 1.8 - 0.9) * 180 / Math.PI;
            var prior = Unit(ra, dec);
            var cone = Cone(prior, 4, 10.0f);
            coneStars += cone.Count;
            // thirty stars scattered at random: plausible-looking, but not any piece of sky
            var det = Enumerable.Range(0, 30).Select(_ => new DetectedStar((rng.NextDouble() - 0.5) * 1200, (rng.NextDouble() - 0.5) * 680, rng.NextDouble() * 100 + 1)).ToList();
            var s = PlateSolver.Solve(det, cone, prior, Width, Height, new PlateSolveOptions { MinArcsecPerPixel = 8, MaxArcsecPerPixel = 14 });
            if (s != null) solvedWrongly++;
        }
        Check.Equal(0, solvedWrongly, "a random picture was 'solved'");
        Check.That(coneStars / 60 > 50, $"the cones had stars (about {coneStars / 60} each): the test is not vacuous");
    }

    [TestMethod]
    public void AMirroredPicture_IsRefused_NotSolvedWrong_WhenMirroringIsNotAllowed()
    {
        var rng = new Random(9);
        int wrong = 0, none = 0;
        for (int i = 0; i < 6; i++)
        {
            var s = SolveField(rng, 100 + i * 20, 20 + i * 4, 40, 30.0, mirrored: true, 9.0f, 0.3, 0.1, 2, out _, options: new PlateSolveOptions { MinArcsecPerPixel = 24, MaxArcsecPerPixel = 38, AllowMirrored = false });
            if (s == null) none++; else wrong++;
        }
        Check.That(wrong == 0, $"{wrong} mirrored pictures were solved as if not mirrored");
        Check.Equal(6, none, "all refused");
    }

    [TestMethod]
    public void FieldsAtThePolesAndOverTheRaWrap_AreSolved()
    {
        var rng = new Random(31);
        foreach (var (ra, dec) in new[] { (0.0, 89.0), (180.0, -89.0), (359.9, 0.0), (0.1, 0.0), (359.0, 60.0), (1.0, -60.0), (45.0, 88.7), (270.0, -88.7) })
        {
            var s = SolveField(rng, ra, dec, rng.NextDouble() * 360, 30.0, false, 9.0f, 0.3, 0.1, 2, out var truth);
            CheckSolution(s, truth, 20, $"field at RA {ra} Dec {dec}");
        }
    }

    [TestMethod]
    public void TooFewStars_IsNotASolution()
    {
        var rng = new Random(1);
        var (det, _, truth) = Render(rng, 120, 30, 10, 30.0, false, 9.0f, 0.3, 0, 0);
        var prior = truth.Center;
        var cone = Cone(prior, 8, 9.0f);
        Check.That(det.Count > 20, "the picture has stars");
        Check.That(PlateSolver.Solve(det.Take(4).ToList(), cone, prior, Width, Height, new PlateSolveOptions { MinArcsecPerPixel = 24, MaxArcsecPerPixel = 38 }) == null, "four stars cannot be a solution");
        Check.That(PlateSolver.Solve(Array.Empty<DetectedStar>(), cone, prior, Width, Height) == null, "nothing detected");
        Check.That(PlateSolver.Solve(det, new List<CatalogEntry>(), prior, Width, Height) == null, "an empty catalog");
    }

    [TestMethod]
    public void TheMatchedPairs_AreTheStarsThatWereReallyThere()
    {
        var rng = new Random(88);
        var (det, ids, truth) = Render(rng, 200, 40, 75, 30.0, false, 9.0f, 0.3, 0.1, 3);
        var prior = PriorNear(rng, truth.Center, 1.5);
        var cone = Cone(prior, 8, 9.0f);
        var s = PlateSolver.Solve(det, cone, prior, Width, Height, new PlateSolveOptions { MinArcsecPerPixel = 24, MaxArcsecPerPixel = 38 });
        Check.That(s != null, "solved");
        int right = 0;
        foreach (var (d, c) in s!.Pairs)
            if (ids[d] != 0 && cone[c].Id == ids[d]) right++;
        Check.That(right >= s.Pairs.Count - 1, $"{right} of {s.Pairs.Count} matches are the right star");
        Check.That(s.Pairs.Count >= Math.Min(det.Count, 64) * 0.85, $"most of the {Math.Min(det.Count, 64)} detections used matched ({s.Pairs.Count})");
    }

    [TestMethod]
    public void ARandomPicture_AgainstADenseCatalog_AndAWideSearch_IsStillNeverSolved()
    {
        // the hard case for false matches: many catalog stars (Hipparcos to 11), many detections, an unsure plate scale and a wide search, so a very large number of
        // guesses is made and some of them are bound to line up five or six stars by pure chance. The solver must count its guesses and refuse those.
        var rng = new Random(321);
        int solvedWrongly = 0;
        for (int i = 0; i < 5; i++)
        {
            double ra = rng.NextDouble() * 360, dec = Math.Asin(rng.NextDouble() * 1.8 - 0.9) * 180 / Math.PI;
            var prior = Unit(ra, dec);
            var cone = Cone(prior, 5, 11.0f);
            var det = Enumerable.Range(0, 40).Select(_ => new DetectedStar((rng.NextDouble() - 0.5) * 1200, (rng.NextDouble() - 0.5) * 680, rng.NextDouble() * 100 + 1)).ToList();
            var s = PlateSolver.SolveSearch(det, cone, prior, 1.5, Width, Height, new PlateSolveOptions { MinArcsecPerPixel = 4, MaxArcsecPerPixel = 20 });
            if (s != null) solvedWrongly++;
        }
        Check.Equal(0, solvedWrongly, "a random picture was 'solved' by chance alignments");
    }

    [TestMethod]
    public void ThePoissonTail_IsRight()
    {
        // P(X >= 1) = 1 - e^-lambda; P(X >= 0) = 1; a big lambda makes small counts certain
        Check.Near(1 - Math.Exp(-2.0), PlateSolver.PoissonTail(1, 2.0), 1e-12, "k=1");
        Check.Near(1.0, PlateSolver.PoissonTail(0, 3.0), 1e-12, "k=0");
        Check.Near(1 - Math.Exp(-2.0) * (1 + 2.0 + 2.0), PlateSolver.PoissonTail(3, 2.0), 1e-12, "k=3");
        Check.That(PlateSolver.PoissonTail(8, 0.05) < 1e-10, "eight matches when 0.05 were expected is not chance");
    }
}
