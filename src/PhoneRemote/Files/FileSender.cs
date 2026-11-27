using System.Security.Cryptography;
using PhoneRemote.Core;
using PhoneRemote.Tools;

namespace PhoneRemote.Files;

/// <summary>
/// One file from this PC to the viewer: announce it (fopen), wait for the go (fok), then stream 16 KiB chunks,
/// never more than a window ahead of the viewer's acknowledgements (fack), and close with the SHA-256 (fdone).
/// </summary>
public sealed class FileSender : IDisposable
{
    private readonly uint _id;
    private readonly string _path;
    private readonly Func<string, Task> _sendText;
    private readonly Func<byte[], Task> _sendChunk;
    private readonly TaskCompletionSource<string?> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _credit = new(0);
    private uint _acked;
    private volatile bool _cancelled;

    public static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(90);   // the viewer may need to pick where to save
    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(45);

    public string Name { get; }
    public long Size { get; }
    public long Sent { get; private set; }
    public uint Id => _id;

    public FileSender(uint id, string path, Func<string, Task> sendText, Func<byte[], Task> sendChunk)
    {
        _id = id; _path = path; _sendText = sendText; _sendChunk = sendChunk;
        Name = Path.GetFileName(path);
        Size = new FileInfo(path).Length;
    }

    // ---- what the viewer answers ----
    public void Ok() => _opened.TrySetResult(null);
    public void Rejected(string why) => _opened.TrySetResult(why.Length > 0 ? why : "refusé");
    public void Ack(uint count) { if (count > _acked) { _acked = count; _credit.Release(); } }
    public void Done(bool ok) => _finished.TrySetResult(ok);
    public void Cancel() { _cancelled = true; _opened.TrySetResult("annulé"); _credit.Release(); _finished.TrySetResult(false); }

    /// <summary>Runs the whole transfer. Returns null on success, else why it failed.</summary>
    public async Task<string?> RunAsync(CancellationToken ct)
    {
        await _sendText(System.Text.Json.JsonSerializer.Serialize(new { t = "fopen", id = _id, name = Name, size = Size }));
        string? refused;
        try { refused = await _opened.Task.WaitAsync(OpenTimeout, ct); }
        catch (TimeoutException) { return "pas de réponse du destinataire"; }
        if (refused != null) return refused;
        uint total = FileChunker.ChunkCount(Size);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous);
            var buf = new byte[FileChunker.ChunkSize];
            for (uint seq = 0; seq < total; seq++)
            {
                while (!FileChunker.CanSend(seq, _acked))
                {
                    if (_cancelled) return "annulé";
                    if (!await _credit.WaitAsync(AckTimeout, ct)) return "le destinataire ne répond plus";
                }
                if (_cancelled) return "annulé";
                int n = await fs.ReadAtLeastAsync(buf, buf.Length, throwOnEndOfStream: false, ct);
                if (n == 0) return "fichier raccourci pendant l’envoi";
                hash.AppendData(buf, 0, n);
                await _sendChunk(Protocol.FileChunk(_id, seq, buf.AsSpan(0, n)));
                Sent += n;
            }
        }
        catch (IOException ex) { return "lecture impossible : " + ex.Message; }
        var sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        await _sendText(System.Text.Json.JsonSerializer.Serialize(new { t = "fdone", id = _id, sha }));
        try { return await _finished.Task.WaitAsync(AckTimeout, ct) ? null : "le destinataire n’a pas pu enregistrer le fichier"; }
        catch (TimeoutException) { return "pas de confirmation du destinataire"; }
    }

    public void Dispose() { _cancelled = true; _credit.Dispose(); }
}
