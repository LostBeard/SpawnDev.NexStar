using SpawnDev.NexStar.Vision;
using SpawnDev.NexStar.Vision.Gpu;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.UnitTesting;

namespace SpawnDev.NexStar.BrowserTests;

/// <summary>
/// The GPU star detector on pictures made here with known stars: Gaussian blobs at known sub-pixel positions over a sky with a gradient and noise, plus hot
/// pixels that must not count. The pixels reach the GPU as a texture (as the eyepiece video does); only the star list comes back.
/// </summary>
public class StarDetectorTests
{
    private readonly SpawnJSRuntime _js;
    public StarDetectorTests(SpawnJSRuntime js) { _js = js; }

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new Exception("FAILED: " + what);
    }

    private sealed record Truth(double X, double Y, double Peak);

    private static double Gaussian(Random rng) => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());

    /// <summary>A grey picture (rgba8) with stars of the given peak heights (0..1 of full scale) and sigma, noise, a gradient and hot pixels.</summary>
    private static (byte[] Pixels, List<Truth> Stars, List<(int X, int Y)> Hot) MakePicture(Random rng, int w, int h, int starCount, double noise, double gradient, int hotPixels, double sigmaPx = 1.4)
    {
        var luma = new double[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                luma[y * w + x] = 0.08 + gradient * (x / (double)w + y / (double)h) * 0.5 + Gaussian(rng) * noise;
        var stars = new List<Truth>();
        for (int i = 0; i < starCount; i++)
        {
            double sx = 12 + rng.NextDouble() * (w - 24), sy = 12 + rng.NextDouble() * (h - 24);
            // keep stars apart so each is a clean test
            if (stars.Any(t => Math.Abs(t.X - sx) < 14 && Math.Abs(t.Y - sy) < 14)) { i--; continue; }
            double peak = 0.12 + rng.NextDouble() * 0.55;
            stars.Add(new Truth(sx, sy, peak));
            int x0 = (int)sx - 7, y0 = (int)sy - 7;
            for (int y = y0; y <= y0 + 14; y++)
                for (int x = x0; x <= x0 + 14; x++)
                {
                    double dx = x + 0.5 - sx, dy = y + 0.5 - sy;
                    luma[y * w + x] += peak * Math.Exp(-(dx * dx + dy * dy) / (2 * sigmaPx * sigmaPx));
                }
        }
        var hot = new List<(int, int)>();
        for (int i = 0; i < hotPixels; i++)
        {
            int hx = 10 + rng.Next(w - 20), hy = 10 + rng.Next(h - 20);
            if (stars.Any(t => Math.Abs(t.X - hx) < 12 && Math.Abs(t.Y - hy) < 12)) { i--; continue; }
            hot.Add((hx, hy));
            luma[hy * w + hx] = 1.0;
        }
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            var v = (byte)Math.Clamp((int)Math.Round(luma[i] * 255), 0, 255);
            pixels[i * 4] = v; pixels[i * 4 + 1] = v; pixels[i * 4 + 2] = v; pixels[i * 4 + 3] = 255;
        }
        return (pixels, stars, hot);
    }

    private GPUTexture Upload(GPUDevice device, GPUQueue queue, byte[] pixels, int w, int h)
    {
        var texture = device.CreateTexture(new GPUTextureDescriptor
        {
            Size = new[] { w, h },
            Format = "rgba8unorm",
            Usage = GPUTextureUsage.TextureBinding | GPUTextureUsage.CopyDst,
        });
        queue.WriteTexture(new GPUTexelCopyTextureInfo { Texture = texture }, pixels, new GPUTexelCopyBufferLayout { BytesPerRow = (uint)(w * 4), RowsPerImage = (uint)h }, new GPUExtent3DDict { Width = (uint)w, Height = (uint)h });
        return texture;
    }

    private async Task<(GPUDevice, GPUQueue)> DeviceAsync()
    {
        using var gpu = _js.Get<GPU?>("navigator.gpu");
        Require(gpu != null, "WebGPU is not available");
        using var adapter = await gpu!.RequestAdapter();
        Require(adapter != null, "no adapter");
        var device = await adapter!.RequestDevice();
        return (device, device.Queue);
    }

    [TestMethod(Timeout = 120000)]
    public async Task EveryStar_IsFound_ToAFractionOfAPixel_AndNoHotPixelIsAStar()
    {
        var (device, queue) = await DeviceAsync();
        try
        {
            using var detector = new StarDetector(device, queue);
            var rng = new Random(2026);
            int w = 640, h = 360;
            int foundTotal = 0, starTotal = 0, falseTotal = 0, hotFound = 0;
            double worst = 0;
            for (int round = 0; round < 6; round++)
            {
                var (pixels, stars, hot) = MakePicture(rng, w, h, 22, noise: 0.006, gradient: 0.10, hotPixels: 8);
                using var texture = Upload(device, queue, pixels, w, h);
                using var view = texture.CreateView();
                var found = await detector.DetectAsync(view, w, h, maxStars: 200);
                var used = new HashSet<int>();
                foreach (var s in stars)
                {
                    // the nearest detection within a pixel
                    int best = -1; double bestD = 1.0;
                    for (int i = 0; i < found.Count; i++)
                    {
                        double dx = found[i].X + w / 2.0 - s.X, dy = found[i].Y + h / 2.0 - s.Y, d = Math.Sqrt(dx * dx + dy * dy);
                        if (d < bestD) { bestD = d; best = i; }
                    }
                    starTotal++;
                    if (best >= 0) { used.Add(best); foundTotal++; worst = Math.Max(worst, bestD); }
                    else Console.WriteLine($"[StarDetector] round {round}: missed a star of height {s.Peak:F2} at ({s.X:F1}, {s.Y:F1})");
                }
                for (int i = 0; i < found.Count; i++)
                {
                    if (used.Contains(i)) continue;
                    falseTotal++;
                    double fx = found[i].X + w / 2.0, fy = found[i].Y + h / 2.0;
                    if (hot.Any(p => Math.Abs(p.X + 0.5 - fx) < 2 && Math.Abs(p.Y + 0.5 - fy) < 2)) hotFound++;
                }
            }
            Console.WriteLine($"[StarDetector] found {foundTotal}/{starTotal} stars (worst error {worst:F2} px), {falseTotal} other detections, {hotFound} of them hot pixels");
            Require(foundTotal >= starTotal - 1, $"only {foundTotal} of {starTotal} stars were found");
            Require(hotFound == 0, $"{hotFound} hot pixels were reported as stars");
            Require(falseTotal <= 3, $"{falseTotal} detections that are not stars");
        }
        finally { device.Dispose(); }
    }

    [TestMethod(Timeout = 120000)]
    public async Task TheCentroid_IsAccurate_ToTenthsOfAPixel()
    {
        var (device, queue) = await DeviceAsync();
        try
        {
            using var detector = new StarDetector(device, queue);
            var rng = new Random(7);
            int w = 320, h = 240;
            var errors = new List<double>();
            for (int round = 0; round < 8; round++)
            {
                var (pixels, stars, _) = MakePicture(rng, w, h, 10, noise: 0.004, gradient: 0.05, hotPixels: 0, sigmaPx: 1.2 + round * 0.15);
                using var texture = Upload(device, queue, pixels, w, h);
                using var view = texture.CreateView();
                var found = await detector.DetectAsync(view, w, h, 100);
                foreach (var s in stars.Where(t => t.Peak > 0.3))
                {
                    var near = found.OrderBy(f => Math.Pow(f.X + w / 2.0 - s.X, 2) + Math.Pow(f.Y + h / 2.0 - s.Y, 2)).FirstOrDefault();
                    if (near == default) continue;
                    errors.Add(Math.Sqrt(Math.Pow(near.X + w / 2.0 - s.X, 2) + Math.Pow(near.Y + h / 2.0 - s.Y, 2)));
                }
            }
            Require(errors.Count >= 20, $"only {errors.Count} bright stars were compared");
            errors.Sort();
            double median = errors[errors.Count / 2], p90 = errors[(int)(errors.Count * 0.9)];
            Console.WriteLine($"[StarDetector] centroid error over {errors.Count} bright stars: median {median:F3} px, 90th percentile {p90:F3} px");
            Require(median < 0.12, $"median centroid error {median:F3} px");
            Require(p90 < 0.3, $"90th percentile centroid error {p90:F3} px");
        }
        finally { device.Dispose(); }
    }

    [TestMethod(Timeout = 120000)]
    public async Task AnEmptyNoisySky_HasNoStars()
    {
        var (device, queue) = await DeviceAsync();
        try
        {
            using var detector = new StarDetector(device, queue);
            var rng = new Random(11);
            int w = 640, h = 360;
            var (pixels, _, _) = MakePicture(rng, w, h, 0, noise: 0.012, gradient: 0.2, hotPixels: 0);
            using var texture = Upload(device, queue, pixels, w, h);
            using var view = texture.CreateView();
            var found = await detector.DetectAsync(view, w, h, 100);
            Require(found.Count <= 2, $"{found.Count} stars found in an empty noisy sky");
        }
        finally { device.Dispose(); }
    }

    [TestMethod(Timeout = 120000)]
    public async Task TheBrightestStars_ComeFirst_AndTheListIsCapped()
    {
        var (device, queue) = await DeviceAsync();
        try
        {
            using var detector = new StarDetector(device, queue);
            var rng = new Random(3);
            int w = 640, h = 360;
            var (pixels, stars, _) = MakePicture(rng, w, h, 40, noise: 0.005, gradient: 0.05, hotPixels: 0);
            using var texture = Upload(device, queue, pixels, w, h);
            using var view = texture.CreateView();
            var found = await detector.DetectAsync(view, w, h, 12);
            Require(found.Count == 12, $"asked for 12, got {found.Count}");
            for (int i = 1; i < found.Count; i++) Require(found[i - 1].Flux >= found[i].Flux, "not brightest first");
            // the brightest star in the picture is among them
            var brightest = stars.OrderByDescending(s => s.Peak).First();
            Require(found.Any(f => Math.Abs(f.X + w / 2.0 - brightest.X) < 1 && Math.Abs(f.Y + h / 2.0 - brightest.Y) < 1), "the brightest star is in the list");
        }
        finally { device.Dispose(); }
    }
}
