using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace FPSLimiter
{
    internal static class PlayniteHotkey
    {
        public static bool TryCapture(KeyEventArgs e, out string binding)
        {
            binding = null;
            if (e == null) return false;
            var key = ResolveKey(e);
            if (key == Key.None || IsModifierKey(key)) return false;
            binding = Format(Keyboard.Modifiers, key);
            return true;
        }

        public static bool Matches(string binding, KeyEventArgs e)
        {
            if (e == null || !TryParse(binding, out var modifiers, out var key)) return false;
            return ResolveKey(e) == key && Keyboard.Modifiers == modifiers;
        }

        private static string Format(ModifierKeys modifiers, Key key)
        {
            var parts = new List<string>();
            if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
            if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
            if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
            if ((modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
            parts.Add(key.ToString());
            return string.Join("+", parts);
        }

        internal static bool TryParse(string binding, out ModifierKeys modifiers, out Key key)
        {
            modifiers = ModifierKeys.None;
            key = Key.None;
            if (string.IsNullOrWhiteSpace(binding) || binding.Equals("None", StringComparison.OrdinalIgnoreCase)) return false;

            var foundKey = false;
            foreach (var raw in binding.Split('+'))
            {
                var token = raw.Trim();
                if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || token.Equals("Control", StringComparison.OrdinalIgnoreCase))
                    modifiers |= ModifierKeys.Control;
                else if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                    modifiers |= ModifierKeys.Alt;
                else if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                    modifiers |= ModifierKeys.Shift;
                else if (token.Equals("Win", StringComparison.OrdinalIgnoreCase) || token.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                    modifiers |= ModifierKeys.Windows;
                else if (!foundKey && Enum.TryParse(token, true, out key) && key != Key.None && !IsModifierKey(key))
                    foundKey = true;
                else
                    return false;
            }
            return foundKey;
        }

        private static Key ResolveKey(KeyEventArgs e) => e.Key == Key.System ? e.SystemKey : e.Key;

        private static bool IsModifierKey(Key key) =>
            key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LWin || key == Key.RWin;
    }
}
