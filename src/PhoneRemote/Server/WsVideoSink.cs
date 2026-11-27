using PhoneRemote.Core;
using PhoneRemote.Media;

namespace PhoneRemote.Server;

/// <summary>
/// Video over the WebSocket: a small queue, and when the link falls behind, frames are dropped until the next
/// keyframe (a delta frame without its predecessors cannot be decoded). v1 viewers get the 1-byte prefix,
/// v2 viewers the extended frame with codec and timestamp.
/// </summary>
public sealed class WsVideoSink : IVideoSink, IDisposable
{
    private const int QueueMax = 6;
    private readonly Func<byte[], CancellationToken, Task> _send;
    private readonly CancellationToken _ct;
    private readonly object _gate = new();
    private readonly Queue<byte[]> _queue = new();
    private readonly SemaphoreSlim _ready = new(0);
    private readonly Task _loop;
    private bool _dropping = true, _disposed;
    public bool V2 { get; set; }
    public int Dropped { get; private set; }
    public long Sent { get; private set; }

    public WsVideoSink(Func<byte[], CancellationToken, Task> send, CancellationToken ct)
    {
        _send = send;
        _ct = ct;
        _loop = Task.Run(Loop);
    }

    /// <summary>Start again at the next keyframe (viewer lost its decoder or fell behind).</summary>
    public void DropUntilKey()
    {
        lock (_gate) { _queue.Clear(); _dropping = true; }
    }

    public void OnFrame(byte[] au, bool key, VideoCodec codec, long tsUs)
    {
        if (!V2 && codec != VideoCodec.H264) return; // a 1.4 viewer only decodes H.264
        var msg = V2 ? Protocol.FrameV2(au, codec, key, tsUs) : Protocol.FrameV1(au, key);
        lock (_gate)
        {
            if (_disposed) return;
            if (_queue.Count >= QueueMax) { _queue.Clear(); _dropping = true; Dropped++; }
            if (_dropping && !key) return;
            _dropping = false;
            _queue.Enqueue(msg);
        }
        _ready.Release();
    }

    private async Task Loop()
    {
        try
        {
            while (!_ct.IsCancellationRequested)
            {
                await _ready.WaitAsync(_ct);
                byte[]? msg;
                lock (_gate) msg = _queue.Count > 0 ? _queue.Dequeue() : null;
                if (msg == null) continue;
                await _send(msg, _ct);
                Sent += msg.Length;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _queue.Clear(); }
        _ready.Release();
    }
}
