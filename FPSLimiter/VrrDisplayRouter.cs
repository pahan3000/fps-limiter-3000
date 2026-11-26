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
}
