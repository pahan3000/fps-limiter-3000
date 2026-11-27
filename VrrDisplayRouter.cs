using System;
using System.Runtime.InteropServices;

namespace FPSLimiter
{
    internal enum VrrDisplayVendor { Unknown, Amd, Nvidia, Intel }

    // Windows display-adapter association, NOT the GPU used to render the game.
    // EnumDisplayDevices receives the \.\DISPLAYn path obtained from the game window.
    internal static class VrrDisplayRouter
    {
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
        private static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice display, uint flags);

        public static VrrDisplayVendor ForMonitor(string monitorDeviceName)
        {
            if (string.IsNullOrWhiteSpace(monitorDeviceName)) return VrrDisplayVendor.Unknown;
            var device = new DisplayDevice { cb = Marshal.SizeOf(typeof(DisplayDevice)) };
            if (!EnumDisplayDevices(monitorDeviceName, 0, ref device, 0)) return VrrDisplayVendor.Unknown;
            var identity = (device.DeviceID ?? "") + " " + (device.DeviceString ?? "");
            if (identity.IndexOf("VEN_1002", StringComparison.OrdinalIgnoreCase) >= 0 ||
                identity.IndexOf("VEN_1022", StringComparison.OrdinalIgnoreCase) >= 0 ||
                identity.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                identity.IndexOf("Radeon", StringComparison.OrdinalIgnoreCase) >= 0)
                return VrrDisplayVendor.Amd;
            if (identity.IndexOf("VEN_10DE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                identity.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                identity.IndexOf("GeForce", StringComparison.OrdinalIgnoreCase) >= 0)
                return VrrDisplayVendor.Nvidia;
            if (identity.IndexOf("VEN_8086", StringComparison.OrdinalIgnoreCase) >= 0 ||
                identity.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0)
                return VrrDisplayVendor.Intel;
            return VrrDisplayVendor.Unknown;
        }
    }

    /// <summary>
    /// Which GPU vendors are physically installed in this machine (not which one drives a given display).
    /// Used to decide whether AMD-only options such as the FreeSync toggle make sense at all:
    /// NVIDIA-only / Intel-only PCs hide them, a laptop with an AMD and an NVIDIA GPU keeps them.
    /// </summary>
    internal static class GpuInventory
    {
        private static bool? hasAmdGpu;
        private static readonly object gate = new object();

        /// <summary>True when at least one AMD/ATI GPU is present. Cached after the first call.</summary>
        public static bool HasAmdGpu
        {
            get
            {
                lock (gate)
                {
                    if (!hasAmdGpu.HasValue) hasAmdGpu = Detect();
                    return hasAmdGpu.Value;
                }
            }
        }

        private static bool Detect()
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT Name, PNPDeviceID, AdapterCompatibility FROM Win32_VideoController"))
                using (var results = searcher.Get())
                {
                    foreach (System.Management.ManagementBaseObject gpu in results)
                    {
                        var identity = string.Join(" ",
                            Convert.ToString(gpu["PNPDeviceID"]),
                            Convert.ToString(gpu["Name"]),
                            Convert.ToString(gpu["AdapterCompatibility"]));
                        if (IsAmdGpu(identity)) return true;
                    }
                }
                return false;
            }
            catch
            {
                // Could not enumerate adapters: don't hide the option on a guess. The FreeSync helper
                // still has to report a usable state before anything is shown.
                return true;
            }
        }

        // VEN_1002 = AMD/ATI GPUs. (VEN_1022 is AMD's chipset/CPU vendor id, not a GPU, so it is not matched.)
        private static bool IsAmdGpu(string identity)
        {
            if (string.IsNullOrWhiteSpace(identity)) return false;
            return identity.IndexOf("VEN_1002", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   identity.IndexOf("Radeon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   identity.IndexOf("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   identity.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
