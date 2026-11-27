namespace PhoneRemote.Core;

/// <summary>A file name coming from the other side becomes a plain, safe name inside the downloads folder.</summary>
public static class PathSanitizer
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };
    private static readonly HashSet<string> Risky = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".com", ".scr", ".pif", ".msi", ".msp", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".hta", ".cpl", ".jar", ".lnk", ".reg", ".inf", ".dll", ".sys", ".url", ".appx", ".msix",
    };

    /// <summary>Strips folders, control and forbidden characters, reserved device names; never empty, at most 150 chars.</summary>
    public static string SafeName(string? name)
    {
        var raw = (name ?? "").Replace('\\', '/');
        raw = raw[(raw.LastIndexOf('/') + 1)..];
        var bad = Path.GetInvalidFileNameChars();
        var chars = raw.Where(c => c >= 32 && Array.IndexOf(bad, c) < 0 && c != '\u007f').ToArray();
        var s = new string(chars).Trim().TrimEnd('.', ' ');
        if (s.Length == 0 || s == "." || s == "..") s = "fichier";
        if (s.Length > 150)
        {
            var ext = Path.GetExtension(s);
            if (ext.Length > 20) ext = "";
            s = s[..(150 - ext.Length)].TrimEnd('.', ' ') + ext;
        }
        var stem = Path.GetFileNameWithoutExtension(s);
        if (Reserved.Contains(stem)) s = "_" + s;
        return s;
    }

    /// <summary>"name.ext", or "name (2).ext", "name (3).ext"… when the folder already has one.</summary>
    public static string UniquePath(string dir, string safeName)
    {
        var candidate = Path.Combine(dir, safeName);
        if (!File.Exists(candidate) && !File.Exists(candidate + ".part")) return candidate;
        var stem = Path.GetFileNameWithoutExtension(safeName);
        var ext = Path.GetExtension(safeName);
        for (int i = 2; i < 10000; i++)
        {
            candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !File.Exists(candidate + ".part")) return candidate;
        }
        return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    /// <summary>Something Windows would run when double-clicked.</summary>
    public static bool IsRisky(string name) => Risky.Contains(Path.GetExtension(name));
}
