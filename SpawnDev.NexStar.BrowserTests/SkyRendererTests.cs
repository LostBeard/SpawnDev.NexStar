using SpawnDev.NexStar.Sky;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.UnitTesting;

namespace SpawnDev.NexStar.BrowserTests;

/// <summary>
/// The GPU sky against the CPU geometry, in a real browser on a real WebGPU device. The real catalogs are drawn by the real <see cref="SkyRenderer"/> into an
/// offscreen texture, the pixels are read back, and every bright, isolated star the HUD would label is looked for at the pixel <see cref="SkyView"/>
/// projects it to. If the shader's rotation or projection ever disagrees with the geometry the labels, picking and the telescope reticle use, this fails.
/// The readback is a terminal sink for the check; the app itself never reads the sky back.
/// </summary>
public class SkyRendererTests
{
    private const int Width = 1024, Height = 640;
    private readonly SpawnJSRuntime _js;

    public SkyRendererTests(SpawnJSRuntime js) { _js = js; }

    private sealed class Frame
    {
        public byte[] Pixels = System.Array.Empty<byte>();
        public int Stride;
        public byte R(int x, int y) => Pixels[y * Stride + x * 4];
        public byte G(int x, int y) => Pixels[y * Stride + x * 4 + 1];
        public byte B(int x, int y) => Pixels[y * Stride + x * 4 + 2];
        public int Max(int x, int y) => Math.Max(R(x, y), Math.Max(G(x, y), B(x, y)));
    }

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new Exception("FAILED: " + what);
    }

    private static SkyView NewView(double az, double alt, double fov) => new SkyView
    {
        Latitude = 40.7484, Longitude = -73.9857,   // New York City: a public place
        Utc = new DateTime(2026, 1, 15, 3, 0, 0, DateTimeKind.Utc),
        CenterAzimuth = az, CenterAltitude = alt, FovDegrees = fov,
        WidthPx = Width, HeightPx = Height,
    };

    private async Task<(GPUDevice device, GPUQueue queue, SkyData data)> StartAsync()
    {
        using var gpu = _js.Get<GPU?>("navigator.gpu");
        Require(gpu != null, "WebGPU is not available in this browser");
        using var adapter = await gpu!.RequestAdapter();
        Require(adapter != null, "No WebGPU adapter");
        var device = await adapter!.RequestDevice();
        var queue = device.Queue;
        var data = await SkyData.LoadAsync(_js, device, queue, "data");
        return (device, queue, data);
    }

    private static async Task<Frame> RenderAsync(GPUDevice device, GPUQueue queue, SkyData data, SkyView view, SkyLayers layers, bool night)
    {
        view.Update();
        using var renderer = new SkyRenderer(device, queue, "rgba8unorm", data) { Layers = layers, NightMode = night };
        var stride = (Width * 4 + 255) / 256 * 256;
        using var texture = device.CreateTexture(new GPUTextureDescriptor
        {
            Size = new[] { Width, Height },
            Format = "rgba8unorm",
            Usage = GPUTextureUsage.RenderAttachment | GPUTextureUsage.CopySrc,
        });
        using var target = texture.CreateView();
        using var staging = device.CreateBuffer(new GPUBufferDescriptor
        {
            Size = (ulong)(stride * Height),
            Usage = GPUBufferUsage.MapRead | GPUBufferUsage.CopyDst,
        });
        using (var encoder = device.CreateCommandEncoder())
        {
            renderer.Render(encoder, target, view, 1.0);
            encoder.CopyTextureToBuffer(
                new GPUTexelCopyTextureInfo { Texture = texture },
                new GPUTexelCopyBufferInfo { Buffer = staging, Offset = 0, BytesPerRow = (uint)stride, RowsPerImage = (uint)Height },
                new GPUExtent3DDict { Width = Width, Height = Height });
            using var commands = encoder.Finish();
            queue.Submit(new[] { commands });
        }
        await staging.MapAsync(GPUMapMode.Read);
        byte[] bytes;
        using (var mapped = staging.GetMappedRange())
        {
            bytes = mapped.ReadBytes();
        }
        staging.Unmap();
        return new Frame { Pixels = bytes, Stride = stride };
    }

    /// <summary>
    /// The stars the check looks at: bright enough to be well inside the magnitude limit, well inside the picture, and with no other bright star close
    /// enough to move the centroid (a neighbour more than 2.5 magnitudes fainter cannot).
    /// </summary>
    private static List<(CatalogStar star, double sx, double sy)> IsolatedStars(SkyData data, SkyView view, double maxMagnitude)
    {
        var all = new List<(CatalogStar star, double sx, double sy)>();
        foreach (var s in data.BrightStars)
        {
            if (view.TryProject(view.ToHorizontal(s.Position), out var sx, out var sy)) all.Add((s, sx, sy));
        }
        var chosen = new List<(CatalogStar star, double sx, double sy)>();
        foreach (var c in all)
        {
            if (c.star.Magnitude > maxMagnitude) continue;
            if (c.sx < 24 || c.sy < 24 || c.sx > Width - 24 || c.sy > Height - 24) continue;
            var crowded = all.Any(o => o.star.Hip != c.star.Hip && o.star.Magnitude < c.star.Magnitude + 2.5f && Math.Abs(o.sx - c.sx) < 12 && Math.Abs(o.sy - c.sy) < 12);
            if (!crowded) chosen.Add(c);
        }
        return chosen;
    }

    /// <summary>
    /// The brightness-weighted centre of the star's own pixels around a point, in the projection's coordinates (a pixel's centre is +0.5). Only pixels
    /// at least a third as bright as the peak count, so a faint star beside it (the .NET side holds only the bright ones) cannot drag the centre.
    /// </summary>
    private static (double x, double y, int peak) Centroid(Frame frame, double cx, double cy, int radius)
    {
        int x0 = (int)Math.Floor(cx) - radius, y0 = (int)Math.Floor(cy) - radius;
        int peak = 0;
        for (int y = y0; y <= y0 + 2 * radius; y++)
            for (int x = x0; x <= x0 + 2 * radius; x++)
                peak = Math.Max(peak, frame.Max(x, y));
        double sum = 0, sx = 0, sy = 0;
        var floor = Math.Max(20, peak / 3);
        for (int y = y0; y <= y0 + 2 * radius; y++)
            for (int x = x0; x <= x0 + 2 * radius; x++)
            {
                var v = frame.Max(x, y);
                if (v < floor) continue;
                sum += v; sx += v * (x + 0.5); sy += v * (y + 0.5);
            }
        return sum > 0 ? (sx / sum, sy / sum, peak) : (double.NaN, double.NaN, peak);
    }

    private async Task CheckStarsAtProjectedPositions(SkyView view, double maxMagnitude, int minStars, string name)
    {
        var (device, queue, data) = await StartAsync();
        try
        {
            Require(data.StarCount > 100000, $"the star catalog loaded {data.StarCount} stars");
            var frame = await RenderAsync(device, queue, data, view, SkyLayers.Stars, night: false);
            var stars = IsolatedStars(data, view, maxMagnitude);
            Require(stars.Count >= minStars, $"{name}: only {stars.Count} isolated stars up to magnitude {maxMagnitude} are in view, need {minStars} for the check to mean anything");

            double worst = 0, sumDx = 0, sumDy = 0;
            var errors = new List<double>();
            string worstStar = "", report = "";
            foreach (var (star, sx, sy) in stars)
            {
                var (x, y, peak) = Centroid(frame, sx, sy, 7);
                Require(peak >= 90, $"{name}: HIP {star.Hip} (mag {star.Magnitude:F2}) projects to ({sx:F1}, {sy:F1}) but the brightest pixel there is only {peak}: nothing was drawn");
                var err = Math.Sqrt((x - sx) * (x - sx) + (y - sy) * (y - sy));
                sumDx += x - sx; sumDy += y - sy;
                errors.Add(err);
                report += $" [HIP {star.Hip} m{star.Magnitude:F1} d=({x - sx:+0.00;-0.00},{y - sy:+0.00;-0.00}) peak {peak}]";
                if (err > worst) { worst = err; worstStar = $"HIP {star.Hip}"; }
            }
            Console.WriteLine($"[SkyRenderer] {name}: {stars.Count} stars checked, worst centroid error {worst:F3} px ({worstStar}), mean offset ({sumDx / stars.Count:+0.000;-0.000}, {sumDy / stars.Count:+0.000;-0.000}):{report}");
            // A few stars have a faint companion the .NET side does not hold, which pulls their centre by up to a pixel or so; a real mismatch moves them all.
            errors.Sort();
            var median = errors[errors.Count / 2];
            var within = errors.Count(e => e < 0.5) / (double)errors.Count;
            Require(median < 0.25, $"{name}: the median star is drawn {median:F3} px from where the geometry says; mean offset ({sumDx / stars.Count:F2}, {sumDy / stars.Count:F2}):{report}");
            Require(within >= 0.6, $"{name}: only {within:P0} of the stars are within half a pixel of where the geometry says:{report}");
            Require(worst < 2.5, $"{name}: a star was drawn {worst:F2} px from where the geometry says ({worstStar}):{report}");
        }
        finally
        {
            data.Dispose();
            queue.Dispose();
            device.Dispose();
        }
    }

    [TestMethod(Timeout = 120000)]
    public Task Stars_AreDrawnWhereTheGeometryProjectsThem_WideView() =>
        CheckStarsAtProjectedPositions(NewView(180, 40, 70), maxMagnitude: 3.8, minStars: 12, "wide view, south at 40 deg");

    [TestMethod(Timeout = 120000)]
    public Task Stars_AreDrawnWhereTheGeometryProjectsThem_NarrowViewTowardsTheEast() =>
        CheckStarsAtProjectedPositions(NewView(100, 25, 25), maxMagnitude: 5.0, minStars: 5, "narrow view, east at 25 deg");

    [TestMethod(Timeout = 120000)]
    public async Task NightMode_DrawsRedOnly_AndDayModeDrawsColour()
    {
        var (device, queue, data) = await StartAsync();
        try
        {
            var view = NewView(180, 40, 70);
            var night = await RenderAsync(device, queue, data, view, SkyLayers.Default, night: true);
            var day = await RenderAsync(device, queue, data, view, SkyLayers.Default, night: false);
            int lit = 0, leaked = 0;
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    if (night.R(x, y) > 10) lit++;
                    if (night.G(x, y) > 0 || night.B(x, y) > 0) leaked++;
                }
            Require(lit > 200, $"night mode drew only {lit} red pixels, the sky is empty");
            Require(leaked == 0, $"night mode leaked green or blue light into {leaked} pixels: it must be red only");

            int colourful = 0;
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                    if (day.G(x, y) > 40 || day.B(x, y) > 40) colourful++;
            Require(colourful > 200, $"day mode drew only {colourful} pixels with green or blue: colour is missing");
        }
        finally
        {
            data.Dispose();
            queue.Dispose();
            device.Dispose();
        }
    }
}
