using System.Diagnostics;
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

    public static string? FindExecutable() => PhoneRemote.Tools.BundledTools.Find("cloudflared.exe", Path.Combine("tools", "cloudflared.exe"));

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
            PhoneRemote.Tools.ChildProcessJob.Attach(p);
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

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
