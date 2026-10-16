using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using System.Text.Json;

namespace SpawnDev.NexStar.App.Services;

/// <summary>
/// Service for fetching nearby aircraft data from OpenSky Network.
/// Anonymous access: 400 requests/day with caching fallback.
/// </summary>
public class PlaneService
{
    private readonly HttpClient Http;
    private readonly SpawnJSRuntime JS;
    
    private Storage? LocalStorage
    {
        get
        {
            try { return JS.Get<Window>("window").LocalStorage; }
            catch { return null; }
        }
    }

    public PlaneService(HttpClient http, SpawnJSRuntime js)
    {
        Http = http;
        JS = js;
    }

    private const string CacheKey = "planes_cache";

    /// <summary>
    /// Represents an aircraft state.
    /// </summary>
    public class Aircraft
    {
        public string Icao24 { get; set; } = "";
        public string? Callsign { get; set; }
        public string? OriginCountry { get; set; }
        public double? Longitude { get; set; }
        public double? Latitude { get; set; }
        public double? BaroAltitude { get; set; } // meters
        public double? GeoAltitude { get; set; } // meters
        public bool OnGround { get; set; }
        public double? Velocity { get; set; } // m/s
        public double? TrueTrack { get; set; } // degrees clockwise from north
        public double? VerticalRate { get; set; } // m/s

        // Calculated fields
        public double? Azimuth { get; set; }
        public double? Altitude { get; set; } // look angle, not flight altitude
        public double? DistanceKm { get; set; }
    }

    public class CachedPlaneData
    {
        public DateTime LastUpdated { get; set; }
        public List<Aircraft> Aircraft { get; set; } = new();
    }

    /// <summary>
    /// Fetches nearby aircraft from OpenSky Network API.
    /// Falls back to cache if offline or rate limited.
    /// </summary>
    public async Task<List<Aircraft>> GetNearbyAircraftAsync(GeoLocation location, double radiusDegrees = 1.5)
    {
        if (location == null) return new List<Aircraft>();

        List<Aircraft>? aircraft = null;

        try
        {
            var lamin = location.Latitude - radiusDegrees;
            var lamax = location.Latitude + radiusDegrees;
            var lomin = location.Longitude - radiusDegrees;
            var lomax = location.Longitude + radiusDegrees;

            // OpenSky Network API (anonymous, 400 req/day)
            var url = $"https://opensky-network.org/api/states/all?lamin={lamin}&lamax={lamax}&lomin={lomin}&lomax={lomax}";
            var response = await Http.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                aircraft = ParseOpenSkyResponse(json);

                // Cache if successful
                if (aircraft != null && aircraft.Count > 0)
                {
                    CacheData(aircraft);
                }
            }
            else
            {
                Console.WriteLine($"OpenSky: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PlaneService error: {ex.Message}");
        }

        // Fall back to cache if API fails
        if (aircraft == null || aircraft.Count == 0)
        {
            aircraft = LoadFromCache();
        }

        // Calculate look angles
        if (aircraft != null)
        {
            foreach (var plane in aircraft)
            {
                CalculateLookAngle(plane, location);
            }
        }

        return aircraft ?? new List<Aircraft>();
    }

    /// <summary>
    /// Parse OpenSky Network API response
    /// </summary>
    private List<Aircraft> ParseOpenSkyResponse(string json)
    {
        var result = new List<Aircraft>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("states", out var states) && states.ValueKind == JsonValueKind.Array)
            {
                foreach (var state in states.EnumerateArray())
                {
                    if (state.ValueKind != JsonValueKind.Array) continue;
                    var arr = state.EnumerateArray().ToArray();
                    if (arr.Length < 12) continue;

                    var aircraft = new Aircraft
                    {
                        Icao24 = arr[0].GetString() ?? "",
                        Callsign = arr[1].GetString()?.Trim(),
                        OriginCountry = arr[2].GetString(),
                        Longitude = arr[5].ValueKind == JsonValueKind.Number ? arr[5].GetDouble() : null,
                        Latitude = arr[6].ValueKind == JsonValueKind.Number ? arr[6].GetDouble() : null,
                        BaroAltitude = arr[7].ValueKind == JsonValueKind.Number ? arr[7].GetDouble() : null,
                        OnGround = arr[8].ValueKind == JsonValueKind.True,
                        Velocity = arr[9].ValueKind == JsonValueKind.Number ? arr[9].GetDouble() : null,
                        TrueTrack = arr[10].ValueKind == JsonValueKind.Number ? arr[10].GetDouble() : null,
                        VerticalRate = arr[11].ValueKind == JsonValueKind.Number ? arr[11].GetDouble() : null,
                        GeoAltitude = arr.Length > 13 && arr[13].ValueKind == JsonValueKind.Number ? arr[13].GetDouble() : null
                    };

                    if (!aircraft.OnGround && aircraft.Latitude.HasValue && aircraft.Longitude.HasValue)
                    {
                        result.Add(aircraft);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ParseOpenSky error: {ex.Message}");
        }
        return result;
    }

    private void CacheData(List<Aircraft> aircraft)
    {
        try
        {
            var cacheEntry = new CachedPlaneData
            {
                LastUpdated = DateTime.UtcNow,
                Aircraft = aircraft
            };
            LocalStorage?.SetItem(CacheKey, JsonSerializer.Serialize(cacheEntry));
        }
        catch { }
    }

    private List<Aircraft>? LoadFromCache()
    {
        try
        {
            var cachedJson = LocalStorage?.GetItem(CacheKey);
            if (!string.IsNullOrEmpty(cachedJson))
            {
                var cacheEntry = JsonSerializer.Deserialize<CachedPlaneData>(cachedJson);
                return cacheEntry?.Aircraft;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Calculates the look angle (Azimuth/Altitude) from observer to aircraft.
    /// </summary>
    private void CalculateLookAngle(Aircraft plane, GeoLocation observer)
    {
        if (!plane.Latitude.HasValue || !plane.Longitude.HasValue) return;

        double lat1 = observer.Latitude * Math.PI / 180;
        double lon1 = observer.Longitude * Math.PI / 180;
        double lat2 = plane.Latitude.Value * Math.PI / 180;
        double lon2 = plane.Longitude.Value * Math.PI / 180;
        double dLon = lon2 - lon1;

        const double R = 6371.0;

        double a = Math.Sin((lat2 - lat1) / 2) * Math.Sin((lat2 - lat1) / 2) +
                   Math.Cos(lat1) * Math.Cos(lat2) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        double groundDistance = R * c;

        plane.DistanceKm = groundDistance;

        double y = Math.Sin(dLon) * Math.Cos(lat2);
        double x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);
        double azimuth = Math.Atan2(y, x) * 180 / Math.PI;
        plane.Azimuth = (azimuth + 360) % 360;

        double altitudeM = plane.GeoAltitude ?? plane.BaroAltitude ?? 10000;
        double altitudeKm = altitudeM / 1000.0;
        
        if (groundDistance > 0.01)
        {
            plane.Altitude = Math.Atan2(altitudeKm, groundDistance) * 180 / Math.PI;
        }
        else
        {
            plane.Altitude = 90;
        }
    }
}

