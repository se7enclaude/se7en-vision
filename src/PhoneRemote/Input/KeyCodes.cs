using System.Runtime.InteropServices;

namespace PhoneRemote.Input;

/// <summary>
/// Browser <c>KeyboardEvent.code</c> (the physical key, layout-independent) → Windows virtual key for this PC's
/// layout. Letters, digits and punctuation go through the scan code so an AZERTY host receives what the viewer's
/// physical key means here; named keys map directly.
/// </summary>
public static class KeyCodes
{
    private static readonly Dictionary<string, ushort> Named = new(StringComparer.Ordinal)
    {
        ["Enter"] = 0x0D, ["NumpadEnter"] = 0x0D, ["Backspace"] = 0x08, ["Tab"] = 0x09, ["Escape"] = 0x1B, ["Space"] = 0x20,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24,
        ["ArrowLeft"] = 0x25, ["ArrowUp"] = 0x26, ["ArrowRight"] = 0x27, ["ArrowDown"] = 0x28,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["PrintScreen"] = 0x2C, ["ScrollLock"] = 0x91, ["Pause"] = 0x13,
        ["CapsLock"] = 0x14, ["NumLock"] = 0x90, ["ContextMenu"] = 0x5D,
        ["ShiftLeft"] = 0xA0, ["ShiftRight"] = 0xA1, ["ControlLeft"] = 0xA2, ["ControlRight"] = 0xA3,
        ["AltLeft"] = 0xA4, ["AltRight"] = 0xA5, ["MetaLeft"] = 0x5B, ["MetaRight"] = 0x5C, ["OSLeft"] = 0x5B, ["OSRight"] = 0x5C,
        ["NumpadAdd"] = 0x6B, ["NumpadSubtract"] = 0x6D, ["NumpadMultiply"] = 0x6A, ["NumpadDivide"] = 0x6F, ["NumpadDecimal"] = 0x6E,
        ["AudioVolumeMute"] = 0xAD, ["AudioVolumeDown"] = 0xAE, ["AudioVolumeUp"] = 0xAF,
        ["MediaTrackNext"] = 0xB0, ["MediaTrackPrevious"] = 0xB1, ["MediaPlayPause"] = 0xB3, ["MediaStop"] = 0xB2,
    };

    // US scan codes (set 1) of the printable keys: the host's layout turns them into its own characters.
    private static readonly Dictionary<string, ushort> Scan = new(StringComparer.Ordinal)
    {
        ["Backquote"] = 0x29, ["Digit1"] = 0x02, ["Digit2"] = 0x03, ["Digit3"] = 0x04, ["Digit4"] = 0x05, ["Digit5"] = 0x06,
        ["Digit6"] = 0x07, ["Digit7"] = 0x08, ["Digit8"] = 0x09, ["Digit9"] = 0x0A, ["Digit0"] = 0x0B, ["Minus"] = 0x0C, ["Equal"] = 0x0D,
        ["KeyQ"] = 0x10, ["KeyW"] = 0x11, ["KeyE"] = 0x12, ["KeyR"] = 0x13, ["KeyT"] = 0x14, ["KeyY"] = 0x15, ["KeyU"] = 0x16,
        ["KeyI"] = 0x17, ["KeyO"] = 0x18, ["KeyP"] = 0x19, ["BracketLeft"] = 0x1A, ["BracketRight"] = 0x1B,
        ["KeyA"] = 0x1E, ["KeyS"] = 0x1F, ["KeyD"] = 0x20, ["KeyF"] = 0x21, ["KeyG"] = 0x22, ["KeyH"] = 0x23, ["KeyJ"] = 0x24,
        ["KeyK"] = 0x25, ["KeyL"] = 0x26, ["Semicolon"] = 0x27, ["Quote"] = 0x28, ["Backslash"] = 0x2B,
        ["KeyZ"] = 0x2C, ["KeyX"] = 0x2D, ["KeyC"] = 0x2E, ["KeyV"] = 0x2F, ["KeyB"] = 0x30, ["KeyN"] = 0x31, ["KeyM"] = 0x32,
        ["Comma"] = 0x33, ["Period"] = 0x34, ["Slash"] = 0x35, ["IntlBackslash"] = 0x56,
    };

    /// <summary>Virtual key and whether it is an "extended" key; false when the code is unknown.</summary>
    public static bool TryMap(string code, out ushort vk, out bool extended)
    {
        extended = false;
        if (Named.TryGetValue(code, out vk))
        {
            extended = vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2C or 0x2D or 0x2E
                or 0x5B or 0x5C or 0x5D or 0xA3 or 0xA5 or 0x6F or 0x90 or 0xAD or 0xAE or 0xAF or 0xB0 or 0xB1 or 0xB2 or 0xB3
                || code == "NumpadEnter";
            return true;
        }
        if (code.Length is 2 or 3 && code[0] == 'F' && int.TryParse(code.AsSpan(1), out int f) && f is >= 1 and <= 24)
        {
            vk = (ushort)(0x70 + f - 1);
            return true;
        }
        if (code.StartsWith("Numpad", StringComparison.Ordinal) && code.Length == 7 && char.IsDigit(code[6]))
        {
            vk = (ushort)(0x60 + (code[6] - '0'));
            return true;
        }
        if (Scan.TryGetValue(code, out var scan))
        {
            uint mapped = MapVirtualKey(scan, MAPVK_VSC_TO_VK_EX);
            if (mapped == 0) return false;
            vk = (ushort)mapped;
            return true;
        }
        return false;
    }

    private const uint MAPVK_VSC_TO_VK_EX = 3;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
