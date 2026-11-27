using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PhoneRemote.Core;
using PhoneRemote.Cursor;
using PhoneRemote.Files;
using PhoneRemote.Input;
using PhoneRemote.Media;
using PhoneRemote.Media.Capture;
using PhoneRemote.Rtc;
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
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private volatile bool _closing;
    private CancellationToken _ct;

    /// <summary>No message at all for this long: the viewer left without closing (Wi-Fi lost, Safari killed…).</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(10);

    // ---- viewer ----
    private int _version = 1;               // 2 once the viewer says hello
    private bool _phone = true;
    private HashSet<string> _caps = new();
    private int _screen;                    // selected screen index (primary first)
    private bool _mosaic;

    // ---- JPEG pull mode (1.4 fallback) ----
    private int _maxWidth = 1280, _quality = 55;
    private byte[]? _lastFrame;
    private bool _forceFrame = true;

    // ---- video ----
    private readonly object _videoGate = new();
    private readonly VideoSession _video;
    private IViewerSink? _sink;
    private string _via = "ws";             // where the viewer wants the video: "ws" or "rtc"
    private bool _videoWanted;
    private QualityMode _mode = QualityMode.Balanced;
    private int _v1Width = 1280, _v1Kbps = 2500;
    private BandwidthController? _bw;
    private int _lastDropped;
    private double _rttMs, _rttBase = double.NaN;
    private int? _viewerFps;
    private double? _viewerLoss;
    private Task? _ticker;

    // ---- direct link (WebRTC) ----
    private PeerSession? _rtc;
    private readonly Channel<string> _dcInbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    // ---- files ----
    private FileReceiver? _files;
    private readonly Dictionary<uint, FileSender> _sending = new();
    private readonly SemaphoreSlim _sendQueue = new(1, 1);
    private uint _nextFileId = 0x80000000; // ids from this side start high, the viewer's start at 1

    // ---- cursor ----
    private readonly CursorShapeCache _shapesSent = new();
    private bool _cursorOn;
    private long _lastCurMs;
    private CursorState? _pendingCur;

    public string DeviceName { get; }
    public string Transport => _sink?.Via == "rtc" ? (_rtc?.Path == "local" ? "local" : "rtc") : "ws";
    public VideoStats VideoStats => _video.Stats;
    public (double Rtt, int Fps, int Kbps) ViewerStats { get; private set; }

    public ViewerConnection(WebSocket ws, HostServices host, string deviceName)
    {
        _ws = ws;
        _host = host;
        _input = host.Input;
        DeviceName = deviceName;
        _video = new VideoSession(host.Probe);
        _video.State += OnVideoState;
        host.SettingsChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged()
    {
        if (VideoWanted) StartVideo();
    }

    public void Close(int code, string reason)
    {
        _closing = true; // from now on this connection no longer drives the PC
        _ = Task.Run(async () =>
        {
            try
            {
                if (code == 4002) { try { await SendText("{\"t\":\"bye\",\"why\":\"replaced\"}", _ct); } catch { } await Task.Delay(300); }
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
        _ticker = Task.Run(() => TickLoop(ct), ct);
        var dcPump = Task.Run(() => DataChannelLoop(ct), ct);
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
            CursorOff();
            _host.SettingsChanged -= OnSettingsChanged;
            _host.Overlay?.Clear();
            _files?.Dispose();
            lock (_sending) { foreach (var f in _sending.Values) f.Cancel(); }
            PeerSession? rtc; lock (_videoGate) { rtc = _rtc; _rtc = null; }
            rtc?.Dispose();
            try { await dcPump; } catch { }
            try { await sender; } catch { }
            try { if (_ticker != null) await _ticker; } catch { }
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
            else if (r.MessageType == WebSocketMessageType.Binary) await HandleBinary(message.ToArray(), ct);
            message.SetLength(0);
        }
    }

    private async Task Handle(byte[] json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        var m = doc.RootElement;
        string t = Json.Str(m, "t");
        if (_closing && t is "move" or "abs" or "down" or "up" or "click" or "wheel" or "text" or "key" or "power" or "kd" or "ku") return;
        switch (t)
        {
            case "hello":
                _version = Math.Max(1, Json.Int(m, "v", 2));
                _phone = Json.Str(m, "ua", "phone") != "desktop";
                _caps = Json.Strings(m, "caps").ToHashSet();
                await SendInfo(ct);
                if (_caps.Contains("cur")) CursorOn();
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
            case "kd":
            case "ku":
                lock (_input) _input.KeyCode(Json.Str(m, "c"), t == "kd");
                break;
            case "frame":
                _maxWidth = Math.Clamp(Json.Int(m, "w", _maxWidth), 480, 2560);
                _quality = Math.Clamp(Json.Int(m, "q", _quality), 25, 90);
                if (_frameWanted.CurrentCount == 0) _frameWanted.Release();
                break;
            case "screen":
                if (m.TryGetProperty("i", out var iv) && iv.ValueKind == JsonValueKind.String && iv.GetString() == "all")
                    _mosaic = Displays.All().Length > 1;
                else { _mosaic = false; _screen = Math.Clamp(Json.Int(m, "i", 0), 0, Displays.All().Length - 1); }
                _forceFrame = true;
                await SendInfo(ct);
                if (VideoWanted) StartVideo();
                break;
            case "video":
            {
                _v1Width = Math.Clamp(Json.Int(m, "w", _v1Width), 640, 2560);
                _v1Kbps = Math.Clamp(Json.Int(m, "kbps", _v1Kbps), 500, 12000);
                if (m.TryGetProperty("mode", out _)) _mode = QualityPlanner.ParseMode(Json.Str(m, "mode"));
                if (m.TryGetProperty("via", out _)) _via = Json.Str(m, "via") == "rtc" ? "rtc" : "ws";
                bool on = Json.Bool(m, "on");
                lock (_videoGate) _videoWanted = on;
                if (on) StartVideo(); else StopVideo();
                break;
            }
            case "rtc":
                await HandleRtc(m, ct);
                break;
            case "keyframe":
                // The viewer fell behind or lost its decoder: resume at the next keyframe.
                _sink?.DropUntilKey();
                _video.RequestKeyframe();
                break;
            case "draw":
                HandleDraw(m);
                break;
            case "fopen":
            case "fok":
            case "frej":
            case "fack":
            case "fdone":
            case "fcancel":
                await HandleFile(t, m, ct);
                break;
            case "curreq":
            {
                var shape = _host.Cursor.Shape(Json.Str(m, "s"));
                if (shape != null) await SendShape(shape, ct);
                break;
            }
            case "stat":
            {
                if (Json.Obj(m, "viewer") is { } v)
                {
                    _viewerFps = Json.Int(v, "fps", -1) is var f && f >= 0 ? f : null;
                    _viewerLoss = Json.Num(v, "drop", -1) is var d && d >= 0 ? d : null;
                    ViewerStats = (Json.Num(v, "rtt"), _viewerFps ?? 0, Json.Int(v, "kbps", 0));
                    _host.NotifyChanged();
                }
                break;
            }
            case "power":
                await SendText("{\"t\":\"power\",\"ok\":true}", ct);
                PowerActions.Run(Json.Str(m, "a"));
                break;
            case "ping":
            {
                double ts = Json.Num(m, "ts");
                await SendText(JsonSerializer.Serialize(new { t = "pong", ts, hostTs = _clock.Elapsed.TotalMilliseconds }), ct);
                break;
            }
            case "rtt":
                _rttMs = Math.Max(0, Json.Num(m, "ms"));
                if (double.IsNaN(_rttBase) || _rttMs < _rttBase) _rttBase = _rttMs;
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
        PeerSession? rtc; lock (_videoGate) rtc = _rtc;
        if (_via == "rtc" && rtc is { Connected: true }) wanted = rtc.Codec; // the track was negotiated for one codec
        var enc = (_host.Probe.Best(_version >= 2 ? wanted : VideoCodec.H264, s.Encoder) ?? EncoderCatalog.Find("libx264"))!;
        bool drawMouse = !_cursorOn; // v2 viewers draw the pointer themselves
        if (_mosaic && _version >= 2)
        {
            var all = Displays.All();
            if (all.Length > 1)
            {
                var mosaicEnc = _host.Probe.Best(VideoCodec.H264, s.Encoder) ?? EncoderCatalog.Find("libx264")!;
                return QualityPlanner.PlanMosaic(_mode, ScreensLayout.Mosaic(all), mosaicEnc.Caps, _phone, drawMouse);
            }
            _mosaic = false;
        }
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
                    _bw = new BandwidthController(plan);
                    var sink = PickSink(ct);
                    if (_sink == null)
                    {
                        _sink = sink;
                        _lastDropped = 0;
                        _video.Start(plan, _sink);
                    }
                    else
                    {
                        if (sink != _sink) { var old = _sink; _sink = sink; _lastDropped = 0; _video.SetSink(sink); old.Dispose(); Log.Write("Vidéo via " + sink.Via); }
                        _video.Apply(plan, "settings");
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
            catch (Exception ex) { Log.Error("vidéo", ex); }
        });
    }

    /// <summary>The direct link when the viewer asked for it and it is up, otherwise the WebSocket queue. Reuses the current sink when it already fits.</summary>
    private IViewerSink PickSink(CancellationToken ct)
    {
        bool rtc = _via == "rtc" && _rtc is { Connected: true };
        if (_sink != null && (_sink.Via == "rtc") == rtc) return _sink;
        return rtc ? new RtcVideoSink(_rtc!) : new WsVideoSink(SendBinary, ct) { V2 = _version >= 2 };
    }

    /// <summary>The direct link went down while it carried the video: back to the WebSocket without the viewer asking.</summary>
    private void FallBackToWs()
    {
        lock (_videoGate)
        {
            if (_sink is not { Via: "rtc" } || !_videoWanted) return;
            _via = "ws";
            var sink = new WsVideoSink(SendBinary, _ct) { V2 = _version >= 2 };
            var old = _sink; _sink = sink; _lastDropped = 0;
            _video.SetSink(sink);
            old.Dispose();
        }
        Log.Write("Connexion directe perdue : vidéo via le tunnel");
    }

    private void StopVideo()
    {
        IViewerSink? sink;
        lock (_videoGate) { sink = _sink; _sink = null; _bw = null; }
        _video.Stop();
        sink?.Dispose();
    }

    /// <summary>Once a second: feed the bandwidth controller and apply its decision; send host stats to v2 viewers.</summary>
    private async Task TickLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                BandwidthController? bw; IViewerSink? sink; EncodePlan? plan; PeerSession? rtc;
                lock (_videoGate) { bw = _bw; sink = _sink; plan = _video.Plan; rtc = _rtc; }
                var stats = _video.Stats;
                if (bw != null && sink != null && plan != null)
                {
                    int dropped = sink.Dropped;
                    double lossPct = Math.Min(100, (dropped - _lastDropped) * 100.0 / Math.Max(1, plan.Fps / 6.0)); // queue overflows per second, scaled
                    _lastDropped = dropped;
                    double? delay = !double.IsNaN(_rttBase) && _rttMs > 0 ? _rttMs - _rttBase : null;
                    LinkSample sample;
                    if (sink.Via == "rtc" && rtc != null)
                    {
                        var (remb, loss, _) = rtc.TakeSample();
                        sample = new LinkSample(remb, loss ?? _viewerLoss, null, _viewerFps, stats.Kbps, (int)Math.Round(stats.Fps));
                    }
                    else sample = new LinkSample(null, _viewerLoss ?? (lossPct > 0 ? lossPct : null), delay, _viewerFps, stats.Kbps, (int)Math.Round(stats.Fps));
                    var action = bw.Feed(sample, _clock.Elapsed.TotalSeconds);
                    if (action != BandwidthAction.None)
                    {
                        var next = plan with { Kbps = bw.Kbps, Fps = bw.Fps };
                        Log.Write($"Débit : {action} → {next.Kbps} kb/s, {next.Fps} i/s ({bw.EstimateSource})");
                        _video.Apply(next, action is BandwidthAction.FpsDown or BandwidthAction.FpsUp ? "fps" : "bitrate");
                    }
                }
                if (_version >= 2 && plan != null)
                {
                    await SendJson(JsonSerializer.Serialize(new
                    {
                        t = "stat",
                        host = new { fps = Math.Round(stats.Fps), kbps = stats.Kbps, enc = stats.Encoder, q = QualityPlanner.ModeName(_mode), via = sink?.Via ?? "ws",
                            restarts = stats.Restarts, target = plan.Kbps, bw = bw == null ? null : new { est = (int)bw.EstimateKbps, src = bw.EstimateSource } },
                    }), "stat", ct);
                }
                _host.NotifyChanged();
                if (_files is { Active: > 0 }) foreach (var id in _files.Sweep()) await SendText(JsonSerializer.Serialize(new { t = "fcancel", id }), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) { }
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

    // ---- Cursor --------------------------------------------------------------

    private void CursorOn()
    {
        if (_cursorOn) return;
        _cursorOn = true;
        _host.Cursor.NewShape += OnNewShape;
        _host.Cursor.Changed += OnCursor;
        _host.Cursor.Acquire();
    }

    private void CursorOff()
    {
        if (!_cursorOn) return;
        _cursorOn = false;
        _host.Cursor.Changed -= OnCursor;
        _host.Cursor.NewShape -= OnNewShape;
        _host.Cursor.Release();
    }

    private void OnNewShape(CursorShape shape)
    {
        if (_shapesSent.Seen(shape.Id)) return;
        _ = SendShape(shape, _ct);
    }

    private Task SendShape(CursorShape shape, CancellationToken ct)
    {
        _shapesSent.Seen(shape.Id);
        return SendText(JsonSerializer.Serialize(new { t = "curimg", s = shape.Id, w = shape.Width, h = shape.Height, hx = shape.HotX, hy = shape.HotY, png = Convert.ToBase64String(shape.Png) }), ct);
    }

    /// <summary>At most 120 positions a second: a change that comes too soon is sent at the next slot.</summary>
    private void OnCursor(CursorState st)
    {
        if (_ct.IsCancellationRequested) return;
        long now = _clock.ElapsedMilliseconds;
        lock (_shapesSent)
        {
            if (now - _lastCurMs < 8)
            {
                bool schedule = _pendingCur == null;
                _pendingCur = st;
                if (schedule) _ = Task.Delay(8).ContinueWith(_ => { CursorState? p; lock (_shapesSent) { p = _pendingCur; _pendingCur = null; } if (p != null) OnCursor(p); });
                return;
            }
            _lastCurMs = now;
        }
        if (st.ShapeId.Length > 0 && !_shapesSent.Seen(st.ShapeId))
        {
            var shape = _host.Cursor.Shape(st.ShapeId);
            if (shape != null) _ = SendShape(shape, _ct);
        }
        _ = SendJson($"{{\"t\":\"cur\",\"x\":{st.X},\"y\":{st.Y},\"s\":\"{st.ShapeId}\",\"v\":{(st.Visible ? "true" : "false")}}}", "cur", _ct);
    }

    // ---- Sending -------------------------------------------------------------

    private Task SendInfo(CancellationToken ct)
    {
        var screens = Displays.All();
        var b = screens[Math.Clamp(_screen, 0, screens.Length - 1)];
        object info;
        if (_version < 2)
            info = new { t = "info", screen = _screen, count = screens.Length, w = b.W, h = b.H, pc = Environment.MachineName };
        else
        {
            var s = _host.Settings;
            var enc = _host.Probe.Best(Protocol.CodecFromName(s.Codec) ?? VideoCodec.H264, s.Encoder);
            info = new
            {
                t = "info", v = Protocol.Version, screen = _screen, count = screens.Length, w = b.W, h = b.H, pc = Environment.MachineName,
                screens = screens.Select(x => new { i = x.Index, x = x.X, y = x.Y, w = x.W, h = x.H, hz = x.Hz, primary = x.Primary, name = x.Name }),
                mosaic = _mosaic ? MosaicInfo(screens) : null,
                enc = enc == null ? null : new { id = enc.Id, codec = Protocol.CodecName(enc.Codec), gpu = enc.Gpu, maxFps = Math.Min(enc.MaxFps, s.FpsCap) },
                features = Features(s),
                overlayExcluded = _host.Overlay?.ExcludedFromCapture ?? false,
                quality = QualityPlanner.ModeName(_mode),
                probing = !_host.Probe.Done,
            };
        }
        return SendText(JsonSerializer.Serialize(info), ct);
    }

    /// <summary>Small control messages take the matching data channel when the direct link is up, the WebSocket otherwise.</summary>
    private Task SendJson(string json, string channel, CancellationToken ct)
    {
        PeerSession? rtc; lock (_videoGate) rtc = _rtc;
        if (rtc != null && rtc.SendData(channel, json)) return Task.CompletedTask;
        return SendText(json, ct);
    }

    // ---- Direct link (WebRTC) ----------------------------------------------

    private async Task HandleRtc(JsonElement m, CancellationToken ct)
    {
        string sub = Json.Str(m, "sub");
        var s = _host.Settings;
        switch (sub)
        {
            case "offer":
            {
                if (!s.Rtc) { await SendText("{\"t\":\"rtc\",\"sub\":\"fail\",\"why\":\"disabled\"}", ct); return; }
                PeerSession? old;
                var peer = new PeerSession(s.AllowHevcRtc, s.RtcAllInterfaces);
                lock (_videoGate) { old = _rtc; _rtc = peer; }
                old?.Dispose();
                peer.Ice += json => _ = SendText("{\"t\":\"rtc\",\"sub\":\"ice\",\"cand\":" + json + "}", _ct);
                peer.StateChanged += state =>
                {
                    Log.Write("Connexion directe : " + state + (peer.Path.Length > 0 ? " (" + peer.Path + ")" : ""));
                    _ = SendText(JsonSerializer.Serialize(new { t = "rtc", sub = "state", s = state, path = peer.Path }), _ct);
                    if (state is "failed" or "closed" or "disconnected") FallBackToWs();
                    else if (state == "connected" && _via == "rtc" && VideoWanted) StartVideo();
                    _host.NotifyChanged();
                };
                peer.KeyframeWanted += () => { _sink?.DropUntilKey(); _video.RequestKeyframe(); };
                peer.DataText += (_, text) => _dcInbox.Writer.TryWrite(text);
                peer.DataBinary += (_ch, data) => { _ = HandleBinary(data, _ct); };
                try
                {
                    var answer = await peer.AcceptOffer(Json.Str(m, "sdp"));
                    await SendText(JsonSerializer.Serialize(new { t = "rtc", sub = "answer", sdp = answer }), ct);
                }
                catch (Exception ex)
                {
                    Log.Write("Connexion directe refusée : " + ex.Message);
                    lock (_videoGate) if (_rtc == peer) _rtc = null;
                    peer.Dispose();
                    await SendText(JsonSerializer.Serialize(new { t = "rtc", sub = "fail", why = ex.Message }), ct);
                }
                break;
            }
            case "ice":
            {
                PeerSession? rtc; lock (_videoGate) rtc = _rtc;
                if (rtc != null && m.TryGetProperty("cand", out var cand) && cand.ValueKind == JsonValueKind.Object) rtc.AddIce(cand.GetRawText());
                break;
            }
            case "close":
            {
                PeerSession? rtc; lock (_videoGate) { rtc = _rtc; _rtc = null; }
                FallBackToWs();
                rtc?.Dispose();
                break;
            }
        }
    }

    /// <summary>Messages from the data channels, handled one at a time like the WebSocket ones.</summary>
    private async Task DataChannelLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var text in _dcInbox.Reader.ReadAllAsync(ct))
            {
                try { await Handle(Encoding.UTF8.GetBytes(text), ct); }
                catch (JsonException) { }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Binary from the viewer (WebSocket or data channel): file chunks.</summary>
    private async Task HandleBinary(byte[] data, CancellationToken ct)
    {
        if (!Protocol.TryParseFileChunk(data, out var id, out var seq, out var payload)) return;
        var r = _files;
        if (r == null || _closing) return;
        long ack = r.Chunk(id, seq, payload);
        if (ack > 0) await SendText(JsonSerializer.Serialize(new { t = "fack", id, n = ack }), ct);
        else if (ack < 0) await SendText(JsonSerializer.Serialize(new { t = "fcancel", id }), ct);
    }

    private string[] Features(Settings.AppSettings s)
    {
        var f = new List<string> { "stat", "cur", "kd" };
        if (s.Rtc) f.Add("rtc");
        if (_host.Overlay != null) f.Add("draw");
        f.Add("file");
        return f.ToArray();
    }

    // ---- Files ----------------------------------------------------------------

    private FileReceiver Receiver()
    {
        if (_files != null) return _files;
        var s = _host.Settings;
        var r = new FileReceiver(s.ResolvedDownloadsDir, s.MaxFileBytes);
        r.Event += e => { _host.NotifyFile("in", e); if (e.Kind == "done") Log.Write($"Fichier reçu : {e.Name} ({FileChunker.Human(e.Size)})"); else if (e.Kind == "failed") Log.Write($"Fichier non reçu ({e.Name}) : {e.Why}"); };
        _files = r;
        return r;
    }

    private async Task HandleFile(string t, JsonElement m, CancellationToken ct)
    {
        uint id = (uint)Math.Max(0, Json.Num(m, "id"));
        switch (t)
        {
            case "fopen": // viewer → this PC
            {
                if (_closing) return;
                var why = Receiver().Open(id, Json.Str(m, "name"), (long)Json.Num(m, "size"));
                await SendText(JsonSerializer.Serialize(why == null ? new { t = "fok", id, why = (string?)null } : new { t = "frej", id, why }), ct);
                break;
            }
            case "fdone": // viewer finished sending, or viewer saved what we sent
            {
                FileSender? sender; lock (_sending) _sending.TryGetValue(id, out sender);
                if (sender != null) { sender.Done(Json.Bool(m, "ok", true)); break; }
                var path = _files?.Done(id, Json.Str(m, "sha", ""));
                await SendText(JsonSerializer.Serialize(new { t = "fdone", id, ok = path != null }), ct);
                break;
            }
            case "fok": { FileSender? sd; lock (_sending) _sending.TryGetValue(id, out sd); sd?.Ok(); break; }
            case "frej": { FileSender? sd; lock (_sending) _sending.TryGetValue(id, out sd); sd?.Rejected(Json.Str(m, "why", "refusé")); break; }
            case "fack": { FileSender? sd; lock (_sending) _sending.TryGetValue(id, out sd); sd?.Ack((uint)Math.Max(0, Json.Num(m, "n"))); break; }
            case "fcancel":
            {
                FileSender? sd; lock (_sending) _sending.TryGetValue(id, out sd);
                if (sd != null) sd.Cancel(); else _files?.Cancel(id, "annulé par l’expéditeur");
                break;
            }
        }
    }

    /// <summary>Sends a file from this PC to the viewer (drop on the host window). Transfers go one after the other.</summary>
    public async Task<string?> SendFileAsync(string path)
    {
        var ct = _ct;
        if (_version < 2) return "le viewer connecté ne sait pas recevoir de fichiers (version trop ancienne)";
        if (!File.Exists(path)) return "fichier introuvable";
        var info = new FileInfo(path);
        if (info.Length > _host.Settings.MaxFileBytes) return $"fichier trop gros (max {FileChunker.Human(_host.Settings.MaxFileBytes)})";
        var id = Interlocked.Increment(ref _nextFileId);
        var sender = new FileSender(id, path, text => SendText(text, ct), chunk => SendChunk(chunk, ct));
        lock (_sending) _sending[id] = sender;
        _host.NotifyFile("out", new FileEvent("open", id, sender.Name, sender.Size, 0, path, null, false));
        await _sendQueue.WaitAsync(ct);
        try
        {
            var why = await sender.RunAsync(ct);
            _host.NotifyFile("out", why == null
                ? new FileEvent("done", id, sender.Name, sender.Size, sender.Size, path, null, false)
                : new FileEvent("failed", id, sender.Name, sender.Size, sender.Sent, path, why, false));
            if (why == null) Log.Write($"Fichier envoyé : {sender.Name} ({FileChunker.Human(sender.Size)})"); else Log.Write($"Fichier non envoyé ({sender.Name}) : {why}");
            return why;
        }
        catch (OperationCanceledException) { return "connexion terminée"; }
        finally
        {
            _sendQueue.Release();
            lock (_sending) _sending.Remove(id);
            sender.Dispose();
        }
    }

    public void CancelFile(uint id)
    {
        FileSender? sd; lock (_sending) _sending.TryGetValue(id, out sd);
        if (sd != null) { sd.Cancel(); _ = SendText(JsonSerializer.Serialize(new { t = "fcancel", id }), _ct); }
    }

    /// <summary>
    /// A file chunk to the viewer, always on the WebSocket: 16 KiB messages sent by SIPSorcery on a data channel
    /// reach the browser damaged (its SCTP fragmentation), while the browser's own 16 KiB messages to us are fine.
    /// </summary>
    private Task SendChunk(byte[] chunk, CancellationToken ct) => SendBinary(chunk, ct);

    /// <summary>Laser dot and pen strokes: fractions of a screen, drawn by the overlay window.</summary>
    private void HandleDraw(JsonElement m)
    {
        var o = _host.Overlay;
        if (o == null || _closing) return;
        int screen = Json.Int(m, "s", _screen);
        if (screen < 0 || screen >= Displays.All().Length) screen = _screen;
        switch (Json.Str(m, "op"))
        {
            case "laser": o.Laser(Json.Num(m, "x"), Json.Num(m, "y"), screen, Json.Str(m, "c", "")); break;
            case "begin": o.Begin(Json.Int(m, "id", 0), Json.Num(m, "x"), Json.Num(m, "y"), screen, Json.Str(m, "c", ""), Json.Num(m, "w", 4)); break;
            case "pts":
            {
                if (!m.TryGetProperty("p", out var arr) || arr.ValueKind != JsonValueKind.Array) break;
                var xy = new List<double>();
                foreach (var v in arr.EnumerateArray()) { if (v.ValueKind == JsonValueKind.Number && xy.Count < 4000) xy.Add(v.GetDouble()); }
                o.Points(Json.Int(m, "id", 0), xy.ToArray());
                break;
            }
            case "end": o.End(Json.Int(m, "id", 0)); break;
            case "undo": o.Undo(); break;
            case "clear": o.Clear(); break;
        }
    }

    private static object MosaicInfo(ScreenInfo[] screens)
    {
        var m = ScreensLayout.Mosaic(screens);
        return new { w = m.W, h = m.H, fps = m.Fps, cells = m.Cells.Select(c => new { i = c.Index, x = c.X, y = c.Y, w = c.W, h = c.H }) };
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
