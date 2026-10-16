using System.Security.Cryptography;
using System.Text;

namespace SpawnDev.NexStar.Eyepiece;

public enum PairingRole { Phone, Viewer }

/// <summary>
/// The handshake that decides whether the other end of a WebRTC connection is the paired device. Anyone who learns the room on the hub can connect, and
/// the connection is encrypted but does not say WHO is at the other end, so each side proves it knows the secret from the pairing: an HMAC over both
/// fresh nonces and both sides' DTLS certificate fingerprints. The nonces stop a recorded proof being replayed; the fingerprints tie the proof to THIS
/// connection, so a man in the middle who relays the proofs between two connections of his own is caught (the fingerprints he relays do not match the
/// connections the proofs were made for).
/// </summary>
public static class PairingProof
{
    public const int NonceLength = 16;
    private const string Context = "SpawnDev.NexStar eyepiece pairing v1";

    public static byte[] NewNonce() => RandomNumberGenerator.GetBytes(NonceLength);

    /// <summary>The proof that <paramref name="prover"/> knows <paramref name="secret"/> for this exchange.</summary>
    public static byte[] Make(byte[] secret, PairingRole prover, byte[] phoneNonce, byte[] viewerNonce, string phoneFingerprint, string viewerFingerprint)
    {
        if (secret is not { Length: PairingInfo.SecretLength }) throw new ArgumentException("the secret is 16 bytes", nameof(secret));
        if (phoneNonce is not { Length: NonceLength } || viewerNonce is not { Length: NonceLength }) throw new ArgumentException("nonces are 16 bytes");
        if (string.IsNullOrWhiteSpace(phoneFingerprint) || string.IsNullOrWhiteSpace(viewerFingerprint)) throw new ArgumentException("both fingerprints are needed");
        var text = $"{Context}|{prover}|{Convert.ToBase64String(phoneNonce)}|{Convert.ToBase64String(viewerNonce)}|{Normalize(phoneFingerprint)}|{Normalize(viewerFingerprint)}";
        return HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(text));
    }

    /// <summary>True when <paramref name="proof"/> is what <paramref name="prover"/> would have made. Compared in constant time.</summary>
    public static bool Verify(byte[] proof, byte[] secret, PairingRole prover, byte[] phoneNonce, byte[] viewerNonce, string phoneFingerprint, string viewerFingerprint)
    {
        if (proof is not { Length: 32 }) return false;
        return CryptographicOperations.FixedTimeEquals(proof, Make(secret, prover, phoneNonce, viewerNonce, phoneFingerprint, viewerFingerprint));
    }

    private static string Normalize(string fingerprint) => fingerprint.Trim().ToUpperInvariant();

    /// <summary>The DTLS certificate fingerprint ("sha-256 AB:CD:...") from an SDP, or null if it has none. The first one found, as browsers put one per session.</summary>
    public static string? FingerprintFromSdp(string? sdp)
    {
        if (string.IsNullOrEmpty(sdp)) return null;
        foreach (var raw in sdp.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("a=fingerprint:", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line["a=fingerprint:".Length..].Trim();
            return value.Length > 0 ? value : null;
        }
        return null;
    }
}
