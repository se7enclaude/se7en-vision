using PhoneRemote.Rtc;
using Xunit;

namespace PhoneRemote.Tests;

public class PeerSessionTests
{
    [Fact]
    public async Task Direct_link_connects_over_loopback_and_carries_video_and_data()
    {
        var r = await SelfTest.RunAsync(TimeSpan.FromSeconds(20));
        Assert.True(r.Ok, r.Summary);
        Assert.Equal(60, r.FramesSent);
        Assert.InRange(r.FramesReceived, 54, 60);
    }
}
