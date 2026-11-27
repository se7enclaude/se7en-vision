using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using PhoneRemote.Tools;

namespace PhoneRemote;

/// <summary>
/// « Se connecter à un PC » : a window showing another PC's se7en Vision page, the same page a phone opens,
/// in its desktop form (real mouse, real keyboard, full screen). It has its own browser profile, keeps the
/// browser's own shortcuts out of the way (F5, Ctrl+… go to the other PC) and only ever navigates within
/// the link it was opened with.
/// </summary>
public sealed class ViewerForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Uri _link;
    private readonly string _origin;
    private FormWindowState _stateBeforeFull = FormWindowState.Normal;
    private FormBorderStyle _borderBeforeFull = FormBorderStyle.Sizable;
    private Rectangle _boundsBeforeFull;

    /// <summary>The other PC told us its name (from the page title).</summary>
    public event Action<string>? PcNamed;

    public string Link => _link.ToString();

    public ViewerForm(string link)
    {
        _link = new Uri(link);
        _origin = _link.GetLeftPart(UriPartial.Authority);
        Text = AppInfo.Name + " — connexion…";
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        ClientSize = new Size(1280, 800);
        MinimumSize = new Size(640, 420);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(0x12, 0x11, 0x16);
        _web.DefaultBackgroundColor = BackColor;
        Controls.Add(_web);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppInfo.DataDir, "WebView2-viewer"));
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, AppInfo.Name + " a besoin de Microsoft Edge WebView2 (déjà présent sur Windows 11).\n\n" + ex.Message,
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }
        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false; // F5, Ctrl+F… belong to the other PC
        core.Settings.IsPinchZoomEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;
        core.NewWindowRequested += (_, a) => a.Handled = true;
        core.NavigationStarting += (_, a) =>
        {
            // the page may reload itself, never leave for another site
            if (!Uri.TryCreate(a.Uri, UriKind.Absolute, out var u) || u.GetLeftPart(UriPartial.Authority) != _origin) a.Cancel = true;
        };
        core.PermissionRequested += (_, a) =>
        {
            a.State = a.PermissionKind is CoreWebView2PermissionKind.ClipboardRead ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            a.Handled = true;
        };
        core.ContainsFullScreenElementChanged += (_, _) => SetFullScreen(core.ContainsFullScreenElement);
        core.DocumentTitleChanged += (_, _) =>
        {
            var t = core.DocumentTitle ?? "";
            Text = t.Length > 0 ? t : AppInfo.Name;
            const string prefix = "se7en Vision — ";
            if (t.StartsWith(prefix, StringComparison.Ordinal) && t.Length > prefix.Length) PcNamed?.Invoke(t[prefix.Length..]);
        };
        core.ProcessFailed += (_, a) => Log.Write("Viewer : WebView2 en échec (" + a.ProcessFailedKind + ")");

        var b = new UriBuilder(_link);
        var q = b.Query.TrimStart('?');
        b.Query = (q.Length > 0 ? q + "&" : "") + "ui=desktop";
        core.Navigate(b.Uri.ToString());
    }

    private void SetFullScreen(bool on)
    {
        if (on)
        {
            _stateBeforeFull = WindowState;
            _borderBeforeFull = FormBorderStyle;
            _boundsBeforeFull = Bounds;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).Bounds;
        }
        else
        {
            FormBorderStyle = _borderBeforeFull;
            Bounds = _boundsBeforeFull;
            WindowState = _stateBeforeFull;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        try { _web.Dispose(); } catch { }
    }
}
