using System.Diagnostics;
using PhoneRemote.Core;
using PhoneRemote.Tools;

namespace PhoneRemote.Media;

/// <summary>
/// One ffmpeg run writing a raw video stream to stdout. The stream is cut into access units
/// (<see cref="AccessUnitSplitter"/>) and handed to <see cref="FrameReady"/> with a monotonic timestamp.
/// </summary>
public sealed class FfmpegProcess : IDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _reader;
    private readonly AccessUnitSplitter _splitter;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private string _lastError = "";
    private int _frames;

    /// <summary>One encoded picture: bytes, keyframe?, host timestamp in microseconds.</summary>
    public event Action<byte[], bool, long>? FrameReady;

    /// <summary>Raised once if ffmpeg stops on its own, with its last error line.</summary>
    public event Action<string>? Stopped;

    public static string? FfmpegPath => BundledTools.Find("ffmpeg.exe", Path.Combine("tools", "ffmpeg", "ffmpeg.exe"));

    public int Frames => _frames;
    public string LastError => _lastError;
    public bool HasExited { get { try { return _process.HasExited; } catch { return true; } } }
    public string[] Arguments { get; }

    public FfmpegProcess(string[] args, VideoCodec codec)
    {
        var exe = FfmpegPath ?? throw new FileNotFoundException("ffmpeg.exe introuvable");
        Arguments = args;
        _splitter = new AccessUnitSplitter(codec);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _lastError = e.Data; };
        _process.Start();
        ChildProcessJob.Attach(_process);
        try { _process.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        _process.BeginErrorReadLine();
        _reader = Task.Run(ReadLoop);
    }

    private async Task ReadLoop()
    {
        var stream = _process.StandardOutput.BaseStream;
        var buf = new byte[64 * 1024];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buf, _cts.Token);
                if (n <= 0) break;
                _splitter.Push(buf.AsSpan(0, n), Emit);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }

        if (!_cts.IsCancellationRequested)
            Stopped?.Invoke(string.IsNullOrEmpty(_lastError) ? "ffmpeg s'est arrêté" : _lastError);
    }

    private void Emit(byte[] au, bool key)
    {
        Interlocked.Increment(ref _frames);
        FrameReady?.Invoke(au, key, _clock.ElapsedTicks * 1_000_000 / Stopwatch.Frequency);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        try { _reader.Wait(1000); } catch { }
        _process.Dispose();
    }
}
