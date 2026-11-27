using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PhoneRemote.Core;
using PhoneRemote.Input;
using PhoneRemote.Media;
using PhoneRemote.Media.Capture;
using PhoneRemote.Tools;

namespace PhoneRemote.Server;

/// <summary>
/// One viewer (phone or PC) driving this PC. Text frames carry JSON commands; the video goes back as binary
/// frames (H.264 access units, or JPEGs for the old pull mode). Starting or stopping the capture never runs on
/// the receive loop, so mouse and keyboard keep flowing meanwhile. A viewer silent for <see cref="IdleTimeout"/>
/// (it pings every 2 s) is considered gone and the connection is dropped.
/// Speaks both the 1.4 protocol (no <c>hello</c>) and the 2.0 one.
/// </summary>
public sealed class ViewerConnection
{
    private readonly WebSocket _ws;
    private readonly HostServices _host;
    private readonly InputInjector _input;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _frameWanted = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _closing;
    private CancellationToken _ct;

    /// <summary>No message at all for this long: the viewer left without closing (Wi-Fi lost, Safari killed…).</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(10);

    // ---- viewer ----
    private int _version = 1;               // 2 once the viewer says hello
    private bool _phone = true;
    private int _screen;                    // selected screen index (primary first)
    private bool _mosaic;

    // ---- JPEG pull mode (1.4 fallback) ----
    private int _maxWidth = 1280, _quality = 55;
    private byte[]? _lastFrame;
    private bool _forceFrame = true;

    // ---- video ----
    private readonly object _videoGate = new();
    private readonly VideoSession _video;
    private WsVideoSink? _sink;
    private bool _videoWanted;
    private QualityMode _mode = QualityMode.Balanced;
    private int _v1Width = 1280, _v1Kbps = 2500;

    public string DeviceName { get; }

    public ViewerConnection(WebSocket ws, HostServices host, string deviceName)
    {
        _ws = ws;
        _host = host;
        _input = host.Input;
        DeviceName = deviceName;
        _video = new VideoSession(host.Probe);
        _video.State += OnVideoState;
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
        try
        {
            await ReceiveLoop(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Write($"Viewer muet depuis {IdleTimeout.TotalSeconds:0} s : connexion fermée");
            _ws.Abort();
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            _cts.Cancel();
            StopVideo();
            _video.Dispose();
            try { await sender; } catch { }
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
        string t = Json.Str(m, "t");
        if (_closing && t is "move" or "abs" or "down" or "up" or "click" or "wheel" or "text" or "key" or "power") return;
        switch (t)
        {
            case "hello":
                _version = Math.Max(1, Json.Int(m, "v", 2));
                _phone = Json.Str(m, "ua", "phone") != "desktop";
                await SendInfo(ct);
                break;
            case "move":
                lock (_input) _input.MoveBy(Json.Num(m, "dx"), Json.Num(m, "dy"));
                break;
            case "abs":
            {
                int s = Json.Int(m, "s", -1);
                var bounds = Displays.Bounds(s >= 0 ? s : _screen);
                lock (_input) _input.MoveTo(bounds, Json.Num(m, "x"), Json.Num(m, "y"));
                break;
            }
            case "down":
            case "up":
                lock (_input) _input.Button(Btn(m), t == "down");
                break;
            case "click":
                lock (_input) _input.Click(Btn(m), Math.Clamp(Json.Int(m, "n", 1), 1, 3));
                break;
            case "wheel":
                lock (_input) _input.Wheel((int)Json.Num(m, "dy"), (int)Json.Num(m, "dx"));
                break;
            case "text":
            {
                var s = Json.Str(m, "s");
                if (s.Length <= 2000) lock (_input) _input.Text(s);
                break;
            }
            case "key":
                lock (_input) _input.Key(Json.Str(m, "k"), Json.Strings(m, "mods"));
                break;
            case "frame":
                _maxWidth = Math.Clamp(Json.Int(m, "w", _maxWidth), 480, 2560);
                _quality = Math.Clamp(Json.Int(m, "q", _quality), 25, 90);
                if (_frameWanted.CurrentCount == 0) _frameWanted.Release();
                break;
            case "screen":
                _screen = Math.Clamp(Json.Int(m, "i", 0), 0, Displays.All().Length - 1);
                _forceFrame = true;
                await SendInfo(ct);
                if (VideoWanted) StartVideo();
                break;
            case "video":
            {
                _v1Width = Math.Clamp(Json.Int(m, "w", _v1Width), 640, 2560);
                _v1Kbps = Math.Clamp(Json.Int(m, "kbps", _v1Kbps), 500, 12000);
                if (m.TryGetProperty("mode", out _)) _mode = QualityPlanner.ParseMode(Json.Str(m, "mode"));
                bool on = Json.Bool(m, "on");
                lock (_videoGate) _videoWanted = on;
                if (on) StartVideo(); else StopVideo();
                break;
            }
            case "keyframe":
                // The viewer fell behind or lost its decoder: resume at the next keyframe.
                _sink?.DropUntilKey();
                _video.RequestKeyframe();
                break;
            case "power":
                await SendText("{\"t\":\"power\",\"ok\":true}", ct);
                PowerActions.Run(Json.Str(m, "a"));
                break;
            case "ping":
                await SendText(JsonSerializer.Serialize(new { t = "pong", ts = Json.Num(m, "ts") }), ct);
                break;
        }
    }

    private static char Btn(JsonElement m) => Json.Str(m, "b", "l") switch { "r" => 'r', "m" => 'm', _ => 'l' };

    // ---- JPEG pull mode ----------------------------------------------------

    private async Task FrameLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _frameWanted.WaitAsync(ct);
            byte[] frame;
            try
            {
                frame = _host.Capturer.Capture(_screen, _maxWidth, _quality);
            }
            catch (Exception)
            {
                // Capture fails on the secure desktop (UAC prompt, lock screen): tell the viewer to retry.
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
            await SendBinary(frame, ct);
        }
    }

    // ---- Video -------------------------------------------------------------

    private bool VideoWanted { get { lock (_videoGate) return _videoWanted; } }

    private EncodePlan BuildPlan()
    {
        var s = _host.Settings;
        var screen = Displays.Get(_screen);
        var wanted = Protocol.CodecFromName(s.Codec) ?? VideoCodec.H264;
        var enc = (_host.Probe.Best(_version >= 2 ? wanted : VideoCodec.H264, s.Encoder) ?? EncoderCatalog.Find("libx264"))!;
        bool drawMouse = true; // v2 viewers get the cursor on its own channel from M2 on
        return _version >= 2
            ? QualityPlanner.Plan(_mode, screen, enc.Caps, s.FpsCap, _phone, drawMouse, s.PowerSaver)
            : QualityPlanner.PlanV1(_v1Width, _v1Kbps, screen, enc.Caps, s.FpsCap, drawMouse, s.PowerSaver);
    }

    private void StartVideo()
    {
        var ct = _ct;
        _ = Task.Run(async () =>
        {
            try
            {
                if (!_host.Probe.Done) await _host.Probe.WaitAsync(TimeSpan.FromSeconds(3));
                var plan = BuildPlan();
                lock (_videoGate)
                {
                    if (!_videoWanted || ct.IsCancellationRequested) return;
                    if (_sink == null)
                    {
                        _sink = new WsVideoSink(SendBinary, ct) { V2 = _version >= 2 };
                        _video.Start(plan, _sink);
                    }
                    else _video.Apply(plan, "settings");
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
            catch (Exception ex) { Log.Error("vidéo", ex); }
        });
    }

    private void StopVideo()
    {
        WsVideoSink? sink;
        lock (_videoGate) { sink = _sink; _sink = null; _videoWanted = _videoWanted && !_closing && !_ct.IsCancellationRequested && false; }
        _video.Stop();
        sink?.Dispose();
    }

    private void OnVideoState(string state, string? detail)
    {
        var ct = _ct;
        if (ct.IsCancellationRequested) return;
        try
        {
            switch (state)
            {
                case "on":
                    var p = _video.Plan;
                    _ = SendText(JsonSerializer.Serialize(new { t = "video", state = "on", plan = p == null ? null : new
                        { w = p.OutWidth, h = p.OutHeight, fps = p.Fps, kbps = p.Kbps, codec = Protocol.CodecName(p.Codec), enc = p.EncoderId, gop = p.Gop } }), ct);
                    break;
                case "restart":
                    _sink?.DropUntilKey();
                    if (_version >= 2) _ = SendText(JsonSerializer.Serialize(new { t = "video", state = "restart", why = detail }), ct);
                    break;
                case "encoder":
                    Log.Write("Encodeur de secours : " + detail);
                    break;
                case "error":
                    _ = SendText(JsonSerializer.Serialize(new { t = "video", state = "error", reason = detail }), ct);
                    break;
            }
        }
        catch { }
    }

    /// <summary>Screens changed (resolution, plugged or unplugged): resend the layout and restart the capture.</summary>
    public void DisplayChanged()
    {
        var ct = _ct;
        _ = Task.Run(async () =>
        {
            try
            {
                _screen = Math.Clamp(_screen, 0, Displays.All().Length - 1);
                _forceFrame = true;
                await SendInfo(ct);
                if (VideoWanted) StartVideo();
            }
            catch { }
        });
    }

    // ---- Sending -------------------------------------------------------------

    private Task SendInfo(CancellationToken ct)
    {
        var screens = Displays.All();
        var b = screens[Math.Clamp(_screen, 0, screens.Length - 1)];
        object info = _version < 2
            ? new { t = "info", screen = _screen, count = screens.Length, w = b.W, h = b.H, pc = Environment.MachineName }
            : new
            {
                t = "info", v = Protocol.Version, screen = _screen, count = screens.Length, w = b.W, h = b.H, pc = Environment.MachineName,
                screens = screens.Select(s => new { i = s.Index, s.X, s.Y, w = s.W, h = s.H, hz = s.Hz, primary = s.Primary, name = s.Name }),
                mosaic = _mosaic,
                features = new[] { "stat" },
                quality = QualityPlanner.ModeName(_mode),
            };
        return SendText(JsonSerializer.Serialize(info), ct);
    }

    private async Task SendText(string text, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try { await _ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct); }
        finally { _sendLock.Release(); }
    }

    private async Task SendBinary(byte[] data, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try { await _ws.SendAsync(data, WebSocketMessageType.Binary, true, ct); }
        finally { _sendLock.Release(); }
    }
}
