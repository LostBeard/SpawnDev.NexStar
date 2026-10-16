using SGPdotNET.TLE;
using SpawnDev.NexStar.App.Services;
using static SpawnDev.NexStar.App.Services.PlaneService;

namespace SpawnDev.NexStar.App.Services;

/// <summary>
/// A unified sky object for display on the overhead map
/// </summary>
public class SkyObject
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = ""; // Satellite, Plane, Planet, Star, DSO
    public string Subcategory { get; set; } = ""; // e.g., "Space Station", "Starlink", "Messier"
    public double Azimuth { get; set; }
    public double Altitude { get; set; } // elevation angle above horizon
    public double? Magnitude { get; set; }
    public double? Distance { get; set; } // km for satellites/planes, light years for stars/DSOs
    public string? ExtraInfo { get; set; } // Additional info for tooltip
    
    // For tracking
    public double? Ra { get; set; }
    public double? Dec { get; set; }
    
    // Source reference
    public object? SourceObject { get; set; }
}

/// <summary>
/// Aggregates sky objects from all sources for the overhead view.
/// Each source updates independently to avoid slow sources blocking fast ones.
/// </summary>
public class OverheadService
{
    private readonly SatelliteService SatelliteService;
    private readonly PlaneService PlaneService;
    private readonly LocationService LocationService;

    // Cached data per source - updated independently
    private List<SkyObject> _satellites = new();
    private List<SkyObject> _planes = new();
    private List<SkyObject> _planets = new();
    private List<SkyObject> _moon = new();
    private List<SkyObject> _stars = new();
    private List<SkyObject> _dsos = new();

    // Last update times per source
    private DateTime _lastSatelliteUpdate = DateTime.MinValue;
    private DateTime _lastPlaneUpdate = DateTime.MinValue;
    private DateTime _lastCelestialUpdate = DateTime.MinValue;

    // Update intervals (satellites and celestial can update faster, planes have API rate limits)
    private readonly TimeSpan SatelliteUpdateInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan PlaneUpdateInterval = TimeSpan.FromSeconds(30);
    private readonly TimeSpan CelestialUpdateInterval = TimeSpan.FromSeconds(10);

    // Event to notify when any source updates
    public event Action? OnDataUpdated;

    public OverheadService(SatelliteService satelliteService, PlaneService planeService, LocationService locationService)
    {
        SatelliteService = satelliteService;
        PlaneService = planeService;
        LocationService = locationService;
    }

    /// <summary>
    /// Filter settings for what to display
    /// </summary>
    public class FilterSettings
    {
        public bool ShowSatellites { get; set; } = true;
        public bool ShowPlanes { get; set; } = true;
        public bool ShowPlanets { get; set; } = true;
        public bool ShowMoon { get; set; } = true;
        public bool ShowStars { get; set; } = true;
        public bool ShowDSOs { get; set; } = true;
        
        // Subcategory filters
        public HashSet<string> SatelliteCategories { get; set; } = new() { "stations", "visual" };
    }

    /// <summary>
    /// Refresh all sources that are due for update (non-blocking, fire-and-forget)
    /// </summary>
    public async Task RefreshSourcesAsync(FilterSettings filters)
    {
        var location = LocationService.Location;
        if (location == null) return;

        var now = DateTime.UtcNow;
        var tasks = new List<Task>();

        // Satellites - update if interval elapsed
        if (filters.ShowSatellites && (now - _lastSatelliteUpdate) > SatelliteUpdateInterval)
        {
            tasks.Add(RefreshSatellitesAsync(filters, location));
        }

        // Planes - update if interval elapsed
        if (filters.ShowPlanes && (now - _lastPlaneUpdate) > PlaneUpdateInterval)
        {
            tasks.Add(RefreshPlanesAsync(location));
        }

        // Celestial objects (stars, DSOs, planets, moon) - update if interval elapsed
        if ((filters.ShowStars || filters.ShowDSOs || filters.ShowPlanets || filters.ShowMoon) && 
            (now - _lastCelestialUpdate) > CelestialUpdateInterval)
        {
            tasks.Add(RefreshCelestialAsync(filters, location));
        }

        // Wait for all concurrent updates
        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
        }
    }

    private async Task RefreshSatellitesAsync(FilterSettings filters, GeoLocation location)
    {
        try
        {
            var satellites = new List<SkyObject>();
            foreach (var category in filters.SatelliteCategories)
            {
                var tles = await SatelliteService.GetTlesAsync(category);
                foreach (var tle in tles)
                {
                    var pos = SatelliteService.CalculatePosition(tle, location);
                    if (pos != null && pos.TopocentricAltitude > 0)
                    {
                        satellites.Add(new SkyObject
                        {
                            Id = $"sat_{tle.Name}",
                            Name = tle.Name,
                            Category = "Satellite",
                            Subcategory = GetSatelliteSubcategory(category),
                            Azimuth = pos.TopocentricAzimuth,
                            Altitude = pos.TopocentricAltitude,
                            Distance = pos.RangeKm,
                            ExtraInfo = $"Range: {pos.RangeKm:F0} km",
                            SourceObject = tle
                        });
                    }
                }
            }
            _satellites = satellites;
            _lastSatelliteUpdate = DateTime.UtcNow;
            OnDataUpdated?.Invoke();
        }
        catch { /* Ignore errors, keep old data */ }
    }

    private async Task RefreshPlanesAsync(GeoLocation location)
    {
        try
        {
            var planes = new List<SkyObject>();
            var planeData = await PlaneService.GetNearbyAircraftAsync(location);
            foreach (var plane in planeData)
            {
                if (plane.Altitude.HasValue && plane.Altitude > 0 && plane.Azimuth.HasValue)
                {
                    planes.Add(new SkyObject
                    {
                        Id = $"plane_{plane.Icao24}",
                        Name = plane.Callsign ?? plane.Icao24,
                        Category = "Plane",
                        Subcategory = plane.OriginCountry ?? "Unknown",
                        Azimuth = plane.Azimuth.Value,
                        Altitude = plane.Altitude.Value,
                        Distance = plane.DistanceKm,
                        ExtraInfo = $"Alt: {plane.BaroAltitude:F0}m, Speed: {(plane.Velocity ?? 0) * 3.6:F0} km/h",
                        SourceObject = plane
                    });
                }
            }
            _planes = planes;
            _lastPlaneUpdate = DateTime.UtcNow;
            OnDataUpdated?.Invoke();
        }
        catch { /* Ignore errors, keep old data */ }
    }

    private Task RefreshCelestialAsync(FilterSettings filters, GeoLocation location)
    {
        var now = DateTime.UtcNow;

        if (filters.ShowStars)
        {
            _stars = GetVisibleStars(location, now);
        }
        
        if (filters.ShowDSOs)
        {
            _dsos = GetVisibleDSOs(location, now);
        }
        
        if (filters.ShowPlanets)
        {
            _planets = GetPlanets(location, now);
        }

        if (filters.ShowMoon)
        {
            _moon = GetMoon(location, now);
        }

        _lastCelestialUpdate = DateTime.UtcNow;
        OnDataUpdated?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Get all visible sky objects from cached data (instant, no async)
    /// </summary>
    public List<SkyObject> GetVisibleObjects(FilterSettings filters)
    {
        var objects = new List<SkyObject>();

        if (filters.ShowSatellites)
            objects.AddRange(_satellites);
        
        if (filters.ShowPlanes)
            objects.AddRange(_planes);
        
        if (filters.ShowPlanets)
            objects.AddRange(_planets);
        
        if (filters.ShowMoon)
            objects.AddRange(_moon);
        
        if (filters.ShowStars)
            objects.AddRange(_stars);
        
        if (filters.ShowDSOs)
            objects.AddRange(_dsos);

        return objects;
    }

    /// <summary>
    /// Get all visible sky objects (async - triggers refresh if needed, returns cached data)
    /// </summary>
    public async Task<List<SkyObject>> GetVisibleObjectsAsync(FilterSettings filters)
    {
        // Start refreshing sources that need it (non-blocking for slow sources)
        _ = RefreshSourcesAsync(filters);
        
        // Return current cached data immediately
        return GetVisibleObjects(filters);
    }

    private string GetSatelliteSubcategory(string category) => category switch
    {
        "stations" => "Space Station",
        "starlink" => "Starlink",
        "visual" => "Bright",
        "weather" => "Weather",
        "gps-ops" => "GPS",
        _ => category
    };

    private List<SkyObject> GetPlanets(GeoLocation location, DateTime utc)
    {
        var planets = new List<SkyObject>();

        foreach (var planet in SolarSystemMath.Planets)
        {
            var altAz = SolarSystemMath.GetAzAlt(planet, location.Latitude, location.Longitude, utc);
            if (altAz.Altitude > 0) // Above horizon
            {
                var raDec = SolarSystemMath.GetPosition(planet, utc);
                planets.Add(new SkyObject
                {
                    Id = $"planet_{planet}",
                    Name = SolarSystemMath.GetName(planet),
                    Category = "Planet",
                    Subcategory = "Planet",
                    Azimuth = altAz.Azimuth,
                    Altitude = altAz.Altitude,
                    Ra = raDec.RightAscension,
                    Dec = raDec.Declination,
                    ExtraInfo = $"Az: {altAz.Azimuth:F1}°, Alt: {altAz.Altitude:F1}°",
                    SourceObject = planet
                });
            }
        }

        return planets;
    }

    private List<SkyObject> GetMoon(GeoLocation location, DateTime utc)
    {
        var moon = new List<SkyObject>();
        
        var altAz = LunarMath.GetMoonAzAlt(location.Latitude, location.Longitude, utc);
        if (altAz.Altitude > 0) // Above horizon
        {
            var raDec = LunarMath.GetMoonPosition(utc);
            var phaseName = LunarMath.GetMoonPhaseName(utc);
            var illumination = LunarMath.GetMoonIllumination(utc);
            
            moon.Add(new SkyObject
            {
                Id = "moon",
                Name = "Moon",
                Category = "Moon",
                Subcategory = phaseName,
                Azimuth = altAz.Azimuth,
                Altitude = altAz.Altitude,
                Ra = raDec.RightAscension,
                Dec = raDec.Declination,
                ExtraInfo = $"{phaseName}, {illumination:F0}% illuminated",
                SourceObject = null
            });
        }

        return moon;
    }

    private List<SkyObject> GetVisibleStars(GeoLocation location, DateTime utc)
    {
        var stars = new List<SkyObject>();

        foreach (var star in CelestialCatalogs.AlignmentStars)
        {
            var altAz = AstronomyMath.EquatorialToHorizontal(
                star.RightAscension, star.Declination, location.Latitude, location.Longitude, utc);
            if (altAz.Altitude > 5)
            {
                stars.Add(new SkyObject
                {
                    Id = $"star_{star.Name}",
                    Name = star.Name,
                    Category = "Star",
                    Subcategory = star.Constellation,
                    Azimuth = altAz.Azimuth,
                    Altitude = altAz.Altitude,
                    Magnitude = star.Magnitude,
                    Ra = star.RightAscension,
                    Dec = star.Declination,
                    ExtraInfo = $"Mag: {star.Magnitude:F1}, {star.Constellation}",
                    SourceObject = star
                });
            }
        }
        return stars;
    }

    private List<SkyObject> GetVisibleDSOs(GeoLocation location, DateTime utc)
    {
        var dsos = new List<SkyObject>();

        foreach (var m in CelestialCatalogs.Messier)
        {
            var altAz = AstronomyMath.EquatorialToHorizontal(
                m.RightAscension, m.Declination, location.Latitude, location.Longitude, utc);
            if (altAz.Altitude > 10)
            {
                dsos.Add(new SkyObject
                {
                    Id = $"dso_{m.Id}",
                    Name = string.IsNullOrEmpty(m.Name) ? m.Id : $"{m.Id} {m.Name}",
                    Category = "DSO",
                    Subcategory = m.ObjectType,
                    Azimuth = altAz.Azimuth,
                    Altitude = altAz.Altitude,
                    Magnitude = m.Magnitude,
                    Ra = m.RightAscension,
                    Dec = m.Declination,
                    ExtraInfo = $"{m.ObjectType}, Mag: {m.Magnitude:F1}",
                    SourceObject = m
                });
            }
        }
        return dsos;
    }
}
