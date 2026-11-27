using PhoneRemote.Core;
using Xunit;

namespace PhoneRemote.Tests;

public class AnnexBTests
{
    private static byte[] Nal(int type, params byte[] payload) =>
        new byte[] { 0, 0, 0, 1, (byte)(0x60 | type) }.Concat(payload).ToArray();

    private static byte[] Aud() => Nal(9, 0xF0);
    private static byte[] Sps() => Nal(7, 0x42, 0xC0, 0x33, 0xAA);
    private static byte[] Pps() => Nal(8, 0xCE);
    private static byte[] Idr() => Nal(5, 0x88, 0x84); // first_mb_in_slice = 0 (first bit 1)
    private static byte[] P(bool first = true) => Nal(1, first ? (byte)0x9A : (byte)0x3A, 0x11);

    private static List<(byte[] Au, bool Key)> Run(VideoCodec codec, byte[] stream, int chunk)
    {
        var splitter = new AccessUnitSplitter(codec);
        var got = new List<(byte[], bool)>();
        for (int i = 0; i < stream.Length; i += chunk)
            splitter.Push(stream.AsSpan(i, Math.Min(chunk, stream.Length - i)), (au, key) => got.Add((au, key)));
        splitter.Finish((au, key) => got.Add((au, key)));
        return got;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(4096)]
    public void Splits_on_access_unit_delimiters_whatever_the_chunking(int chunk)
    {
        var stream = Aud().Concat(Sps()).Concat(Pps()).Concat(Idr())
            .Concat(Aud()).Concat(P())
            .Concat(Aud()).Concat(P()).ToArray();
        var got = Run(VideoCodec.H264, stream, chunk);
        Assert.Equal(3, got.Count);
        Assert.True(got[0].Key);
        Assert.False(got[1].Key);
        Assert.False(got[2].Key);
        Assert.Equal(stream, got.SelectMany(g => g.Au).ToArray()); // nothing lost, nothing duplicated
        Assert.All(got, g => Assert.Equal(9, g.Au[4] & 0x1F)); // every unit starts with its AUD
    }

    [Fact]
    public void Three_byte_start_codes_are_fine()
    {
        var stream = new byte[] { 0, 0, 1, 0x69, 0xF0, 0, 0, 1, 0x65, 0x88, 0, 0, 1, 0x69, 0xF0, 0, 0, 1, 0x41, 0x9A };
        var got = Run(VideoCodec.H264, stream, 5);
        Assert.Equal(2, got.Count);
        Assert.True(got[0].Key);
        Assert.Equal(stream, got.SelectMany(g => g.Au).ToArray());
    }

    [Fact]
    public void Without_delimiters_a_new_picture_starts_at_a_first_slice_or_parameter_set()
    {
        var stream = Sps().Concat(Pps()).Concat(Idr()).Concat(P(false)) // second slice of the same picture
            .Concat(P()).Concat(Sps()).Concat(Pps()).Concat(Idr()).ToArray();
        var got = Run(VideoCodec.H264, stream, 6);
        Assert.Equal(3, got.Count);
        Assert.True(got[0].Key);
        Assert.False(got[1].Key);
        Assert.True(got[2].Key);
        Assert.Equal(stream, got.SelectMany(g => g.Au).ToArray());
    }

    [Fact]
    public void Hevc_delimiters_and_irap_detection()
    {
        static byte[] N(int type, params byte[] p) => new byte[] { 0, 0, 0, 1, (byte)(type << 1), 1 }.Concat(p).ToArray();
        var stream = N(35, 0x50).Concat(N(32, 0)).Concat(N(33, 0)).Concat(N(34, 0)).Concat(N(19, 0xAF))
            .Concat(N(35, 0x50)).Concat(N(1, 0xAF)).ToArray();
        var got = Run(VideoCodec.H265, stream, 4);
        Assert.Equal(2, got.Count);
        Assert.True(got[0].Key);
        Assert.False(got[1].Key);
    }

    private static byte[] Obu(int type, params byte[] payload) =>
        new byte[] { (byte)((type << 3) | 0x02), (byte)payload.Length }.Concat(payload).ToArray();

    [Fact]
    public void Av1_temporal_units_and_keyframes()
    {
        var stream = Obu(2).Concat(Obu(1, 0x00, 0x00)).Concat(Obu(6, 0x10, 0x00)) // TD, sequence header, key frame (show_existing=0, type=0)
            .Concat(Obu(2)).Concat(Obu(6, 0x30, 0x00))                              // TD, inter frame (type=1)
            .Concat(Obu(2)).Concat(Obu(6, 0x10, 0x00)).ToArray();                   // TD, key frame
        var got = Run(VideoCodec.Av1, stream, 3);
        Assert.Equal(3, got.Count);
        Assert.True(got[0].Key);
        Assert.False(got[1].Key);
        Assert.True(got[2].Key);
        Assert.Equal(stream, got.SelectMany(g => g.Au).ToArray());
    }

    [Fact]
    public void Profile_level_id_comes_from_the_sps()
    {
        var au = Aud().Concat(Sps()).Concat(Idr()).ToArray();
        Assert.Equal("42C033", AccessUnitSplitter.H264ProfileLevelId(au));
        Assert.Null(AccessUnitSplitter.H264ProfileLevelId(P()));
    }

    [Fact]
    public void Large_units_grow_the_buffer()
    {
        var big = Aud().Concat(Idr()).Concat(new byte[600 * 1024]).Concat(Aud()).Concat(P()).ToArray();
        var got = Run(VideoCodec.H264, big, 50_000);
        Assert.Equal(2, got.Count);
        Assert.Equal(big.Length, got.Sum(g => g.Au.Length));
    }
}
