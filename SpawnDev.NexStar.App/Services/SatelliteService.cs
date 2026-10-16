using SGPdotNET.CoordinateSystem;
using SGPdotNET.Observation;
using SGPdotNET.Propagation;
using SGPdotNET.TLE;
using SGPdotNET.Util;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using System.Text.Json;

namespace SpawnDev.NexStar.App.Services;

public class SatelliteService
{
    private readonly HttpClient Http;
    private readonly SpawnJSRuntime JS;
    // Use JS.Get<Window>("window") as SpawnJSRuntime might not have Window property directly or it's an extension
    private Storage? LocalStorage
    {
        get
        {
            try { return JS.Get<Window>("window").LocalStorage; }
            catch { return null; }
        }
    }

    public SatelliteService(HttpClient http, SpawnJSRuntime js)
    {
        Http = http;
        JS = js;
    }

    public record SatelliteCategory(string Name, string UrlKey);

    public List<SatelliteCategory> Categories { get; } = new()
    {
        new("Space Stations", "stations"),
        new("Brightest", "visual"),
        new("Starlink", "starlink"),
        new("Weather", "weather"),
        new("NOAA", "noaa"),
        new("GOES", "goes"),
        new("GPS-OPS", "gps-ops"),
    };

    public class CachedTleGroup
    {
        public DateTime LastUpdated { get; set; }
        public List<string> TleLines { get; set; } = new();
    }

    /// <summary>
    /// Fetches TLEs for a given category key (e.g. "stations").
    /// Tries online first, updates cache, falls back to cache if offline.
    /// </summary>
    public async Task<List<Tle>> GetTlesAsync(string categoryKey)
    {
        var cacheKey = $"tle_{categoryKey}";
        string? rawData = null;

        try
        {
            // Try fetching from CelesTrak
            var url = $"https://celestrak.org/NORAD/elements/gp.php?GROUP={categoryKey}&FORMAT=tle";
            rawData = await Http.GetStringAsync(url);
            
            // Cache if successful
            if (!string.IsNullOrWhiteSpace(rawData))
            {
                var lines = rawData.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                var cacheEntry = new CachedTleGroup
                {
                    LastUpdated = DateTime.UtcNow,
                    TleLines = lines
                };
                LocalStorage?.SetItem(cacheKey, JsonSerializer.Serialize(cacheEntry));
            }
        }
        catch (Exception)
        {
            // Ignore network errors, fall back to cache
        }

        // If no rawData (failed fetch), try cache
        if (string.IsNullOrWhiteSpace(rawData))
        {
            var cachedJson = LocalStorage?.GetItem(cacheKey);
            if (!string.IsNullOrEmpty(cachedJson))
            {
                try
                {
                    var cacheEntry = JsonSerializer.Deserialize<CachedTleGroup>(cachedJson);
                    if (cacheEntry != null && cacheEntry.TleLines.Count > 0)
                    {
                        rawData = string.Join("\n", cacheEntry.TleLines);
                    }
                }
                catch { /* corrupted cache */ }
            }
        }

        if (string.IsNullOrWhiteSpace(rawData))
        {
            return new List<Tle>();
        }

        // Parse TLEs
        var tles = new List<Tle>();
        var allLines = rawData.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        
        // CelesTrak GP TLE format is 3 lines: Name, Line1, Line2
        for (int i = 0; i < allLines.Length; i += 3)
        {
            if (i + 2 < allLines.Length)
            {
                try
                {
                    var name = allLines[i].Trim();
                    var l1 = allLines[i + 1].Trim();
                    var l2 = allLines[i + 2].Trim();
                    var tle = new Tle(name, l1, l2);
                    tles.Add(tle);
                }
                catch { /* skip bad TLE */ }
            }
        }

        return tles;
    }

    public record SatellitePosition(double TopocentricAzimuth, double TopocentricAltitude, double RangeKm, double RateRa, double RateDec);

    /// <summary>
    /// Calculates the current position of the satellite for the observer.
    /// </summary>
    public SatellitePosition? CalculatePosition(Tle tle, GeoLocation location)
    {
        if (location == null) return null;

        try
        {
            // Attempt to use Satellite class if Sgp4 is not direct
            var sat = new Satellite(tle);
            // Use GeodeticCoordinate instead of abstract Coordinate
            // Altitude 0 for observer (or use actual if known? NexStar Location usually has no alt)
            var groundStation = new GroundStation(new GeodeticCoordinate(Angle.FromDegrees(location.Latitude), Angle.FromDegrees(location.Longitude), 0));
            
            // SGP.NET uses UTC
            var now = DateTime.UtcNow;
            var topo = groundStation.Observe(sat, now);

            return new SatellitePosition(
                topo.Azimuth.Degrees, 
                topo.Elevation.Degrees, 
                topo.Range, 
                0, // Rates not calculated yet
                0
            );
        }
        catch (Exception ex)
        {
            // Debug failure
            Console.WriteLine($"CalcPos Error: {ex.Message}");
            return null;
        }
    }
}
