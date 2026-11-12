using System.Diagnostics;
using System.Text.RegularExpressions;
using PhoneRemote.Tools;

namespace PhoneRemote.Tunnel;

public enum TunnelState { Starting, Ready, Error }

/// <summary>
/// Runs a free Cloudflare "quick tunnel" (no account) that exposes the local server at a
/// random https://*.trycloudflare.com address. A new address is created on every start.
/// If the tunnel stops or never comes up, it is restarted on its own after 5 s, 15 s, then every 60 s,
/// and right away when the PC wakes up from sleep with the tunnel down.
/// </summary>
public sealed partial class CloudflareTunnel : IDisposable
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60)];

    private readonly int _port;
    private readonly object _gate = new();
    private Process? _process;
    private volatile bool _disposed;
    private int _generation;
    private int _failures;

    public TunnelState State { get; private set; } = TunnelState.Starting;
    public string? PublicUrl { get; private set; }
    public string? Error { get; private set; }

    public event Action? Changed;

    public CloudflareTunnel(int port)
    {
        _port = port;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume || _disposed) return;
        // A running tunnel reconnects by itself (and keeps its address); only a dead one is restarted.
        if (State != TunnelState.Ready)
        {
            Log.Write("Sortie de veille : tunnel relancé");
            _failures = 0;
            Task.Run(Start);
        }
    }

    [GeneratedRegex(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    public static string? FindExecutable() => PhoneRemote.Tools.BundledTools.Find("cloudflared.exe", Path.Combine("tools", "cloudflared.exe"));

    public void Start()
    {
        lock (_gate) StartLocked();
    }

    private void StartLocked()
    {
        if (_disposed) return;
        Stop();
        int generation = ++_generation;
        Set(TunnelState.Starting, null, null);
        Log.Write("Tunnel : démarrage");

        var exe = FindExecutable();
        if (exe == null)
        {
            Fail("cloudflared.exe est introuvable.", retry: false);
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
            PhoneRemote.Tools.ChildProcessJob.Attach(p);
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();
            _process = p;
            _ = TimeoutAsync(generation);
        }
        catch (Exception ex)
        {
            Fail("Impossible de lancer cloudflared : " + ex.Message, retry: true);
        }
    }

    /// <summary>Shows the error and, unless it can't help, schedules a new attempt.</summary>
    private void Fail(string error, bool retry)
    {
        Log.Write("Tunnel : " + error);
        if (!retry || _disposed)
        {
            Set(TunnelState.Error, null, error);
            return;
        }
        var delay = RetryDelays[Math.Min(_failures, RetryDelays.Length - 1)];
        _failures++;
        int generation = _generation;
        Set(TunnelState.Error, null, $"{error} Nouvel essai dans {delay.TotalSeconds:0} s…");
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay);
            lock (_gate)
            {
                // nothing else happened meanwhile (manual retry, stop, closing)
                if (!_disposed && generation == _generation && State == TunnelState.Error) StartLocked();
            }
        });
    }

    private async Task TimeoutAsync(int generation)
    {
        await Task.Delay(TimeSpan.FromSeconds(40));
        lock (_gate)
        {
            if (generation == _generation && State == TunnelState.Starting)
            {
                Stop();
                Fail("Cloudflare ne répond pas. Vérifie ta connexion Internet.", retry: true);
            }
        }
    }

    private void OnLine(int generation, string? line)
    {
        if (line == null || generation != _generation || State == TunnelState.Ready) return;
        var m = UrlRegex().Match(line);
        if (m.Success && !m.Value.StartsWith("https://api.", StringComparison.OrdinalIgnoreCase))
        {
            _failures = 0;
            Log.Write("Tunnel prêt");
            Set(TunnelState.Ready, m.Value, null);
        }
    }

    private void OnExited(int generation)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation) return;
            Fail("Le tunnel s'est arrêté.", retry: true);
        }
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

    public void Dispose()
    {
        _disposed = true;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        lock (_gate) Stop();
    }
}
