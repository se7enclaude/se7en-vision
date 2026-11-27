using PhoneRemote.Core;

namespace PhoneRemote.Media;

/// <summary>An encoder ffmpeg may offer, and how it is driven.</summary>
public sealed record EncoderSpec(string Id, string Label, VideoCodec Codec, string Vendor, bool Gpu, int Tier, int MaxFps, int MaxWidthCpu)
{
    public EncoderCaps Caps => new(Id, Codec, Gpu, MaxFps, MaxWidthCpu);
}

/// <summary>
/// The ffmpeg command lines. Capture is <c>ddagrab</c> (DXGI Desktop Duplication): frames stay on the GPU and go
/// straight into NVENC / AMF; Intel QSV maps them; Media Foundation and libx264 download them first. Every
/// argument comes from typed values (ints, enum ids), never from viewer text.
/// </summary>
public static class EncoderCatalog
{
    public static readonly EncoderSpec[] All =
    {
        new("h264_nvenc", "NVIDIA (H.264)", VideoCodec.H264, "nvidia", true, 1, 240, 0),
        new("hevc_nvenc", "NVIDIA (HEVC)", VideoCodec.H265, "nvidia", true, 1, 240, 0),
        new("av1_nvenc", "NVIDIA (AV1)", VideoCodec.Av1, "nvidia", true, 1, 240, 0),
        new("h264_amf", "AMD (H.264)", VideoCodec.H264, "amd", true, 2, 240, 0),
        new("hevc_amf", "AMD (HEVC)", VideoCodec.H265, "amd", true, 2, 240, 0),
        new("av1_amf", "AMD (AV1)", VideoCodec.Av1, "amd", true, 2, 240, 0),
        new("h264_qsv", "Intel (H.264)", VideoCodec.H264, "intel", true, 3, 120, 0),
        new("hevc_qsv", "Intel (HEVC)", VideoCodec.H265, "intel", true, 3, 120, 0),
        new("av1_qsv", "Intel (AV1)", VideoCodec.Av1, "intel", true, 3, 120, 0),
        new("h264_mf", "Windows (H.264)", VideoCodec.H264, "any", false, 4, 30, 1920),
        new("libx264", "Processeur (H.264)", VideoCodec.H264, "cpu", false, 5, 30, 1280),
        new("gdigrab_x264", "Processeur (compatibilité)", VideoCodec.H264, "cpu", false, 6, 30, 1280),
    };

    public static EncoderSpec? Find(string id) => All.FirstOrDefault(e => e.Id == id);

    public static string Muxer(VideoCodec c) => c switch { VideoCodec.H265 => "hevc", VideoCodec.Av1 => "obu", _ => "h264" };

    /// <summary>The full argument list (after "ffmpeg") for a live stream to stdout.</summary>
    public static string[] Args(EncodePlan p)
    {
        var enc = Find(p.EncoderId) ?? All[^2];
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        int bufsize = Math.Max(100, p.Kbps * 2 / Math.Max(1, p.Fps));

        // ---- capture ----
        if (enc.Id == "gdigrab_x264")
        {
            a.AddRange(new[] { "-f", "gdigrab", "-framerate", p.Fps.ToString(), "-draw_mouse", p.DrawMouse ? "1" : "0",
                "-offset_x", p.X.ToString(), "-offset_y", p.Y.ToString(), "-video_size", $"{p.Width}x{p.Height}", "-i", "desktop",
                "-vf", $"scale={p.OutWidth}:-2:flags=fast_bilinear,format=yuv420p" });
        }
        else
        {
            if (enc.Vendor == "intel")
                a.AddRange(new[] { "-init_hw_device", "d3d11va=dx", "-init_hw_device", "qsv=qs@dx", "-filter_hw_device", "dx" });
            else
                a.AddRange(new[] { "-init_hw_device", "d3d11va" });
            a.Add("-filter_complex");
            a.Add(p.Mosaic != null ? MosaicGraph(p, enc) : SingleGraph(p, enc));
        }

        // ---- encoder ----
        a.AddRange(EncoderArgs(enc, p, bufsize));
        a.AddRange(new[] { "-flush_packets", "1", "-f", Muxer(enc.Codec), "-" });
        return a.ToArray();
    }

    private static string Source(EncodePlan p, int outputIdx, int fps) =>
        $"ddagrab=output_idx={outputIdx}:framerate={fps}:draw_mouse={(p.DrawMouse ? 1 : 0)}:dup_frames={(p.DupFrames ? 1 : 0)}" +
        (p.DupFrames ? ",setpts=N/FRAME_RATE/TB" : "");

    private static string SingleGraph(EncodePlan p, EncoderSpec enc)
    {
        string src = Source(p, p.OutputIdx, p.Fps);
        return enc.Vendor switch
        {
            "nvidia" or "amd" => src,
            "intel" => src + ",hwmap=derive_device=qsv,format=qsv",
            _ => src + ",hwdownload,format=bgra" + (p.ScaleWidth > 0 ? $",scale={p.OutWidth}:-2:flags=fast_bilinear" : "") +
                 (enc.Id == "h264_mf" ? ",format=nv12" : ",format=yuv420p"),
        };
    }

    private static string MosaicGraph(EncodePlan p, EncoderSpec enc)
    {
        var m = p.Mosaic!;
        var parts = new List<string>();
        var labels = new List<string>();
        for (int i = 0; i < m.Cells.Length; i++)
        {
            var c = m.Cells[i];
            parts.Add($"ddagrab=output_idx={c.OutputIdx}:framerate={m.Fps}:draw_mouse={(p.DrawMouse ? 1 : 0)},hwdownload,format=bgra,scale={c.W}:{c.H}:flags=fast_bilinear,fps={m.Fps}[c{i}]");
            labels.Add($"[c{i}]");
        }
        string fmt = enc.Gpu ? "nv12" : "yuv420p";
        string stack = m.Cells.Length == 1
            ? $"{labels[0]}format={fmt}"
            : $"{string.Concat(labels)}xstack=inputs={m.Cells.Length}:layout={ScreensLayout.XstackLayout(m)}:fill=black,format={fmt}";
        if (enc.Vendor is "nvidia" or "amd") stack += ",hwupload"; // back to the GPU for the encoder (d3d11 frames)
        else if (enc.Vendor == "intel") stack += ",hwupload=extra_hw_frames=8";
        return string.Join(";", parts) + ";" + stack;
    }

    private static IEnumerable<string> EncoderArgs(EncoderSpec enc, EncodePlan p, int bufsize)
    {
        string k = $"{p.Kbps}k", maxrate = $"{p.Kbps}k", buf = $"{bufsize}k", g = p.Gop.ToString();
        switch (enc.Vendor)
        {
            case "nvidia":
            {
                var l = new List<string> { "-c:v", enc.Id, "-preset", "p1", "-tune", "ull", "-zerolatency", "1", "-rc", "cbr",
                    "-b:v", k, "-maxrate", maxrate, "-bufsize", buf, "-g", g, "-bf", "0", "-forced-idr", "1", "-delay", "0" };
                if (enc.Codec == VideoCodec.H264) l.AddRange(new[] { "-profile:v", "baseline", "-aud", "1" });
                else if (enc.Codec == VideoCodec.H265) l.AddRange(new[] { "-profile:v", "main", "-aud", "1" });
                return l;
            }
            case "amd":
            {
                var l = new List<string> { "-c:v", enc.Id, "-usage", "ultralowlatency", "-quality", "speed", "-rc", "cbr",
                    "-b:v", k, "-maxrate", maxrate, "-bufsize", buf, "-g", g, "-bf", "0", "-forced_idr", "1" };
                if (enc.Codec == VideoCodec.H264) l.AddRange(new[] { "-profile:v", "constrained_baseline", "-aud", "1" });
                else if (enc.Codec == VideoCodec.H265) l.AddRange(new[] { "-profile:v", "main", "-aud", "1" });
                return l;
            }
            case "intel":
            {
                var l = new List<string> { "-c:v", enc.Id, "-preset", "veryfast", "-low_power", "1", "-scenario", "remotegaming",
                    "-b:v", k, "-maxrate", maxrate, "-bufsize", buf, "-g", g, "-bf", "0", "-forced_idr", "1" };
                if (enc.Codec == VideoCodec.H264) l.AddRange(new[] { "-profile:v", "baseline", "-aud", "1", "-repeat_pps", "1" });
                else if (enc.Codec == VideoCodec.H265) l.AddRange(new[] { "-profile:v", "main", "-aud", "1" });
                return l;
            }
            case "any": // Media Foundation
                return new[] { "-c:v", "h264_mf", "-rate_control", "cbr", "-b:v", k, "-g", g, "-bf", "0" };
            default: // libx264 (ddagrab or gdigrab)
                return new[] { "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-profile:v", "baseline", "-bf", "0",
                    "-g", g, "-b:v", k, "-maxrate", maxrate, "-bufsize", $"{Math.Max(100, p.Kbps / 2)}k",
                    "-x264-params", "aud=1:repeat-headers=1" };
        }
    }

    /// <summary>A short run of the real pipeline, used by the probe (60 fps on one output, 4 Mb/s).</summary>
    public static EncodePlan ProbePlan(EncoderSpec enc, ScreenInfo screen) =>
        new(enc.Id, enc.Codec, screen.OutputIdx, screen.X, screen.Y, screen.W, screen.H, enc.Gpu ? 0 : Math.Min(1280, screen.W),
            Math.Min(60, enc.MaxFps), 4000, 1000, 8000, false, true, null);
}
