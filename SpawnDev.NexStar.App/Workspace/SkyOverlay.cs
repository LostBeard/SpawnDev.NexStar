using System.Drawing;
using System.Numerics;
using SpawnDev.GameUI;
using SpawnDev.NexStar.Sky;

namespace SpawnDev.NexStar.App.Workspace;

/// <summary>
/// What is drawn on top of the GPU sky with GameUI's renderer: the compass points, star and constellation names, the Sun, Moon and planets. These are a
/// few dozen items, so the CPU places them (using the same <see cref="SkyView"/> geometry the shaders use); the tens of thousands of stars never leave the GPU.
/// </summary>
public sealed class SkyOverlay : UIElement
{
    private readonly SkyView _view;
    private readonly SkyData _data;

    // text labels are one quad per glyph and the renderer batches a few thousand quads a frame: keep to a budget
    private const int MaxStarLabels = 40;
    private const int MaxConstellationLabels = 24;

    private readonly CatalogStar[] _namedStars;
    private readonly List<(float X0, float Y0, float X1, float Y1)> _placed = new();

    /// <summary>Use red only, for dark adaptation.</summary>
    public bool NightMode { get; set; } = true;

    public bool ShowStarNames { get; set; } = true;
    public bool ShowConstellationNames { get; set; } = true;
    public bool ShowSolarSystem { get; set; } = true;

    /// <summary>Where the telescope points, true azimuth and altitude (null when unknown or the frame is not set up).</summary>
    public (double Azimuth, double Altitude)? TelescopePointing { get; set; }

    /// <summary>Where the eyepiece phone's camera points by its compass and tilt (true azimuth), a cross-check on the mount.</summary>
    public (double Azimuth, double Altitude)? PhonePointing { get; set; }

    /// <summary>The picked object, ringed on the chart.</summary>
    public SkyObject? Selected { get; set; }

    public SkyOverlay(SkyView view, SkyData data)
    {
        _view = view;
        _data = data;
        _namedStars = data.BrightStars.Where(s => data.StarNames.ContainsKey(s.Hip)).ToArray();
    }

    private Color Tone(int r, int g, int b, int a = 255)
    {
        if (!NightMode) return Color.FromArgb(a, r, g, b);
        var l = (int)(0.3 * r + 0.59 * g + 0.11 * b);
        return Color.FromArgb(a, Math.Min(255, l + 20), 0, 0);
    }

    public override void Draw(UIRenderer renderer)
    {
        if (!Visible) return;
        _placed.Clear();
        DrawCompass(renderer);
        if (ShowConstellationNames) DrawConstellationNames(renderer);
        if (ShowStarNames) DrawStarNames(renderer);
        if (ShowSolarSystem) DrawSolarSystem(renderer);
        DrawSelected(renderer);
        DrawTelescope(renderer);
        DrawPhone(renderer);
        base.Draw(renderer);
    }

    private bool OnScreen(double x, double y, double margin = 0) => x >= -margin && x <= _view.WidthPx + margin && y >= -margin && y <= _view.HeightPx + margin;

    /// <summary>True (and the box is remembered) if a label box does not overlap a label already placed this frame.</summary>
    private bool Place(float x, float y, float w, float h)
    {
        foreach (var p in _placed)
            if (x < p.X1 && x + w > p.X0 && y < p.Y1 && y + h > p.Y0) return false;
        _placed.Add((x, y, x + w, y + h));
        return true;
    }

    private void DrawCompass(UIRenderer r)
    {
        var color = Tone(255, 190, 120);
        foreach (var (name, az) in new[] { ("N", 0.0), ("E", 90.0), ("S", 180.0), ("W", 270.0) })
        {
            if (!_view.TryProject(SkyView.FromAzAlt(az, 0), out var x, out var y) || !OnScreen(x, y, -10)) continue;
            // sit just above the horizon so the letter is on the sky side
            if (_view.TryProject(SkyView.FromAzAlt(az, 3.0 * Math.Max(0.15, Math.Min(1.0, _view.FovDegrees / 60.0))), out var ax, out var ay)) { x = ax; y = ay; }
            var w = r.MeasureText(name, 22f);
            r.DrawText(name, (float)x - w / 2, (float)y - 14, 22f, color);
            Place((float)x - w / 2, (float)y - 14, w, 22);
        }
    }

    private void DrawConstellationNames(UIRenderer r)
    {
        if (_view.FovDegrees < 24 || _view.FovDegrees > 170) return;
        var color = Tone(110, 150, 200, 190);
        var shown = 0;
        foreach (var c in _data.Constellations)
        {
            if (shown >= MaxConstellationLabels) break;
            var h = _view.ToHorizontal(c.Center);
            if (h.Z < -0.05f) continue; // below the horizon
            if (!_view.TryProject(h, out var x, out var y) || !OnScreen(x, y, -20)) continue;
            var text = (c.Latin ?? c.Name ?? c.Abbreviation).ToUpperInvariant();
            var w = r.MeasureText(text, 13f);
            if (!Place((float)x - w / 2, (float)y - 7, w, 14)) continue;
            r.DrawText(text, (float)x - w / 2, (float)y - 7, 13f, color);
            shown++;
        }
    }

    private void DrawStarNames(UIRenderer r)
    {
        // name the stars bright enough to stand out at this zoom: fewer when the whole sky is in view
        var limit = Math.Min(_view.MagnitudeLimit - 1.6, 4.5 + 7.0 / Math.Max(1.0, _view.FovDegrees / 8.0));
        var color = Tone(255, 235, 200, 230);
        var shown = 0;
        foreach (var s in _namedStars)
        {
            if (s.Magnitude > limit) break;
            if (shown >= MaxStarLabels) break;
            var h = _view.ToHorizontal(s.Position);
            if (h.Z < -0.02f) continue;
            if (!_view.TryProject(h, out var x, out var y) || !OnScreen(x, y, -6)) continue;
            var name = _data.StarNames[s.Hip].Name;
            var size = s.Magnitude < 1.5 ? 15f : 13f;
            var w = r.MeasureText(name, size);
            var radius = (float)(3.0 + 2.2 * Math.Max(0, 3.0 - s.Magnitude));
            if (!Place((float)x + radius + 2, (float)y - size * 0.6f, w, size + 2)) continue;
            r.DrawText(name, (float)x + radius + 2, (float)y - size * 0.6f, size, color);
            shown++;
        }
    }

    private void DrawSolarSystem(UIRenderer r)
    {
        var lat = _view.Latitude;
        var lon = _view.Longitude;
        var utc = _view.Utc;

        // planets
        foreach (var body in SolarSystemMath.Planets)
        {
            var eq = SolarSystemMath.GetPosition(body, utc);
            var aa = AstronomyMath.EquatorialToHorizontal(eq.RightAscension, eq.Declination, lat, lon, utc);
            var tint = body switch
            {
                SolarSystemObject.Mars => Tone(255, 120, 80),
                SolarSystemObject.Jupiter => Tone(255, 225, 170),
                SolarSystemObject.Saturn => Tone(255, 235, 160),
                SolarSystemObject.Venus => Tone(255, 250, 220),
                SolarSystemObject.Uranus or SolarSystemObject.Neptune => Tone(150, 220, 240),
                _ => Tone(220, 220, 210),
            };
            Marker(r, aa, SolarSystemMath.GetName(body), tint, 5f);
        }

        // the Moon, at its apparent size and as seen from here (parallax is up to a degree)
        var moonDist = LunarMath.GetMoonDistanceKm(utc);
        var moon = LunarMath.GetMoonAzAlt(lat, lon, utc);
        var moonDir = SkyView.FromAzAlt(moon.Azimuth, moon.Altitude);
        if (_view.TryProject(moonDir, out var mx, out var my) && OnScreen(mx, my, 60))
        {
            var radiusPx = (float)Math.Max(5.0, LunarMath.GetSemiDiameterDegrees(moonDist) * 60.0 * _view.PixelsPerArcminute(moonDir));
            var lit = LunarMath.GetMoonIllumination(utc) / 100.0;
            r.DrawCircleFill((float)mx, (float)my, radiusPx, Tone(70, 70, 75));
            r.DrawCircleFill((float)mx, (float)my, radiusPx * (float)(0.35 + 0.65 * lit), Tone(235, 235, 220, 235));
            Label(r, "Moon", mx, my, radiusPx, Tone(235, 235, 220));
        }

        // the Sun: shown so it can be avoided, never as a target
        var sun = SolarSystemMath.GetPosition(SolarSystemObject.Sun, utc);
        var sunAa = AstronomyMath.EquatorialToHorizontal(sun.RightAscension, sun.Declination, lat, lon, utc);
        var sunDir = SkyView.FromAzAlt(sunAa.Azimuth, sunAa.Altitude);
        if (_view.TryProject(sunDir, out var sx, out var sy) && OnScreen(sx, sy, 60))
        {
            var radiusPx = (float)Math.Max(7.0, 16.0 * _view.PixelsPerArcminute(sunDir));
            r.DrawCircleFill((float)sx, (float)sy, radiusPx, Tone(255, 210, 60));
            r.DrawRoundedRing((float)sx - radiusPx * 1.9f, (float)sy - radiusPx * 1.9f, radiusPx * 3.8f, radiusPx * 3.8f, radiusPx * 1.9f, 2f, Tone(255, 80, 40));
            Label(r, "Sun - never point the telescope here", sx, sy, radiusPx * 1.9f, Tone(255, 120, 70));
        }
    }

    private void DrawSelected(UIRenderer r)
    {
        if (Selected is not { } o || !_view.TryProject(o.Direction, out var x, out var y) || !OnScreen(x, y, 40)) return;
        const float radius = 17f;
        var color = Tone(255, 255, 255, 235);
        r.DrawRoundedRing((float)x - radius, (float)y - radius, radius * 2, radius * 2, radius, 2f, color);
    }

    /// <summary>The telescope's aim: a ring with a gap at each side, so the thing it points at stays visible in the middle.</summary>
    private void DrawTelescope(UIRenderer r)
    {
        if (TelescopePointing is not { } p || !_view.TryProject(SkyView.FromAzAlt(p.Azimuth, p.Altitude), out var x, out var y) || !OnScreen(x, y, 40)) return;
        var color = Tone(90, 255, 120, 245);
        const float radius = 15f;
        r.DrawRoundedRing((float)x - radius, (float)y - radius, radius * 2, radius * 2, radius, 2f, color);
        float fx = (float)x, fy = (float)y;
        r.DrawRect(fx - radius - 9, fy - 1, 7, 2, color);
        r.DrawRect(fx + radius + 2, fy - 1, 7, 2, color);
        r.DrawRect(fx - 1, fy - radius - 9, 2, 7, color);
        r.DrawRect(fx - 1, fy + radius + 2, 2, 7, color);
    }

    /// <summary>A diamond: the phone's idea of where it points, from its own sensors.</summary>
    private void DrawPhone(UIRenderer r)
    {
        if (PhonePointing is not { } p || !_view.TryProject(SkyView.FromAzAlt(p.Azimuth, p.Altitude), out var x, out var y) || !OnScreen(x, y, 40)) return;
        var color = Tone(120, 200, 255, 240);
        float fx = (float)x, fy = (float)y;
        for (int i = -9; i <= 9; i++)
        {
            int w = 9 - Math.Abs(i);
            r.DrawRect(fx - w, fy + i, 1.6f, 1.6f, color);
            r.DrawRect(fx + w, fy + i, 1.6f, 1.6f, color);
        }
        r.DrawText("phone", fx + 14, fy - 7, 13f, color);
    }

    private void Marker(UIRenderer r, AzAltCoordinates aa, string name, Color color, float radius)
    {
        if (aa.Altitude < -2) return;
        var dir = SkyView.FromAzAlt(aa.Azimuth, aa.Altitude);
        if (!_view.TryProject(dir, out var x, out var y) || !OnScreen(x, y, 30)) return;
        r.DrawCircleFill((float)x, (float)y, radius, color);
        Label(r, name, x, y, radius, color);
    }

    private void Label(UIRenderer r, string text, double x, double y, float radius, Color color)
    {
        const float size = 14f;
        var w = r.MeasureText(text, size);
        var lx = (float)x + radius + 4;
        var ly = (float)y - size * 0.6f;
        if (Place(lx, ly, w, size + 2)) r.DrawText(text, lx, ly, size, color);
    }
}
