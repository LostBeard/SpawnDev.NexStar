using SpawnDev.GameUI;
using SpawnDev.GameUI.Elements;

namespace SpawnDev.NexStar.App.Workspace;

/// <summary>What the dock shows, gathered by the host a few times a second.</summary>
public sealed class DockState
{
    public bool Connected { get; init; }
    public string Title { get; init; } = "";
    public bool Aligned { get; init; }
    public bool HasFrame { get; init; }
    public (double Azimuth, double Altitude)? TruePointing { get; init; }
    public (double Azimuth, double Altitude)? MountPointing { get; init; }
    public bool Tracking { get; init; }
    public bool Moving { get; init; }
    public string? Message { get; init; }
    public CableGuard Cable { get; init; } = new();
}

/// <summary>
/// The telescope panel on the right: where it points, the press-and-hold slew pad with its rate, stop, tracking, and the cable (its position, the buttons
/// that set it right, and unwinding). It only raises events; the host runs the telescope.
/// </summary>
public sealed class TelescopeDock
{
    public const float Width = 268;
    public const float Height = 492;

    private readonly UIPanel _panel = new() { Width = Width, Height = Height };
    private readonly UILabel _title = new() { Text = "TELESCOPE", FontSize = FontSize.Body, X = 14, Y = 10 };
    private readonly UILabel _status = new() { Text = "", FontSize = FontSize.Caption, X = 14, Y = 32 };
    private readonly UILabel _true = new() { Text = "", FontSize = FontSize.Body, X = 14, Y = 52 };
    private readonly UILabel _mount = new() { Text = "", FontSize = FontSize.Caption, X = 14, Y = 74 };
    private readonly UIHoldButton _up = new() { Text = "^", Width = 64, Height = 44, X = 102, Y = 104 };
    private readonly UIHoldButton _left = new() { Text = "<", Width = 64, Height = 44, X = 34, Y = 152 };
    private readonly UIHoldButton _right = new() { Text = ">", Width = 64, Height = 44, X = 170, Y = 152 };
    private readonly UIHoldButton _down = new() { Text = "v", Width = 64, Height = 44, X = 102, Y = 200 };
    private readonly UIButton _stop = new() { Text = "STOP", Width = 64, Height = 44, X = 102, Y = 152 };
    private readonly UISlider _rate = new() { MinValue = 1, MaxValue = 9, Value = 6, Label = "Rate", Format = "F0", WheelStep = 1, Width = 240, Height = 30, X = 14, Y = 256 };
    private readonly UILabel _rateInfo = new() { Text = "", FontSize = FontSize.Caption, X = 14, Y = 290 };
    private readonly UIToggle _tracking = new() { Text = "Tracking", Width = 240, Height = 28, X = 14, Y = 314 };
    private readonly UILabel _cableTitle = new() { Text = "CABLE", FontSize = FontSize.Body, X = 14, Y = 352 };
    private readonly CableBar _cableBar = new() { Width = 240, Height = 12, X = 14, Y = 380 };
    private readonly UILabel _cableText = new() { Text = "", FontSize = FontSize.Caption, X = 14, Y = 398 };
    private readonly UIButton _unwind = new() { Text = "Unwind", Width = 116, Height = 28, X = 14, Y = 420 };
    private readonly UIButton _centered = new() { Text = "Centered here", Width = 124, Height = 28, X = 134, Y = 420 };
    private readonly UITextBlock _message = new() { Text = "", FontSize = FontSize.Caption, X = 14, Y = 456, Width = 240, Height = 32, MaxLines = 2 };

    private bool _suppressTracking;

    public UIElement Root => _panel;

    /// <summary>A pad button went down: the direction and the rate chosen.</summary>
    public event Action<PadDirection, SlewRate>? PadPressed;

    /// <summary>The held pad button was released.</summary>
    public event Action? PadReleased;

    public event Action? StopClicked;
    public event Action<bool>? TrackingChanged;
    public event Action? UnwindClicked;

    /// <summary>"The cable hangs straight here": sets the cable's neutral at the current azimuth.</summary>
    public event Action? CableCenteredClicked;

    public SlewRate Rate => (SlewRate)(int)Math.Clamp(Math.Round(_rate.Value), 1, 9);

    public TelescopeDock()
    {
        foreach (var e in new UIElement[] { _title, _status, _true, _mount, _up, _left, _right, _down, _stop, _rate, _rateInfo, _tracking, _cableTitle, _cableBar, _cableText, _unwind, _centered, _message })
            _panel.AddChild(e);

        Hold(_up, PadDirection.Up);
        Hold(_down, PadDirection.Down);
        Hold(_left, PadDirection.Left);
        Hold(_right, PadDirection.Right);
        _stop.OnClick = () => StopClicked?.Invoke();
        _unwind.OnClick = () => UnwindClicked?.Invoke();
        _centered.OnClick = () => CableCenteredClicked?.Invoke();
        _tracking.OnChanged = on => { if (!_suppressTracking) TrackingChanged?.Invoke(on); };
        _rate.OnChanged = _ => UpdateRateInfo();
        UpdateRateInfo();
    }

    private void Hold(UIHoldButton button, PadDirection direction)
    {
        button.OnHold = () => PadPressed?.Invoke(direction, Rate);
        button.OnRelease = () => PadReleased?.Invoke();
    }

    private void UpdateRateInfo()
    {
        var speed = NexStarProtocol.MeasuredFixedRateDegreesPerSecond[(int)Rate];
        _rateInfo.Text = speed >= 0.1 ? $"{speed:F2} deg/s" : $"{speed * 3600:F0} arcsec/s";
    }

    /// <summary>Releases a held pad button (the window lost focus, the screen is hidden).</summary>
    public void ReleaseAll()
    {
        _up.Release(); _down.Release(); _left.Release(); _right.Release();
    }

    /// <summary>Updates the readouts. Cheap: call it a few times a second.</summary>
    public void Refresh(DockState s)
    {
        var t = UITheme.Current;
        _status.Color = t.TextSecondary;
        _mount.Color = t.TextSecondary;
        _rateInfo.Color = t.TextSecondary;
        _cableText.Color = t.TextSecondary;
        _message.Color = t.TextPrimary;

        _status.Text = s.Connected ? $"{s.Title}   {(s.Aligned ? "aligned" : s.HasFrame ? "aligned by NexStar" : "not aligned")}" : "Not connected";
        if (s.Connected && s.TruePointing is { } tp)
            _true.Text = $"Az {tp.Azimuth:F2}   Alt {tp.Altitude:F2}";
        else if (s.Connected && s.MountPointing is { } mp)
            _true.Text = $"Az {mp.Azimuth:F2}   Alt {mp.Altitude:F2}";
        else
            _true.Text = "";
        _mount.Text = s.Connected && !s.HasFrame && s.MountPointing != null ? "mount's own frame: align to point by name" : "";

        foreach (var b in new UIButton[] { _up, _down, _left, _right, _stop }) b.Enabled = s.Connected;
        _tracking.Enabled = s.Connected;
        _suppressTracking = true;
        if (_tracking.IsOn != s.Tracking) _tracking.IsOn = s.Tracking;
        _suppressTracking = false;

        var cable = s.Cable;
        _cableBar.Cumulative = cable.Cumulative;
        _cableBar.Limit = cable.Options.LimitDegrees;
        _cableBar.Warn = cable.Options.WarnMarginDegrees;
        _cableBar.Uncertain = cable.Uncertain;
        if (!s.Connected)
        {
            _cableText.Text = "";
        }
        else if (cable.Uncertain)
        {
            _cableText.Text = "Position unknown: look at the cords";
        }
        else
        {
            var zone = cable.Zone switch { CableZone.Limit => "AT LIMIT", CableZone.Warn => "near the limit", _ => "clear" };
            _cableText.Text = $"{cable.Cumulative:+0;-0;0} deg of +/-{cable.Options.LimitDegrees:F0}   {zone}";
        }
        // two ways to say where the cable is: unwind if it is known and off-centre; "centered here" when it is not known (or to correct it)
        _unwind.Enabled = s.Connected && !cable.Uncertain && Math.Abs(cable.Cumulative) > 5 && !s.Moving;
        _centered.Enabled = s.Connected && !s.Moving;
        _message.Text = s.Message ?? "";
    }
}
