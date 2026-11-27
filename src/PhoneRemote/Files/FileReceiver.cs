using System.Security.Cryptography;
using PhoneRemote.Core;
using PhoneRemote.Tools;

namespace PhoneRemote.Files;

public sealed record FileEvent(string Kind, uint Id, string Name, long Size, long Done, string? Path, string? Why, bool Risky);

/// <summary>
/// Files the viewer sends to this PC. Each one is written as "name.part" in the downloads folder, hashed on
/// the way, then renamed once its SHA-256 matches. Names are sanitised, sizes capped, nothing is ever
/// overwritten, and a transfer that goes quiet for a while is dropped with its partial file.
/// </summary>
public sealed class FileReceiver : IDisposable
{
    private sealed class Incoming
    {
        public required string Name;
        public required string TempPath;
        public required string FinalPath;
        public required long Size;
        public required FileStream Stream;
        public required IncrementalHash Hash;
        public uint NextSeq;
        public long Received;
        public DateTime LastAt = DateTime.UtcNow;
    }

    private readonly Dictionary<uint, Incoming> _open = new();
    private readonly object _gate = new();
    private readonly string _dir;
    private readonly long _maxBytes;
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(45);

    /// <summary>"open", "progress", "done", "failed".</summary>
    public event Action<FileEvent>? Event;

    public FileReceiver(string downloadsDir, long maxBytes)
    {
        _dir = downloadsDir;
        _maxBytes = maxBytes;
    }

    public int Active { get { lock (_gate) return _open.Count; } }

    /// <summary>Accepts or refuses a new file. Returns null when accepted, else the reason to send back.</summary>
    public string? Open(uint id, string? rawName, long size)
    {
        var name = PathSanitizer.SafeName(rawName);
        if (size < 0) return "taille invalide";
        if (size > _maxBytes) return $"fichier trop gros (max {FileChunker.Human(_maxBytes)})";
        lock (_gate)
        {
            if (_open.ContainsKey(id)) return "identifiant déjà utilisé";
            if (_open.Count >= 4) return "trop de fichiers en même temps";
        }
        try
        {
            Directory.CreateDirectory(_dir);
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_dir)) ?? "C:\\").AvailableFreeSpace;
            if (free < size + 64L * 1024 * 1024) return "pas assez de place sur le disque";
            var final = PathSanitizer.UniquePath(_dir, name);
            var temp = final + ".part";
            var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan);
            var inc = new Incoming { Name = Path.GetFileName(final), TempPath = temp, FinalPath = final, Size = size, Stream = stream, Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256) };
            lock (_gate) _open[id] = inc;
            Event?.Invoke(new FileEvent("open", id, inc.Name, size, 0, null, null, PathSanitizer.IsRisky(inc.Name)));
            if (size == 0) Finish(id, inc, null);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("réception de fichier", ex);
            return "impossible d’écrire dans le dossier de réception";
        }
    }

    /// <summary>One chunk. Returns the count to acknowledge when an ack is due, 0 when none is due, -1 on failure.</summary>
    public long Chunk(uint id, uint seq, ReadOnlySpan<byte> payload)
    {
        Incoming? inc;
        lock (_gate) _open.TryGetValue(id, out inc);
        if (inc == null) return -1;
        if (seq != inc.NextSeq) { Fail(id, inc, "morceau manquant"); return -1; }
        if (inc.Received + payload.Length > inc.Size) { Fail(id, inc, "plus de données qu’annoncé"); return -1; }
        try
        {
            inc.Stream.Write(payload);
            inc.Hash.AppendData(payload);
        }
        catch (Exception ex) { Fail(id, inc, "écriture impossible : " + ex.Message); return -1; }
        inc.NextSeq++;
        inc.Received += payload.Length;
        inc.LastAt = DateTime.UtcNow;
        if (inc.NextSeq % 64 == 0) Event?.Invoke(new FileEvent("progress", id, inc.Name, inc.Size, inc.Received, null, null, false));
        return FileChunker.ShouldAck(seq, FileChunker.ChunkCount(inc.Size)) ? inc.NextSeq : 0;
    }

    /// <summary>The sender says it is done; the hash must match. Returns the final path or null.</summary>
    public string? Done(uint id, string? shaHex)
    {
        Incoming? inc;
        lock (_gate) _open.TryGetValue(id, out inc);
        if (inc == null) return null;
        return Finish(id, inc, shaHex);
    }

    private string? Finish(uint id, Incoming inc, string? shaHex)
    {
        try
        {
            if (inc.Received != inc.Size) { Fail(id, inc, "fichier incomplet"); return null; }
            inc.Stream.Flush(true);
            inc.Stream.Dispose();
            var hash = Convert.ToHexString(inc.Hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.IsNullOrEmpty(shaHex) && !string.Equals(hash, shaHex, StringComparison.OrdinalIgnoreCase)) { Fail(id, inc, "contenu abîmé en route (empreinte différente)"); return null; }
            var final = File.Exists(inc.FinalPath) ? PathSanitizer.UniquePath(_dir, inc.Name) : inc.FinalPath;
            File.Move(inc.TempPath, final);
            lock (_gate) _open.Remove(id);
            Event?.Invoke(new FileEvent("done", id, Path.GetFileName(final), inc.Size, inc.Size, final, null, PathSanitizer.IsRisky(final)));
            return final;
        }
        catch (Exception ex) { Fail(id, inc, ex.Message); return null; }
    }

    public void Cancel(uint id, string why = "annulé")
    {
        Incoming? inc;
        lock (_gate) _open.TryGetValue(id, out inc);
        if (inc != null) Fail(id, inc, why);
    }

    /// <summary>Drops transfers that went quiet. Returns their ids (to tell the other side).</summary>
    public uint[] Sweep()
    {
        List<(uint, Incoming)> stale;
        lock (_gate) stale = _open.Where(kv => DateTime.UtcNow - kv.Value.LastAt > IdleTimeout).Select(kv => (kv.Key, kv.Value)).ToList();
        foreach (var (id, inc) in stale) Fail(id, inc, "plus de nouvelles de l’expéditeur");
        return stale.Select(s => s.Item1).ToArray();
    }

    private void Fail(uint id, Incoming inc, string why)
    {
        lock (_gate) _open.Remove(id);
        try { inc.Stream.Dispose(); } catch { }
        try { inc.Hash.Dispose(); } catch { }
        try { File.Delete(inc.TempPath); } catch { }
        Event?.Invoke(new FileEvent("failed", id, inc.Name, inc.Size, inc.Received, null, why, false));
    }

    public void Dispose()
    {
        List<(uint, Incoming)> all;
        lock (_gate) all = _open.Select(kv => (kv.Key, kv.Value)).ToList();
        foreach (var (id, inc) in all) Fail(id, inc, "connexion terminée");
    }
}
