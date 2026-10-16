using SpawnDev.NexStar.Eyepiece;
using SpawnDev.NexStar.Eyepiece.Qr;
using SpawnDev.RTC;
using SpawnDev.SpawnJS.JSObjects;

namespace SpawnDev.NexStar.App.Eyepiece;

/// <summary>
/// The controlling instance's side of Eyepiece Mode: the pairing (kept between visits), the link to the phone, its latest orientation and status,
/// and the phone's video as a texture. The HUD panel shows what is here and calls <see cref="PairAsync"/> and <see cref="UnpairAsync"/>.
/// </summary>
public sealed class EyepieceViewer : IAsyncDisposable
{
    private readonly GPUDevice _device;
    private readonly GPUQueue _queue;
    private readonly PairingStore _store;
    private readonly string _origin;
    private EyepieceSession? _session;
    private VideoSurface? _video;
    private readonly object _lock = new();
    private bool _disposed;

    public PairingInfo? Pairing { get; private set; }

    /// <summary>The link a phone opens to pair (the app's address plus the pairing), when there is a pairing.</summary>
    public string? Link { get; private set; }

    /// <summary>The QR code of <see cref="Link"/> as modules (true = dark), when there is a pairing.</summary>
    public bool[,]? Qr { get; private set; }

    public EyepieceLinkState State => _session?.State ?? EyepieceLinkState.Stopped;
    public string? Detail => _session?.Detail;

    public EyepieceMessage.Orientation? Orientation { get; private set; }
    public DateTime OrientationAtUtc { get; private set; }
    public EyepieceMessage.Status? Status { get; private set; }

    /// <summary>The phone's picture (null until its first frame).</summary>
    public VideoSurface? Video => _video;

    /// <summary>The magnetic declination at the site (east positive), once the site is known: what turns the phone's compass bearing into a true one.</summary>
    public double? Declination { get; set; }

    /// <summary>The phone's camera pose from its latest orientation, with the compass azimuth as the phone gives it (against magnetic north).</summary>
    public CameraPose? MagneticPose => Orientation is { } o ? CameraPose.FromDeviceOrientation(o.Alpha, o.Beta, o.Gamma) : null;

    /// <summary>True when <see cref="Pose"/> is against true north: the phone has a compass and the site's declination is known.</summary>
    public bool PoseIsTrue => Declination != null && Orientation is { Absolute: true };

    /// <summary>The camera pose with the azimuth against true north when that can be had, else as the phone reports it.</summary>
    public CameraPose? Pose => MagneticPose is { } p ? (Declination is double d && Orientation is { Absolute: true } ? p.WithDeclination(d) : p) : null;

    /// <summary>The phone's pointing for the chart, only while its readings are fresh and its azimuth is against true north.</summary>
    public (double Azimuth, double Altitude)? ChartPointing =>
        PoseIsTrue && Pose is { } p && (DateTime.UtcNow - OrientationAtUtc).TotalSeconds < 2 ? (p.Azimuth, p.Altitude) : null;

    public EyepieceViewer(GPUDevice device, GPUQueue queue, PairingStore store, string origin)
    {
        _device = device;
        _queue = queue;
        _store = store;
        _origin = origin;
    }

    /// <summary>Joins the saved pairing, if there is one (so a phone that opens its page later finds the room waiting).</summary>
    public async Task StartSavedAsync()
    {
        var saved = _store.Load();
        if (saved != null) await StartAsync(saved);
    }

    /// <summary>Makes a new pairing (replacing any), saves it and starts waiting for the phone.</summary>
    public async Task PairAsync()
    {
        await StopAsync();
        var pairing = PairingInfo.Create();
        _store.Save(pairing);
        await StartAsync(pairing);
    }

    /// <summary>Forgets the pairing and closes the link.</summary>
    public async Task UnpairAsync()
    {
        await StopAsync();
        _store.Clear();
        Pairing = null;
        Link = null;
        Qr = null;
    }

    private async Task StartAsync(PairingInfo pairing)
    {
        Pairing = pairing;
        Link = pairing.ToLink(_origin);
        Qr = QREncoder.Encode(Link, QRTables.ECLevel.M);
        var session = new EyepieceSession(PairingRole.Viewer, pairing);
        session.MessageReceived += OnMessage;
        session.TrackReceived += OnTrack;
        session.StateChanged += state =>
        {
            if (state != EyepieceLinkState.Open)
            {
                // the phone went away: its picture and readings are stale
                lock (_lock) { DropVideo(); Orientation = null; Status = null; }
            }
        };
        _session = session;
        try { await session.StartAsync(); }
        catch (Exception ex) { Console.WriteLine($"[Eyepiece] could not join the hub: {ex.Message}"); }
    }

    private void OnMessage(EyepieceMessage message)
    {
        switch (message)
        {
            case EyepieceMessage.Orientation o: Orientation = o; OrientationAtUtc = DateTime.UtcNow; break;
            case EyepieceMessage.Status s: Status = s; break;
        }
    }

    private void OnTrack(IRTCMediaStreamTrack track)
    {
        lock (_lock)
        {
            DropVideo();
            try
            {
                _video = new VideoSurface(_device, _queue, track);
                _ = _video.StartAsync();
            }
            catch (Exception ex) { Console.WriteLine($"[Eyepiece] could not show the video: {ex.Message}"); }
        }
    }

    private void DropVideo()
    {
        _video?.Dispose();
        _video = null;
    }

    /// <summary>Per frame: brings the video texture up to date.</summary>
    public void Update()
    {
        lock (_lock) _video?.Update();
    }

    /// <summary>Asks the phone to do something with its camera.</summary>
    public bool Send(EyepieceMessage message) => _session?.Send(message) ?? false;

    private async Task StopAsync()
    {
        var session = _session;
        _session = null;
        lock (_lock) { DropVideo(); Orientation = null; Status = null; }
        if (session != null) { try { await session.DisposeAsync(); } catch { } }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
    }
}
