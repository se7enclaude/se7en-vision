using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhoneRemote.Settings;

/// <summary>What the user chose in « Réglages ». Saved as JSON in the data folder; written atomically.</summary>
public sealed class AppSettings
{
    /// <summary>Default quality for a new viewer: "fast" | "balanced" | "sharp".</summary>
    public string Quality { get; set; } = "balanced";
    /// <summary>"auto" or an encoder id from the probe ("h264_nvenc", "libx264"…).</summary>
    public string Encoder { get; set; } = "auto";
    /// <summary>Highest frame rate ever sent (the screen's refresh rate still caps it).</summary>
    public int FpsCap { get; set; } = 240;
    /// <summary>"avc1" | "hvc1" | "av01" — used where the viewer can decode it (WebRTC stays H.264 unless HEVC is allowed).</summary>
    public string Codec { get; set; } = "avc1";
    public bool AllowHevcRtc { get; set; }
    /// <summary>Where received files go; empty = Downloads\se7en Vision.</summary>
    public string DownloadsDir { get; set; } = "";
    /// <summary>ddagrab dup_frames=0: no frame while nothing changes on screen.</summary>
    public bool PowerSaver { get; set; }
    public bool Rtc { get; set; } = true;
    public bool Clipboard { get; set; }
    public bool OverlayVisible { get; set; } = true;
    public bool ProbeAll { get; set; }
    public long MaxFileBytes { get; set; } = 4L * 1024 * 1024 * 1024;

    public static string FilePath => Path.Combine(AppInfo.DataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJson.Default.AppSettings)?.Sanitised() ?? new AppSettings();
        }
        catch (Exception ex) { Tools.Log.Error("réglages", ex); }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppInfo.DataDir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, SettingsJson.Default.AppSettings));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public AppSettings Sanitised()
    {
        if (Quality is not ("fast" or "balanced" or "sharp")) Quality = "balanced";
        if (string.IsNullOrWhiteSpace(Encoder) || Encoder.Length > 32 || Encoder.Any(c => !(char.IsLetterOrDigit(c) || c == '_'))) Encoder = "auto";
        FpsCap = Math.Clamp(FpsCap, 24, 240);
        if (Codec is not ("avc1" or "hvc1" or "av01")) Codec = "avc1";
        MaxFileBytes = Math.Clamp(MaxFileBytes, 1024 * 1024, 64L * 1024 * 1024 * 1024);
        return this;
    }

    /// <summary>Applies one key/value from the UI. Returns an error message when the value is refused.</summary>
    public string? Apply(string key, JsonElement value)
    {
        switch (key)
        {
            case "quality": Quality = value.GetString() ?? Quality; break;
            case "encoder": Encoder = value.GetString() ?? Encoder; break;
            case "fpsCap": if (value.TryGetInt32(out int f)) FpsCap = f; break;
            case "codec": Codec = value.GetString() ?? Codec; break;
            case "allowHevcRtc": AllowHevcRtc = value.ValueKind == JsonValueKind.True; break;
            case "downloadsDir": DownloadsDir = value.GetString() ?? ""; break;
            case "powerSaver": PowerSaver = value.ValueKind == JsonValueKind.True; break;
            case "rtc": Rtc = value.ValueKind == JsonValueKind.True; break;
            case "clipboard": Clipboard = value.ValueKind == JsonValueKind.True; break;
            case "overlayVisible": OverlayVisible = value.ValueKind == JsonValueKind.True; break;
            case "probeAll": ProbeAll = value.ValueKind == JsonValueKind.True; break;
            default: return "Réglage inconnu.";
        }
        Sanitised();
        return null;
    }

    public string ResolvedDownloadsDir =>
        string.IsNullOrWhiteSpace(DownloadsDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", AppInfo.Name)
            : DownloadsDir;
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJson : JsonSerializerContext;
