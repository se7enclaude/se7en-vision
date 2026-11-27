namespace PhoneRemote.Core;

public enum QualityMode { Fast, Balanced, Sharp }

/// <summary>What one ffmpeg run captures and encodes. Immutable: a change means a restart.</summary>
public sealed record EncodePlan(
    string EncoderId,
    VideoCodec Codec,
    int OutputIdx,          // ddagrab output; -1 = mosaic of all screens
    int X, int Y,           // screen position (gdigrab fallback)
    int Width, int Height,  // captured size (native)
    int ScaleWidth,         // 0 = native, else downscale to this width (CPU encoders only)
    int Fps,
    int Kbps,
    int MinKbps, int MaxKbps,
    bool DrawMouse,
    bool DupFrames,
    MosaicLayout? Mosaic)
{
    public int Gop => Math.Max(1, Fps);
    public int OutWidth => ScaleWidth > 0 && ScaleWidth < Width ? ScaleWidth : Width;
    public int OutHeight => ScaleWidth > 0 && ScaleWidth < Width ? Math.Max(2, (int)Math.Round(Height * (double)ScaleWidth / Width) & ~1) : Height;
}

/// <summary>What an encoder can do, as the planner needs it (filled from the catalog and the probe).</summary>
public sealed record EncoderCaps(string Id, VideoCodec Codec, bool Gpu, int MaxFps, int MaxWidthCpu);

/// <summary>
/// Picks resolution, frame rate and bitrate range for a quality mode. GPU encoders always take the native
/// picture (downscaling on the CPU costs more than encoding); only frame rate and bitrate move. CPU encoders
/// downscale and stay at 30 fps.
/// </summary>
public static class QualityPlanner
{
    public static QualityMode ParseMode(string? s) => s switch
    {
        "fast" or "fluid" => QualityMode.Fast,
        "sharp" => QualityMode.Sharp,
        _ => QualityMode.Balanced,
    };

    public static string ModeName(QualityMode m) => m switch { QualityMode.Fast => "fast", QualityMode.Sharp => "sharp", _ => "balanced" };

    public static EncodePlan Plan(QualityMode mode, ScreenInfo screen, EncoderCaps enc, int fpsCap, bool phone, bool drawMouse, bool powerSaver)
    {
        int hz = screen.Hz > 0 ? screen.Hz : 60;
        int fps, floor, ceil, scaleW;
        switch (mode)
        {
            case QualityMode.Fast:
                fps = Math.Min(60, hz); floor = 1500; ceil = 4000; scaleW = enc.Gpu ? 0 : 1280; break;
            case QualityMode.Sharp:
                fps = Math.Min(144, hz); floor = 8000; ceil = 30000; scaleW = enc.Gpu ? 0 : 1920; break;
            default:
                fps = Math.Min(60, hz); floor = 3000; ceil = 10000; scaleW = enc.Gpu ? 0 : 1600; break;
        }
        fps = Math.Min(fps, Math.Min(fpsCap, enc.MaxFps));
        if (!enc.Gpu) { fps = Math.Min(fps, 30); scaleW = Math.Min(scaleW, enc.MaxWidthCpu); }
        if (phone) { floor = (int)(floor * 0.7); ceil = (int)(ceil * 0.7); }
        double codecScale = enc.Codec switch { VideoCodec.H265 => 0.7, VideoCodec.Av1 => 0.6, _ => 1.0 };
        floor = (int)(floor * codecScale); ceil = (int)(ceil * codecScale);
        // start in the lower half of the range: the controller climbs when the link allows it
        int kbps = Math.Clamp((int)(floor + (ceil - floor) * 0.4), floor, ceil);
        fps = Math.Max(10, fps);
        return new EncodePlan(enc.Id, enc.Codec, screen.OutputIdx, screen.X, screen.Y, screen.W, screen.H,
            scaleW >= screen.W ? 0 : scaleW, fps, kbps, floor, ceil, drawMouse, !powerSaver, null);
    }

    /// <summary>Plan for a 1.4 viewer that asked for a width and a bitrate (no quality mode).</summary>
    public static EncodePlan PlanV1(int reqWidth, int reqKbps, ScreenInfo screen, EncoderCaps enc, int fpsCap, bool drawMouse, bool powerSaver)
    {
        int hz = screen.Hz > 0 ? screen.Hz : 60;
        int fps = Math.Min(Math.Min(60, hz), Math.Min(fpsCap, enc.MaxFps));
        int scaleW = Math.Min(reqWidth, screen.W) & ~1;
        int kbps = reqKbps;
        if (enc.Gpu)
        {
            // native picture instead of the requested width: scale the bitrate with the extra pixels (gently)
            double area = (double)screen.W * screen.H / Math.Max(1.0, (double)scaleW * scaleW * screen.H / screen.W);
            kbps = (int)Math.Clamp(reqKbps * Math.Min(3.0, Math.Pow(Math.Max(1.0, area), 0.6)), 1500, 16000);
            scaleW = 0;
        }
        else
        {
            fps = Math.Min(fps, 30);
            scaleW = Math.Min(scaleW, enc.MaxWidthCpu);
        }
        return new EncodePlan(enc.Id, enc.Codec, screen.OutputIdx, screen.X, screen.Y, screen.W, screen.H,
            scaleW >= screen.W ? 0 : scaleW, Math.Max(10, fps), kbps, Math.Max(500, kbps / 3), Math.Max(kbps, 12000), drawMouse, !powerSaver, null);
    }

    /// <summary>The "all screens" plan: CPU mosaic at the layout's frame rate.</summary>
    public static EncodePlan PlanMosaic(QualityMode mode, MosaicLayout layout, EncoderCaps enc, bool phone, bool drawMouse)
    {
        int floor = mode == QualityMode.Fast ? 1500 : 3000, ceil = mode == QualityMode.Sharp ? 16000 : 8000;
        if (phone) { floor = (int)(floor * 0.7); ceil = (int)(ceil * 0.7); }
        int kbps = (int)(floor + (ceil - floor) * 0.4);
        return new EncodePlan(enc.Id, enc.Codec, -1, 0, 0, layout.W, layout.H, 0, layout.Fps, kbps, floor, ceil, drawMouse, true, layout);
    }
}
