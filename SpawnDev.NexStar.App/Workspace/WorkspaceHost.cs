using Microsoft.AspNetCore.Components;
using SpawnDev.GameUI;
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

    private void ApplyDeeplink()
    {
        var d = Deeplink;
        if (d == null) return;
        if (d.Latitude is double lat && d.Longitude is double lon) { _sky.Latitude = lat; _sky.Longitude = lon; _siteFixed = true; }
        _timeFixed = d.Utc;
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
            _sky.Utc = _timeFixed ?? DateTime.UtcNow;
            _sky.Update();

            _ui.Update(dt);
            _interaction?.Update(_ui.Input);
            // the Moon and planets move and the sky turns: keep the picked object where it is now
            _sinceSolar += dt;
            if (_sinceSolar >= 1.0)
            {
                _sinceSolar = 0;
                _solar = SkyPicker.SolarSystem(_sky);
                if (_selected != null) _selected = SkyPicker.Refresh(_selected, _sky, _solar);
            }
            if (_scope?.PadHeld != null) _scope.Heartbeat();
            _overlay!.Selected = _selected;
            _overlay.TelescopePointing = _scope?.HasFrame == true ? _scope.TruePointing() : null;
            _hud.Card.Show(_selected, _mount.ComsEnabled, _mount.IsAligned, _cardNote);
            _hud.Refresh(dt, _sky, _location.Location != null, BuildDockState());

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
