using System;
using System.Runtime.InteropServices;

namespace FPSLimiter
{
    /// <summary>
    /// Reads the active monitor's actual fractional signal refresh (e.g. 29.9001 Hz).
    /// EnumDisplaySettings.dmDisplayFrequency is an INTEGER/NOMINAL mode label and cannot
    /// provide this precision. Windows CCD exposes the active vSync numerator/denominator.
    /// Read-only; changing modes continues to use the legacy nominal Win32 API.
    /// </summary>
    internal static class PreciseRefreshRateReader
    {
        private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
        private const int ERROR_SUCCESS = 0;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;
        private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        private const uint DISPLAYCONFIG_MODE_INFO_TYPE_TARGET = 2;
        private const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xffffffff;

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rational
        {
            public uint Numerator, Denominator;
            public double ToHz() { return Denominator > 0 ? (double)Numerator / Denominator : 0; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Region { public uint Width, Height; }

        [StructLayout(LayoutKind.Sequential)]
        private struct VideoSignalInfo
        {
            public ulong PixelRate;
            public Rational HSyncFreq;
            public Rational VSyncFreq;
            public Region ActiveSize;
            public Region TotalSize;
            public uint VideoStandard;
            public uint ScanLineOrdering;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PathSourceInfo
        {
            public LUID AdapterId;
            public uint Id, ModeInfoIdx, StatusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PathTargetInfo
        {
            public LUID AdapterId;
            public uint Id, ModeInfoIdx, OutputTechnology, Rotation, Scaling;
            public Rational RefreshRate;
            public uint ScanLineOrdering;
            [MarshalAs(UnmanagedType.Bool)] public bool TargetAvailable;
            public uint StatusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PathInfo
        {
            public PathSourceInfo Source;
            public PathTargetInfo Target;
            public uint Flags;
        }

        // The native union contains a 48-byte target mode and smaller source/desktop modes.
        // Explicit Size keeps the interop stride correct in both 32- and 64-bit builds.
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        private struct ModeInfo
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(4)] public uint Id;
            [FieldOffset(8)] public LUID AdapterId;
            [FieldOffset(16)] public VideoSignalInfo Signal;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceInfoHeader
        {
            public uint Type, Size;
            public LUID AdapterId;
            public uint Id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SourceDeviceName
        {
            public DeviceInfoHeader Header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string GdiDeviceName;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint pathCount,
            [Out] PathInfo[] paths, ref uint modeCount, [Out] ModeInfo[] modes, IntPtr topologyId);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        private static extern int GetSourceDeviceName(ref SourceDeviceName request);

        /// <summary>0 means unavailable; never substitutes an unrelated monitor's rate.</summary>
        public static double GetActiveRefreshRate(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName)) return 0;
            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    uint pathCount, modeCount;
                    if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount) != ERROR_SUCCESS)
                        return 0;
                    if (pathCount > 64 || modeCount > 1024) return 0;
                    var paths = new PathInfo[(int)pathCount];
                    var modes = new ModeInfo[(int)modeCount];
                    var status = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths,
                        ref modeCount, modes, IntPtr.Zero);
                    if (status == ERROR_INSUFFICIENT_BUFFER) continue;
                    if (status != ERROR_SUCCESS) return 0;

                    for (int i = 0; i < (int)pathCount; i++)
                    {
                        var path = paths[i];
                        var request = new SourceDeviceName
                        {
                            Header = new DeviceInfoHeader
                            {
                                Type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                                Size = (uint)Marshal.SizeOf(typeof(SourceDeviceName)),
                                AdapterId = path.Source.AdapterId,
                                Id = path.Source.Id
                            }
                        };
                        if (GetSourceDeviceName(ref request) != ERROR_SUCCESS ||
                            !string.Equals(request.GdiDeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Physical target signal timing, not the rounded GDI mode label.
                        if (path.Target.ModeInfoIdx != DISPLAYCONFIG_PATH_MODE_IDX_INVALID &&
                            path.Target.ModeInfoIdx < modeCount)
                        {
                            var mode = modes[(int)path.Target.ModeInfoIdx];
                            if (mode.Type == DISPLAYCONFIG_MODE_INFO_TYPE_TARGET)
                            {
                                var hz = mode.Signal.VSyncFreq.ToHz();
                                if (hz >= 10 && hz <= 1000) return hz;
                            }
                        }

                        // If the target mode isn't present, the path's rational is still useful.
                        var fallback = path.Target.RefreshRate.ToHz();
                        return fallback >= 10 && fallback <= 1000 ? fallback : 0;
                    }
                    return 0;
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (Exception) { } // Device hotplug / unsupported driver: caller uses legacy fallback.
            return 0;
        }
    }
}
