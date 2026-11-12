namespace PhoneRemote.Tools;

/// <summary>
/// Simple rotating log in the data folder (se7en Vision.log, previous one kept as .old.log once it passes
/// 1 MB): tunnel restarts, connections, video errors and crashes, to understand a problem after the fact.
/// </summary>
public static class Log
{
    private const long MaxBytes = 1_000_000;
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(AppInfo.DataDir, AppInfo.Name + ".log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppInfo.DataDir);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(FilePath, Path.ChangeExtension(FilePath, ".old.log"), overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch { } // a log must never break the app
    }

    public static void Error(string context, Exception? ex) => Write($"ERREUR {context} : {ex}");
}
