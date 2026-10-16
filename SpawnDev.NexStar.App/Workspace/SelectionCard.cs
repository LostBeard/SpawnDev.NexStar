using SpawnDev.GameUI;
using SpawnDev.GameUI.Elements;
using SpawnDev.NexStar.Sky;

namespace SpawnDev.NexStar.App.Workspace;

/// <summary>
/// The card for the picked object: what it is, where it is now, and what can be done with it (go there, centre the chart on it, align the telescope on
/// it). The Sun gets a warning instead of the buttons. Raises events; the host runs the telescope.
/// </summary>
public sealed class SelectionCard
{
    public const float Width = 352;
    public const float Height = 168;

    private readonly UIPanel _panel = new() { Width = Width, Height = Height, Visible = false };
    private readonly UILabel _name = new() { Text = "", FontSize = FontSize.Heading, X = 14, Y = 10 };
    private readonly UILabel _detail = new() { Text = "", FontSize = FontSize.Body, X = 14, Y = 36 };
    private readonly UILabel _where = new() { Text = "", FontSize = FontSize.Body, X = 14, Y = 58 };
    private readonly UILabel _coords = new() { Text = "", FontSize = FontSize.Caption, X = 14, Y = 80 };
    private readonly UITextBlock _note = new() { Text = "", FontSize = FontSize.Caption, X = 14, Y = 98, Width = Width - 28, Height = 30, MaxLines = 2 };
    private readonly UIButton _goto = new() { Text = "Go to", Width = 84, Height = 30, X = 14, Y = 130 };
    private readonly UIButton _center = new() { Text = "Center", Width = 84, Height = 30, X = 104, Y = 130 };
    private readonly UIButton _align = new() { Text = "Align here", Width = 110, Height = 30, X = 194, Y = 130 };
    private readonly UIButton _close = new() { Text = "x", Width = 30, Height = 28, X = Width - 40, Y = 8 };

    public UIElement Root => _panel;
    public bool IsOpen => _panel.Visible;

    public event Action? GotoClicked;
    public event Action? CenterClicked;
    public event Action? AlignClicked;
    public event Action? CloseClicked;

    public SelectionCard()
    {
        foreach (var e in new UIElement[] { _name, _detail, _where, _coords, _note, _goto, _center, _align, _close }) _panel.AddChild(e);
        _goto.OnClick = () => GotoClicked?.Invoke();
        _center.OnClick = () => CenterClicked?.Invoke();
        _align.OnClick = () => AlignClicked?.Invoke();
        _close.OnClick = () => CloseClicked?.Invoke();
    }

    /// <summary>Shows the object, or hides the card when it is null.</summary>
    /// <param name="connected">A telescope is connected</param>
    /// <param name="aligned">The hand control has its own alignment</param>
    /// <param name="note">What the last attempt to use the object said, if anything</param>
    public void Show(SkyObject? o, bool connected, bool aligned, string? note)
    {
        _panel.Visible = o != null;
        if (o == null) return;
        var t = UITheme.Current;
        _detail.Color = t.TextSecondary;
        _coords.Color = t.TextSecondary;
        _note.Color = t.TextPrimary;

        _name.Text = o.Name;
        _detail.Text = o.Detail ?? "";
        var magnitude = o.Magnitude is double m && m < 90 ? $"mag {m:F1}   " : "";
        _where.Text = $"{magnitude}Az {o.Azimuth:F1}   Alt {o.Altitude:F1}" + (o.Altitude < 0 ? "   (below the horizon)" : "");
        if (o.J2000 is System.Numerics.Vector3 v)
        {
            var (ra, dec) = CatalogFormats.ToRaDec(v);
            _coords.Text = $"J2000  RA {AstronomyMath.FormatHMS(ra / 15.0)}   Dec {AstronomyMath.FormatDMS(dec)}";
        }
        else
        {
            _coords.Text = "";
        }

        var isSun = !o.CanGoto;
        _note.Text = isSun ? "Never point the telescope at the Sun: it blinds and burns." : note ?? "";
        _goto.Enabled = connected && o.CanGoto && o.Altitude >= 0;
        _align.Visible = connected && !aligned && o.CanGoto;
        _goto.Visible = !isSun;
    }
}
