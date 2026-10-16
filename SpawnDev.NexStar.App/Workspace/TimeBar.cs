using SpawnDev.GameUI;
using SpawnDev.GameUI.Elements;

namespace SpawnDev.NexStar.App.Workspace;

/// <summary>
/// Steps the chart's clock: back and forward by a day, an hour or ten minutes, and back to now. The sky keeps turning while it is shifted; a shifted
/// chart is labelled in the top bar, and the telescope is never sent anywhere from it.
/// </summary>
public sealed class TimeBar
{
    public const float Height = 38;
    private const float ButtonWidth = 54;
    private const float Gap = 4;

    private readonly UIPanel _panel = new() { Height = Height };
    private readonly UIButton _now = new() { Text = "Now", Width = 64, Height = 28, Y = 5 };

    public UIElement Root => _panel;

    /// <summary>The chart's clock was moved by this much.</summary>
    public event Action<TimeSpan>? Shifted;

    /// <summary>Back to the present.</summary>
    public event Action? NowClicked;

    public float Width => _panel.Width;

    public TimeBar()
    {
        var steps = new (string Text, TimeSpan Step)[]
        {
            ("-1d", TimeSpan.FromDays(-1)), ("-1h", TimeSpan.FromHours(-1)), ("-10m", TimeSpan.FromMinutes(-10)),
            ("+10m", TimeSpan.FromMinutes(10)), ("+1h", TimeSpan.FromHours(1)), ("+1d", TimeSpan.FromDays(1)),
        };
        float x = 8;
        for (int i = 0; i < steps.Length; i++)
        {
            if (i == 3)
            {
                _now.X = x;
                _panel.AddChild(_now);
                x += _now.Width + Gap;
            }
            var step = steps[i].Step;
            var button = new UIButton { Text = steps[i].Text, Width = ButtonWidth, Height = 28, X = x, Y = 5 };
            button.OnClick = () => Shifted?.Invoke(step);
            _panel.AddChild(button);
            x += ButtonWidth + Gap;
        }
        _panel.Width = x + 4;
        _now.OnClick = () => NowClicked?.Invoke();
    }
}
