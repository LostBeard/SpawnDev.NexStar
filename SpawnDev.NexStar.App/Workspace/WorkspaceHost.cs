using Microsoft.AspNetCore.Components;
using SpawnDev.GameUI;
using SpawnDev.NexStar.App.Eyepiece;
using SpawnDev.NexStar.Eyepiece;
using SpawnDev.NexStar.Vision;
using SpawnDev.NexStar.Vision.Gpu;
using SpawnDev.NexStar.App.Services;
using SpawnDev.NexStar.Sky;
using SkyObject = SpawnDev.NexStar.Sky.SkyObject;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace SpawnDev.NexStar.App.Workspace;

/// <summary>
/// Owns the workspace's WebGPU device, canvas and render loop. One canvas, one frame loop: the sky is drawn first (stars, grid, constellations, deep-sky
/// markers by the GPU), then GameUI draws the labels and the HUD over it. Handles are manual in SpawnJS, so everything it creates is disposed in <see cref="Dispose"/>.
/// </summary>
public sealed class WorkspaceHost : IDisposable
{
    private readonly SpawnJSRuntime _js;
    private readonly NexStarService _mount;
    private readonly LocationService _location;
    private readonly GameUIService _ui = new();

    private GPUDevice? _device;
    private GPUQueue? _queue;
    private HTMLCanvasElement? _canvas;
    private GPUCanvasContext? _context;
    private Window? _window;
    private ActionCallback<double>? _frameCallback;

    private WorkspaceUi? _hud;
    private readonly SkyView _sky = new();
    private SkyData? _skyData;
    private SkyRenderer? _skyRenderer;
    private SkyOverlay? _overlay;
    private SkyInteraction? _interaction;

    private TelescopeController? _scope;
    private CancellationTokenSource? _runCts;
    private ActionCallback<Event>? _releaseCallback;
    private List<SkyObject> _solar = new();
    private double _sinceSolar = 10;
    private SkyObject? _selected;
    private string? _cardNote;
    private bool _gotoBusy;
    private Storage? _storage;
    private bool _demo;
    private EyepieceViewer? _eyepiece;
    private StarDetector? _detector;
    private CatalogCone? _cone;
    private bool _solving;
    private PlateSolution? _solution;
    private DateTime _solutionUtc;
    private (double Az, double Alt)? _solutionMount;
    private const string ScaleKey = "nexstar.eyepiece.scale";
    private ViewSettings _settings = new();
    private TimeSpan _timeShift;
    private const string ViewKey = "nexstar.view";

    private const string PointingKey = "nexstar.pointing";
    private const string CableKey = "nexstar.cable";

    private bool _running;
    private double _lastTimestamp;
    private int _frame;
    private int _cssWidth, _cssHeight;
    private double _pixelRatio = 1;

    /// <summary>What the host is doing, for the page to show when WebGPU is missing.</summary>
    public string Status { get; private set; } = "starting";

    /// <summary>A link's view, site and time, applied at start (null: the normal start: looking south, here, now).</summary>
    public WorkspaceDeeplink? Deeplink { get; set; }

    private bool _siteFixed;
    private DateTime? _timeFixed;

    /// <summary>The chart: tests and the page can steer it (look somewhere, set the time).</summary>
    public SkyView Sky => _sky;

    public WorkspaceHost(SpawnJSRuntime js, NexStarService mount, LocationService location)
    {
        _js = js;
        _mount = mount;
        _location = location;
    }

    public async Task StartAsync(ElementReference canvasRef)
    {
        try
        {
            using var gpu = _js.Get<GPU?>("navigator.gpu");
            if (gpu == null) { Status = "WebGPU is not available in this browser"; return; }
            using var adapter = await gpu.RequestAdapter();
            if (adapter == null) { Status = "No WebGPU adapter found"; return; }
            _device = await adapter.RequestDevice();
            _queue = _device.Queue;

            _canvas = canvasRef.As<HTMLCanvasElement>();
            _context = _canvas.GetContext<GPUCanvasContext>("webgpu");
            var format = gpu.GetPreferredCanvasFormat();
            _context.Configure(new GPUCanvasConfiguration { Device = _device, Format = format, AlphaMode = "opaque" });

            _window = new Window();
            MeasureViewport();

            UITheme.Current = NexStarThemes.NightRed;
            _ui.Init(_device, _queue, format, canvasRef, _cssWidth, _cssHeight);
            _ui.Screens.DimBackground = false;
            _hud = new WorkspaceUi(_mount, _location);
            _hud.ConnectRequested += useUsb => _ = ConnectAsync(useUsb);
            _hud.DisconnectRequested += () => _ = _mount.DeselectPortAsync();
            StartTelescope();

            // the catalogs: fetched in the browser and uploaded straight to the GPU
            _skyData = await SkyData.LoadAsync(_js, _device, _queue, "data");
            _skyRenderer = new SkyRenderer(_device, _queue, format, _skyData) { NightMode = true };
            _overlay = new SkyOverlay(_sky, _skyData) { NightMode = true };
            _interaction = new SkyInteraction(_sky, _hud.Root);
            _interaction.Tapped += OnTapped;
            _sky.LookAt(180, 35);
            _sky.FovDegrees = 100;
            ApplySettings();
            ApplyDeeplink();

            // the overlay first, so every panel draws over it
            var screen = new UIElement();
            screen.AddChild(_overlay);
            screen.AddChild(_hud.Root);
            _ui.Screens.Register("workspace", screen);
            _ui.Screens.SetScreen("workspace");
            ApplyViewport();

            Console.WriteLine($"[Workspace] ready: {_skyData.StarCount} stars, {_skyData.DeepSkyCount} deep-sky objects, canvas {_cssWidth}x{_cssHeight} css, ratio {_pixelRatio:F2}");
            Status = "running";
            _frameCallback = new ActionCallback<double>(OnFrame);
            _running = true;
            _window.RequestAnimationFrame(_frameCallback);

            // where the observer is: the telescope's stored site, or this device's position
            _ = _location.RequestBrowserLocationAsync();
        }
        catch (Exception ex)
        {
            Status = $"Could not start: {ex.Message}";
            Console.WriteLine($"[Workspace] {ex}");
        }
    }

    private void StartTelescope()
    {
        _scope = new TelescopeController(_mount);
        try { _storage = _window?.LocalStorage; } catch { _storage = null; }

        _settings = ViewSettings.FromJson(ReadStored(ViewKey));
        _hud!.View.Load(_settings);
        _hud.View.Changed += s => { _settings = s; ApplySettings(); WriteStored(ViewKey, s.ToJson()); };
        _hud.View.LocateClicked += () => _ = _location.RequestBrowserLocationAsync();
        _hud.Time.Shifted += step => _timeShift += step;
        _hud.Time.NowClicked += () => { _timeShift = TimeSpan.Zero; _timeFixed = null; };

        // what survives a reload: the pointing offsets, and where the cable was (the guard re-checks it against the clock on the first reading)
        _scope.SetPointing(PointingModel.FromJson(ReadStored(PointingKey)));
        if (CableState.FromJson(ReadStored(CableKey)) is { } saved) _mount.Cable.Restore(saved);
        _mount.Cable.StateChanged += SaveCable;

        var hud = _hud!;
        hud.Dock.PadPressed += (direction, rate) => _ = _scope.BeginPadAsync(direction, rate);
        hud.Dock.PadReleased += () => _ = _scope.EndPadAsync();
        hud.Dock.StopClicked += () => _ = _scope.StopAsync();
        hud.Dock.TrackingChanged += on => _ = _mount.SetTrackingModeAsync(on ? TrackingMode.AltAz : TrackingMode.Off);
        hud.Dock.UnwindClicked += () => _ = UnwindAsync();
        hud.Dock.CableCenteredClicked += CableCentered;
        hud.Card.GotoClicked += () => _ = GotoSelectedAsync();
        hud.Card.CenterClicked += () => { if (_selected is { } o) _sky.LookAt(o.Azimuth, o.Altitude); };
        hud.Card.AlignClicked += () => _ = AlignSelectedAsync();
        hud.Card.CloseClicked += () => { _selected = null; _cardNote = null; };

        // a held button must never outlive the page's attention: losing focus or leaving counts as letting go
        _releaseCallback = new ActionCallback<Event>(_ => ReleaseHolds());
        _window!.AddEventListener("blur", _releaseCallback);
        _window.AddEventListener("pagehide", _releaseCallback);

        _eyepiece = new EyepieceViewer(_device!, _queue!, new PairingStore(_storage, "nexstar.eyepiece.pairing"), _window!.Location.Origin);
        hud.Eyepiece.PairClicked += () => _ = _eyepiece.PairAsync();
        hud.Eyepiece.UnpairClicked += () => _ = _eyepiece.UnpairAsync();
        hud.Eyepiece.CopyLinkClicked += () => _ = CopyLinkAsync();
        hud.Eyepiece.SolveClicked += () => _ = SolveEyepieceAsync();
        hud.Eyepiece.AlignSolutionClicked += () => _ = AlignOnSolutionAsync();
        _ = _eyepiece.StartSavedAsync();

        _runCts = new CancellationTokenSource();
        _ = _scope.RunAsync(_runCts.Token);

        if (Deeplink?.Simulate == true) _ = AttachSimulatorAsync();
    }

    /// <summary>A simulated telescope at real speeds, for trying the workspace with nothing attached. It starts unaligned and facing its own zero, like a real one.</summary>
    private async Task AttachSimulatorAsync()
    {
        _demo = true;
        _hud!.Demo = true;
        var simulator = new SpawnDev.NexStar.Simulation.SimulatedMount(scale: 1, startAzimuth: 20, startAltitude: 30);
        if (!await _mount.AttachTransportAsync(simulator)) Console.WriteLine("[Workspace] the simulated telescope did not connect");
    }

    /// <summary>
    /// Plate-solves the phone's picture: finds its stars on the GPU, picks the catalog stars around where the telescope is thought to point (also on the
    /// GPU), and solves. The prior pointing is the telescope's own when it has a frame, else the phone's compass and tilt.
    /// </summary>
    private async Task SolveEyepieceAsync()
    {
        if (_solving || _eyepiece == null || _skyData?.Stars == null || _device == null || _queue == null) return;
        var video = _eyepiece.Video;
        if (video?.View == null || video.Width <= 0) { _hud!.Eyepiece.SetSolveText("There is no picture from the phone yet.", false); return; }
        if (!ChartIsNow(out var note)) { _hud!.Eyepiece.SetSolveText(note, false); return; }

        (double Az, double Alt)? prior; double uncertainty; string priorName;
        if (_scope?.HasFrame == true && _scope.TruePointing() is { } scopeAt) { prior = scopeAt; uncertainty = 2.0; priorName = "the telescope's position"; }
        else if (_eyepiece.ChartPointing is { } phoneAt) { prior = phoneAt; uncertainty = 8.0; priorName = "the phone's compass"; }
        else { _hud!.Eyepiece.SetSolveText("Nothing says where the telescope points yet: align it on a bright star first, or point the phone's compass (the phone needs a compass and a known site).", false); return; }

        _solving = true;
        _solution = null;
        _hud!.Eyepiece.SetSolveText($"Solving from {priorName}...", false);
        try
        {
            _detector ??= new StarDetector(_device, _queue);
            _cone ??= new CatalogCone(_device, _queue);
            int w = video.Width, h = video.Height;
            var capturedUtc = DateTime.UtcNow;
            var capturedMount = _mount.CurrentAzAlt is { } m ? (m.Azimuth, m.Altitude) : ((double, double)?)null;
            var detections = await _detector.DetectAsync(video.View, w, h, 64);
            if (detections.Count < 5) { _hud.Eyepiece.SetSolveText($"Only {detections.Count} stars were found in the picture: it needs at least five. Is the telescope focused, and the exposure long enough?", false); return; }

            // the plate scale: what was solved last time (to within 15%), else anything a phone through an eyepiece could be
            double? saved = double.TryParse(ReadStored(ScaleKey), System.Globalization.CultureInfo.InvariantCulture, out var sc) ? sc : null;
            var options = saved is double s0 ? new PlateSolveOptions { MinArcsecPerPixel = s0 * 0.85, MaxArcsecPerPixel = s0 * 1.15 } : new PlateSolveOptions { MinArcsecPerPixel = 0.8, MaxArcsecPerPixel = 25 };
            double fieldRadius = 0.5 * Math.Sqrt((double)w * w + (double)h * h) * options.MaxArcsecPerPixel / 3600.0;
            var priorDirection = _sky.ToJ2000(SkyView.FromAzAlt(prior.Value.Az, prior.Value.Alt));
            float magnitudeLimit = options.MaxArcsecPerPixel > 12 ? 9.5f : 11.5f;
            var stars = await _cone.SearchAsync(_skyData.Stars, _skyData.StarCount, priorDirection, uncertainty + fieldRadius * 1.5 + 0.5, magnitudeLimit);
            if (stars.Count < 5) { _hud.Eyepiece.SetSolveText("The catalog has too few stars around there for this picture.", false); return; }

            var progress = new Progress<(int Done, int Total)>(p => _hud.Eyepiece.SetSolveText($"Solving from {priorName}: patch {p.Done + 1} of {p.Total}, {detections.Count} stars in the picture...", false));
            var solution = await PlateSolver.SolveSearchAsync(detections, stars, priorDirection, uncertainty, w, h, options, progress);
            if (solution == null)
            {
                _hud.Eyepiece.SetSolveText($"No solution: {detections.Count} stars in the picture, {stars.Count} catalog stars near {priorName}. The catalog here is Hipparcos (to about magnitude 11): a narrow eyepiece field may hold too few catalog stars to solve.", false);
                return;
            }
            _solution = solution;
            _solutionUtc = capturedUtc;
            _solutionMount = capturedMount;
            WriteStored(ScaleKey, solution.ArcsecPerPixel.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            var ra = AstronomyMath.FormatHMS(solution.RaDegrees / 15.0);
            var dec = AstronomyMath.FormatDMS(solution.DecDegrees);
            _hud.Eyepiece.SetSolveText($"Solved: RA {ra}  Dec {dec}\n{solution.ArcsecPerPixel:F2} arcsec/px  roll {solution.RollDegrees:F1}{(solution.Mirrored ? "  mirrored" : "")}  {solution.Matches} stars, rms {solution.RmsPixels:F2} px", !_mount.IsAligned && _mount.ComsEnabled);
        }
        catch (Exception ex)
        {
            _hud.Eyepiece.SetSolveText("The solve failed: " + ex.Message, false);
            Console.WriteLine($"[Eyepiece] solve: {ex}");
        }
        finally { _solving = false; }
    }

    /// <summary>Aligns the unaligned telescope on what the picture was solved to be: where it truly pointed when the picture was taken, against where the mount says it points.</summary>
    private async Task AlignOnSolutionAsync()
    {
        if (_solution is not { } s || _scope == null) return;
        if ((DateTime.UtcNow - _solutionUtc).TotalSeconds > 60) { _hud!.Eyepiece.SetSolveText("That solution is too old: solve again.", false); return; }
        if (_mount.CurrentAzAlt is { } now && _solutionMount is { } then && (Math.Abs(now.Azimuth - then.Az) > 0.05 || Math.Abs(now.Altitude - then.Alt) > 0.05))
        { _hud!.Eyepiece.SetSolveText("The telescope has moved since that picture: solve again.", false); return; }
        // where that sky direction was in the horizon frame at the moment of the picture
        var at = new SkyView { Latitude = _sky.Latitude, Longitude = _sky.Longitude, Utc = _solutionUtc, WidthPx = 100, HeightPx = 100 };
        at.Update();
        var (az, alt) = SkyView.ToAzAlt(at.ToHorizontal(s.Center));
        if (await _scope.AlignHereAsync(az, alt))
        {
            WriteStored(PointingKey, _scope.Pointing.ToJson());
            _hud!.Eyepiece.SetSolveText($"Aligned: the telescope was pointing at Az {az:F2}  Alt {alt:F2}. Go to now uses this.", false);
        }
        else
        {
            _hud!.Eyepiece.SetSolveText(_scope.Message ?? "Could not align.", false);
        }
    }

    private async Task CopyLinkAsync()
    {
        if (_eyepiece?.Link is not { } link) return;
        try
        {
            using var navigator = _js.Get<Navigator>("navigator");
            await navigator.Clipboard.WriteText(link);
            _scope?.SetMessage("The pairing link was copied.");
        }
        catch (Exception ex)
        {
            _scope?.SetMessage("Could not copy: " + ex.Message);
        }
    }

    private string? ReadStored(string key)
    {
        try { return _storage?.GetItem(key); } catch { return null; }
    }

    private void WriteStored(string key, string value)
    {
        try { _storage?.SetItem(key, value); } catch { }
    }

    private void SaveCable() => WriteStored(CableKey, _mount.Cable.State.ToJson());

    private void ReleaseHolds()
    {
        _hud?.Dock.ReleaseAll();
        if (_scope != null) _ = _scope.EndPadAsync();
    }

    private void OnTapped(System.Numerics.Vector2 position)
    {
        if (_skyData == null) return;
        _selected = SkyPicker.Pick(_sky, position.X, position.Y, 20, _skyData.BrightStars, _skyData.StarNames, _skyData.DeepSky, _skyData.DeepSkyNames, _solar);
        _cardNote = null;
    }

    /// <summary>The picked object's position is the chart's; the telescope needs it for now. If the chart shows another time, say so instead of sending it wrong.</summary>
    private bool ChartIsNow(out string note)
    {
        var late = Math.Abs((_sky.Utc - DateTime.UtcNow).TotalSeconds) > 90;
        note = late ? "The chart is not showing the current time: positions would be wrong for the telescope." : "";
        return !late;
    }

    private async Task GotoSelectedAsync()
    {
        if (_selected is not { } o || _scope == null || _gotoBusy) return;
        if (!ChartIsNow(out var note)) { _cardNote = note; return; }
        _gotoBusy = true;
        _cardNote = $"Going to {o.Name}...";
        try
        {
            var result = await _scope.GotoAsync(o.Azimuth, o.Altitude, _sky.Latitude, _sky.Longitude, DateTime.UtcNow);
            _cardNote = result.Success ? $"At {o.Name}." : result.Message;
        }
        finally { _gotoBusy = false; }
    }

    private async Task AlignSelectedAsync()
    {
        if (_selected is not { } o || _scope == null) return;
        if (!ChartIsNow(out var note)) { _cardNote = note; return; }
        if (await _scope.AlignHereAsync(o.Azimuth, o.Altitude))
        {
            WriteStored(PointingKey, _scope.Pointing.ToJson());
            _cardNote = $"Aligned on {o.Name}. Go to now uses this.";
        }
        else
        {
            _cardNote = _scope.Message;
        }
    }

    private async Task UnwindAsync()
    {
        var result = await _mount.UnwindCableAsync();
        _scope!.SetMessage(result.Success ? "The cable is back at neutral." : result.Message);
    }

    private void CableCentered()
    {
        var p = _mount.CurrentAzAlt;
        if (p == null) { _scope?.SetMessage("The mount's position is not known yet."); return; }
        _mount.Cable.SetNeutral(p.Azimuth, DateTime.UtcNow);
        _scope?.SetMessage("The cable is now counted from here.");
    }

    private DockState BuildDockState()
    {
        var mp = _mount.CurrentAzAlt;
        return new DockState
        {
            Connected = _mount.ComsEnabled,
            Demo = _demo,
            Title = $"{_mount.Model.GetDisplayName()} {_mount.Version}",
            Aligned = _mount.IsAligned,
            HasFrame = _scope?.HasFrame ?? false,
            TruePointing = _scope?.HasFrame == true ? _scope.TruePointing() : null,
            MountPointing = mp != null ? (mp.Azimuth, mp.Altitude) : null,
            Tracking = _mount.CurrentTrackingMode != TrackingMode.Off,
            Moving = _mount.GuardedMotionActive,
            Message = _scope?.Message,
            Cable = _mount.Cable,
        };
    }

    /// <summary>Puts the saved or chosen settings into the renderer, the overlay and the theme.</summary>
    private void ApplySettings()
    {
        if (_skyRenderer == null || _overlay == null) return;
        _skyRenderer.Layers = _settings.Layers;
        _skyRenderer.StarSize = _settings.StarSize;
        _skyRenderer.NightMode = _settings.Night;
        _overlay.NightMode = _settings.Night;
        _overlay.ShowStarNames = _settings.StarNames;
        _overlay.ShowConstellationNames = _settings.ConstellationNames;
        _overlay.ShowSolarSystem = _settings.SolarSystem;
        UITheme.Current = _settings.Night ? NexStarThemes.NightRed : NexStarThemes.Day;
    }

    private void ApplyDeeplink()
    {
        var d = Deeplink;
        if (d == null) return;
        if (d.Latitude is double lat && d.Longitude is double lon) { _sky.Latitude = lat; _sky.Longitude = lon; _siteFixed = true; }
        _timeFixed = d.Utc;
        if (d.Utc is DateTime fixedTime) _timeShift = fixedTime - DateTime.UtcNow;
        if (d.Fov is double fov) _sky.FovDegrees = Math.Clamp(fov, SkyView.MinFov, SkyView.MaxFov);
        if (d.Azimuth is double az || d.Altitude is double) _sky.LookAt(d.Azimuth ?? _sky.CenterAzimuth, d.Altitude ?? _sky.CenterAltitude);
        if (d.Night is bool night) { _skyRenderer!.NightMode = night; _overlay!.NightMode = night; UITheme.Current = night ? NexStarThemes.NightRed : NexStarThemes.Day; }
        if (d.Layers is SkyLayers layers) _skyRenderer!.Layers = layers;
    }

    private async Task ConnectAsync(bool useUsb)
    {
        // Straight from the click: the browser's device picker needs the user's gesture, and a reconnect to an already-granted device needs none.
        if (await _mount.TryReconnectAsync(useUsb)) return;
        await _mount.SelectPortAsync(useUsb);
    }

    /// <summary>Sizes the canvas to the window at the device's pixel ratio, so the HUD text and the sky are sharp.</summary>
    private void MeasureViewport()
    {
        if (_window == null || _canvas == null) return;
        _pixelRatio = _window.DevicePixelRatio;
        _cssWidth = Math.Max(1, _window.InnerWidth);
        _cssHeight = Math.Max(1, _window.InnerHeight);
        _canvas.Width = (int)Math.Round(_cssWidth * _pixelRatio);
        _canvas.Height = (int)Math.Round(_cssHeight * _pixelRatio);
    }

    private void ApplyViewport()
    {
        _ui.SetViewport(_cssWidth, _cssHeight);
        _hud?.Layout(_cssWidth, _cssHeight);
        _sky.WidthPx = _cssWidth;
        _sky.HeightPx = _cssHeight;
    }

    private void OnFrame(double timestamp)
    {
        if (!_running || _device == null || _queue == null || _context == null || _hud == null || _window == null || _skyRenderer == null) return;
        var dt = _lastTimestamp > 0 ? (float)((timestamp - _lastTimestamp) / 1000.0) : 1f / 60f;
        _lastTimestamp = timestamp;
        if (dt > 0.1f) dt = 0.1f;

        try
        {
            // the window can be resized any time: look every few frames, not every frame (each look is a call into JS)
            if (++_frame % 8 == 0)
            {
                var w = _window.InnerWidth;
                var h = _window.InnerHeight;
                if (w != _cssWidth || h != _cssHeight || Math.Abs(_window.DevicePixelRatio - _pixelRatio) > 1e-6)
                {
                    MeasureViewport();
                    ApplyViewport();
                }
            }

            // where and when: the observer's site and now
            if (!_siteFixed && _location.Location is { } site)
            {
                _sky.Latitude = site.Latitude;
                _sky.Longitude = site.Longitude;
            }
            _sky.Utc = DateTime.UtcNow + _timeShift;
            _sky.Update();

            _ui.Update(dt);
            _interaction?.Update(_ui.Input);
            // the Moon and planets move and the sky turns: keep the picked object where it is now
            _sinceSolar += dt;
            if (_sinceSolar >= 1.0)
            {
                _sinceSolar = 0;
                _solar = SkyPicker.SolarSystem(_sky);
                // the compass of the phone reads magnetic north: the model turns it into true north for this site
                if (_eyepiece != null && (_siteFixed || _location.Location != null))
                    _eyepiece.Declination = WorldMagneticModel.Default.Declination(_sky.Latitude, _sky.Longitude, 0, DateTime.UtcNow);
                _hud.View.ShowSite(_location.Location is { } siteHere
                    ? $"Site {siteHere.Latitude:F3}, {siteHere.Longitude:F3} ({(_location.IsBrowserLocation ? "this device" : "telescope")})"
                    : "Site unknown: using a default");
                if (_selected != null) _selected = SkyPicker.Refresh(_selected, _sky, _solar);
            }
            if (_scope?.PadHeld != null) _scope.Heartbeat();
            _overlay!.Selected = _selected;
            _overlay.PhonePointing = _eyepiece?.ChartPointing;
            _overlay.TelescopePointing = _scope?.HasFrame == true ? _scope.TruePointing() : null;
            _hud.Card.Show(_selected, _mount.ComsEnabled, _mount.IsAligned, _cardNote);
            _eyepiece?.Update();
            _hud.Eyepiece.UpdateVideo(_eyepiece);
            _hud.Refresh(dt, _sky, _location.Location != null, BuildDockState(), _eyepiece);

            using var texture = _context.GetCurrentTexture();
            using var view = texture.CreateView();
            using var encoder = _device.CreateCommandEncoder();

            _skyRenderer.Render(encoder, view, _sky, _pixelRatio);

            _ui.BeginRender(_cssWidth, _cssHeight);
            _ui.Screens.Draw(_ui.Renderer);
            _ui.EndRender(encoder, view);

            using var commands = encoder.Finish();
            _queue.Submit(new[] { commands });
        }
        catch (Exception ex)
        {
            _running = false;
            Status = $"Render error: {ex.Message}";
            Console.WriteLine($"[Workspace] {ex}");
            return;
        }
        _window.RequestAnimationFrame(_frameCallback!);
    }

    public void Dispose()
    {
        _running = false;
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = null;
        _detector?.Dispose(); _detector = null;
        _cone?.Dispose(); _cone = null;
        if (_eyepiece != null) { _ = _eyepiece.DisposeAsync(); _eyepiece = null; }
        _mount.Cable.StateChanged -= SaveCable;
        if (_scope != null) _ = _scope.StopAsync();
        if (_releaseCallback != null && _window != null)
        {
            _window.RemoveEventListener("blur", _releaseCallback);
            _window.RemoveEventListener("pagehide", _releaseCallback);
        }
        _releaseCallback?.Dispose();
        _releaseCallback = null;
        _frameCallback?.Dispose();
        _frameCallback = null;
        _skyRenderer?.Dispose();
        _skyRenderer = null;
        _skyData?.Dispose();
        _skyData = null;
        _ui.Dispose();
        _context?.Dispose();
        _context = null;
        _canvas?.Dispose();
        _canvas = null;
        _queue?.Dispose();
        _queue = null;
        _device?.Dispose();
        _device = null;
        _window?.Dispose();
        _window = null;
    }
}
