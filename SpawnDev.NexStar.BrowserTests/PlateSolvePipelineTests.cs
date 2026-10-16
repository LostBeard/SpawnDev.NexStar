using System.Numerics;
using SpawnDev.NexStar.Sky;
using SpawnDev.NexStar.Vision;
using SpawnDev.NexStar.Vision.Gpu;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.UnitTesting;

namespace SpawnDev.NexStar.BrowserTests;

/// <summary>
/// The whole alignment pipeline on the real GPU with the real catalog: a picture of a known piece of sky is drawn (the stars of the Hipparcos catalog at their
/// true places, with a star-shaped blur, noise and a gradient), the GPU finds its stars, the GPU picks the catalog stars around where the telescope is
/// thought to point, and the solver works out where the picture really points, how it is turned and how big a pixel is. Everything is compared with what
/// was drawn. This is a rehearsal on a synthetic sky: real eyepiece footage (its optics, its exposure, its noise) is not in it.
/// </summary>
public class PlateSolvePipelineTests
{
    private readonly SpawnJSRuntime _js;
    public PlateSolvePipelineTests(SpawnJSRuntime js) { _js = js; }

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new Exception("FAILED: " + what);
    }

    private static double Gaussian(Random rng) => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());

    private async Task<CatalogStar[]> AllStarsAsync()
    {
        using var response = await _js.Fetch("data/stars.bin");
        using var buffer = await response.ArrayBuffer();
        var bytes = buffer.ReadBytes();   // the test needs the star list on the CPU to DRAW the sky; the app itself never reads it
        var header = StarCatalog.ReadHeader(bytes);
        var stars = new CatalogStar[header.Count];
        for (int i = 0; i < stars.Length; i++) stars[i] = StarCatalog.Read(bytes, i);
        return stars;
    }

    private sealed record Drawn(byte[] Pixels, Vector3 Center, double RollDeg, double Scale, bool Mirrored, int Stars);

    private static Drawn DrawSky(CatalogStar[] catalog, Random rng, double raDeg, double decDeg, double rollDeg, double arcsecPerPixel, bool mirrored, int w, int h, float magLimit)
    {
        var center = CatalogFormats.UnitVector(raDeg, decDeg);
        var east = Vector3.Normalize(Vector3.Cross(new Vector3(0, 0, 1), center));
        var north = Vector3.Cross(center, east);
        double sigma = mirrored ? -1 : 1, roll = rollDeg * Math.PI / 180;
        double theta = mirrored ? -roll : Math.PI - roll;
        double scale = arcsecPerPixel * Math.PI / 180 / 3600;
        double aRe = scale * Math.Cos(theta), aIm = scale * Math.Sin(theta), den = aRe * aRe + aIm * aIm;
        var luma = new double[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) luma[y * w + x] = 0.07 + 0.06 * (x / (double)w) + Gaussian(rng) * 0.005;
        int drawn = 0;
        foreach (var s in catalog)
        {
            if (s.Magnitude > magLimit) continue;
            double d = Vector3.Dot(s.Position, center);
            if (d < 0.5) continue;
            double xi = Vector3.Dot(s.Position, east) / d, eta = Vector3.Dot(s.Position, north) / d;
            double zx = (xi * aRe + eta * aIm) / den, zy = (eta * aRe - xi * aIm) / den;
            double px = zx + w / 2.0, py = sigma * zy + h / 2.0;
            if (px < 8 || py < 8 || px > w - 8 || py > h - 8) continue;
            // brighter stars are brighter and a little bigger
            double peak = Math.Min(0.95, 0.9 * Math.Pow(10, -0.4 * (s.Magnitude - 7.0)));
            double sg = 1.3 + Math.Max(0, 8 - s.Magnitude) * 0.07;
            int x0 = (int)px - 7, y0 = (int)py - 7;
            for (int y = y0; y <= y0 + 14; y++)
                for (int x = x0; x <= x0 + 14; x++)
                {
                    double dx = x + 0.5 - px, dy = y + 0.5 - py;
                    luma[y * w + x] += peak * Math.Exp(-(dx * dx + dy * dy) / (2 * sg * sg));
                }
            drawn++;
        }
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            var v = (byte)Math.Clamp((int)Math.Round(luma[i] * 255), 0, 255);
            pixels[i * 4] = v; pixels[i * 4 + 1] = v; pixels[i * 4 + 2] = v; pixels[i * 4 + 3] = 255;
        }
        return new Drawn(pixels, center, rollDeg, arcsecPerPixel, mirrored, drawn);
    }

    private static double AngleArcsec(Vector3 a, Vector3 b)
    {
        double ax = a.X, ay = a.Y, az = a.Z, bx = b.X, by = b.Y, bz = b.Z;
        double cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
        return Math.Atan2(Math.Sqrt(cx * cx + cy * cy + cz * cz), ax * bx + ay * by + az * bz) * 180 / Math.PI * 3600;
    }

    [TestMethod(Timeout = 300000)]
    public async Task APictureOfTheSky_IsSolved_ByTheGpuDetector_TheGpuConeSearch_AndTheSolver()
    {
        using var gpu = _js.Get<GPU?>("navigator.gpu");
        Require(gpu != null, "WebGPU is not available");
        using var adapter = await gpu!.RequestAdapter();
        var device = await adapter!.RequestDevice();
        var queue = device.Queue;
        SkyData? data = null;
        try
        {
            data = await SkyData.LoadAsync(_js, device, queue, "data");
            var catalog = await AllStarsAsync();
            using var detector = new StarDetector(device, queue);
            using var cone = new CatalogCone(device, queue);
            var rng = new Random(2026);
            int w = 1280, h = 720, solved = 0, tried = 0;
            var report = new List<string>();
            foreach (var (arcsec, magLimit, count) in new[] { (8.0, 11.0f, 8), (25.0, 9.5f, 6) })
            {
                for (int i = 0; i < count; i++)
                {
                    double ra = rng.NextDouble() * 360, dec = Math.Asin(rng.NextDouble() * 1.6 - 0.8) * 180 / Math.PI, roll = rng.NextDouble() * 360;
                    bool mirrored = i % 2 == 1;
                    var picture = DrawSky(catalog, rng, ra, dec, roll, arcsec, mirrored, w, h, magLimit);
                    using var texture = device.CreateTexture(new GPUTextureDescriptor { Size = new[] { w, h }, Format = "rgba8unorm", Usage = GPUTextureUsage.TextureBinding | GPUTextureUsage.CopyDst });
                    queue.WriteTexture(new GPUTexelCopyTextureInfo { Texture = texture }, picture.Pixels, new GPUTexelCopyBufferLayout { BytesPerRow = (uint)(w * 4), RowsPerImage = (uint)h }, new GPUExtent3DDict { Width = (uint)w, Height = (uint)h });
                    using var view = texture.CreateView();

                    var detections = await detector.DetectAsync(view, w, h, 64);

                    // the telescope is thought to point up to two degrees away from where it really does
                    var east = Vector3.Normalize(Vector3.Cross(new Vector3(0, 0, 1), picture.Center));
                    var north = Vector3.Cross(picture.Center, east);
                    double off = 1.5 * Math.PI / 180, ang = rng.NextDouble() * 2 * Math.PI;
                    var prior = Vector3.Normalize(picture.Center + east * (float)(Math.Tan(off) * Math.Cos(ang)) + north * (float)(Math.Tan(off) * Math.Sin(ang)));
                    double fovRadius = 0.5 * Math.Sqrt((double)w * w + (double)h * h) * arcsec / 3600;
                    var stars = await cone.SearchAsync(data.Stars!, data.StarCount, prior, 1.6 + fovRadius * 1.5 + 0.5, magLimit);

                    var options = new PlateSolveOptions { MinArcsecPerPixel = arcsec * 0.8, MaxArcsecPerPixel = arcsec * 1.25 };
                    var solution = PlateSolver.SolveSearch(detections, stars, prior, 1.8, w, h, options);
                    tried++;
                    if (solution == null) { report.Add($"{arcsec}\"/px #{i}: {detections.Count} detections, {stars.Count} catalog stars, {picture.Stars} drawn: NOT solved"); continue; }
                    double centerError = AngleArcsec(solution.Center, picture.Center);
                    double scaleError = Math.Abs(solution.ArcsecPerPixel / arcsec - 1);
                    double rollError = Math.Abs(((solution.RollDegrees - roll + 540) % 360) - 180);
                    report.Add($"{arcsec}\"/px #{i}: {detections.Count} detections, {solution.Matches} matched, centre {centerError:F1}\" off, scale {scaleError * 100:F2}% off, roll {rollError:F2} deg off");
                    Require(centerError < arcsec * 1.5, $"{arcsec}\"/px #{i}: centre {centerError:F1} arcsec off");
                    Require(scaleError < 0.01, $"{arcsec}\"/px #{i}: scale {scaleError * 100:F2}% off");
                    Require(rollError < 0.3, $"{arcsec}\"/px #{i}: roll {rollError:F2} degrees off");
                    Require(solution.Mirrored == mirrored, $"{arcsec}\"/px #{i}: parity");
                    solved++;
                }
            }
            foreach (var line in report) Console.WriteLine("[PlateSolve] " + line);
            Require(solved >= tried - 2, $"only {solved} of {tried} pictures were solved:\n" + string.Join("\n", report));
        }
        finally
        {
            data?.Dispose();
            queue.Dispose();
            device.Dispose();
        }
    }

    [TestMethod(Timeout = 120000)]
    public async Task TheConeSearch_ReturnsExactlyTheStarsInTheCone()
    {
        using var gpu = _js.Get<GPU?>("navigator.gpu");
        using var adapter = await gpu!.RequestAdapter();
        var device = await adapter!.RequestDevice();
        var queue = device.Queue;
        SkyData? data = null;
        try
        {
            data = await SkyData.LoadAsync(_js, device, queue, "data");
            var catalog = await AllStarsAsync();
            using var cone = new CatalogCone(device, queue);
            var rng = new Random(9);
            for (int i = 0; i < 12; i++)
            {
                double ra = rng.NextDouble() * 360, dec = Math.Asin(rng.NextDouble() * 1.8 - 0.9) * 180 / Math.PI;
                var center = CatalogFormats.UnitVector(ra, dec);
                double radius = 0.5 + rng.NextDouble() * 3.0;
                float limit = 8.0f + (float)rng.NextDouble() * 3;
                var found = await cone.SearchAsync(data.Stars!, data.StarCount, center, radius, limit);
                double cosR = Math.Cos(radius * Math.PI / 180);
                // the same selection on the CPU
                var expected = catalog.Where(s => s.Magnitude <= limit && Vector3.Dot(s.Position, center) >= cosR).Select(s => s.Hip).ToHashSet();
                if (expected.Count > CatalogCone.MaxResults) continue;
                var got = found.Select(e => e.Id).ToHashSet();
                // a star within a hair of the edge may fall either side of the float rounding
                var missing = expected.Except(got).Where(h => Vector3.Dot(catalog.First(s => s.Hip == h).Position, center) > cosR + 1e-6).ToList();
                var extra = got.Except(expected).Where(h => Vector3.Dot(catalog.First(s => s.Hip == h).Position, center) < cosR - 1e-6).ToList();
                Require(missing.Count == 0 && extra.Count == 0, $"cone {i} (radius {radius:F1}, mag {limit:F1}): {missing.Count} missing, {extra.Count} extra of {expected.Count}");
                for (int k = 1; k < found.Count; k++) Require(found[k - 1].Magnitude <= found[k].Magnitude, "brightest first");
            }
        }
        finally
        {
            data?.Dispose();
            queue.Dispose();
            device.Dispose();
        }
    }
}
