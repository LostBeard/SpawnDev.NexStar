// SpawnDev.NexStar.Probe: measures how the REAL mount behaves, for the things the cable guard depends on.
// It MOVES the telescope, so it only runs a named stage, never goes more than MaxFromStart degrees from where it started,
// never touches altitude, refuses to run if the mount is tracking, and stops and cancels on every exit (including Ctrl+C).
//
//   probe baseline        read-only: position stability, command latency, goto-in-progress flag
//   probe small-goto      goto +5 and -5 degrees and back: does a goto work on this (unaligned) mount, how long, how exact
//   probe slew-rates      each fixed rate 1..9, both signs, a few degrees at most: which way "positive" turns and how fast each rate is
//   probe goto-direction  for many requested turns (10 to 350 degrees, both ways): which way does the mount START turning? Moves about 1.5 degrees, then cancels
//   probe goto-big        a guarded goto of +90 degrees and back through the cable guard (about 90 degrees each way)
//
// Needs NEXSTAR_ALLOW_MOTION=1 for every stage that moves.
using System.Diagnostics;
using SpawnDev.NexStar;
using SpawnDev.NexStar.Desktop;

const double MaxFromStart = 190; // TJ: the cords allow 200 either way from neutral
var stage = args.FirstOrDefault() ?? "";
var moves = stage is "small-goto" or "slew-rates" or "goto-direction" or "goto-big" or "goto-window";
if (moves && Environment.GetEnvironmentVariable("NEXSTAR_ALLOW_MOTION") != "1")
{
    Console.WriteLine("This stage moves the telescope. Set NEXSTAR_ALLOW_MOTION=1 to run it.");
    return 2;
}

var transport = await SystemSerialTransport.FindMountAsync();
if (transport == null) { Console.WriteLine("No hand control answered on any serial port."); return 3; }
var service = new NexStarService();
if (!await service.AttachTransportAsync(transport)) { Console.WriteLine($"Attach failed: {service.LastError}"); return 3; }

Console.WriteLine($"Mount: {service.Model} hand control {service.Version}, aligned {service.IsAligned}, tracking {service.CurrentTrackingMode}");
if (service.CurrentTrackingMode != TrackingMode.Off) { Console.WriteLine("The mount is tracking. Not running."); return 4; }

var start = await service.GetAzAltAsync(precise: true);
if (start == null) { Console.WriteLine("Could not read the position."); return 3; }
var startAz = start.Azimuth;
var alt0 = start.Altitude;
Console.WriteLine($"Start: az {startAz:F4}  alt {alt0:F4}");
service.Cable.SetNeutral(startAz, DateTime.UtcNow);

var clock = Stopwatch.StartNew();
var aborted = false;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; aborted = true; };

static double Wrap180(double a) { a %= 360; if (a > 180) a -= 360; if (a < -180) a += 360; return a; }

async Task StopEverything()
{
    for (int i = 0; i < 3; i++)
    {
        var a = await service.StopAllSlewAsync();
        var b = await service.CancelGotoAsync();
        if (a && b) break;
    }
}

/// <summary>Degrees turned from the start azimuth, short way; null if unreadable.</summary>
async Task<double?> Offset()
{
    var p = await service.GetAzAltAsync(precise: true);
    if (p == null) return null;
    var d = Wrap180(p.Azimuth - startAz);
    if (Math.Abs(d) > MaxFromStart) { Console.WriteLine($"ABORT: {d:F1} degrees from the start"); aborted = true; }
    return d;
}

async Task WaitStill(double seconds = 8)
{
    var t = Stopwatch.StartNew();
    double? last = null;
    int still = 0;
    while (t.Elapsed.TotalSeconds < seconds)
    {
        var o = await Offset();
        if (o != null && last != null && Math.Abs(o.Value - last.Value) < 0.002) { if (++still >= 4) return; } else still = 0;
        last = o;
        await Task.Delay(120);
    }
}

try
{
    switch (stage)
    {
        case "baseline":
        {
            var lat = new List<double>();
            var vals = new List<double>();
            for (int i = 0; i < 40; i++)
            {
                var t0 = clock.Elapsed.TotalMilliseconds;
                var p = await service.GetAzAltAsync(precise: true);
                lat.Add(clock.Elapsed.TotalMilliseconds - t0);
                if (p != null) vals.Add(p.Azimuth);
                await Task.Delay(50);
            }
            Console.WriteLine($"azimuth over 40 reads: min {vals.Min():F6} max {vals.Max():F6} spread {(vals.Max() - vals.Min()) * 3600:F2} arcsec");
            Console.WriteLine($"latency of a precise position read: mean {lat.Average():F0} ms, max {lat.Max():F0} ms");
            Console.WriteLine($"goto in progress: {await service.IsGotoInProgressAsync()}");
            break;
        }

        case "small-goto":
        {
            foreach (var d in new[] { 5.0, -5.0 })
            {
                if (aborted) break;
                var target = (startAz + d + 360) % 360;
                var t0 = clock.Elapsed.TotalSeconds;
                var ok = await service.GotoAzAltAsync(target, alt0, precise: true);
                Console.WriteLine($"goto {d:+0;-0} deg (az {target:F3}): accepted {ok}");
                if (!ok) break;
                double? firstL1 = null, firstMove = null;
                double peak = 0; double? prev = null; var prevT = t0;
                while (clock.Elapsed.TotalSeconds - t0 < 25 && !aborted)
                {
                    var inProg = await service.IsGotoInProgressAsync();
                    var o = await Offset();
                    var now = clock.Elapsed.TotalSeconds;
                    if (inProg == true && firstL1 == null) firstL1 = now - t0;
                    if (o != null && Math.Abs(o.Value) > 0.05 && firstMove == null) firstMove = now - t0;
                    if (o != null && prev != null && now > prevT) peak = Math.Max(peak, Math.Abs(o.Value - prev.Value) / (now - prevT));
                    if (o != null) prev = o; prevT = now;
                    if (inProg == false && now - t0 > 0.5) break;
                    await Task.Delay(60);
                }
                var end = await Offset();
                Console.WriteLine($"   L became 1 after {firstL1:F2} s, motion seen after {firstMove:F2} s, finished after {clock.Elapsed.TotalSeconds - t0:F2} s, ended at {end:F4} deg (asked {d}), peak speed about {peak:F2} deg/s");
                await WaitStill();
            }
            var home = await service.GotoAzAltAsync(startAz, alt0, precise: true);
            await Task.Delay(500);
            while (await service.IsGotoInProgressAsync() == true && !aborted) await Task.Delay(100);
            await WaitStill();
            Console.WriteLine($"back at {await Offset():F4} deg from the start (goto accepted {home})");
            break;
        }

        case "slew-rates":
        {
            Console.WriteLine("rate | sign sent | turned | seconds | speed deg/s | direction of azimuth");
            double estimate = 0.05;
            for (int rate = 1; rate <= 9 && !aborted; rate++)
            {
                foreach (var dir in new[] { SlewDirection.Positive, SlewDirection.Negative })
                {
                    if (aborted) break;
                    var before = await Offset() ?? 0;
                    var maxSeconds = Math.Min(3.0, 3.0 / Math.Max(estimate * (rate == 1 ? 1 : 3), 0.02));
                    var t0 = clock.Elapsed.TotalSeconds;
                    if (!await service.SlewFixedAsync(SlewAxis.RaAzm, dir, (SlewRate)rate)) { Console.WriteLine($"{rate} {dir}: not accepted"); break; }
                    double? o = before;
                    var samples = new List<(double t, double o)>();
                    while (!aborted)
                    {
                        o = await Offset();
                        var now = clock.Elapsed.TotalSeconds - t0;
                        if (o != null) samples.Add((now, o.Value));
                        if (now >= maxSeconds || (o != null && Math.Abs(o.Value - before) >= 4.0)) break;
                        await Task.Delay(40);
                    }
                    await service.StopAllSlewAsync();
                    var stopAt = clock.Elapsed.TotalSeconds - t0;
                    await WaitStill(5);
                    var after = await Offset() ?? before;
                    // speed from the second half of the samples (past the ramp-up)
                    var tail = samples.Where(s => s.t > samples.Last().t * 0.4).ToList();
                    double speed = tail.Count >= 2 ? Math.Abs(tail.Last().o - tail.First().o) / (tail.Last().t - tail.First().t) : 0;
                    if (dir == SlewDirection.Positive && speed > 0) estimate = speed;
                    Console.WriteLine($"{rate} | {dir,-8} | {after - before,7:F3} | {stopAt,6:F2} | {speed,6:F3} | {(after - before > 0.01 ? "increasing" : after - before < -0.01 ? "decreasing" : "none")}");
                }
            }
            // come home
            await service.GotoAzAltAsync(startAz, alt0, precise: true);
            await Task.Delay(500);
            while (await service.IsGotoInProgressAsync() == true && !aborted) await Task.Delay(100);
            await WaitStill();
            Console.WriteLine($"back at {await Offset():F4} deg from the start");
            break;
        }

        case "goto-direction":
        {
            Console.WriteLine("requested turn | shortest way | observed first direction | after cancel offset");
            var turns = new[] { 10.0, -10.0, 90, -90, 170, -170, 179, -179, 181, -181, 190, -190, 270, -270, 350, -350 };
            foreach (var turn in turns)
            {
                if (aborted) break;
                var target = ((startAz + turn) % 360 + 360) % 360;
                var shortest = Wrap180(turn);
                if (!await service.GotoAzAltAsync(target, alt0, precise: true)) { Console.WriteLine($"{turn:+0;-0}: goto not accepted"); continue; }
                var t0 = clock.Elapsed.TotalSeconds;
                double seen = 0; double? o = 0;
                while (clock.Elapsed.TotalSeconds - t0 < 6 && !aborted)
                {
                    await Task.Delay(40);
                    o = await Offset();
                    if (o != null && Math.Abs(o.Value) >= 1.5) { seen = o.Value; break; }
                }
                await service.CancelGotoAsync();
                await StopEverything();
                await WaitStill(6);
                var settled = await Offset();
                var dir = seen > 0 ? "UP (azimuth increasing)" : seen < 0 ? "DOWN (azimuth decreasing)" : "no motion seen";
                var agrees = seen == 0 ? "?" : (Math.Sign(seen) == Math.Sign(shortest) ? "short way" : "THE LONG WAY");
                Console.WriteLine($"{turn,+7:+0;-0} | {(shortest > 0 ? "up" : "down"),-5} | {dir,-26} | {agrees,-12} | at {settled:F3}");
                // back to the start
                await service.GotoAzAltAsync(startAz, alt0, precise: true);
                await Task.Delay(400);
                var back = clock.Elapsed.TotalSeconds;
                while (await service.IsGotoInProgressAsync() == true && !aborted && clock.Elapsed.TotalSeconds - back < 20) await Task.Delay(100);
                await WaitStill(6);
            }
            break;
        }

        case "goto-window":
        {
            // Is the mount a straight-line axis with a fixed 360-degree window (a firmware cord wrap)? Find the barrier P by bisection,
            // then check the prediction from a different starting azimuth. Each trial moves about 1.2 degrees and cancels.
            async Task<double> Direction(double baseAz, double targetAz)
            {
                if (!await service.GotoAzAltAsync(((targetAz % 360) + 360) % 360, alt0, precise: true)) return double.NaN;
                var t0 = clock.Elapsed.TotalSeconds;
                double seen = 0;
                while (clock.Elapsed.TotalSeconds - t0 < 6 && !aborted)
                {
                    await Task.Delay(40);
                    var p = await service.GetAzAltAsync(precise: true);
                    if (p == null) continue;
                    var d = Wrap180(p.Azimuth - baseAz);
                    if (Math.Abs(d) > MaxFromStart + 5) { aborted = true; break; }
                    if (Math.Abs(d) >= 1.2) { seen = d; break; }
                }
                await service.CancelGotoAsync();
                await StopEverything();
                await WaitStill(6);
                await service.GotoAzAltAsync(baseAz, alt0, precise: true);
                await Task.Delay(400);
                var back = clock.Elapsed.TotalSeconds;
                while (await service.IsGotoInProgressAsync() == true && !aborted && clock.Elapsed.TotalSeconds - back < 20) await Task.Delay(100);
                await WaitStill(6);
                return seen;
            }

            // Phase A: from the start, targets between 303.52 (went up) and 312.52 (went down)
            double lo = 303.52, hi = 312.52; // up / down
            Console.WriteLine($"A: bisecting the barrier from az {startAz:F2}; known: up at {lo}, down at {hi}");
            for (int i = 0; i < 5 && !aborted; i++)
            {
                var mid = (lo + hi) / 2;
                var d = await Direction(startAz, mid);
                Console.WriteLine($"   target {mid:F3}: {(d > 0 ? "UP" : d < 0 ? "DOWN" : "none")} ({d:F2})");
                if (double.IsNaN(d) || d == 0) break;
                if (d > 0) lo = mid; else hi = mid;
            }
            var barrier = (lo + hi) / 2;
            Console.WriteLine($"   barrier P is between {lo:F3} and {hi:F3} (about {barrier:F2})");

            // Phase B: move up 60 degrees and test predictions of the straight-line-window model from there
            Console.WriteLine($"B: moving to az {startAz + 60:F2}");
            await service.GotoAzAltAsync(startAz + 60, alt0, precise: true);
            await Task.Delay(500);
            var tt = clock.Elapsed.TotalSeconds;
            while (await service.IsGotoInProgressAsync() == true && !aborted && clock.Elapsed.TotalSeconds - tt < 60) await Task.Delay(150);
            await WaitStill(8);
            var here = (await service.GetAzAltAsync(precise: true))!.Azimuth;
            Console.WriteLine($"   at az {here:F3}; cable {service.Cable.Cumulative:F2}");
            // model: representative of T within (P-360, P]: up if T <= P and T > here; down if T maps below here
            double Predict(double t) { var u = t > barrier ? t - 360 : t; return u > here ? 1 : -1; }
            foreach (var t in new[] { barrier - 3, barrier + 3, here + 170, here + 120, here - 100, here + 200 })
            {
                if (aborted) break;
                var tn = ((t % 360) + 360) % 360;
                var d = await Direction(here, tn);
                var shortest = Wrap180(tn - here) > 0 ? 1 : -1;
                Console.WriteLine($"   target {tn:F2} (turn {Wrap180(tn - here):+0.0;-0.0}): observed {(d > 0 ? "UP" : d < 0 ? "DOWN" : "none")}; window model says {(Predict(tn) > 0 ? "UP" : "DOWN")}; shortest says {(shortest > 0 ? "UP" : "DOWN")}");
            }
            // home
            await service.GotoAzAltAsync(startAz, alt0, precise: true);
            await Task.Delay(500);
            var th = clock.Elapsed.TotalSeconds;
            while (await service.IsGotoInProgressAsync() == true && !aborted && clock.Elapsed.TotalSeconds - th < 60) await Task.Delay(150);
            await WaitStill(8);
            Console.WriteLine($"   back at {await Offset():F3} from the start");
            break;
        }

        case "goto-big":
        {
            service.Cable.Options.LimitDegrees = 200;
            var up = await service.GotoAzAltGuardedAsync((startAz + 90) % 360, alt0);
            Console.WriteLine($"guarded goto +90: {up.Success} {up.Message}; cable {service.Cable.Cumulative:F2} deg");
            if (up.Success)
            {
                var back = await service.GotoAzAltGuardedAsync(startAz, alt0);
                Console.WriteLine($"guarded goto back: {back.Success} {back.Message}; cable {service.Cable.Cumulative:F2} deg");
            }
            break;
        }

        default:
            Console.WriteLine("stages: baseline | small-goto | slew-rates | goto-direction | goto-big");
            return 1;
    }
}
finally
{
    await StopEverything();
    var end = await service.GetAzAltAsync(precise: true);
    Console.WriteLine($"Finished; mount at az {end?.Azimuth:F4} alt {end?.Altitude:F4} (started az {startAz:F4} alt {alt0:F4}); cable count {service.Cable.Cumulative:F2}");
    await service.DeselectPortAsync();
}
return 0;
