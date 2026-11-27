using System.Diagnostics;
using System.Text;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace PhoneRemote.Rtc;

/// <summary>
/// Checks the direct link end to end without a browser: a second peer in this process plays the viewer
/// (offer with a receive-only video track and a data channel), the host side answers, ICE connects over the
/// loopback, 60 synthetic H.264 access units go across the track and a message goes across the data channel.
/// Run with <c>se7en Vision.exe --selftest</c>; also used by the unit tests.
/// </summary>
public static class SelfTest
{
    public sealed record Result(bool Ok, string Summary, int FramesReceived, int FramesSent, double ConnectMs);

    public static async Task<Result> RunAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var sw = Stopwatch.StartNew();
        using var host = new PeerSession(allowHevc: false, allInterfaces: false);
        using var viewer = new RTCPeerConnection(new RTCConfiguration { X_UseRtpFeedbackProfile = true });
        viewer.addTrack(new MediaStreamTrack(
            new VideoFormat(VideoCodecsEnum.H264, 96, 90000, "packetization-mode=1;profile-level-id=42e01f;level-asymmetry-allowed=1"),
            MediaStreamStatusEnum.RecvOnly));
        int received = 0;
        var gotText = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dcOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.OnVideoFrameReceived += (_, _, _, _) => Interlocked.Increment(ref received);
        var dc = await viewer.createDataChannel("ctl", null);
        dc.onopen += () => dcOpen.TrySetResult();
        host.DataText += (label, text) => gotText.TrySetResult(label + ":" + text);
        host.Ice += json => { if (RTCIceCandidateInit.TryParse(json, out var init)) viewer.addIceCandidate(init); };
        viewer.onicecandidate += c => { if (c != null) host.AddIce(c.toJSON()); };

        var offer = viewer.createOffer(null);
        await viewer.setLocalDescription(offer);
        string answer;
        try { answer = await host.AcceptOffer(offer.sdp); }
        catch (Exception ex) { return new Result(false, "réponse impossible : " + ex.Message, 0, 0, sw.Elapsed.TotalMilliseconds); }
        var set = viewer.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answer });
        if (set != SetDescriptionResultEnum.OK) return new Result(false, "réponse refusée côté viewer : " + set, 0, 0, sw.Elapsed.TotalMilliseconds);

        while (!(host.Connected && viewer.connectionState == RTCPeerConnectionState.connected))
        {
            if (cts.IsCancellationRequested) return new Result(false, $"pas de connexion ICE en {timeout.TotalSeconds:0} s (hôte : {host.State}, viewer : {viewer.connectionState})", 0, 0, sw.Elapsed.TotalMilliseconds);
            await Task.Delay(50);
        }
        double connectMs = sw.Elapsed.TotalMilliseconds;

        try { await dcOpen.Task.WaitAsync(cts.Token); } catch { return new Result(false, "canal de données jamais ouvert", 0, 0, connectMs); }
        dc.send("{\"t\":\"ping\",\"ts\":1}");
        string text;
        try { text = await gotText.Task.WaitAsync(cts.Token); } catch { return new Result(false, "message du canal de données jamais reçu", 0, 0, connectMs); }
        if (!text.StartsWith("ctl:")) return new Result(false, "canal inattendu : " + text, 0, 0, connectMs);

        int sent = 0;
        for (int i = 0; i < 60 && !cts.IsCancellationRequested; i++)
        {
            bool key = i % 30 == 0;
            if (host.SendVideo(SyntheticAu(i, key), key, Core.VideoCodec.H264, i * 16667L)) sent++;
            await Task.Delay(5);
        }
        var until = Stopwatch.StartNew();
        while (received < sent * 9 / 10 && until.ElapsedMilliseconds < 2000) await Task.Delay(20);
        bool ok = sent >= 60 && received >= sent * 9 / 10;
        return new Result(ok, ok ? $"OK : connexion en {connectMs:0} ms, {received}/{sent} images reçues, canal de données OK" : $"{received}/{sent} images reçues", received, sent, connectMs);
    }

    /// <summary>A plausible Annex B access unit (SPS, PPS, IDR or a P slice) with filler payload: the packetizer only needs NAL boundaries.</summary>
    private static byte[] SyntheticAu(int i, bool key)
    {
        var ms = new MemoryStream();
        void Nal(byte header, int len)
        {
            ms.Write(new byte[] { 0, 0, 0, 1, header });
            var body = new byte[len];
            new Random(i * 7 + len).NextBytes(body);
            for (int k = 0; k + 2 < body.Length; k++) if (body[k] == 0 && body[k + 1] == 0) body[k + 1] = 3; // no start codes inside
            ms.Write(body);
        }
        if (key)
        {
            Nal(0x67, 12);  // SPS
            Nal(0x68, 4);   // PPS
            Nal(0x65, 3000); // IDR, several RTP packets
        }
        else Nal(0x41, 900);
        return ms.ToArray();
    }

    /// <summary>Console entry point for <c>--selftest</c>: prints the result and returns the process exit code.</summary>
    public static int RunBlocking()
    {
        var r = RunAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("Connexion directe (WebRTC) : " + r.Summary);
        return r.Ok ? 0 : 1;
    }
}
