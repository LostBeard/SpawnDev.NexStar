using SpawnDev.GameUI.Elements;
using SpawnDev.RTC;
using SpawnDev.RTC.Browser;
using SpawnDev.SpawnJS.JSObjects;
using GPUCopyExternalImageSource = SpawnDev.SpawnJS.Union<SpawnDev.SpawnJS.JSObjects.ImageBitmap, SpawnDev.SpawnJS.JSObjects.ImageData, SpawnDev.SpawnJS.JSObjects.HTMLImageElement, SpawnDev.SpawnJS.JSObjects.HTMLVideoElement, SpawnDev.SpawnJS.JSObjects.VideoFrame, SpawnDev.SpawnJS.JSObjects.HTMLCanvasElement, SpawnDev.SpawnJS.JSObjects.OffscreenCanvas>;

namespace SpawnDev.NexStar.App.Eyepiece;

/// <summary>
/// A received video track as a GPU texture: the track plays in a video element that is never shown, and each frame the browser copies its current picture
/// straight into a texture (no pixels pass through .NET). The texture is what the HUD draws.
/// </summary>
public sealed class VideoSurface : IDisposable
{
    private readonly GPUDevice _device;
    private readonly GPUQueue _queue;
    private readonly HTMLVideoElement _video = new();
    private readonly MediaStream _stream = new();
    private GPUTexture? _texture;
    private GPUTextureView? _view;
    private bool _started;

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>The picture, once the first frame has arrived.</summary>
    public GPUTextureView? View => _view;

    /// <summary>Frames copied so far (a counter for the tests and the status line).</summary>
    public long Frames { get; private set; }

    public VideoSurface(GPUDevice device, GPUQueue queue, IRTCMediaStreamTrack track)
    {
        _device = device;
        _queue = queue;
        if (track is not BrowserRTCMediaStreamTrack browserTrack) throw new ArgumentException("a browser track is needed", nameof(track));
        _stream.AddTrack(browserTrack.NativeTrack);
        _video.Muted = true;
        _video.PlaysInline = true;
        _video.SrcObject = _stream;
    }

    /// <summary>Starts playback. Not awaited by the caller's frame loop: a refused play just leaves the picture empty.</summary>
    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        try { await _video.Play(); }
        catch (Exception ex) { Console.WriteLine($"[Eyepiece] the video would not play: {ex.Message}"); }
    }

    /// <summary>Per frame: copies the video's current picture into the texture (creating or resizing it as the picture's size changes).</summary>
    public void Update()
    {
        if (!_started || _video.ReadyState < 2) return;
        int w = _video.VideoWidth, h = _video.VideoHeight;
        if (w <= 0 || h <= 0) return;
        if (_texture == null || w != Width || h != Height)
        {
            _view?.Dispose();
            _texture?.Dispose();
            _texture = _device.CreateTexture(new GPUTextureDescriptor
            {
                Size = new[] { w, h },
                Format = "rgba8unorm",
                Usage = GPUTextureUsage.TextureBinding | GPUTextureUsage.CopyDst | GPUTextureUsage.RenderAttachment,
            });
            _view = _texture.CreateView();
            Width = w;
            Height = h;
        }
        _queue.CopyExternalImageToTexture(
            new GPUCopyExternalImageSourceInfo { Source = (GPUCopyExternalImageSource)_video },
            new GPUCopyExternalImageDestInfo { Texture = _texture },
            new GPUExtent3DDict { Width = (uint)w, Height = (uint)h });
        Frames++;
    }

    public void Dispose()
    {
        try { _video.Pause(); } catch { }
        _view?.Dispose();
        _texture?.Dispose();
        _video.Dispose();
        _stream.Dispose();
    }
}
