using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace FPSLimiter
{
    /// <summary>
    /// Changes and restores the primary display refresh rate using Win32 ChangeDisplaySettingsEx.
    /// </summary>
    public static class RefreshRateManager
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        // ── Win32 interop ────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        private static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

        private const int ENUM_CURRENT_SETTINGS = -1;
        private const int DM_DISPLAYFREQUENCY = 0x400000;
        private const int DISP_CHANGE_SUCCESSFUL = 0;
        private const uint CDS_UPDATEREGISTRY = 0x01;
        private const uint CDS_NORESET = 0x10000000;

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>
        /// Reads precise active signal Hz for the specified GDI monitor (or primary monitor).
        /// Returns a nominal integer only when Windows/driver supplies no fractional timing.
        /// </summary>
        public static double GetCurrentPreciseRefreshRate(string deviceName = null)
        {
            deviceName = string.IsNullOrWhiteSpace(deviceName) ? MonitorManager.PrimaryDeviceName : deviceName;
            var precise = PreciseRefreshRateReader.GetActiveRefreshRate(deviceName);
            if (precise > 0) return precise;
            return MonitorManager.GetRefreshRate(deviceName);
        }

        /// <summary>
        /// Converts a nominal refresh mode (30 Hz label) to its real 29.9001 Hz timing ONLY
        /// when it matches the currently active display mode. We must not pretend some other
        /// inactive mode has the same fractional timing.
        /// </summary>
        public static double GetPreciseCapForNominalMode(int nominalHz, string deviceName = null)
        {
            if (nominalHz <= 0) return 0;
            deviceName = string.IsNullOrWhiteSpace(deviceName) ? MonitorManager.PrimaryDeviceName : deviceName;
            if (MonitorManager.GetRefreshRate(deviceName) != nominalHz)
            {
                var other = GetRefreshModes(deviceName, 0, 0).FirstOrDefault(m => m.Nominal == nominalHz);
                return other != null ? other.Hz : nominalHz;
            }
            var measured = GetCurrentPreciseRefreshRate(deviceName);
            return measured > 0 && Math.Abs(measured - nominalHz) < 1.0 ? measured : nominalHz;
        }

        /// <summary>Returns the current refresh rate of the primary display, or 0 on failure.</summary>
        public static int GetCurrentRefreshRate()
        {
            return GetCurrentRefreshRate(null);
        }

        /// <summary>Returns the current refresh rate of the given display (null/empty = primary), or 0.</summary>
        public static int GetCurrentRefreshRate(string deviceName)
        {
            var dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(dm);
            if (EnumDisplaySettings(NormalizeDevice(deviceName), ENUM_CURRENT_SETTINGS, ref dm))
            {
                return dm.dmDisplayFrequency;
            }

            return 0;
        }

        private static string NormalizeDevice(string deviceName)
        {
            return string.IsNullOrWhiteSpace(deviceName) ? null : deviceName;
        }

        /// <summary>Active Windows display mode (resolution + nominal refresh) of one display.</summary>
        public sealed class DisplayModeInfo
        {
            public int Width { get; set; }
            public int Height { get; set; }
            public int Hz { get; set; }
            public override string ToString() { return Width + "x" + Height + " @ " + Hz + " Hz"; }
        }

        public static DisplayModeInfo GetCurrentMode(string deviceName)
        {
            var dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(dm);
            if (!EnumDisplaySettings(NormalizeDevice(deviceName), ENUM_CURRENT_SETTINGS, ref dm) ||
                dm.dmPelsWidth <= 0 || dm.dmPelsHeight <= 0) return null;
            return new DisplayModeInfo { Width = dm.dmPelsWidth, Height = dm.dmPelsHeight, Hz = dm.dmDisplayFrequency };
        }

        /// <summary>Refresh rates (Hz) the display offers at a specific resolution and the current color depth.</summary>
        public static List<int> GetSupportedRefreshRatesAt(string deviceName, int width, int height)
        {
            var rates = new List<int>();
            var current = new DEVMODE();
            current.dmSize = (short)Marshal.SizeOf(current);
            if (!EnumDisplaySettings(NormalizeDevice(deviceName), ENUM_CURRENT_SETTINGS, ref current)) return rates;

            var dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(dm);
            for (int modeNum = 0; EnumDisplaySettings(NormalizeDevice(deviceName), modeNum, ref dm); modeNum++)
            {
                if (dm.dmPelsWidth == width && dm.dmPelsHeight == height &&
                    dm.dmBitsPerPel == current.dmBitsPerPel &&
                    dm.dmDisplayFrequency > 1 && !rates.Contains(dm.dmDisplayFrequency))
                    rates.Add(dm.dmDisplayFrequency);
            }
            rates.Sort();
            return rates;
        }

        /// <summary>Distinct resolutions (width, height) the display offers at the current color depth, largest first.</summary>
        public static List<DisplayModeInfo> GetSupportedResolutions(string deviceName)
        {
            var result = new List<DisplayModeInfo>();
            var current = new DEVMODE();
            current.dmSize = (short)Marshal.SizeOf(current);
            if (!EnumDisplaySettings(NormalizeDevice(deviceName), ENUM_CURRENT_SETTINGS, ref current)) return result;

            var dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(dm);
            for (int modeNum = 0; EnumDisplaySettings(NormalizeDevice(deviceName), modeNum, ref dm); modeNum++)
            {
                if (dm.dmBitsPerPel != current.dmBitsPerPel || dm.dmPelsWidth <= 0 || dm.dmPelsHeight <= 0) continue;
                var w = dm.dmPelsWidth; var h = dm.dmPelsHeight;
                if (!result.Any(r => r.Width == w && r.Height == h))
                    result.Add(new DisplayModeInfo { Width = w, Height = h });
            }
            return result.OrderByDescending(r => (long)r.Width * r.Height).ThenByDescending(r => r.Width).ToList();
        }

        private const int DM_PELSWIDTH = 0x80000;
        private const int DM_PELSHEIGHT = 0x100000;
        private const uint CDS_TEST = 0x2;

        /// <summary>
        /// Switches one display to an exact resolution + refresh rate. The mode is validated first
        /// (CDS_TEST) and applied dynamically (not written to the registry), so a reboot or logoff
        /// always returns Windows to the user's saved desktop mode.
        /// </summary>
        public static bool SetDisplayMode(string deviceName, int width, int height, int hz)
        {
            var dev = NormalizeDevice(deviceName);
            var dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(dm);
            if (!EnumDisplaySettings(dev, ENUM_CURRENT_SETTINGS, ref dm))
            {
                logger.Error("RefreshRateManager: EnumDisplaySettings failed for " + (dev ?? "primary") + ".");
                return false;
            }
            if (dm.dmPelsWidth == width && dm.dmPelsHeight == height && (hz <= 0 || dm.dmDisplayFrequency == hz))
                return true;

            dm.dmPelsWidth = width;
            dm.dmPelsHeight = height;
            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;
            if (hz > 0)
            {
                dm.dmDisplayFrequency = hz;
                dm.dmFields |= DM_DISPLAYFREQUENCY;
            }

            int test = ChangeDisplaySettingsEx(dev, ref dm, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
            if (test != DISP_CHANGE_SUCCESSFUL)
            {
                logger.Warn("RefreshRateManager: " + width + "x" + height + " @ " + hz + " Hz is not accepted by " + (dev ?? "primary") + " (code " + test + ").");
                return false;
            }
            int result = ChangeDisplaySettingsEx(dev, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
            if (result == DISP_CHANGE_SUCCESSFUL)
            {
                logger.Info("RefreshRateManager: " + (dev ?? "primary") + " set to " + width + "x" + height + " @ " + hz + " Hz.");
                return true;
            }
            logger.Error("RefreshRateManager: ChangeDisplaySettingsEx returned " + result + " for " + width + "x" + height + " @ " + hz + " Hz.");
            return false;
        }

        /// <summary>
        /// Sets the primary display refresh rate.
        /// </summary>
        /// <param name="hz">Target refresh rate in Hz.</param>
        /// <returns>True on success.</returns>
        public static bool SetRefreshRate(int hz)
        {
            return SetRefreshRate(hz, null);
        }

        /// <summary>Sets the refresh rate of a specific display (null/empty = primary).</summary>
        public static bool SetRefreshRate(int hz, string deviceName)
        {
            var dev = NormalizeDevice(deviceName);
            var dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(dm);

            if (!EnumDisplaySettings(dev, ENUM_CURRENT_SETTINGS, ref dm))
            {
                logger.Error("RefreshRateManager: EnumDisplaySettings failed.");
                return false;
            }

            if (dm.dmDisplayFrequency == hz)
            {
                logger.Info($"RefreshRateManager: Already at {hz} Hz, nothing to do.");
                return true;
            }

            dm.dmDisplayFrequency = hz;
            dm.dmFields = DM_DISPLAYFREQUENCY;

            int result = ChangeDisplaySettingsEx(dev, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
            if (result == DISP_CHANGE_SUCCESSFUL)
            {
                logger.Info($"RefreshRateManager: Switched to {hz} Hz.");
                return true;
            }

            logger.Error($"RefreshRateManager: ChangeDisplaySettingsEx returned {result} when trying {hz} Hz.");
            return false;
        }

        /// <summary>
        /// Returns the distinct refresh rates (Hz) the primary display supports at its current
        /// resolution and color depth.
        /// </summary>
        public static List<int> GetSupportedRefreshRates(string deviceName = null)
        {
            var modes = GetRefreshModes(deviceName, 0, 0);
            if (modes.Count > 0)
                return modes.Select(m => m.Nominal).Distinct().OrderBy(r => r).ToList();
            return GetRawSupportedRefreshRates(deviceName);
        }

        /// <summary>The display's real refresh modes at a resolution (0,0 = the current one), each with its exact Hz.</summary>
        public static List<RefreshMode> GetRefreshModes(string deviceName, int width, int height)
        {
            var result = new List<RefreshMode>();
            try
            {
                if (width <= 0 || height <= 0)
                {
                    var cur = GetCurrentMode(deviceName);
                    if (cur == null) return result;
                    width = cur.Width;
                    height = cur.Height;
                }
                var raw = (width > 0 ? GetSupportedRefreshRatesAt(deviceName, width, height) : null) ?? new List<int>();
                var exact = DxgiModeReader.GetRates(string.IsNullOrWhiteSpace(deviceName) ? MonitorManager.PrimaryDeviceName : deviceName, width, height);

                foreach (var hz in exact)
                {
                    // The whole number Windows accepts for this mode.
                    int nominal = (int)Math.Round(hz);
                    if (!raw.Contains(nominal))
                    {
                        if (raw.Contains((int)Math.Floor(hz))) nominal = (int)Math.Floor(hz);
                        else if (raw.Contains((int)Math.Ceiling(hz))) nominal = (int)Math.Ceiling(hz);
                    }
                    result.Add(new RefreshMode { Nominal = nominal, Hz = hz });
                }
                // Whole-number modes DXGI did not list and that are not just a rounded copy of a real one.
                foreach (var n in raw)
                    if (!exact.Any(h => Math.Abs(h - n) < 1.5))
                        result.Add(new RefreshMode { Nominal = n, Hz = n });

                // Use the exact signal timing for the mode that is active right now.
                var active = GetCurrentPreciseRefreshRate(deviceName);
                var current = result.OrderBy(m => Math.Abs(m.Hz - active)).FirstOrDefault();
                if (current != null && active > 0 && Math.Abs(current.Hz - active) < 0.5) current.Hz = active;

                result = result.OrderBy(m => m.Hz).ToList();
            }
            catch { result.Clear(); }
            return result;
        }

        private static List<int> GetRawSupportedRefreshRates(string deviceName)
        {
            var rates = new List<int>();

            var current = new DEVMODE();
            current.dmSize = (short)Marshal.SizeOf(current);
            if (!EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref current))
            {
                logger.Error("RefreshRateManager: EnumDisplaySettings (current mode) failed.");
                return rates;
            }

            var dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(dm);

            int modeNum = 0;
            while (EnumDisplaySettings(deviceName, modeNum, ref dm))
            {
                if (dm.dmPelsWidth == current.dmPelsWidth &&
                    dm.dmPelsHeight == current.dmPelsHeight &&
                    dm.dmBitsPerPel == current.dmBitsPerPel &&
                    dm.dmDisplayFrequency > 1 &&
                    !rates.Contains(dm.dmDisplayFrequency))
                {
                    rates.Add(dm.dmDisplayFrequency);
                }

                modeNum++;
            }

            rates.Sort();
            return rates;
        }

        /// <summary>
        /// Finds the best display refresh rate that the FPS cap divides evenly into, so every
        /// rendered frame gets the same number of display refreshes (no judder on non-VRR
        /// displays). Tries 2x the cap first, then 3x, 4x, ... up to <paramref name="maxMultiplier"/>,
        /// and finally falls back to a 1x match (refresh rate == cap). Returns 0 if the display
        /// doesn't support any matching mode.
        /// </summary>
        public static int FindMatchingRefreshRate(int fps, int maxMultiplier = 4)
        {
            if (fps <= 0)
            {
                return 0;
            }

            var supported = GetSupportedRefreshRates();
            if (supported.Count == 0)
            {
                return 0;
            }

            for (int multiplier = 2; multiplier <= maxMultiplier; multiplier++)
            {
                int candidate = fps * multiplier;
                if (supported.Contains(candidate))
                {
                    return candidate;
                }
            }

            return supported.Contains(fps) ? fps : 0;
        }
        /// <summary>
        /// Display refresh mode (nominal Hz, at the current resolution) whose whole fractions
        /// (Hz, Hz/2, Hz/3 ...) include the FPS cap. The active mode is kept when it already fits;
        /// otherwise the highest other mode that fits is chosen. Returns 0 when no mode fits, so
        /// caps such as 45 FPS on a 78/72/60 Hz panel never switch the display.
        /// </summary>
        public static int FindModeForCap(string deviceName, double cap, int currentHz)
        {
            if (cap <= 0 || double.IsNaN(cap) || double.IsInfinity(cap)) return 0;
            var modes = GetRefreshModes(deviceName, 0, 0);
            if (modes.Count == 0) return 0;

            Func<double, bool> fits = hz =>
            {
                if (hz <= 0) return false;
                for (int d = 1; d <= 16; d++)
                {
                    var expected = hz / d;
                    if (expected < 10) break;
                    if (Math.Abs(cap - expected) <= expected * 0.006) return true;
                }
                return false;
            };

            // Stay in the active mode when the cap already fits it.
            var active = modes.FirstOrDefault(m => m.Nominal == currentHz);
            if (active != null && fits(active.Hz)) return active.Nominal;
            // Otherwise the highest other mode the cap fits.
            for (int i = modes.Count - 1; i >= 0; i--)
                if (fits(modes[i].Hz)) return modes[i].Nominal;
            return 0;
        }

        /// <summary>
        /// Blur Busters' recommended safe FPS cap for VRR (G-Sync/FreeSync) displays: comfortably
        /// under the panel's max refresh rate so frame delivery never outruns the VRR window, which
        /// is what causes stutter/frame-time spikes right at the refresh-rate ceiling.
        /// Formula: Refresh - (Refresh^2 / 3600).
        /// </summary>
        public static double GetVrrSafeCap(double refreshHz)
        {
            if (refreshHz <= 0)
            {
                return 0;
            }

            return refreshHz - (refreshHz * refreshHz) / 3600.0;
        }
    }

    /// <summary>One real display mode: the value Windows can apply (Nominal) and its true refresh (Hz).</summary>
    public sealed class RefreshMode
    {
        public int Nominal { get; set; }
        public double Hz { get; set; }
    }

    /// <summary>
    /// Reads the monitor's real modes with their exact refresh (59.968, 71.93 ...) from DXGI.
    /// GDI only knows rounded numbers and also lists "rounded" copies of real modes (71 and 72 for 71.93).
    /// </summary>
    internal static class DxgiModeReader
    {
        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OutputDesc
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public int Left, Top, Right, Bottom;
            public int AttachedToDesktop;
            public int Rotation;
            public IntPtr Monitor;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumFn(IntPtr self, uint index, out IntPtr obj);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDescFn(IntPtr self, out OutputDesc desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetModesFn(IntPtr self, uint format, uint flags, ref uint count, IntPtr modes);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleaseFn(IntPtr self);

        private static T Method<T>(IntPtr obj, int slot) where T : class
        {
            var vtable = Marshal.ReadIntPtr(obj);
            var fn = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
        }

        private static void Release(IntPtr obj)
        {
            if (obj == IntPtr.Zero) return;
            try { Method<ReleaseFn>(obj, 2)(obj); } catch { }
        }

        /// <summary>Exact refresh rates of the monitor at one resolution (empty if DXGI is unavailable).</summary>
        public static List<double> GetRates(string gdiDeviceName, int width, int height)
        {
            var rates = new List<double>();
            if (string.IsNullOrWhiteSpace(gdiDeviceName)) return rates;
            IntPtr factory = IntPtr.Zero;
            try
            {
                var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
                if (CreateDXGIFactory1(ref iid, out factory) != 0 || factory == IntPtr.Zero) return rates;
                var enumAdapters = Method<EnumFn>(factory, 7);
                for (uint a = 0; a < 16; a++)
                {
                    IntPtr adapter;
                    if (enumAdapters(factory, a, out adapter) != 0) break;
                    try
                    {
                        var enumOutputs = Method<EnumFn>(adapter, 7);
                        for (uint o = 0; o < 16; o++)
                        {
                            IntPtr output;
                            if (enumOutputs(adapter, o, out output) != 0) break;
                            try
                            {
                                OutputDesc desc;
                                if (Method<GetDescFn>(output, 7)(output, out desc) != 0) continue;
                                if (!string.Equals(desc.DeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase)) continue;
                                ReadModes(output, width, height, rates);
                            }
                            finally { Release(output); }
                        }
                    }
                    finally { Release(adapter); }
                }
            }
            catch { rates.Clear(); }
            finally { Release(factory); }
            rates.Sort();
            return rates;
        }

        private static void ReadModes(IntPtr output, int width, int height, List<double> rates)
        {
            var getModes = Method<GetModesFn>(output, 8);
            foreach (uint format in new uint[] { 28 /* R8G8B8A8 */, 87 /* B8G8R8A8 */ })
            {
                uint count = 0;
                if (getModes(output, format, 0, ref count, IntPtr.Zero) != 0 || count == 0 || count > 4096) continue;
                IntPtr buffer = Marshal.AllocHGlobal((int)count * 28);
                try
                {
                    if (getModes(output, format, 0, ref count, buffer) != 0) continue;
                    for (int i = 0; i < count; i++)
                    {
                        int p = i * 28;
                        int w = Marshal.ReadInt32(buffer, p);
                        int h = Marshal.ReadInt32(buffer, p + 4);
                        uint num = (uint)Marshal.ReadInt32(buffer, p + 8);
                        uint den = (uint)Marshal.ReadInt32(buffer, p + 12);
                        if (w != width || h != height || den == 0) continue;
                        double hz = (double)num / den;
                        if (hz < 10 || hz > 1000) continue;
                        if (!rates.Any(r => Math.Abs(r - hz) < 0.001)) rates.Add(hz);
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
    }
}
