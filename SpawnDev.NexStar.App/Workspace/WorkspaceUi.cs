using System.Drawing;
using SpawnDev.GameUI;
using SpawnDev.GameUI.Elements;
using SpawnDev.NexStar.App.Services;

namespace SpawnDev.NexStar.App.Workspace;

/// <summary>
/// The workspace's HUD: one tree drawn by GameUI over the sky. A top bar with the connection, where the scope points, the cable and the time;
/// everything else (side docks, the object card, time controls) is added here as it is built.
/// </summary>
public sealed class WorkspaceUi
{
    private const float BarHeight = 46;

    private readonly NexStarService _mount;
    private readonly LocationService _location;
    private readonly UIAnchorPanel _root = new();
    private readonly UIPanel _topBar = new() { Height = BarHeight, CornerRadius = 0 };
    private readonly UILabel _title = new() { Text = "NexStar", FontSize = FontSize.Heading };
    private readonly UILabel _connection = new() { Text = "Not connected", FontSize = FontSize.Body };
    private readonly UILabel _position = new() { Text = "", FontSize = FontSize.Body };
    private readonly UILabel _cable = new() { Text = "", FontSize = FontSize.Body };
    private readonly UILabel _clock = new() { Text = "", FontSize = FontSize.Body, Align = TextAlign.Right };
    private readonly UIButton _serial = new() { Text = "Connect", Width = 96, Height = 30 };
    private readonly UIButton _usb = new() { Text = "Connect USB", Width = 118, Height = 30 };
    private readonly UIButton _disconnect = new() { Text = "Disconnect", Width = 104, Height = 30 };
    private double _sinceRefresh = 1;
    private int _width, _height;

    /// <summary>The element to register as the HUD screen.</summary>
    public UIElement Root => _root;

    /// <summary>Raised when the user asks to connect. The argument is true for WebUSB.</summary>
    public event Action<bool>? ConnectRequested;

    public event Action? DisconnectRequested;

    public WorkspaceUi(NexStarService mount, LocationService location)
    {
        _mount = mount;
        _location = location;

        _root.AddAnchored(_topBar, Anchor.TopLeft);
        _topBar.AddChild(_title);
        _topBar.AddChild(_connection);
        _topBar.AddChild(_position);
        _topBar.AddChild(_cable);
        _topBar.AddChild(_clock);
        _topBar.AddChild(_serial);
        _topBar.AddChild(_usb);
        _topBar.AddChild(_disconnect);

        _serial.OnClick = () => ConnectRequested?.Invoke(false);
        _usb.OnClick = () => ConnectRequested?.Invoke(true);
        _disconnect.OnClick = () => DisconnectRequested?.Invoke();
    }

    /// <summary>Lays the HUD out for a viewport in CSS pixels.</summary>
    public void Layout(int width, int height)
    {
        _width = width;
        _height = height;
        _root.Width = width;
        _root.Height = height;
        _topBar.Width = width;

        var narrow = width < 760;
        _title.X = 14; _title.Y = 12;
        _connection.X = 100; _connection.Y = 14;
        _position.X = narrow ? 14 : 330; _position.Y = narrow ? 30 : 14;
        _cable.X = narrow ? 14 : 640; _cable.Y = 14;
        _clock.Width = 150; _clock.X = width - 150 - 14; _clock.Y = 14;
        _serial.Y = 8; _usb.Y = 8; _disconnect.Y = 8;
        _serial.X = width - 150 - 14 - 96 - 8 - 118 - 8; _usb.X = _serial.X + 96 + 8;
        _disconnect.X = _serial.X;

        _topBar.Height = narrow ? BarHeight + 18 : BarHeight;
    }

    /// <summary>Per-frame: updates the readouts a few times a second.</summary>
    public void Refresh(float dt)
    {
        _sinceRefresh += dt;
        if (_sinceRefresh < 0.25) return;
        _sinceRefresh = 0;

        var connected = _mount.ComsEnabled;
        _serial.Visible = _usb.Visible = !connected;
        _disconnect.Visible = connected;
        _connection.Text = connected
            ? $"{_mount.Model.GetDisplayName()} {_mount.Version}  {(_mount.IsAligned ? "aligned" : "not aligned")}"
            : "Not connected";

        var p = _mount.CurrentAzAlt;
        _position.Text = connected && p != null ? $"Az {p.Azimuth:F3}°   Alt {p.Altitude:F3}°" : "";

        var cable = _mount.Cable;
        if (connected)
        {
            var zone = cable.Zone switch { CableZone.Limit => "AT LIMIT", CableZone.Warn => "near limit", _ => cable.Uncertain ? "unknown" : "clear" };
            _cable.Text = $"Cable {cable.Cumulative:+0;-0;0}° {zone}";
        }
        else
        {
            _cable.Text = "";
        }
        _clock.Text = DateTime.UtcNow.ToString("HH:mm:ss") + " UTC";
    }
}
