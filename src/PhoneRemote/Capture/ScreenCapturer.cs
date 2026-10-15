using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PhoneRemote.Capture;

/// <summary>Grabs a screen (with the mouse cursor drawn on it) and encodes it as a downscaled JPEG.</summary>
public sealed class ScreenCapturer
{
    private static readonly ImageCodecInfo Jpeg =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public static Screen[] Screens => Screen.AllScreens.OrderByDescending(s => s.Primary).ToArray();

    public static Rectangle Bounds(int index)
    {
        var screens = Screens;
        return screens[Math.Clamp(index, 0, screens.Length - 1)].Bounds;
    }

    public byte[] Capture(int screenIndex, int maxWidth, int quality)
    {
        var bounds = Bounds(screenIndex);
        using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full))
        {
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            DrawCursor(g, bounds);
        }

        Bitmap output = full;
        try
        {
            if (bounds.Width > maxWidth)
            {
                int h = (int)Math.Round(bounds.Height * (double)maxWidth / bounds.Width);
                output = new Bitmap(maxWidth, h, PixelFormat.Format24bppRgb);
                using var g = Graphics.FromImage(output);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                g.CompositingQuality = CompositingQuality.HighSpeed;
                g.DrawImage(full, 0, 0, maxWidth, h);
            }

            using var ms = new MemoryStream();
            using var p = new EncoderParameters(1);
            p.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 20, 95));
            output.Save(ms, Jpeg, p);
            return ms.ToArray();
        }
        finally
        {
            if (!ReferenceEquals(output, full)) output.Dispose();
        }
    }

    private static void DrawCursor(Graphics g, Rectangle bounds)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || ci.flags != CURSOR_SHOWING) return;
        if (!bounds.Contains(ci.ptScreenPos.X, ci.ptScreenPos.Y)) return;

        int hotX = 0, hotY = 0;
        if (GetIconInfo(ci.hCursor, out var info))
        {
            hotX = info.xHotspot;
            hotY = info.yHotspot;
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
        }

        IntPtr hdc = g.GetHdc();
        try
        {
            DrawIconEx(hdc, ci.ptScreenPos.X - bounds.X - hotX, ci.ptScreenPos.Y - bounds.Y - hotY,
                ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
    }

    private const int CURSOR_SHOWING = 0x1, DI_NORMAL = 0x3;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT ptScreenPos; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
}
