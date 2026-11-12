using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PhoneRemote.Capture;
using PhoneRemote.Input;
using PhoneRemote.Tools;

namespace PhoneRemote.Server;

/// <summary>
/// One phone's control channel. Text frames carry JSON commands; the PC answers frame
/// requests with binary JPEGs (the phone pulls the next image once it has shown the last one).
/// Starting or stopping the video (which kills and launches ffmpeg) never runs on the receive loop,
/// so mouse and keyboard keep flowing meanwhile. A phone silent for <see cref="IdleTimeout"/> (it pings
/// every 2 s) is considered gone and the connection is dropped.
/// </summary>
public sealed class PhoneConnection
{
    private readonly WebSocket _ws;
    private readonly InputInjector _input;
    private readonly ScreenCapturer _capturer;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _frameWanted = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _closing;
    private CancellationToken _ct;

    /// <summary>No message at all for this long: the phone left without closing (Wi-Fi lost, Safari killed…).</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(10);

    private int _screen;
    private int _maxWidth = 1280, _quality = 55;
    private byte[]? _lastFrame;
    private bool _forceFrame = true;

    // Video mode: ffmpeg frames go through a small queue; when the network falls behind,
    // frames are dropped until the next keyframe (a delta frame without its predecessors can't be decoded).
    private readonly object _videoGate = new();
    private VideoStream? _video;
    private int _videoWidth = 1280, _videoKbps = 2500;
    private readonly Queue<(byte[] Data, bool Key)> _videoQueue = new();
    private readonly SemaphoreSlim _videoReady = new(0);
    private bool _videoDropping;
    private const int VideoQueueMax = 6;

    // Video start/stop requests: the latest one wins, applied one at a time off the receive loop.
    private readonly SemaphoreSlim _videoSwitch = new(1, 1);
    private int _videoRequest, _videoApplied;
    private bool _videoWanted, _videoClosed;

    public PhoneConnection(WebSocket ws, InputInjector input, ScreenCapturer capturer)
    {
        _ws = ws;
        _input = input;
        _capturer = capturer;
    }

    public void Close(int code, string reason)
    {
        _closing = true; // from now on this connection no longer drives the PC
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseOutputAsync((WebSocketCloseStatus)code, reason, timeout.Token);
            }
            catch { }
            finally { _cts.Cancel(); }
        });
    }

    public async Task RunAsync(CancellationToken aborted)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(aborted, _cts.Token);
        var ct = linked.Token;
        _ct = ct;
        await SendInfo(ct);
        var sender = Task.Run(() => FrameLoop(ct), ct);
        var videoSender = Task.Run(() => VideoLoop(ct), ct);
        try
        {
            await ReceiveLoop(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Write($"Téléphone muet depuis {IdleTimeout.TotalSeconds:0} s : connexion fermée");
            _ws.Abort();
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            _cts.Cancel();
            // wait for a start/stop in progress, then make sure no ffmpeg is left behind
            await _videoSwitch.WaitAsync();
            try
            {
                _videoClosed = true;
                StopVideo();
            }
            finally { _videoSwitch.Release(); }
            try { await sender; } catch { }
            try { await videoSender; } catch { }
        }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            idle.CancelAfter(IdleTimeout); // restarted on every message
            var r = await _ws.ReceiveAsync(buffer, idle.Token);
            if (r.MessageType == WebSocketMessageType.Close) return;
            message.Write(buffer, 0, r.Count);
            if (message.Length > 256 * 1024) return; // nothing legitimate is this big
            if (!r.EndOfMessage) continue;
            if (r.MessageType == WebSocketMessageType.Text)
            {
                try { await Handle(message.ToArray(), ct); }
                catch (JsonException) { }
                catch (KeyNotFoundException) { }
                catch (InvalidOperationException) { }
                catch (FormatException) { }
            }
            message.SetLength(0);
        }
    }

    private async Task Handle(byte[] json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        var m = doc.RootElement;
        string t = m.GetProperty("t").GetString() ?? "";
        if (_closing && t is "move" or "abs" or "down" or "up" or "click" or "wheel" or "text" or "key" or "power") return;
        switch (t)
        {
            case "move":
                lock (_input) _input.MoveBy(Num(m, "dx"), Num(m, "dy"));
                break;
            case "abs":
                lock (_input) _input.MoveTo(ScreenCapturer.Bounds(_screen), Num(m, "x"), Num(m, "y"));
                break;
            case "down":
            case "up":
                lock (_input) _input.Button(Btn(m), t == "down");
                break;
            case "click":
                lock (_input) _input.Click(Btn(m), Math.Clamp(Int(m, "n", 1), 1, 3));
                break;
            case "wheel":
                lock (_input) _input.Wheel((int)Num(m, "dy"), (int)Num(m, "dx"));
                break;
            case "text":
                var s = m.GetProperty("s").GetString() ?? "";
                if (s.Length <= 2000) lock (_input) _input.Text(s);
                break;
            case "key":
                var mods = m.TryGetProperty("mods", out var mm) && mm.ValueKind == JsonValueKind.Array
                    ? mm.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
                    : Array.Empty<string>();
                lock (_input) _input.Key(m.GetProperty("k").GetString() ?? "", mods);
                break;
            case "frame":
                _maxWidth = Math.Clamp(Int(m, "w", _maxWidth), 480, 2560);
                _quality = Math.Clamp(Int(m, "q", _quality), 25, 90);
                if (_frameWanted.CurrentCount == 0) _frameWanted.Release();
                break;
            case "screen":
                _screen = Math.Clamp(Int(m, "i", 0), 0, ScreenCapturer.Screens.Length - 1);
                _forceFrame = true;
                await SendInfo(ct);
                if (VideoWanted) RequestVideo(true);
                break;
            case "video":
                _videoWidth = Math.Clamp(Int(m, "w", _videoWidth), 640, 2560);
                _videoKbps = Math.Clamp(Int(m, "kbps", _videoKbps), 500, 12000);
                RequestVideo(m.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True);
                break;
            case "keyframe":
                // The phone fell behind or lost its decoder: drop what is queued and resume at the next
                // keyframe (one per second) instead of restarting ffmpeg.
                lock (_videoGate)
                {
                    _videoQueue.Clear();
                    _videoDropping = true;
                }
                break;
            case "power":
                await SendText("{\"t\":\"power\",\"ok\":true}", ct);
                PowerActions.Run(m.GetProperty("a").GetString() ?? "");
                break;
            case "ping":
                await SendText(JsonSerializer.Serialize(new { t = "pong", ts = Num(m, "ts") }), ct);
                break;
        }
    }

    private static double Num(JsonElement m, string name) =>
        m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && double.IsFinite(v.GetDouble())
            ? Math.Clamp(v.GetDouble(), -1e7, 1e13) : 0;

    /// <summary>Whole number, tolerant: 2.0 or 1279.6 are fine, anything else gives the fallback.</summary>
    private static int Int(JsonElement m, string name, int fallback)
    {
        if (!m.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return fallback;
        if (v.TryGetInt32(out int i)) return i;
        double d = v.GetDouble();
        return double.IsFinite(d) ? (int)Math.Round(Math.Clamp(d, int.MinValue, int.MaxValue)) : fallback;
    }

    private static char Btn(JsonElement m) =>
        m.TryGetProperty("b", out var b) && b.GetString() == "r" ? 'r' : 'l';

    private async Task FrameLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _frameWanted.WaitAsync(ct);
            byte[] frame;
            try
            {
                frame = _capturer.Capture(_screen, _maxWidth, _quality);
            }
            catch (Exception)
            {
                // Capture fails on the secure desktop (UAC prompt, lock screen): tell the phone to retry.
                await SendText("{\"t\":\"same\",\"blocked\":true}", ct);
                continue;
            }

            if (!_forceFrame && _lastFrame != null && frame.AsSpan().SequenceEqual(_lastFrame))
            {
                await SendText("{\"t\":\"same\"}", ct);
                continue;
            }
            _forceFrame = false;
            _lastFrame = frame;
            await _sendLock.WaitAsync(ct);
            try { await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct); }
            finally { _sendLock.Release(); }
        }
    }

    // ---- Video -------------------------------------------------------------

    private bool VideoWanted { get { lock (_videoGate) return _videoWanted; } }

    /// <summary>Asks for the video on (restarted with the current settings) or off, without waiting.</summary>
    private void RequestVideo(bool on)
    {
        lock (_videoGate)
        {
            _videoWanted = on;
            _videoRequest++;
        }
        var ct = _ct;
        _ = Task.Run(() => ApplyVideoAsync(ct));
    }

    private async Task ApplyVideoAsync(CancellationToken ct)
    {
        try { await _videoSwitch.WaitAsync(ct); }
        catch (Exception) { return; } // connection over (token cancelled or disposed)
        try
        {
            int request;
            bool on;
            lock (_videoGate)
            {
                request = _videoRequest;
                on = _videoWanted;
            }
            if (request == _videoApplied || _videoClosed) return; // a later request already did the work
            _videoApplied = request;
            if (on && !ct.IsCancellationRequested) await StartVideo(ct);
            else StopVideo();
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException) { }
        catch (Exception ex) { Log.Error("vidéo", ex); }
        finally { _videoSwitch.Release(); }
    }

    /// <summary>Screens changed (resolution, plugged or unplugged): resend the layout and restart the capture
    /// with the new area.</summary>
    public void DisplayChanged()
    {
        var ct = _ct;
        _ = Task.Run(async () =>
        {
            try
            {
                _screen = Math.Clamp(_screen, 0, ScreenCapturer.Screens.Length - 1);
                _forceFrame = true;
                await SendInfo(ct);
                if (VideoWanted) RequestVideo(true);
            }
            catch { }
        });
    }

    private async Task StartVideo(CancellationToken ct)
    {
        StopVideo();
        VideoStream video;
        try
        {
            video = new VideoStream(ScreenCapturer.Bounds(_screen), _videoWidth, _videoKbps);
        }
        catch (Exception ex)
        {
            Log.Write("Vidéo impossible : " + ex.Message);
            await SendText(JsonSerializer.Serialize(new { t = "video", state = "error", reason = ex.Message }), ct);
            return;
        }
        video.FrameReady += (data, key) =>
        {
            lock (_videoGate)
            {
                if (_video != video) return;
                if (_videoQueue.Count >= VideoQueueMax) { _videoQueue.Clear(); _videoDropping = true; }
                if (_videoDropping && !key) return;
                _videoDropping = false;
                _videoQueue.Enqueue((data, key));
            }
            _videoReady.Release();
        };
        video.Stopped += reason =>
        {
            lock (_videoGate) { if (_video != video) return; }
            Log.Write("ffmpeg arrêté : " + reason);
            _ = SendText(JsonSerializer.Serialize(new { t = "video", state = "error", reason }), ct);
        };
        lock (_videoGate)
        {
            _video = video;
            _videoQueue.Clear();
            _videoDropping = false;
        }
        await SendText("{\"t\":\"video\",\"state\":\"on\"}", ct);
    }

    private void StopVideo()
    {
        VideoStream? old;
        lock (_videoGate)
        {
            old = _video;
            _video = null;
            _videoQueue.Clear();
        }
        old?.Dispose();
    }

    private async Task VideoLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _videoReady.WaitAsync(ct);
            (byte[] Data, bool Key) item;
            lock (_videoGate)
            {
                if (_videoQueue.Count == 0) continue;
                item = _videoQueue.Dequeue();
            }
            // First byte tells the phone what follows: 1 = H.264 keyframe, 2 = H.264 frame (JPEG starts with 0xFF).
            var msg = new byte[item.Data.Length + 1];
            msg[0] = item.Key ? (byte)1 : (byte)2;
            Buffer.BlockCopy(item.Data, 0, msg, 1, item.Data.Length);
            await _sendLock.WaitAsync(ct);
            try { await _ws.SendAsync(msg, WebSocketMessageType.Binary, true, ct); }
            finally { _sendLock.Release(); }
        }
    }

    private Task SendInfo(CancellationToken ct)
    {
        var screens = ScreenCapturer.Screens;
        var b = ScreenCapturer.Bounds(_screen);
        var info = new
        {
            t = "info",
            screen = _screen,
            count = screens.Length,
            w = b.Width,
            h = b.Height,
            pc = Environment.MachineName,
        };
        return SendText(JsonSerializer.Serialize(info), ct);
    }

    private async Task SendText(string text, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try { await _ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct); }
        finally { _sendLock.Release(); }
    }
}
