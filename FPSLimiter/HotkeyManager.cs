using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace FPSLimiter
{
    /// <summary>Foreground-scoped low-level hotkeys. Unlike RegisterHotKey, these do not
    /// reserve key combinations throughout Windows while Playnite is running.</summary>
    public class HotkeyManager : IDisposable
    {
        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x100;
        private const int WM_SYSKEYDOWN = 0x104;
        private readonly HookProc proc;
        private IntPtr hook;
        private List<HotkeyBinding> bindings = new List<HotkeyBinding>();
        private bool disposed;
        public event Action<HotkeyBinding> HotkeyPressed;
        /// <summary>Must be a fast, non-blocking check for foreground ownership.</summary>
        public Func<bool> IsAllowedForeground { get; set; }
        /// <summary>Fast check: true while any game is running. Lets global (non-game-specific)
        /// actions work when the game's window belongs to a process Playnite does not track.</summary>
        public Func<bool> IsGameSessionActive { get; set; }
        /// <summary>
        /// True while a hotkey capture box has focus. The hook then lets every key through, otherwise it
        /// swallows a combo that is already assigned (e.g. Ctrl+1) before the capture box can see it.
        /// </summary>
        public static volatile bool CaptureActive;
        public bool IsAttached => hook != IntPtr.Zero;
        public HotkeyManager() { proc = Callback; }
        public bool Attach(Window window)
        {
            if (disposed) return false;
            if (hook != IntPtr.Zero) return true;
            hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, IntPtr.Zero, 0);
            return hook != IntPtr.Zero;
        }
        public void UpdateBindings(IEnumerable<HotkeyBinding> requested)
        {
            bindings = requested == null ? new List<HotkeyBinding>() :
                requested.Where(b => b != null && b.Key != Key.None).ToList();
        }
        private IntPtr Callback(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && !CaptureActive && (message == (IntPtr)WM_KEYDOWN || message == (IntPtr)WM_SYSKEYDOWN))
            {
                try
                {
                    var strictAllowed = IsAllowedForeground != null && IsAllowedForeground();
                    var sessionAllowed = !strictAllowed && IsGameSessionActive != null && IsGameSessionActive();
                    if (strictAllowed || sessionAllowed)
                    {
                        int vk = Marshal.ReadInt32(data);
                        var key = KeyInterop.KeyFromVirtualKey(vk);
                        ModifierKeys modifiers = ModifierKeys.None;
                        if ((GetAsyncKeyState(0x12) & 0x8000) != 0) modifiers |= ModifierKeys.Alt;
                        if ((GetAsyncKeyState(0x11) & 0x8000) != 0) modifiers |= ModifierKeys.Control;
                        if ((GetAsyncKeyState(0x10) & 0x8000) != 0) modifiers |= ModifierKeys.Shift;
                        if ((GetAsyncKeyState(0x5B) & 0x8000) != 0 ||
                            (GetAsyncKeyState(0x5C) & 0x8000) != 0) modifiers |= ModifierKeys.Windows;
                        foreach (var b in bindings)
                        {
                            if (b.Key != key || b.Modifiers != modifiers) continue;
                            // With a game running but a foreground process we don't track, only the
                            // global RTSS toggle may fire; per-game actions keep the strict rule.
                            if (!strictAllowed && b.Action != HotkeyAction.ToggleFrameColorIndicator) continue;
                            HotkeyPressed?.Invoke(b);
                            return (IntPtr)1;
                        }
                    }
                }
                catch { /* Never fail open by swallowing another application's keys. */ }
            }
            return CallNextHookEx(hook, code, message, data);
        }
        public void Dispose()
        {
            disposed = true;
            if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        }
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    }
}
