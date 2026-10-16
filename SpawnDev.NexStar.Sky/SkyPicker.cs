using System.Numerics;

namespace SpawnDev.NexStar.Sky;

public enum SkyObjectKind { Star, DeepSky, Planet, Moon, Sun }

/// <summary>
/// An object on the chart, picked by the pointer or chosen from a list: what it is, where it is now, and how bright.
/// </summary>
/// <param name="Name">The label: "Vega", "M31 Andromeda Galaxy", "Jupiter".</param>
/// <param name="Detail">Type and constellation: "Star, Lyra", "Galaxy, Andromeda".</param>
/// <param name="Azimuth">Degrees, true, at the chart's time and site</param>
/// <param name="Altitude">Degrees, true (no refraction)</param>
/// <param name="Magnitude">Visual magnitude when known</param>
/// <param name="Body">The planet, for <see cref="SkyObjectKind.Planet"/></param>
/// <param name="CatalogNumber">Hipparcos number of a star, 0 otherwise</param>
/// <param name="J2000">The fixed position of a star or deep-sky object (null for the Moon, Sun and planets, which move)</param>
public sealed record SkyObject(SkyObjectKind Kind, string Name, string? Detail, double Azimuth, double Altitude, double? Magnitude, SolarSystemObject? Body = null, uint CatalogNumber = 0, Vector3? J2000 = null)
{
    /// <summary>The Sun must never be a target: pointing a telescope at it blinds and burns.</summary>
    public bool CanGoto => Kind != SkyObjectKind.Sun;

    public Vector3 Direction => SkyView.FromAzAlt(Azimuth, Altitude);
}

/// <summary>Finds the object under a point on the chart.</summary>
public static class SkyPicker
{
    /// <summary>The solar system as it is at the view's time and site: the Sun, the Moon and the planets.</summary>
    public static List<SkyObject> SolarSystem(SkyView view)
    {
        var list = new List<SkyObject>(10);
        var lat = view.Latitude; var lon = view.Longitude; var utc = view.Utc;
        foreach (var body in SolarSystemMath.Planets)
        {
            var aa = SolarSystemMath.GetAzAlt(body, lat, lon, utc);
            list.Add(new SkyObject(SkyObjectKind.Planet, SolarSystemMath.GetName(body), "Planet", aa.Azimuth, aa.Altitude, null, body));
        }
        var moon = LunarMath.GetMoonAzAlt(lat, lon, utc);
        list.Add(new SkyObject(SkyObjectKind.Moon, "Moon", $"Moon, {LunarMath.GetMoonIllumination(utc):F0}% lit", moon.Azimuth, moon.Altitude, -12.7));
        var sun = SolarSystemMath.GetAzAlt(SolarSystemObject.Sun, lat, lon, utc);
        list.Add(new SkyObject(SkyObjectKind.Sun, "Sun", "Never point the telescope here", sun.Azimuth, sun.Altitude, -26.7, SolarSystemObject.Sun));
        return list;
    }

    /// <summary>
    /// The same object where it is at the view's time: the sky turns, and the Moon and planets move, so a selection made a minute ago is not where it was.
    /// <paramref name="solarSystem"/> is <see cref="SolarSystem"/> for the same view, passed in so a caller refreshing several objects computes it once.
    /// </summary>
    public static SkyObject Refresh(SkyObject o, SkyView view, IReadOnlyList<SkyObject> solarSystem)
    {
        if (o.J2000 is Vector3 fixedPosition)
        {
            var (az, alt) = SkyView.ToAzAlt(view.ToHorizontal(fixedPosition));
            return o with { Azimuth = az, Altitude = alt };
        }
        foreach (var s in solarSystem)
            if (s.Kind == o.Kind && s.Name == o.Name) return s;
        return o;
    }

    /// <summary>
    /// Where the object is, as a function of time, for a tracker: true azimuth and altitude at a site. A star or deep-sky object stays where it is on the
    /// celestial sphere and the sky turns under it; the Moon and planets also move among the stars. The Sun is not a target.
    /// </summary>
    public static SpawnDev.NexStar.TargetFunction TargetFor(SkyObject o, double latitude, double longitude)
    {
        if (o.Kind == SkyObjectKind.Sun) throw new ArgumentException("the Sun is never a target", nameof(o));
        if (o.J2000 is Vector3 fixedPosition)
        {
            var view = new SkyView { Latitude = latitude, Longitude = longitude, WidthPx = 100, HeightPx = 100 };
            return utc =>
            {
                lock (view)
                {
                    view.Utc = utc;
                    view.Update();
                    return SkyView.ToAzAlt(view.ToHorizontal(fixedPosition));
                }
            };
        }
        if (o.Kind == SkyObjectKind.Moon)
            return utc => { var m = LunarMath.GetMoonAzAlt(latitude, longitude, utc); return (m.Azimuth, m.Altitude); };
        if (o.Body is SolarSystemObject body)
            return utc => { var p = SolarSystemMath.GetAzAlt(body, latitude, longitude, utc); return (p.Azimuth, p.Altitude); };
        throw new ArgumentException("this object has no position to follow", nameof(o));
    }

    /// <summary>The label for a deep-sky object's type.</summary>
    public static string TypeName(DeepSkyType type) => type switch
    {
        DeepSkyType.Galaxy => "Galaxy",
        DeepSkyType.GalaxyGroup => "Galaxy group",
        DeepSkyType.GlobularCluster => "Globular cluster",
        DeepSkyType.OpenCluster => "Open cluster",
        DeepSkyType.Association => "Stellar association",
        DeepSkyType.EmissionNebula => "Emission nebula",
        DeepSkyType.ReflectionNebula => "Reflection nebula",
        DeepSkyType.DarkNebula => "Dark nebula",
        DeepSkyType.PlanetaryNebula => "Planetary nebula",
        DeepSkyType.SupernovaRemnant => "Supernova remnant",
        DeepSkyType.ClusterWithNebula => "Cluster with nebula",
        DeepSkyType.Nebula => "Nebula",
        DeepSkyType.Nova => "Nova",
        _ => "Deep-sky object",
    };

    /// <summary>
    /// The object nearest the point (<paramref name="sx"/>, <paramref name="sy"/>) within <paramref name="radiusPx"/> pixels, or null. Brighter
    /// objects win ties, so a tap near a bright star and a faint one beside it picks the bright one; a deep-sky object counts as hit anywhere inside its
    /// own outline as well.
    /// </summary>
    public static SkyObject? Pick(SkyView view, double sx, double sy, double radiusPx, IReadOnlyList<CatalogStar> stars, IReadOnlyDictionary<uint, StarName> starNames,
        IReadOnlyList<DeepSkyObject> deepSky, IReadOnlyList<DeepSkyName> deepSkyNames, IReadOnlyList<SkyObject> solarSystem)
    {
        SkyObject? best = null;
        double bestScore = double.MaxValue;

        void Consider(SkyObject candidate, double distancePx, double magnitude)
        {
            // each magnitude brighter makes the object count as 30% closer, up to magnitude -2
            var score = distancePx / (1.0 + 0.3 * Math.Clamp(6.5 - magnitude, 0, 8.5));
            if (score < bestScore) { bestScore = score; best = candidate; }
        }

        foreach (var o in solarSystem)
        {
            var h = o.Direction;
            if (!view.TryProject(h, out var px, out var py)) continue;
            var d = Math.Sqrt((px - sx) * (px - sx) + (py - sy) * (py - sy));
            var reach = radiusPx;
            if (o.Kind == SkyObjectKind.Moon || o.Kind == SkyObjectKind.Sun)
                reach = Math.Max(reach, (o.Kind == SkyObjectKind.Sun ? 16.0 : 15.5) * view.PixelsPerArcminute(h));
            if (d <= reach) Consider(o, d, o.Magnitude ?? 0);
        }

        for (int i = 0; i < stars.Count; i++)
        {
            var s = stars[i];
            // the catalog is sorted brightest first: past the limit the chart shows nothing, so nothing else can be picked
            if (s.Magnitude > view.MagnitudeLimit) break;
            if (!view.TryProject(view.ToHorizontal(s.Position), out var px, out var py)) continue;
            var dx = px - sx; var dy = py - sy;
            if (Math.Abs(dx) > radiusPx || Math.Abs(dy) > radiusPx) continue;
            var d = Math.Sqrt(dx * dx + dy * dy);
            if (d > radiusPx) continue;
            var (az, alt) = SkyView.ToAzAlt(view.ToHorizontal(s.Position));
            var label = starNames.TryGetValue(s.Hip, out var name) ? name.Name : $"HIP {s.Hip}";
            var detail = name?.Constellation is string c ? $"Star, {c}" : "Star";
            Consider(new SkyObject(SkyObjectKind.Star, label, detail, az, alt, s.Magnitude, null, s.Hip, s.Position), d, s.Magnitude);
        }

        for (int i = 0; i < deepSky.Count; i++)
        {
            var o = deepSky[i];
            var h = view.ToHorizontal(o.Position);
            if (!view.TryProject(h, out var px, out var py)) continue;
            var dx = px - sx; var dy = py - sy;
            var d = Math.Sqrt(dx * dx + dy * dy);
            var outline = Math.Max(0.5 * o.MajorAxisArcmin * view.PixelsPerArcminute(h), 0);
            if (d > Math.Max(radiusPx, outline)) continue;
            var (az, alt) = SkyView.ToAzAlt(h);
            var names = i < deepSkyNames.Count ? deepSkyNames[i] : null;
            var detail = names?.Constellation is string c ? $"{TypeName(o.Type)}, {c}" : TypeName(o.Type);
            // inside its own outline counts as on top of it
            Consider(new SkyObject(SkyObjectKind.DeepSky, names?.Label ?? $"Deep-sky object {i + 1}", detail, az, alt, o.Magnitude, null, 0, o.Position), d <= outline ? 0.5 * d : d, Math.Min(o.Magnitude, 8));
        }
        return best;
    }
}
