using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhoneRemote.Core;
using PhoneRemote.Media.Capture;
using PhoneRemote.Tools;

namespace PhoneRemote.Media;

public sealed class ProbeResult
{
    public string Id { get; set; } = "";
    public string Codec { get; set; } = "avc1";
    public bool Ok { get; set; }
    public double Fps { get; set; }
    public int Ms { get; set; }
    public string? Error { get; set; }
}

public sealed class ProbeCache
{
    public int V { get; set; } = 1;
    public string Key { get; set; } = "";
    public string[] Adapters { get; set; } = Array.Empty<string>();
    public DateTime ProbedAt { get; set; }
    public List<ProbeResult> Results { get; set; } = new();
}

/// <summary>
/// Finds out, once, which encoders really work on this PC: each candidate runs the real capture pipeline for
/// ~1.5 s and must produce frames. The answer is cached (keyed on the ffmpeg build, the graphics adapters and
/// Windows) so later launches start at once.
/// </summary>
public sealed class EncoderProbe
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ProbeCache? _cache;
    private bool _started;

    public static string CachePath => Path.Combine(AppInfo.DataDir, "encoders.json");

    public bool Done => _done.Task.IsCompleted;
    public IReadOnlyList<ProbeResult> Results { get { lock (_gate) return _cache?.Results.ToArray() ?? Array.Empty<ProbeResult>(); } }
    public event Action? Completed;

    public Task WaitAsync(TimeSpan timeout) => Task.WhenAny(_done.Task, Task.Delay(timeout));

    /// <summary>Starts the probe in the background (cached answer reused when still valid).</summary>
    public void Start(bool probeAll = false, bool force = false)
    {
        lock (_gate)
        {
            if (_started && !force) return;
            _started = true;
        }
        Task.Run(() =>
        {
            try { Run(probeAll, force); }
            catch (Exception ex) { Log.Error("sonde des encodeurs", ex); }
            finally
            {
                _done.TrySetResult();
                Completed?.Invoke();
            }
        });
    }

    public void RunBlocking(bool probeAll = false, bool force = true) { Run(probeAll, force); _done.TrySetResult(); Completed?.Invoke(); }

    private void Run(bool probeAll, bool force)
    {
        var adapters = Displays.AdapterNames();
        string key = CacheKey(adapters);
        if (!force)
        {
            var cached = LoadCache();
            if (cached != null && cached.Key == key && cached.Results.Count > 0)
            {
                lock (_gate) _cache = cached;
                Log.Write($"Encodeurs (cache) : {string.Join(", ", cached.Results.Where(r => r.Ok).Select(r => r.Id))}");
                return;
            }
        }
        var screen = Displays.Get(0);
        var results = new List<ProbeResult>();
        foreach (var enc in EncoderCatalog.All)
        {
            if (!probeAll && !VendorPresent(enc.Vendor, adapters))
            {
                results.Add(new ProbeResult { Id = enc.Id, Codec = Protocol.CodecName(enc.Codec), Ok = false, Error = "carte graphique absente" });
                continue;
            }
            results.Add(Test(enc, screen));
            lock (_gate) _cache = new ProbeCache { Key = key, Adapters = adapters, ProbedAt = DateTime.Now, Results = results.ToList() };
        }
        var cache = new ProbeCache { Key = key, Adapters = adapters, ProbedAt = DateTime.Now, Results = results };
        lock (_gate) _cache = cache;
        SaveCache(cache);
        Log.Write($"Encodeurs : {string.Join(", ", results.Select(r => r.Id + (r.Ok ? $" ok {r.Fps:0} i/s" : " ✗")))}");
    }

    private static bool VendorPresent(string vendor, string[] adapters)
    {
        string all = string.Join(" ", adapters).ToLowerInvariant();
        return vendor switch
        {
            "nvidia" => all.Contains("nvidia") || all.Contains("geforce") || all.Contains("quadro") || all.Contains("rtx"),
            "amd" => all.Contains("amd") || all.Contains("radeon"),
            "intel" => all.Contains("intel") || all.Contains("arc"),
            _ => true,
        };
    }

    /// <summary>Runs the real pipeline for a moment and counts frames. Success needs at least 10 of them.</summary>
    public static ProbeResult Test(EncoderSpec enc, ScreenInfo screen, int runMs = 1500)
    {
        var r = new ProbeResult { Id = enc.Id, Codec = Protocol.CodecName(enc.Codec) };
        var sw = Stopwatch.StartNew();
        FfmpegProcess? p = null;
        try
        {
            var plan = EncoderCatalog.ProbePlan(enc, screen);
            string? stopped = null;
            long firstAt = 0, lastAt = 0;
            int frames = 0;
            p = new FfmpegProcess(EncoderCatalog.Args(plan), enc.Codec);
            p.FrameReady += (_, _, ts) => { if (frames++ == 0) firstAt = ts; lastAt = ts; };
            p.Stopped += reason => stopped = reason;
            var deadline = sw.ElapsedMilliseconds + runMs + 2500;
            while (sw.ElapsedMilliseconds < deadline && stopped == null && !(frames > 0 && sw.ElapsedMilliseconds > runMs))
                Thread.Sleep(50);
            r.Ms = (int)sw.ElapsedMilliseconds;
            if (frames >= 10 && stopped == null)
            {
                r.Ok = true;
                r.Fps = lastAt > firstAt ? Math.Round((frames - 1) * 1_000_000.0 / (lastAt - firstAt)) : frames;
            }
            else
            {
                r.Error = stopped ?? (frames == 0 ? (p.LastError.Length > 0 ? p.LastError : "aucune image produite") : $"seulement {frames} images");
            }
        }
        catch (Exception ex) { r.Error = ex.Message; r.Ms = (int)sw.ElapsedMilliseconds; }
        finally { p?.Dispose(); }
        return r;
    }

    /// <summary>The encoder to use for a codec, honouring the user's choice when it works; null = none yet.</summary>
    public EncoderSpec? Best(VideoCodec codec, string setting = "auto")
    {
        var results = Results;
        if (setting != "auto")
        {
            var chosen = EncoderCatalog.Find(setting);
            if (chosen != null && (results.Any(r => r.Id == setting && r.Ok) || chosen.Vendor == "cpu")) return chosen;
        }
        foreach (var enc in EncoderCatalog.All.Where(e => e.Codec == codec).OrderBy(e => e.Tier))
            if (results.Any(r => r.Id == enc.Id && r.Ok)) return enc;
        if (codec != VideoCodec.H264) return Best(VideoCodec.H264, "auto");
        // nothing probed yet (or everything failed): the CPU encoders always exist
        return results.Any(r => r.Id == "libx264" && !r.Ok) ? EncoderCatalog.Find("gdigrab_x264") : EncoderCatalog.Find("libx264");
    }

    /// <summary>The next thing to try after <paramref name="failed"/> broke at runtime (same codec first, then H.264).</summary>
    public EncoderSpec? Next(EncoderSpec failed)
    {
        var ok = Results.Where(r => r.Ok).Select(r => r.Id).ToHashSet();
        ok.Add("libx264"); ok.Add("gdigrab_x264");
        return EncoderCatalog.All.Where(e => e.Tier > failed.Tier && ok.Contains(e.Id))
            .OrderBy(e => e.Codec == failed.Codec ? 0 : 1).ThenBy(e => e.Tier).FirstOrDefault();
    }

    public string ToJson()
    {
        lock (_gate) return JsonSerializer.Serialize(_cache ?? new ProbeCache(), ProbeJson.Default.ProbeCache);
    }

    private static string CacheKey(string[] adapters)
    {
        string ff = "";
        try
        {
            var path = FfmpegProcess.FfmpegPath;
            if (path != null) { var fi = new FileInfo(path); ff = $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}"; }
        }
        catch { }
        return $"{ff}|{string.Join(",", adapters)}|{Environment.OSVersion.Version}|{Application.ProductVersion}";
    }

    private static ProbeCache? LoadCache()
    {
        try { return File.Exists(CachePath) ? JsonSerializer.Deserialize(File.ReadAllText(CachePath), ProbeJson.Default.ProbeCache) : null; }
        catch { return null; }
    }

    private static void SaveCache(ProbeCache c)
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            File.WriteAllText(CachePath + ".tmp", JsonSerializer.Serialize(c, ProbeJson.Default.ProbeCache));
            File.Move(CachePath + ".tmp", CachePath, overwrite: true);
        }
        catch (Exception ex) { Log.Error("cache des encodeurs", ex); }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProbeCache))]
internal partial class ProbeJson : JsonSerializerContext;
