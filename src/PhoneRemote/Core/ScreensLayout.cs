namespace PhoneRemote.Core;

/// <summary>One screen as the viewer sees it: position in the virtual desktop, size, refresh rate.</summary>
public sealed record ScreenInfo(int Index, int X, int Y, int W, int H, int Hz, bool Primary, string Name, int OutputIdx);

/// <summary>A screen's place inside the "all screens" picture.</summary>
public sealed record MosaicCell(int Index, int OutputIdx, int X, int Y, int W, int H);

public sealed record MosaicLayout(int W, int H, int Fps, MosaicCell[] Cells);

/// <summary>Geometry shared by host and viewer: normalised coordinates ↔ pixels, and the mosaic of all screens.</summary>
public static class ScreensLayout
{
    public const int MosaicMaxWidth = 2560;
    public const int MosaicMaxHeight = 1440;

    /// <summary>
    /// Lays every screen out as it sits on the virtual desktop, scaled so the whole picture fits in
    /// 2560×1440, each cell snapped to even pixels (encoders want even sizes). Gaps stay black.
    /// </summary>
    public static MosaicLayout Mosaic(IReadOnlyList<ScreenInfo> screens)
    {
        if (screens.Count == 0) return new MosaicLayout(2, 2, 30, Array.Empty<MosaicCell>());
        int minX = screens.Min(s => s.X), minY = screens.Min(s => s.Y);
        int maxX = screens.Max(s => s.X + s.W), maxY = screens.Max(s => s.Y + s.H);
        double scale = Math.Min(1.0, Math.Min((double)MosaicMaxWidth / (maxX - minX), (double)MosaicMaxHeight / (maxY - minY)));
        var cells = screens.Select(s => new MosaicCell(s.Index, s.OutputIdx,
            Even((s.X - minX) * scale), Even((s.Y - minY) * scale), Math.Max(2, Even(s.W * scale)), Math.Max(2, Even(s.H * scale)))).ToArray();
        int w = Math.Max(2, Even(cells.Max(c => c.X + c.W)));
        int h = Math.Max(2, Even(cells.Max(c => c.Y + c.H)));
        int fps = screens.Count > 2 ? 15 : 30;
        return new MosaicLayout(w, h, fps, cells);
    }

    /// <summary>Which screen a point (0..1 of the mosaic) falls on, and where on it (0..1). Null in a gap.</summary>
    public static (int Index, double Fx, double Fy)? Hit(MosaicLayout m, double fx, double fy)
    {
        double px = fx * m.W, py = fy * m.H;
        foreach (var c in m.Cells)
        {
            if (px >= c.X && px < c.X + c.W && py >= c.Y && py < c.Y + c.H)
                return (c.Index, Math.Clamp((px - c.X) / c.W, 0, 1), Math.Clamp((py - c.Y) / c.H, 0, 1));
        }
        return null;
    }

    /// <summary>ffmpeg xstack layout string ("0_0|w0_0" style, with absolute pixel positions).</summary>
    public static string XstackLayout(MosaicLayout m) => string.Join("|", m.Cells.Select(c => $"{c.X}_{c.Y}"));

    private static int Even(double v) => ((int)Math.Round(v)) & ~1;
}
