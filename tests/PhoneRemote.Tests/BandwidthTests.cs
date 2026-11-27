using PhoneRemote.Core;
using Xunit;

namespace PhoneRemote.Tests;

public class BandwidthTests
{
    private static BandwidthController New() =>
        new(new EncodePlan("h264_nvenc", VideoCodec.H264, 0, 0, 0, 2560, 1440, 0, 60, 5000, 3000, 10000, false, true, null));

    [Fact]
    public void Remb_drop_lowers_the_bitrate_at_once_then_climbs_back_slowly()
    {
        var c = New();
        double t = 0;
        for (; t < 12; t++) Assert.Equal(BandwidthAction.None, c.Feed(new LinkSample(RembBps: 6_000_000, SentKbps: c.Kbps, SentFps: 60), t));
        Assert.Equal(BandwidthAction.Down, c.Feed(new LinkSample(RembBps: 3_500_000, SentKbps: c.Kbps, SentFps: 60), t++));
        Assert.Equal(3000, c.Kbps); // 3.5 Mb/s × 0.85 < floor → floor
        // the link recovers: no change before five clean seconds since the drop (a receiver estimate is trusted quickly)
        for (int i = 0; i < 4; i++) Assert.Equal(BandwidthAction.None, c.Feed(new LinkSample(RembBps: 9_000_000, SentKbps: c.Kbps, SentFps: 60), t++));
        Assert.Equal(BandwidthAction.Up, c.Feed(new LinkSample(RembBps: 9_000_000, SentKbps: c.Kbps, SentFps: 60), t++));
        Assert.InRange(c.Kbps, 3001, 3750); // +25 % at most
        Assert.Equal("remb", c.EstimateSource);
    }

    [Fact]
    public void Loss_without_remb_steps_down_and_heavy_loss_may_repeat_after_two_seconds()
    {
        var c = New();
        double t = 20;
        Assert.Equal(BandwidthAction.Down, c.Feed(new LinkSample(LossPercent: 20, SentKbps: 5000, SentFps: 60), t++));
        int first = c.Kbps;
        Assert.True(first < 5000);
        Assert.Equal(BandwidthAction.None, c.Feed(new LinkSample(LossPercent: 20, SentKbps: first, SentFps: 60), t++));
        t++;
        Assert.Equal(BandwidthAction.Down, c.Feed(new LinkSample(LossPercent: 20, SentKbps: first, SentFps: 60), t++));
        Assert.True(c.Kbps < first);
        Assert.Equal("loss", c.EstimateSource);
    }

    [Fact]
    public void Stuck_at_the_floor_the_frame_rate_steps_down_then_recovers_after_twenty_clean_seconds()
    {
        var c = New();
        double t = 20;
        var actions = new List<BandwidthAction>();
        for (int i = 0; i < 40; i++) actions.Add(c.Feed(new LinkSample(RembBps: 1_000_000, SentKbps: c.Kbps, SentFps: c.Fps), t += 3));
        Assert.Contains(BandwidthAction.FpsDown, actions);
        Assert.True(c.Fps <= 30); // 60 → 30, then 20 if it keeps starving
        actions.Clear();
        for (int i = 0; i < 30; i++) actions.Add(c.Feed(new LinkSample(RembBps: 20_000_000, SentKbps: c.Kbps, SentFps: c.Fps), t += 1));
        Assert.Contains(BandwidthAction.FpsUp, actions);
        Assert.Equal(60, c.Fps);
    }

    [Fact]
    public void A_viewer_that_cannot_keep_up_counts_as_congestion()
    {
        var c = New();
        Assert.Equal(BandwidthAction.Down, c.Feed(new LinkSample(ViewerFps: 20, SentKbps: 5000, SentFps: 60), 30));
        Assert.Equal("viewer", c.EstimateSource);
    }

    [Fact]
    public void Nothing_known_means_a_careful_climb_only()
    {
        var c = New();
        double t = 0;
        var ups = 0;
        for (int i = 0; i < 40; i++) if (c.Feed(new LinkSample(SentKbps: c.Kbps, SentFps: 60), t++) == BandwidthAction.Up) ups++;
        Assert.InRange(ups, 2, 4);
        Assert.True(c.Kbps <= 10000);
    }

    [Fact]
    public void Cursor_shape_cache_is_an_lru()
    {
        var cache = new CursorShapeCache(2);
        Assert.False(cache.Seen("a"));
        Assert.False(cache.Seen("b"));
        Assert.True(cache.Seen("a"));
        Assert.False(cache.Seen("c")); // evicts b (least recent)
        Assert.False(cache.Seen("b"));
        Assert.Equal(2, cache.Count);
        Assert.Equal(8, CursorShapeCache.IdOf(new byte[] { 1, 2, 3 }).Length);
    }
}
