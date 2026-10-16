using SpawnDev.NexStar.Eyepiece;
using SpawnDev.UnitTesting;

namespace SpawnDev.NexStar.Tests;

/// <summary>
/// The handshake that opens an eyepiece link, run end to end with every message really encoded and decoded as JSON on the way, against the ways it must
/// not open: the wrong secret, a man in the middle relaying between two connections of his own, a replay, a stranger that skips ahead, messages out of order.
/// </summary>
public class PairingHandshakeTests
{
    private const string PhoneFp = "sha-256 AA:AA:AA";
    private const string ViewerFp = "sha-256 BB:BB:BB";

    private static EyepieceMessage? Wire(EyepieceMessage? m) => m == null ? null : EyepieceMessage.Decode(m.Encode());

    private static (PairingHandshake phone, PairingHandshake viewer) Pair(byte[]? viewerSecret = null, byte[]? phoneSecret = null)
    {
        var secret = PairingInfo.Create().Secret;
        return (new PairingHandshake(PairingRole.Phone, phoneSecret ?? secret, PhoneFp, ViewerFp),
                new PairingHandshake(PairingRole.Viewer, viewerSecret ?? secret, ViewerFp, PhoneFp));
    }

    /// <summary>Runs the exchange between two ends; every message goes through the JSON wire. Returns when nothing is left to say.</summary>
    private static void Run(PairingHandshake phone, PairingHandshake viewer)
    {
        viewer.Begin();
        var toViewer = Wire(phone.Begin());
        for (int i = 0; i < 10 && (toViewer != null); i++)
        {
            var toPhone = Wire(viewer.Handle(toViewer));
            if (toPhone == null) break;
            toViewer = Wire(phone.Handle(toPhone));
            if (toViewer == null) break;
            var ready = Wire(viewer.Handle(toViewer));
            if (ready != null) phone.Handle(ready);
            break;
        }
    }

    [TestMethod]
    public void TheRightSecret_OpensBothEnds()
    {
        var (phone, viewer) = Pair();
        Run(phone, viewer);
        Check.That(phone.IsOpen && viewer.IsOpen, $"phone {phone.State} {phone.FailureReason}, viewer {viewer.State} {viewer.FailureReason}");
    }

    [TestMethod]
    public void AViewerWithAnotherSecret_IsRefusedByThePhone_AndNeverOpens()
    {
        var (phone, viewer) = Pair(viewerSecret: PairingInfo.Create().Secret);
        Run(phone, viewer);
        Check.That(phone.Failed && phone.FailureReason!.Contains("secret"), $"the phone refuses: {phone.State} {phone.FailureReason}");
        Check.That(!viewer.IsOpen && !phone.IsOpen, "neither is open");
    }

    [TestMethod]
    public void APhoneWithAnotherSecret_IsRefusedByTheViewer_AndNeverOpens()
    {
        var (phone, viewer) = Pair(phoneSecret: PairingInfo.Create().Secret);
        Run(phone, viewer);
        // the phone cannot tell the viewer is wrong first: it fails at the Challenge, because the viewer's proof is made with the real secret
        Check.That(phone.Failed, $"the phone fails: {phone.State}");
        Check.That(!viewer.IsOpen, "the viewer did not open");
    }

    [TestMethod]
    public void ARoguePhone_ThatSkipsTheProof_NeverOpensTheViewer()
    {
        var (_, viewer) = Pair();
        viewer.Begin();
        viewer.Handle(new EyepieceMessage.Hello(1, PairingProof.NewNonce()));
        var forged = new byte[32]; new Random(1).NextBytes(forged);
        var reply = viewer.Handle(new EyepieceMessage.Auth(forged));
        Check.That(viewer.Failed && reply == null, $"a forged proof: {viewer.State}");
    }

    [TestMethod]
    public void ARogueViewer_CannotSayReadyToOpenThePhone()
    {
        var (phone, _) = Pair();
        phone.Begin();
        phone.Handle(new EyepieceMessage.Ready());
        Check.That(phone.Failed && !phone.IsOpen, $"Ready before the challenge: {phone.State}");
        var (phone2, _) = Pair();
        phone2.Begin();
        phone2.Handle(new EyepieceMessage.Challenge(PairingProof.NewNonce(), new byte[32]));
        Check.That(phone2.Failed, "a challenge with no valid proof");
        phone2.Handle(new EyepieceMessage.Ready());
        Check.That(!phone2.IsOpen, "and a Ready after the failure changes nothing");
    }

    [TestMethod]
    public void AManInTheMiddle_RelayingBetweenTwoConnections_IsCaught()
    {
        // The attacker is in the room and holds a connection to each end, relaying every message unchanged. He does not know the secret, but the
        // proofs are bound to the fingerprints of the connection they were made on, and the two connections have different ones.
        var secret = PairingInfo.Create().Secret;
        const string attackerToPhone = "sha-256 EE:EE:EE";
        const string attackerToViewer = "sha-256 DD:DD:DD";
        var phone = new PairingHandshake(PairingRole.Phone, secret, PhoneFp, attackerToPhone);      // the phone thinks the attacker is the viewer
        var viewer = new PairingHandshake(PairingRole.Viewer, secret, ViewerFp, attackerToViewer);  // the viewer thinks the attacker is the phone
        viewer.Begin();
        var hello = Wire(phone.Begin());
        var challenge = Wire(viewer.Handle(hello));
        phone.Handle(challenge);
        Check.That(phone.Failed, $"the phone caught the relay: {phone.State}");
        Check.That(!viewer.IsOpen, "and the viewer never opened");
    }

    [TestMethod]
    public void ARecordedExchange_CannotBeReplayedToANewViewer()
    {
        var secret = PairingInfo.Create().Secret;
        var phone = new PairingHandshake(PairingRole.Phone, secret, PhoneFp, ViewerFp);
        var viewer = new PairingHandshake(PairingRole.Viewer, secret, ViewerFp, PhoneFp);
        viewer.Begin();
        var hello = phone.Begin()!;
        var challenge = viewer.Handle(hello)!;
        var auth = phone.Handle(challenge)!;
        Check.That(viewer.Handle(auth) is EyepieceMessage.Ready && viewer.IsOpen, "the real exchange opens");

        // a new viewer (new nonce) is sent the recorded hello and auth
        var viewer2 = new PairingHandshake(PairingRole.Viewer, secret, ViewerFp, PhoneFp);
        viewer2.Begin();
        viewer2.Handle(hello);
        viewer2.Handle(auth);
        Check.That(viewer2.Failed && !viewer2.IsOpen, $"the replayed proof is refused: {viewer2.State}");
    }

    [TestMethod]
    public void MessagesOutOfOrder_FailTheHandshake_AndNothingReopensIt()
    {
        var (phone, viewer) = Pair();
        viewer.Begin();
        viewer.Handle(new EyepieceMessage.Auth(new byte[32]));
        Check.That(viewer.Failed, "an Auth before any Hello");
        Check.That(viewer.Handle(new EyepieceMessage.Hello(1, PairingProof.NewNonce())) == null && viewer.Failed, "a failed handshake stays failed");

        var (phone2, viewer2) = Pair();
        viewer2.Begin();
        var hello = phone2.Begin();
        var challenge = viewer2.Handle(hello)!;
        phone2.Handle(challenge);
        phone2.Handle(challenge);
        Check.That(phone2.Failed, "a second Challenge");

        var (p3, v3) = Pair();
        Check.That(p3.Handle(new EyepieceMessage.Ready()) == null && p3.State == HandshakeState.NotStarted, "nothing is handled before Begin");
        v3.Begin();
        Check.That(v3.Begin() == null && v3.Failed, "Begin twice");
        Check.That(phone.Begin() is EyepieceMessage.Hello, "(a fresh phone still starts)");
    }

    [TestMethod]
    public void AnotherProtocolVersion_IsRefused()
    {
        var (_, viewer) = Pair();
        viewer.Begin();
        viewer.Handle(new EyepieceMessage.Hello(2, PairingProof.NewNonce()));
        Check.That(viewer.Failed && viewer.FailureReason!.Contains("version"), $"{viewer.State} {viewer.FailureReason}");
    }

    [TestMethod]
    public void Nothing_ReachesOpen_WithoutTheCompleteExchange()
    {
        var (phone, viewer) = Pair();
        viewer.Begin();
        var hello = phone.Begin();
        var challenge = viewer.Handle(hello);
        Check.That(!viewer.IsOpen && !phone.IsOpen, "after the challenge neither is open");
        phone.Handle(challenge);
        Check.That(!phone.IsOpen, "after sending its proof the phone still waits for Ready");
        Check.That(phone.State == HandshakeState.AwaitingReady, $"{phone.State}");
    }

    [TestMethod]
    public void ConstructorChecks_RefuseBadInput()
    {
        Check.That(throws(() => new PairingHandshake(PairingRole.Phone, new byte[5], PhoneFp, ViewerFp)), "short secret");
        Check.That(throws(() => new PairingHandshake(PairingRole.Phone, new byte[16], "", ViewerFp)), "no local fingerprint");
        Check.That(throws(() => new PairingHandshake(PairingRole.Viewer, new byte[16], PhoneFp, " ")), "no remote fingerprint");
        static bool throws(Action a) { try { a(); return false; } catch (ArgumentException) { return true; } }
    }
}
