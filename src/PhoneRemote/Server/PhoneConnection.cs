using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PhoneRemote.Capture;
using PhoneRemote.Input;

namespace PhoneRemote.Server;

/// <summary>
/// One phone's control channel. Text frames carry JSON commands; the PC answers frame
/// requests with binary JPEGs (the phone pulls the next image once it has shown the last one).
/// </summary>
public sealed class PhoneConnection
{
    private readonly WebSocket _ws;
    private readonly InputInjector _input;
    private readonly ScreenCapturer _capturer;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _frameWanted = new(0, 1);
    private readonly CancellationTokenSource _cts = new();

    private int _screen;
    private int _maxWidth = 1280, _quality = 55;
    private byte[]? _lastFrame;
    private bool _forceFrame = true;

    public PhoneConnection(WebSocket ws, InputInjector input, ScreenCapturer capturer)
    {
        _ws = ws;
        _input = input;
        _capturer = capturer;
    }

    public void Close(int code, string reason)
    {
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
        await SendInfo(ct);
        var sender = Task.Run(() => FrameLoop(ct), ct);
        try
        {
            await ReceiveLoop(ct);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            _cts.Cancel();
            try { await sender; } catch { }
        }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var r = await _ws.ReceiveAsync(buffer, ct);
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
            }
            message.SetLength(0);
        }
    }

    private async Task Handle(byte[] json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        var m = doc.RootElement;
        string t = m.GetProperty("t").GetString() ?? "";
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
                lock (_input) _input.Click(Btn(m), m.TryGetProperty("n", out var n) ? n.GetInt32() : 1);
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
                if (m.TryGetProperty("w", out var w)) _maxWidth = Math.Clamp(w.GetInt32(), 480, 2560);
                if (m.TryGetProperty("q", out var q)) _quality = Math.Clamp(q.GetInt32(), 25, 90);
                if (_frameWanted.CurrentCount == 0) _frameWanted.Release();
                break;
            case "screen":
                _screen = Math.Clamp(m.GetProperty("i").GetInt32(), 0, ScreenCapturer.Screens.Length - 1);
                _forceFrame = true;
                await SendInfo(ct);
                break;
            case "ping":
                await SendText(JsonSerializer.Serialize(new { t = "pong", ts = Num(m, "ts") }), ct);
                break;
        }
    }

    private static double Num(JsonElement m, string name) =>
        m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && double.IsFinite(v.GetDouble())
            ? Math.Clamp(v.GetDouble(), -1e7, 1e13) : 0;

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
