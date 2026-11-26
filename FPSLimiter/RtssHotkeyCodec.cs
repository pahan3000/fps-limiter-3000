using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace FPSLimiter
{
    /// <summary>
    /// Converts a PlayniteHotkey binding ("Ctrl+Oem5") to the value RTSS's HotkeyHandler.cfg stores.
    /// RTSS uses the MFC hot-key control layout: low word = virtual-key code, high word = modifier flags
    /// (Shift 0x01, Ctrl 0x02, Alt 0x04, Extended-key 0x08). The user's own 000C0022 (Alt + extended PgDn) matches this.
    /// The Windows key cannot be stored in that format.
    /// </summary>
    internal static class RtssHotkeyCodec
    {
        private static readonly HashSet<int> ExtendedKeys = new HashSet<int>
        {
            0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // PgUp PgDn End Home arrows
            0x2D, 0x2E,                                     // Insert Delete
            0x6F, 0x90                                      // Numpad divide, NumLock
        };

        /// <summary>Returns the 8-digit hex RTSS value, or "00000000" for None / unsupported bindings.</summary>
        public static string ToRtssValue(string binding)
        {
            if (!PlayniteHotkey.TryParse(binding, out var mods, out var key)) return "00000000";
            if ((mods & ModifierKeys.Windows) != 0) return "00000000";
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk <= 0 || vk > 0xFF) return "00000000";
            int flags = 0;
            if ((mods & ModifierKeys.Shift) != 0) flags |= 0x01;
            if ((mods & ModifierKeys.Control) != 0) flags |= 0x02;
            if ((mods & ModifierKeys.Alt) != 0) flags |= 0x04;
            if (ExtendedKeys.Contains(vk)) flags |= 0x08;
            return ((flags << 16) | vk).ToString("X8");
        }

        public static bool IsSupported(string binding)
        {
            if (!PlayniteHotkey.TryParse(binding, out var mods, out _)) return true; // None
            return (mods & ModifierKeys.Windows) == 0;
        }

        public static string ToDisplay(string binding)
        {
            if (string.IsNullOrWhiteSpace(binding) || binding.Equals("None", StringComparison.OrdinalIgnoreCase)) return "None";
            var parts = binding.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                switch (parts[i].Trim())
                {
                    case "Oem5": parts[i] = "\\"; break;
                    case "OemPipe": parts[i] = "\\"; break;
                    case "Oem1": parts[i] = ";"; break;
                    case "Oem2": parts[i] = "/"; break;
                    case "Oem3": parts[i] = "`"; break;
                    case "Oem4": parts[i] = "["; break;
                    case "Oem6": parts[i] = "]"; break;
                    case "Oem7": parts[i] = "'"; break;
                    case "OemPlus": parts[i] = "="; break;
                    case "OemMinus": parts[i] = "-"; break;
                    case "OemComma": parts[i] = ","; break;
                    case "OemPeriod": parts[i] = "."; break;
                    case "Next": parts[i] = "PageDown"; break;
                    case "Prior": parts[i] = "PageUp"; break;
                }
            }
            return string.Join("+", parts);
        }
    }
}
