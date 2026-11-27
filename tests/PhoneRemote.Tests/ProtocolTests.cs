using System.Text.Json;
using PhoneRemote.Core;
using Xunit;

namespace PhoneRemote.Tests;

public class ProtocolTests
{
    [Fact]
    public void V1_frames_keep_the_one_byte_prefix()
    {
        var au = new byte[] { 0, 0, 1, 0x65, 1, 2, 3 };
        Assert.Equal(new byte[] { 1, 0, 0, 1, 0x65, 1, 2, 3 }, Protocol.FrameV1(au, true));
        Assert.Equal(2, Protocol.FrameV1(au, false)[0]);
    }

    [Fact]
    public void V2_frames_round_trip()
    {
        var au = new byte[] { 0, 0, 1, 0x26, 7, 8 };
        var msg = Protocol.FrameV2(au, VideoCodec.H265, true, 123_456_789_012);
        Assert.True(Protocol.TryParseV2(msg, out var codec, out var key, out var ts, out var back));
        Assert.Equal(Protocol.CodecH265, codec);
        Assert.True(key);
        Assert.Equal(123_456_789_012, ts);
        Assert.Equal(au, back.ToArray());
        Assert.False(Protocol.TryParseV2(new byte[] { 0x04, 1 }, out _, out _, out _, out _));
        Assert.False(Protocol.TryParseV2(Protocol.FrameV1(au, true), out _, out _, out _, out _));
    }

    [Fact]
    public void File_chunks_round_trip_and_reject_oversize()
    {
        var payload = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        var msg = Protocol.FileChunk(7, 42, payload);
        Assert.True(Protocol.TryParseFileChunk(msg, out var id, out var seq, out var back));
        Assert.Equal(7u, id);
        Assert.Equal(42u, seq);
        Assert.Equal(payload, back.ToArray());
        Assert.False(Protocol.TryParseFileChunk(Protocol.FileChunk(1, 1, new byte[Protocol.FileChunkSize + 1]), out _, out _, out _));
    }

    [Fact]
    public void Json_readers_are_tolerant()
    {
        using var doc = JsonDocument.Parse("""{"x":1279.6,"y":"no","n":2.0,"s":"abc","b":true,"a":["q","r",3],"o":{"k":1}}""");
        var m = doc.RootElement;
        Assert.Equal(1280, Json.Int(m, "x", 0));
        Assert.Equal(5, Json.Int(m, "y", 5));
        Assert.Equal(2, Json.Int(m, "n", 0));
        Assert.Equal(1279.6, Json.Num(m, "x"));
        Assert.Equal(0, Json.Num(m, "missing"));
        Assert.Equal("abc", Json.Str(m, "s"));
        Assert.Equal("d", Json.Str(m, "y2", "d"));
        Assert.True(Json.Bool(m, "b"));
        Assert.False(Json.Bool(m, "s"));
        Assert.Equal(new[] { "q", "r" }, Json.Strings(m, "a"));
        Assert.NotNull(Json.Obj(m, "o"));
        Assert.Null(Json.Obj(m, "s"));
    }

    [Fact]
    public void Codec_names()
    {
        Assert.Equal(VideoCodec.H265, Protocol.CodecFromName("hvc1"));
        Assert.Equal(VideoCodec.Av1, Protocol.CodecFromName("av01"));
        Assert.Null(Protocol.CodecFromName("vp9"));
        Assert.Equal("avc1", Protocol.CodecName(VideoCodec.H264));
    }
}
