using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FPSLimiter
{
    /// <summary>
    /// Local ("wrapper") Special K injection: copies SpecialK64.dll / SpecialK32.dll from the global
    /// install into the game's exe folder under the name of the graphics DLL it should stand in for
    /// (dxgi.dll, d3d9.dll, opengl32.dll ...). The wrapper keeps its settings next to itself in the game
    /// folder as &lt;DLL-name&gt;.ini (e.g. dxgi.ini); the extension writes the FPS cap and its other
    /// managed keys into that file. No SpecialK.central marker is created (it would move the ini away).
    /// Nothing that is not Special K is ever overwritten.
    /// </summary>
    internal static class SpecialKLocalWrapper
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        public const string CentralMarkerName = "SpecialK.central";

        internal sealed class Result
        {
            /// <summary>A Special K wrapper is in place and will load with the game.</summary>
            public bool Ready;
            /// <summary>This call copied the DLL (so the extension owns it and may remove it later).</summary>
            public bool Copied;
            public bool CreatedMarker;
            public string DllPath;
            /// <summary>The wrapper's ini in the game folder (may not exist yet).</summary>
            public string IniPath;
            public string Message;
        }

        private static ApiKind DetectApi(string exePath)
        {
            ApiKind api = ApiKind.Unknown;
            try
            {
                api = GraphicsApiDetector.DetectLinkedApi(exePath);
                if (api == ApiKind.Unknown)
                {
                    var hint = GraphicsApiDetector.DetectFromSiblings(Path.GetDirectoryName(exePath));
                    if (hint != null) api = hint.Api;
                }
            }
            catch (Exception e)
            {
                logger.Debug(e, "Graphics API detection for the Special K wrapper failed.");
            }
            return api;
        }

        /// <summary>Wrapper names to try, best first, for the API the exe links against.</summary>
        private static List<string> PrimaryWrapperNames(ApiKind api)
        {
            switch (api)
            {
                case ApiKind.D3D9:
                    return new List<string> { "d3d9.dll" };
                case ApiKind.OpenGL:
                    return new List<string> { "opengl32.dll" };
                case ApiKind.D3D11:
                    return new List<string> { "dxgi.dll", "d3d11.dll" };
                default:
                    // D3D10 / D3D12 / unknown: Special K's recommended default.
                    return new List<string> { "dxgi.dll" };
            }
        }

        /// <summary>
        /// Other proxy names Special K can stand in for, used only when every primary name is taken by another mod
        /// (OptiScaler, ReShade, DXVK ...). A proxy only loads when the exe actually asks Windows for that DLL, so a
        /// name is only offered when the exe mentions it.
        /// </summary>
        private static List<string> FallbackWrapperNames(string exePath, ApiKind api, ICollection<string> alreadyTried)
        {
            var candidates = new List<string>();
            if (api != ApiKind.D3D9 && api != ApiKind.OpenGL && api != ApiKind.D3D11) candidates.Add("d3d11.dll");
            candidates.Add("dinput8.dll");
            candidates.RemoveAll(c => alreadyTried.Contains(c, StringComparer.OrdinalIgnoreCase));
            if (candidates.Count == 0) return candidates;

            var mentioned = ModDllIdentity.FindReferencedDlls(exePath, candidates);
            return candidates.Where(c => mentioned.Contains(c)).ToList();
        }

        public static Result Install(string exePath, string globalBinaryDirectory)
        {
            var result = new Result();
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                result.Message = "the game's executable could not be found in its install folder (is the game installed, and does Playnite know its install directory?)";
                return result;
            }

            int bits = GetExeBitness(exePath);
            if (bits == 0)
            {
                result.Message = "the game's executable is not a 32-bit or 64-bit Windows program";
                return result;
            }

            string source = string.IsNullOrWhiteSpace(globalBinaryDirectory)
                ? null
                : Path.Combine(globalBinaryDirectory, bits == 64 ? "SpecialK64.dll" : "SpecialK32.dll");
            if (source == null || !File.Exists(source))
            {
                result.Message = "the global Special K install has no " + (bits == 64 ? "SpecialK64.dll" : "SpecialK32.dll") +
                                 " to copy (install Special K first)";
                return result;
            }

            string dir = Path.GetDirectoryName(exePath);
            string target = null;
            bool alreadyPresent = false;
            string blockedName = null;
            string blockedOwner = null;

            var api = DetectApi(exePath);
            var names = PrimaryWrapperNames(api);
            for (int pass = 0; pass < 2 && target == null; pass++)
            {
                if (pass == 1)
                {
                    // Every usual name is taken by another mod: try other proxy names the exe really loads.
                    var extra = FallbackWrapperNames(exePath, api, names);
                    if (extra.Count == 0) break;
                    names = extra;
                }

                foreach (var name in names)
                {
                    string candidate = Path.Combine(dir, name);
                    if (!File.Exists(candidate)) { target = candidate; break; }

                    var owner = ModDllIdentity.Describe(candidate);
                    if (owner == ModDllIdentity.OwnerSpecialK) { target = candidate; alreadyPresent = true; break; }
                    if (blockedName == null) { blockedName = name; blockedOwner = owner; }
                }
            }

            if (target == null)
            {
                result.Message = blockedName + " in the game folder belongs to " + blockedOwner +
                                 " and was left untouched. " + ModDllIdentity.HintFor(blockedOwner);
                return result;
            }
            if (blockedName != null)
            {
                logger.Info($"Special K wrapper: {blockedName} is used by {blockedOwner}; using {Path.GetFileName(target)} instead.");
            }

            if (!alreadyPresent)
            {
                string temp = target + ".fpslimiter-new-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.Copy(source, temp, false);
                    File.Move(temp, target);
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
                result.Copied = true;
                logger.Info("Installed local Special K wrapper: " + target);
            }

            result.DllPath = target;
            result.IniPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(target) + ".ini");

            // A leftover central marker from an older version would send the ini to Profiles\<game>
            // instead of the game folder, so a marker this extension created is removed by the caller.

            result.Ready = true;
            return result;
        }

        /// <summary>Removes a wrapper this extension installed. False when it could not (e.g. in use); nothing else is touched.</summary>
        public static bool Remove(string dllPath, bool removeMarker)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(dllPath) && File.Exists(dllPath))
                {
                    // Only ever delete something that is still Special K.
                    if (SpecialKUpdateService.IsSpecialKDll(dllPath))
                    {
                        File.Delete(dllPath);
                        logger.Info("Removed local Special K wrapper: " + dllPath);
                    }
                }

                if (removeMarker && !string.IsNullOrWhiteSpace(dllPath))
                {
                    string marker = Path.Combine(Path.GetDirectoryName(dllPath) ?? string.Empty, CentralMarkerName);
                    if (File.Exists(marker)) File.Delete(marker);
                }
                return true;
            }
            catch (Exception e)
            {
                logger.Warn("Could not remove the local Special K wrapper " + dllPath + ": " + e.Message);
                return false;
            }
        }

        /// <summary>32, 64, or 0 when the file is not an x86 / x64 PE image.</summary>
        private static int GetExeBitness(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var br = new BinaryReader(fs))
                {
                    if (fs.Length < 0x40 || br.ReadUInt16() != 0x5A4D) return 0;
                    fs.Position = 0x3C;
                    int pe = br.ReadInt32();
                    if (pe <= 0 || pe > fs.Length - 6) return 0;
                    fs.Position = pe;
                    if (br.ReadUInt32() != 0x00004550) return 0;
                    ushort machine = br.ReadUInt16();
                    return machine == 0x8664 ? 64 : machine == 0x014C ? 32 : 0;
                }
            }
            catch { return 0; }
        }
    }

    /// <summary>
    /// Tells Special K's DLLs apart from the other proxy DLLs that use the same file names (OptiScaler, ReShade,
    /// DXVK ...), judged by what is inside the file, not by its name. Same idea as the DXVK Injector's DllIdentity.
    /// </summary>
    internal static class ModDllIdentity
    {
        public const string OwnerSpecialK = "Special K";
        public const string OwnerOptiScaler = "OptiScaler";
        public const string OwnerReShade = "ReShade";
        public const string OwnerDxvk = "DXVK";
        public const string OwnerUnknown = "another mod";

        public static string Describe(string path)
        {
            // Special K first: its DLL also contains the words "ReShade" and "DXVK" (compatibility code).
            if (HasSpecialKMetadata(path)) return OwnerSpecialK;
            // OptiScaler before ReShade: it mentions ReShade too.
            if (ContainsText(path, "OptiScaler")) return OwnerOptiScaler;
            if (ContainsText(path, "ReShade")) return OwnerReShade;
            try { if (SpecialKUpdateService.IsSpecialKDll(path)) return OwnerSpecialK; } catch { }
            if (ContainsText(path, "DXVK")) return OwnerDxvk;
            return OwnerUnknown;
        }

        public static string HintFor(string owner)
        {
            switch (owner)
            {
                case OwnerOptiScaler:
                    return "Give OptiScaler a different proxy DLL for this game (for example winmm.dll) in the OptiScaler Injector settings, inject OptiScaler again, then switch Special K to Local again.";
                case OwnerReShade:
                    return "Remove ReShade's proxy DLL for this game (ReShade Manager > Remove, or run ReShade as a Vulkan layer), then switch Special K to Local again. Special K Global injection also works next to ReShade.";
                case OwnerDxvk:
                    return "DXVK needs that file name. Use Global injection for this game instead (Special K then loads on top of DXVK), or remove DXVK first.";
                default:
                    return "Remove or rename that file first, or use Global injection for this game.";
            }
        }

        private static bool HasSpecialKMetadata(string path)
        {
            try
            {
                var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                var meta = (vi.ProductName + "|" + vi.FileDescription + "|" + vi.InternalName + "|" + vi.OriginalFilename).Replace(" ", string.Empty);
                return meta.IndexOf("SpecialK", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        /// <summary>The names (lower-case) from <paramref name="names"/> that the exe mentions, i.e. imports or loads.</summary>
        public static HashSet<string> FindReferencedDlls(string exePath, IEnumerable<string> names)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var needles = names.Select(n => new KeyValuePair<string, byte[]>(n, Encoding.ASCII.GetBytes(n.ToLowerInvariant()))).ToList();
                int maxLen = needles.Max(n => n.Value.Length);
                const int chunk = 4 << 20;
                var buffer = new byte[chunk + maxLen];
                int carry = 0;
                using (var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int read;
                    while ((read = fs.Read(buffer, carry, chunk)) > 0)
                    {
                        int total = carry + read;
                        foreach (var n in needles)
                            if (!found.Contains(n.Key) && Find(buffer, total, n.Value)) found.Add(n.Key);
                        if (found.Count == needles.Count) break;
                        carry = Math.Min(maxLen - 1, total);
                        Buffer.BlockCopy(buffer, total - carry, buffer, 0, carry);
                    }
                }
            }
            catch (Exception e)
            {
                LogManager.GetLogger().Debug(e, "Could not scan the exe for the DLLs it loads.");
            }
            return found;
        }

        private static bool ContainsText(string path, string text)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                var lower = text.ToLowerInvariant();
                var ascii = Encoding.ASCII.GetBytes(lower);
                var wide = Encoding.Unicode.GetBytes(lower);
                const int chunk = 1 << 20;
                var buffer = new byte[chunk + wide.Length];
                int carry = 0;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int read;
                    while ((read = stream.Read(buffer, carry, chunk)) > 0)
                    {
                        int total = carry + read;
                        if (Find(buffer, total, ascii) || Find(buffer, total, wide)) return true;
                        carry = Math.Min(wide.Length - 1, total);
                        Buffer.BlockCopy(buffer, total - carry, buffer, 0, carry);
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool Find(byte[] data, int length, byte[] needle)
        {
            int last = length - needle.Length;
            for (int i = 0; i <= last; i++)
            {
                int j = 0;
                for (; j < needle.Length; j++)
                {
                    byte b = data[i + j];
                    if (b >= (byte)'A' && b <= (byte)'Z') b = (byte)(b + 32);
                    if (b != needle[j]) break;
                }
                if (j == needle.Length) return true;
            }
            return false;
        }
    }
}
