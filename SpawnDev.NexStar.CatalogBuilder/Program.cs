// SpawnDev.NexStar.CatalogBuilder: turns the public astronomical catalogs into the compact files the sky renderer loads.
//
//   catalogbuilder --download <inputDir>            fetch the source catalogs (once; about 60 MB)
//   catalogbuilder <inputDir> <outputDir>           build stars.bin, dso.bin, constellation-lines.bin, constellation-edges.bin and the JSON name files
//
// Sources (see ATTRIBUTION.md, written next to the outputs):
//   Hipparcos main catalogue (ESA 1997, via CDS I/239)        stars, J1991.25 positions carried to J2000 with their proper motions
//   IAU Catalog of CatalogStar Names (WGSN)                          proper names, CC BY
//   OpenNGC (Mattia Verga)                                    deep-sky objects, CC BY-SA 4.0
//   Stellarium "modern" sky culture                           constellation figures and IAU boundaries, CC BY-SA 4.0
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SpawnDev.NexStar;
using SpawnDev.NexStar.Sky;

var inv = CultureInfo.InvariantCulture;

var sources = new (string File, string Url)[]
{
    ("hip_main.dat", "https://cdsarc.cds.unistra.fr/ftp/cats/I/239/hip_main.dat"),
    ("IAU-CSN.txt", "https://www.pas.rochester.edu/~emamajek/WGSN/IAU-CSN.txt"),
    ("NGC.csv", "https://raw.githubusercontent.com/mattiaverga/OpenNGC/master/database_files/NGC.csv"),
    ("addendum.csv", "https://raw.githubusercontent.com/mattiaverga/OpenNGC/master/database_files/addendum.csv"),
    ("modern_index.json", "https://raw.githubusercontent.com/Stellarium/stellarium/master/skycultures/modern/index.json"),
};

if (args.Length == 2 && args[0] == "--download")
{
    Directory.CreateDirectory(args[1]);
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    foreach (var (file, url) in sources)
    {
        var path = Path.Combine(args[1], file);
        if (File.Exists(path)) { Console.WriteLine($"have {file}"); continue; }
        Console.WriteLine($"downloading {url}");
        await File.WriteAllBytesAsync(path, await http.GetByteArrayAsync(url));
    }
    return 0;
}
if (args.Length != 2) { Console.WriteLine("usage: catalogbuilder --download <inputDir> | catalogbuilder <inputDir> <outputDir>"); return 1; }
var input = args[0];
var output = args[1];
Directory.CreateDirectory(output);

double? D(string s) => double.TryParse(s.Trim(), NumberStyles.Float, inv, out var v) ? v : null;

// ---------------------------------------------------------------- stars
const double HipEpochToJ2000Years = 2000.0 - 1991.25;
var stars = new List<CatalogStar>(120000);
foreach (var line in File.ReadLines(Path.Combine(input, "hip_main.dat")))
{
    var f = line.Split('|');
    if (f.Length < 45) continue;
    var ra = D(f[8]);
    var dec = D(f[9]);
    if (ra == null || dec == null) continue; // 263 stars have no astrometric solution
    var hip = int.Parse(f[1].Trim(), inv);
    var mag = D(f[5]) ?? D(f[44]) ?? 14.0;
    var bv = D(f[37]) ?? 0.6;
    var pmRa = D(f[12]) ?? 0;   // mas/yr, already times cos(dec)
    var pmDec = D(f[13]) ?? 0;
    var cosDec = Math.Max(Math.Cos(dec.Value * Math.PI / 180.0), 1e-6);
    var ra2000 = ra.Value + pmRa / cosDec * HipEpochToJ2000Years / 3.6e6;
    var dec2000 = dec.Value + pmDec * HipEpochToJ2000Years / 3.6e6;
    var radPerMas = Math.PI / 180.0 / 3.6e6;
    stars.Add(new CatalogStar(CatalogFormats.UnitVector(ra2000, dec2000), (float)mag, (float)bv, (float)(pmRa * radPerMas), (float)(pmDec * radPerMas), (uint)hip));
}
stars.Sort((a, b) => a.Magnitude != b.Magnitude ? a.Magnitude.CompareTo(b.Magnitude) : a.Hip.CompareTo(b.Hip));
var starBytes = StarCatalog.Write(stars);
File.WriteAllBytes(Path.Combine(output, "stars.bin"), starBytes);
var header = StarCatalog.ReadHeader(starBytes);
Console.WriteLine($"stars: {header.Count} ({header.BrightCount} at V <= {StarCatalog.BrightMagnitudeLimit}), {starBytes.Length / 1024} KB");
var byHip = new Dictionary<uint, CatalogStar>(stars.Count);
foreach (var s in stars) byHip[s.Hip] = s;

// ---------------------------------------------------------------- star names (IAU WGSN)
var dateRx = new Regex(@"^\d{4}-\d{2}-\d{2}$");
var names = new List<object>();
foreach (var line in File.ReadLines(Path.Combine(input, "IAU-CSN.txt"), Encoding.UTF8))
{
    if (line.Length < 40 || line[0] == '#' || line[0] == '$') continue;
    var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var di = Array.FindIndex(t, x => dateRx.IsMatch(x));
    if (di < 11) continue;
    var hipText = t[di - 4];
    if (!uint.TryParse(hipText, out var hip) || !byHip.ContainsKey(hip)) continue; // exoplanet hosts without a Hipparcos number are not on the chart
    var name = line[..18].Trim();
    var bayerGreek = t[di - 10];
    var con = t[di - 9];
    names.Add(new { hip, name, bayer = bayerGreek == "_" ? null : bayerGreek, con = con == "_" ? null : con });
}
var jsonOptions = new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
File.WriteAllText(Path.Combine(output, "star-names.json"), JsonSerializer.Serialize(names, jsonOptions));
Console.WriteLine($"star names: {names.Count}");

// ---------------------------------------------------------------- constellation lines and names
var modern = JsonDocument.Parse(File.ReadAllText(Path.Combine(input, "modern_index.json")));
var segments = new List<(Vector3, Vector3)>();
var constellations = new List<object>();
var missing = 0;
foreach (var c in modern.RootElement.GetProperty("constellations").EnumerateArray())
{
    var id = c.GetProperty("id").GetString()!;
    var abbr = id.Split(' ')[^1];
    var used = new List<Vector3>();
    if (c.TryGetProperty("lines", out var lines))
    {
        foreach (var chain in lines.EnumerateArray())
        {
            CatalogStar? prev = null;
            foreach (var hipElement in chain.EnumerateArray())
            {
                var hip = hipElement.GetUInt32();
                if (!byHip.TryGetValue(hip, out var s)) { missing++; prev = null; continue; }
                if (prev != null) segments.Add((prev.Value.Position, s.Position));
                used.Add(s.Position);
                prev = s;
            }
        }
    }
    string? english = null, native = null;
    if (c.TryGetProperty("common_name", out var cn))
    {
        english = cn.TryGetProperty("english", out var e) ? e.GetString() : null;
        native = cn.TryGetProperty("native", out var n) ? n.GetString() : null;
    }
    var center = used.Count > 0 ? Vector3.Normalize(used.Aggregate(Vector3.Zero, (a, b) => a + b)) : Vector3.Zero;
    constellations.Add(new { abbr, name = english, latin = native, center = new[] { center.X, center.Y, center.Z } });
}
File.WriteAllBytes(Path.Combine(output, "constellation-lines.bin"), LineCatalog.Write(segments));
File.WriteAllText(Path.Combine(output, "constellations.json"), JsonSerializer.Serialize(constellations, jsonOptions));
Console.WriteLine($"constellations: {constellations.Count}, line segments: {segments.Count} ({missing} stars missing from the catalog)");

// ---------------------------------------------------------------- IAU constellation boundaries (B1875 edges, precessed to J2000)
// Each edge is a stretch of a meridian (M) or parallel (P) of the 1875 equinox, so a parallel is a curve on the sphere: it is cut into short steps.
var edgeSegments = new List<(Vector3, Vector3)>();
var b1875ToJ2000 = Precession.J2000ToDateMatrix((1875.0 - 2000.0) / 100.0); // J2000 -> B1875; its transpose goes back
Vector3 FromB1875(double raDeg, double decDeg)
{
    var v = CatalogFormats.UnitVector(raDeg, decDeg);
    var m = b1875ToJ2000;
    return Vector3.Normalize(new Vector3(
        (float)(m[0, 0] * v.X + m[1, 0] * v.Y + m[2, 0] * v.Z),
        (float)(m[0, 1] * v.X + m[1, 1] * v.Y + m[2, 1] * v.Z),
        (float)(m[0, 2] * v.X + m[1, 2] * v.Y + m[2, 2] * v.Z)));
}
double Hms(string s) { var p = s.Split(':'); return (double.Parse(p[0], inv) + double.Parse(p[1], inv) / 60 + double.Parse(p[2], inv) / 3600) * 15.0; }
double Dms(string s) { var sign = s[0] == '-' ? -1 : 1; var p = s.TrimStart('+', '-').Split(':'); return sign * (double.Parse(p[0], inv) + double.Parse(p[1], inv) / 60 + double.Parse(p[2], inv) / 3600); }
foreach (var line in modern.RootElement.GetProperty("edges").EnumerateArray().Select(x => x.GetString()!))
{
    var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (t.Length < 6) continue;
    var ra1 = Hms(t[2]); var dec1 = Dms(t[3]); var ra2 = Hms(t[4]); var dec2 = Dms(t[5]);
    var dRa = ra2 - ra1;
    if (dRa > 180) dRa -= 360; if (dRa < -180) dRa += 360;
    var length = Math.Max(Math.Abs(dRa) * Math.Cos(dec1 * Math.PI / 180.0), Math.Abs(dec2 - dec1));
    var steps = Math.Max(1, (int)Math.Ceiling(length / 2.0));
    var last = FromB1875(ra1, dec1);
    for (int i = 1; i <= steps; i++)
    {
        var u = (double)i / steps;
        var p = FromB1875(ra1 + dRa * u, dec1 + (dec2 - dec1) * u);
        edgeSegments.Add((last, p));
        last = p;
    }
}
File.WriteAllBytes(Path.Combine(output, "constellation-edges.bin"), LineCatalog.Write(edgeSegments));
Console.WriteLine($"constellation boundaries: {edgeSegments.Count} segments");

// ---------------------------------------------------------------- deep-sky objects (OpenNGC)
var dsos = new List<(DeepSkyObject Object, string Id, string? Name, string Constellation)>();
var idRx = new Regex(@"^(NGC|IC)\s*0*(\d+)(.*)$");
foreach (var file in new[] { "NGC.csv", "addendum.csv" })
{
    var first = true;
    foreach (var line in File.ReadLines(Path.Combine(input, file), Encoding.UTF8))
    {
        if (first) { first = false; continue; }
        var f = line.Split(';');
        if (f.Length < 29) continue;
        var typeCode = f[1];
        if (typeCode is "NonEx" or "Dup" or "*" or "**" or "") continue;
        if (f[2].Length == 0 || f[3].Length == 0) continue;
        var type = typeCode switch
        {
            "G" => DeepSkyType.Galaxy,
            "GPair" or "GTrpl" or "GGroup" => DeepSkyType.GalaxyGroup,
            "GCl" => DeepSkyType.GlobularCluster,
            "OCl" => DeepSkyType.OpenCluster,
            "*Ass" => DeepSkyType.Association,
            "EmN" or "HII" => DeepSkyType.EmissionNebula,
            "RfN" => DeepSkyType.ReflectionNebula,
            "DrkN" => DeepSkyType.DarkNebula,
            "PN" => DeepSkyType.PlanetaryNebula,
            "SNR" => DeepSkyType.SupernovaRemnant,
            "Cl+N" => DeepSkyType.ClusterWithNebula,
            "Neb" => DeepSkyType.Nebula,
            "Nova" => DeepSkyType.Nova,
            _ => DeepSkyType.Other,
        };
        var mag = D(f[9]) ?? D(f[8]) ?? 99.0;
        var messier = int.TryParse(f[23].Trim(), out var m) && m is > 0 and < 256 ? (byte)m : (byte)0;
        var raDeg = Hms(f[2]);
        var decDeg = Dms(f[3]);
        var idMatch = idRx.Match(f[0]);
        var id = idMatch.Success ? $"{idMatch.Groups[1].Value} {idMatch.Groups[2].Value}{idMatch.Groups[3].Value}" : f[0];
        // OpenNGC does not tag M102: the Messier list's duplicate of M101, now usually taken to be NGC 5866. (M40 is a double star, not in NGC/IC: absent.)
        if (id == "NGC 5866") messier = 102;
        var common = f[28].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).FirstOrDefault();
        dsos.Add((new DeepSkyObject(CatalogFormats.UnitVector(raDeg, decDeg), (float)(D(f[5]) ?? 0), (float)(D(f[6]) ?? 0), (float)(D(f[7]) ?? 0), (float)mag, type, messier),
                  id, string.IsNullOrWhiteSpace(common) ? null : common, f[4]));
    }
}
dsos.Sort((a, b) => a.Object.Magnitude != b.Object.Magnitude ? a.Object.Magnitude.CompareTo(b.Object.Magnitude) : b.Object.MajorAxisArcmin.CompareTo(a.Object.MajorAxisArcmin));
File.WriteAllBytes(Path.Combine(output, "dso.bin"), DeepSkyCatalog.Write(dsos.Select(d => d.Object).ToList()));
File.WriteAllText(Path.Combine(output, "dso-names.json"), JsonSerializer.Serialize(dsos.Select(d => new { id = d.Id, m = d.Object.Messier == 0 ? (int?)null : d.Object.Messier, name = d.Name, con = d.Constellation }), jsonOptions));
Console.WriteLine($"deep-sky objects: {dsos.Count} ({dsos.Count(d => d.Object.Messier != 0)} Messier, {dsos.Count(d => d.Object.Magnitude <= 12)} at mag 12 or brighter)");

// ---------------------------------------------------------------- attribution
File.WriteAllText(Path.Combine(output, "ATTRIBUTION.md"), """
    # Sky catalog data

    These files are built by `SpawnDev.NexStar.CatalogBuilder` from public catalogs. The data files in this folder are licensed
    **CC BY-SA 4.0** (the strictest of the sources); the program that reads them is not.

    | File | Source | License |
    |---|---|---|
    | `stars.bin` | The Hipparcos Main Catalogue, ESA (1997), via CDS I/239. Positions at J1991.25 carried to J2000.0 by their proper motions. | ESA/CDS, free for use with acknowledgement |
    | `star-names.json` | IAU Catalog of CatalogStar Names (IAU Working Group on CatalogStar Names) | CC BY |
    | `dso.bin`, `dso-names.json` | OpenNGC by Mattia Verga et al. (https://github.com/mattiaverga/OpenNGC) | CC BY-SA 4.0 |
    | `constellation-lines.bin`, `constellations.json` | Stellarium "modern" sky culture (https://github.com/Stellarium/stellarium) | CC BY-SA 4.0 |
    | `constellation-edges.bin` | IAU constellation boundaries (Delporte 1930) in the form published by P. Barbier, via the Stellarium "modern" sky culture, precessed from B1875 to J2000 | CC BY-SA 4.0 |

    Hipparcos: ESA, 1997, The Hipparcos and Tycho Catalogues, ESA SP-1200.
    """.Replace("\r\n", "\n"));
Console.WriteLine("done");
return 0;
