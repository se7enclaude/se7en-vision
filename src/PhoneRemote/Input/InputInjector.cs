using System.Drawing;
using System.Runtime.InteropServices;

namespace PhoneRemote.Input;

/// <summary>Injects mouse and keyboard input into Windows with SendInput.</summary>
public sealed class InputInjector
{
    private bool _leftDown, _rightDown;
    private readonly HashSet<ushort> _heldKeys = new();

    // ---- Mouse ---------------------------------------------------------------

    public void MoveBy(double dx, double dy)
    {
        GetCursorPos(out var p);
        MoveTo(p.X + (int)Math.Round(dx), p.Y + (int)Math.Round(dy));
    }

    /// <summary>Moves to a point given as 0..1 fractions of a screen's bounds.</summary>
    public void MoveTo(Rectangle screen, double fx, double fy)
    {
        fx = Math.Clamp(fx, 0, 1);
        fy = Math.Clamp(fy, 0, 1);
        MoveTo(screen.Left + (int)Math.Round(fx * (screen.Width - 1)),
               screen.Top + (int)Math.Round(fy * (screen.Height - 1)));
    }

    private static void MoveTo(int x, int y)
    {
        var vs = SystemInformation.VirtualScreen;
        x = Math.Clamp(x, vs.Left, vs.Right - 1);
        y = Math.Clamp(y, vs.Top, vs.Bottom - 1);
        // Absolute coordinates on the virtual desktop are normalised to 0..65535.
        int nx = (int)Math.Round((x - vs.Left) * 65535.0 / Math.Max(1, vs.Width - 1));
        int ny = (int)Math.Round((y - vs.Top) * 65535.0 / Math.Max(1, vs.Height - 1));
        SendMouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny, 0);
    }

    public void Button(char button, bool down)
    {
        bool left = button != 'r';
        if (left) { if (_leftDown == down) return; _leftDown = down; }
        else { if (_rightDown == down) return; _rightDown = down; }
        uint flag = left
            ? (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP)
            : (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP);
        SendMouse(flag, 0, 0, 0);
    }

    public void Click(char button, int count = 1)
    {
        for (int i = 0; i < Math.Clamp(count, 1, 3); i++)
        {
            Button(button, true);
            Button(button, false);
        }
    }

    public void Wheel(int dy, int dx = 0)
    {
        if (dy != 0) SendMouse(MOUSEEVENTF_WHEEL, 0, 0, Math.Clamp(dy, -2400, 2400));
        if (dx != 0) SendMouse(MOUSEEVENTF_HWHEEL, 0, 0, Math.Clamp(dx, -2400, 2400));
    }

    // ---- Keyboard ------------------------------------------------------------

    public void Text(string text)
    {
        foreach (char c in text)
        {
            if (c == '\n' || c == '\r') { Key("Enter", Array.Empty<string>()); continue; }
            if (c == '\t') { Key("Tab", Array.Empty<string>()); continue; }
            var inputs = new INPUT[2];
            inputs[0] = KeyInput(0, c, KEYEVENTF_UNICODE);
            inputs[1] = KeyInput(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
            SendInput(2, inputs, Marshal.SizeOf<INPUT>());
        }
    }

    /// <summary>Presses a named key, optionally with modifiers (Ctrl, Alt, Shift, Win).</summary>
    public bool Key(string name, IEnumerable<string> modifiers)
    {
        if (!TryVk(name, out ushort vk)) return false;
        var mods = modifiers.Select(m => TryVk(m, out ushort v) ? v : (ushort)0).Where(v => v != 0 && v != vk).Distinct().ToList();
        foreach (var m in mods) KeyRaw(m, true);
        KeyRaw(vk, true);
        KeyRaw(vk, false);
        for (int i = mods.Count - 1; i >= 0; i--) KeyRaw(mods[i], false);
        return true;
    }

    /// <summary>Releases anything still held down (called when the phone disconnects).</summary>
    public void ReleaseAll()
    {
        Button('l', false);
        Button('r', false);
        foreach (var vk in _heldKeys.ToArray()) KeyRaw(vk, false);
    }

    private void KeyRaw(ushort vk, bool down)
    {
        uint flags = down ? 0 : KEYEVENTF_KEYUP;
        if (ExtendedKeys.Contains(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        ushort scan = (ushort)MapVirtualKey(vk, 0);
        var inputs = new[] { KeyInput(vk, scan, flags) };
        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
        if (down) _heldKeys.Add(vk); else _heldKeys.Remove(vk);
    }

    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = 0x0D, ["Backspace"] = 0x08, ["Tab"] = 0x09, ["Escape"] = 0x1B, ["Space"] = 0x20,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["PrintScreen"] = 0x2C,
        ["Shift"] = 0x10, ["Ctrl"] = 0x11, ["Alt"] = 0x12, ["Win"] = 0x5B,
        ["VolumeMute"] = 0xAD, ["VolumeDown"] = 0xAE, ["VolumeUp"] = 0xAF,
        ["MediaNext"] = 0xB0, ["MediaPrev"] = 0xB1, ["MediaPlayPause"] = 0xB3,
    };

    private static readonly HashSet<ushort> ExtendedKeys = new()
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2C, 0x2D, 0x2E, 0x5B,
        0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB3,
    };

    private static bool TryVk(string name, out ushort vk)
    {
        if (NamedKeys.TryGetValue(name, out vk)) return true;
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') { vk = c; return true; }
        }
        if (name.Length is 2 or 3 && (name[0] == 'F' || name[0] == 'f') &&
            int.TryParse(name.AsSpan(1), out int f) && f is >= 1 and <= 12)
        {
            vk = (ushort)(0x70 + f - 1);
            return true;
        }
        vk = 0;
        return false;
    }

    // ---- Win32 ---------------------------------------------------------------

    private static void SendMouse(uint flags, int dx, int dy, int data)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = unchecked((uint)data), dwFlags = flags } },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static INPUT KeyInput(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
    };

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
        MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_WHEEL = 0x0800,
        MOUSEEVENTF_HWHEEL = 0x1000, MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
