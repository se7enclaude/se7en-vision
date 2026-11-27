using System.Text;
using System.Text.Json;
using PhoneRemote.Core;
using PhoneRemote.Tools;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace PhoneRemote.Rtc;

/// <summary>
/// One direct link to a viewer: the browser makes the offer (video receive-only plus its data channels),
/// this side answers, ICE finds a path (host, STUN) and the picture then goes straight to the viewer as RTP
/// over DTLS-SRTP. Everything the viewer could say over the WebSocket may also arrive on a data channel.
/// Signalling (offer, answer, ICE candidates) is the caller's business: it travels on the WebSocket.
/// </summary>
public sealed class PeerSession : IDisposable
{
    private readonly RTCPeerConnection _pc;
    private readonly Dictionary<string, RTCDataChannel> _channels = new();
    private readonly object _gate = new();
    private int _payloadId = -1;
    private bool _waitKey = true, _disposed;
    private double? _remb, _loss;
    private long _bytes, _lastTsUs = -1;

    /// <summary>A local ICE candidate to forward to the viewer (JSON, browser shape).</summary>
    public event Action<string>? Ice;
    /// <summary>"connecting", "connected", "disconnected", "failed", "closed".</summary>
    public event Action<string>? StateChanged;
    /// <summary>Text message on a data channel: (channel label, text).</summary>
    public event Action<string, string>? DataText;
    /// <summary>Binary message on a data channel: (channel label, bytes).</summary>
    public event Action<string, byte[]>? DataBinary;
    /// <summary>The viewer lost its picture (PLI/FIR): send a keyframe.</summary>
    public event Action? KeyframeWanted;

    public VideoCodec Codec { get; private set; } = VideoCodec.H264;
    public bool Connected { get; private set; }
    public string State { get; private set; } = "new";
    public string Path { get; private set; } = ""; // "host", "srflx"…

    public PeerSession(bool allowHevc, bool allInterfaces)
    {
        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new() { urls = "stun:stun.l.google.com:19302" },
                new() { urls = "stun:stun.cloudflare.com:3478" },
            },
            X_UseRtpFeedbackProfile = true,
            X_ICEIncludeAllInterfaceAddresses = allInterfaces,
        };
        _pc = new RTCPeerConnection(config);
        var formats = new List<VideoFormat>
        {
            new(VideoCodecsEnum.H264, 96, 90000, "packetization-mode=1;profile-level-id=42e01f;level-asymmetry-allowed=1"),
        };
        if (allowHevc) formats.Add(new VideoFormat(VideoCodecsEnum.H265, 98, 90000, null));
        _pc.addTrack(new MediaStreamTrack(formats, MediaStreamStatusEnum.SendOnly));

        _pc.onicecandidate += c => { if (c != null && !_disposed) Ice?.Invoke(c.toJSON()); };
        _pc.onconnectionstatechange += s =>
        {
            State = s.ToString();
            Connected = s == RTCPeerConnectionState.connected;
            if (Connected) Path = DescribePath();
            StateChanged?.Invoke(State);
        };
        _pc.ondatachannel += dc =>
        {
            lock (_gate) _channels[dc.label] = dc;
            dc.onmessage += OnData;
        };
        _pc.OnReceiveReport += OnReport;
    }

    /// <summary>Takes the viewer's offer and returns the answer SDP.</summary>
    public async Task<string> AcceptOffer(string sdp)
    {
        if (sdp.Length > 65536) throw new InvalidOperationException("offre trop grande");
        var result = _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp });
        if (result != SetDescriptionResultEnum.OK) throw new InvalidOperationException("offre refusée : " + result);
        var sending = _pc.VideoStream?.GetSendingFormat();
        if (sending == null) throw new InvalidOperationException("aucun format vidéo commun");
        _payloadId = sending.Value.ID;
        Codec = sending.Value.ToVideoFormat().Codec == VideoCodecsEnum.H265 ? VideoCodec.H265 : VideoCodec.H264;
        var answer = _pc.createAnswer(null);
        await _pc.setLocalDescription(answer);
        return answer.sdp;
    }

    public void AddIce(string json)
    {
        if (_disposed) return;
        if (RTCIceCandidateInit.TryParse(json, out var init) && !string.IsNullOrEmpty(init.candidate))
        {
            try { _pc.addIceCandidate(init); } catch (Exception ex) { Log.Write("ICE ignoré : " + ex.Message); }
        }
    }

    /// <summary>Start again at the next keyframe (new sink, PLI, viewer lost its decoder).</summary>
    public void DropUntilKey() { lock (_gate) _waitKey = true; }

    /// <summary>Sends one access unit. Delta frames are skipped until the first keyframe after a reset.</summary>
    public bool SendVideo(byte[] au, bool key, VideoCodec codec, long tsUs)
    {
        if (!Connected || _disposed || codec != Codec || _payloadId < 0) return false;
        lock (_gate)
        {
            if (_waitKey && !key) return false;
            _waitKey = false;
        }
        try
        {
            // The RTP clock (90 kHz) advances by each frame's duration; the previous interval is a fair guess for it.
            uint duration = _lastTsUs < 0 ? 1500 : (uint)Math.Clamp((tsUs - _lastTsUs) * 9 / 100, 90, 90000);
            _lastTsUs = tsUs;
            if (codec == VideoCodec.H265) _pc.VideoStream.SendH265Frame(duration, _payloadId, au);
            else _pc.VideoStream.SendH264Frame(duration, _payloadId, au);
            Interlocked.Add(ref _bytes, au.Length);
            return true;
        }
        catch (Exception ex) when (!_disposed)
        {
            Log.Write("Envoi RTP : " + ex.Message);
            return false;
        }
    }

    public bool SendData(string channel, string text)
    {
        RTCDataChannel? dc;
        lock (_gate) _channels.TryGetValue(channel, out dc);
        if (dc == null || dc.readyState != RTCDataChannelState.open) return false;
        try { dc.send(text); return true; } catch { return false; }
    }

    public bool SendData(string channel, byte[] data)
    {
        RTCDataChannel? dc;
        lock (_gate) _channels.TryGetValue(channel, out dc);
        if (dc == null || dc.readyState != RTCDataChannelState.open) return false;
        try { dc.send(data, 0, data.Length); return true; } catch { return false; }
    }

    public bool ChannelOpen(string channel)
    {
        lock (_gate) return _channels.TryGetValue(channel, out var dc) && dc.readyState == RTCDataChannelState.open;
    }

    /// <summary>Receiver feedback since the last call: REMB estimate (bit/s) and packet loss (%).</summary>
    public (double? RembBps, double? LossPercent, long Bytes) TakeSample()
    {
        lock (_gate)
        {
            var s = (_remb, _loss, Interlocked.Exchange(ref _bytes, 0));
            _remb = null; _loss = null;
            return s;
        }
    }

    private void OnData(RTCDataChannel dc, DataChannelPayloadProtocols proto, byte[] data)
    {
        if (_disposed) return;
        switch (proto)
        {
            case DataChannelPayloadProtocols.WebRTC_String:
            case DataChannelPayloadProtocols.WebRTC_String_Partial:
                if (data.Length <= 1 << 20) DataText?.Invoke(dc.label, Encoding.UTF8.GetString(data));
                break;
            case DataChannelPayloadProtocols.WebRTC_Binary:
            case DataChannelPayloadProtocols.WebRTC_Binary_Partial:
                DataBinary?.Invoke(dc.label, data);
                break;
        }
    }

    private void OnReport(System.Net.IPEndPoint ep, SDPMediaTypesEnum media, RTCPCompoundPacket pkt)
    {
        if (media != SDPMediaTypesEnum.video || _disposed) return;
        var fb = pkt.Feedback;
        if (fb != null)
        {
            var h = fb.Header;
            if (h.PacketType == RTCPReportTypesEnum.PSFB)
            {
                if (h.PayloadFeedbackMessageType is PSFBFeedbackTypesEnum.PLI or PSFBFeedbackTypesEnum.FIR) KeyframeWanted?.Invoke();
                else if (h.PayloadFeedbackMessageType == PSFBFeedbackTypesEnum.AFB && fb.BitrateMantissa > 0)
                    lock (_gate) _remb = fb.BitrateMantissa * Math.Pow(2, fb.BitrateExp);
            }
        }
        var rr = pkt.ReceiverReport?.ReceptionReports;
        if (rr is { Count: > 0 }) lock (_gate) _loss = rr[0].FractionLost * 100.0 / 256.0;
    }

    private string DescribePath()
    {
        try
        {
            var ch = _pc.GetRtpChannel();
            var pair = ch?.NominatedEntry;
            if (pair == null) return "";
            return pair.RemoteCandidate.type == RTCIceCandidateType.host && pair.LocalCandidate.type == RTCIceCandidateType.host ? "local" : "direct";
        }
        catch { return ""; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Connected = false;
        try { _pc.close(); } catch { }
        try { _pc.Dispose(); } catch { }
    }
}
