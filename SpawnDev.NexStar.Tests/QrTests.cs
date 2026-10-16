using SpawnDev.NexStar.Eyepiece;
using SpawnDev.NexStar.Eyepiece.Qr;
using SpawnDev.UnitTesting;
using ZXing;
using ZXing.Common;
using static SpawnDev.NexStar.Eyepiece.Qr.QRTables;

namespace SpawnDev.NexStar.Tests;

/// <summary>
/// The pairing QR code, checked against ZXing.Net, which shares no code with the encoder. Two checks: our matrix must equal ZXing's own encoder's for the
/// same text, version, error level and mask (that is what "a correct QR code" means, bit for bit); and a ZXing scan of it must read the link back.
/// (ZXing's scanner is not perfect: it misses about one clean synthetic code in fifty, even ones made by its own encoder. So the bit-for-bit comparison is
/// the proof, and the scans try several image scales and count a code as read if any of them reads it.)
/// </summary>
public class QrTests
{
    private static readonly int[] Scales = { 3, 4, 5, 6, 8, 10 };

    /// <summary>Draws the matrix as a grey image with a quiet zone and reads it back with ZXing.</summary>
    private static string? ScanOnce(bool[,] matrix, int scale, int quiet = 4)
    {
        int n = matrix.GetLength(0);
        int size = (n + quiet * 2) * scale;
        var pixels = new byte[size * size];
        Array.Fill(pixels, (byte)255);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                if (matrix[y, x])
                    for (int dy = 0; dy < scale; dy++)
                        for (int dx = 0; dx < scale; dx++)
                            pixels[((y + quiet) * scale + dy) * size + (x + quiet) * scale + dx] = 0;
        var reader = new BarcodeReaderGeneric { Options = new DecodingOptions { PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE }, TryHarder = true } };
        return reader.Decode(new RGBLuminanceSource(pixels, size, size, RGBLuminanceSource.BitmapFormat.Gray8))?.Text;
    }

    private static string? Scan(bool[,] matrix)
    {
        foreach (var scale in Scales)
        {
            var text = ScanOnce(matrix, scale);
            if (text != null) return text;
        }
        return null;
    }

    /// <summary>The mask our encoder chose, read back from the format information (first copy, most significant bit first).</summary>
    private static int MaskOf(bool[,] m, ECLevel level)
    {
        int f = 0;
        for (int i = 0; i < 6; i++) f = (f << 1) | (m[8, i] ? 1 : 0);
        f = (f << 1) | (m[8, 7] ? 1 : 0);
        f = (f << 1) | (m[8, 8] ? 1 : 0);
        f = (f << 1) | (m[7, 8] ? 1 : 0);
        for (int j = 5; j >= 0; j--) f = (f << 1) | (m[j, 8] ? 1 : 0);
        for (int mask = 0; mask < 8; mask++) if (FormatInfo[(int)level * 8 + mask] == f) return mask;
        return -1;
    }

    private static ZXing.QrCode.Internal.ErrorCorrectionLevel ZXingLevel(ECLevel level) => level switch
    {
        ECLevel.L => ZXing.QrCode.Internal.ErrorCorrectionLevel.L,
        ECLevel.M => ZXing.QrCode.Internal.ErrorCorrectionLevel.M,
        ECLevel.Q => ZXing.QrCode.Internal.ErrorCorrectionLevel.Q,
        _ => ZXing.QrCode.Internal.ErrorCorrectionLevel.H,
    };

    /// <summary>What a correct encoder makes of this text at the same version, level and mask.</summary>
    private static bool[,] Reference(string text, ECLevel level, int version, int mask)
    {
        var bits = new ZXing.QrCode.QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0, new Dictionary<EncodeHintType, object>
        {
            [EncodeHintType.MARGIN] = 0, [EncodeHintType.ERROR_CORRECTION] = ZXingLevel(level), [EncodeHintType.QR_VERSION] = version, [EncodeHintType.QR_MASK_PATTERN] = mask,
        });
        var matrix = new bool[bits.Height, bits.Width];
        for (int y = 0; y < bits.Height; y++) for (int x = 0; x < bits.Width; x++) matrix[y, x] = bits[x, y];
        return matrix;
    }

    private static string FirstDifference(bool[,] a, bool[,] b)
    {
        int n = a.GetLength(0);
        if (b.GetLength(0) != n) return $"sizes differ: {n} vs {b.GetLength(0)}";
        int count = 0; string first = "";
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) if (a[y, x] != b[y, x]) { if (count++ == 0) first = $"first at row {y} col {x}"; }
        return count == 0 ? "" : $"{count} modules differ, {first}";
    }

    private static void CheckAgainstReference(string text, ECLevel level, string what)
    {
        var ours = QREncoder.Encode(text, level);
        int version = (ours.GetLength(0) - 17) / 4;
        int mask = MaskOf(ours, level);
        Check.That(mask >= 0, $"{what}: the format information names a mask");
        var diff = FirstDifference(ours, Reference(text, level, version, mask));
        Check.That(diff == "", $"{what}: our code is not the reference code for version {version}, level {level}, mask {mask}: {diff}");
    }

    [TestMethod]
    public void TheScanHarness_ReadsAKnownGoodCode_FromAnotherEncoder()
    {
        // without this a failing scan could be the harness, not our encoder
        var matrix = Reference("https://nexstar.example.test/eyepiece#pair=abc", ECLevel.M, 5, 2);
        Check.Equal("https://nexstar.example.test/eyepiece#pair=abc", Scan(matrix), "a code made by ZXing reads back");
    }

    [TestMethod]
    public void ThePairingLink_ScansBackToTheSameLink()
    {
        var link = PairingInfo.Create().ToLink("https://nexstar.example.test");
        var matrix = QREncoder.Encode(link, ECLevel.M);
        Check.That(matrix.GetLength(0) >= 29 && matrix.GetLength(0) <= 77, $"a plausible size: {matrix.GetLength(0)}");
        Check.Equal(link, Scan(matrix), "what a scanner reads");
        Check.That(PairingInfo.TryParseLink(Scan(matrix), out var back) && back != null, "and it is still a pairing");
    }

    [TestMethod]
    public void ManyRandomPairingLinks_AreExactlyTheReferenceCode_AtEveryErrorLevel()
    {
        int compared = 0;
        for (int i = 0; i < 40; i++)
        {
            var hub = i % 3 == 0 ? PairingInfo.DefaultHub : i % 3 == 1 ? "wss://hub.example.test/announce" : "ws://192.168.1.50:44365/announce";
            var link = PairingInfo.Create(hub).ToLink(i % 2 == 0 ? "https://nexstar.example.test" : "https://a.very.long.host.name.example.test:8443/some/path");
            foreach (var level in new[] { ECLevel.L, ECLevel.M, ECLevel.Q, ECLevel.H })
            {
                CheckAgainstReference(link, level, $"link {i} at level {level}");
                compared++;
            }
        }
        Check.Equal(160, compared, "every one was compared");
    }

    [TestMethod]
    public void ManyRandomTexts_OfEveryLength_AreExactlyTheReferenceCode()
    {
        // every version from 1 up to the largest a pairing link needs, with both letter cases and symbols (byte mode)
        var rng = new Random(2026);
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_./:#=";
        var versions = new HashSet<int>();
        for (int i = 0; i < 600; i++)
        {
            int len = 1 + rng.Next(260);
            // a lower-case letter first, so the reference encoder also picks byte mode (it would pick numeric or alphanumeric for digits or capitals alone)
            var text = "q" + new string(Enumerable.Range(0, len - 1).Select(_ => chars[rng.Next(chars.Length)]).ToArray());
            var level = (ECLevel)rng.Next(4);
            CheckAgainstReference(text, level, $"text of {len} chars at level {level}");
            versions.Add((QREncoder.Encode(text, level).GetLength(0) - 17) / 4);
        }
        Check.That(versions.Count >= 12, $"the texts covered {versions.Count} versions: too few for the check to mean much");
    }

    [TestMethod]
    public void MostCodes_AlsoScan_WithZXingsScanner()
    {
        // the scanner is imperfect on clean synthetic codes; what matters is that it reads the large majority, which a wrongly built code would not
        int read = 0, total = 120;
        for (int i = 0; i < total; i++)
        {
            var link = PairingInfo.Create().ToLink("https://nexstar.example.test");
            var level = (ECLevel)(i % 4);
            if (Scan(QREncoder.Encode(link, level)) == link) read++;
        }
        Check.That(read >= total * 0.97, $"{read} of {total} scanned");
    }

    [TestMethod]
    public void ShortAndAwkwardText_ScansToo()
    {
        foreach (var text in new[] { "NexStar", "https://x.y/z#pair=AAAA", new string('9', 120), "unicode: café ☃" })
            Check.Equal(text, Scan(QREncoder.Encode(text)), $"text '{text}'");
    }

    [TestMethod]
    public void ACorruptedCode_DoesNotScanToTheLink_AndIsNotTheReference()
    {
        var link = PairingInfo.Create().ToLink("https://nexstar.example.test");
        var matrix = QREncoder.Encode(link, ECLevel.L);
        int version = (matrix.GetLength(0) - 17) / 4;
        var reference = Reference(link, ECLevel.L, version, MaskOf(matrix, ECLevel.L));
        Check.That(FirstDifference(matrix, reference) == "", "before damage it is the reference");
        int n = matrix.GetLength(0);
        for (int y = n / 3; y < n / 3 + 8; y++)
            for (int x = 10; x < n - 10; x++) matrix[y, x] = !matrix[y, x];
        Check.That(FirstDifference(matrix, reference) != "", "the comparison sees damage");
        Check.That(Scan(matrix) != link, "a damaged code does not read as the link");
    }
}
