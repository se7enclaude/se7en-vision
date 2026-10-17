using System.Diagnostics;
using System.Drawing;
using PhoneRemote.Tools;

namespace PhoneRemote.Capture;

/// <summary>
/// Live H.264 stream of one screen: ffmpeg captures it (GDI, 30 fps), encodes it with x264 tuned for
/// low latency and writes an Annex B stream to stdout. The stream is cut into access units (one per
/// frame, delimited by AUD NAL units) and handed to <see cref="FrameReady"/>.
/// </summary>
public sealed class VideoStream : IDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _reader;

    /// <summary>One encoded frame (Annex B) and whether it is a keyframe.</summary>
    public event Action<byte[], bool>? FrameReady;

    /// <summary>Raised once if ffmpeg stops on its own, with its last error line.</summary>
    public event Action<string>? Stopped;

    public static string? FfmpegPath => BundledTools.Find("ffmpeg.exe", Path.Combine("tools", "ffmpeg", "ffmpeg.exe"));

    private string _lastError = "";

    public VideoStream(Rectangle screen, int width, int kbps, int fps = 30)
    {
        var exe = FfmpegPath ?? throw new FileNotFoundException("ffmpeg.exe introuvable");
        width = Math.Min(width, screen.Width) & ~1;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[]
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-f", "gdigrab", "-framerate", fps.ToString(), "-draw_mouse", "1",
            "-offset_x", screen.X.ToString(), "-offset_y", screen.Y.ToString(),
            "-video_size", $"{screen.Width}x{screen.Height}", "-i", "desktop",
            "-vf", $"scale={width}:-2:flags=bilinear,format=yuv420p",
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
            "-profile:v", "baseline", "-bf", "0", "-g", (fps * 2).ToString(),
            "-b:v", $"{kbps}k", "-maxrate", $"{kbps}k", "-bufsize", $"{kbps / 2}k",
            "-x264-params", "aud=1:repeat-headers=1",
            "-flush_packets", "1", "-f", "h264", "-",
        })
            psi.ArgumentList.Add(a);

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _lastError = e.Data; };
        _process.Start();
        ChildProcessJob.Attach(_process);
        _process.BeginErrorReadLine();
        _reader = Task.Run(ReadLoop);
    }

    private async Task ReadLoop()
    {
        var stream = _process.StandardOutput.BaseStream;
        var buf = new byte[256 * 1024];
        int len = 0;
        int auStart = -1; // offset of the current access unit's AUD start code
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (len == buf.Length) Array.Resize(ref buf, buf.Length * 2);
                int n = await stream.ReadAsync(buf.AsMemory(len), _cts.Token);
                if (n <= 0) break;
                int scanFrom = Math.Max(0, len - 4);
                len += n;

                // Every AUD (NAL type 9) starts a new frame: emit the previous one.
                for (int i = scanFrom; i + 4 < len; i++)
                {
                    if (buf[i] != 0 || buf[i + 1] != 0 || buf[i + 2] != 1 || (buf[i + 3] & 0x1F) != 9) continue;
                    int start = i > 0 && buf[i - 1] == 0 ? i - 1 : i;
                    if (auStart >= 0 && start > auStart) Emit(buf.AsSpan(auStart, start - auStart));
                    auStart = start;
                }

                // Keep only the unfinished frame in the buffer.
                if (auStart > 0)
                {
                    Buffer.BlockCopy(buf, auStart, buf, 0, len - auStart);
                    len -= auStart;
                    auStart = 0;
                }
                else if (auStart < 0 && len > 4)
                {
                    Buffer.BlockCopy(buf, len - 4, buf, 0, 4);
                    len = 4;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }

        if (!_cts.IsCancellationRequested)
            Stopped?.Invoke(string.IsNullOrEmpty(_lastError) ? "ffmpeg s'est arrêté" : _lastError);
    }

    private void Emit(ReadOnlySpan<byte> au)
    {
        bool key = false;
        for (int i = 0; i + 3 < au.Length; i++)
        {
            if (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1)
            {
                int type = au[i + 3] & 0x1F;
                if (type == 5 || type == 7) { key = true; break; }
                if (type == 1) break; // first slice is not IDR
            }
        }
        FrameReady?.Invoke(au.ToArray(), key);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        try { _reader.Wait(1000); } catch { }
        _process.Dispose();
    }
}
