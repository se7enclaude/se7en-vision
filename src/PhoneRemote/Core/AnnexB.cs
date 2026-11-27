namespace PhoneRemote.Core;

public enum VideoCodec { H264, H265, Av1 }

/// <summary>
/// Cuts an encoder's raw output into access units (one per picture) and tells keyframes apart.
/// H.264 / HEVC: Annex B byte stream, split on the access unit delimiter NAL that every encoder we use writes
/// (<c>-aud 1</c> / <c>aud=1</c>). If an encoder writes none, the splitter falls back to "a new picture starts
/// at a parameter set, or at a slice whose first_mb_in_slice is 0". AV1: low-overhead OBU stream (<c>-f obu</c>),
/// split on temporal delimiters. Pure logic, no I/O: tested on its own.
/// </summary>
public sealed class AccessUnitSplitter
{
    private readonly VideoCodec _codec;
    private byte[] _buf = new byte[256 * 1024];
    private int _len;          // bytes in _buf
    private int _auStart = -1; // where the unfinished access unit begins, -1 = none yet
    private int _scan;         // first byte not yet looked at
    private bool _sawAud;      // the stream has delimiters: only they split (no heuristics)
    private bool _auHasSlice;  // the unfinished AU already holds a picture slice (heuristic mode)

    public AccessUnitSplitter(VideoCodec codec) { _codec = codec; }

    /// <summary>Feeds more bytes; <paramref name="emit"/> receives each complete access unit and whether it is a keyframe.</summary>
    public void Push(ReadOnlySpan<byte> data, Action<byte[], bool> emit)
    {
        if (_len + data.Length > _buf.Length)
        {
            int size = _buf.Length;
            while (size < _len + data.Length) size *= 2;
            Array.Resize(ref _buf, size);
        }
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;

        if (_codec == VideoCodec.Av1) ScanObu(emit);
        else ScanNal(emit);

        // keep only the unfinished access unit (and, before the first one, the bytes not scanned yet)
        int drop = _auStart >= 0 ? _auStart : Math.Max(0, Math.Min(_scan, _len - 4) - 1); // −1: the zero before a 4-byte start code
        if (drop > 0)
        {
            Buffer.BlockCopy(_buf, drop, _buf, 0, _len - drop);
            _len -= drop;
            if (_auStart >= 0) _auStart = 0;
            _scan = Math.Max(0, _scan - drop);
        }
    }

    /// <summary>Flushes what is left (end of stream).</summary>
    public void Finish(Action<byte[], bool> emit)
    {
        if (_auStart >= 0 && _len > _auStart) Emit(_auStart, _len - _auStart, emit);
        _len = 0;
        _auStart = -1;
        _scan = 0;
    }

    // ---- H.264 / HEVC ------------------------------------------------------

    private void ScanNal(Action<byte[], bool> emit)
    {
        // Every byte is looked at once: the loop stops in front of a NAL it cannot classify yet (its header or
        // first slice byte not arrived) and resumes there on the next push.
        int i = _scan;
        while (i + 3 < _len)
        {
            if (_buf[i] != 0 || _buf[i + 1] != 0 || _buf[i + 2] != 1) { i++; continue; }
            int nal = i + 3;
            int need = _codec == VideoCodec.H264 ? nal + 1 : nal + 2; // bytes needed to read the slice flag
            if (need >= _len) break;
            int start = i > 0 && _buf[i - 1] == 0 ? i - 1 : i;
            bool isAud, isParam, isSlice, firstInPic;
            if (_codec == VideoCodec.H264)
            {
                int type = _buf[nal] & 0x1F;
                isAud = type == 9;
                isParam = type is 7 or 8 or 6;
                isSlice = type is 1 or 5;
                firstInPic = isSlice && (_buf[nal + 1] & 0x80) != 0; // ue(v) first_mb_in_slice == 0
            }
            else
            {
                int type = (_buf[nal] >> 1) & 0x3F;
                isAud = type == 35;
                isParam = type is 32 or 33 or 34 or 39;
                isSlice = type <= 21;
                firstInPic = isSlice && (_buf[nal + 2] & 0x80) != 0; // first_slice_segment_in_pic_flag
            }
            if (isAud) _sawAud = true;
            bool boundary = isAud || (!_sawAud && _auHasSlice && (isParam || firstInPic));
            if (boundary)
            {
                if (_auStart >= 0 && start > _auStart) Emit(_auStart, start - _auStart, emit);
                _auStart = start;
                _auHasSlice = false;
            }
            else if (_auStart < 0) _auStart = start;
            if (isSlice) _auHasSlice = true;
            i = nal + 1;
        }
        _scan = i;
    }

    // ---- AV1 (OBU stream) ---------------------------------------------------

    private void ScanObu(Action<byte[], bool> emit)
    {
        int cursor = _scan;
        while (cursor < _len)
        {
            if (!TryObu(_buf.AsSpan(0, _len), cursor, out int type, out int total)) break; // incomplete: wait for more
            if (type == 2) // temporal delimiter
            {
                if (_auStart >= 0 && cursor > _auStart) Emit(_auStart, cursor - _auStart, emit);
                _auStart = cursor;
            }
            else if (_auStart < 0) _auStart = cursor;
            cursor += total;
        }
        _scan = cursor;
    }

    private static bool TryObu(ReadOnlySpan<byte> buf, int at, out int type, out int total)
    {
        type = 0; total = 0;
        if (at >= buf.Length) return false;
        byte h = buf[at];
        type = (h >> 3) & 0x0F;
        bool ext = (h & 0x04) != 0, hasSize = (h & 0x02) != 0;
        int p = at + 1 + (ext ? 1 : 0);
        if (!hasSize) { total = buf.Length - at; return true; }
        long size = 0; int shift = 0;
        for (int k = 0; k < 8; k++)
        {
            if (p >= buf.Length) return false;
            byte b = buf[p++];
            size |= (long)(b & 0x7F) << shift;
            shift += 7;
            if ((b & 0x80) == 0) break;
        }
        if (p + size > buf.Length) return false;
        total = (int)(p - at + size);
        return true;
    }

    private void Emit(int start, int length, Action<byte[], bool> emit)
    {
        var au = new byte[length];
        Buffer.BlockCopy(_buf, start, au, 0, length);
        emit(au, IsKeyframe(_codec, au));
    }

    /// <summary>True when the access unit carries an IDR / IRAP picture (or a parameter set, which only precedes one).</summary>
    public static bool IsKeyframe(VideoCodec codec, ReadOnlySpan<byte> au)
    {
        if (codec == VideoCodec.Av1) return Av1IsKey(au);
        for (int i = 0; i + 3 < au.Length; i++)
        {
            if (au[i] != 0 || au[i + 1] != 0 || au[i + 2] != 1) continue;
            if (codec == VideoCodec.H264)
            {
                int type = au[i + 3] & 0x1F;
                if (type is 5 or 7) return true;
                if (type == 1) return false;
            }
            else
            {
                int type = (au[i + 3] >> 1) & 0x3F;
                if (type is >= 16 and <= 21 || type is 32 or 33) return true;
                if (type <= 9) return false;
            }
            i += 3;
        }
        return false;
    }

    private static bool Av1IsKey(ReadOnlySpan<byte> tu)
    {
        int at = 0;
        while (at < tu.Length)
        {
            if (!TryObu(tu, at, out int type, out int total)) return false;
            byte h = tu[at];
            int p = at + 1 + ((h & 0x04) != 0 ? 1 : 0);
            if ((h & 0x02) != 0) { while (p < tu.Length && (tu[p++] & 0x80) != 0) { } } // skip leb128 size
            if (type == 1) return true; // sequence header: only precedes a keyframe in low-overhead streams
            if (type is 3 or 6 && p < tu.Length)
            {
                byte f = tu[p];
                bool showExisting = (f & 0x80) != 0;
                int frameType = (f >> 5) & 0x03;
                return !showExisting && frameType == 0;
            }
            if (total <= 0) break;
            at += total;
        }
        return false;
    }

    /// <summary>The H.264 SPS profile bytes as hex ("42C033"), for WebCodecs ("avc1." + id) and SDP.</summary>
    public static string? H264ProfileLevelId(ReadOnlySpan<byte> au)
    {
        for (int i = 0; i + 6 < au.Length; i++)
        {
            if (au[i] == 0 && au[i + 1] == 0 && au[i + 2] == 1 && (au[i + 3] & 0x1F) == 7)
                return Convert.ToHexString(au.Slice(i + 4, 3));
        }
        return null;
    }
}
