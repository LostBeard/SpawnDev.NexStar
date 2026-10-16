using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SpawnDev.SpawnJS;
using SpawnDev.NexStar;
using SpawnDev.NexStar.App;
using SpawnDev.NexStar.App.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddSpawnJSRuntime(out var JS);
builder.Services.AddSingleton<NexStarService>();
builder.Services.AddSingleton<PhoneSensorService>();
builder.Services.AddSingleton<LocationService>();
builder.Services.AddSingleton<SatelliteService>();
builder.Services.AddSingleton<PlaneService>();
builder.Services.AddSingleton<OverheadService>();
builder.Services.AddSingleton(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

if (JS.IsWindow)
{
    builder.RootComponents.Add<App>("#app");
    builder.RootComponents.Add<HeadOutlet>("head::after");
}

await builder.Build().SpawnJSRunAsync();
