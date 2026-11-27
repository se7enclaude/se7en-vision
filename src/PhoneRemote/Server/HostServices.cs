using PhoneRemote.Capture;
using PhoneRemote.Cursor;
using PhoneRemote.Input;
using PhoneRemote.Media;
using PhoneRemote.Settings;

namespace PhoneRemote.Server;

/// <summary>Everything a viewer connection drives on this PC, shared by all connections (one at a time).</summary>
public sealed class HostServices
{
    public InputInjector Input { get; } = new();
    public ScreenCapturer Capturer { get; } = new();
    public CursorTracker Cursor { get; } = new();
    /// <summary>Laser and pen on this PC's screens; created by the main window (needs its message loop).</summary>
    public Overlay.AnnotationOverlay? Overlay { get; set; }
    public EncoderProbe Probe { get; }
    public AppSettings Settings { get; set; }

    /// <summary>Raised when something the host window shows changed (viewer stats, transport…).</summary>
    public event Action? Changed;
    /// <summary>A file came in, went out, or failed: (direction "in"|"out", event).</summary>
    public event Action<string, Files.FileEvent>? FileChanged;
    /// <summary>The last files received or sent, newest first (shown in the host window).</summary>
    public List<(string Dir, string Name, string? Path, long Size, DateTime At)> RecentFiles { get; } = new();

    public void NotifyFile(string dir, Files.FileEvent e)
    {
        if (e.Kind == "done")
        {
            lock (RecentFiles)
            {
                RecentFiles.Insert(0, (dir, e.Name, e.Path, e.Size, DateTime.Now));
                if (RecentFiles.Count > 6) RecentFiles.RemoveAt(RecentFiles.Count - 1);
            }
        }
        FileChanged?.Invoke(dir, e);
        NotifyChanged();
    }

    public HostServices(AppSettings settings, EncoderProbe probe)
    {
        Settings = settings;
        Probe = probe;
    }

    public void NotifyChanged() => Changed?.Invoke();

    /// <summary>Releases every key and button a viewer may still hold (crash, viewer gone).</summary>
    public void ReleaseInput()
    {
        lock (Input) Input.ReleaseAll();
    }
}
