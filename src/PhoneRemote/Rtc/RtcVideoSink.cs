using PhoneRemote.Core;
using PhoneRemote.Server;

namespace PhoneRemote.Rtc;

/// <summary>Video straight onto the WebRTC track: no queue, UDP never waits; a frame the link cannot take is lost.</summary>
public sealed class RtcVideoSink : IViewerSink
{
    private readonly PeerSession _peer;
    private int _dropped;

    public RtcVideoSink(PeerSession peer) { _peer = peer; }

    public int Dropped => _dropped;
    public string Via => "rtc";

    public void DropUntilKey() => _peer.DropUntilKey();

    public void OnFrame(byte[] au, bool key, VideoCodec codec, long tsUs)
    {
        if (!_peer.SendVideo(au, key, codec, tsUs)) Interlocked.Increment(ref _dropped);
    }

    public void Dispose() { }
}
