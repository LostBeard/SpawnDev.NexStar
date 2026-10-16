using System.Drawing;
using SpawnDev.GameUI;

namespace SpawnDev.NexStar.App.Workspace;

/// <summary>
/// The workspace's looks. <see cref="NightRed"/> is red only (no green or blue light at all): looking at a bright screen un-adapts the eye, and red
/// is the one colour that leaves the dark-adapted rods alone. It is the default; <see cref="Day"/> is the ordinary slate look for daylight set-up.
/// </summary>
public static class NexStarThemes
{
    public static UITheme NightRed => new()
    {
        PanelBackground = Color.FromArgb(215, 14, 0, 0),
        PanelBorder = Color.FromArgb(150, 130, 22, 22),
        PanelBorderWidth = 1,
        PanelCornerRadius = 6,
        ControlCornerRadius = 4,
        ButtonCornerRadius = 4,
        ButtonNormal = Color.FromArgb(255, 54, 6, 6),
        ButtonHover = Color.FromArgb(255, 96, 12, 12),
        ButtonPressed = Color.FromArgb(255, 30, 2, 2),
        ButtonDisabled = Color.FromArgb(255, 28, 6, 6),
        ButtonText = Color.FromArgb(255, 255, 96, 96),
        ButtonBorder = Color.FromArgb(200, 170, 30, 30),
        SliderTrack = Color.FromArgb(255, 40, 6, 6),
        SliderFill = Color.FromArgb(255, 190, 30, 30),
        SliderThumb = Color.FromArgb(255, 255, 110, 110),
        SliderLabel = Color.FromArgb(255, 200, 70, 70),
        LabelColor = Color.FromArgb(255, 255, 96, 96),
        TextPrimary = Color.FromArgb(255, 255, 96, 96),
        TextSecondary = Color.FromArgb(255, 196, 64, 64),
        TextMuted = Color.FromArgb(255, 120, 34, 34),
        FocusBorder = Color.FromArgb(255, 255, 72, 72),
        TooltipBackground = Color.FromArgb(235, 18, 0, 0),
        TooltipText = Color.FromArgb(255, 255, 110, 110),
        TooltipBorder = Color.FromArgb(170, 130, 22, 22),
        SeparatorColor = Color.FromArgb(110, 150, 30, 30),
        WorldTextOutlineColor = Color.FromArgb(220, 0, 0, 0),
    };

    public static UITheme Day => UITheme.Dark;
}
