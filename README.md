# SpawnDev.NexStar

**SpawnDev.NexStar** provides a robust `NexStarService` for communicating with and controlling Celestron NexStar telescopes directly from a browser using the [Web Serial API](https://developer.mozilla.org/en-US/docs/Web/API/Web_Serial_API) in Blazor WebAssembly.

Built on top of [SpawnDev.SpawnJS](https://github.com/LostBeard/SpawnDev.SpawnJS), this library enables full hardware access without requiring a backend server.

[![NuGet version](https://badge.fury.io/nu/SpawnDev.NexStar.svg?label=SpawnDev.NexStar)](https://www.nuget.org/packages/SpawnDev.NexStar)

---

## 🔭 SpawnDev NexStar App
**[Launch NexStar Control](https://lostbeard.github.io/SpawnDev.NexStar/)**

The repository includes `SpawnDev.NexStar.App`, a full-featured Progressive Web App (PWA) demonstrating the library's capabilities.

### App Features:
- **Telescope Control**: 
  - Complete directional slewing with variable rates.
  - GoTo coordinates (RA/Dec).
  - Tracking mode selection (EQ North, EQ South, Alt-Az, Off).
- **Object Browser**: 
  - Catalog of 110 Messier objects and bright alignment stars.
  - "Quick Access" panel showing objects currently visible from your location.
  - Filter by constellation, magnitude, and type.
- **Satellite Tracker** 🛰️:
  - Real-time tracking of orbital objects (ISS, Starlink, weather satellites, etc.).
  - Fetches TLE data from CelesTrak with offline caching.
  - Automatic telescope slewing to track selected satellite.
- **Plane Tracker** ✈️:
  - Real-time tracking of nearby aircraft using ADS-B data.
  - Displays callsign, altitude, speed, and distance.
  - Calculate look angles and track aircraft with telescope.
- **Alignment Helper**: 
  - Real-time suggestions for best alignment stars based on time and location.
  - Visual tracking of alignment status.
- **Location Services**: 
  - Sync telescope time and location with browser data.
- **Fallback USB Support**: 
  - For Android devices, uses Web USB API to connect via Prolific PL2303 driver.
- **Dark Mode**: 
  - Optimized dark red/black UI for viewing in low light environments.

### Data Sources & Credits

| Feature | Source | License |
|---------|--------|---------|
| **Satellite TLE Data** | [CelesTrak](https://celestrak.org/) | Free for non-commercial use |
| **Aircraft Data** | [OpenSky Network](https://opensky-network.org/) | Free API (non-commercial) |

### Dependencies

| Package | Purpose |
|---------|---------|
| [SGP.NET](https://www.nuget.org/packages/SGPdotNET/) | SGP4 orbital propagation for satellite tracking |

---

## 💻 Library Features (`SpawnDev.NexStar`)

### Core Capabilities
- **Web Serial Connectivity**: Direct serial port selection and connection via browser.
- **Web USB Connectivity**: Fallback connection method for Android devices using Web USB API.
- **Command Protocol**: Implementation of the Celestron NexStar communication protocol.
- **Position Tracking**: 
  - Real-time RA/Dec and Az/Alt monitoring.
  - Automatic polling and state management.
- **Astronomy Math**: 
  - Built-in utilities for coordinate conversion (Equatorial <-> Horizontal).
  - LST (Local Sidereal Time) calculation.
  - Visibility calculations based on observer location.
- **Catalogs**: 
  - Integrated database of alignment stars and Messier objects.

### Installation

```bash
dotnet add package SpawnDev.NexStar
```

### Usage Example (Program.cs)

```cs
using SpawnDev.SpawnJS;
using SpawnDev.NexStar;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Add SpawnJSRuntime (Required for Web Serial/USB)
builder.Services.AddBlazorJSRuntime();

// Add NexStarService
builder.Services.AddSingleton<NexStarService>();

await builder.Build().BlazorJSRunAsync();
```

### Common Usage Examples

```razor
@inject NexStarService NexStar

<button @onclick="Connect" disabled="@NexStar.SerialPortSelected">Connect</button>
<button @onclick="Disconnect" disabled="@(!NexStar.SerialPortSelected)">Disconnect</button>

@if (NexStar.SerialPortSelected)
{
    <p>Model: @NexStar.Model | Aligned: @NexStar.IsAligned</p>
    <p>RA: @NexStar.CurrentRa?.ToString("F4")h | Dec: @NexStar.CurrentDec?.ToString("F2")°</p>
    <p>Az: @NexStar.CurrentAz?.ToString("F2")° | Alt: @NexStar.CurrentAlt?.ToString("F2")°</p>
    
    <button @onclick="GoToM31">GoTo M31 (Andromeda)</button>
    <button @onclick="StopSlew">Stop</button>
}

@code {
    // Connect to telescope (opens device picker)
    private async Task Connect()
    {
        if (await NexStar.SelectPortAsync())
        {
            Console.WriteLine($"Connected to {NexStar.Model}");
        }
    }

    // Disconnect from telescope
    private async Task Disconnect() => await NexStar.DeselectPortAsync();

    // GoTo a specific RA/Dec coordinate (M31 Andromeda Galaxy)
    private async Task GoToM31()
    {
        double ra = 0.712;   // RA in hours (0h 42m 44s)
        double dec = 41.27;  // Dec in degrees (+41° 16')
        await NexStar.GotoRaDecAsync(ra, dec);
    }

    // GoTo using Az/Alt (useful for terrestrial or satellite tracking)
    private async Task GoToAzAlt(double az, double alt)
    {
        await NexStar.GotoAzAltAsync(az, alt);
    }

    // Slew manually (for directional buttons)
    private async Task SlewNorth() => await NexStar.SlewFixedAsync(SlewAxis.Dec, SlewDirection.Positive, 5);
    private async Task SlewSouth() => await NexStar.SlewFixedAsync(SlewAxis.Dec, SlewDirection.Negative, 5);
    private async Task SlewEast()  => await NexStar.SlewFixedAsync(SlewAxis.Ra, SlewDirection.Positive, 5);
    private async Task SlewWest()  => await NexStar.SlewFixedAsync(SlewAxis.Ra, SlewDirection.Negative, 5);

    // Stop all slewing
    private async Task StopSlew() => await NexStar.StopAllSlewAsync();

    // Set tracking mode
    private async Task SetTracking(TrackingMode mode) => await NexStar.SetTrackingModeAsync(mode);

    // Sync current position (after centering on a known star)
    private async Task SyncPosition(double ra, double dec) => await NexStar.SyncRaDecAsync(ra, dec);

    // Set telescope time/location from browser
    private async Task SyncTimeLocation()
    {
        await NexStar.SetTimeAsync(DateTime.UtcNow);
        // Location can be set via NexStar.SetLocationAsync(lat, lon)
    }
}
```

### Key Properties

| Property | Type | Description |
|----------|------|-------------|
| `SerialPortSelected` | `bool` | True if connected to telescope |
| `IsAligned` | `bool` | True if telescope is aligned |
| `Model` | `string?` | Telescope model name |
| `CurrentRa` / `CurrentDec` | `double?` | Current RA/Dec position |
| `CurrentAz` / `CurrentAlt` | `double?` | Current Az/Alt position |
| `TrackingMode` | `TrackingMode` | Current tracking mode |

### Key Methods

| Method | Description |
|--------|-------------|
| `SelectPortAsync()` | Opens device picker, connects to telescope |
| `DeselectPortAsync()` | Disconnects from telescope |
| `GotoRaDecAsync(ra, dec)` | Slew to RA/Dec coordinates |
| `GotoAzAltAsync(az, alt)` | Slew to Az/Alt coordinates |
| `SlewFixedAsync(axis, dir, rate)` | Manual slew at fixed rate (1-9) |
| `StopAllSlewAsync()` | Stop all motion |
| `SyncRaDecAsync(ra, dec)` | Sync position after centering |
| `SetTrackingModeAsync(mode)` | Set tracking (Off, AltAz, EqNorth, EqSouth) |

### Requirements
- A browser with **Web Serial API** support (Chrome, Edge, Opera).
- A browser with **Web USB API** support (Chrome on Android).
- A Celestron NexStar telescope (or compatible mount).
- A USB cable connected to the telescope's hand controller port.

### Tested Platforms
- **Windows (Chrome)**: Verified working with Web Serial API.
- **Android (Chrome)**: Verified working with Web USB API (using minimal PL2303 driver).
- **Web Serial**: Generic Support
- **Web USB**: Generic Prolific PL2303 Support


