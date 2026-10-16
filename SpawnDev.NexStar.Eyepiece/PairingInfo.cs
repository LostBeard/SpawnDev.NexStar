using System.Security.Cryptography;
using System.Text;

namespace SpawnDev.NexStar.Eyepiece;

/// <summary>
/// What an eyepiece phone and the controlling instance share once paired: where to meet (the signalling hub and a room), and a secret that only the two of
/// them know. The room is only a meeting place, and anyone who learns its name could walk in, so the secret is what proves who is who: see
/// <see cref="PairingProof"/>. The whole thing travels as a link, <c>https://host/eyepiece#pair=...</c>, shown as a QR code by the controlling instance and
/// opened by the phone's own camera app. The part after the # is never sent to any server.
/// </summary>
public sealed class PairingInfo
{
    public const int RoomKeyLength = 20;
    public const int SecretLength = 16;
    public const string DefaultHub = "wss://hub.spawndev.com:44365/announce";
    private const byte Version = 1;
    private const string LinkMarker = "pair=";

    /// <summary>The meeting place on the hub: 20 random bytes.</summary>
    public byte[] RoomKey { get; }

    /// <summary>The shared secret: 16 random bytes. Never sent over the hub.</summary>
    public byte[] Secret { get; }

    /// <summary>The signalling hub's websocket address.</summary>
    public string Hub { get; }

    public PairingInfo(byte[] roomKey, byte[] secret, string hub = DefaultHub)
    {
        if (roomKey is not { Length: RoomKeyLength }) throw new ArgumentException($"the room key is {RoomKeyLength} bytes", nameof(roomKey));
        if (secret is not { Length: SecretLength }) throw new ArgumentException($"the secret is {SecretLength} bytes", nameof(secret));
        if (!IsValidHub(hub)) throw new ArgumentException("the hub must be a ws:// or wss:// address", nameof(hub));
        RoomKey = (byte[])roomKey.Clone();
        Secret = (byte[])secret.Clone();
        Hub = hub;
    }

    /// <summary>A fresh pairing: random room, random secret.</summary>
    public static PairingInfo Create(string hub = DefaultHub) => new(RandomNumberGenerator.GetBytes(RoomKeyLength), RandomNumberGenerator.GetBytes(SecretLength), hub);

    private static bool IsValidHub(string? hub) =>
        hub != null && hub.Length <= 200 && (hub.StartsWith("wss://", StringComparison.Ordinal) || hub.StartsWith("ws://", StringComparison.Ordinal)) && Uri.TryCreate(hub, UriKind.Absolute, out _);

    /// <summary>The pairing as the text after the # of a link (URL-safe base64).</summary>
    public string ToToken()
    {
        var hub = Encoding.UTF8.GetBytes(Hub);
        var bytes = new byte[1 + RoomKeyLength + SecretLength + 1 + hub.Length];
        bytes[0] = Version;
        RoomKey.CopyTo(bytes, 1);
        Secret.CopyTo(bytes, 1 + RoomKeyLength);
        bytes[1 + RoomKeyLength + SecretLength] = (byte)hub.Length;
        hub.CopyTo(bytes, 2 + RoomKeyLength + SecretLength);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Reads a token made by <see cref="ToToken"/>. False for anything else: wrong version, truncated, a bad hub address.</summary>
    public static bool TryParseToken(string? token, out PairingInfo? info)
    {
        info = null;
        if (string.IsNullOrEmpty(token) || token.Length > 400) return false;
        try
        {
            var padded = token.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            var bytes = Convert.FromBase64String(padded);
            if (bytes.Length < 2 + RoomKeyLength + SecretLength || bytes[0] != Version) return false;
            var hubLength = bytes[1 + RoomKeyLength + SecretLength];
            if (bytes.Length != 2 + RoomKeyLength + SecretLength + hubLength) return false;
            var hub = Encoding.UTF8.GetString(bytes, 2 + RoomKeyLength + SecretLength, hubLength);
            if (!IsValidHub(hub)) return false;
            info = new PairingInfo(bytes[1..(1 + RoomKeyLength)], bytes[(1 + RoomKeyLength)..(1 + RoomKeyLength + SecretLength)], hub);
            return true;
        }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>The link a phone opens: <paramref name="origin"/> is where the app is served ("https://nexstar.example").</summary>
    public string ToLink(string origin) => $"{origin.TrimEnd('/')}/eyepiece#{LinkMarker}{ToToken()}";

    /// <summary>Finds the pairing in a link or in its fragment alone ("#pair=...", "pair=..."). False if there is none.</summary>
    public static bool TryParseLink(string? link, out PairingInfo? info)
    {
        info = null;
        if (string.IsNullOrEmpty(link)) return false;
        var at = link.IndexOf(LinkMarker, StringComparison.Ordinal);
        if (at < 0) return false;
        var token = link[(at + LinkMarker.Length)..];
        var end = token.IndexOfAny(new[] { '&', '#', '?' });
        if (end >= 0) token = token[..end];
        return TryParseToken(token, out info);
    }

    /// <summary>The room as the hash the hub wants (the same 20 bytes, as hex) for logs and tests.</summary>
    public string RoomHex => Convert.ToHexString(RoomKey).ToLowerInvariant();
}
