using System.Runtime.InteropServices;
using PhoneRemote.Core;

namespace PhoneRemote.Media.Capture;

/// <summary>
/// The screens, as Windows lists them, with what the capture needs: position, size, refresh rate and the index
/// ddagrab (DXGI) uses for them. Index 0 is always the primary screen (what 1.4 viewers expect).
/// </summary>
public static class Displays
{
    public static ScreenInfo[] All()
    {
        var devices = EnumerateDevices();
        var outputs = Dxgi.Outputs();
        var screens = Screen.AllScreens.OrderByDescending(s => s.Primary).ThenBy(s => s.DeviceName).ToArray();
        var list = new List<ScreenInfo>();
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            var dev = devices.FirstOrDefault(d => string.Equals(d.Name, s.DeviceName, StringComparison.OrdinalIgnoreCase));
            int hz = dev?.Hz ?? 0;
            // ddagrab's output_idx = the output's position on the default adapter, as DXGI lists them (matched by
            // device name, else by position); without DXGI, Windows' own order is the best guess
            var o = outputs.FirstOrDefault(x => string.Equals(x.DeviceName, s.DeviceName, StringComparison.OrdinalIgnoreCase))
                    ?? outputs.FirstOrDefault(x => x.Left == s.Bounds.Left && x.Top == s.Bounds.Top);
            int outputIdx = o?.Index ?? dev?.OutputIdx ?? i;
            var b = s.Bounds;
            list.Add(new ScreenInfo(i, b.X, b.Y, b.Width, b.Height, hz, s.Primary, $"Écran {i + 1}", outputIdx));
        }
        if (list.Count == 0) list.Add(new ScreenInfo(0, 0, 0, 1920, 1080, 60, true, "Écran 1", 0));
        return list.ToArray();
    }

    public static ScreenInfo Get(int index)
    {
        var all = All();
        return all[Math.Clamp(index, 0, all.Length - 1)];
    }

    public static System.Drawing.Rectangle Bounds(int index)
    {
        var s = Get(index);
        return new System.Drawing.Rectangle(s.X, s.Y, s.W, s.H);
    }

    /// <summary>Graphics adapters driving the attached screens ("NVIDIA GeForce RTX 5070 Ti"…), deduplicated.</summary>
    public static string[] AdapterNames() => EnumerateDevices().Select(d => d.Adapter).Where(a => a.Length > 0).Distinct().ToArray();

    private sealed record Device(string Name, string Adapter, int Hz, int OutputIdx);

    private static List<Device> EnumerateDevices()
    {
        var list = new List<Device>();
        try
        {
            int output = 0;
            for (uint i = 0; ; i++)
            {
                var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
                if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0 || (dd.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) != 0) continue;
                var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                int hz = EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref dm) ? dm.dmDisplayFrequency : 0;
                list.Add(new Device(dd.DeviceName, dd.DeviceString ?? "", hz, output++));
            }
        }
        catch { }
        return list;
    }

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1, DISPLAY_DEVICE_MIRRORING_DRIVER = 0x8;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);
}
