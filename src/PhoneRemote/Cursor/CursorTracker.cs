using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PhoneRemote.Tools;

namespace PhoneRemote.Cursor;

/// <summary>The mouse pointer: where it is, whether it shows, and the image of its current shape.</summary>
public sealed record CursorState(int X, int Y, string ShapeId, bool Visible);

public sealed record CursorShape(string Id, int Width, int Height, int HotX, int HotY, byte[] Png);

/// <summary>
/// Watches the mouse pointer 120 times a second and reports every change of position or shape, so the viewer
/// draws the pointer itself without waiting for the video. Shapes are rendered once to PNG (with alpha,
/// including the old monochrome XOR cursors) and identified by a short hash.
/// </summary>
public sealed class CursorTracker : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<IntPtr, CursorShape?> _shapes = new();
    private Thread? _thread;
    private volatile bool _running;
    private int _subscribers;

    /// <summary>Position / visibility / shape changed. Raised on the tracker thread.</summary>
    public event Action<CursorState>? Changed;
    /// <summary>A shape seen for the first time (its PNG). Raised before the matching <see cref="Changed"/>.</summary>
    public event Action<CursorShape>? NewShape;

    public CursorState? Last { get; private set; }

    /// <summary>Starts polling (first subscriber) — call <see cref="Release"/> when done.</summary>
    public void Acquire()
    {
        lock (_gate)
        {
            if (++_subscribers > 1) return;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "cursor", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (_subscribers == 0 || --_subscribers > 0) return;
            _running = false;
        }
    }

    public CursorShape? Shape(string id)
    {
        lock (_gate) return _shapes.Values.FirstOrDefault(s => s?.Id == id);
    }

    private void Loop()
    {
        timeBeginPeriod(1);
        try
        {
            CursorState? last = null;
            long lastSent = 0;
            while (_running)
            {
                Thread.Sleep(8);
                var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
                if (!GetCursorInfo(ref ci)) continue;
                bool visible = (ci.flags & CURSOR_SHOWING) != 0;
                string shapeId = "";
                if (visible && ci.hCursor != IntPtr.Zero)
                {
                    var shape = ShapeOf(ci.hCursor);
                    shapeId = shape?.Id ?? "";
                }
                var st = new CursorState(ci.ptScreenPos.X, ci.ptScreenPos.Y, shapeId, visible);
                long now = Environment.TickCount64;
                if (st == last && now - lastSent < 2000) continue;
                last = st;
                lastSent = now;
                Last = st;
                try { Changed?.Invoke(st); } catch (Exception ex) { Log.Error("curseur", ex); }
            }
        }
        finally { timeEndPeriod(1); }
    }

    private CursorShape? ShapeOf(IntPtr hCursor)
    {
        lock (_gate)
        {
            if (_shapes.TryGetValue(hCursor, out var known)) return known;
            if (_shapes.Count > 256) _shapes.Clear(); // animated cursors churn handles
        }
        CursorShape? shape = null;
        try { shape = Render(hCursor); }
        catch (Exception ex) { Log.Error("forme du curseur", ex); }
        bool isNew;
        lock (_gate)
        {
            isNew = shape != null && !_shapes.Values.Any(s => s?.Id == shape.Id);
            _shapes[hCursor] = shape;
        }
        if (isNew) { try { NewShape?.Invoke(shape!); } catch { } }
        return shape;
    }

    /// <summary>Draws the cursor on transparent black and on transparent white to recover its alpha and XOR parts.</summary>
    private static CursorShape? Render(IntPtr hCursor)
    {
        if (!GetIconInfo(hCursor, out var info)) return null;
        int hotX = info.xHotspot, hotY = info.yHotspot;
        int w = 32, h = 32;
        try
        {
            using var mask = Image.FromHbitmap(info.hbmMask);
            w = mask.Width;
            h = info.hbmColor == IntPtr.Zero ? mask.Height / 2 : mask.Height; // monochrome: AND and XOR stacked
        }
        catch { }
        finally
        {
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
        }
        w = Math.Clamp(w, 1, 256); h = Math.Clamp(h, 1, 256);

        using var onBlack = Draw(hCursor, w, h, Color.Black);
        using var onWhite = Draw(hCursor, w, h, Color.White);
        using var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var rb = onBlack.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var rw = onWhite.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var ro = result.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < h; y++)
                {
                    byte* pb = (byte*)rb.Scan0 + y * rb.Stride, pw = (byte*)rw.Scan0 + y * rw.Stride, po = (byte*)ro.Scan0 + y * ro.Stride;
                    for (int x = 0; x < w; x++, pb += 4, pw += 4, po += 4)
                    {
                        // alpha = 1 − (white − black): pixels that changed with the background are transparent/inverting
                        int a = 255 - Math.Clamp((pw[0] + pw[1] + pw[2] - pb[0] - pb[1] - pb[2]) / 3, 0, 255);
                        if (a < 8 && pw[0] > 200 && pb[0] < 60) { po[0] = po[1] = po[2] = 0; po[3] = 96; continue; } // XOR region: faint dark edge
                        po[0] = pb[0]; po[1] = pb[1]; po[2] = pb[2]; po[3] = (byte)a;
                    }
                }
            }
        }
        finally
        {
            onBlack.UnlockBits(rb); onWhite.UnlockBits(rw); result.UnlockBits(ro);
        }
        using var ms = new MemoryStream();
        result.Save(ms, ImageFormat.Png);
        var png = ms.ToArray();
        string id = Convert.ToHexString(SHA256.HashData(png).AsSpan(0, 4)).ToLowerInvariant();
        return new CursorShape(id, w, h, hotX, hotY, png);
    }

    private static Bitmap Draw(IntPtr hCursor, int w, int h, Color bg)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(bg);
        IntPtr hdc = g.GetHdc();
        try { DrawIconEx(hdc, 0, 0, hCursor, w, h, 0, IntPtr.Zero, DI_NORMAL); }
        finally { g.ReleaseHdc(hdc); }
        return bmp;
    }

    public void Dispose() { lock (_gate) { _running = false; _subscribers = 0; } }

    private const int CURSOR_SHOWING = 0x1, DI_NORMAL = 0x3;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
}
