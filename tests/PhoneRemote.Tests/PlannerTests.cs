using PhoneRemote.Core;
using PhoneRemote.Media;
using Xunit;

namespace PhoneRemote.Tests;

public class PlannerTests
{
    private static readonly ScreenInfo Qhd240 = new(0, 0, 0, 2560, 1440, 240, true, "Écran 1", 1);
    private static readonly ScreenInfo Fhd60 = new(1, 2560, 0, 1920, 1080, 60, false, "Écran 2", 0);
    private static readonly EncoderCaps Nvenc = new("h264_nvenc", VideoCodec.H264, true, 240, 0);
    private static readonly EncoderCaps X264 = new("libx264", VideoCodec.H264, false, 30, 1280);

    [Fact]
    public void Gpu_encoders_keep_the_native_picture_and_move_fps_and_bitrate()
    {
        var fast = QualityPlanner.Plan(QualityMode.Fast, Qhd240, Nvenc, 240, false, true, false);
        Assert.Equal(0, fast.ScaleWidth);
        Assert.Equal(2560, fast.OutWidth);
        Assert.Equal(60, fast.Fps);
        Assert.InRange(fast.Kbps, fast.MinKbps, fast.MaxKbps);
        Assert.Equal(4000, fast.MaxKbps);

        var sharp = QualityPlanner.Plan(QualityMode.Sharp, Qhd240, Nvenc, 240, false, true, false);
        Assert.Equal(144, sharp.Fps);
        Assert.Equal(30000, sharp.MaxKbps);

        var capped = QualityPlanner.Plan(QualityMode.Sharp, Qhd240, Nvenc, 100, false, true, false);
        Assert.Equal(100, capped.Fps);
        Assert.Equal(60, QualityPlanner.Plan(QualityMode.Sharp, Fhd60, Nvenc, 240, false, true, false).Fps); // screen refresh caps it
    }

    [Fact]
    public void Cpu_encoders_downscale_and_stay_at_30_fps()
    {
        var p = QualityPlanner.Plan(QualityMode.Balanced, Qhd240, X264, 240, false, true, false);
        Assert.Equal(30, p.Fps);
        Assert.Equal(1280, p.ScaleWidth);
        Assert.Equal(720, p.OutHeight);
    }

    [Fact]
    public void Phones_get_lower_ceilings_and_hevc_needs_fewer_bits()
    {
        var pc = QualityPlanner.Plan(QualityMode.Balanced, Qhd240, Nvenc, 240, false, true, false);
        var phone = QualityPlanner.Plan(QualityMode.Balanced, Qhd240, Nvenc, 240, true, true, false);
        Assert.True(phone.MaxKbps < pc.MaxKbps);
        var hevc = QualityPlanner.Plan(QualityMode.Balanced, Qhd240, new EncoderCaps("hevc_nvenc", VideoCodec.H265, true, 240, 0), 240, false, true, false);
        Assert.True(hevc.MaxKbps < pc.MaxKbps);
    }

    [Fact]
    public void A_v1_viewer_gets_native_resolution_on_a_gpu_with_a_scaled_bitrate()
    {
        var p = QualityPlanner.PlanV1(1280, 2500, Qhd240, Nvenc, 240, true, false);
        Assert.Equal(0, p.ScaleWidth);
        Assert.Equal(60, p.Fps);
        Assert.InRange(p.Kbps, 2500, 7500);
        var cpu = QualityPlanner.PlanV1(1920, 5000, Qhd240, X264, 240, true, false);
        Assert.Equal(1280, cpu.ScaleWidth); // the CPU encoder's own limit
        Assert.Equal(30, cpu.Fps);
    }

    [Fact]
    public void Power_saver_turns_off_frame_duplication()
    {
        Assert.False(QualityPlanner.Plan(QualityMode.Fast, Qhd240, Nvenc, 240, false, true, true).DupFrames);
        Assert.True(QualityPlanner.Plan(QualityMode.Fast, Qhd240, Nvenc, 240, false, true, false).DupFrames);
    }

    [Fact]
    public void Mosaic_lays_screens_out_as_on_the_desktop_within_2560_wide()
    {
        var m = ScreensLayout.Mosaic(new[] { Qhd240, Fhd60 });
        Assert.True(m.W <= 2560 && m.H <= 1440);
        Assert.Equal(2, m.Cells.Length);
        Assert.Equal(0, m.Cells[0].X);
        Assert.True(m.Cells[1].X >= m.Cells[0].W);
        Assert.All(m.Cells, c => { Assert.Equal(0, c.W % 2); Assert.Equal(0, c.H % 2); Assert.Equal(0, c.X % 2); });
        Assert.Equal(30, m.Fps);
        Assert.Contains("|", ScreensLayout.XstackLayout(m));

        var hit = ScreensLayout.Hit(m, 0.9, 0.3);
        Assert.NotNull(hit);
        Assert.Equal(1, hit!.Value.Index);
        Assert.Null(ScreensLayout.Hit(m, 0.9, 0.99)); // below the shorter second screen: a gap
        Assert.Equal(15, ScreensLayout.Mosaic(new[] { Qhd240, Fhd60, Fhd60 with { Index = 2, Y = 1080 } }).Fps);
    }

    [Fact]
    public void Ffmpeg_arguments_are_the_validated_ones()
    {
        var plan = QualityPlanner.Plan(QualityMode.Balanced, Qhd240, Nvenc, 240, false, false, false);
        var args = EncoderCatalog.Args(plan);
        Assert.Equal(new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-init_hw_device", "d3d11va", "-filter_complex" }, args.Take(7));
        Assert.Equal("ddagrab=output_idx=1:framerate=60:draw_mouse=0:dup_frames=1,setpts=N/FRAME_RATE/TB", args[7]);
        Assert.Contains("-c:v", args); Assert.Contains("h264_nvenc", args);
        Assert.Contains("-tune", args); Assert.Contains("ull", args);
        Assert.Contains("-forced-idr", args); Assert.Contains("-aud", args);
        Assert.Equal(new[] { "-f", "h264", "-" }, args.TakeLast(3));
        Assert.DoesNotContain("hwdownload", string.Join(" ", args)); // frames stay on the GPU

        var cpu = EncoderCatalog.Args(QualityPlanner.Plan(QualityMode.Balanced, Qhd240, X264, 240, false, true, false));
        Assert.Contains("hwdownload,format=bgra,scale=1280:-2:flags=fast_bilinear,format=yuv420p", cpu[7]);
        Assert.Contains("aud=1:repeat-headers=1", cpu);

        var gdi = EncoderCatalog.Args(plan with { EncoderId = "gdigrab_x264", ScaleWidth = 1280, Fps = 30 });
        Assert.Contains("gdigrab", gdi);
        Assert.Contains("-video_size", gdi); Assert.Contains("2560x1440", gdi);

        var hevc = EncoderCatalog.Args(plan with { EncoderId = "hevc_nvenc", Codec = VideoCodec.H265 });
        Assert.Equal("hevc", hevc[^2]);
        var av1 = EncoderCatalog.Args(plan with { EncoderId = "av1_nvenc", Codec = VideoCodec.Av1 });
        Assert.Equal("obu", av1[^2]);
        Assert.DoesNotContain("-aud", av1);

        var mosaic = EncoderCatalog.Args(QualityPlanner.PlanMosaic(QualityMode.Balanced, ScreensLayout.Mosaic(new[] { Qhd240, Fhd60 }), Nvenc, false, false));
        Assert.Contains("xstack=inputs=2:layout=", mosaic[7]);
        Assert.Contains("hwupload", mosaic[7]);
        Assert.Contains("fps=30", mosaic[7]);
    }
}
