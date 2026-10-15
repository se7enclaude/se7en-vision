namespace PhoneRemote;

internal static class AppInfo
{
    public const string Name = "se7en Vision";

    /// <summary>%LOCALAPPDATA%\se7en Vision — WebView2 profile and the extracted cloudflared.exe.</summary>
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);
}
