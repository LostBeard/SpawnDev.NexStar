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
| **Phone sensors** | `PhoneSensorService`: device orientation for a push-to aid. |

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
- No fixed azimuth stop was found. Celestron's hand control has an optional **Cordwrap** setting (Scope Setup, off by default) that defines a "no-cross line" the scope will not slew over; see [Celestron: What is cordwrap?](https://www.celestron.com/blogs/knowledgebase/what-is-cordwrap-how-can-i-prevent-it). Whether it is on cannot be read over the serial protocol; the guard works the same either way.

Other models, or other firmware, may differ: re-run the probe stages before trusting these numbers.

## Tests

Run through PlaywrightMultiTest (`dotnet test PlaywrightMultiTest`). The suite covers the framing, codecs, clock, command channel, the service over a protocol simulator, the cable guard over a physical mount simulator, and the astronomy against JPL Horizons. The hardware tests talk to a real mount on a desktop serial port (`NEXSTAR_PORT`, or auto-detected) and are **read-only**; they report as unsupported when no mount answers.

## Data sources and credits

| Feature | Source | License |
|---|---|---|
| Satellite TLE data | [CelesTrak](https://celestrak.org/) | Free for non-commercial use |
| Aircraft | [OpenSky Network](https://opensky-network.org/) | Free API (non-commercial) |
| Planet positions | JPL "Keplerian Elements for Approximate Positions of the Major Planets" (E. M. Standish) | Public |
| Moon series | Meeus, *Astronomical Algorithms*, Tables 47.A and 47.B; the coefficients were read from [astronomia](https://github.com/commenthol/astronomia) (Sonia Keys, commenthol) | MIT |
| Reference positions in the tests | [NASA/JPL Horizons](https://ssd.jpl.nasa.gov/horizons/) | Public |
| SGP4 | [SGP.NET](https://www.nuget.org/packages/SGPdotNET/) | |

Planet accuracy is the documented accuracy of that table (Jupiter 400 arcseconds, Saturn 600, the inner planets under 50); the Moon is good to about 20 arcseconds. That is for finding and pointing, not occultation timing.

## Requirements

- Chrome or Edge with Web Serial (desktop) or WebUSB (Android).
- A Celestron NexStar telescope and a USB cable to the hand control's port.
- The Sun must never be pointed at without a solar filter: the library does not list it as a goto target.
