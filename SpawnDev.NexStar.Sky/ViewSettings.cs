using System.Text.Json;


namespace SpawnDev.NexStar.Sky;

/// <summary>What the person chose to see: the sky layers, the names, night vision and the star size. Kept in the browser between visits.</summary>
public sealed class ViewSettings
{
    public SkyLayers Layers { get; set; } = SkyLayers.Default;
    public bool StarNames { get; set; } = true;
    public bool ConstellationNames { get; set; } = true;
    public bool SolarSystem { get; set; } = true;
    public bool Night { get; set; } = true;
    public float StarSize { get; set; } = 1.0f;

    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>Restores saved settings; anything unreadable or out of range gives the defaults (or the nearest allowed value).</summary>
    public static ViewSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ViewSettings();
        try
        {
            var s = JsonSerializer.Deserialize<ViewSettings>(json);
            if (s == null) return new ViewSettings();
            // only the known layer bits
            s.Layers &= SkyLayers.AltAzGrid | SkyLayers.EquatorialGrid | SkyLayers.GroundAndHorizon | SkyLayers.Stars | SkyLayers.ConstellationLines | SkyLayers.ConstellationBoundaries | SkyLayers.DeepSky;
            s.StarSize = float.IsFinite(s.StarSize) ? Math.Clamp(s.StarSize, 0.5f, 2.5f) : 1.0f;
            return s;
        }
        catch (JsonException)
        {
            return new ViewSettings();
        }
    }
}
