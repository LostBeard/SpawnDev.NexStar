using System.Text;
using System.Text.Json;

namespace SpawnDev.NexStar.Eyepiece;

/// <summary>A message on the eyepiece link's control channel. Text JSON, one object per message, checked and size-limited on the way in.</summary>
public abstract record EyepieceMessage
{
    public const int MaxLength = 2048;

    /// <summary>The JSON text of the message.</summary>
    public abstract string Encode();

    /// <summary>Reads a message; null for anything that is not a well-formed message of a known kind (the link ignores what it does not understand).</summary>
    public static EyepieceMessage? Decode(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String) return null;
            switch (t.GetString())
            {
                case "hello":
                    return Version(root) is int hv && Bytes(root, "np", PairingProof.NonceLength) is { } np ? new Hello(hv, np) : null;
                case "challenge":
                    return Bytes(root, "nv", PairingProof.NonceLength) is { } nv && Bytes(root, "proof", 32) is { } vp ? new Challenge(nv, vp) : null;
                case "auth":
                    return Bytes(root, "proof", 32) is { } pp ? new Auth(pp) : null;
                case "ready":
                    return new Ready();
                case "bye":
                    return new Bye();
                case "o":
                {
                    if (Num(root, "ts") is not double ts || Num(root, "a") is not double a || Num(root, "b") is not double b || Num(root, "g") is not double g) return null;
                    var absolute = root.TryGetProperty("abs", out var abs) && abs.ValueKind == JsonValueKind.True;
                    return new Orientation(ts, a, b, g, absolute, Num(root, "hd"));
                }
                case "status":
                {
                    if (!root.TryGetProperty("cam", out var cam) || cam.ValueKind != JsonValueKind.String) return null;
                    var label = cam.GetString()!;
                    return new Status(label.Length > 120 ? label[..120] : label, (int)(Num(root, "w") ?? 0), (int)(Num(root, "h") ?? 0), Num(root, "fps"), Num(root, "bat"));
                }
                case "cmd":
                {
                    if (!root.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) return null;
                    var n = name.GetString()!;
                    if (n.Length is 0 or > 32) return null;
                    return new Command(n, Num(root, "value"));
                }
                default:
                    return null;
            }
        }
        catch (JsonException) { return null; }
        catch (FormatException) { return null; }
    }

    private static int? Version(JsonElement root) => Num(root, "v") is double v && v is >= 0 and < 1000 ? (int)v : null;

    private static double? Num(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.Number) return null;
        var d = e.GetDouble();
        return double.IsFinite(d) ? d : null;
    }

    private static byte[]? Bytes(JsonElement root, string name, int length)
    {
        if (!root.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.String) return null;
        var s = e.GetString();
        if (s == null || s.Length > 100) return null;
        var bytes = Convert.FromBase64String(s);
        return bytes.Length == length ? bytes : null;
    }

    protected static string Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Phone to viewer, first: the protocol version and the phone's nonce.</summary>
    public sealed record Hello(int ProtocolVersion, byte[] PhoneNonce) : EyepieceMessage
    {
        public const int Current = 1;
        public override string Encode() => Write(w => { w.WriteString("t", "hello"); w.WriteNumber("v", ProtocolVersion); w.WriteString("np", Convert.ToBase64String(PhoneNonce)); });
    }

    /// <summary>Viewer to phone: the viewer's nonce and its proof of the secret.</summary>
    public sealed record Challenge(byte[] ViewerNonce, byte[] Proof) : EyepieceMessage
    {
        public override string Encode() => Write(w => { w.WriteString("t", "challenge"); w.WriteString("nv", Convert.ToBase64String(ViewerNonce)); w.WriteString("proof", Convert.ToBase64String(Proof)); });
    }

    /// <summary>Phone to viewer: the phone's proof of the secret.</summary>
    public sealed record Auth(byte[] Proof) : EyepieceMessage
    {
        public override string Encode() => Write(w => { w.WriteString("t", "auth"); w.WriteString("proof", Convert.ToBase64String(Proof)); });
    }

    /// <summary>Viewer to phone: both proofs checked, the link is open.</summary>
    public sealed record Ready : EyepieceMessage
    {
        public override string Encode() => Write(w => w.WriteString("t", "ready"));
    }

    public sealed record Bye : EyepieceMessage
    {
        public override string Encode() => Write(w => w.WriteString("t", "bye"));
    }

    /// <summary>
    /// The phone's orientation, as the browser reports it (degrees; the W3C device-orientation Euler angles) at the phone's clock time in milliseconds.
    /// <see cref="Absolute"/> says alpha is measured against north (a compass) rather than from wherever the phone started; <see cref="CompassHeading"/>
    /// is the iPhone's own compass reading when it has one.
    /// </summary>
    public sealed record Orientation(double TimestampMs, double Alpha, double Beta, double Gamma, bool Absolute, double? CompassHeading = null) : EyepieceMessage
    {
        public override string Encode() => Write(w =>
        {
            w.WriteString("t", "o");
            w.WriteNumber("ts", TimestampMs);
            w.WriteNumber("a", Math.Round(Alpha, 3)); w.WriteNumber("b", Math.Round(Beta, 3)); w.WriteNumber("g", Math.Round(Gamma, 3));
            if (Absolute) w.WriteBoolean("abs", true);
            if (CompassHeading is double h) w.WriteNumber("hd", Math.Round(h, 3));
        });
    }

    /// <summary>The phone's camera and battery, a few times a minute.</summary>
    public sealed record Status(string Camera, int Width, int Height, double? FramesPerSecond, double? Battery) : EyepieceMessage
    {
        public override string Encode() => Write(w =>
        {
            w.WriteString("t", "status");
            w.WriteString("cam", Camera);
            w.WriteNumber("w", Width); w.WriteNumber("h", Height);
            if (FramesPerSecond is double f) w.WriteNumber("fps", Math.Round(f, 1));
            if (Battery is double b) w.WriteNumber("bat", Math.Round(b, 3));
        });
    }

    /// <summary>Viewer to phone: do something with the camera ("torch", "zoom", "exposure"...).</summary>
    public sealed record Command(string Name, double? Value) : EyepieceMessage
    {
        public override string Encode() => Write(w =>
        {
            w.WriteString("t", "cmd");
            w.WriteString("name", Name);
            if (Value is double v) w.WriteNumber("value", v);
        });
    }
}
