using System.Text.Json;

namespace PhoneRemote.Core;

/// <summary>
/// What viewer and host say to each other, on the WebSocket or on WebRTC data channels. Text messages are JSON
/// with a <c>t</c> field. Binary messages start with one byte that says what follows.
/// </summary>
public static class Protocol
{
    public const int Version = 2;

    // ---- binary framing (WebSocket) ----
    public const byte BinJpeg = 0xFF;      // raw JPEG (v1: first byte of a JPEG is 0xFF)
    public const byte BinH264Key = 0x01;   // v1: H.264 access unit, keyframe
    public const byte BinH264Delta = 0x02; // v1: H.264 access unit, other frame
    public const byte BinFileChunk = 0x03; // [0x03][id u32 LE][seq u32 LE][payload]
    public const byte BinVideo = 0x04;     // v2: [0x04][codec u8][flags u8][tsUs u64 LE][access unit]

    public const byte CodecH264 = 1, CodecH265 = 2, CodecAv1 = 3;
    public const byte FlagKey = 0x01;

    public const int FileChunkSize = 16 * 1024;

    public static byte CodecByte(VideoCodec c) => c switch { VideoCodec.H265 => CodecH265, VideoCodec.Av1 => CodecAv1, _ => CodecH264 };

    public static string CodecName(VideoCodec c) => c switch { VideoCodec.H265 => "hvc1", VideoCodec.Av1 => "av01", _ => "avc1" };

    public static VideoCodec? CodecFromName(string? s) => s switch
    {
        "avc1" or "h264" => VideoCodec.H264,
        "hvc1" or "hev1" or "h265" or "hevc" => VideoCodec.H265,
        "av01" or "av1" => VideoCodec.Av1,
        _ => null,
    };

    /// <summary>v1 framing: one prefix byte, then the H.264 access unit.</summary>
    public static byte[] FrameV1(ReadOnlySpan<byte> au, bool key)
    {
        var msg = new byte[au.Length + 1];
        msg[0] = key ? BinH264Key : BinH264Delta;
        au.CopyTo(msg.AsSpan(1));
        return msg;
    }

    /// <summary>v2 framing: codec, flags, timestamp (µs), then the access unit.</summary>
    public static byte[] FrameV2(ReadOnlySpan<byte> au, VideoCodec codec, bool key, long tsUs)
    {
        var msg = new byte[au.Length + 11];
        msg[0] = BinVideo;
        msg[1] = CodecByte(codec);
        msg[2] = key ? FlagKey : (byte)0;
        BitConverter.TryWriteBytes(msg.AsSpan(3, 8), tsUs);
        au.CopyTo(msg.AsSpan(11));
        return msg;
    }

    public static bool TryParseV2(ReadOnlySpan<byte> msg, out byte codec, out bool key, out long tsUs, out ReadOnlySpan<byte> au)
    {
        codec = 0; key = false; tsUs = 0; au = default;
        if (msg.Length < 11 || msg[0] != BinVideo) return false;
        codec = msg[1];
        key = (msg[2] & FlagKey) != 0;
        tsUs = BitConverter.ToInt64(msg.Slice(3, 8));
        au = msg.Slice(11);
        return true;
    }

    public static byte[] FileChunk(uint id, uint seq, ReadOnlySpan<byte> payload)
    {
        var msg = new byte[payload.Length + 9];
        msg[0] = BinFileChunk;
        BitConverter.TryWriteBytes(msg.AsSpan(1, 4), id);
        BitConverter.TryWriteBytes(msg.AsSpan(5, 4), seq);
        payload.CopyTo(msg.AsSpan(9));
        return msg;
    }

    public static bool TryParseFileChunk(ReadOnlySpan<byte> msg, out uint id, out uint seq, out ReadOnlySpan<byte> payload)
    {
        id = seq = 0; payload = default;
        if (msg.Length < 9 || msg[0] != BinFileChunk) return false;
        id = BitConverter.ToUInt32(msg.Slice(1, 4));
        seq = BitConverter.ToUInt32(msg.Slice(5, 4));
        payload = msg.Slice(9);
        return payload.Length <= FileChunkSize;
    }
}

/// <summary>Tolerant readers for the viewer's JSON: a missing or odd value gives a safe default, never an exception.</summary>
public static class Json
{
    public static double Num(JsonElement m, string name, double fallback = 0) =>
        m.ValueKind == JsonValueKind.Object && m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && double.IsFinite(v.GetDouble())
            ? Math.Clamp(v.GetDouble(), -1e7, 1e13) : fallback;

    /// <summary>Whole number, tolerant: 2.0 or 1279.6 are fine, anything else gives the fallback.</summary>
    public static int Int(JsonElement m, string name, int fallback)
    {
        if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return fallback;
        if (v.TryGetInt32(out int i)) return i;
        double d = v.GetDouble();
        return double.IsFinite(d) ? (int)Math.Round(Math.Clamp(d, int.MinValue, int.MaxValue)) : fallback;
    }

    public static string Str(JsonElement m, string name, string fallback = "") =>
        m.ValueKind == JsonValueKind.Object && m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    public static bool Bool(JsonElement m, string name, bool fallback = false) =>
        m.ValueKind == JsonValueKind.Object && m.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.True || (v.ValueKind != JsonValueKind.False && fallback)
            : fallback;

    public static string[] Strings(JsonElement m, string name)
    {
        if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToArray();
    }

    public static JsonElement? Obj(JsonElement m, string name) =>
        m.ValueKind == JsonValueKind.Object && m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;
}
