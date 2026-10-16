using SpawnDev.NexStar.Eyepiece;
using SpawnDev.RTC;
using SpawnDev.RTC.Browser;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace SpawnDev.NexStar.App.Eyepiece;

/// <summary>
/// The phone on the eyepiece: its rear camera, its orientation sensors and the link to the controlling instance. Started from a button (the browser
/// wants a tap before it gives out a camera, the iPhone's sensors or a wake lock), it keeps the screen awake, streams the camera as video and sends the
/// orientation about twenty times a second and the camera and battery every few seconds.
/// </summary>
public sealed class EyepiecePhone : IAsyncDisposable
{
    private readonly SpawnJSRuntime _js;
    private readonly Window _window;
    private EyepieceSession? _session;
    private IRTCMediaStream? _camera;
    private WakeLockSentinel? _wakeLock;
    private CancellationTokenSource? _cts;
    private Task? _sender;
    private ActionCallback<DeviceOrientationEvent>? _onAbsolute, _onRelative;
    private bool _listening;
    private double _alpha, _beta, _gamma;
    private double? _heading;
    private bool _absolute, _haveSample;
    private BatteryManager? _battery;

    public EyepieceLinkState State => _session?.State ?? EyepieceLinkState.Stopped;
    public string? Detail => _session?.Detail;

    /// <summary>What the camera is doing, for the page.</summary>
    public string CameraText { get; private set; } = "";

    private string _cameraLabel = "camera";

    /// <summary>The latest reading of the phone's own sensors, for the page (so a person can see that they work).</summary>
    public CameraPose? Pose { get; private set; }
    public bool CompassAbsolute => _absolute;

    public event Action? Changed;

    public EyepiecePhone(SpawnJSRuntime js)
    {
        _js = js;
        _window = new Window();
    }

    /// <summary>Starts everything. Returns why not, or null when the link is being made.</summary>
    public async Task<string?> StartAsync(PairingInfo pairing, HTMLVideoElement? preview)
    {
        if (_session != null) return null;
        try
        {
            _camera = await RTCMediaDevices.GetUserMedia(new SpawnDev.RTC.MediaStreamConstraints
            {
                Video = new SpawnDev.RTC.MediaTrackConstraints { FacingMode = "environment", Width = 1280, Height = 720, FrameRate = 30 },
            });
        }
        catch (Exception ex)
        {
            return "The camera is not available: " + ex.Message;
        }

        if (preview != null && _camera is BrowserRTCMediaStream browserStream)
        {
            preview.Muted = true;
            preview.PlaysInline = true;
            preview.SrcObject = browserStream.NativeStream;
            try { await preview.Play(); } catch { }
        }
        var track = _camera.GetVideoTracks().FirstOrDefault();
        if (track != null)
        {
            var s = track.GetSettings();
            _cameraLabel = string.IsNullOrWhiteSpace(track.Label) ? "camera" : track.Label;
            CameraText = $"{_cameraLabel}  {s.Width}x{s.Height}";
        }

        await StartSensorsAsync();
        try
        {
            using var navigator = _js.Get<Navigator>("navigator");
            if (navigator.WakeLock is { } wake) _wakeLock = await wake.Request("screen");
            _battery = await navigator.GetBattery();
        }
        catch (Exception ex) { Console.WriteLine($"[Eyepiece] wake lock or battery: {ex.Message}"); }

        _session = new EyepieceSession(PairingRole.Phone, pairing);
        _session.StateChanged += _ => Changed?.Invoke();
        _session.MessageReceived += OnMessage;
        await _session.StartAsync(_camera);

        _cts = new CancellationTokenSource();
        _sender = Task.Run(() => SendLoopAsync(_cts.Token));
        Changed?.Invoke();
        return null;
    }

    private async Task StartSensorsAsync()
    {
        if (_listening) return;
        try
        {
            // an iPhone makes the page ask first (from a tap); other phones just start sending
            if (DeviceOrientationEvent.RequestPermissionSupported)
            {
                var state = await DeviceOrientationEvent.RequestPermission();
                if (state != "granted") return;
            }
        }
        catch { }
        _onAbsolute = new ActionCallback<DeviceOrientationEvent>(e => Sample(e, absolute: true));
        _onRelative = new ActionCallback<DeviceOrientationEvent>(e => Sample(e, absolute: false));
        _window.AddEventListener("deviceorientationabsolute", _onAbsolute);
        _window.AddEventListener("deviceorientation", _onRelative);
        _listening = true;
    }

    private DateTime _lastAbsolute = DateTime.MinValue;

    private void Sample(DeviceOrientationEvent e, bool absolute)
    {
        if (e.Beta is not double beta || e.Gamma is not double gamma) return;
        if (absolute)
        {
            if (e.Alpha is not double a) return;
            _alpha = a; _beta = beta; _gamma = gamma; _absolute = true; _heading = null; _lastAbsolute = DateTime.UtcNow;
            _haveSample = true;
        }
        else
        {
            // the plain event is the fallback: an iPhone gives a compass heading with it, other phones a relative alpha
            if ((DateTime.UtcNow - _lastAbsolute).TotalSeconds < 1) return;
            _beta = beta; _gamma = gamma;
            if (e.WebkitCompassHeading is double heading) { _alpha = (360.0 - heading) % 360.0; _absolute = true; _heading = heading; }
            else if (e.Alpha is double a) { _alpha = a; _absolute = e.Absolute; _heading = null; }
            else return;
            _haveSample = true;
        }
    }

    private void OnMessage(EyepieceMessage message)
    {
        if (message is EyepieceMessage.Command c) Console.WriteLine($"[Eyepiece] command {c.Name} {c.Value} (not supported yet)");
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        int tick = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(50, ct);
                if (_haveSample)
                {
                    Pose = CameraPose.FromDeviceOrientation(_alpha, _beta, _gamma);
                    if (_session?.State == EyepieceLinkState.Open)
                        _session.Send(new EyepieceMessage.Orientation(Math.Round(DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalMilliseconds, 0), _alpha, _beta, _gamma, _absolute, _heading));
                }
                if (++tick % 100 == 0 && _session?.State == EyepieceLinkState.Open)
                {
                    var settings = _camera?.GetVideoTracks().FirstOrDefault()?.GetSettings();
                    double? level = null;
                    try { level = _battery?.Level; } catch { }
                    _session.Send(new EyepieceMessage.Status(_cameraLabel, settings?.Width ?? 0, settings?.Height ?? 0, settings?.FrameRate, level));
                }
                if (tick % 4 == 0) Changed?.Invoke();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.WriteLine($"[Eyepiece] send loop: {ex.Message}"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_sender != null) { try { await _sender; } catch { } }
        _cts?.Dispose();
        if (_listening)
        {
            try { _window.RemoveEventListener("deviceorientationabsolute", _onAbsolute!); _window.RemoveEventListener("deviceorientation", _onRelative!); } catch { }
            _onAbsolute?.Dispose(); _onRelative?.Dispose();
            _listening = false;
        }
        if (_session != null) { try { await _session.DisposeAsync(); } catch { } _session = null; }
        if (_camera != null) { foreach (var t in _camera.GetTracks()) { try { t.Stop(); } catch { } } _camera.Dispose(); _camera = null; }
        if (_wakeLock != null) { try { await _wakeLock.Release(); } catch { } _wakeLock = null; }
        _window.Dispose();
    }
}
