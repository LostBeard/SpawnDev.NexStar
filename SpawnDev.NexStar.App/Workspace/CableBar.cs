using System.Drawing;
using SpawnDev.GameUI;

namespace SpawnDev.NexStar.App.Workspace;

/// <summary>
/// The cable's position as a bar: neutral in the middle, the limits at the ends, the warning zones shaded, and a marker where the cable is now.
/// Cords wrap by degrees turned, so a straight bar says more at a glance than a dial.
/// </summary>
public sealed class CableBar : UIElement
{
    public double Cumulative { get; set; }
    public double Limit { get; set; } = 200;
    public double Warn { get; set; } = 30;

    /// <summary>When the position is not known no marker is drawn: a marker would claim something it cannot know.</summary>
    public bool Uncertain { get; set; }

    public override void Draw(UIRenderer renderer)
    {
        if (!Visible) return;
        var b = ScreenBounds;
        var t = UITheme.Current;
        renderer.DrawRect(b.X, b.Y, Width, Height, t.SliderTrack);
        var warnWidth = (float)(Math.Clamp(Warn / (2 * Limit), 0, 0.5) * Width);
        var warnColor = Color.FromArgb(120, t.FocusBorder);
        renderer.DrawRect(b.X, b.Y, warnWidth, Height, warnColor);
        renderer.DrawRect(b.X + Width - warnWidth, b.Y, warnWidth, Height, warnColor);
        renderer.DrawRect(b.X + Width / 2 - 1, b.Y - 3, 2, Height + 6, t.TextMuted);
        if (!Uncertain && Limit > 0)
        {
            var fraction = Math.Clamp((Cumulative + Limit) / (2 * Limit), 0, 1);
            var x = b.X + (float)(fraction * Width);
            renderer.DrawRect(x - 2, b.Y - 4, 4, Height + 8, t.TextPrimary);
        }
        base.Draw(renderer);
    }
}
