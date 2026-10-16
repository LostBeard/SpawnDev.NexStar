using System.Numerics;
using Microsoft.Playwright;
using SpawnDev.NexStar.Eyepiece;
using SpawnDev.NexStar.Sky;

/// <summary>
/// Plate solving end to end in headless Chrome. A video of a synthetic star field (the real Hipparcos catalog, drawn as a y4m file Chrome plays as its fake
/// camera) is streamed by the "phone" page, whose compass and tilt are set to roughly where that sky is; the workspace pairs, receives the video, and its
/// Solve button runs the GPU detector, the GPU cone search and the solver. The truth is printed so the screenshot can be checked against it.
///   shot solve http://localhost:8099 outDir [--az 200 --alt 40 --scale 25 --roll 30 --lat 40.7484 --lon -73.9857]
/// </summary>
public static class SolveDemo
{
    public static async Task<int> RunAsync(string[] args)
    {
        var baseUrl = args[0].TrimEnd('/');
        var outDir = args[1];
        double az = 200, alt = 40, scale = 25, roll = 30, lat = 40.7484, lon = -73.9857;
        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--az": az = double.Parse(args[++i]); break;
                case "--alt": alt = double.Parse(args[++i]); break;
                case "--scale": scale = double.Parse(args[++i]); break;
                case "--roll": roll = double.Parse(args[++i]); break;
                case "--lat": lat = double.Parse(args[++i]); break;
                case "--lon": lon = double.Parse(args[++i]); break;
            }
        }
        Directory.CreateDirectory(outDir);

        // the sky the "camera" sees: where az/alt is a minute from now, in J2000 (which does not change while the sky turns)
        var when = DateTime.UtcNow.AddSeconds(90);
        var view = new SkyView { Latitude = lat, Longitude = lon, Utc = when, WidthPx = 100, HeightPx = 100 };
        view.Update();
        var truth = view.ToJ2000(SkyView.FromAzAlt(az, alt));
        var (ra, dec) = CatalogFormats.ToRaDec(truth);
        Console.WriteLine($"truth: Az {az} Alt {alt} -> RA {ra:F4} Dec {dec:F4} (J2000), {scale} arcsec/px, roll {roll}");

        var y4m = Path.Combine(outDir, "stars.y4m");
        WriteVideo(y4m, truth, roll, scale, 640, 360, 9.5f);

        // the phone's compass: magnetic azimuth = true azimuth - declination; tilt from the altitude
        double declination = WorldMagneticModel.Default.Declination(lat, lon, 0, DateTime.UtcNow);
        double magnetic = ((az - declination) % 360 + 360) % 360;
        double alpha = (360 - magnetic) % 360, beta = 90 + alt, gamma = 0;
        Console.WriteLine($"declination {declination:F2}: the phone is told alpha {alpha:F1} beta {beta:F1} gamma {gamma:F1}");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = "chrome",
            Headless = true,
            Args = new[]
            {
                "--enable-unsafe-webgpu", "--enable-features=Vulkan,WebGPUService,SkiaGraphite", "--ignore-gpu-blocklist",
                "--use-fake-device-for-media-stream", "--use-fake-ui-for-media-stream", $"--use-file-for-fake-video-capture={y4m}",
            },
        });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1280, Height = 800 } });
        await context.GrantPermissionsAsync(new[] { "camera", "accelerometer", "gyroscope", "magnetometer" });

        var viewer = await context.NewPageAsync();
        viewer.Console += (_, m) => { if (!m.Text.Contains("Geolocation") && !m.Text.Contains("Workspace] ready")) Console.WriteLine($"[viewer.{m.Type}] {m.Text}"); };
        viewer.PageError += (_, e) => Console.WriteLine($"[viewer.pageerror] {e}");
        await viewer.GotoAsync($"{baseUrl}/workspace?sim=1&lat={lat}&lon={lon}", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120000 });
        await viewer.WaitForTimeoutAsync(6000);
        await viewer.Mouse.ClickAsync(311, 68);   // Eyepiece
        await viewer.WaitForTimeoutAsync(800);
        await viewer.Mouse.ClickAsync(331, 219);  // Pair a phone
        await viewer.WaitForTimeoutAsync(4000);
        var token = await viewer.EvaluateAsync<string?>("localStorage.getItem('nexstar.eyepiece.pairing')");
        if (string.IsNullOrEmpty(token)) { Console.WriteLine("NO PAIRING"); return 2; }

        var phone = await context.NewPageAsync();
        phone.Console += (_, m) => Console.WriteLine($"[phone.{m.Type}] {m.Text}");
        var cdp = await context.NewCDPSessionAsync(phone);
        var q = Quat(alpha, beta, gamma);
        await cdp.SendAsync("Emulation.setSensorOverrideEnabled", new Dictionary<string, object> { ["enabled"] = true, ["type"] = "absolute-orientation" });
        await cdp.SendAsync("Emulation.setSensorOverrideReadings", new Dictionary<string, object>
        {
            ["type"] = "absolute-orientation",
            ["reading"] = new Dictionary<string, object> { ["quaternion"] = new Dictionary<string, object> { ["x"] = q.x, ["y"] = q.y, ["z"] = q.z, ["w"] = q.w } },
        });
        await phone.SetViewportSizeAsync(412, 800);
        await phone.GotoAsync($"{baseUrl}/eyepiece#pair={token}", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120000 });
        await phone.WaitForTimeoutAsync(1000);
        await phone.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await phone.WaitForTimeoutAsync(1500);
        await phone.GetByRole(AriaRole.Button, new() { Name = "Start" }).ClickAsync();
        await viewer.BringToFrontAsync();
        await viewer.WaitForTimeoutAsync(12000);
        await viewer.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(outDir, "viewer-linked.png") });

        // the Solve button
        await viewer.Mouse.ClickAsync(311, 406);
        await viewer.WaitForTimeoutAsync(1500);
        await viewer.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(outDir, "viewer-solving.png") });
        await viewer.WaitForTimeoutAsync(25000);
        await viewer.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(outDir, "viewer-solved.png") });

        // align the (simulated, unaligned) telescope on the solution
        await viewer.Mouse.ClickAsync(455, 406);
        await viewer.WaitForTimeoutAsync(2500);
        await viewer.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(outDir, "viewer-aligned.png") });
        Console.WriteLine($"saved screenshots in {outDir}");
        return 0;
    }

    private static (double x, double y, double z, double w) Quat(double alphaDeg, double betaDeg, double gammaDeg)
    {
        static (double x, double y, double z, double w) Mul((double x, double y, double z, double w) a, (double x, double y, double z, double w) b) =>
            (a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y, a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x, a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w, a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);
        double h(double d) => d * Math.PI / 360.0;
        return Mul(Mul((0.0, 0.0, Math.Sin(h(alphaDeg)), Math.Cos(h(alphaDeg))), (Math.Sin(h(betaDeg)), 0.0, 0.0, Math.Cos(h(betaDeg)))), (0.0, Math.Sin(h(gammaDeg)), 0.0, Math.Cos(h(gammaDeg))));
    }

    private static double Gaussian(Random rng) => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());

    /// <summary>A y4m video (a dozen frames, new noise each) of the sky about <paramref name="center"/>, which Chrome can play as a fake camera.</summary>
    private static void WriteVideo(string path, Vector3 center, double rollDeg, double arcsecPerPixel, int w, int h, float magLimit)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SpawnDev.NexStar.slnx"))) dir = dir.Parent;
        var bytes = File.ReadAllBytes(Path.Combine(dir!.FullName, "SpawnDev.NexStar.App", "wwwroot", "data", "stars.bin"));
        var header = StarCatalog.ReadHeader(bytes);
        var east = Vector3.Normalize(Vector3.Cross(new Vector3(0, 0, 1), center));
        var north = Vector3.Cross(center, east);
        double roll = rollDeg * Math.PI / 180, theta = Math.PI - roll;
        double scale = arcsecPerPixel * Math.PI / 180 / 3600;
        double aRe = scale * Math.Cos(theta), aIm = scale * Math.Sin(theta), den = aRe * aRe + aIm * aIm;
        var stars = new List<(double X, double Y, double Peak, double Sigma)>();
        for (int i = 0; i < header.Count; i++)
        {
            var s = StarCatalog.Read(bytes, i);
            if (s.Magnitude > magLimit) continue;
            double d = Vector3.Dot(s.Position, center);
            if (d < 0.5) continue;
            double xi = Vector3.Dot(s.Position, east) / d, eta = Vector3.Dot(s.Position, north) / d;
            double zx = (xi * aRe + eta * aIm) / den, zy = (eta * aRe - xi * aIm) / den;
            double px = zx + w / 2.0, py = zy + h / 2.0;
            if (px < 8 || py < 8 || px > w - 8 || py > h - 8) continue;
            stars.Add((px, py, Math.Min(0.95, 0.9 * Math.Pow(10, -0.4 * (s.Magnitude - 7.0))), 1.3 + Math.Max(0, 8 - s.Magnitude) * 0.07));
        }
        Console.WriteLine($"the picture holds {stars.Count} catalog stars to magnitude {magLimit}; the brightest (x from centre, y from centre, peak): " + string.Join(" ", stars.OrderByDescending(s => s.Peak).Take(8).Select(s => $"({s.X - w / 2.0:F1},{s.Y - h / 2.0:F1},{s.Peak:F2})")));
        var rng = new Random(1);
        using var file = File.Create(path);
        file.Write(System.Text.Encoding.ASCII.GetBytes($"YUV4MPEG2 W{w} H{h} F30:1 Ip A1:1 C420jpeg\n"));
        var chroma = new byte[(w / 2) * (h / 2) * 2];
        Array.Fill(chroma, (byte)128);
        for (int frame = 0; frame < 12; frame++)
        {
            var luma = new double[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) luma[y * w + x] = 0.07 + 0.06 * (x / (double)w) + Gaussian(rng) * 0.005;
            foreach (var (px, py, peak, sg) in stars)
            {
                int x0 = (int)px - 7, y0 = (int)py - 7;
                for (int y = y0; y <= y0 + 14; y++)
                    for (int x = x0; x <= x0 + 14; x++)
                    {
                        double dx = x + 0.5 - px, dy = y + 0.5 - py;
                        luma[y * w + x] += peak * Math.Exp(-(dx * dx + dy * dy) / (2 * sg * sg));
                    }
            }
            var y8 = new byte[w * h];
            for (int i = 0; i < y8.Length; i++) y8[i] = (byte)Math.Clamp((int)Math.Round(16 + luma[i] * 219), 16, 235);
            file.Write(System.Text.Encoding.ASCII.GetBytes("FRAME\n"));
            file.Write(y8);
            file.Write(chroma);
        }
    }
}
