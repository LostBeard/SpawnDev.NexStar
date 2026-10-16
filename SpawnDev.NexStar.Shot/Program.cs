// SpawnDev.NexStar.Shot: opens a URL in a FRESH headless Chrome (its own profile, never the user's browser), lets the page run, and saves a screenshot.
// Prints the page's console and errors, so a blank canvas explains itself.
//
//   shot <url> <out.png> [--size 1280x800] [--wait 4000] [--click x,y]... [--drag x0,y0,x1,y1]... [--wheel x,y,delta]... [--press Key]... [--touch] [--scale 1]
//
// Example: shot http://localhost:8099/workspace shot.png --size 1280x800 --wait 5000
using Microsoft.Playwright;

if (args.Length < 2) { Console.WriteLine("usage: shot <url> <out.png> [--size WxH] [--wait ms] [--click x,y]... [--press Key]... [--scale n]"); return 1; }
var url = args[0];
var output = args[1];
int width = 1280, height = 800, wait = 4000;
double scale = 1;
var clicks = new List<(float x, float y)>();
var presses = new List<string>();
var drags = new List<(float x0, float y0, float x1, float y1)>();
var wheels = new List<(float x, float y, float delta)>();
var touch = false;
for (int i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--size": var p = args[++i].Split('x'); width = int.Parse(p[0]); height = int.Parse(p[1]); break;
        case "--wait": wait = int.Parse(args[++i]); break;
        case "--scale": scale = double.Parse(args[++i]); break;
        case "--click": var c = args[++i].Split(','); clicks.Add((float.Parse(c[0]), float.Parse(c[1]))); break;
        case "--press": presses.Add(args[++i]); break;
        case "--drag": var d = args[++i].Split(','); drags.Add((float.Parse(d[0]), float.Parse(d[1]), float.Parse(d[2]), float.Parse(d[3]))); break;
        case "--wheel": var wv = args[++i].Split(','); wheels.Add((float.Parse(wv[0]), float.Parse(wv[1]), float.Parse(wv[2]))); break;
        case "--touch": touch = true; break;
    }
}

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
{
    Channel = "chrome",
    Headless = true,
    Args = new[] { "--enable-unsafe-webgpu", "--enable-features=Vulkan,WebGPUService,SkiaGraphite", "--ignore-gpu-blocklist" },
});
await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
{
    ViewportSize = new ViewportSize { Width = width, Height = height },
    DeviceScaleFactor = (float)scale,
    HasTouch = touch,
    IsMobile = touch,
});
var page = await context.NewPageAsync();
page.Console += (_, m) => Console.WriteLine($"[console.{m.Type}] {m.Text}");
page.PageError += (_, e) => Console.WriteLine($"[pageerror] {e}");
await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120000 });
await page.WaitForTimeoutAsync(wait);
foreach (var (x, y) in clicks)
{
    if (touch) await page.Touchscreen.TapAsync(x, y); else await page.Mouse.ClickAsync(x, y);
    await page.WaitForTimeoutAsync(600);
}
foreach (var (x0, y0, x1, y1) in drags)
{
    await page.Mouse.MoveAsync(x0, y0);
    await page.Mouse.DownAsync();
    for (int step = 1; step <= 12; step++) await page.Mouse.MoveAsync(x0 + (x1 - x0) * step / 12f, y0 + (y1 - y0) * step / 12f);
    await page.Mouse.UpAsync();
    await page.WaitForTimeoutAsync(500);
}
foreach (var (x, y, delta) in wheels)
{
    await page.Mouse.MoveAsync(x, y);
    await page.Mouse.WheelAsync(0, delta);
    await page.WaitForTimeoutAsync(500);
}
foreach (var key in presses)
{
    await page.Keyboard.PressAsync(key);
    await page.WaitForTimeoutAsync(300);
}
await page.ScreenshotAsync(new PageScreenshotOptions { Path = output });
Console.WriteLine($"saved {output} ({width}x{height} at {scale}x)");
return 0;
