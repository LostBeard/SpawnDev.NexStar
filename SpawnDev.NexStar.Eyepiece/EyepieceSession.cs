using System.Security.Cryptography;
using SpawnDev.RTC;
using SpawnDev.RTC.Signaling;

namespace SpawnDev.NexStar.Eyepiece;

public enum EyepieceLinkState { Stopped, Waiting, Connecting, Open, Failed }

/// <summary>
/// One end of the eyepiece link over WebRTC. Both ends join the pairing's room on the hub; the hub only introduces them. A connection is believed only after
/// <see cref="PairingHandshake"/> opens it (the shared secret, tied to the connection's own certificate fingerprints), and only one open link at a time is
/// kept: a stranger who finds the room gets a connection that is closed after ten seconds without a proof, and never sees a frame or a number.
/// The phone sends its camera as a video track and its orientation and status on the control channel; the viewer receives them and sends commands.
/// </summary>
public sealed class EyepieceSession : IAsyncDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private const string ControlLabel = "ctl";

    private readonly PairingRole _role;
    private readonly object _lock = new();
    private readonly RTCPeerConnectionConfig? _config;
    private readonly Dictionary<string, Peer> _peers = new();
    private RtcPeerConnectionRoomHandler? _handler;
    private TrackerSignalingClient? _client;
    private CancellationTokenSource? _cts;
    private IRTCMediaStream? _localStream;
    private string? _activePeer;
    private Task? _announcer;

    private sealed class Peer
    {
        public required string Id { get; init; }
        public IRTCPeerConnection? Pc { get; set; }
        public IRTCDataChannel? Channel { get; set; }
        public PairingHandshake? Handshake { get; set; }
        public readonly List<IRTCMediaStreamTrack> Tracks = new();
        public CancellationTokenSource? Timeout { get; set; }
        public bool Open { get; set; }
    }

    public PairingInfo Pairing { get; }
    public EyepieceLinkState State { get; private set; } = EyepieceLinkState.Stopped;

    /// <summary>What is going on, for the person: why a connection was refused, who is connected.</summary>
    public string? Detail { get; private set; }

    public PairingRole Role => _role;

    /// <summary>The link changed state.</summary>
    public event Action<EyepieceLinkState>? StateChanged;

    /// <summary>A message from the other end, after the link is open (orientation and status for the viewer, commands for the phone).</summary>
    public event Action<EyepieceMessage>? MessageReceived;

    /// <summary>The phone's video track, raised for the viewer once the link is open (and again if the phone starts a new track).</summary>
    public event Action<IRTCMediaStreamTrack>? TrackReceived;

    public EyepieceSession(PairingRole role, PairingInfo pairing, RTCPeerConnectionConfig? config = null)
    {
        _role = role;
        Pairing = pairing;
        _config = config ?? new RTCPeerConnectionConfig { IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } } };
    }

    private void SetState(EyepieceLinkState state, string? detail = null)
    {
        lock (_lock) { State = state; Detail = detail; }
        StateChanged?.Invoke(state);
    }

    /// <summary>Joins the room. The phone passes its camera stream; the viewer passes null.</summary>
    public async Task StartAsync(IRTCMediaStream? cameraStream = null)
    {
        if (State != EyepieceLinkState.Stopped && State != EyepieceLinkState.Failed) return;
        if (_role == PairingRole.Phone && cameraStream == null) throw new ArgumentException("the phone needs its camera stream", nameof(cameraStream));
        _localStream = cameraStream;
        _cts = new CancellationTokenSource();

        _handler = new RtcPeerConnectionRoomHandler(_config) { DataChannelLabel = ControlLabel };
        _handler.OnPeerConnectionCreated = OnPeerConnectionCreatedAsync;
        _handler.OnPeerConnection += (pc, id) => { lock (_lock) PeerFor(id).Pc = pc; };
        _handler.OnDataChannel += OnDataChannel;
        _handler.OnPeerDisconnected += OnPeerDisconnected;

        _client = new TrackerSignalingClient(Pairing.Hub, RandomNumberGenerator.GetBytes(20));
        _client.OnWarning += w => Console.WriteLine($"[Eyepiece] hub: {w}");
        var room = SpawnDev.RTC.Signaling.RoomKey.FromBytes(Pairing.RoomKey);
        _client.Subscribe(room, _handler);
        SetState(EyepieceLinkState.Waiting, "waiting for the other device");
        await _client.AnnounceAsync(room, new AnnounceOptions { Event = "started", NumWant = 5 }, _cts.Token).ConfigureAwait(false);
        _announcer = Task.Run(() => AnnounceLoopAsync(room, _cts.Token));
    }

    /// <summary>The hub only introduces peers when one announces, so while waiting the end that is here keeps announcing: whichever joins later finds the other.</summary>
    private async Task AnnounceLoopAsync(SpawnDev.RTC.Signaling.RoomKey room, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
                if (State == EyepieceLinkState.Waiting) await _client!.AnnounceAsync(room, new AnnounceOptions { NumWant = 5 }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.WriteLine($"[Eyepiece] announce: {ex.Message}"); }
        }
    }

    private Peer PeerFor(string id)
    {
        if (!_peers.TryGetValue(id, out var peer)) _peers[id] = peer = new Peer { Id = id };
        return peer;
    }

    private Task OnPeerConnectionCreatedAsync(IRTCPeerConnection pc, string remoteId)
    {
        if (_role == PairingRole.Phone)
        {
            foreach (var track in _localStream!.GetTracks()) pc.AddTrack(track, _localStream);
        }
        else
        {
            // the viewer wants the phone's video; when the viewer is the one making the offer it has to ask for it
            if (remoteId.Length == 0) pc.AddTransceiver("video", new RTCRtpTransceiverInit { Direction = "recvonly" });
            pc.OnTrack += e =>
            {
                if (e.Track.Kind != "video") return;
                var raise = false;
                lock (_lock)
                {
                    // the peer is not known by id until the connection is installed: file the track under the connection, or keep it until then
                    var claimed = false;
                    foreach (var p in _peers.Values)
                        if (ReferenceEquals(p.Pc, pc)) { p.Tracks.Add(e.Track); claimed = true; raise |= p.Open; }
                    if (!claimed) _unclaimedTracks.Add((pc, e.Track));
                }
                if (raise) TrackReceived?.Invoke(e.Track);
            };
        }
        return Task.CompletedTask;
    }

    private readonly List<(IRTCPeerConnection Pc, IRTCMediaStreamTrack Track)> _unclaimedTracks = new();

    private void OnDataChannel(IRTCDataChannel channel, string peerId)
    {
        if (channel.Label != ControlLabel) return;
        Peer peer;
        lock (_lock)
        {
            peer = PeerFor(peerId);
            peer.Channel = channel;
            // tracks that arrived before the connection was filed under its peer
            if (peer.Pc != null)
                foreach (var (pc, track) in _unclaimedTracks.ToArray())
                    if (ReferenceEquals(pc, peer.Pc)) { peer.Tracks.Add(track); _unclaimedTracks.Remove((pc, track)); }
        }
        channel.OnStringMessage += text => OnControlMessage(peer, text);
        channel.OnClose += () => { if (!peer.Open) CloseOrphan(peer); };
        if (channel.ReadyState == "open") OnChannelOpen(peer); else channel.OnOpen += () => OnChannelOpen(peer);
    }

    private void OnChannelOpen(Peer peer)
    {
        lock (_lock)
        {
            if (_activePeer != null && _activePeer != peer.Id) { _ = ClosePeerAsync(peer, "another device is already linked"); return; }
        }
        // fingerprints of the two ends of THIS connection
        var local = PairingProof.FingerprintFromSdp(peer.Pc?.LocalDescription?.Sdp);
        var remote = PairingProof.FingerprintFromSdp(peer.Pc?.RemoteDescription?.Sdp);
        if (local == null || remote == null) { _ = ClosePeerAsync(peer, "the connection has no certificate fingerprint"); return; }

        peer.Handshake = new PairingHandshake(_role, Pairing.Secret, local, remote);
        peer.Timeout = new CancellationTokenSource();
        var timeout = peer.Timeout.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(HandshakeTimeout, timeout).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            if (!peer.Open) await ClosePeerAsync(peer, "no proof of the pairing secret in time").ConfigureAwait(false);
        });
        if (State == EyepieceLinkState.Waiting) SetState(EyepieceLinkState.Connecting, "checking the other device");
        var first = peer.Handshake.Begin();
        if (first != null) Send(peer, first);
    }

    private void OnControlMessage(Peer peer, string text)
    {
        var message = EyepieceMessage.Decode(text);
        if (peer.Open)
        {
            if (message == null) return;
            // only what the other end is allowed to send
            var allowed = _role == PairingRole.Viewer ? message is EyepieceMessage.Orientation or EyepieceMessage.Status or EyepieceMessage.Bye : message is EyepieceMessage.Command or EyepieceMessage.Bye;
            if (!allowed) return;
            if (message is EyepieceMessage.Bye) { _ = ClosePeerAsync(peer, "the other device ended the link"); return; }
            MessageReceived?.Invoke(message);
            return;
        }

        var h = peer.Handshake;
        if (h == null) return;
        var reply = h.Handle(message);
        if (h.Failed) { _ = ClosePeerAsync(peer, h.FailureReason ?? "the pairing check failed"); return; }
        if (reply != null) Send(peer, reply);
        if (h.IsOpen) Opened(peer);
    }

    private void Opened(Peer peer)
    {
        IRTCMediaStreamTrack[] tracks;
        lock (_lock)
        {
            if (_activePeer != null && _activePeer != peer.Id) { _ = ClosePeerAsync(peer, "another device is already linked"); return; }
            _activePeer = peer.Id;
            peer.Open = true;
            peer.Timeout?.Cancel();
            tracks = peer.Tracks.ToArray();
        }
        SetState(EyepieceLinkState.Open, _role == PairingRole.Viewer ? "the eyepiece phone is linked" : "linked to the controlling device");
        foreach (var t in tracks) TrackReceived?.Invoke(t);
    }

    private static void Send(Peer peer, EyepieceMessage message)
    {
        try { if (peer.Channel?.ReadyState == "open") peer.Channel.Send(message.Encode()); }
        catch (Exception ex) { Console.WriteLine($"[Eyepiece] send: {ex.Message}"); }
    }

    /// <summary>Sends a message to the linked device (nothing is sent before the link is open).</summary>
    public bool Send(EyepieceMessage message)
    {
        Peer? peer;
        lock (_lock) peer = _activePeer != null && _peers.TryGetValue(_activePeer, out var p) && p.Open ? p : null;
        if (peer == null) return false;
        Send(peer, message);
        return true;
    }

    private async Task ClosePeerAsync(Peer peer, string reason)
    {
        bool wasActive;
        lock (_lock)
        {
            wasActive = _activePeer == peer.Id;
            if (wasActive) _activePeer = null;
            _peers.Remove(peer.Id);
            peer.Open = false;
        }
        peer.Timeout?.Cancel();
        try { peer.Channel?.Close(); } catch { }
        try { peer.Pc?.Close(); } catch { }
        if (wasActive || State == EyepieceLinkState.Connecting) SetState(EyepieceLinkState.Waiting, reason);
        await Task.CompletedTask;
        if (wasActive && _client != null && _cts is { IsCancellationRequested: false } && _handler != null)
        {
            try { await _client.AnnounceAsync(SpawnDev.RTC.Signaling.RoomKey.FromBytes(Pairing.RoomKey), new AnnounceOptions { NumWant = 5 }, _cts.Token).ConfigureAwait(false); } catch { }
        }
    }

    private void CloseOrphan(Peer peer)
    {
        lock (_lock) { if (!_peers.ContainsKey(peer.Id)) return; }
        _ = ClosePeerAsync(peer, "the connection closed before it was checked");
    }

    private void OnPeerDisconnected(string peerId)
    {
        Peer? peer;
        lock (_lock) _peers.TryGetValue(peerId, out peer);
        if (peer != null) _ = ClosePeerAsync(peer, "the other device disconnected");
    }

    public async ValueTask DisposeAsync()
    {
        if (State == EyepieceLinkState.Stopped) return;
        Send(new EyepieceMessage.Bye());
        _cts?.Cancel();
        Peer[] peers;
        lock (_lock) peers = _peers.Values.ToArray();
        foreach (var p in peers) { try { p.Channel?.Close(); } catch { } try { p.Pc?.Close(); } catch { } }
        try { _client?.Unsubscribe(SpawnDev.RTC.Signaling.RoomKey.FromBytes(Pairing.RoomKey)); } catch { }
        if (_client != null) { try { await _client.DisposeAsync().ConfigureAwait(false); } catch { } }
        _handler?.Dispose();
        if (_announcer != null) { try { await _announcer.ConfigureAwait(false); } catch { } }
        _cts?.Dispose();
        lock (_lock) { _peers.Clear(); _activePeer = null; State = EyepieceLinkState.Stopped; }
        StateChanged?.Invoke(EyepieceLinkState.Stopped);
    }
}
