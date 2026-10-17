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
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly IFileProvider _files =
        new ManifestEmbeddedFileProvider(typeof(MainForm).Assembly, "wwwroot");
    private readonly Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider _types = new();
    private bool _webReady;
    private string? _qrFor, _qrSvg;

    public MainForm(SessionManager session, RemoteServer server, CloudflareTunnel tunnel)
    {
        _session = session;
        _server = server;
        _tunnel = tunnel;

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

        var tick = new System.Windows.Forms.Timer { Interval = 30_000 };
        tick.Tick += (_, _) => { if (_session.PhoneConnected) PushState(); };
        tick.Start();
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
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
        string type, pin = "";
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            type = doc.RootElement.GetProperty("type").GetString() ?? "";
            if (doc.RootElement.TryGetProperty("pin", out var p)) pin = p.GetString() ?? "";
        }
        catch { return; }

        switch (type)
        {
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
                var url = CurrentLink();
                if (url != null) Clipboard.SetText(url);
                break;
        }
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
