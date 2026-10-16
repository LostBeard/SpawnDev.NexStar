using SpawnDev.NexStar.Eyepiece;
using SpawnDev.NexStar.Eyepiece.Qr;
using static SpawnDev.NexStar.Eyepiece.Qr.QRTables;
using SpawnDev.UnitTesting;
using ZXing;
using ZXing.Common;

namespace SpawnDev.NexStar.Tests;

/// <summary>The pairing QR code, checked by a decoder that shares no code with the encoder: what a phone camera would read is the link.</summary>
public class QrTests
{
    /// <summary>Draws the matrix as a grey image with the quiet zone a scanner needs, and reads it back with ZXing.</summary>
    private static string? Scan(bool[,] matrix, int scale = 6, int quiet = 4)
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
        var result = reader.Decode(new RGBLuminanceSource(pixels, size, size, RGBLuminanceSource.BitmapFormat.Gray8));
        return result?.Text;
    }

    [TestMethod]
    public void ThePairingLink_ScansBackToTheSameLink()
    {
        var link = PairingInfo.Create().ToLink("https://nexstar.example.test");
        var matrix = QREncoder.Encode(link, ECLevel.M);
        Check.That(matrix.GetLength(0) >= 29 && matrix.GetLength(0) <= 77, $"a plausible size: {matrix.GetLength(0)}");
        var scanned = Scan(matrix);
        Check.Equal(link, scanned, "what a scanner reads");
        Check.That(PairingInfo.TryParseLink(scanned, out var back) && back != null, "and it is still a pairing");
    }

    [TestMethod]
    public void ManyRandomPairingLinks_AllScan_AtEveryErrorLevel()
    {
        int scanned = 0;
        for (int i = 0; i < 24; i++)
        {
            var hub = i % 3 == 0 ? PairingInfo.DefaultHub : i % 3 == 1 ? "wss://hub.example.test/announce" : "ws://192.168.1.50:44365/announce";
            var link = PairingInfo.Create(hub).ToLink(i % 2 == 0 ? "https://nexstar.example.test" : "https://a.very.long.host.name.example.test:8443/some/path");
            foreach (var level in new[] { ECLevel.L, ECLevel.M, ECLevel.Q, ECLevel.H })
            {
                Check.Equal(link, Scan(QREncoder.Encode(link, level)), $"link {i} at level {level}");
                scanned++;
            }
        }
        Check.Equal(96, scanned, "every one was checked");
    }

    [TestMethod]
    public void ShortAndAwkwardText_ScansToo()
    {
        foreach (var text in new[] { "a", "NexStar", "https://x.y/z#pair=AAAA", new string('9', 120), "unicode: café ☃" })
            Check.Equal(text, Scan(QREncoder.Encode(text)), $"text '{text}'");
    }

    [TestMethod]
    public void ACorruptedCode_DoesNotScanToTheLink()
    {
        // flipping a whole band of modules is beyond the error correction: it must not decode to the real link (the check is that the decoder is really in play)
        var link = PairingInfo.Create().ToLink("https://nexstar.example.test");
        var matrix = QREncoder.Encode(link, ECLevel.L);
        int n = matrix.GetLength(0);
        for (int y = n / 3; y < n / 3 + 8; y++)
            for (int x = 10; x < n - 10; x++) matrix[y, x] = !matrix[y, x];
        Check.That(Scan(matrix) != link, "a damaged code does not read as the link");
    }

    [TestMethod]
    public void TheScanHarness_ReadsAKnownGoodCode_FromAnotherEncoder()
    {
        // without this a failing scan could be the harness, not our encoder
        var bits = new ZXing.QrCode.QRCodeWriter().encode("https://nexstar.example.test/eyepiece#pair=abc", BarcodeFormat.QR_CODE, 0, 0, new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 0, [EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.M });
        var matrix = new bool[bits.Height, bits.Width];
        for (int y = 0; y < bits.Height; y++) for (int x = 0; x < bits.Width; x++) matrix[y, x] = bits[x, y];
        Check.Equal("https://nexstar.example.test/eyepiece#pair=abc", Scan(matrix), "a code made by ZXing reads back");
    }
}
