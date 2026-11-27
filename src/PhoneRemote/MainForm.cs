using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using PhoneRemote.Server;
using PhoneRemote.Tunnel;
using QRCoder;

namespace PhoneRemote;

/// <summary>Desktop window: hosts the HTML interface (QR code, link, PIN, status) in WebView2.</summary>
public sealed class MainForm : Form
{
    private const string Origin = "https://vision.app";

    private readonly SessionManager _session;
    private readonly RemoteServer _server;
    private readonly CloudflareTunnel _tunnel;
    private readonly HostServices _host;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly IFileProvider _files =
        new ManifestEmbeddedFileProvider(typeof(MainForm).Assembly, "wwwroot");
    private readonly Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider _types = new();
    private bool _webReady;
    private string? _qrFor, _qrSvg;
    private readonly List<ViewerForm> _viewers = new();

    public MainForm(SessionManager session, RemoteServer server, CloudflareTunnel tunnel, HostServices host)
    {
        _session = session;
        _server = server;
        _tunnel = tunnel;
        _host = host;

        Text = AppInfo.Name;
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        ClientSize = new Size(980, 660);
        MinimumSize = new Size(760, 580);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(0xED, 0xE7, 0xE1);
        _web.DefaultBackgroundColor = BackColor;
        Controls.Add(_web);

        _session.Changed += PushStateSoon;
        _tunnel.Changed += PushStateSoon;
        _host.FileChanged += (dir, ev) => { if (IsHandleCreated) BeginInvoke(() => OnFile(dir, ev)); };
        _host.Probe.Completed += PushStateSoon;
        _host.Changed += PushStateSoon;

        var tick = new System.Windows.Forms.Timer { Interval = 30_000 };
        tick.Tick += (_, _) => { if (_session.PhoneConnected) PushState(); };
        tick.Start();

        // Local testing only (VISION_DEBUG=1): paths listed in send.txt next to debug.txt are sent to the viewer.
        if (Environment.GetEnvironmentVariable("VISION_DEBUG") == "1")
        {
            var dbg = new System.Windows.Forms.Timer { Interval = 2000 };
            dbg.Tick += (_, _) =>
            {
                var f = Path.Combine(AppInfo.DataDir, "send.txt");
                if (!File.Exists(f)) return;
                string[] lines;
                try { lines = File.ReadAllLines(f); File.Delete(f); } catch { return; }
                foreach (var l in lines) if (l.Trim().Length > 0) _ = SendFile(l.Trim());
            };
            dbg.Start();
        }
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (_host.Overlay == null)
        {
            var overlay = new Overlay.AnnotationOverlay { OverlayEnabled = _host.Settings.OverlayVisible };
            _ = overlay.Handle; // the handle now (the window stays hidden): the capture-exclusion probe runs on creation
            _host.Overlay = overlay;
        }
        var dataDir = Path.Combine(AppInfo.DataDir, "WebView2");
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                AppInfo.Name + " a besoin de Microsoft Edge WebView2 (déjà présent sur Windows 11).\n\n" + ex.Message,
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnResource;
        core.WebMessageReceived += OnMessage;
        core.Settings.IsWebMessageEnabled = true;
        core.NewWindowRequested += (_, a) => a.Handled = true;
        core.Navigate(Origin + "/desktop/index.html");
    }

    private void OnResource(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var path = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
        var file = _files.GetFileInfo(path);
        var env = _web.CoreWebView2.Environment;
        if (!file.Exists || file.IsDirectory)
        {
            e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }
        if (!_types.TryGetContentType(path, out var type)) type = "application/octet-stream";
        var ms = new MemoryStream();
        using (var s = file.CreateReadStream()) s.CopyTo(ms);
        ms.Position = 0;
        e.Response = env.CreateWebResourceResponse(ms, 200, "OK", $"Content-Type: {type}\r\nCache-Control: no-store");
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string type, pin = "", url = "";
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            type = doc.RootElement.GetProperty("type").GetString() ?? "";
            if (doc.RootElement.TryGetProperty("pin", out var p)) pin = p.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("url", out var u)) url = (u.GetString() ?? "").Trim();
        }
        catch { return; }

        if (type == "sendFiles")
        {
            // files dropped on the window or picked: WebView2 hands us their paths
            foreach (var o in e.AdditionalObjects ?? Array.Empty<object>())
                if (o is CoreWebView2File f) _ = SendFile(f.Path);
            return;
        }
        switch (type)
        {
            case "setSetting":
            {
                string key = "";
                JsonElement value = default;
                try
                {
                    using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                    key = doc.RootElement.GetProperty("key").GetString() ?? "";
                    value = doc.RootElement.GetProperty("value").Clone();
                }
                catch { break; }
                var err = _host.Settings.Apply(key, value);
                if (err != null) { Post(new { type = "toast", text = err, icon = "x" }); break; }
                _host.Settings.Save();
                _host.NotifySettingsChanged();
                PushState();
                break;
            }
            case "pickDownloads":
            {
                using var dlg = new FolderBrowserDialog { Description = "Dossier où arrivent les fichiers reçus", UseDescriptionForTitle = true, SelectedPath = _host.Settings.ResolvedDownloadsDir };
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _host.Settings.DownloadsDir = dlg.SelectedPath;
                    _host.Settings.Save();
                    PushState();
                }
                break;
            }
            case "reprobe":
                _host.Probe.Start(probeAll: _host.Settings.ProbeAll, force: true);
                Post(new { type = "toast", text = "Test des encodeurs en cours…", icon = "loader" });
                PushState();
                break;
            case "openDownloads":
            {
                var dir = _host.Settings.ResolvedDownloadsDir;
                try { Directory.CreateDirectory(dir); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true }); } catch { }
                break;
            }
            case "ready":
                _webReady = true;
                PushState();
                break;
            case "newLink":
                _session.Reset();
                break;
            case "disconnect":
                _session.Reset();
                break;
            case "retryTunnel":
                _tunnel.Start();
                break;
            case "setPin":
                var error = _session.SetPersonalPin(pin);
                _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "pinSaved", ok = error == null, error }));
                break;
            case "copy":
                var link = CurrentLink();
                if (link != null) Clipboard.SetText(link);
                break;
            case "connect":
                Connect(url);
                break;
            case "forget":
                if (_host.Settings.ForgetRecent(url)) { _host.Settings.Save(); PushState(); }
                break;
        }
    }

    /// <summary>Opens the viewer window on another PC's link and remembers the link.</summary>
    private void Connect(string url)
    {
        if (!Settings.AppSettings.IsViewerLink(url))
        {
            Post(new { type = "connectError", error = "Ce n’est pas un lien se7en Vision. Il ressemble à https://….trycloudflare.com/s/…" });
            return;
        }
        if (CurrentLink() is { } mine && url.StartsWith(mine, StringComparison.Ordinal))
        {
            Post(new { type = "connectError", error = "C’est le lien de ce PC-ci. Entre celui affiché sur l’autre PC." });
            return;
        }
        var existing = _viewers.FirstOrDefault(v => v.Link == url && !v.IsDisposed);
        if (existing != null) { existing.Activate(); return; }
        _host.Settings.AddRecent(url);
        _host.Settings.Save();
        var form = new ViewerForm(url);
        form.PcNamed += name => { if (_host.Settings.RenameRecent(url, name)) { _host.Settings.Save(); PushStateSoon(); } };
        form.FormClosed += (_, _) => _viewers.Remove(form);
        _viewers.Add(form);
        form.Show();
        PushState();
    }

    private void Post(object message)
    {
        if (_webReady) _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private async Task SendFile(string path)
    {
        var conn = _server.Current;
        if (conn == null) { Post(new { type = "fileError", error = "Personne n’est connecté : il n’y a pas de destinataire." }); return; }
        var why = await conn.SendFileAsync(path);
        if (why != null) Post(new { type = "fileError", error = Path.GetFileName(path) + " : " + why });
    }

    private void OnFile(string dir, Files.FileEvent ev)
    {
        Post(new { type = "file", dir, kind = ev.Kind, id = ev.Id, name = ev.Name, size = ev.Size, done = ev.Done, why = ev.Why, risky = ev.Risky });
        PushState();
    }

    private string? CurrentLink() =>
        _tunnel.PublicUrl is { } u ? $"{u}/s/{_session.Token}" : null;

    private void PushStateSoon()
    {
        if (IsHandleCreated) BeginInvoke(PushState);
    }

    private void PushState()
    {
        WriteDebugFile();
        if (!_webReady) return;
        var link = CurrentLink();
        var qrLink = link == null ? null : link + "#k=" + _session.QrKey;
        if (qrLink != _qrFor)
        {
            _qrFor = qrLink;
            _qrSvg = qrLink == null ? null : Qr(qrLink);
        }

        var state = new
        {
            type = "state",
            link,
            qr = _qrSvg,
            qrFor = _qrFor,
            pin = _session.Pin,
            pinPersonal = _session.PinIsPersonal,
            tunnel = _tunnel.State.ToString().ToLowerInvariant(),
            tunnelError = _tunnel.Error,
            phone = _session.PhoneConnected,
            phoneName = _session.PhoneName,
            connectedMinutes = _session.ConnectedSince is { } since ? (int)(DateTime.Now - since).TotalMinutes : 0,
            failed = _session.FailedAttempts,
            maxAttempts = SessionManager.MaxAttempts,
            settings = new
            {
                encoder = _host.Settings.Encoder, codec = _host.Settings.Codec, fpsCap = _host.Settings.FpsCap, powerSaver = _host.Settings.PowerSaver,
                rtc = _host.Settings.Rtc, rtcAllInterfaces = _host.Settings.RtcAllInterfaces, allowHevcRtc = _host.Settings.AllowHevcRtc,
                overlayVisible = _host.Settings.OverlayVisible, probeAll = _host.Settings.ProbeAll,
            },
            encoders = _host.Probe.Results.Select(r => new { r.Id, label = Media.EncoderCatalog.Find(r.Id)?.Label ?? r.Id, r.Ok, fps = Math.Round(r.Fps), r.Error, r.Codec }),
            probing = !_host.Probe.Done,
            viewer = _server.Current is { } c ? new { transport = c.Transport, rtt = Math.Round(c.ViewerStats.Rtt), fps = Math.Round(c.VideoStats.Fps), kbps = c.VideoStats.Kbps, enc = c.VideoStats.Encoder } : null,
            files = _host.RecentFiles.Select(f => new { f.Dir, f.Name, f.Size, at = f.At.ToString("o") }),
            downloadsDir = _host.Settings.ResolvedDownloadsDir,
            transport = _server.Current?.Transport,
            recent = _host.Settings.Recent.Select(r => new { r.Url, r.Name, at = r.At.ToString("o"), host = new Uri(r.Url).Host, open = _viewers.Any(v => v.Link == r.Url && !v.IsDisposed) }),
        };
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(state));
    }

    private static string Qr(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        using var svg = new SvgQRCode(data);
        return svg.GetGraphic(8, "#141416", "#FFFFFF", drawQuietZones: false, sizingMode: SvgQRCode.SizingMode.ViewBoxAttribute);
    }

    /// <summary>For local testing only: set VISION_DEBUG=1 to write the local link and PIN to a file.</summary>
    private void WriteDebugFile()
    {
        if (Environment.GetEnvironmentVariable("VISION_DEBUG") != "1") return;
        var dir = AppInfo.DataDir;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "debug.txt"),
            $"local=http://127.0.0.1:{_server.Port}/s/{_session.Token}\npin={_session.Pin}\nqrkey={_session.QrKey}\npublic={CurrentLink()}\ntunnel={_tunnel.State} {_tunnel.Error}\n");
    }
}
