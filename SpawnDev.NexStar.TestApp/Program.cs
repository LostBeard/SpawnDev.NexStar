using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SpawnDev.NexStar.BrowserTests;
using SpawnDev.SpawnJS;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.Services.AddSpawnJSRuntime();
// handle lifetime is manual in SpawnJS: the watcher names anything the renderer test leaks
SpawnJSRuntime.EnableIDisposableWatcher = true;

// the test classes PMT discovers
builder.Services.AddSingleton<SkyRendererTests>();

builder.RootComponents.Add<SpawnDev.NexStar.TestApp.App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

await builder.Build().SpawnJSRunAsync();
