using PhoneRemote.Server;
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

        ApplicationConfiguration.Initialize();

        // Unexpected error: let go of every key/button the phone held, log it, tell the user.
        RemoteServer? running = null;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => OnCrash(e.Exception, running, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => OnCrash(e.ExceptionObject as Exception, running, fatal: e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) => { e.SetObserved(); Log.Error("tâche", e.Exception); };
        Log.Write($"Démarrage v{Application.ProductVersion}");

        var session = new SessionManager();
        var server = new RemoteServer(session);
        running = server;
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

        Application.Run(new MainForm(session, server, tunnel));

        tunnel.Dispose();
        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Log.Write("Fermeture");
    }

    private static int _crashing;

    private static void OnCrash(Exception? ex, RemoteServer? server, bool fatal)
    {
        try { server?.ReleaseInput(); } catch { }
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
}
