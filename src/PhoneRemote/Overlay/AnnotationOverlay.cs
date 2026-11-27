using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using PhoneRemote.Media.Capture;
using PhoneRemote.Tools;

namespace PhoneRemote.Overlay;

/// <summary>
/// The viewer's laser dot and pen strokes, drawn on this PC's screens: one layered window over the whole
/// virtual desktop, click-through, never activated, excluded from screen capture when Windows allows it
/// (then the viewer draws its own copy; otherwise the strokes travel inside the video). Everything is
/// cleared when the viewer leaves.
/// </summary>
public sealed class AnnotationOverlay : Form
{
    private const int LaserFadeMs = 1200;
    private readonly object _gate = new();
    private readonly List<Stroke> _strokes = new();
    private readonly Dictionary<int, Stroke> _open = new();
    private PointF? _laser; private long _laserAt; private Color _laserColor = Color.FromArgb(255, 70, 70);
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 16 };
    private bool _dirty, _shown, _enabled = true;
    private Rectangle _desktop;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>True when Windows keeps this window out of screen captures (Windows 10 2004+).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ExcludedFromCapture { get; private set; }

    private sealed class Stroke
    {
        public List<PointF> Points = new();
        public Color Color;
        public float Width;
        public int Screen;
    }

    public AnnotationOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        _tick.Tick += (_, _) => Render();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ExcludedFromCapture = SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);
        if (!ExcludedFromCapture) Log.Write("Annotations : Windows ne sait pas les cacher à la capture (elles seront dans la vidéo)");
    }

    /// <summary>Host-side switch (« Réglages ») : hidden means nothing is drawn here, the viewer still sees its own copy.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool OverlayEnabled
    {
        get => _enabled;
        set { _enabled = value; if (!value) HideNow(); }
    }

    // ---- what the viewer sends (any thread) ----

    public void Laser(double fx, double fy, int screen, string? color)
    {
        var p = ToDesktop(fx, fy, screen);
        lock (_gate) { _laser = p; _laserAt = _clock.ElapsedMilliseconds; if (color != null) _laserColor = ParseColor(color, _laserColor); _dirty = true; }
        Wake();
    }

    public void Begin(int id, double fx, double fy, int screen, string? color, double width)
    {
        var s = new Stroke { Color = ParseColor(color, Color.FromArgb(255, 70, 70)), Width = (float)Math.Clamp(width, 1, 24), Screen = screen };
        s.Points.Add(ToDesktop(fx, fy, screen));
        lock (_gate)
        {
            if (_strokes.Count >= 400) _strokes.RemoveAt(0);
            _open[id] = s; _strokes.Add(s); _dirty = true;
        }
        Wake();
    }

    public void Points(int id, double[] xy)
    {
        lock (_gate)
        {
            if (!_open.TryGetValue(id, out var s)) return;
            for (int i = 0; i + 1 < xy.Length && s.Points.Count < 20000; i += 2) s.Points.Add(ToDesktop(xy[i], xy[i + 1], s.Screen));
            _dirty = true;
        }
        Wake();
    }

    public void End(int id) { lock (_gate) _open.Remove(id); }

    public void Undo()
    {
        lock (_gate) { if (_strokes.Count > 0) { var last = _strokes[^1]; _strokes.RemoveAt(_strokes.Count - 1); foreach (var k in _open.Where(kv => kv.Value == last).Select(kv => kv.Key).ToList()) _open.Remove(k); _dirty = true; } }
        Wake();
    }

    public void Clear()
    {
        lock (_gate) { _strokes.Clear(); _open.Clear(); _laser = null; _dirty = true; }
        Wake();
    }

    // ---- drawing ----

    private void Wake()
    {
        if (!_enabled || IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(Wake); } catch { } return; }
        if (!_shown)
        {
            _desktop = SystemInformation.VirtualScreen;
            Bounds = _desktop;
            Show();
            _shown = true;
        }
        if (!_tick.Enabled) _tick.Start();
    }

    private void HideNow()
    {
        if (InvokeRequired) { try { BeginInvoke(HideNow); } catch { } return; }
        _tick.Stop();
        if (_shown) { Hide(); _shown = false; }
    }

    private void Render()
    {
        if (!_shown || IsDisposed) return;
        List<Stroke> strokes; PointF? laser; long laserAge; Color laserColor; bool dirty;
        lock (_gate)
        {
            strokes = _strokes.Select(s => new Stroke { Color = s.Color, Width = s.Width, Screen = s.Screen, Points = s.Points.ToList() }).ToList();
            laser = _laser; laserAge = _clock.ElapsedMilliseconds - _laserAt; laserColor = _laserColor;
            dirty = _dirty; _dirty = false;
        }
        bool laserLive = laser != null && laserAge < LaserFadeMs;
        if (!dirty && !laserLive)
        {
            // nothing moves: stop the timer (and the window, when it is empty)
            if (laser != null) lock (_gate) { _laser = null; }
            if (strokes.Count == 0) HideNow(); else { _tick.Stop(); Paint(strokes, null, 0, laserColor); }
            return;
        }
        if (SystemInformation.VirtualScreen != _desktop) { _desktop = SystemInformation.VirtualScreen; Bounds = _desktop; }
        Paint(strokes, laserLive ? laser : null, laserLive ? 1 - laserAge / (double)LaserFadeMs : 0, laserColor);
    }

    private void Paint(List<Stroke> strokes, PointF? laser, double laserAlpha, Color laserColor)
    {
        int w = Math.Max(1, _desktop.Width), h = Math.Max(1, _desktop.Height);
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            foreach (var s in strokes)
            {
                if (s.Points.Count == 0) continue;
                using var pen = new Pen(s.Color, s.Width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                if (s.Points.Count == 1) g.FillEllipse(new SolidBrush(s.Color), s.Points[0].X - s.Width / 2, s.Points[0].Y - s.Width / 2, s.Width, s.Width);
                else g.DrawLines(pen, s.Points.ToArray());
            }
            if (laser is { } p)
            {
                int a = (int)(255 * Math.Clamp(laserAlpha, 0, 1));
                using var glow = new SolidBrush(Color.FromArgb(a / 3, laserColor));
                using var core = new SolidBrush(Color.FromArgb(a, laserColor));
                g.FillEllipse(glow, p.X - 16, p.Y - 16, 32, 32);
                g.FillEllipse(core, p.X - 7, p.Y - 7, 14, 14);
            }
            // a small mark so the person at this PC knows where the drawing comes from
            using var f = new Font("Segoe UI", 9, FontStyle.Bold);
            using var tb = new SolidBrush(Color.FromArgb(140, 255, 255, 255));
            using var ts = new SolidBrush(Color.FromArgb(90, 0, 0, 0));
            const string mark = "se7en Vision";
            var size = g.MeasureString(mark, f);
            g.DrawString(mark, f, ts, w - size.Width - 11, h - size.Height - 9);
            g.DrawString(mark, f, tb, w - size.Width - 12, h - size.Height - 10);
        }
        Push(bmp);
    }

    private void Push(Bitmap bmp)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero), memDc = CreateCompatibleDC(screenDc), hBitmap = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            old = SelectObject(memDc, hBitmap);
            var size = new SIZE(bmp.Width, bmp.Height);
            var src = new POINT(0, 0);
            var dst = new POINT(_desktop.Left, _desktop.Top);
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memDc, old);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static PointF ToDesktop(double fx, double fy, int screen)
    {
        var b = Displays.Bounds(screen);
        return new PointF(b.Left + (float)(Math.Clamp(fx, 0, 1) * (b.Width - 1)), b.Top + (float)(Math.Clamp(fy, 0, 1) * (b.Height - 1)));
    }

    private static Color ParseColor(string? hex, Color fallback)
    {
        if (hex == null || hex.Length != 7 || hex[0] != '#') return fallback;
        try { return Color.FromArgb(255, Convert.ToInt32(hex.Substring(1, 2), 16), Convert.ToInt32(hex.Substring(3, 2), 16), Convert.ToInt32(hex.Substring(5, 2), 16)); }
        catch { return fallback; }
    }

    // ---- Win32 ----
    private const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int W, H; public SIZE(int w, int h) { W = w; H = h; } }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
}
