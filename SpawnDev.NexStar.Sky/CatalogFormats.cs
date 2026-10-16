using System.Buffers.Binary;
using System.Numerics;

namespace SpawnDev.NexStar.Sky;

/// <summary>
/// The binary sky catalog files, shared by the builder that writes them, the tests that check them and the app that loads them.
/// Everything is little-endian and every record is 32 bytes, so a file's bytes can be copied straight into a GPU storage buffer (struct of eight 4-byte
/// fields) with no per-record conversion on the way. Positions are unit vectors in the J2000 equatorial frame (x toward RA 0h, Dec 0; z toward the pole).
/// </summary>
public static class CatalogFormats
{
    public const int HeaderBytes = 32;
    public const int RecordBytes = 32;

    /// <summary>"NXST" read as a little-endian 32-bit number.</summary>
    public const uint StarsMagic = 0x5453584E;
    /// <summary>"NXDS".</summary>
    public const uint DeepSkyMagic = 0x5344584E;
    /// <summary>"NXLN".</summary>
    public const uint LinesMagic = 0x4E4C584E;

    /// <summary>The unit vector of a J2000 right ascension and declination, degrees.</summary>
    public static Vector3 UnitVector(double raDegrees, double decDegrees)
    {
        var ra = raDegrees * Math.PI / 180.0;
        var dec = decDegrees * Math.PI / 180.0;
        return new Vector3((float)(Math.Cos(dec) * Math.Cos(ra)), (float)(Math.Cos(dec) * Math.Sin(ra)), (float)Math.Sin(dec));
    }

    /// <summary>Right ascension (0-360) and declination in degrees of a unit vector.</summary>
    public static (double Ra, double Dec) ToRaDec(Vector3 v)
    {
        var ra = Math.Atan2(v.Y, v.X) * 180.0 / Math.PI;
        if (ra < 0) ra += 360.0;
        var dec = Math.Atan2(v.Z, Math.Sqrt((double)v.X * v.X + (double)v.Y * v.Y)) * 180.0 / Math.PI;
        return (ra, dec);
    }
}

/// <summary>
/// One star. Positions are J2000.0 (Hipparcos' J1991.25 positions carried forward by their proper motion).
/// </summary>
/// <param name="Position">Unit vector, J2000 equatorial</param>
/// <param name="Magnitude">Visual magnitude</param>
/// <param name="ColorIndex">Johnson B-V</param>
/// <param name="PmRa">Proper motion in right ascension times cos(dec), radians per year</param>
/// <param name="PmDec">Proper motion in declination, radians per year</param>
/// <param name="Hip">Hipparcos number</param>
public readonly record struct CatalogStar(Vector3 Position, float Magnitude, float ColorIndex, float PmRa, float PmDec, uint Hip);

/// <summary>Header of a stars file.</summary>
/// <param name="Count">Stars in the file, sorted brightest first</param>
/// <param name="BrightCount">How many of the first stars are at or brighter than <see cref="StarCatalog.BrightMagnitudeLimit"/></param>
/// <param name="Epoch">The epoch the positions are for (2000.0)</param>
public readonly record struct StarCatalogHeader(int Count, int BrightCount, float Epoch);

/// <summary>Reads and writes the stars file: a header, then <see cref="CatalogStar"/> records sorted brightest first.</summary>
public static class StarCatalog
{
    /// <summary>The first <see cref="StarCatalogHeader.BrightCount"/> stars are this bright or brighter: the ones the app keeps in memory for labels, search and picking.</summary>
    public const float BrightMagnitudeLimit = 6.5f;

    public static byte[] Write(IReadOnlyList<CatalogStar> starsSortedBrightFirst, float epoch = 2000.0f)
    {
        var bright = 0;
        foreach (var s in starsSortedBrightFirst) { if (s.Magnitude <= BrightMagnitudeLimit) bright++; else break; }
        var data = new byte[CatalogFormats.HeaderBytes + starsSortedBrightFirst.Count * CatalogFormats.RecordBytes];
        var span = data.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, CatalogFormats.StarsMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)starsSortedBrightFirst.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)bright);
        BinaryPrimitives.WriteSingleLittleEndian(span[16..], epoch);
        for (int i = 0; i < starsSortedBrightFirst.Count; i++)
        {
            var r = span[(CatalogFormats.HeaderBytes + i * CatalogFormats.RecordBytes)..];
            var s = starsSortedBrightFirst[i];
            BinaryPrimitives.WriteSingleLittleEndian(r, s.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(r[4..], s.Position.Y);
            BinaryPrimitives.WriteSingleLittleEndian(r[8..], s.Position.Z);
            BinaryPrimitives.WriteSingleLittleEndian(r[12..], s.Magnitude);
            BinaryPrimitives.WriteSingleLittleEndian(r[16..], s.ColorIndex);
            BinaryPrimitives.WriteSingleLittleEndian(r[20..], s.PmRa);
            BinaryPrimitives.WriteSingleLittleEndian(r[24..], s.PmDec);
            BinaryPrimitives.WriteUInt32LittleEndian(r[28..], s.Hip);
        }
        return data;
    }

    public static StarCatalogHeader ReadHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < CatalogFormats.HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(data) != CatalogFormats.StarsMagic)
            throw new InvalidDataException("not a stars file");
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 1) throw new InvalidDataException("unknown stars file version");
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        if (data.Length != CatalogFormats.HeaderBytes + (long)count * CatalogFormats.RecordBytes) throw new InvalidDataException("stars file length does not match its count");
        return new StarCatalogHeader(count, (int)BinaryPrimitives.ReadUInt32LittleEndian(data[12..]), BinaryPrimitives.ReadSingleLittleEndian(data[16..]));
    }

    public static CatalogStar Read(ReadOnlySpan<byte> data, int index)
    {
        var r = data[(CatalogFormats.HeaderBytes + index * CatalogFormats.RecordBytes)..];
        return new CatalogStar(
            new Vector3(BinaryPrimitives.ReadSingleLittleEndian(r), BinaryPrimitives.ReadSingleLittleEndian(r[4..]), BinaryPrimitives.ReadSingleLittleEndian(r[8..])),
            BinaryPrimitives.ReadSingleLittleEndian(r[12..]), BinaryPrimitives.ReadSingleLittleEndian(r[16..]),
            BinaryPrimitives.ReadSingleLittleEndian(r[20..]), BinaryPrimitives.ReadSingleLittleEndian(r[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(r[28..]));
    }

    /// <summary>The first <paramref name="count"/> stars (the brightest).</summary>
    public static CatalogStar[] ReadFirst(ReadOnlySpan<byte> data, int count)
    {
        var stars = new CatalogStar[count];
        for (int i = 0; i < count; i++) stars[i] = Read(data, i);
        return stars;
    }
}

/// <summary>What a deep-sky object is.</summary>
public enum DeepSkyType : byte
{
    Other = 0,
    Galaxy = 1,
    GalaxyGroup = 2,
    GlobularCluster = 3,
    OpenCluster = 4,
    Association = 5,
    EmissionNebula = 6,
    ReflectionNebula = 7,
    DarkNebula = 8,
    PlanetaryNebula = 9,
    SupernovaRemnant = 10,
    ClusterWithNebula = 11,
    Nebula = 12,
    Nova = 13,
}

/// <summary>One deep-sky object (a galaxy, cluster or nebula).</summary>
/// <param name="Position">Unit vector, J2000 equatorial</param>
/// <param name="MajorAxisArcmin">Apparent major axis, arcminutes (0 if unknown)</param>
/// <param name="MinorAxisArcmin">Apparent minor axis, arcminutes (0 if unknown)</param>
/// <param name="PositionAngle">Degrees from north through east of the major axis</param>
/// <param name="Magnitude">V, or B if there is no V; 99 if neither is known</param>
/// <param name="Type">What it is</param>
/// <param name="Messier">Messier number, or 0</param>
public readonly record struct DeepSkyObject(Vector3 Position, float MajorAxisArcmin, float MinorAxisArcmin, float PositionAngle, float Magnitude, DeepSkyType Type, byte Messier);

/// <summary>Reads and writes the deep-sky file: a header, then <see cref="DeepSkyObject"/> records sorted brightest first. Names are in a parallel JSON file, in the same order.</summary>
public static class DeepSkyCatalog
{
    public static byte[] Write(IReadOnlyList<DeepSkyObject> sortedBrightFirst)
    {
        var data = new byte[CatalogFormats.HeaderBytes + sortedBrightFirst.Count * CatalogFormats.RecordBytes];
        var span = data.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, CatalogFormats.DeepSkyMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)sortedBrightFirst.Count);
        for (int i = 0; i < sortedBrightFirst.Count; i++)
        {
            var r = span[(CatalogFormats.HeaderBytes + i * CatalogFormats.RecordBytes)..];
            var o = sortedBrightFirst[i];
            BinaryPrimitives.WriteSingleLittleEndian(r, o.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(r[4..], o.Position.Y);
            BinaryPrimitives.WriteSingleLittleEndian(r[8..], o.Position.Z);
            BinaryPrimitives.WriteSingleLittleEndian(r[12..], o.MajorAxisArcmin);
            BinaryPrimitives.WriteSingleLittleEndian(r[16..], o.MinorAxisArcmin);
            BinaryPrimitives.WriteSingleLittleEndian(r[20..], o.PositionAngle);
            BinaryPrimitives.WriteSingleLittleEndian(r[24..], o.Magnitude);
            BinaryPrimitives.WriteUInt32LittleEndian(r[28..], (uint)o.Type | ((uint)o.Messier << 8));
        }
        return data;
    }

    public static int ReadCount(ReadOnlySpan<byte> data)
    {
        if (data.Length < CatalogFormats.HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(data) != CatalogFormats.DeepSkyMagic)
            throw new InvalidDataException("not a deep-sky file");
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        if (data.Length != CatalogFormats.HeaderBytes + (long)count * CatalogFormats.RecordBytes) throw new InvalidDataException("deep-sky file length does not match its count");
        return count;
    }

    public static DeepSkyObject Read(ReadOnlySpan<byte> data, int index)
    {
        var r = data[(CatalogFormats.HeaderBytes + index * CatalogFormats.RecordBytes)..];
        var packed = BinaryPrimitives.ReadUInt32LittleEndian(r[28..]);
        return new DeepSkyObject(
            new Vector3(BinaryPrimitives.ReadSingleLittleEndian(r), BinaryPrimitives.ReadSingleLittleEndian(r[4..]), BinaryPrimitives.ReadSingleLittleEndian(r[8..])),
            BinaryPrimitives.ReadSingleLittleEndian(r[12..]), BinaryPrimitives.ReadSingleLittleEndian(r[16..]), BinaryPrimitives.ReadSingleLittleEndian(r[20..]),
            BinaryPrimitives.ReadSingleLittleEndian(r[24..]), (DeepSkyType)(packed & 0xFF), (byte)((packed >> 8) & 0xFF));
    }
}

/// <summary>Constellation lines: pairs of unit vectors. Each segment is one 32-byte record: two positions and eight bytes of padding, so it too copies straight to the GPU.</summary>
public static class LineCatalog
{
    public static byte[] Write(IReadOnlyList<(Vector3 A, Vector3 B)> segments)
    {
        var data = new byte[CatalogFormats.HeaderBytes + segments.Count * CatalogFormats.RecordBytes];
        var span = data.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, CatalogFormats.LinesMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)segments.Count);
        for (int i = 0; i < segments.Count; i++)
        {
            var r = span[(CatalogFormats.HeaderBytes + i * CatalogFormats.RecordBytes)..];
            var (a, b) = segments[i];
            BinaryPrimitives.WriteSingleLittleEndian(r, a.X);
            BinaryPrimitives.WriteSingleLittleEndian(r[4..], a.Y);
            BinaryPrimitives.WriteSingleLittleEndian(r[8..], a.Z);
            BinaryPrimitives.WriteSingleLittleEndian(r[12..], b.X);
            BinaryPrimitives.WriteSingleLittleEndian(r[16..], b.Y);
            BinaryPrimitives.WriteSingleLittleEndian(r[20..], b.Z);
        }
        return data;
    }

    public static int ReadCount(ReadOnlySpan<byte> data)
    {
        if (data.Length < CatalogFormats.HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(data) != CatalogFormats.LinesMagic)
            throw new InvalidDataException("not a lines file");
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        if (data.Length != CatalogFormats.HeaderBytes + (long)count * CatalogFormats.RecordBytes) throw new InvalidDataException("lines file length does not match its count");
        return count;
    }

    public static (Vector3 A, Vector3 B) Read(ReadOnlySpan<byte> data, int index)
    {
        var r = data[(CatalogFormats.HeaderBytes + index * CatalogFormats.RecordBytes)..];
        return (new Vector3(BinaryPrimitives.ReadSingleLittleEndian(r), BinaryPrimitives.ReadSingleLittleEndian(r[4..]), BinaryPrimitives.ReadSingleLittleEndian(r[8..])),
                new Vector3(BinaryPrimitives.ReadSingleLittleEndian(r[12..]), BinaryPrimitives.ReadSingleLittleEndian(r[16..]), BinaryPrimitives.ReadSingleLittleEndian(r[20..])));
    }
}
