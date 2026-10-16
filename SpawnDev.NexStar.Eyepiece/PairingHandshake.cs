namespace SpawnDev.NexStar.Eyepiece;

public enum HandshakeState { NotStarted, AwaitingHello, AwaitingChallenge, AwaitingAuth, AwaitingReady, Open, Failed }

/// <summary>
/// One side of the handshake that opens an eyepiece link, as a pure state machine fed with the messages that arrive: no transport in it, so every path
/// (the good one, a wrong secret, a man in the middle, a replay, messages out of order) can be run and checked directly.
/// The phone speaks first (<see cref="EyepieceMessage.Hello"/>); the viewer answers with its nonce and proof; the phone checks that proof and sends
/// its own; the viewer checks it and says Ready. Until a side is Open it believes nothing the other says, and anything out of order fails the handshake.
/// </summary>
public sealed class PairingHandshake
{
    private readonly byte[] _secret;
    private readonly string _phoneFingerprint;
    private readonly string _viewerFingerprint;
    private byte[]? _phoneNonce;
    private byte[]? _viewerNonce;

    public PairingRole Role { get; }
    public HandshakeState State { get; private set; }

    /// <summary>Why the handshake failed, when it did.</summary>
    public string? FailureReason { get; private set; }

    public bool IsOpen => State == HandshakeState.Open;
    public bool Failed => State == HandshakeState.Failed;

    /// <param name="localFingerprint">This side's DTLS certificate fingerprint (from its own SDP)</param>
    /// <param name="remoteFingerprint">The other side's (from the SDP it sent)</param>
    public PairingHandshake(PairingRole role, byte[] secret, string localFingerprint, string remoteFingerprint)
    {
        if (secret is not { Length: PairingInfo.SecretLength }) throw new ArgumentException("the secret is 16 bytes", nameof(secret));
        if (string.IsNullOrWhiteSpace(localFingerprint) || string.IsNullOrWhiteSpace(remoteFingerprint)) throw new ArgumentException("both fingerprints are needed");
        Role = role;
        _secret = (byte[])secret.Clone();
        (_phoneFingerprint, _viewerFingerprint) = role == PairingRole.Phone ? (localFingerprint, remoteFingerprint) : (remoteFingerprint, localFingerprint);
    }

    /// <summary>Starts the handshake. The phone returns its Hello to send; the viewer waits for one and returns null.</summary>
    public EyepieceMessage? Begin()
    {
        if (State != HandshakeState.NotStarted) return Fail("the handshake was already started");
        if (Role == PairingRole.Phone)
        {
            _phoneNonce = PairingProof.NewNonce();
            State = HandshakeState.AwaitingChallenge;
            return new EyepieceMessage.Hello(EyepieceMessage.Hello.Current, _phoneNonce);
        }
        State = HandshakeState.AwaitingHello;
        return null;
    }

    /// <summary>Handles a message from the other side. Returns the reply to send, if there is one. A failure is recorded in <see cref="State"/>.</summary>
    public EyepieceMessage? Handle(EyepieceMessage? message)
    {
        if (State is HandshakeState.Failed or HandshakeState.NotStarted or HandshakeState.Open) return null;
        switch (message)
        {
            case EyepieceMessage.Hello hello when State == HandshakeState.AwaitingHello:
            {
                if (hello.ProtocolVersion != EyepieceMessage.Hello.Current) return Fail($"the other end speaks version {hello.ProtocolVersion}, this one {EyepieceMessage.Hello.Current}");
                _phoneNonce = hello.PhoneNonce;
                _viewerNonce = PairingProof.NewNonce();
                var proof = PairingProof.Make(_secret, PairingRole.Viewer, _phoneNonce, _viewerNonce, _phoneFingerprint, _viewerFingerprint);
                State = HandshakeState.AwaitingAuth;
                return new EyepieceMessage.Challenge(_viewerNonce, proof);
            }
            case EyepieceMessage.Challenge challenge when State == HandshakeState.AwaitingChallenge:
            {
                _viewerNonce = challenge.ViewerNonce;
                if (!PairingProof.Verify(challenge.Proof, _secret, PairingRole.Viewer, _phoneNonce!, _viewerNonce, _phoneFingerprint, _viewerFingerprint))
                    return Fail("the other end did not prove it knows the pairing secret");
                State = HandshakeState.AwaitingReady;
                return new EyepieceMessage.Auth(PairingProof.Make(_secret, PairingRole.Phone, _phoneNonce!, _viewerNonce, _phoneFingerprint, _viewerFingerprint));
            }
            case EyepieceMessage.Auth auth when State == HandshakeState.AwaitingAuth:
            {
                if (!PairingProof.Verify(auth.Proof, _secret, PairingRole.Phone, _phoneNonce!, _viewerNonce!, _phoneFingerprint, _viewerFingerprint))
                    return Fail("the other end did not prove it knows the pairing secret");
                State = HandshakeState.Open;
                return new EyepieceMessage.Ready();
            }
            case EyepieceMessage.Ready when State == HandshakeState.AwaitingReady:
                State = HandshakeState.Open;
                return null;
            default:
                return Fail($"unexpected {message?.GetType().Name ?? "message"} while {State}");
        }
    }

    private EyepieceMessage? Fail(string reason)
    {
        State = HandshakeState.Failed;
        FailureReason = reason;
        return null;
    }
}
