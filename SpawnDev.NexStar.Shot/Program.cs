// SpawnDev.NexStar.Shot: opens a URL in a FRESH headless Chrome (its own profile, never the user's browser), lets the page run, and saves a screenshot.
// Prints the page's console and errors, so a blank canvas explains itself.
//
//   shot <url> <out.png> [--size 1280x800] [--wait 4000] [--click x,y]... [--press Key]... [--touch] [--scale 1]
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
foreach (var key in presses)
{
    await page.Keyboard.PressAsync(key);
    await page.WaitForTimeoutAsync(300);
}
await page.ScreenshotAsync(new PageScreenshotOptions { Path = output });
Console.WriteLine($"saved {output} ({width}x{height} at {scale}x)");
return 0;
