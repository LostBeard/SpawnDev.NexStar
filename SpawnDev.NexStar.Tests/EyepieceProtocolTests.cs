using SpawnDev.NexStar.Eyepiece;
using SpawnDev.UnitTesting;

namespace SpawnDev.NexStar.Tests;

/// <summary>The eyepiece link's pure parts: the pairing link, the proof that the other end is the paired device, the messages, and the camera pose.</summary>
public class EyepieceProtocolTests
{
    // ---- the pairing link ----

    [TestMethod]
    public void APairing_SurvivesTheLink_WithItsRoomSecretAndHub()
    {
        var p = PairingInfo.Create("wss://hub.example.test:44365/announce");
        var link = p.ToLink("https://nexstar.example.test/");
        Check.That(link.StartsWith("https://nexstar.example.test/eyepiece#pair="), $"link: {link}");
        Check.That(PairingInfo.TryParseLink(link, out var back) && back != null, "parsed");
        Check.Bytes(p.RoomKey, back!.RoomKey, "room");
        Check.Bytes(p.Secret, back.Secret, "secret");
        Check.Equal(p.Hub, back.Hub, "hub");
        // the fragment alone is enough (a phone's browser may hand over just that)
        Check.That(PairingInfo.TryParseLink("#pair=" + p.ToToken(), out var frag) && frag!.RoomHex == p.RoomHex, "fragment alone");
        Check.That(PairingInfo.TryParseLink(link + "&x=1", out var extra) && extra!.RoomHex == p.RoomHex, "trailing parameters are ignored");
    }

    [TestMethod]
    public void TheDefaultHub_IsTheSpawnDevHub()
    {
        var p = PairingInfo.Create();
        Check.Equal("wss://hub.spawndev.com:44365/announce", p.Hub, "default hub");
        Check.That(PairingInfo.TryParseToken(p.ToToken(), out var back) && back!.Hub == p.Hub, "round trip");
    }

    [TestMethod]
    public void TwoPairings_NeverShareARoomOrASecret()
    {
        var a = PairingInfo.Create();
        var b = PairingInfo.Create();
        Check.That(a.RoomHex != b.RoomHex, "different rooms");
        Check.That(!a.Secret.SequenceEqual(b.Secret), "different secrets");
        Check.That(!a.Secret.SequenceEqual(new byte[16]) && !a.RoomKey.SequenceEqual(new byte[20]), "not zeros");
    }

    [TestMethod]
    public void BadLinks_AreRefused()
    {
        var good = PairingInfo.Create().ToToken();
        foreach (var bad in new[]
        {
            null, "", "pair=", "pair=!!!", "pair=" + good[..^4], good + "AAAA", "https://x/eyepiece",
            "pair=" + Convert.ToBase64String(new byte[10]), "pair=" + Convert.ToBase64String(new byte[200]),
        })
            Check.That(!PairingInfo.TryParseLink(bad, out var info) && info == null, $"refused: {bad}");

        // wrong version byte, and a hub that is not a websocket address
        var bytes = Convert.FromBase64String(good.Replace('-', '+').Replace('_', '/').PadRight((good.Length + 3) / 4 * 4, '='));
        bytes[0] = 9;
        Check.That(!PairingInfo.TryParseToken(Convert.ToBase64String(bytes), out _), "wrong version");
        Check.That(throws(() => new PairingInfo(new byte[20], new byte[16], "https://not-a-websocket")), "an https hub is refused");
        Check.That(throws(() => new PairingInfo(new byte[19], new byte[16])), "short room");
        Check.That(throws(() => new PairingInfo(new byte[20], new byte[15])), "short secret");

        static bool throws(Action a) { try { a(); return false; } catch (ArgumentException) { return true; } }
    }

    [TestMethod]
    public void PairingInfo_CopiesItsInputs()
    {
        var room = new byte[20]; var secret = new byte[16];
        var p = new PairingInfo(room, secret);
        room[0] = 7; secret[0] = 9;
        Check.That(p.RoomKey[0] == 0 && p.Secret[0] == 0, "the pairing holds its own copy");
    }

    // ---- the proof ----

    private static (byte[] secret, byte[] np, byte[] nv) Setup() => (PairingInfo.Create().Secret, PairingProof.NewNonce(), PairingProof.NewNonce());

    private const string PhoneFp = "sha-256 AA:BB:CC:DD";
    private const string ViewerFp = "sha-256 11:22:33:44";

    [TestMethod]
    public void TheProofs_VerifyForTheRightSecretRoleNoncesAndFingerprints()
    {
        var (secret, np, nv) = Setup();
        var viewerProof = PairingProof.Make(secret, PairingRole.Viewer, np, nv, PhoneFp, ViewerFp);
        var phoneProof = PairingProof.Make(secret, PairingRole.Phone, np, nv, PhoneFp, ViewerFp);
        Check.That(PairingProof.Verify(viewerProof, secret, PairingRole.Viewer, np, nv, PhoneFp, ViewerFp), "the viewer's proof verifies");
        Check.That(PairingProof.Verify(phoneProof, secret, PairingRole.Phone, np, nv, PhoneFp, ViewerFp), "the phone's proof verifies");
        Check.That(!viewerProof.SequenceEqual(phoneProof), "the two proofs differ (one cannot be reflected back as the other)");
    }

    [TestMethod]
    public void AProof_FailsForAnyChange_OfSecretRoleNonceOrFingerprint()
    {
        var (secret, np, nv) = Setup();
        var proof = PairingProof.Make(secret, PairingRole.Phone, np, nv, PhoneFp, ViewerFp);
        Check.That(!PairingProof.Verify(proof, PairingInfo.Create().Secret, PairingRole.Phone, np, nv, PhoneFp, ViewerFp), "another secret");
        Check.That(!PairingProof.Verify(proof, secret, PairingRole.Viewer, np, nv, PhoneFp, ViewerFp), "reflected as the other role");
        Check.That(!PairingProof.Verify(proof, secret, PairingRole.Phone, PairingProof.NewNonce(), nv, PhoneFp, ViewerFp), "another phone nonce (a replay)");
        Check.That(!PairingProof.Verify(proof, secret, PairingRole.Phone, np, PairingProof.NewNonce(), PhoneFp, ViewerFp), "another viewer nonce (a replay)");
        Check.That(!PairingProof.Verify(proof, secret, PairingRole.Phone, np, nv, "sha-256 FF:FF", ViewerFp), "another phone fingerprint (a man in the middle)");
        Check.That(!PairingProof.Verify(proof, secret, PairingRole.Phone, np, nv, PhoneFp, "sha-256 FF:FF"), "another viewer fingerprint (a man in the middle)");
        Check.That(!PairingProof.Verify(new byte[32], secret, PairingRole.Phone, np, nv, PhoneFp, ViewerFp), "all zeros");
        Check.That(!PairingProof.Verify(proof[..31], secret, PairingRole.Phone, np, nv, PhoneFp, ViewerFp), "truncated");
        Check.That(PairingProof.Verify(proof, secret, PairingRole.Phone, np, nv, PhoneFp.ToLowerInvariant(), "  " + ViewerFp + " "), "fingerprint case and whitespace do not matter");
    }

    [TestMethod]
    public void TheFingerprint_IsFoundInAnSdp()
    {
        var sdp = "v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\ns=-\r\na=ice-ufrag:abcd\r\na=fingerprint:sha-256 0A:1B:2C:3D:4E\r\na=setup:actpass\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\n";
        Check.Equal("sha-256 0A:1B:2C:3D:4E", PairingProof.FingerprintFromSdp(sdp), "fingerprint");
        Check.That(PairingProof.FingerprintFromSdp("v=0\r\ns=-\r\n") == null && PairingProof.FingerprintFromSdp(null) == null && PairingProof.FingerprintFromSdp("") == null, "none");
    }

    // ---- the messages ----

    private static T RoundTrip<T>(T message) where T : EyepieceMessage
    {
        var text = message.Encode();
        Check.That(text.Length < EyepieceMessage.MaxLength, "short enough");
        var back = EyepieceMessage.Decode(text);
        Check.That(back is T, $"{typeof(T).Name} came back as {back?.GetType().Name ?? "null"} from {text}");
        return (T)back!;
    }

    [TestMethod]
    public void EveryMessage_SurvivesTheWire()
    {
        var np = PairingProof.NewNonce(); var nv = PairingProof.NewNonce(); var proof = new byte[32]; Random.Shared.NextBytes(proof);
        var hello = RoundTrip(new EyepieceMessage.Hello(1, np));
        Check.That(hello.ProtocolVersion == 1 && hello.PhoneNonce.SequenceEqual(np), "hello");
        var challenge = RoundTrip(new EyepieceMessage.Challenge(nv, proof));
        Check.That(challenge.ViewerNonce.SequenceEqual(nv) && challenge.Proof.SequenceEqual(proof), "challenge");
        Check.That(RoundTrip(new EyepieceMessage.Auth(proof)).Proof.SequenceEqual(proof), "auth");
        RoundTrip(new EyepieceMessage.Ready());
        RoundTrip(new EyepieceMessage.Bye());
        var o = RoundTrip(new EyepieceMessage.Orientation(1234567.5, 271.123, 89.5, -2.25, true, 181.5));
        Check.That(o.Alpha == 271.123 && o.Beta == 89.5 && o.Gamma == -2.25 && o.Absolute && o.CompassHeading == 181.5 && o.TimestampMs == 1234567.5, "orientation");
        var o2 = RoundTrip(new EyepieceMessage.Orientation(5, 1, 2, 3, false));
        Check.That(!o2.Absolute && o2.CompassHeading == null, "orientation without compass");
        var s = RoundTrip(new EyepieceMessage.Status("Back Camera", 1920, 1080, 29.97, 0.84));
        Check.That(s.Camera == "Back Camera" && s.Width == 1920 && s.Height == 1080 && Math.Abs(s.FramesPerSecond!.Value - 30.0) < 0.1 && s.Battery == 0.84, "status");
        var c = RoundTrip(new EyepieceMessage.Command("torch", 1));
        Check.That(c.Name == "torch" && c.Value == 1, "command");
        Check.That(RoundTrip(new EyepieceMessage.Command("zoom", null)).Value == null, "command without a value");
    }

    [TestMethod]
    public void WhatIsNotAMessage_IsIgnored_NeverThrown()
    {
        var proof64 = Convert.ToBase64String(new byte[32]);
        foreach (var bad in new string?[]
        {
            null, "", "   ", "not json", "[]", "42", "{}", "{\"t\":5}", "{\"t\":\"unknown\"}", "{\"t\":\"hello\"}", "{\"t\":\"hello\",\"v\":1,\"np\":\"AAAA\"}",
            "{\"t\":\"hello\",\"v\":1,\"np\":\"!!!\"}", "{\"t\":\"auth\",\"proof\":\"" + Convert.ToBase64String(new byte[31]) + "\"}",
            "{\"t\":\"o\",\"ts\":1,\"a\":\"x\",\"b\":2,\"g\":3}", "{\"t\":\"o\",\"ts\":1,\"a\":1,\"b\":2}", "{\"t\":\"status\"}", "{\"t\":\"cmd\",\"name\":\"\"}",
            "{\"t\":\"cmd\",\"name\":\"" + new string('x', 40) + "\"}", new string('{', 5000), "{\"t\":\"o\",\"ts\":1e999,\"a\":1,\"b\":2,\"g\":3}",
        })
            Check.That(EyepieceMessage.Decode(bad) == null, $"ignored: {(bad?.Length > 60 ? bad[..60] + "..." : bad)}");
        Check.That(EyepieceMessage.Decode("{\"t\":\"auth\",\"proof\":\"" + proof64 + "\"}") is EyepieceMessage.Auth, "and a good one still reads");
    }

    // ---- the camera pose ----

    private static void Pose(double alpha, double beta, double gamma, double az, double alt, double roll, string what)
    {
        var p = CameraPose.FromDeviceOrientation(alpha, beta, gamma);
        var dAz = Math.Abs(((p.Azimuth - az + 540) % 360) - 180);
        // straight up or down there is no azimuth to speak of
        Check.That((dAz < 1e-6 || Math.Abs(alt) > 89.999) && Math.Abs(p.Altitude - alt) < 1e-6, $"{what}: az {p.Azimuth:F4} alt {p.Altitude:F4}, expected {az} {alt}");
        if (!double.IsNaN(roll)) Check.That(Math.Abs(((p.Roll - roll + 540) % 360) - 180) < 1e-6, $"{what}: roll {p.Roll:F4}, expected {roll}");
    }

    [TestMethod]
    public void TheCamera_PointsWhereThePhoneIsHeld()
    {
        Pose(0, 0, 0, 0, -90, double.NaN, "flat on a table, screen up: the camera looks straight down");
        Pose(0, 180, 0, 0, 90, double.NaN, "flat screen down: the camera looks straight up");
        Pose(0, 90, 0, 0, 0, 0, "upright, camera to the north");
        Pose(270, 90, 0, 90, 0, 0, "upright, camera to the east (alpha is counter-clockwise)");
        Pose(180, 90, 0, 180, 0, 0, "upright, camera to the south");
        Pose(90, 90, 0, 270, 0, 0, "upright, camera to the west");
        Pose(0, 135, 0, 0, 45, 0, "tilted back: 45 degrees up, still north");
        Pose(0, 60, 0, 0, -30, 0, "tilted forward: 30 degrees down");
        Pose(270, 135, 0, 90, 45, 0, "east, 45 up");
    }

    [TestMethod]
    public void TheRoll_IsTheTurnAboutTheCameraAxis()
    {
        // landscape, camera north, the top of the phone to the east: turned clockwise as seen from behind
        Pose(270, 0, 90, 0, 0, 90, "top to the east");
        Pose(90, 0, -90, 0, 0, -90, "top to the west");
        Pose(0, 90, 0, 0, 0, 0, "upright");
    }

    [TestMethod]
    public void TheCameraPose_AgreesWithAnIndependentMatrixProduct_ForRandomAngles()
    {
        var rng = new Random(2026);
        int checkedCount = 0;
        for (int i = 0; i < 5000; i++)
        {
            double a = rng.NextDouble() * 360, b = rng.NextDouble() * 360 - 180, g = rng.NextDouble() * 180 - 90;
            var r = Mul(Rz(a), Mul(Rx(b), Ry(g)));
            // device -z and +y in the Earth frame
            double ax = -r[0, 2], ay = -r[1, 2], az = -r[2, 2];
            var expectedAz = Math.Atan2(ax, ay) * 180 / Math.PI; if (expectedAz < 0) expectedAz += 360;
            var expectedAlt = Math.Asin(Math.Clamp(az, -1, 1)) * 180 / Math.PI;
            var p = CameraPose.FromDeviceOrientation(a, b, g);
            if (Math.Abs(expectedAlt) > 89.9) continue;   // the azimuth is meaningless at the pole
            Check.That(Math.Abs(((p.Azimuth - expectedAz + 540) % 360) - 180) < 1e-7 && Math.Abs(p.Altitude - expectedAlt) < 1e-7, $"angles {a:F2} {b:F2} {g:F2}");
            // the roll must leave the phone's top vector where it is when applied back: its angle to sky-up about the axis
            checkedCount++;
        }
        Check.That(checkedCount > 4000, $"only {checkedCount} cases were usable");

        static double[,] Rz(double d) { var c = Math.Cos(d * Math.PI / 180); var s = Math.Sin(d * Math.PI / 180); return new[,] { { c, -s, 0 }, { s, c, 0 }, { 0, 0, 1.0 } }; }
        static double[,] Rx(double d) { var c = Math.Cos(d * Math.PI / 180); var s = Math.Sin(d * Math.PI / 180); return new[,] { { 1.0, 0, 0 }, { 0, c, -s }, { 0, s, c } }; }
        static double[,] Ry(double d) { var c = Math.Cos(d * Math.PI / 180); var s = Math.Sin(d * Math.PI / 180); return new[,] { { c, 0, s }, { 0, 1.0, 0 }, { -s, 0, c } }; }
        static double[,] Mul(double[,] m, double[,] n)
        {
            var o = new double[3, 3];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) for (int k = 0; k < 3; k++) o[i, j] += m[i, k] * n[k, j];
            return o;
        }
    }

    [TestMethod]
    public void TheDeclination_TurnsAMagneticAzimuthIntoATrueOne()
    {
        var p = new CameraPose(10, 20, 30);
        Check.Near(5, p.WithDeclination(-5).Azimuth, 1e-9, "west declination");
        Check.Near(355, p.WithDeclination(-15).Azimuth, 1e-9, "wraps below north");
        Check.Near(5, new CameraPose(355, 0, 0).WithDeclination(10).Azimuth, 1e-9, "wraps above north");
        Check.Near(20, p.WithDeclination(7).Altitude, 1e-9, "altitude unchanged");
    }
}
