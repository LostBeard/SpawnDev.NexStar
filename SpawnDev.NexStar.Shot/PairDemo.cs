using Microsoft.Playwright;

/// <summary>
/// Drives the whole eyepiece pairing in headless Chrome with a fake camera: the workspace (with the simulated telescope) pairs a phone, a second page
/// plays the phone from the pairing link, the link opens, and screenshots of both are saved.
///   shot pair http://localhost:8099 outDir [--alpha 0 --beta 90 --gamma 0]
/// </summary>
public static class PairDemo
{
    public static async Task<int> RunAsync(string[] args)
    {
        var baseUrl = args[0].TrimEnd('/');
        var outDir = args[1];
        double alpha = 0, beta = 90, gamma = 0;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--alpha") alpha = double.Parse(args[++i]);
            else if (args[i] == "--beta") beta = double.Parse(args[++i]);
            else if (args[i] == "--gamma") gamma = double.Parse(args[++i]);
        }
        Directory.CreateDirectory(outDir);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = "chrome",
            Headless = true,
            Args = new[]
            {
                "--enable-unsafe-webgpu", "--enable-features=Vulkan,WebGPUService,SkiaGraphite", "--ignore-gpu-blocklist",
                "--use-fake-device-for-media-stream", "--use-fake-ui-for-media-stream",
            },
        });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1280, Height = 800 } });
        await context.GrantPermissionsAsync(new[] { "camera", "accelerometer", "gyroscope", "magnetometer" });

        var viewer = await context.NewPageAsync();
        viewer.Console += (_, m) => { if (!m.Text.Contains("Geolocation")) Console.WriteLine($"[viewer.{m.Type}] {m.Text}"); };
        viewer.PageError += (_, e) => Console.WriteLine($"[viewer.pageerror] {e}");
        await viewer.GotoAsync($"{baseUrl}/workspace?sim=1", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120000 });
        await viewer.WaitForTimeoutAsync(6000);

        // open the Eyepiece panel and press "Pair a phone"
        await viewer.Mouse.ClickAsync(311, 68);
        await viewer.WaitForTimeoutAsync(800);
        await viewer.Mouse.ClickAsync(331, 219);
        await viewer.WaitForTimeoutAsync(4000);
        await viewer.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(outDir, "viewer-pairing.png") });

        var token = await viewer.EvaluateAsync<string?>("localStorage.getItem('nexstar.eyepiece.pairing')");
        if (string.IsNullOrEmpty(token)) { Console.WriteLine("NO PAIRING was saved by the viewer"); return 2; }
        Console.WriteLine($"pairing token saved by the viewer ({token.Length} chars)");

        // the phone: its own page from the link, with the sensors faked through the browser's devtools protocol
        var phone = await context.NewPageAsync();
        phone.Console += (_, m) => Console.WriteLine($"[phone.{m.Type}] {m.Text}");
        phone.PageError += (_, e) => Console.WriteLine($"[phone.pageerror] {e}");
        var cdp = await context.NewCDPSessionAsync(phone);
        // the browser's own absolute-orientation sensor, set to the quaternion of Rz(alpha) Rx(beta) Ry(gamma): what a phone held that way would report
        var q = Quaternion(alpha, beta, gamma);
        await cdp.SendAsync("Emulation.setSensorOverrideEnabled", new Dictionary<string, object> { ["enabled"] = true, ["type"] = "absolute-orientation" });
        await cdp.SendAsync("Emulation.setSensorOverrideReadings", new Dictionary<string, object>
        {
            ["type"] = "absolute-orientation",
            ["reading"] = new Dictionary<string, object> { ["quaternion"] = new Dictionary<string, object> { ["x"] = q.x, ["y"] = q.y, ["z"] = q.z, ["w"] = q.w } },
        });
        await phone.SetViewportSizeAsync(412, 800);
        await phone.GotoAsync($"{baseUrl}/eyepiece#pair={token}", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120000 });
        await phone.WaitForTimeoutAsync(1500);
        // the sensor override only reaches a page that loads after it was set
        await phone.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await phone.WaitForTimeoutAsync(1500);
        await phone.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(outDir, "phone-before.png") });
        var probe = await phone.EvaluateAsync<string>("() => new Promise(r => { const t = setTimeout(() => r('no orientation event in 3 s'), 3000); window.addEventListener('deviceorientation', e => { clearTimeout(t); r('deviceorientation ' + JSON.stringify([e.alpha, e.beta, e.gamma, e.absolute])); }, { once: true }); window.addEventListener('deviceorientationabsolute', e => { clearTimeout(t); r('deviceorientationabsolute ' + JSON.stringify([e.alpha, e.beta, e.gamma])); }, { once: true }); })");
        Console.WriteLine($"browser probe: {probe}");
        await phone.GetByRole(AriaRole.Button, new() { Name = "Start" }).ClickAsync();
        await phone.WaitForTimeoutAsync(1000);
        await viewer.BringToFrontAsync();
        await viewer.WaitForTimeoutAsync(12000);
        await viewer.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(outDir, "viewer-linked.png") });
        await phone.BringToFrontAsync();
        await phone.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(outDir, "phone-linked.png") });
        Console.WriteLine($"saved screenshots in {outDir}");
        return 0;
    }

    private static (double x, double y, double z, double w) Quaternion(double alphaDeg, double betaDeg, double gammaDeg)
    {
        static (double x, double y, double z, double w) Mul((double x, double y, double z, double w) a, (double x, double y, double z, double w) b) =>
            (a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
             a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
             a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
             a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);
        double h(double d) => d * Math.PI / 360.0;
        var qa = (0.0, 0.0, Math.Sin(h(alphaDeg)), Math.Cos(h(alphaDeg)));
        var qb = (Math.Sin(h(betaDeg)), 0.0, 0.0, Math.Cos(h(betaDeg)));
        var qg = (0.0, Math.Sin(h(gammaDeg)), 0.0, Math.Cos(h(gammaDeg)));
        return Mul(Mul(qa, qb), qg);
    }
}
