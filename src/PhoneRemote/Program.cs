using System.Runtime.InteropServices;
using PhoneRemote.Media;
using PhoneRemote.Server;
using PhoneRemote.Settings;
using PhoneRemote.Tools;
using PhoneRemote.Tunnel;

namespace PhoneRemote;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Seventh Hub runs this before deleting the app: remove the WebView2 profile and cloudflared copy.
        if (args.Contains("--uninstall-cleanup"))
        {
            try { Directory.Delete(AppInfo.DataDir, recursive: true); } catch { }
            return;
        }

        // Support: which encoders work here (writes encoders.json and prints it), and how the screens are numbered.
        if (args.Contains("--probe") || args.Contains("--displays"))
        {
            AttachConsole(-1);
            foreach (var s in Media.Capture.Displays.All())
                Console.WriteLine($"screen {s.Index}: {s.Name} {s.W}x{s.H}@{s.Hz} at {s.X},{s.Y} primary={s.Primary} ddagrab output_idx={s.OutputIdx}");
            if (args.Contains("--probe"))
            {
                var probe = new EncoderProbe();
                probe.RunBlocking(probeAll: args.Contains("--all"), force: true);
                Console.WriteLine(probe.ToJson());
            }
            return;
        }

        ApplicationConfiguration.Initialize();

        // Unexpected error: let go of every key/button the viewer held, log it, tell the user.
        HostServices? running = null;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => OnCrash(e.Exception, running, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => OnCrash(e.ExceptionObject as Exception, running, fatal: e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) => { e.SetObserved(); Log.Error("tâche", e.Exception); };
        Log.Write($"Démarrage v{Application.ProductVersion}");

        var settings = AppSettings.Load();
        var encoders = new EncoderProbe();
        var host = new HostServices(settings, encoders);
        running = host;
        var session = new SessionManager();
        var server = new RemoteServer(session, host);
        try
        {
            server.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Impossible de démarrer le serveur local.\n\n" + ex.Message, AppInfo.Name,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using var tunnel = new CloudflareTunnel(server.Port);
        Task.Run(tunnel.Start); // the first start may unpack cloudflared.exe (~50 MB)
        Task.Run(() => encoders.Start(settings.ProbeAll)); // unpacks ffmpeg.exe and tests the encoders (cached afterwards)

        Application.Run(new MainForm(session, server, tunnel, host));

        tunnel.Dispose();
        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Log.Write("Fermeture");
    }

    private static int _crashing;

    private static void OnCrash(Exception? ex, HostServices? host, bool fatal)
    {
        try { host?.ReleaseInput(); } catch { }
        Log.Error(fatal ? "fatale" : "inattendue", ex);
        if (Interlocked.Exchange(ref _crashing, 1) == 1) return; // one message at a time
        try
        {
            MessageBox.Show(
                (fatal ? "se7en Vision a rencontré une erreur et va se fermer." : "se7en Vision a rencontré une erreur.")
                + "\n\nPlus aucune touche ni aucun clic n'est tenu."
                + (ex is null ? "" : "\n\n" + ex.Message)
                + "\n\nDétails : " + Log.FilePath,
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
        finally { Interlocked.Exchange(ref _crashing, 0); }
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int pid);
}
