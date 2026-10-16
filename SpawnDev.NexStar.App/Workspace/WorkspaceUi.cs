using System.Drawing;
using SpawnDev.GameUI;
using SpawnDev.GameUI.Elements;
using SpawnDev.NexStar.App.Services;

namespace SpawnDev.NexStar.App.Workspace;

/// <summary>
/// The workspace's HUD: one tree drawn by GameUI over the sky. A top bar with the connection, where the scope points, the cable and the time; the
/// telescope dock on the right; the card for the picked object at the bottom left; the chart's view line at the bottom.
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
    private readonly UILabel _viewInfo = new() { Text = "", FontSize = FontSize.Body };
    private readonly UIButton _serial = new() { Text = "Connect", Width = 96, Height = 30 };
    private readonly UIButton _usb = new() { Text = "Connect USB", Width = 118, Height = 30 };
    private readonly UIButton _disconnect = new() { Text = "Disconnect", Width = 104, Height = 30 };
    private readonly UIButton _dockToggle = new() { Text = "Telescope", Width = TelescopeDock.Width, Height = 30 };
    private readonly UIButton _viewToggle = new() { Text = "View", Width = ViewPanel.Width, Height = 30 };
    private readonly UIButton _eyepieceToggle = new() { Text = "Eyepiece", Width = 118, Height = 30 };
    private double _sinceRefresh = 1;
    private int _width, _height;
    private bool _dockOpen = true;
    private bool _dockChosen;
    private bool _narrow;

    /// <summary>The element to register as the HUD screen.</summary>
    public UIElement Root => _root;

    public TelescopeDock Dock { get; } = new();
    public SelectionCard Card { get; } = new();
    public ViewPanel View { get; } = new();
    public TimeBar Time { get; } = new();
    public EyepiecePanel Eyepiece { get; } = new();

    /// <summary>Where the heading tape goes: just under the bar (and under the toggle row on a narrow screen).</summary>
    public float CompassTop { get; private set; } = 56;

    /// <summary>The telescope is a simulation: say so everywhere it is named.</summary>
    public bool Demo { get; set; }

    /// <summary>Raised when the user asks to connect. The argument is true for WebUSB.</summary>
    public event Action<bool>? ConnectRequested;

    public event Action? DisconnectRequested;

    public WorkspaceUi(NexStarService mount, LocationService location)
    {
        _mount = mount;
        _location = location;

        _root.AddAnchored(_topBar, Anchor.TopLeft);
        _root.AddAnchored(_viewInfo, Anchor.BottomLeft, offsetX: 14, offsetY: -12);
        _root.AddAnchored(Card.Root, Anchor.BottomCenter, offsetX: 0, offsetY: -(TimeBar.Height + 20));
        _root.AddAnchored(_dockToggle, Anchor.TopRight, offsetX: -12, offsetY: BarHeight + 10);
        _root.AddAnchored(_viewToggle, Anchor.TopLeft, offsetX: 12, offsetY: BarHeight + 10);
        _root.AddAnchored(View.Root, Anchor.TopLeft, offsetX: 12, offsetY: BarHeight + 46);
        _root.AddAnchored(Time.Root, Anchor.BottomCenter, offsetX: 0, offsetY: -10);
        _root.AddAnchored(_eyepieceToggle, Anchor.TopLeft, offsetX: 12 + ViewPanel.Width + 8, offsetY: BarHeight + 10);
        _root.AddAnchored(Eyepiece.Root, Anchor.TopLeft, offsetX: 12 + ViewPanel.Width + 8, offsetY: BarHeight + 46);
        _root.AddAnchored(Dock.Root, Anchor.TopRight, offsetX: -12, offsetY: BarHeight + 46);
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
        _dockToggle.OnClick = () => { _dockOpen = !_dockOpen; _dockChosen = true; if (_narrow && _dockOpen) { View.Show(false); Eyepiece.Show(false); } ApplyDock(); ApplyToggles(); };
        _viewToggle.OnClick = () => { var open = !View.IsOpen; View.Show(open); if (_narrow && open) { Eyepiece.Show(false); _dockOpen = false; _dockChosen = true; ApplyDock(); } ApplyToggles(); };
        _eyepieceToggle.OnClick = () => { var open = !Eyepiece.IsOpen; Eyepiece.Show(open); if (_narrow && open) { View.Show(false); _dockOpen = false; _dockChosen = true; ApplyDock(); } ApplyToggles(); };
    }

    /// <summary>The toggle buttons say what pressing them does. On a narrow screen they are short, three to a row.</summary>
    private void ApplyToggles()
    {
        _viewToggle.Text = View.IsOpen ? (_narrow ? "Close" : "Hide view") : "View";
        _eyepieceToggle.Text = Eyepiece.IsOpen ? (_narrow ? "Close" : "Hide") : "Eyepiece";
        _dockToggle.Text = _dockOpen ? (_narrow ? "Close" : "Hide telescope") : (_narrow ? "Scope" : "Telescope");
    }

    private void ApplyDock()
    {
        Dock.Root.Visible = _dockOpen;
        if (!_dockOpen) Dock.ReleaseAll();
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
        _narrow = narrow;
        float barHeight;
        if (narrow)
        {
            // three rows: the title and the clock; the connection and the cable; the position, or the connect buttons when there is none
            barHeight = 92;
            _title.X = 14; _title.Y = 8;
            _clock.Width = 150; _clock.X = width - 150 - 14; _clock.Y = 12;
            _connection.X = 14; _connection.Y = 38;
            _cable.X = Math.Max(190, width - 190); _cable.Y = 38;
            _position.X = 14; _position.Y = 64;
            _serial.Y = 56; _usb.Y = 56; _disconnect.Y = 56;
            _serial.X = 12; _usb.X = _serial.X + 96 + 8;
            _disconnect.X = width - 104 - 12;
        }
        else
        {
            barHeight = BarHeight;
            _title.X = 14; _title.Y = 12;
            _connection.X = 124; _connection.Y = 14;
            _position.X = 360; _position.Y = 14;
            _cable.X = 650; _cable.Y = 14;
            _clock.Width = 150; _clock.X = width - 150 - 14; _clock.Y = 14;
            _serial.Y = 8; _usb.Y = 8; _disconnect.Y = 8;
            _serial.X = width - 150 - 14 - 96 - 8 - 118 - 8; _usb.X = _serial.X + 96 + 8;
            _disconnect.X = _serial.X;
        }
        _topBar.Height = barHeight;
        CompassTop = narrow ? barHeight + 42 : barHeight + 10;

        // the toggles and the panels they open sit under the bar
        if (narrow)
        {
            float w = (width - 24 - 12) / 3f;
            _viewToggle.Width = _eyepieceToggle.Width = _dockToggle.Width = w;
            _root.SetAnchor(_viewToggle, Anchor.TopLeft, 12, barHeight + 6);
            _root.SetAnchor(_eyepieceToggle, Anchor.TopLeft, 12 + w + 6, barHeight + 6);
            _root.SetAnchor(_dockToggle, Anchor.TopLeft, 12 + 2 * (w + 6), barHeight + 6);
            _root.SetAnchor(View.Root, Anchor.TopLeft, 12, barHeight + 42);
            _root.SetAnchor(Eyepiece.Root, Anchor.TopLeft, 12, barHeight + 42);
            _root.SetAnchor(Dock.Root, Anchor.TopRight, -12, barHeight + 42);
            _root.SetAnchor(_viewInfo, Anchor.BottomLeft, 14, -(TimeBar.Height + 18));
        }
        else
        {
            _viewToggle.Width = ViewPanel.Width; _eyepieceToggle.Width = 118; _dockToggle.Width = TelescopeDock.Width;
            _root.SetAnchor(_viewToggle, Anchor.TopLeft, 12, barHeight + 10);
            _root.SetAnchor(_eyepieceToggle, Anchor.TopLeft, 12 + ViewPanel.Width + 8, barHeight + 10);
            _root.SetAnchor(_dockToggle, Anchor.TopRight, -12, barHeight + 10);
            _root.SetAnchor(View.Root, Anchor.TopLeft, 12, barHeight + 46);
            _root.SetAnchor(Eyepiece.Root, Anchor.TopLeft, 12 + ViewPanel.Width + 8, barHeight + 46);
            _root.SetAnchor(Dock.Root, Anchor.TopRight, -12, barHeight + 46);
            _root.SetAnchor(_viewInfo, Anchor.BottomLeft, 14, -12);
        }

        // the dock opens by default only where it leaves room for the sky; a person's own choice sticks
        if (!_dockChosen) _dockOpen = width >= 900 && height >= TelescopeDock.Height + BarHeight + 60;
        if (narrow && (View.IsOpen || Eyepiece.IsOpen)) _dockOpen = false;
        ApplyDock();
        ApplyToggles();
    }

    /// <summary>Per-frame: updates the readouts a few times a second.</summary>
    public void Refresh(float dt, SpawnDev.NexStar.Sky.SkyView sky, bool locationKnown, DockState dock, SpawnDev.NexStar.App.Eyepiece.EyepieceViewer? eyepiece)
    {
        _sinceRefresh += dt;
        if (_sinceRefresh < 0.25) return;
        _sinceRefresh = 0;

        var connected = _mount.ComsEnabled;
        _serial.Visible = _usb.Visible = !connected;
        _disconnect.Visible = connected;
        _connection.Text = connected
            ? $"{(Demo ? "DEMO " : "")}{_mount.Model.GetDisplayName()}  {(_mount.IsAligned ? "aligned" : dock.HasFrame ? "aligned here" : "not aligned")}"
            : "Not connected";

        var p = _mount.CurrentAzAlt;
        _position.Text = connected && p != null ? $"Mount Az {p.Azimuth:F3}°   Alt {p.Altitude:F3}°" : "";

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
        // the time the chart shows, which is not now when it was set to another time: say so
        var shown = sky.Utc;
        var notNow = Math.Abs((shown - DateTime.UtcNow).TotalSeconds) > 5;
        _clock.Text = (notNow ? shown.ToString("yyyy-MM-dd HH:mm") : shown.ToString("HH:mm:ss")) + " UTC";
        _viewInfo.Text = $"Az {sky.CenterAzimuth:F1}°  Alt {sky.CenterAltitude:F1}°   FOV {sky.FovDegrees:F1}°   mag {sky.MagnitudeLimit:F1}"
            + (locationKnown ? "" : "   (location not known: using a default)");

        Dock.Refresh(dock);
        Eyepiece.Refresh(eyepiece);
    }
}
