using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace FPSLimiter
{
    public enum VrrSupport { Unknown, NotSupported, Supported }

    /// <summary>Outcome of reading a monitor's EDID for variable-refresh (FreeSync / Adaptive-Sync) support.</summary>
    public sealed class VrrDetectionResult
    {
        public VrrSupport Support { get; set; } = VrrSupport.Unknown;
        public string Evidence { get; set; } = "EDID not available";
        public int MinHz { get; set; }
        public int MaxHz { get; set; }
    }

    public sealed class DetectedDisplay : INotifyPropertyChanged
    {
        public string DeviceName { get; set; }
        private string label;
        public string Label
        {
            get => label;
            set
            {
                if (string.Equals(label, value, StringComparison.Ordinal)) return;
                label = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>A pixel resolution supported by a Windows monitor (refresh rates deduplicated).</summary>
    public sealed class DisplayResolution
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public bool IsCurrent { get; set; }
        public string DisplayName => Width + " × " + Height + (IsCurrent ? " (current)" : "");
    }

    /// <summary>Detects connected displays and the display hosting a game's real window.</summary>
    public static class MonitorManager
    {
        private const int EnumCurrentSettings = -1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayMode
        {
            private const int DeviceNameLength = 32;
            private const int FormNameLength = 32;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = DeviceNameLength)] public string DeviceName;
            public short SpecVersion, DriverVersion, Size, DriverExtra;
            public int Fields, PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
            public short Color, Duplex, YResolution, TTOption, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = FormNameLength)] public string FormName;
            public short LogPixels;
            public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
            public int IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayDevice
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayDevices(string device, uint deviceNum, ref DisplayDevice displayDevice, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DisplayMode mode);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
        private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr parameter);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowRect { public int Left, Top, Right, Bottom; }

        // ── VRR support detection (EDID) ─────────────────────────────────────────────────
        // Windows has no simple "is this monitor VRR capable" call for every GPU vendor, so the
        // monitor's own EDID is read: the AMD FreeSync vendor block is authoritative, otherwise a
        // genuinely wide vertical-rate range (e.g. 48-144 Hz) means Adaptive-Sync. A normal fixed
        // monitor reports a narrow range (e.g. 56-76 Hz) or none and is treated as fixed refresh.

        public static VrrDetectionResult DetectVrrSupport(string deviceName)
        {
            var result = new VrrDetectionResult();
            try
            {
                if (string.IsNullOrWhiteSpace(deviceName)) return result;
                var monitorId = GetMonitorDeviceId(deviceName);
                if (string.IsNullOrEmpty(monitorId)) return result;
                var edid = ReadEdid(monitorId);
                if (edid == null || edid.Length < 128) return result;
                return ParseEdidForVrr(edid);
            }
            catch { return result; }
        }

        private static string GetMonitorDeviceId(string deviceName)
        {
            string fallback = null;
            for (uint i = 0; i < 16; i++)
            {
                var dd = new DisplayDevice { cb = Marshal.SizeOf(typeof(DisplayDevice)) };
                if (!EnumDisplayDevices(deviceName, i, ref dd, 0)) break;
                if ((dd.StateFlags & 0x1) != 0) return dd.DeviceID; // attached to desktop
                if (fallback == null) fallback = dd.DeviceID;
            }
            return fallback;
        }

        private static string ParseEdidMonitorName(byte[] edid)
        {
            if (edid == null || edid.Length < 128) return null;
            for (int offset = 54; offset <= 108 && offset + 17 < edid.Length; offset += 18)
            {
                if (edid[offset] != 0 || edid[offset + 1] != 0 ||
                    edid[offset + 2] != 0 || edid[offset + 3] != 0xFC)
                    continue;

                var value = Encoding.ASCII.GetString(edid, offset + 5, 13);
                var line = value.Split('\n')[0].Trim(' ', '\0', '\r', '\n');
                if (!string.IsNullOrWhiteSpace(line)) return line;
            }
            return null;
        }

        private static string GetDetailedMonitorName(string deviceName, string fallbackName)
        {
            try
            {
                var monitorId = GetMonitorDeviceId(deviceName);
                var hardwareId = monitorId?.Split('\\').Skip(1).FirstOrDefault();
                var edidName = ParseEdidMonitorName(ReadEdid(monitorId));
                var baseName = !string.IsNullOrWhiteSpace(edidName) &&
                    !string.Equals(edidName, hardwareId, StringComparison.OrdinalIgnoreCase)
                    ? edidName
                    : fallbackName;

                if (string.IsNullOrWhiteSpace(baseName)) baseName = deviceName;
                if (string.IsNullOrWhiteSpace(hardwareId) ||
                    baseName.StartsWith(hardwareId + " - ", StringComparison.OrdinalIgnoreCase))
                    return baseName;
                if (string.Equals(baseName, hardwareId, StringComparison.OrdinalIgnoreCase))
                    return baseName;
                return hardwareId + " - " + baseName;
            }
            catch
            {
                return string.IsNullOrWhiteSpace(fallbackName) ? deviceName : fallbackName;
            }
        }

        private static byte[] ReadEdid(string monitorDeviceId)
        {
            // MONITOR\<HWID>\{class guid}\<index>
            var parts = monitorDeviceId.Split('\\');
            if (parts.Length < 2 || !string.Equals(parts[0], "MONITOR", StringComparison.OrdinalIgnoreCase)) return null;
            string hwid = parts[1];
            string driver = parts.Length >= 4 ? parts[2] + "\\" + parts[3] : null;
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY\" + hwid))
            {
                if (key == null) return null;
                byte[] fallback = null;
                foreach (var instance in key.GetSubKeyNames())
                {
                    using (var ik = key.OpenSubKey(instance))
                    {
                        if (ik == null) continue;
                        var drv = ik.GetValue("Driver") as string;
                        using (var dp = ik.OpenSubKey("Device Parameters"))
                        {
                            var edid = dp?.GetValue("EDID") as byte[];
                            if (edid == null) continue;
                            if (driver != null && string.Equals(drv, driver, StringComparison.OrdinalIgnoreCase)) return edid;
                            if (fallback == null) fallback = edid;
                        }
                    }
                }
                return fallback;
            }
        }

        private static VrrDetectionResult ParseEdidForVrr(byte[] e)
        {
            var r = new VrrDetectionResult();

            // Display Range Limits descriptor (tag 0xFD) in the four 18-byte descriptor slots.
            int minV = 0, maxV = 0;
            bool haveRange = false;
            for (int off = 54; off <= 108 && off + 17 < e.Length; off += 18)
            {
                if (e[off] == 0 && e[off + 1] == 0 && e[off + 2] == 0 && e[off + 3] == 0xFD)
                {
                    minV = e[off + 5];
                    maxV = e[off + 6];
                    int offsets = e[off + 4] & 0x3;
                    if (offsets == 2) maxV += 255;
                    else if (offsets == 3) { minV += 255; maxV += 255; }
                    haveRange = maxV > 0;
                }
            }

            // CTA-861 extension blocks: look for the AMD FreeSync vendor-specific data block.
            bool amdFound = false, amdFreeSync = false;
            int amdMin = 0, amdMax = 0;
            int extCount = e.Length > 126 ? e[126] : 0;
            for (int blk = 1; blk <= extCount && (blk + 1) * 128 <= e.Length; blk++)
            {
                int b = blk * 128;
                if (e[b] != 0x02) continue;
                int dtd = e[b + 2];
                if (dtd < 4) continue;
                int pos = b + 4;
                int end = b + Math.Min(dtd, 127);
                while (pos < end)
                {
                    int hdr = e[pos];
                    int tag = hdr >> 5;
                    int len = hdr & 0x1F;
                    if (pos + len >= b + 128) break;
                    // Vendor-specific block, IEEE OUI 00-00-1A (AMD) stored little-endian.
                    if (tag == 3 && len >= 7 && e[pos + 1] == 0x1A && e[pos + 2] == 0x00 && e[pos + 3] == 0x00)
                    {
                        amdFound = true;
                        amdFreeSync = (e[pos + 5] & 0x01) != 0;
                        amdMin = e[pos + 6];
                        amdMax = e[pos + 7];
                    }
                    pos += len + 1;
                }
            }

            if (amdFound)
            {
                r.MinHz = amdMin; r.MaxHz = amdMax;
                if (amdFreeSync)
                {
                    r.Support = VrrSupport.Supported;
                    r.Evidence = "FreeSync " + amdMin + "-" + amdMax + " Hz";
                }
                else
                {
                    r.Support = VrrSupport.NotSupported;
                    r.Evidence = "FreeSync off";
                }
                return r;
            }

            if (haveRange)
            {
                r.MinHz = minV; r.MaxHz = maxV;
                // Fixed monitors list a narrow range; Adaptive-Sync monitors a wide one.
                if (minV > 0 && minV <= maxV * 0.5 && (maxV - minV) >= 40)
                {
                    r.Support = VrrSupport.Supported;
                    r.Evidence = "range " + minV + "-" + maxV + " Hz";
                }
                else
                {
                    r.Support = VrrSupport.NotSupported;
                    r.Evidence = "range " + minV + "-" + maxV + " Hz";
                }
                return r;
            }

            r.Support = VrrSupport.NotSupported;
            r.Evidence = "no VRR info";
            return r;
        }

        public static List<DetectedDisplay> GetDisplays()
        {
            return Screen.AllScreens.Select((screen, index) =>
            {
                var displayDevice = new DisplayDevice { cb = Marshal.SizeOf(typeof(DisplayDevice)) };
                var friendlyName = EnumDisplayDevices(screen.DeviceName, 0, ref displayDevice, 0)
                    ? displayDevice.DeviceString : null;
                if (string.IsNullOrWhiteSpace(friendlyName))
                {
                    friendlyName = screen.DeviceName;
                }
                var detailedName = GetDetailedMonitorName(screen.DeviceName, friendlyName);

                return new DetectedDisplay
                {
                    DeviceName = screen.DeviceName,
                    Label = detailedName + " — " + screen.Bounds.Width + "×" + screen.Bounds.Height +
                        (screen.Primary ? " (Primary)" : "")
                };
            }).ToList();
        }

        public static string PrimaryDeviceName => Screen.PrimaryScreen?.DeviceName;

        /// <summary>Gets the active Windows display-mode resolution, including exclusive fullscreen switches.</summary>
        public static DisplayResolution GetCurrentResolution(string deviceName)
        {
            var mode = new DisplayMode();
            mode.Size = (short)Marshal.SizeOf(typeof(DisplayMode));
            if (!string.IsNullOrEmpty(deviceName) &&
                EnumDisplaySettings(deviceName, EnumCurrentSettings, ref mode) &&
                mode.PelsWidth > 0 && mode.PelsHeight > 0)
            {
                return new DisplayResolution { Width = mode.PelsWidth, Height = mode.PelsHeight, IsCurrent = true };
            }
            var screen = Screen.AllScreens.FirstOrDefault(s =>
                string.Equals(s.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
            return screen == null ? null : new DisplayResolution
            {
                Width = screen.Bounds.Width, Height = screen.Bounds.Height, IsCurrent = true
            };
        }

        /// <summary>Lists pixel resolutions, not separate refresh rates or color depths.</summary>
        public static List<DisplayResolution> GetSupportedResolutions(string deviceName)
        {
            var current = GetCurrentResolution(deviceName);
            var result = new List<DisplayResolution>();
            if (string.IsNullOrWhiteSpace(deviceName)) return result;
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < 4096; index++)
            {
                var mode = new DisplayMode();
                mode.Size = (short)Marshal.SizeOf(typeof(DisplayMode));
                if (!EnumDisplaySettings(deviceName, index, ref mode)) break;
                if (mode.PelsWidth < 640 || mode.PelsHeight < 480) continue;
                var key = mode.PelsWidth + "x" + mode.PelsHeight;
                if (unique.Add(key)) result.Add(new DisplayResolution
                {
                    Width = mode.PelsWidth, Height = mode.PelsHeight
                });
            }
            if (current != null)
            {
                var existing = result.FirstOrDefault(r => r.Width == current.Width && r.Height == current.Height);
                if (existing != null) existing.IsCurrent = true;
                else result.Add(current);
            }
            return result.OrderByDescending(r => r.IsCurrent)
                         .ThenByDescending(r => (long)r.Width * r.Height)
                         .ThenByDescending(r => r.Width).ToList();
        }

        public static int GetRefreshRate(string deviceName)
        {
            var mode = new DisplayMode();
            mode.Size = (short)Marshal.SizeOf(typeof(DisplayMode));
            return EnumDisplaySettings(deviceName, EnumCurrentSettings, ref mode) ? mode.DisplayFrequency : 0;
        }

        /// <summary>Fractional signal timing of this monitor (e.g. 29.9001, not rounded 30).</summary>
        public static double GetPreciseRefreshRate(string deviceName)
        {
            return RefreshRateManager.GetCurrentPreciseRefreshRate(deviceName);
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        private const uint SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

        private static IntPtr FindLargestVisibleWindow(int processId)
        {
            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            EnumWindows((hwnd, ignored) =>
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid != processId || !IsWindowVisible(hwnd)) return true;
                WindowRect rect;
                if (!GetWindowRect(hwnd, out rect)) return true;
                long width = (long)rect.Right - rect.Left;
                long height = (long)rect.Bottom - rect.Top;
                var area = width * height;
                if (width > 100 && height > 100 && area > bestArea) { bestArea = area; best = hwnd; }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        /// <summary>
        /// Moves the process's main window onto the target display. Borderless / fullscreen-sized windows are
        /// stretched to the target display's bounds; normal windows keep their size and are centred on it.
        /// Every window handle is handled only once (stored in <paramref name="handled"/>) so a window the
        /// user drags elsewhere afterwards is never fought over. Returns true only when a window was moved.
        /// </summary>
        public static bool TryMoveProcessWindowToMonitor(int processId, string targetDeviceName, ISet<long> handled, out string detail)
        {
            detail = null;
            if (processId <= 0 || string.IsNullOrWhiteSpace(targetDeviceName)) return false;
            try
            {
                var hwnd = FindLargestVisibleWindow(processId);
                if (hwnd == IntPtr.Zero) return false;
                if (!handled.Add(hwnd.ToInt64())) return false;

                var target = Screen.AllScreens.FirstOrDefault(s =>
                    string.Equals(s.DeviceName, targetDeviceName, StringComparison.OrdinalIgnoreCase));
                if (target == null) return false;
                var source = Screen.FromHandle(hwnd);
                if (string.Equals(source.DeviceName, target.DeviceName, StringComparison.OrdinalIgnoreCase)) return false;

                WindowRect rect;
                if (!GetWindowRect(hwnd, out rect)) return false;
                int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
                var src = source.Bounds;
                bool coversMonitor = width >= src.Width - 2 && height >= src.Height - 2;

                bool ok;
                if (coversMonitor)
                {
                    // Borderless / fullscreen-sized window: cover the target display instead.
                    var t = target.Bounds;
                    ok = SetWindowPos(hwnd, IntPtr.Zero, t.X, t.Y, t.Width, t.Height, SwpNoZOrder | SwpNoActivate);
                    detail = "fullscreen window moved to " + target.DeviceName + " (" + t.Width + "x" + t.Height + ")";
                }
                else
                {
                    var wa = target.WorkingArea;
                    int x = wa.X + Math.Max(0, (wa.Width - width) / 2);
                    int y = wa.Y + Math.Max(0, (wa.Height - height) / 2);
                    ok = SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
                    detail = "window centred on " + target.DeviceName;
                }
                return ok;
            }
            catch { return false; }
        }

        // ── Synthetic hotkey (used to fire Special K macros in a running game) ────────────────
        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInputData { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        private struct KeyInputData { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extraInfo; }
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion { [FieldOffset(0)] public MouseInputData mi; [FieldOffset(0)] public KeyInputData ki; }
        [StructLayout(LayoutKind.Sequential)]
        private struct InputData { public uint type; public InputUnion u; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, InputData[] inputs, int size);
        [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);

        private const uint KeyEventScanCode = 0x0008, KeyEventKeyUp = 0x0002;
        private const ushort VkLCtrl = 0xA2, VkLShift = 0xA0, VkLAlt = 0xA4;

        private static InputData KeyInput(ushort vk, bool up)
        {
            var input = new InputData { type = 1 };
            input.u.ki = new KeyInputData
            {
                wVk = 0,
                wScan = (ushort)MapVirtualKey(vk, 0),
                dwFlags = KeyEventScanCode | (up ? KeyEventKeyUp : 0)
            };
            return input;
        }

        /// <summary>
        /// True while every thread of the process is suspended (the game overlay soft-pauses the game with
        /// NtSuspendProcess; a paused game cannot react to hotkeys).
        /// </summary>
        public static bool IsProcessSuspended(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    int total = 0, suspended = 0;
                    foreach (ProcessThread thread in process.Threads)
                    {
                        total++;
                        try
                        {
                            if (thread.ThreadState == System.Diagnostics.ThreadState.Wait &&
                                thread.WaitReason == ThreadWaitReason.Suspended) suspended++;
                        }
                        catch { }
                    }
                    return total > 0 && suspended >= total * 0.9;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// Presses Ctrl+Shift+key in the process's window (bringing it to the front first). Waits briefly for any
        /// modifier keys the user is still holding so the game sees exactly Ctrl+Shift+key. Blocking: call from a
        /// background thread.
        /// </summary>
        public static bool SendCtrlShiftKeyToProcess(int processId, char key)
        {
            try
            {
                var hwnd = FindLargestVisibleWindow(processId);
                if (hwnd == IntPtr.Zero) return false;
                // The game must really be in front, otherwise Special K never sees the keys.
                bool inFront = false;
                for (int attempt = 0; attempt < 6 && !inFront; attempt++)
                {
                    uint foregroundPid = 0;
                    var foreground = GetForegroundWindow();
                    if (foreground != IntPtr.Zero) GetWindowThreadProcessId(foreground, out foregroundPid);
                    if (foregroundPid == processId) { inFront = true; break; }
                    SetForegroundWindow(hwnd);
                    System.Threading.Thread.Sleep(150);
                }
                if (!inFront) return false;

                // Let go of Ctrl/Alt/Shift first (e.g. the extension hotkey that triggered this).
                for (int i = 0; i < 30; i++)
                {
                    bool held = (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x10) & 0x8000) != 0 ||
                                (GetAsyncKeyState(0x12) & 0x8000) != 0;
                    if (!held) break;
                    System.Threading.Thread.Sleep(50);
                }

                ushort vk = (ushort)key; // '0'-'9' share their virtual-key codes
                var steps = new[]
                {
                    KeyInput(VkLCtrl, false), KeyInput(VkLShift, false),
                    KeyInput(vk, false), KeyInput(vk, true),
                    KeyInput(VkLShift, true), KeyInput(VkLCtrl, true)
                };
                foreach (var step in steps)
                {
                    SendInput(1, new[] { step }, Marshal.SizeOf(typeof(InputData)));
                    System.Threading.Thread.Sleep(25);
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>Returns null if the game has not yet created a suitable window.</summary>
        public static string TryGetProcessMonitor(int processId)
        {
            if (processId <= 0) return null;
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    var hwnd = process.MainWindowHandle;
                    if (hwnd != IntPtr.Zero)
                        return Screen.FromHandle(hwnd).DeviceName;
                }
                // Some games expose the real top-level window before Process.MainWindowHandle is populated.
                IntPtr bestWindow = IntPtr.Zero;
                long bestArea = 0;
                EnumWindows((hwnd, ignored) =>
                {
                    uint pid;
                    GetWindowThreadProcessId(hwnd, out pid);
                    if (pid != processId || !IsWindowVisible(hwnd)) return true;
                    WindowRect rect;
                    if (!GetWindowRect(hwnd, out rect)) return true;
                    long width = (long)rect.Right - rect.Left;
                    long height = (long)rect.Bottom - rect.Top;
                    var area = width * height;
                    if (width > 100 && height > 100 && area > bestArea)
                    {
                        bestArea = area;
                        bestWindow = hwnd;
                    }
                    return true;
                }, IntPtr.Zero);
                return bestWindow != IntPtr.Zero ? Screen.FromHandle(bestWindow).DeviceName : null;
            }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (System.ComponentModel.Win32Exception) { return null; }
        }
    }
}
