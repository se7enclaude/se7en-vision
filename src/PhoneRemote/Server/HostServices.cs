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
    public EncoderProbe Probe { get; }
    public AppSettings Settings { get; set; }

    /// <summary>Raised when something the host window shows changed (viewer stats, transport…).</summary>
    public event Action? Changed;

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
