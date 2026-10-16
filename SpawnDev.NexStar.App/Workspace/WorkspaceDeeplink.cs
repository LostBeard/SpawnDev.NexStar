using System.Globalization;
using SpawnDev.NexStar.Sky;

namespace SpawnDev.NexStar.App.Workspace;

/// <summary>
/// A link that opens the workspace looking at a given place and time: <c>/workspace?az=250&amp;alt=20&amp;fov=30&amp;lat=40.7&amp;lon=-74&amp;utc=2026-01-15T03:00:00Z</c>.
/// For sharing a view and for checking the chart against a known sky; every setting a person needs is in the workspace itself.
/// </summary>
public sealed class WorkspaceDeeplink
{
    public double? Azimuth { get; init; }
    public double? Altitude { get; init; }
    public double? Fov { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public DateTime? Utc { get; init; }
    public bool? Night { get; init; }
    public SkyLayers? Layers { get; init; }

    /// <summary>?sim=1 connects a simulated telescope instead of a real one: to try the workspace (and to check it) with nothing attached.</summary>
    public bool Simulate { get; init; }

    public static WorkspaceDeeplink Parse(string uri)
    {
        var q = uri.IndexOf('?');
        if (q < 0) return new WorkspaceDeeplink();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in uri[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            values[Uri.UnescapeDataString(parts[0])] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
        }
        double? Num(string key) => values.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        DateTime? when = values.TryGetValue("utc", out var u) && DateTime.TryParse(u, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
        SkyLayers? layers = values.TryGetValue("layers", out var l) && int.TryParse(l, out var bits) ? (SkyLayers)bits : null;
        return new WorkspaceDeeplink
        {
            Azimuth = Num("az"), Altitude = Num("alt"), Fov = Num("fov"),
            Latitude = Num("lat"), Longitude = Num("lon"),
            Utc = when,
            Night = values.TryGetValue("night", out var n) ? n != "0" : null,
            Layers = layers,
            Simulate = values.TryGetValue("sim", out var sim) && sim != "0",
        };
    }
}
