using SpawnDev.NexStar.Eyepiece;
using SpawnDev.RTC;
using SpawnDev.RTC.Browser;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.UnitTesting;

namespace SpawnDev.NexStar.BrowserTests;

/// <summary>
/// The eyepiece link for real: two sessions in this browser, a phone with a (fake) camera and a viewer, meeting through the real SpawnDev hub, opening the
/// link with the pairing secret, and carrying video, orientation and commands. And a stranger who knows the room but not the secret, who must get nothing.
/// </summary>
public class EyepieceLinkTests
{
    private static void Require(bool condition, string what)
    {
        if (!condition) throw new Exception("FAILED: " + what);
    }

    private static async Task<T> Within<T>(Task<T> task, int seconds, string what)
    {
        var done = await Task.WhenAny(task, Task.Delay(seconds * 1000));
        if (done != task) throw new Exception($"FAILED: {what} did not happen within {seconds} s");
        return await task;
    }

    private static Task<EyepieceLinkState> WaitFor(EyepieceSession session, EyepieceLinkState wanted)
    {
        var tcs = new TaskCompletionSource<EyepieceLinkState>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (session.State == wanted) tcs.TrySetResult(wanted);
        session.StateChanged += s => { if (s == wanted) tcs.TrySetResult(s); };
        return tcs.Task;
    }

    private static async Task<IRTCMediaStream> CameraAsync()
    {
        try { return await RTCMediaDevices.GetUserMedia(new SpawnDev.RTC.MediaStreamConstraints { Video = true }); }
        catch (Exception ex) when (ex.Message.Contains("NotAllowed") || ex.Message.Contains("NotFound"))
        {
            throw new Exception($"FAILED: no camera for the test (the browser should run with a fake device): {ex.Message}");
        }
    }

    [TestMethod(Timeout = 120000)]
    public async Task ThePairedPhoneAndViewer_OpenTheLink_AndCarryVideoOrientationAndCommands()
    {
        var pairing = PairingInfo.Create();
        using var camera = await CameraAsync();
        await using var viewer = new EyepieceSession(PairingRole.Viewer, pairing);
        await using var phone = new EyepieceSession(PairingRole.Phone, pairing);

        var track = new TaskCompletionSource<IRTCMediaStreamTrack>(TaskCreationOptions.RunContinuationsAsynchronously);
        var orientation = new TaskCompletionSource<EyepieceMessage.Orientation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var status = new TaskCompletionSource<EyepieceMessage.Status>(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new TaskCompletionSource<EyepieceMessage.Command>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.TrackReceived += t => track.TrySetResult(t);
        viewer.MessageReceived += m => { if (m is EyepieceMessage.Orientation o) orientation.TrySetResult(o); else if (m is EyepieceMessage.Status s) status.TrySetResult(s); };
        phone.MessageReceived += m => { if (m is EyepieceMessage.Command c) command.TrySetResult(c); };
        var viewerOpen = WaitFor(viewer, EyepieceLinkState.Open);
        var phoneOpen = WaitFor(phone, EyepieceLinkState.Open);

        await viewer.StartAsync();
        await phone.StartAsync(camera);
        await Within(viewerOpen, 60, "the viewer's link opening");
        await Within(phoneOpen, 20, "the phone's link opening");
        Require(viewer.State == EyepieceLinkState.Open && phone.State == EyepieceLinkState.Open, "both ends are open");

        // orientation and status, phone to viewer
        Require(phone.Send(new EyepieceMessage.Orientation(1000.5, 271.25, 89.75, -1.5, true, 181.0)), "the phone could send");
        var o = await Within(orientation.Task, 10, "the orientation arriving");
        Require(o.Alpha == 271.25 && o.Beta == 89.75 && o.Gamma == -1.5 && o.Absolute && o.CompassHeading == 181.0, $"orientation intact: {o}");
        Require(phone.Send(new EyepieceMessage.Status("Fake camera", 640, 480, 30, 0.9)), "status sent");
        var s = await Within(status.Task, 10, "the status arriving");
        Require(s.Camera == "Fake camera" && s.Width == 640, $"status intact: {s}");

        // a command, viewer to phone
        Require(viewer.Send(new EyepieceMessage.Command("torch", 1)), "the viewer could send");
        var c = await Within(command.Task, 10, "the command arriving");
        Require(c.Name == "torch" && c.Value == 1, $"command intact: {c}");

        // the video: frames really decode on the viewer's end
        var remote = await Within(track.Task, 20, "the video track arriving");
        Require(remote is BrowserRTCMediaStreamTrack, "a browser track");
        using var video = new HTMLVideoElement();
        using var stream = new MediaStream();
        stream.AddTrack(((BrowserRTCMediaStreamTrack)remote).NativeTrack);
        video.Muted = true;
        video.SrcObject = stream;
        await video.Play();
        for (int i = 0; i < 40 && video.VideoWidth == 0; i++) await Task.Delay(250);
        Require(video.VideoWidth > 0 && video.VideoHeight > 0, $"the received video has a picture: {video.VideoWidth}x{video.VideoHeight}");
        Console.WriteLine($"[EyepieceLink] link open, video {video.VideoWidth}x{video.VideoHeight}");
        foreach (var t in camera.GetTracks()) t.Stop();
    }

    [TestMethod(Timeout = 150000)]
    public async Task AStrangerWhoKnowsTheRoomButNotTheSecret_GetsNothing_AndDoesNotBlockTheRealPhone()
    {
        var pairing = PairingInfo.Create();
        var strangerPairing = new PairingInfo(pairing.RoomKey, PairingInfo.Create().Secret, pairing.Hub);   // the same room, another secret
        using var camera = await CameraAsync();
        await using var viewer = new EyepieceSession(PairingRole.Viewer, pairing);
        await using var stranger = new EyepieceSession(PairingRole.Phone, strangerPairing);

        var leaked = new List<string>();
        viewer.TrackReceived += _ => leaked.Add("a video track");
        viewer.MessageReceived += m => leaked.Add(m.GetType().Name);
        var viewerOpened = false;
        viewer.StateChanged += st => { if (st == EyepieceLinkState.Open) viewerOpened = true; };

        await viewer.StartAsync();
        await stranger.StartAsync(camera);
        // long enough for the stranger to find the room, connect, and be refused (the proof check, or at the latest the ten-second timeout)
        await Task.Delay(25000);
        Require(!viewerOpened && viewer.State != EyepieceLinkState.Open, $"the stranger did not open the viewer's link (viewer {viewer.State}: {viewer.Detail})");
        Require(stranger.State != EyepieceLinkState.Open, $"and the stranger's own link is not open (stranger {stranger.State}: {stranger.Detail})");
        Require(leaked.Count == 0, $"the viewer received nothing from the stranger, but got: {string.Join(", ", leaked)}");

        // the real phone now joins the same room and still gets through
        await using var phone = new EyepieceSession(PairingRole.Phone, pairing);
        var viewerOpen = WaitFor(viewer, EyepieceLinkState.Open);
        await phone.StartAsync(camera);
        await Within(viewerOpen, 60, "the real phone opening the link after the stranger");
        Require(viewer.State == EyepieceLinkState.Open, "the viewer is linked to the real phone");
        foreach (var t in camera.GetTracks()) t.Stop();
    }
}
