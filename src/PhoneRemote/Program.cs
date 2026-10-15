using PhoneRemote.Server;
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

        var session = new SessionManager();
        var server = new RemoteServer(session);
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
    }
}
