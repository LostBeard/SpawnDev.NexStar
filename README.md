# SpawnDev.NexStar

Control a Celestron NexStar telescope from a browser. **SpawnDev.NexStar** talks to the hand control over the [Web Serial API](https://developer.mozilla.org/en-US/docs/Web/API/Web_Serial_API) (desktop) or [WebUSB](https://developer.mozilla.org/en-US/docs/Web/API/WebUSB_API) (Android, through the Prolific PL2303 adapter), in Blazor WebAssembly, with no server. It is built on [SpawnDev.SpawnJS](https://github.com/LostBeard/SpawnDev.SpawnJS).

[![NuGet version](https://badge.fury.io/nu/SpawnDev.NexStar.svg?label=SpawnDev.NexStar)](https://www.nuget.org/packages/SpawnDev.NexStar)

**[Launch the app](https://lostbeard.github.io/SpawnDev.NexStar/)**

Tested on a Celestron 127SLT (hand control 5.35). The protocol follows Celestron's *NexStar Communication Protocol*; the other models it lists (NexStar GPS, i-Series, SE, GT, CPC, Advanced GT, CGE, Evolution) should work but have not been tried.

---

## What the library does

| Area | Details |
|---|---|
| **Connection** | `NexStarService` over any `INexStarTransport`: WebSerial, WebUSB (a hand-written PL2303 driver), or a desktop serial port (`SpawnDev.NexStar.Desktop`). |
| **Protocol** | Every response is framed by its command's fixed length, not "up to the first `#`" (binary replies contain `0x23`: this mount's version is 5.35). A failed read returns `null`; it is never a position of (0, 0) or a state of "not aligned". |
| **Mount control** | GoTo and sync (RA/Dec, Az/Alt), fixed and variable slews, tracking modes, time and location. |
| **Cable guard** | Counts the turns of the azimuth axis so the hand-control and power cords cannot wrap. See below. |
| **Astronomy** | Planets from the JPL approximate elements, the Moon from the full Meeus series with parallax, phase, libration and lit craters, precession from J2000, sidereal time, Az/Alt conversion, refraction. Validated against NASA/JPL Horizons. |
| **Pointing** | `PointingModel` (offsets for an unaligned mount), `TelescopeController` (true-frame gotos, `SunGuard`, the slew pad's watchdog, align here). |
| **Eyepiece / vision** | `SpawnDev.NexStar.Eyepiece` (pairing, handshake, WebRTC session, camera pose, magnetic declination), `SpawnDev.NexStar.Vision` (plate solver), `SpawnDev.NexStar.Vision.Gpu` (star detector, catalog cone search). |

An unaligned mount (check `IsAligned`) reports RA/Dec that mean nothing, its Az/Alt are relative to the direction it was powered on, and RA/Dec GoTo does nothing until it is aligned.

## Usage

```csharp
// Program.cs
builder.Services.AddSpawnJSRuntime(out var JS);
builder.Services.AddSingleton<NexStarService>();
```

```razor
@inject NexStarService NexStar

<button @onclick="Connect">Connect</button>

@code {
    async Task Connect()
    {
        // opens the browser's device picker; true once a hand control has answered
        if (!await NexStar.SelectPortAsync(useWebUSB: false))
            Console.WriteLine(NexStar.LastError);
    }
}
```

```csharp
var pos = await NexStar.PollAsync();            // read Az/Alt, update the cable count, run the cable checks (call about once a second)
await NexStar.GotoAzAltGuardedAsync(230, 20);   // goes by a route that cannot wrap the cable
await NexStar.JogAsync(2, 0);                   // a small bounded move: safe for a press-and-hold button when you cannot see the telescope
await NexStar.UnwindCableAsync();               // back to cable-neutral, the long way round if that is the way that unwinds the cord
```

Without a browser (tools, tests):

```csharp
var transport = await SystemSerialTransport.FindMountAsync();  // NEXSTAR_PORT, or the first port that answers an echo
var service = new NexStarService();
await service.AttachTransportAsync(transport!);
```

## The workspace

The app opens on one full-screen canvas (drawn with WebGPU through [SpawnDev.GameUI](https://github.com/LostBeard/SpawnDev.GameUI), so the same screen can go to a headset): a sky chart with every Hipparcos star, the Messier, NGC and IC objects, constellation figures and borders, grids, the Moon, planets and the Sun, in night-vision red or in colour. Drag to look around, wheel or pinch to zoom, tap a star, object or planet for its card (**Go to**, **Center**, **Align here**).

- **Telescope dock**: a press-and-hold slew pad with the measured rate table, tracking, and the cable bar with **Unwind** and **Centered here**. A held button is released when the page loses focus, and by a heartbeat watchdog if the page stops answering, because a mount's slew never ends by itself.
- **Gotos are in the sky's frame.** An unaligned mount's own azimuth and altitude are relative to the power-on direction, so one centred object (**Align here**, or a plate solve) sets the offsets and every goto afterwards goes through them. Gotos are refused near the Sun (12 degrees, while it is up), below the horizon, and when there is no frame.
- **Time bar** steps the chart's clock; a shifted chart is labelled and never sends the telescope anywhere. **View** switches layers, names, night vision and star size.
- **Try it with no telescope:** `/workspace?sim=1` connects a simulated 127SLT (labelled DEMO everywhere). A link can also set the view: `?az=250&alt=20&fov=30&lat=..&lon=..&utc=..&night=0&layers=..`.

## Eyepiece Mode

Put a phone on the eyepiece, open `/eyepiece` on it, and its camera, compass and tilt stream to the controlling screen.

1. On the controlling screen open **Eyepiece**, press **Pair a phone**: a QR code and link appear.
2. Scan the code with the phone's camera app. The link carries a random room and a shared secret in the part after the `#`, which no server ever sees. Press **Start** on the phone (it keeps the screen awake). It reconnects by itself next time.
3. The phone's picture appears on the canvas (a GPU texture; the pixels never pass through .NET), with its camera's azimuth (true north, from the World Magnetic Model), altitude and roll.

Transport is [SpawnDev.RTC](https://github.com/LostBeard/SpawnDev.RTC) (WebRTC) through the signalling hub; the hub only introduces the two ends. Anyone who learns the room could connect, so each side must prove it knows the secret: an HMAC over both sides' fresh nonces and both DTLS certificate fingerprints, which defeats replays and a man in the middle relaying between two connections. A stranger gets no video, no numbers and does not block the real phone (tested against a stranger on the same room).

### Plate solving and auto-alignment

**Solve** finds the stars in the phone's picture on the GPU, picks the catalog stars near where the telescope is thought to point (also on the GPU), and solves for where the picture really points, its rotation, plate scale and mirroring. On an unaligned mount **Align telescope here** then uses the solution as the alignment.

What is verified: on pictures drawn from the real Hipparcos catalog the solver recovers the centre within about an arcsecond, the scale to 0.01% and the roll exactly, over the whole sky, either parity; and it never solves a picture that is not of the sky (the chance of an accidental match counts every guess made, and that is tested to matter). The whole path, video through WebRTC to the answer, runs in headless Chrome.

What is **not** verified: real eyepiece footage. The catalog here is Hipparcos (to about magnitude 11), so a narrow eyepiece field (under about a degree) may hold too few catalog stars to solve; a deeper catalog fetched by cone search is the planned fix. The first solve with an unknown plate scale can take a while; later solves reuse the saved scale.

## Cable guard

The hand control and power cord run to the part of the mount that turns. The mount reports azimuth only as 0-360, so after a turn and a bit it reads exactly as after a bit: nothing in it says how many times the cord went round. `CableGuard` (`NexStarService.Cable`) keeps that count itself.

- **Running total.** Every azimuth reading is unwrapped into the degrees turned from the cable-neutral position (`Cumulative`), kept between sessions (`Cable.State.ToJson()`), with a limit of +/-200 degrees by default (400 of travel; configurable).
- **Routing.** A goto is planned around the limit: if the short way would wrap the cord, it goes the long way in steps under 150 degrees, so the mount's own short-way choice for each step is the planned way. After each step the real position is checked against the plan, and the move is cancelled if the mount turns the wrong way.
- **Slews** are slowed in the warning margin and stopped before the limit by a monitor that watches the real position. **Tracking** is switched off at the limit (and the time left is reported).
- **It says when it does not know.** After a long gap in readings, a lost connection mid-move, or on a fresh start, the guard refuses motion until you confirm where the cable is (`Cable.SetNeutral`, `Cable.Confirm`).
- **A sync moves the reading, not the cable.** After a sync or alignment the next reading is taken as a new baseline.
- **Out of sight.** A slew does not end by itself: if the page dies, the mount keeps going. For remote use, drive it with `JogAsync`: each step ends by itself.

### Measured on a real 127SLT (hand control 5.35, unaligned)

The routing rules rest on measurements, not assumptions (`SpawnDev.NexStar.Probe`, and the motion tests in `HardwareMotionTests`):

- A goto **starts the short way round** for every turn tried up to 170 degrees both ways. The mount's cut-over is not exactly 180: it goes up for turns up to about +182.5 and down only up to about -177.5. The guard's steps are at most 150 degrees, 27 degrees inside that.
- Gotos work on an unaligned mount (Az/Alt are raw encoder angles relative to the power-on direction), land within about 0.005 degrees, and run at up to about 3 degrees a second.
- A fixed-rate slew with **positive** direction turns the azimuth **up**. Speeds in degrees per second, rates 1 to 9: 0.002, 0.004, 0.017, 0.033, 0.067, 0.27, 1.0, 1.75, about 3 (`NexStarProtocol.MeasuredFixedRateDegreesPerSecond`). The slow-down ladder is built from these, not guessed.
- No fixed azimuth stop was found. Celestron's hand control has an optional **Cordwrap** setting (Scope Setup, off by default) that defines a "no-cross line" the scope will not slew over; see [Celestron: What is cordwrap?](https://www.celestron.com/blogs/knowledgebase/what-is-cordwrap-how-can-i-prevent-it). Whether it is on cannot be read over the serial protocol; the guard works the same either way. On the 127SLT this was tested on it is **off**, so the guard is the only thing stopping a wrapped cord.

Other models, or other firmware, may differ: re-run the probe stages before trusting these numbers.

## Tests

Run through PlaywrightMultiTest (`dotnet test PlaywrightMultiTest`). The suite covers the framing, codecs, clock, command channel, the service over a protocol simulator, the cable guard over a physical mount simulator, the astronomy against JPL Horizons, the sky chart's geometry against the GPU's pixels (a real WebGPU device in Chrome), the pairing handshake (wrong secret, replay, man in the middle), a real two-peer eyepiece link through the hub with a fake camera, the QR code bit for bit against ZXing's encoder, the World Magnetic Model against NOAA's test values, and the star detector and plate solver on pictures with known stars. Every guard has been red-checked: disable the fix, watch its test fail.

For looking at the app: `SpawnDev.NexStar.Shot` drives a published build in headless Chrome (`shot <url> out.png --click x,y --hold x,y,ms --snap file`; `shot pair <base> <dir>` pairs a fake phone; `shot solve <base> <dir>` streams a synthetic star field and solves it). The hardware tests talk to a real mount on a desktop serial port (`NEXSTAR_PORT`, or auto-detected) and are **read-only**; they report as unsupported when no mount answers.

## Data sources and credits

| Feature | Source | License |
|---|---|---|
| Satellite TLE data | [CelesTrak](https://celestrak.org/) | Free for non-commercial use |
| Aircraft | [OpenSky Network](https://opensky-network.org/) | Free API (non-commercial) |
| Planet positions | JPL "Keplerian Elements for Approximate Positions of the Major Planets" (E. M. Standish) | Public |
| Moon series | Meeus, *Astronomical Algorithms*, Tables 47.A and 47.B; the coefficients were read from [astronomia](https://github.com/commenthol/astronomia) (Sonia Keys, commenthol) | MIT |
| Reference positions in the tests | [NASA/JPL Horizons](https://ssd.jpl.nasa.gov/horizons/) | Public |
| SGP4 | [SGP.NET](https://www.nuget.org/packages/SGPdotNET/) | |
| Stars | Hipparcos (ESA/CDS), names from the IAU Catalog of Star Names | ESA public use, IAU CC BY |
| Deep-sky objects | [OpenNGC](https://github.com/mattiaverga/OpenNGC) | CC BY-SA 4.0 |
| Constellation figures and borders | Stellarium modern skyculture | CC BY-SA 4.0 |
| Magnetic declination | NOAA / BGS World Magnetic Model 2025 | Public domain |
| QR encoder | from SpawnDev.ILGPU (same author), with its format-information bug fixed; checked against [ZXing.Net](https://github.com/micjahn/ZXing.Net) in the tests only | MIT / Apache-2.0 |

The shipped data files are CC BY-SA 4.0 (see `wwwroot/data/ATTRIBUTION.md`).

Planet accuracy is the documented accuracy of that table (Jupiter 400 arcseconds, Saturn 600, the inner planets under 50); the Moon is good to about 20 arcseconds. That is for finding and pointing, not occultation timing.

## Requirements

- Chrome or Edge with Web Serial (desktop) or WebUSB (Android).
- A Celestron NexStar telescope and a USB cable to the hand control's port.
- The Sun must never be pointed at without a solar filter: the library does not list it as a goto target.
