using SpawnDev.GameUI;
using SpawnDev.GameUI.Elements;
using SpawnDev.NexStar.Sky;

namespace SpawnDev.NexStar.App.Workspace;

/// <summary>The panel of switches for what the chart shows, with night vision and the star size. Raises <see cref="Changed"/> whenever anything is switched.</summary>
public sealed class ViewPanel
{
    public const float Width = 232;
    private const float RowHeight = 28;

    private readonly UIPanel _panel = new() { Width = Width, Visible = false };
    private readonly List<(UIToggle Toggle, Action<ViewSettings, bool> Apply, Func<ViewSettings, bool> Read)> _rows = new();
    private readonly UISlider _starSize = new() { MinValue = 0.5f, MaxValue = 2.5f, Value = 1f, Label = "Star size", Format = "F1", WheelStep = 0.1f, Width = Width - 28, Height = 30, X = 14 };
    private readonly UILabel _site = new() { Text = "", FontSize = FontSize.Caption, X = 14 };
    private readonly UIButton _locate = new() { Text = "Use this device's location", Width = Width - 28, Height = 28, X = 14 };
    private bool _loading;

    public UIElement Root => _panel;
    public bool IsOpen => _panel.Visible;

    /// <summary>Anything was switched: the new settings.</summary>
    public event Action<ViewSettings>? Changed;

    public event Action? LocateClicked;

    public ViewPanel()
    {
        Row("Constellation lines", (s, on) => s.Layers = Flag(s.Layers, SkyLayers.ConstellationLines, on), s => s.Layers.HasFlag(SkyLayers.ConstellationLines));
        Row("Constellation names", (s, on) => s.ConstellationNames = on, s => s.ConstellationNames);
        Row("Constellation borders", (s, on) => s.Layers = Flag(s.Layers, SkyLayers.ConstellationBoundaries, on), s => s.Layers.HasFlag(SkyLayers.ConstellationBoundaries));
        Row("Stars", (s, on) => s.Layers = Flag(s.Layers, SkyLayers.Stars, on), s => s.Layers.HasFlag(SkyLayers.Stars));
        Row("Star names", (s, on) => s.StarNames = on, s => s.StarNames);
        Row("Deep-sky objects", (s, on) => s.Layers = Flag(s.Layers, SkyLayers.DeepSky, on), s => s.Layers.HasFlag(SkyLayers.DeepSky));
        Row("Planets, Moon, Sun", (s, on) => s.SolarSystem = on, s => s.SolarSystem);
        Row("Alt-az grid", (s, on) => s.Layers = Flag(s.Layers, SkyLayers.AltAzGrid, on), s => s.Layers.HasFlag(SkyLayers.AltAzGrid));
        Row("Equatorial grid", (s, on) => s.Layers = Flag(s.Layers, SkyLayers.EquatorialGrid, on), s => s.Layers.HasFlag(SkyLayers.EquatorialGrid));
        Row("Ground and horizon", (s, on) => s.Layers = Flag(s.Layers, SkyLayers.GroundAndHorizon, on), s => s.Layers.HasFlag(SkyLayers.GroundAndHorizon));
        Row("Night vision (red)", (s, on) => s.Night = on, s => s.Night);

        float y = 12 + _rows.Count * RowHeight + 4;
        _starSize.Y = y;
        _panel.AddChild(_starSize);
        _starSize.OnChanged = _ => Raise();
        y += 40;
        _site.Y = y;
        _panel.AddChild(_site);
        y += 20;
        _locate.Y = y;
        _locate.OnClick = () => LocateClicked?.Invoke();
        _panel.AddChild(_locate);
        _panel.Height = y + 28 + 14;
    }

    public float Height => _panel.Height;

    private static SkyLayers Flag(SkyLayers layers, SkyLayers flag, bool on) => on ? layers | flag : layers & ~flag;

    private void Row(string text, Action<ViewSettings, bool> apply, Func<ViewSettings, bool> read)
    {
        var toggle = new UIToggle { Text = text, Width = Width - 28, Height = RowHeight - 2, X = 14, Y = 12 + _rows.Count * RowHeight };
        toggle.OnChanged = _ => Raise();
        _panel.AddChild(toggle);
        _rows.Add((toggle, apply, read));
    }

    /// <summary>Sets the switches to match the settings (without raising <see cref="Changed"/>).</summary>
    public void Load(ViewSettings settings)
    {
        _loading = true;
        foreach (var row in _rows) row.Toggle.IsOn = row.Read(settings);
        _starSize.Value = settings.StarSize;
        _loading = false;
    }

    private void Raise()
    {
        if (_loading) return;
        var s = new ViewSettings();
        foreach (var row in _rows) row.Apply(s, row.Toggle.IsOn);
        s.StarSize = _starSize.Value;
        Changed?.Invoke(s);
    }

    public void Show(bool open) => _panel.Visible = open;

    public void ShowSite(string text) { _site.Text = text; _site.Color = UITheme.Current.TextSecondary; }
}
