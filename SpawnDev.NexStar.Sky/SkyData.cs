using System.Numerics;
using System.Text.Json;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace SpawnDev.NexStar.Sky;

/// <summary>An IAU star name.</summary>
public sealed record StarName(string Name, string? Bayer, string? Constellation);

/// <summary>The names of a deep-sky object, parallel to the records in the file.</summary>
public sealed record DeepSkyName(string Id, int? Messier, string? Name, string? Constellation)
{
    /// <summary>"M31 Andromeda Galaxy", "NGC 6543", the most recognisable label.</summary>
    public string Label => Messier is int m ? (Name != null ? $"M{m} {Name}" : $"M{m}") : (Name != null ? $"{Id} {Name}" : Id);
}

/// <summary>A constellation: its IAU abbreviation, names and where to put its label.</summary>
public sealed record ConstellationInfo(string Abbreviation, string? Name, string? Latin, Vector3 Center);

/// <summary>
/// The sky catalogs, loaded once. The bulk of the data (every star, every deep-sky object, every constellation line) goes from the network into a JavaScript
/// buffer and from there straight into GPU storage buffers: .NET never holds it. Only what the HUD itself needs lives in .NET: the bright stars (for
/// labels, search and picking), the deep-sky list and the names.
/// </summary>
public sealed class SkyData : IDisposable
{
    public GPUBuffer? Stars { get; private set; }
    public GPUBuffer? DeepSkyBuffer { get; private set; }
    public GPUBuffer? ConstellationLines { get; private set; }
    public GPUBuffer? ConstellationBoundaries { get; private set; }

    public int StarCount { get; private set; }
    public int DeepSkyCount { get; private set; }
    public int ConstellationLineCount { get; private set; }
    public int ConstellationBoundaryCount { get; private set; }

    /// <summary>The stars brighter than <see cref="StarCatalog.BrightMagnitudeLimit"/>, brightest first.</summary>
    public CatalogStar[] BrightStars { get; private set; } = System.Array.Empty<CatalogStar>();

    public DeepSkyObject[] DeepSky { get; private set; } = System.Array.Empty<DeepSkyObject>();
    public DeepSkyName[] DeepSkyNames { get; private set; } = System.Array.Empty<DeepSkyName>();
    public Dictionary<uint, StarName> StarNames { get; private set; } = new();
    public ConstellationInfo[] Constellations { get; private set; } = System.Array.Empty<ConstellationInfo>();

    /// <summary>Fetches and uploads everything under <paramref name="baseUrl"/> (the folder holding stars.bin and the others).</summary>
    public static async Task<SkyData> LoadAsync(SpawnJSRuntime js, GPUDevice device, GPUQueue queue, string baseUrl)
    {
        var data = new SkyData();
        try
        {
            baseUrl = baseUrl.TrimEnd('/') + "/";

            // stars: the whole file to the GPU, a header and the bright prefix to .NET
            using (var buffer = await FetchAsync(js, baseUrl + "stars.bin"))
            {
                data.Stars = Upload(device, queue, buffer);
                var length = (long)buffer.ByteLength;
                using var header = new Uint8Array(buffer, 0, CatalogFormats.HeaderBytes);
                var h = StarCatalog.ReadHeader(HeaderOnly(header.ReadBytes(), length));
                data.StarCount = h.Count;
                var prefixBytes = CatalogFormats.HeaderBytes + (long)h.BrightCount * CatalogFormats.RecordBytes;
                using var prefix = new Uint8Array(buffer, 0, prefixBytes);
                data.BrightStars = ReadPrefix(prefix.ReadBytes(), h.BrightCount);
            }

            using (var buffer = await FetchAsync(js, baseUrl + "dso.bin"))
            {
                data.DeepSkyBuffer = Upload(device, queue, buffer);
                using var all = new Uint8Array(buffer);
                var bytes = all.ReadBytes();
                data.DeepSkyCount = DeepSkyCatalog.ReadCount(bytes);
                var list = new DeepSkyObject[data.DeepSkyCount];
                for (int i = 0; i < list.Length; i++) list[i] = DeepSkyCatalog.Read(bytes, i);
                data.DeepSky = list;
            }

            using (var buffer = await FetchAsync(js, baseUrl + "constellation-lines.bin"))
            {
                data.ConstellationLines = Upload(device, queue, buffer);
                data.ConstellationLineCount = (int)(((long)buffer.ByteLength - CatalogFormats.HeaderBytes) / CatalogFormats.RecordBytes);
            }
            using (var buffer = await FetchAsync(js, baseUrl + "constellation-edges.bin"))
            {
                data.ConstellationBoundaries = Upload(device, queue, buffer);
                data.ConstellationBoundaryCount = (int)(((long)buffer.ByteLength - CatalogFormats.HeaderBytes) / CatalogFormats.RecordBytes);
            }

            data.StarNames = ParseStarNames(await FetchTextAsync(js, baseUrl + "star-names.json"));
            data.DeepSkyNames = ParseDeepSkyNames(await FetchTextAsync(js, baseUrl + "dso-names.json"));
            data.Constellations = ParseConstellations(await FetchTextAsync(js, baseUrl + "constellations.json"));
            return data;
        }
        catch
        {
            data.Dispose();
            throw;
        }
    }

    private static async Task<ArrayBuffer> FetchAsync(SpawnJSRuntime js, string url)
    {
        using var response = await js.Fetch(url);
        if (!response.Ok) throw new InvalidOperationException($"{url}: HTTP {response.Status}");
        return await response.ArrayBuffer();
    }

    private static async Task<string> FetchTextAsync(SpawnJSRuntime js, string url)
    {
        using var response = await js.Fetch(url);
        if (!response.Ok) throw new InvalidOperationException($"{url}: HTTP {response.Status}");
        return await response.Text();
    }

    private static GPUBuffer Upload(GPUDevice device, GPUQueue queue, ArrayBuffer data)
    {
        var size = (long)data.ByteLength;
        var buffer = device.CreateBuffer(new GPUBufferDescriptor
        {
            Size = (ulong)((size + 3) & ~3L),
            Usage = GPUBufferUsage.Storage | GPUBufferUsage.CopyDst,
        });
        queue.WriteBuffer(buffer, 0, data);
        return buffer;
    }

    /// <summary>ReadHeader checks the file length against the count, so give it a header that says the file length it was fetched with.</summary>
    private static byte[] HeaderOnly(byte[] header, long fileLength)
    {
        var padded = new byte[fileLength];
        Buffer.BlockCopy(header, 0, padded, 0, header.Length);
        return padded;
    }

    private static CatalogStar[] ReadPrefix(byte[] prefix, int count)
    {
        var stars = new CatalogStar[count];
        // the prefix is the header and the first records: read them with the same record layout
        for (int i = 0; i < count; i++)
        {
            var r = prefix.AsSpan(CatalogFormats.HeaderBytes + i * CatalogFormats.RecordBytes);
            stars[i] = new CatalogStar(
                new Vector3(BitConverter.ToSingle(r), BitConverter.ToSingle(r[4..]), BitConverter.ToSingle(r[8..])),
                BitConverter.ToSingle(r[12..]), BitConverter.ToSingle(r[16..]), BitConverter.ToSingle(r[20..]), BitConverter.ToSingle(r[24..]),
                BitConverter.ToUInt32(r[28..]));
        }
        return stars;
    }

    private static Dictionary<uint, StarName> ParseStarNames(string json)
    {
        var names = new Dictionary<uint, StarName>();
        using var doc = JsonDocument.Parse(json);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            names[e.GetProperty("hip").GetUInt32()] = new StarName(
                e.GetProperty("name").GetString()!,
                e.TryGetProperty("bayer", out var b) ? b.GetString() : null,
                e.TryGetProperty("con", out var c) ? c.GetString() : null);
        }
        return names;
    }

    private static DeepSkyName[] ParseDeepSkyNames(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<DeepSkyName>(13000);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            list.Add(new DeepSkyName(
                e.GetProperty("id").GetString()!,
                e.TryGetProperty("m", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : null,
                e.TryGetProperty("name", out var n) ? n.GetString() : null,
                e.TryGetProperty("con", out var c) ? c.GetString() : null));
        }
        return list.ToArray();
    }

    private static ConstellationInfo[] ParseConstellations(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<ConstellationInfo>(88);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var c = e.GetProperty("center");
            list.Add(new ConstellationInfo(
                e.GetProperty("abbr").GetString()!,
                e.TryGetProperty("name", out var n) ? n.GetString() : null,
                e.TryGetProperty("latin", out var l) ? l.GetString() : null,
                new Vector3(c[0].GetSingle(), c[1].GetSingle(), c[2].GetSingle())));
        }
        return list.ToArray();
    }

    public void Dispose()
    {
        Stars?.Dispose(); Stars = null;
        DeepSkyBuffer?.Dispose(); DeepSkyBuffer = null;
        ConstellationLines?.Dispose(); ConstellationLines = null;
        ConstellationBoundaries?.Dispose(); ConstellationBoundaries = null;
    }
}
