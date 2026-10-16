using System.Drawing;
using SpawnDev.GameUI;
using SpawnDev.GameUI.Elements;
using SpawnDev.NexStar.App.Eyepiece;
using SpawnDev.NexStar.Eyepiece;

namespace SpawnDev.NexStar.App.Workspace;

/// <summary>A QR code drawn as rectangles: a light square with the dark modules as runs (a few hundred quads, not thousands). Light and dark, whatever the theme: a scanner needs the contrast.</summary>
public sealed class QrView : UIElement
{
    public bool[,]? Matrix { get; set; }
    public int QuietModules { get; set; } = 3;

    public override void Draw(UIRenderer renderer)
    {
        if (!Visible || Matrix == null) return;
        var b = ScreenBounds;
        int n = Matrix.GetLength(0);
        float cell = Math.Min(b.Width, b.Height) / (n + QuietModules * 2);
        renderer.DrawRect(b.X, b.Y, cell * (n + QuietModules * 2), cell * (n + QuietModules * 2), Color.FromArgb(255, 235, 235, 235));
        var dark = Color.FromArgb(255, 10, 10, 10);
        for (int y = 0; y < n; y++)
        {
            int x = 0;
            while (x < n)
            {
                if (!Matrix[y, x]) { x++; continue; }
                int start = x;
                while (x < n && Matrix[y, x]) x++;
                renderer.DrawRect(b.X + (QuietModules + start) * cell, b.Y + (QuietModules + y) * cell, (x - start) * cell, cell + 0.5f, dark);
            }
        }
        base.Draw(renderer);
    }
}

/// <summary>
/// The Eyepiece panel: pair a phone (a QR code to scan with its camera, or a link to copy), and once linked, the phone's picture and where it points.
/// Raises events; the host owns the <see cref="EyepieceViewer"/>.
/// </summary>
public sealed class EyepiecePanel
{
    public const float Width = 372;
    private const float QrSize = 210;

    private readonly UIPanel _panel = new() { Width = Width, Visible = false };
    private readonly UILabel _title = new() { Text = "EYEPIECE", FontSize = FontSize.Body, X = 14, Y = 10 };
    private readonly UILabel _state = new() { Text = "", FontSize = FontSize.Caption, X = 14, Y = 32 };
    private readonly UITextBlock _help = new() { Text = "", FontSize = FontSize.Caption, X = 14, Y = 52, Width = Width - 28, Height = 52 };
    private readonly UIButton _pair = new() { Text = "Pair a phone", Width = 130, Height = 30, X = 14 };
    private readonly UIButton _copy = new() { Text = "Copy link", Width = 100, Height = 30, X = 150 };
    private readonly UIButton _unpair = new() { Text = "Unpair", Width = 90, Height = 30 };
    private readonly QrView _qr = new() { Width = QrSize, Height = QrSize, X = (Width - QrSize) / 2 };
    private readonly UIImage _image = new() { Width = Width - 28, Height = (Width - 28) * 9 / 16, X = 14 };
    private readonly UILabel _pose = new() { Text = "", FontSize = FontSize.Body, X = 14 };
    private readonly UILabel _camera = new() { Text = "", FontSize = FontSize.Caption, X = 14 };

    public UIElement Root => _panel;
    public bool IsOpen => _panel.Visible;
    public float Height => _panel.Height;

    public event Action? PairClicked;
    public event Action? UnpairClicked;
    public event Action? CopyLinkClicked;

    public EyepiecePanel()
    {
        foreach (var e in new UIElement[] { _title, _state, _help, _pair, _copy, _unpair, _qr, _image, _pose, _camera }) _panel.AddChild(e);
        _pair.OnClick = () => PairClicked?.Invoke();
        _unpair.OnClick = () => UnpairClicked?.Invoke();
        _copy.OnClick = () => CopyLinkClicked?.Invoke();
        Refresh(null);
    }

    public void Show(bool open) => _panel.Visible = open;

    /// <summary>Per frame: the picture follows the video texture (it is recreated when the picture's size changes).</summary>
    public void UpdateVideo(EyepieceViewer? viewer)
    {
        _image.TextureView = viewer?.Video?.View;
        if (viewer?.Video is { Width: > 0, Height: > 0 } v)
        {
            var aspect = (float)v.Height / v.Width;
            _image.Height = (Width - 28) * aspect;
        }
    }

    /// <summary>A few times a second: texts, buttons and the layout for what the link is doing.</summary>
    public void Refresh(EyepieceViewer? viewer)
    {
        var t = UITheme.Current;
        _state.Color = t.TextSecondary;
        _help.Color = t.TextSecondary;
        _camera.Color = t.TextSecondary;

        var state = viewer?.State ?? EyepieceLinkState.Stopped;
        var hasPairing = viewer?.Pairing != null;
        _qr.Matrix = viewer?.Qr;
        float y = 52;

        _state.Text = state switch
        {
            EyepieceLinkState.Open => "Linked: " + (viewer?.Detail ?? "phone"),
            EyepieceLinkState.Connecting => "Checking the phone...",
            EyepieceLinkState.Waiting => "Waiting for the phone: " + (viewer?.Detail ?? ""),
            EyepieceLinkState.Failed => "Could not link: " + (viewer?.Detail ?? ""),
            _ => "Not paired",
        };

        _pair.Visible = !hasPairing;
        _copy.Visible = hasPairing && state != EyepieceLinkState.Open;
        _unpair.Visible = hasPairing;
        _qr.Visible = hasPairing && state != EyepieceLinkState.Open;
        _image.Visible = state == EyepieceLinkState.Open;
        _pose.Visible = _camera.Visible = state == EyepieceLinkState.Open;

        if (!hasPairing)
        {
            _help.Text = "Put a phone on the eyepiece and stream its camera and compass to this screen. Pair once; it reconnects by itself.";
            _help.Y = y; y += 60;
            _pair.Y = y; y += 40;
        }
        else if (state != EyepieceLinkState.Open)
        {
            _help.Text = "On the phone, scan this code with its camera app and open the link. (The code is light on purpose: a scanner needs the contrast.)";
            _help.Y = y; y += 62;
            _qr.Y = y; y += QrSize + 10;
            _copy.Y = y; _unpair.Y = y; _unpair.X = Width - 14 - _unpair.Width; y += 40;
        }
        else
        {
            _help.Text = "";
            _image.Y = y;
            y += _image.Height + 8;
            if (viewer?.Pose is { } pose)
            {
                var age = viewer.Orientation != null ? (DateTime.UtcNow - viewer.OrientationAtUtc).TotalSeconds : 99;
                _pose.Text = age > 3 ? "Phone orientation: no data" : $"Camera  Az {pose.Azimuth:F1}   Alt {pose.Altitude:F1}   Roll {pose.Roll:F1}";
            }
            else
            {
                _pose.Text = "Phone orientation: no data";
            }
            _pose.Y = y; y += 24;
            var s = viewer?.Status;
            _camera.Text = s != null ? $"{s.Camera}  {s.Width}x{s.Height}" + (s.Battery is double b ? $"   battery {b * 100:F0}%" : "") : "";
            _camera.Y = y; y += 22;
            _unpair.Y = y; _unpair.X = 14; y += 40;
        }
        _panel.Height = y + 6;
    }
}
