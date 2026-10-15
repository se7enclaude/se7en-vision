using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PhoneRemote.Tunnel;

public enum TunnelState { Starting, Ready, Error }

/// <summary>
/// Runs a free Cloudflare "quick tunnel" (no account) that exposes the local server at a
/// random https://*.trycloudflare.com address. A new address is created on every start.
/// </summary>
public sealed partial class CloudflareTunnel : IDisposable
{
    private readonly int _port;
    private Process? _process;
    private bool _disposed;
    private int _generation;

    public TunnelState State { get; private set; } = TunnelState.Starting;
    public string? PublicUrl { get; private set; }
    public string? Error { get; private set; }

    public event Action? Changed;

    public CloudflareTunnel(int port) => _port = port;

    [GeneratedRegex(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    public static string? FindExecutable()
    {
        // During development: next to the exe or in the repository's tools folder.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; dir != null && i < 7; i++, dir = dir.Parent)
        {
            foreach (var candidate in new[] { Path.Combine(dir.FullName, "cloudflared.exe"), Path.Combine(dir.FullName, "tools", "cloudflared.exe") })
                if (File.Exists(candidate)) return candidate;
        }
        return ExtractEmbedded();
    }

    /// <summary>The published exe carries cloudflared.exe inside it; unpack it once into the data folder.</summary>
    private static string? ExtractEmbedded()
    {
        using var res = typeof(CloudflareTunnel).Assembly.GetManifestResourceStream("cloudflared.exe");
        if (res == null) return null;
        var target = Path.Combine(PhoneRemote.AppInfo.DataDir, "cloudflared.exe");
        if (File.Exists(target) && new FileInfo(target).Length == res.Length) return target;

        Directory.CreateDirectory(PhoneRemote.AppInfo.DataDir);
        var temp = target + ".tmp";
        using (var file = File.Create(temp)) res.CopyTo(file);
        File.Move(temp, target, overwrite: true);
        return target;
    }

    public void Start()
    {
        Stop();
        int generation = ++_generation;
        Set(TunnelState.Starting, null, null);

        var exe = FindExecutable();
        if (exe == null)
        {
            Set(TunnelState.Error, null, "cloudflared.exe est introuvable.");
            return;
        }

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var arg in new[] { "tunnel", "--no-autoupdate", "--url", $"http://127.0.0.1:{_port}" })
            psi.ArgumentList.Add(arg);

        try
        {
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => OnLine(generation, e.Data);
            p.ErrorDataReceived += (_, e) => OnLine(generation, e.Data);
            p.Exited += (_, _) => OnExited(generation);
            p.Start();
            KillWithThisApp(p);
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();
            _process = p;
            _ = TimeoutAsync(generation);
        }
        catch (Exception ex)
        {
            Set(TunnelState.Error, null, "Impossible de lancer cloudflared : " + ex.Message);
        }
    }

    private async Task TimeoutAsync(int generation)
    {
        await Task.Delay(TimeSpan.FromSeconds(40));
        if (generation == _generation && State == TunnelState.Starting)
        {
            Stop();
            Set(TunnelState.Error, null, "Cloudflare ne répond pas. Vérifie ta connexion Internet.");
        }
    }

    private void OnLine(int generation, string? line)
    {
        if (line == null || generation != _generation || State == TunnelState.Ready) return;
        var m = UrlRegex().Match(line);
        if (m.Success && !m.Value.StartsWith("https://api.", StringComparison.OrdinalIgnoreCase))
            Set(TunnelState.Ready, m.Value, null);
    }

    private void OnExited(int generation)
    {
        if (_disposed || generation != _generation) return;
        Set(TunnelState.Error, null, "Le tunnel s'est arrêté. Clique sur « Réessayer ».");
    }

    private void Set(TunnelState state, string? url, string? error)
    {
        State = state;
        PublicUrl = url;
        Error = error;
        Changed?.Invoke();
    }

    public void Stop()
    {
        var p = _process;
        _process = null;
        _generation++;
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        p.Dispose();
    }

    // A job object with KILL_ON_JOB_CLOSE ends cloudflared whenever the app exits, even after a crash.
    private static readonly Lazy<IntPtr> Job = new(() =>
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        int size = Marshal.SizeOf(info);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size);
        }
        finally { Marshal.FreeHGlobal(ptr); }
        return job;
    });

    private static void KillWithThisApp(Process p)
    {
        try { AssignProcessToJobObject(Job.Value, p.Handle); } catch { }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
