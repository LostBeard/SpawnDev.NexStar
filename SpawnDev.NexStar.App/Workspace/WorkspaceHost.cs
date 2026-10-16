using Microsoft.AspNetCore.Components;
using SpawnDev.GameUI;
using SpawnDev.NexStar.App.Services;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace SpawnDev.NexStar.App.Workspace;

/// <summary>
/// Owns the workspace's WebGPU device, canvas and render loop. One canvas, one frame loop: the sky is drawn first, then GameUI draws the HUD over it.
/// Handles are manual in SpawnJS, so everything it creates is disposed in <see cref="Dispose"/>.
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

    private bool _running;
    private double _lastTimestamp;
    private int _frame;
    private int _cssWidth, _cssHeight;
    private double _pixelRatio = 1;

    /// <summary>What the host is doing, for the page to show when WebGPU is missing.</summary>
    public string Status { get; private set; } = "starting";

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
            _hud.Layout(_cssWidth, _cssHeight);
            _ui.Screens.Register("hud", _hud.Root);
            _ui.Screens.SetScreen("hud");

            Console.WriteLine($"[Workspace] WebGPU ready, canvas {_cssWidth}x{_cssHeight} css, ratio {_pixelRatio:F2}");
            Status = "running";
            _frameCallback = new ActionCallback<double>(OnFrame);
            _running = true;
            _window.RequestAnimationFrame(_frameCallback);
        }
        catch (Exception ex)
        {
            Status = $"Could not start: {ex.Message}";
            Console.WriteLine($"[Workspace] {ex}");
        }
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

    private void OnFrame(double timestamp)
    {
        if (!_running || _device == null || _queue == null || _context == null || _hud == null || _window == null) return;
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
                    _ui.SetViewport(_cssWidth, _cssHeight);
                    _hud.Layout(_cssWidth, _cssHeight);
                }
            }

            _ui.Update(dt);
            _hud.Refresh(dt);

            using var texture = _context.GetCurrentTexture();
            using var view = texture.CreateView();
            using var encoder = _device.CreateCommandEncoder();
            using (var pass = encoder.BeginRenderPass(new GPURenderPassDescriptor
            {
                ColorAttachments = new[]
                {
                    new GPURenderPassColorAttachment
                    {
                        View = view,
                        LoadOp = GPULoadOp.Clear,
                        StoreOp = GPUStoreOp.Store,
                        ClearValue = new GPUColorDict { R = 0.0, G = 0.0, B = 0.0, A = 1.0 },
                    }
                }
            }))
            {
                pass.End();
            }

            // (the sky is drawn here, before the HUD)

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
        _frameCallback?.Dispose();
        _frameCallback = null;
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
