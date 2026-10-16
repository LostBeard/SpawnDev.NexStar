// SpawnDev.NexStar.Shot: opens a URL in a FRESH headless Chrome (its own profile, never the user's browser), lets the page run, plays a list of actions
// in the order given, and saves a screenshot. Prints the page's console and errors, so a blank canvas explains itself.
//
//   shot <url> <out.png> [--size 1280x800] [--wait 4000] [--touch] [--scale 1] then any of, in order:
//     --click x,y   --drag x0,y0,x1,y1   --wheel x,y,delta   --press Key   --hold x,y,ms   --sleep ms   --snap file.png
//
// Example: shot http://localhost:8099/workspace?sim=1 shot.png --size 1280x800 --wait 5000 --click 1100,520 --sleep 500 --hold 1134,262,1500 --snap held.png
using Microsoft.Playwright;

if (args.Length >= 3 && args[0] == "pair") return await PairDemo.RunAsync(args[1..]);
if (args.Length < 2) { Console.WriteLine("usage: shot <url> <out.png> [--size WxH] [--wait ms] [--touch] [--scale n] [--click x,y|--drag x0,y0,x1,y1|--wheel x,y,delta|--press Key|--hold x,y,ms|--sleep ms|--snap file]..."); return 1; }
var url = args[0];
var output = args[1];
int width = 1280, height = 800, wait = 4000;
double scale = 1;
var touch = false;
var actions = new List<(string Kind, string Value)>();
for (int i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--size": var p = args[++i].Split('x'); width = int.Parse(p[0]); height = int.Parse(p[1]); break;
        case "--wait": wait = int.Parse(args[++i]); break;
        case "--scale": scale = double.Parse(args[++i]); break;
        case "--touch": touch = true; break;
        case "--click": case "--drag": case "--wheel": case "--press": case "--hold": case "--sleep": case "--snap":
            actions.Add((args[i][2..], args[++i]));
            break;
        default: Console.WriteLine($"unknown option {args[i]}"); return 1;
    }
}

static float[] Nums(string value) => value.Split(',').Select(float.Parse).ToArray();

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
foreach (var (kind, value) in actions)
{
    switch (kind)
    {
        case "click":
        {
            var c = Nums(value);
            if (touch) await page.Touchscreen.TapAsync(c[0], c[1]); else await page.Mouse.ClickAsync(c[0], c[1]);
            await page.WaitForTimeoutAsync(600);
            break;
        }
        case "drag":
        {
            var d = Nums(value);
            await page.Mouse.MoveAsync(d[0], d[1]);
            await page.Mouse.DownAsync();
            for (int step = 1; step <= 12; step++) await page.Mouse.MoveAsync(d[0] + (d[2] - d[0]) * step / 12f, d[1] + (d[3] - d[1]) * step / 12f);
            await page.Mouse.UpAsync();
            await page.WaitForTimeoutAsync(500);
            break;
        }
        case "wheel":
        {
            var w = Nums(value);
            await page.Mouse.MoveAsync(w[0], w[1]);
            await page.Mouse.WheelAsync(0, w[2]);
            await page.WaitForTimeoutAsync(500);
            break;
        }
        case "press":
            await page.Keyboard.PressAsync(value);
            await page.WaitForTimeoutAsync(300);
            break;
        case "hold":
        {
            var h = Nums(value);
            await page.Mouse.MoveAsync(h[0], h[1]);
            await page.Mouse.DownAsync();
            await page.WaitForTimeoutAsync((int)h[2]);
            await page.Mouse.UpAsync();
            await page.WaitForTimeoutAsync(400);
            break;
        }
        case "sleep":
            await page.WaitForTimeoutAsync(int.Parse(value));
            break;
        case "snap":
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = value });
            Console.WriteLine($"snapped {value}");
            break;
    }
}
await page.ScreenshotAsync(new PageScreenshotOptions { Path = output });
Console.WriteLine($"saved {output} ({width}x{height} at {scale}x)");
return 0;
