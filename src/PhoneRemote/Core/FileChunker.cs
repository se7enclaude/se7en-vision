namespace PhoneRemote.Core;

/// <summary>
/// How a file travels: 16 KiB chunks numbered from 0, with a credit window — the sender never has more than
/// <see cref="Window"/> chunks beyond the last one the receiver acknowledged, so a slow link never piles up.
/// The receiver acknowledges every <see cref="AckEvery"/> chunks and at the end.
/// </summary>
public static class FileChunker
{
    public const int ChunkSize = Protocol.FileChunkSize;
    public const int Window = 32;      // chunks in flight (512 KiB)
    public const int AckEvery = 8;

    public static uint ChunkCount(long size) => size <= 0 ? 0u : (uint)((size + ChunkSize - 1) / ChunkSize);

    /// <summary>May chunk <paramref name="nextSeq"/> go out while <paramref name="ackedCount"/> chunks are acknowledged?</summary>
    public static bool CanSend(uint nextSeq, uint ackedCount) => nextSeq < ackedCount + Window;

    /// <summary>Should the receiver acknowledge after taking chunk <paramref name="seq"/> of <paramref name="total"/>?</summary>
    public static bool ShouldAck(uint seq, uint total) => (seq + 1) % AckEvery == 0 || seq + 1 == total;

    /// <summary>A human size: "12 Ko", "3,4 Mo", "1,2 Go".</summary>
    public static string Human(long bytes)
    {
        if (bytes < 1024) return bytes + " o";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0") + " Ko";
        if (bytes < 1024L * 1024 * 1024) return (bytes / 1048576.0).ToString("0.#") + " Mo";
        return (bytes / 1073741824.0).ToString("0.##") + " Go";
    }
}
