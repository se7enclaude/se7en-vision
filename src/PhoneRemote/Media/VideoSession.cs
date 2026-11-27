using System.Diagnostics;
using PhoneRemote.Core;
using PhoneRemote.Tools;

namespace PhoneRemote.Media;

/// <summary>Where encoded pictures go (WebSocket queue or WebRTC track).</summary>
public interface IVideoSink
{
    void OnFrame(byte[] au, bool key, VideoCodec codec, long tsUs);
}

public sealed record VideoStats(double Fps, int Kbps, int Restarts, string Encoder);

/// <summary>
/// Runs the capture for one viewer. A plan change (bitrate, frame rate, screen, encoder) starts the new ffmpeg
/// before stopping the old one and switches at the new stream's first keyframe, so the viewer never sees a hole.
/// Restarts are budgeted; when ffmpeg dies (UAC prompt, lock screen, resolution change) it comes back with a
/// growing delay and, if an encoder keeps failing, the next one in line takes over.
/// </summary>
public sealed class VideoSession : IDisposable
{
    private readonly EncoderProbe _probe;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _switch = new(1, 1);
    private FfmpegProcess? _active, _pending;
    private IVideoSink? _sink;
    private EncodePlan? _plan;
    private bool _stopped;
    private int _restarts, _failures;
    private long _lastStartMs, _lastKeyMs, _lastRestartRequestMs;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _fpsCount; private long _fpsAt; private double _fps; private long _bytes; private int _kbps;

    /// <summary>"on" (plan applied), "restart" (why), "error" (reason), "encoder" (new encoder id).</summary>
    public event Action<string, string?>? State;

    public EncodePlan? Plan { get { lock (_gate) return _plan; } }
    public VideoStats Stats { get { lock (_gate) return new VideoStats(_fps, _kbps, _restarts, _plan?.EncoderId ?? ""); } }

    public VideoSession(EncoderProbe probe) { _probe = probe; }

    public void Start(EncodePlan plan, IVideoSink sink)
    {
        lock (_gate) { _sink = sink; _stopped = false; _failures = 0; }
        Apply(plan, "start");
    }

    /// <summary>Applies a new plan (restart with overlap). Does nothing when the plan is the same.</summary>
    public void Apply(EncodePlan plan, string why)
    {
        lock (_gate)
        {
            if (_stopped) return;
            if (_plan == plan && _active != null) return;
            _plan = plan;
        }
        _ = Task.Run(() => Launch(plan, why));
    }

    /// <summary>Makes a keyframe come soon: waits for the scheduled one if close, else restarts the encoder.</summary>
    public void RequestKeyframe()
    {
        EncodePlan? plan;
        long now = _clock.ElapsedMilliseconds;
        lock (_gate)
        {
            plan = _plan;
            if (plan == null || _stopped) return;
            long gopMs = 1000L * plan.Gop / Math.Max(1, plan.Fps);
            if (now - _lastKeyMs < gopMs - 500 && now - _lastKeyMs > 0 && (_lastKeyMs + gopMs - now) <= 500) return; // next one is due anyway
            if (now - _lastStartMs < 2000 || now - _lastRestartRequestMs < 2000) return;
            _lastRestartRequestMs = now;
        }
        _ = Task.Run(() => Launch(plan, "keyframe"));
    }

    public void Stop()
    {
        FfmpegProcess? a, p;
        lock (_gate) { _stopped = true; a = _active; p = _pending; _active = _pending = null; _sink = null; }
        a?.Dispose();
        p?.Dispose();
    }

    public void Dispose() => Stop();

    private async Task Launch(EncodePlan plan, string why)
    {
        await _switch.WaitAsync();
        try
        {
            lock (_gate) { if (_stopped || _plan != plan && why != "keyframe") return; }
            var enc = EncoderCatalog.Find(plan.EncoderId);
            if (enc == null) { Fail("encodeur inconnu"); return; }
            FfmpegProcess proc;
            try { proc = new FfmpegProcess(EncoderCatalog.Args(plan), plan.Codec); }
            catch (Exception ex) { Fail(ex.Message); return; }

            bool hadActive;
            lock (_gate)
            {
                _pending?.Dispose();
                _pending = proc;
                hadActive = _active != null;
                _lastStartMs = _clock.ElapsedMilliseconds;
                if (why is not "start" and not "keyframe") _restarts++;
            }
            if (hadActive) State?.Invoke("restart", why);
            proc.FrameReady += (au, key, ts) => OnFrame(proc, au, key, ts);
            proc.Stopped += reason => OnStopped(proc, reason);

            // wait for the first frame (keyframe) of the new stream before retiring the old one
            var deadline = _clock.ElapsedMilliseconds + 4000;
            while (_clock.ElapsedMilliseconds < deadline && proc.Frames == 0 && !proc.HasExited) await Task.Delay(20);
            if (proc.Frames == 0)
            {
                lock (_gate) { if (_pending == proc) _pending = null; }
                string reason = proc.LastError.Length > 0 ? proc.LastError : "aucune image";
                proc.Dispose();
                OnStartFailed(plan, enc, reason);
            }
        }
        finally { _switch.Release(); }
    }

    private void OnFrame(FfmpegProcess proc, byte[] au, bool key, long ts)
    {
        IVideoSink? sink;
        EncodePlan? plan;
        lock (_gate)
        {
            if (proc == _pending)
            {
                if (!key) return; // the new stream only takes over at a keyframe
                _active?.Dispose();
                _active = proc;
                _pending = null;
                _failures = 0;
                State?.Invoke("on", null);
            }
            else if (proc != _active) return;
            if (key) _lastKeyMs = _clock.ElapsedMilliseconds;
            sink = _sink; plan = _plan;
            _fpsCount++; _bytes += au.Length;
            long now = _clock.ElapsedMilliseconds;
            if (now - _fpsAt >= 1000)
            {
                _fps = _fpsCount * 1000.0 / Math.Max(1, now - _fpsAt);
                _kbps = (int)(_bytes * 8 / Math.Max(1, now - _fpsAt));
                _fpsCount = 0; _bytes = 0; _fpsAt = now;
            }
        }
        if (plan != null) sink?.OnFrame(au, key, plan.Codec, ts);
    }

    private void OnStopped(FfmpegProcess proc, string reason)
    {
        EncodePlan? plan;
        bool mine;
        lock (_gate)
        {
            mine = proc == _active;
            if (mine) _active = null;
            else if (proc == _pending) _pending = null;
            else return;
            plan = _plan;
            if (_stopped) return;
        }
        if (!mine || plan == null) return;
        Log.Write("ffmpeg arrêté : " + reason);
        // UAC prompt, lock screen, resolution change: come back with a growing delay
        int failures = Interlocked.Increment(ref _failures);
        if (failures > 6) { Fail(reason); return; }
        int delay = failures switch { 1 => 500, 2 => 1000, 3 => 2000, _ => 5000 };
        State?.Invoke("restart", "capture");
        _ = Task.Delay(delay).ContinueWith(_ => { lock (_gate) { if (_stopped || _plan != plan) return; } _ = Launch(plan, "recover"); });
    }

    private void OnStartFailed(EncodePlan plan, EncoderSpec enc, string reason)
    {
        Log.Write($"{enc.Id} ne démarre pas : {reason}");
        var next = _probe.Next(enc);
        if (next != null && next.Id != enc.Id)
        {
            var alt = plan with { EncoderId = next.Id, Codec = next.Codec, ScaleWidth = next.Gpu ? 0 : Math.Min(plan.Width, next.MaxWidthCpu), Fps = Math.Min(plan.Fps, next.MaxFps) };
            lock (_gate) { if (_stopped) return; _plan = alt; }
            State?.Invoke("encoder", next.Id);
            _ = Task.Run(() => Launch(alt, "encoder"));
            return;
        }
        int failures = Interlocked.Increment(ref _failures);
        if (failures > 5) { Fail(reason); return; }
        State?.Invoke("restart", "capture");
        _ = Task.Delay(failures * 1000).ContinueWith(_ => { lock (_gate) { if (_stopped || _plan != plan) return; } _ = Launch(plan, "recover"); });
    }

    private void Fail(string reason)
    {
        lock (_gate) { _failures = 0; }
        State?.Invoke("error", reason);
    }
}
