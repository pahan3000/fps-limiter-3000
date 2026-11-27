using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace FPSLimiter
{
    /// <summary>Graphics APIs the detection can tell apart.</summary>
    internal enum ApiKind
    {
        Unknown,
        D3D9,
        D3D10,
        D3D11,
        D3D12,
        OpenGL,
        Vulkan
    }

    /// <summary>One detection result: the API and a short, human-readable reason for it.</summary>
    internal sealed class ApiHint
    {
        public ApiKind Api { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// Extra graphics-API evidence on top of the plain import-table check.
    /// It exists for games the import table cannot classify: ports that load Vulkan at runtime (volk, SDL, shadPS4 style),
    /// engines whose renderer lives in a DLL next to a thin exe, and games told which API to use on the command line.
    ///
    /// Self-contained on purpose (it only needs PeImportReader, ApiKind and the namespace's enums), so the same
    /// file can be dropped into another extension. Every method is safe to call on the UI thread's behalf from a worker:
    /// results are cached per file (path, size, timestamp), and the string scan only reads data sections, never code or resources.
    /// </summary>
    internal static class GraphicsApiDetector
    {
        // ---- 1. Launch arguments ---------------------------------------------------------------

        private static readonly HashSet<string> ApiKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "renderer", "gfx", "graphicsapi", "graphics-api", "api", "rhi", "video", "gpuapi", "backend",
            "driver", "graphics", "renderapi", "renderingapi", "rendering-driver"
        };

        private static ApiKind ApiFromWord(string word)
        {
            switch ((word ?? string.Empty).Trim().Trim('"').ToLowerInvariant())
            {
                case "vulkan":
                case "vk": return ApiKind.Vulkan;
                case "dx12":
                case "d3d12":
                case "directx12": return ApiKind.D3D12;
                case "dx11":
                case "d3d11":
                case "directx11": return ApiKind.D3D11;
                case "dx10":
                case "d3d10":
                case "directx10": return ApiKind.D3D10;
                case "dx9":
                case "d3d9":
                case "directx9": return ApiKind.D3D9;
                case "opengl":
                case "gl":
                case "glcore":
                case "opengles": return ApiKind.OpenGL;
                default: return ApiKind.Unknown;
            }
        }

        /// <summary>
        /// Reads the API from launch arguments such as -vulkan, -dx12, -force-d3d11, -force-glcore, --renderer=vulkan.
        /// Null when the arguments do not name one.
        /// </summary>
        public static ApiHint FromLaunchArguments(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments)) return null;

            foreach (Match m in Regex.Matches(arguments, "\"[^\"]*\"|\\S+"))
            {
                var token = m.Value.Trim().Trim('"').Trim();
                if (token.Length < 2) continue;

                var body = token.TrimStart('-', '/');
                if (body.Length == token.Length) continue; // not an option
                if (body.StartsWith("force-", StringComparison.OrdinalIgnoreCase) ||
                    body.StartsWith("force_", StringComparison.OrdinalIgnoreCase))
                    body = body.Substring(6);

                ApiKind api;
                var sep = body.IndexOfAny(new[] { '=', ':' });
                if (sep > 0)
                {
                    if (!ApiKeys.Contains(body.Substring(0, sep))) continue;
                    api = ApiFromWord(body.Substring(sep + 1));
                }
                else
                {
                    api = ApiFromWord(body);
                }

                if (api != ApiKind.Unknown)
                    return new ApiHint { Api = api, Reason = "launch argument " + token };
            }
            return null;
        }

        // ---- 2. Vulkan in the exe ----------------------------------------------------------------

        private const long ExeScanBudget = 160L * 1024 * 1024;
        private const long DllScanBudget = 48L * 1024 * 1024;
        private const int MaxNeedleLength = 64;

        // Needle tags: 0 = the loader's file name ("vulkan-1.dll"), 1.. = Vulkan entry points a real renderer uses.
        // Entry points every SDL / loader stub carries (vkGetInstanceProcAddr, vkCreateInstance) are left out on purpose:
        // statically linked SDL would otherwise turn every OpenGL/D3D game into a "Vulkan game".
        private static readonly string[] HeavyFunctions =
        {
            "vkCreateDevice", "vkQueueSubmit", "vkCmdDraw", "vkCreateSwapchainKHR",
            "vkCreateGraphicsPipelines", "VK_KHR_swapchain"
        };

        private static readonly byte[][] NeedleBytes;
        private static readonly int[] NeedleTag;
        private static readonly List<int>[] NeedlesByFirstByte = new List<int>[256];
        private static readonly int TagCount = HeavyFunctions.Length + 1;

        private sealed class VkScan
        {
            public bool LoaderString;
            public int HeavyHits;
            public bool Strong => HeavyHits >= 3 || (LoaderString && HeavyHits >= 2);
        }

        private static readonly Dictionary<string, VkScan> ScanCache = new Dictionary<string, VkScan>();
        private static readonly object CacheLock = new object();

        static GraphicsApiDetector()
        {
            var bytes = new List<byte[]>();
            var tags = new List<int>();

            bytes.Add(Encoding.ASCII.GetBytes("vulkan-1.dll")); tags.Add(0);
            bytes.Add(Encoding.Unicode.GetBytes("vulkan-1.dll")); tags.Add(0);
            for (int i = 0; i < HeavyFunctions.Length; i++)
            {
                bytes.Add(Encoding.ASCII.GetBytes(HeavyFunctions[i]));
                tags.Add(i + 1);
            }

            NeedleBytes = bytes.ToArray();
            NeedleTag = tags.ToArray();
            for (int i = 0; i < NeedleBytes.Length; i++)
            {
                var first = NeedleBytes[i][0];
                if (NeedlesByFirstByte[first] == null) NeedlesByFirstByte[first] = new List<int>();
                NeedlesByFirstByte[first].Add(i);
            }
        }

        private sealed class ImportInfo
        {
            public bool Vulkan;
            public bool OpenGL;
            public bool D3D9, D3D10, D3D11, D3D12;
            public bool AnyD3D => D3D9 || D3D10 || D3D11 || D3D12;
        }

        private static ImportInfo ReadImports(string path)
        {
            var info = new ImportInfo();
            try
            {
                var imports = PeImportReader.ReadImportedDllNames(path);
                var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (imports.Regular != null)
                    foreach (var n in imports.Regular) if (!string.IsNullOrEmpty(n)) all.Add(n);
                if (imports.Delay != null)
                    foreach (var n in imports.Delay) if (!string.IsNullOrEmpty(n)) all.Add(n);

                info.Vulkan = all.Contains("vulkan-1.dll") || all.Contains("vulkan.dll");
                info.OpenGL = all.Contains("opengl32.dll");
                info.D3D12 = all.Contains("d3d12.dll") || all.Contains("d3d12core.dll");
                info.D3D11 = all.Contains("d3d11.dll");
                info.D3D10 = all.Contains("d3d10.dll") || all.Contains("d3d10_1.dll") || all.Contains("d3d10core.dll");
                info.D3D9 = all.Contains("d3d9.dll") || all.Contains("d3d9ex.dll");
            }
            catch { /* packed or protected exe: no import information, the other signals still apply */ }
            return info;
        }

        /// <summary>
        /// The Direct3D / OpenGL API the exe links against (D3D12 > D3D11 > D3D10 > D3D9 > OpenGL),
        /// Unknown when the import table says nothing. Used to pick the local Special K wrapper name.
        /// </summary>
        public static ApiKind DetectLinkedApi(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return ApiKind.Unknown;
            var i = ReadImports(exePath);
            if (i.D3D12) return ApiKind.D3D12;
            if (i.D3D11) return ApiKind.D3D11;
            if (i.D3D10) return ApiKind.D3D10;
            if (i.D3D9) return ApiKind.D3D9;
            if (i.OpenGL) return ApiKind.OpenGL;
            return ApiKind.Unknown;
        }

        private static bool LooksLikeBrowserEngine(string dir)
        {
            // Chromium / CEF / Electron apps carry Vulkan strings (ANGLE, Dawn) without being Vulkan games.
            try
            {
                if (string.IsNullOrEmpty(dir)) return false;
                return File.Exists(Path.Combine(dir, "libcef.dll")) ||
                       File.Exists(Path.Combine(dir, "v8_context_snapshot.bin")) ||
                       File.Exists(Path.Combine(dir, "chrome_100_percent.pak"));
            }
            catch { return false; }
        }

        /// <summary>
        /// Vulkan for games that the import table calls "unknown" or "DirectX 12":
        ///  - the exe imports vulkan-1.dll and no Direct3D, or
        ///  - the exe has no Direct3D import and carries the entry points of a real Vulkan renderer (loaded at runtime).
        /// A game that really links Direct3D (or ships the D3D12 runtime) is left to the normal scoring.
        /// Strings such as "d3d12.dll" are deliberately not counted against Vulkan: SDL, Qt and cross-API engines contain them
        /// for backends they never use, which is what made Vulkan ports look like DirectX 12 games.
        /// </summary>
        public static ApiHint DetectVulkan(string exePath, bool hasD3D12Runtime)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return null;
            if (LooksLikeBrowserEngine(Path.GetDirectoryName(exePath))) return null;

            var imports = ReadImports(exePath);
            if (imports.AnyD3D) return null;
            if (imports.Vulkan)
                return new ApiHint { Api = ApiKind.Vulkan, Reason = "the exe imports vulkan-1.dll and no Direct3D" };
            if (hasD3D12Runtime) return null;

            var scan = ScanForVulkan(exePath, ExeScanBudget);
            if (scan.Strong)
                return new ApiHint { Api = ApiKind.Vulkan, Reason = "the exe loads Vulkan at run time and has no Direct3D import" };

            // The exe says nothing (no Direct3D import, no clear Vulkan strings) but ships Vulkan / SPIR-V libraries beside it.
            if (!imports.OpenGL)
            {
                var lib = FindVulkanLibrary(Path.GetDirectoryName(exePath));
                if (lib != null)
                    return new ApiHint { Api = ApiKind.Vulkan, Reason = "no Direct3D import, and " + lib + " (a Vulkan / SPIR-V library) is next to the exe" };
            }
            return null;
        }

        /// <summary>
        /// File name of a Vulkan-only library in the folder (the loader, a SPIR-V compiler, a software Vulkan driver), or null.
        /// Vendor upscaler DLLs such as amd_fidelityfx_vk.dll are not counted: OptiScaler copies those into every game.
        /// </summary>
        /// <summary>
        /// Wider fallback: when the resolved exe shows nothing, look at the other executables in the
        /// install folder (a few levels deep). Vulkan when one imports vulkan-1.dll and no Direct3D.
        /// </summary>
        public static ApiHint DetectVulkanInTree(string gameDir, string alreadyChecked)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return null;
                var pending = new Queue<Tuple<string, int>>();
                pending.Enqueue(Tuple.Create(gameDir, 0));
                var inspected = 0;
                while (pending.Count > 0 && inspected < 80)
                {
                    var item = pending.Dequeue();
                    IEnumerable<string> exes;
                    try { exes = Directory.EnumerateFiles(item.Item1, "*.exe"); }
                    catch { continue; }
                    foreach (var exe in exes)
                    {
                        if (inspected >= 80) break;
                        if (string.Equals(exe, alreadyChecked, StringComparison.OrdinalIgnoreCase)) continue;
                        if (EmulatorDetection.IsNonGameExecutable(exe)) continue;
                        inspected++;
                        var imports = ReadImports(exe);
                        if (imports.Vulkan && !imports.AnyD3D)
                            return new ApiHint { Api = ApiKind.Vulkan, Reason = Path.GetFileName(exe) + " in the game folder imports vulkan-1.dll and no Direct3D" };
                    }
                    if (item.Item2 < 3)
                    {
                        try
                        {
                            foreach (var sub in Directory.EnumerateDirectories(item.Item1))
                                pending.Enqueue(Tuple.Create(sub, item.Item2 + 1));
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return null;
        }

        private static string FindVulkanLibrary(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
                foreach (var file in Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    var n = Path.GetFileName(file).ToLowerInvariant();
                    if (n == "vulkan-1.dll" || n == "vulkan.dll" || n.StartsWith("glslang", StringComparison.Ordinal) ||
                        n.StartsWith("spirv", StringComparison.Ordinal) || n.StartsWith("shaderc", StringComparison.Ordinal) ||
                        n.StartsWith("libshaderc", StringComparison.Ordinal) || n.StartsWith("vk_swiftshader", StringComparison.Ordinal) ||
                        n.StartsWith("vklayer_", StringComparison.Ordinal) || n.StartsWith("libmoltenvk", StringComparison.Ordinal))
                        return Path.GetFileName(file);
                }
            }
            catch { }
            return null;
        }

        // ---- 3. Engine DLLs and files next to the exe ---------------------------------------------

        private static readonly string[] SkippedDllPrefixes =
        {
            "msvcp", "vcruntime", "api-ms", "ucrtbase", "concrt", "vccorlib", "d3d", "dxgi", "opengl32", "dinput",
            "xinput", "steam", "galaxy", "eossdk", "discord", "oo2core", "bink", "fmod", "wwise", "physx", "nvngx",
            "nvapi", "amd_", "ffx_", "libcurl", "libssl", "libcrypto", "openssl", "zlib", "sdl", "reshade", "dxvk",
            "vkd3d", "optiscaler", "crashpad", "libcef", "cef", "ffmpeg", "avcodec", "avformat", "tbb", "mimalloc",
            "openal", "wrap_oal", "libogg", "libvorbis", "unitycrash", "vulkan"
        };

        private static bool IsSkippedDll(string fileName)
        {
            var n = (fileName ?? string.Empty).ToLowerInvariant();
            return SkippedDllPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal));
        }

        private static bool HasVulkanFileName(string name)
        {
            var n = (name ?? string.Empty).ToLowerInvariant();
            return n.Contains("vulkan") || n.Contains("spirv") || n.Contains("shaderc") || n.Contains("glslang") ||
                   n.Contains("moltenvk") || n.Contains("vk_swiftshader") || n.EndsWith(".spv", StringComparison.Ordinal);
        }

        /// <summary>
        /// For an exe that told nothing (a launcher, or an engine that keeps its renderer in a DLL): looks at the biggest
        /// game DLLs next to it and at Vulkan files in the folder. Returns null when there is nothing to go on.
        /// </summary>
        public static ApiHint DetectFromSiblings(string gameDir)
        {
            try
            {
                if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir)) return null;
                if (LooksLikeBrowserEngine(gameDir)) return null;

                var vulkanImport = false;
                var vulkanStrings = false;
                string vulkanDll = null;
                bool d3d9 = false, d3d10 = false, d3d11 = false, d3d12 = false;
                string d3dDll = null;

                var dlls = new DirectoryInfo(gameDir).EnumerateFiles("*.dll", SearchOption.TopDirectoryOnly)
                    .Where(f => f.Length >= 512 * 1024 && !IsSkippedDll(f.Name))
                    .OrderByDescending(f => f.Length)
                    .Take(6)
                    .ToList();

                long budget = ExeScanBudget;
                foreach (var dll in dlls)
                {
                    var imports = ReadImports(dll.FullName);
                    if (imports.Vulkan) { vulkanImport = true; vulkanDll = vulkanDll ?? dll.Name; }
                    if (imports.AnyD3D)
                    {
                        d3d9 |= imports.D3D9; d3d10 |= imports.D3D10; d3d11 |= imports.D3D11; d3d12 |= imports.D3D12;
                        d3dDll = d3dDll ?? dll.Name;
                    }
                    if (!vulkanStrings && budget > 0)
                    {
                        var scanBudget = Math.Min(DllScanBudget, budget);
                        budget -= scanBudget;
                        if (ScanForVulkan(dll.FullName, scanBudget).Strong)
                        {
                            vulkanStrings = true;
                            vulkanDll = vulkanDll ?? dll.Name;
                        }
                    }
                }

                var anyD3D = d3d9 || d3d10 || d3d11 || d3d12;

                if ((vulkanImport || vulkanStrings) && !anyD3D)
                    return new ApiHint { Api = ApiKind.Vulkan, Reason = "the game DLL " + vulkanDll + " uses Vulkan and no Direct3D" };

                if (!anyD3D && !vulkanImport && !vulkanStrings && HasVulkanFiles(gameDir))
                    return new ApiHint { Api = ApiKind.Vulkan, Reason = "Vulkan / SPIR-V files in the game folder" };

                if (anyD3D && !vulkanImport && !vulkanStrings)
                {
                    var api = d3d12 ? ApiKind.D3D12 : d3d11 ? ApiKind.D3D11 : d3d9 ? ApiKind.D3D9 : ApiKind.D3D10;
                    return new ApiHint { Api = api, Reason = "the game DLL " + d3dDll + " imports it" };
                }
            }
            catch { }
            return null;
        }

        private static bool HasVulkanFiles(string gameDir)
        {
            try
            {
                var seen = 0;
                var queue = new Queue<Tuple<string, int>>();
                queue.Enqueue(Tuple.Create(gameDir, 0));
                while (queue.Count > 0 && seen < 400)
                {
                    var item = queue.Dequeue();
                    foreach (var entry in Directory.EnumerateFileSystemEntries(item.Item1))
                    {
                        if (++seen > 400) break;
                        var name = Path.GetFileName(entry);
                        if (HasVulkanFileName(name)) return true;
                        if (item.Item2 < 2 && Directory.Exists(entry)) queue.Enqueue(Tuple.Create(entry, item.Item2 + 1));
                    }
                }
            }
            catch { }
            return false;
        }

        // ---- String scan over the data sections ---------------------------------------------------

        private static VkScan ScanForVulkan(string path, long budget)
        {
            FileInfo fi;
            try
            {
                fi = new FileInfo(path);
                if (!fi.Exists) return new VkScan();
            }
            catch { return new VkScan(); }

            var key = path.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks + "|" + budget;
            lock (CacheLock)
            {
                VkScan cached;
                if (ScanCache.TryGetValue(key, out cached)) return cached;
            }

            var result = new VkScan();
            try
            {
                var found = new bool[TagCount];
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    foreach (var range in GetScanRanges(fs, fi.Length, budget))
                        if (ScanRange(fs, range[0], range[1], found)) break;
                }

                result.LoaderString = found[0];
                for (int i = 1; i < found.Length; i++)
                    if (found[i]) result.HeavyHits++;
            }
            catch { }

            lock (CacheLock)
            {
                if (ScanCache.Count > 64) ScanCache.Clear();
                ScanCache[key] = result;
            }
            return result;
        }

        /// <summary>Raw-data ranges of the sections that can hold strings: everything except code and resources.</summary>
        private static List<long[]> GetScanRanges(FileStream fs, long fileLength, long budget)
        {
            var ranges = new List<long[]>();
            try
            {
                using (var br = new BinaryReader(fs, Encoding.UTF8, true))
                {
                    fs.Seek(0x3C, SeekOrigin.Begin);
                    var pe = br.ReadInt32();
                    if (pe < 0 || pe > fileLength - 24) throw new InvalidDataException();
                    fs.Seek(pe, SeekOrigin.Begin);
                    if (br.ReadUInt32() != 0x00004550) throw new InvalidDataException();

                    br.ReadUInt16(); // machine
                    var sectionCount = br.ReadUInt16();
                    br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt32();
                    var optionalSize = br.ReadUInt16();
                    br.ReadUInt16();

                    var table = pe + 24L + optionalSize;
                    long total = 0;
                    for (int s = 0; s < sectionCount && s < 96; s++)
                    {
                        fs.Seek(table + 40L * s, SeekOrigin.Begin);
                        var nameBytes = br.ReadBytes(8);
                        br.ReadUInt32(); // virtual size
                        br.ReadUInt32(); // virtual address
                        var rawSize = br.ReadUInt32();
                        var rawPtr = br.ReadUInt32();
                        br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt16(); br.ReadUInt16();
                        var characteristics = br.ReadUInt32();

                        var name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
                        if (rawSize == 0 || rawPtr == 0 || rawPtr >= fileLength) continue;
                        if ((characteristics & 0x20000020u) != 0) continue; // executable / code
                        if (name.Equals(".rsrc", StringComparison.OrdinalIgnoreCase)) continue;

                        var length = Math.Min((long)rawSize, fileLength - rawPtr);
                        if (total + length > budget) length = budget - total;
                        if (length <= 0) break;
                        ranges.Add(new[] { (long)rawPtr, length });
                        total += length;
                    }
                }
            }
            catch
            {
                ranges.Clear();
            }

            if (ranges.Count == 0) // not a readable PE: look at the start of the file
                ranges.Add(new[] { 0L, Math.Min(fileLength, Math.Min(budget, 16L * 1024 * 1024)) });
            return ranges;
        }

        private static bool ScanDone(bool[] found)
        {
            var heavy = 0;
            for (int i = 1; i < found.Length; i++) if (found[i]) heavy++;
            return heavy >= 3 && found[0];
        }

        /// <summary>Scans one range for the needles. True when everything that can be learned has been found.</summary>
        private static bool ScanRange(FileStream fs, long start, long length, bool[] found)
        {
            const int chunk = 4 * 1024 * 1024;
            var buffer = new byte[chunk + MaxNeedleLength];
            var pos = start;
            var end = start + length;

            while (pos < end)
            {
                fs.Seek(pos, SeekOrigin.Begin);
                var want = (int)Math.Min((long)buffer.Length, end - pos);
                var got = 0;
                while (got < want)
                {
                    var n = fs.Read(buffer, got, want - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got <= 0) break;

                for (int i = 0; i < got; i++)
                {
                    var candidates = NeedlesByFirstByte[buffer[i]];
                    if (candidates == null) continue;
                    for (int k = 0; k < candidates.Count; k++)
                    {
                        var needle = NeedleBytes[candidates[k]];
                        if (i + needle.Length > got) continue;
                        int j = 1;
                        for (; j < needle.Length; j++)
                            if (buffer[i + j] != needle[j]) break;
                        if (j == needle.Length) found[NeedleTag[candidates[k]]] = true;
                    }
                }

                if (ScanDone(found)) return true;
                pos += chunk; // the 64 spare bytes of the buffer overlap the next chunk, so no needle is cut in two
            }
            return false;
        }
    }

    /// <summary>
    /// Minimal PE import-table reader (same approach as the DXVK plugin).
    /// </summary>
    internal static class PeImportReader
    {
        public struct ImportedDllNames
        {
            public List<string> Regular;
            public List<string> Delay;
        }

        private struct SectionInfo
        {
            public uint VirtualAddress;
            public uint VirtualSize;
            public uint PointerToRawData;
        }

        public static ImportedDllNames ReadImportedDllNames(string exePath)
        {
            using (var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(fs))
            {
                fs.Seek(0x3C, SeekOrigin.Begin);
                var peHeaderOffset = reader.ReadInt32();

                fs.Seek(peHeaderOffset, SeekOrigin.Begin);
                if (reader.ReadUInt32() != 0x00004550)
                    throw new InvalidDataException("Not a valid PE file.");

                reader.ReadUInt16(); // Machine
                var numberOfSections = reader.ReadUInt16();
                reader.ReadUInt32();
                reader.ReadUInt32();
                reader.ReadUInt32();
                var sizeOfOptionalHeader = reader.ReadUInt16();
                reader.ReadUInt16();

                var optionalHeaderStart = fs.Position;
                var magic = reader.ReadUInt16();
                bool isPe32Plus = magic == 0x20B;

                var dataDirectoryOffset = isPe32Plus ? 112 : 96;

                fs.Seek(optionalHeaderStart + dataDirectoryOffset + 1 * 8, SeekOrigin.Begin);
                var importTableRva = reader.ReadUInt32();
                var importTableSize = reader.ReadUInt32();

                fs.Seek(optionalHeaderStart + dataDirectoryOffset + 13 * 8, SeekOrigin.Begin);
                var delayImportRva = reader.ReadUInt32();
                var delayImportSize = reader.ReadUInt32();

                var regularNames = new List<string>();
                var delayNames = new List<string>();
                if ((importTableRva == 0 || importTableSize == 0) && (delayImportRva == 0 || delayImportSize == 0))
                    return new ImportedDllNames { Regular = regularNames, Delay = delayNames };

                var sectionHeaderStart = optionalHeaderStart + sizeOfOptionalHeader;
                var sections = new List<SectionInfo>();
                fs.Seek(sectionHeaderStart, SeekOrigin.Begin);
                for (int i = 0; i < numberOfSections; i++)
                {
                    fs.Seek(8, SeekOrigin.Current);
                    var virtualSize = reader.ReadUInt32();
                    var virtualAddress = reader.ReadUInt32();
                    var sizeOfRawData = reader.ReadUInt32();
                    var pointerToRawData = reader.ReadUInt32();
                    fs.Seek(16, SeekOrigin.Current);
                    sections.Add(new SectionInfo
                    {
                        VirtualAddress = virtualAddress,
                        VirtualSize = Math.Max(virtualSize, sizeOfRawData),
                        PointerToRawData = pointerToRawData
                    });
                }

                long RvaToOffset(uint rva)
                {
                    foreach (var s in sections)
                    {
                        if (rva >= s.VirtualAddress && rva < s.VirtualAddress + s.VirtualSize)
                            return s.PointerToRawData + (rva - s.VirtualAddress);
                    }
                    throw new InvalidDataException($"RVA 0x{rva:X} not found in any section.");
                }

                string ReadAsciiZ(long offset)
                {
                    fs.Seek(offset, SeekOrigin.Begin);
                    var bytes = new List<byte>(64);
                    byte b;
                    while ((b = reader.ReadByte()) != 0)
                        bytes.Add(b);
                    return System.Text.Encoding.ASCII.GetString(bytes.ToArray());
                }

                if (importTableRva != 0 && importTableSize != 0)
                {
                    try
                    {
                        var descriptorOffset = RvaToOffset(importTableRva);
                        const int descriptorSize = 20;

                        while (true)
                        {
                            fs.Seek(descriptorOffset, SeekOrigin.Begin);
                            var originalFirstThunk = reader.ReadUInt32();
                            var timeDateStamp = reader.ReadUInt32();
                            var forwarderChain = reader.ReadUInt32();
                            var nameRva = reader.ReadUInt32();
                            var firstThunk = reader.ReadUInt32();

                            if (originalFirstThunk == 0 && timeDateStamp == 0 && forwarderChain == 0 &&
                                nameRva == 0 && firstThunk == 0)
                                break;

                            if (nameRva != 0)
                                regularNames.Add(ReadAsciiZ(RvaToOffset(nameRva)));

                            descriptorOffset += descriptorSize;
                        }
                    }
                    catch (InvalidDataException) { }
                }

                if (delayImportRva != 0 && delayImportSize != 0)
                {
                    try
                    {
                        var descriptorOffset = RvaToOffset(delayImportRva);
                        const int descriptorSize = 32;

                        while (true)
                        {
                            fs.Seek(descriptorOffset, SeekOrigin.Begin);
                            var attributes = reader.ReadUInt32();
                            var dllNameRva = reader.ReadUInt32();

                            if (attributes == 0 && dllNameRva == 0)
                                break;

                            if (dllNameRva != 0)
                            {
                                try { delayNames.Add(ReadAsciiZ(RvaToOffset(dllNameRva))); }
                                catch (InvalidDataException) { }
                            }

                            descriptorOffset += descriptorSize;
                        }
                    }
                    catch (InvalidDataException) { }
                }

                return new ImportedDllNames { Regular = regularNames, Delay = delayNames };
            }
        }
    }

    /// <summary>
    /// Emulated games do not have an exe of their own: the process that renders is the emulator, so
    /// the limiter has to look at the emulator's exe. This works out which exe that is from Playnite's emulator configuration.
    /// </summary>
    internal static class EmulatorDetection
    {
        /// <summary>Set once by the plugin so static helpers (e.g. exe resolution) can reach Playnite.</summary>

        /// <summary>The emulator executable an emulated game starts (null when the game is not emulated or it cannot be found).</summary>
        public static string ResolveExe(IPlayniteAPI api, Game game, GameAction sourceAction = null, string manualExe = null)
        {
            if (api == null || game == null) return null;
            try
            {
                GameAction action = null;
                if (sourceAction != null && sourceAction.Type == GameActionType.Emulator) action = sourceAction;
                else if (game.GameActions != null)
                    action = game.GameActions.FirstOrDefault(a => a != null && a.IsPlayAction && a.Type == GameActionType.Emulator);
                if (action == null) return null;

                Emulator emulator = null;
                try { emulator = api.Database.Emulators.Get(action.EmulatorId); } catch { }
                if (emulator == null) emulator = FindFallbackEmulator(api, game, action);
                if (emulator == null) return null;

                var installDir = MakeAbsolute(Expand(api, game, emulator.InstallDir, null), null);
                if ((string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir)) && IsRetroArch(emulator))
                {
                    var steamDir = FindSteamRetroArchDir();
                    if (steamDir != null) installDir = steamDir;
                }
                if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir)) return null;

                EmulatorProfile profile = null;
                var profileId = Convert.ToString(action.EmulatorProfileId);
                if (emulator.AllProfiles != null)
                    profile = emulator.AllProfiles.FirstOrDefault(p =>
                        p != null && string.Equals(Convert.ToString(p.Id), profileId, StringComparison.OrdinalIgnoreCase));

                string exe = null;
                var custom = profile as CustomEmulatorProfile;
                if (custom != null && !string.IsNullOrWhiteSpace(custom.Executable))
                    exe = MakeAbsolute(Expand(api, game, custom.Executable, installDir), installDir);
                else
                {
                    var builtIn = profile as BuiltInEmulatorProfile;
                    if (builtIn != null) exe = FindBuiltInExecutable(api, emulator, builtIn, installDir);
                }

                if (string.IsNullOrEmpty(exe) || !File.Exists(exe) ||
                    !string.Equals(Path.GetExtension(exe), ".exe", StringComparison.OrdinalIgnoreCase))
                    exe = GuessExecutable(installDir, emulator.Name);

                return !string.IsNullOrEmpty(exe) && File.Exists(exe) ? exe : null;
            }
            catch { return null; }
        }


        // ---- Known emulators -----------------------------------------------------------------

        private static readonly string[] KnownEmulatorExePrefixes =
        {
            "pcsx2", "retroarch", "dolphin", "rpcs3", "ppsspp", "duckstation", "cemu", "yuzu", "ryujinx",
            "citra", "xenia", "melonds", "mgba", "snes9x", "flycast", "redream", "mame", "xemu", "vita3k",
            "shadps4", "suyu", "sudachi", "azahar", "lime3ds", "zsnes", "superzsnes", "bsnes", "higan", "mesen", "nestopia", "fceux", "project64", "desmume", "visualboyadvance", "mednafen"
        };

        // Emulators whose renderer is Vulkan (by default, or the only one), whatever their exe imports.
        private static readonly string[] VulkanEmulatorExePrefixes =
        {
            "pcsx2", "shadps4", "rpcs3", "cemu", "ryujinx", "yuzu", "suyu", "sudachi", "vita3k"
        };

        // Emulators that draw through OpenGL (no other renderer, or OpenGL by default).
        private static readonly string[] OpenGlEmulatorExePrefixes =
        {
            "melonds", "mgba", "redream", "xemu", "citra", "lime3ds", "flycast"
        };

        /// <summary>True when the executable is one of the well-known emulators (matched by file name).</summary>
        public static bool IsKnownEmulatorExe(string exePath)
        {
            var name = (Path.GetFileNameWithoutExtension(exePath ?? string.Empty) ?? string.Empty).ToLowerInvariant();
            if (name.Length == 0) return false;
            return KnownEmulatorExePrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));
        }

        /// <summary>
        /// The graphics API an emulator is normally used with, when that is more useful than what its exe imports
        /// (PCSX2 links Direct3D but is used with Vulkan; RetroArch links OpenGL but is used with Direct3D 11).
        /// Unknown when there is no such preference. Add a line here for any other emulator that is detected wrongly.
        /// </summary>
        public static ApiKind PreferredApiForEmulatorExe(string exePath)
        {
            var name = (Path.GetFileNameWithoutExtension(exePath ?? string.Empty) ?? string.Empty).ToLowerInvariant();
            if (VulkanEmulatorExePrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal))) return ApiKind.Vulkan;
            if (name.StartsWith("retroarch", StringComparison.Ordinal)) return ApiKind.D3D11;
            // Emulators that only render with one API (or whose settings could not be read): their usual one.
            if (name.StartsWith("zsnes", StringComparison.Ordinal) || name.StartsWith("superzsnes", StringComparison.Ordinal))
                return ApiKind.D3D11;
            if (name.StartsWith("azahar", StringComparison.Ordinal)) return ApiKind.Vulkan;
            if (name.StartsWith("xenia", StringComparison.Ordinal)) return ApiKind.D3D12;
            if (name.StartsWith("ppsspp", StringComparison.Ordinal) || name.StartsWith("duckstation", StringComparison.Ordinal) ||
                name.StartsWith("dolphin", StringComparison.Ordinal)) return ApiKind.D3D11;
            if (OpenGlEmulatorExePrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal))) return ApiKind.OpenGL;
            return ApiKind.Unknown;
        }

        /// <summary>
        /// Reads the renderer the user actually picked in the emulator's own settings file, so two games of the same emulator
        /// can never get different answers. Unknown when the emulator is not supported, the file is missing or the renderer
        /// is not one that is relevant here. <paramref name="reason"/> says where the answer came from.
        /// </summary>
        public static ApiKind ReadConfiguredApi(string exePath, out string reason)
        {
            reason = null;
            try
            {
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return ApiKind.Unknown;
                var dir = Path.GetDirectoryName(exePath);
                var name = (Path.GetFileNameWithoutExtension(exePath) ?? string.Empty).ToLowerInvariant();
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                bool portable = File.Exists(Path.Combine(dir, "portable.ini")) || File.Exists(Path.Combine(dir, "portable.txt"));

                string file = null, key = null, section = null;
                Func<string, ApiKind> map = null;

                if (name.StartsWith("pcsx2", StringComparison.Ordinal))
                {
                    file = FirstExisting(Path.Combine(dir, "inis", "PCSX2.ini"), Path.Combine(docs, "PCSX2", "inis", "PCSX2.ini"), portable);
                    section = "EmuCore/GS"; key = "Renderer";
                    // GSRendererType: -1 Auto, 3 D3D11, 12 OpenGL, 13 Software, 14 Vulkan, 15 D3D12
                    map = v =>
                    {
                        switch ((v ?? "").Trim())
                        {
                            case "14": return ApiKind.Vulkan;
                            case "15": return ApiKind.D3D12;
                            case "3": return ApiKind.D3D11;
                            case "12": return ApiKind.OpenGL;
                            case "-1": return ApiKind.D3D11; // "Automatic" resolves to Direct3D 11 on Windows
                            default: return ApiKind.Unknown; // software / null renderer
                        }
                    };
                }
                else if (name.StartsWith("duckstation", StringComparison.Ordinal))
                {
                    file = FirstExisting(Path.Combine(dir, "settings.ini"), Path.Combine(docs, "DuckStation", "settings.ini"), portable);
                    section = "GPU"; key = "Renderer";
                    map = v =>
                    {
                        var t = (v ?? "").Trim().ToLowerInvariant();
                        if (t.Contains("vulkan")) return ApiKind.Vulkan;
                        if (t.Contains("12")) return ApiKind.D3D12;
                        if (t.Contains("11")) return ApiKind.D3D11;
                        if (t.Contains("opengl")) return ApiKind.OpenGL;
                        return ApiKind.Unknown;
                    };
                }
                else if (name.StartsWith("dolphin", StringComparison.Ordinal))
                {
                    file = FirstExisting(Path.Combine(dir, "User", "Config", "Dolphin.ini"),
                        FirstExisting(Path.Combine(appData, "Dolphin Emulator", "Config", "Dolphin.ini"),
                                      Path.Combine(docs, "Dolphin Emulator", "Config", "Dolphin.ini"), false), portable);
                    section = "Core"; key = "GFXBackend";
                    map = v =>
                    {
                        switch ((v ?? "").Trim().ToLowerInvariant())
                        {
                            case "vulkan": return ApiKind.Vulkan;
                            case "d3d12": return ApiKind.D3D12;
                            case "d3d": return ApiKind.D3D11;
                            case "ogl": return ApiKind.OpenGL;
                            default: return ApiKind.Unknown;
                        }
                    };
                }
                else if (name.StartsWith("retroarch", StringComparison.Ordinal))
                {
                    file = Path.Combine(dir, "retroarch.cfg");
                    key = "video_driver";
                    map = v =>
                    {
                        switch ((v ?? "").Trim().Trim('"').ToLowerInvariant())
                        {
                            case "vulkan": return ApiKind.Vulkan;
                            case "d3d12": return ApiKind.D3D12;
                            case "d3d11": return ApiKind.D3D11;
                            case "gl": case "glcore": return ApiKind.OpenGL;
                            case "d3d9_hlsl": case "d3d9_cg": return ApiKind.D3D9;
                            default: return ApiKind.Unknown;
                        }
                    };
                }
                else if (name.StartsWith("rpcs3", StringComparison.Ordinal))
                {
                    var yml = Path.Combine(dir, "config", "config.yml");
                    if (!File.Exists(yml)) return ApiKind.Unknown;
                    var m = Regex.Match(File.ReadAllText(yml), @"^Video:\s*\r?\n(?:[ \t]+.*\r?\n)*?[ \t]+Renderer:\s*(.+?)\s*$", RegexOptions.Multiline);
                    if (!m.Success) return ApiKind.Unknown;
                    var v = m.Groups[1].Value.Trim().ToLowerInvariant();
                    reason = "renderer set in RPCS3 config.yml";
                    if (v.Contains("vulkan")) return ApiKind.Vulkan;
                    if (v.Contains("directx")) return ApiKind.D3D12;
                    if (v.Contains("opengl")) return ApiKind.OpenGL;
                    reason = null;
                    return ApiKind.Unknown;
                }

                if (map == null)
                {
                    var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    return ReadOtherEmulator(name, dir, docs, appData, localApp, out reason);
                }

                if (file == null || !File.Exists(file)) return ApiKind.Unknown;

                var value = ReadIniValue(File.ReadAllText(file), section, key);
                if (string.IsNullOrWhiteSpace(value)) return ApiKind.Unknown;
                var api = map(value);
                if (api != ApiKind.Unknown)
                    reason = "renderer set in " + Path.GetFileName(file);
                return api;
            }
            catch
            {
                reason = null;
                return ApiKind.Unknown;
            }
        }

        private static string ReadFirstExisting(out string usedFile, params string[] candidates)
        {
            usedFile = null;
            foreach (var c in candidates)
            {
                try
                {
                    if (!string.IsNullOrEmpty(c) && File.Exists(c))
                    {
                        usedFile = c;
                        return File.ReadAllText(c);
                    }
                }
                catch { }
            }
            return null;
        }

        private static string RxGroup(string text, string pattern)
        {
            if (text == null) return null;
            var m = Regex.Match(text, pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        /// <summary>Renderer readers for the emulators whose settings are not a plain ini key (xml, json, yaml, toml, qt-config, mame.ini).</summary>
        private static ApiKind ReadOtherEmulator(string name, string dir, string docs, string appData, string localApp, out string reason)
        {
            reason = null;
            string used;
            string text;
            var api = ApiKind.Unknown;

            if (name.StartsWith("ppsspp", StringComparison.Ordinal))
            {
                text = ReadFirstExisting(out used,
                    Path.Combine(dir, "memstick", "PSP", "SYSTEM", "ppsspp.ini"),
                    Path.Combine(docs, "PPSSPP", "PSP", "SYSTEM", "ppsspp.ini"));
                // GPUBackend: 0 OpenGL, 1 Direct3D 9, 2 Direct3D 11, 3 Vulkan (the value can be followed by a "(NAME)" comment)
                switch (RxGroup(text, @"^\s*GraphicsBackend\s*=\s*(\d+)"))
                {
                    case "0": api = ApiKind.OpenGL; break;
                    case "1": api = ApiKind.D3D9; break;
                    case "2": api = ApiKind.D3D11; break;
                    case "3": api = ApiKind.Vulkan; break;
                }
            }
            else if (name.StartsWith("cemu", StringComparison.Ordinal))
            {
                text = ReadFirstExisting(out used, Path.Combine(dir, "settings.xml"), Path.Combine(appData, "Cemu", "settings.xml"));
                switch (RxGroup(text, @"<Graphic>[\s\S]*?<api>\s*(\d+)\s*</api>"))
                {
                    case "0": api = ApiKind.OpenGL; break;
                    case "1": api = ApiKind.Vulkan; break;
                }
            }
            else if (name.StartsWith("ryujinx", StringComparison.Ordinal))
            {
                text = ReadFirstExisting(out used, Path.Combine(dir, "portable", "Config.json"), Path.Combine(appData, "Ryujinx", "Config.json"));
                var v = (RxGroup(text, "\"graphics_backend\"\\s*:\\s*\"(\\w+)\"") ?? "").ToLowerInvariant();
                if (v == "vulkan") api = ApiKind.Vulkan;
                else if (v == "opengl") api = ApiKind.OpenGL;
            }
            else if (name.StartsWith("yuzu", StringComparison.Ordinal) || name.StartsWith("suyu", StringComparison.Ordinal) ||
                     name.StartsWith("sudachi", StringComparison.Ordinal))
            {
                var folder = name.StartsWith("suyu", StringComparison.Ordinal) ? "suyu" : name.StartsWith("sudachi", StringComparison.Ordinal) ? "sudachi" : "yuzu";
                text = ReadFirstExisting(out used,
                    Path.Combine(dir, "user", "config", "qt-config.ini"),
                    Path.Combine(appData, folder, "config", "qt-config.ini"));
                if (text != null)
                {
                    var isDefault = ReadIniValue(text, "Renderer", "backend\\default");
                    var backend = ReadIniValue(text, "Renderer", "backend");
                    if (string.Equals(isDefault, "true", StringComparison.OrdinalIgnoreCase)) api = ApiKind.Vulkan; // yuzu's default
                    else if (backend == "1") api = ApiKind.Vulkan;
                    else if (backend == "0") api = ApiKind.OpenGL;
                }
            }
            else if (name.StartsWith("azahar", StringComparison.Ordinal) || name.StartsWith("lime3ds", StringComparison.Ordinal) ||
                     name.StartsWith("citra", StringComparison.Ordinal))
            {
                var folder = name.StartsWith("azahar", StringComparison.Ordinal) ? "Azahar" : name.StartsWith("lime3ds", StringComparison.Ordinal) ? "Lime3DS" : "Citra";
                text = ReadFirstExisting(out used,
                    Path.Combine(dir, "user", "config", "qt-config.ini"),
                    Path.Combine(appData, folder, "config", "qt-config.ini"));
                if (text != null)
                {
                    var isDefault = ReadIniValue(text, "Renderer", "graphics_api\\default");
                    var g = ReadIniValue(text, "Renderer", "graphics_api");
                    // 1 OpenGL, 2 Vulkan (0 = software). When it is left on default the usual-API table below answers.
                    if (!string.Equals(isDefault, "true", StringComparison.OrdinalIgnoreCase))
                    {
                        if (g == "1") api = ApiKind.OpenGL;
                        else if (g == "2") api = ApiKind.Vulkan;
                    }
                }
            }
            else if (name.StartsWith("xenia", StringComparison.Ordinal))
            {
                text = ReadFirstExisting(out used, Path.Combine(dir, "xenia.config.toml"), Path.Combine(docs, "Xenia", "xenia.config.toml"));
                var v = (RxGroup(text, "^\\s*gpu\\s*=\\s*\\\"(\\w+)\\\"") ?? "").ToLowerInvariant();
                if (v == "vulkan") api = ApiKind.Vulkan;
                else if (v == "d3d12" || v == "any") api = ApiKind.D3D12; // "any" picks Direct3D 12 on Windows
            }
            else if (name.StartsWith("vita3k", StringComparison.Ordinal))
            {
                text = ReadFirstExisting(out used, Path.Combine(dir, "config.yml"), Path.Combine(appData, "Vita3K", "Vita3K", "config.yml"));
                var v = (RxGroup(text, @"^backend-renderer:\s*(\w+)") ?? "").ToLowerInvariant();
                if (v == "vulkan") api = ApiKind.Vulkan;
                else if (v == "opengl") api = ApiKind.OpenGL;
            }
            else if (name.StartsWith("flycast", StringComparison.Ordinal))
            {
                text = ReadFirstExisting(out used, Path.Combine(dir, "emu.cfg"), Path.Combine(dir, "data", "emu.cfg"), Path.Combine(localApp, "flycast", "emu.cfg"));
                // pvr.rend: 0 / 3 OpenGL (+OIT), 4 / 5 Vulkan (+OIT). Other values fall through to the usual-API table.
                switch (RxGroup(text, @"^\s*pvr\.rend\s*=\s*(\d+)"))
                {
                    case "0": case "3": api = ApiKind.OpenGL; break;
                    case "4": case "5": api = ApiKind.Vulkan; break;
                }
            }
            else if (name.StartsWith("mame", StringComparison.Ordinal))
            {
                text = ReadFirstExisting(out used, Path.Combine(dir, "mame.ini"));
                var video = (RxGroup(text, @"^video\s+(\w+)") ?? "").ToLowerInvariant();
                if (video == "d3d") api = ApiKind.D3D9;
                else if (video == "opengl") api = ApiKind.OpenGL;
                else if (video == "bgfx")
                {
                    switch ((RxGroup(text, @"^bgfx_backend\s+(\w+)") ?? "").ToLowerInvariant())
                    {
                        case "d3d11": case "auto": api = ApiKind.D3D11; break;
                        case "d3d12": api = ApiKind.D3D12; break;
                        case "vulkan": api = ApiKind.Vulkan; break;
                        case "opengl": api = ApiKind.OpenGL; break;
                    }
                }
            }
            else
            {
                return ApiKind.Unknown; // no settings reader: the usual-API table / exe analysis decides
            }

            if (api != ApiKind.Unknown && used != null)
                reason = "renderer set in " + Path.GetFileName(used);
            return api;
        }

        private static string FirstExisting(string a, string b, bool preferFirst)
        {
            if (preferFirst && File.Exists(a)) return a;
            if (File.Exists(b)) return b;
            return File.Exists(a) ? a : (preferFirst ? a : b);
        }



        // ---- Emulator fallbacks ----------------------------------------------------------------

        private static bool IsRetroArch(Emulator emulator)
        {
            if (emulator == null) return false;
            return (emulator.Name ?? string.Empty).IndexOf("retroarch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   string.Equals(emulator.BuiltInConfigId, "retroarch", StringComparison.OrdinalIgnoreCase);
        }

        private static Emulator FindFallbackEmulator(IPlayniteAPI api, Game game, GameAction action)
        {
            List<Emulator> all;
            try { all = api.Database.Emulators.ToList(); }
            catch { return null; }
            if (all.Count == 0) return null;

            // 1) The emulator that owns the profile the action points at.
            var profileId = Convert.ToString(action.EmulatorProfileId);
            if (!string.IsNullOrWhiteSpace(profileId) && profileId != Guid.Empty.ToString())
            {
                var byProfile = all.FirstOrDefault(e => e.AllProfiles != null && e.AllProfiles.Any(p =>
                    p != null && string.Equals(Convert.ToString(p.Id), profileId, StringComparison.OrdinalIgnoreCase)));
                if (byProfile != null) return byProfile;
            }

            // 2) The only emulator with a profile for one of the game's platforms.
            try
            {
                if (game.PlatformIds != null && game.PlatformIds.Count > 0)
                {
                    var byPlatform = all.Where(e => e.AllProfiles != null && e.AllProfiles.Any(p =>
                    {
                        var custom = p as CustomEmulatorProfile;
                        return custom != null && custom.Platforms != null && custom.Platforms.Any(id => game.PlatformIds.Contains(id));
                    })).ToList();
                    if (byPlatform.Count == 1) return byPlatform[0];
                }
            }
            catch { }

            // 3) A single RetroArch entry (also covers the Steam version).
            var retro = all.Where(IsRetroArch).ToList();
            return retro.Count == 1 ? retro[0] : null;
        }

        /// <summary>Finds steamapps\common\RetroArch in the Steam install and its extra library folders.</summary>
        private static string FindSteamRetroArchDir()
        {
            try
            {
                var roots = new List<string>();
                foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
                {
                    foreach (var sub in new[] { @"Software\Valve\Steam", @"Software\WOW6432Node\Valve\Steam" })
                    {
                        using (var key = hive.OpenSubKey(sub))
                        {
                            var path = (key?.GetValue("SteamPath") ?? key?.GetValue("InstallPath")) as string;
                            if (!string.IsNullOrWhiteSpace(path)) roots.Add(path.Replace('/', '\\'));
                        }
                    }
                }

                foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                {
                    var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                    if (!File.Exists(vdf)) continue;
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        roots.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                }

                foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var dir = Path.Combine(root, "steamapps", "common", "RetroArch");
                    if (File.Exists(Path.Combine(dir, "retroarch.exe"))) return dir;
                }
            }
            catch { }
            return null;
        }

        // ---- Executable lookup ---------------------------------------------------------------

        private static string Expand(IPlayniteAPI api, Game game, string input, string emulatorDir)
        {
            if (string.IsNullOrWhiteSpace(input)) return input;
            try
            {
                return emulatorDir == null
                    ? api.ExpandGameVariables(game, input)
                    : api.ExpandGameVariables(game, input, emulatorDir);
            }
            catch
            {
                return input;
            }
        }

        private static string MakeAbsolute(string path, string baseDir)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            if (Path.IsPathRooted(path)) return path;
            var root = string.IsNullOrWhiteSpace(baseDir) ? AppDomain.CurrentDomain.BaseDirectory : baseDir;
            return Path.Combine(root, path);
        }

        private static List<string> EnumerateExecutables(string dir, SearchOption option)
        {
            try
            {
                return Directory.EnumerateFiles(dir, "*.exe", option).ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// Playnite's built-in emulator definitions name the startup executable as a regex matched against
        /// the path relative to the install folder (e.g. "^Dolphin\.exe$"). Apply the same rule.
        /// </summary>
        private static string FindBuiltInExecutable(IPlayniteAPI api, Emulator emulator, BuiltInEmulatorProfile profile, string installDir)
        {
            EmulatorDefinition definition = null;
            try { definition = api.Emulation != null ? api.Emulation.GetEmulator(emulator.BuiltInConfigId) : null; }
            catch { }

            if (definition == null || definition.Profiles == null) return null;

            var defProfile = definition.Profiles.FirstOrDefault(p =>
                p != null && string.Equals(p.Name, profile.BuiltInProfileName, StringComparison.OrdinalIgnoreCase));
            if (defProfile == null || string.IsNullOrWhiteSpace(defProfile.StartupExecutable)) return null;

            Regex rx;
            try { rx = new Regex(defProfile.StartupExecutable, RegexOptions.IgnoreCase); }
            catch { return null; }

            var root = installDir.TrimEnd('\\', '/');
            return EnumerateExecutables(installDir, SearchOption.AllDirectories)
                .Select(f => new
                {
                    File = f,
                    Rel = f.Length > root.Length ? f.Substring(root.Length).TrimStart('\\', '/') : f
                })
                .Where(x => rx.IsMatch(x.Rel))
                .OrderBy(x => x.Rel.Count(c => c == '\\'))
                .ThenBy(x => x.Rel.Length)
                .Select(x => x.File)
                .FirstOrDefault();
        }

        /// <summary>Last resort: the .exe in the emulator folder whose name best matches the emulator's name.</summary>
        private static string GuessExecutable(string installDir, string emulatorName)
        {
            var tokens = new List<string>();
            if (!string.IsNullOrWhiteSpace(emulatorName))
            {
                foreach (var part in Regex.Split(emulatorName.ToLowerInvariant(), @"[^a-z0-9]+"))
                    if (part.Length >= 3) tokens.Add(part);
            }

            var files = EnumerateExecutables(installDir, SearchOption.TopDirectoryOnly);
            if (files.Count == 0) files = EnumerateExecutables(installDir, SearchOption.AllDirectories);
            if (files.Count == 0) return null;

            var candidates = files.Where(f => !IsNonGameExecutable(f)).ToList();
            if (candidates.Count == 0) candidates = files;

            return candidates
                .Select(f => new { File = f, Score = ScoreEmulatorExe(f, tokens), Size = SafeLength(f) })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Size)
                .Select(x => x.File)
                .FirstOrDefault();
        }

        private static int ScoreEmulatorExe(string path, List<string> tokens)
        {
            var name = (Path.GetFileNameWithoutExtension(path) ?? string.Empty).ToLowerInvariant();
            var score = 10;
            foreach (var t in tokens)
                if (name.Contains(t)) score += 40;
            if (name.Contains("cli") || name.Contains("console") || name.Contains("nogui") || name.Contains("batch"))
                score -= 20;
            return score;
        }

        private static long SafeLength(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }





        // ---- Path helpers ---------------------------------------------------------------------

        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var trimmed = path.Trim().Trim('"');
            try { return Path.GetFullPath(trimmed).TrimEnd('\\', '/'); }
            catch { return trimmed.TrimEnd('\\', '/'); }
        }

        public static bool PathsEqual(string a, string b)
        {
            var na = NormalizePath(a);
            var nb = NormalizePath(b);
            if (na == null || nb == null) return false;
            return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
        }

        internal static string ReadIniValue(string ini, string section, string key)
        {
            if (string.IsNullOrEmpty(ini)) return null;
            string current = null;
            foreach (var raw in ini.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    current = line.Substring(1, line.Length - 2);
                    continue;
                }
                if (!string.Equals(current, section, StringComparison.OrdinalIgnoreCase)) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(eq + 1).Trim();
            }
            return null;
        }

        /// <summary>Installers, redistributables and helpers that are never the emulator itself.</summary>
        internal static bool IsNonGameExecutable(string path)
        {
            var name = (Path.GetFileName(path) ?? string.Empty).ToLowerInvariant();
            string[] junk =
            {
                "unins", "uninstall", "setup", "install", "redist", "vcredist", "crash", "report", "werfault",
                "updater", "update", "download", "dotnet", "msiexec", "prereq", "bootstrap", "helper"
            };
            return junk.Any(j => name.Contains(j));
        }
    }

    /// <summary>
    /// Decides whether a game renders with Vulkan (native Vulkan, DXVK, or an emulator running its Vulkan renderer).
    /// Same detection order as ReShade Manager: launch arguments, DXVK in the folder, the renderer picked inside the emulator,
    /// the emulator's usual API, Vulkan loaded at run time, the import table, and the game's own DLLs.
    /// </summary>
    internal static class VulkanGameDetector
    {
        /// <param name="exePath">The exe the limiter resolved for the game (null for emulated games: the emulator is resolved here).</param>
        public static bool IsVulkan(IPlayniteAPI api, Game game, string exePath, out string reason)
        {
            reason = null;
            if (game == null) return false;

            string args = null;
            try
            {
                var play = game.GameActions?.FirstOrDefault(a => a != null && a.IsPlayAction);
                args = play?.Arguments;
                if (!string.IsNullOrWhiteSpace(args) && api != null)
                {
                    try { args = api.ExpandGameVariables(game, args); } catch { }
                }
            }
            catch { }

            var emulatorExe = EmulatorDetection.ResolveExe(api, game);
            var isEmulator = emulatorExe != null;
            var exe = isEmulator ? emulatorExe : exePath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                exe = exePath;
            isEmulator = isEmulator || EmulatorDetection.IsKnownEmulatorExe(exe);

            // 1. Launch arguments (-vulkan, -dx12, --renderer=vulkan ...)
            var hint = GraphicsApiDetector.FromLaunchArguments(args);
            if (hint != null)
            {
                reason = hint.Reason;
                return hint.Api == ApiKind.Vulkan;
            }

            var haveExe = !string.IsNullOrWhiteSpace(exe) && File.Exists(exe);
            var dir = haveExe ? Path.GetDirectoryName(exe) : null;
            if (dir == null)
            {
                try { dir = game.InstallDirectory; } catch { }
                if (!string.IsNullOrWhiteSpace(dir) && api != null)
                {
                    try { dir = api.ExpandGameVariables(game, dir); } catch { }
                }
            }

            // 2. DXVK next to the exe
            if (IsDxvkPresent(dir))
            {
                reason = "DXVK is in the game folder";
                return true;
            }

            // 2b. The game's own settings file names the renderer (recomps / cross-API engines ship D3D12 and Vulkan together)
            if (!isEmulator)
            {
                string settingsFile;
                var fromSettings = ReadGameSettingsApi(dir, out settingsFile);
                if (fromSettings != ApiKind.Unknown)
                {
                    reason = "renderer set in " + settingsFile;
                    return fromSettings == ApiKind.Vulkan;
                }
            }

            // No exe resolved (the limiter only knows the exe once the game runs): guess it from the install folder,
            // the way ReShade Manager does, so the same evidence is available.
            if (!haveExe && !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                var guessed = GuessExe(dir, game.Name);
                if (guessed != null)
                {
                    exe = guessed;
                    haveExe = true;
                    isEmulator = isEmulator || EmulatorDetection.IsKnownEmulatorExe(exe);
                }
            }

            if (!haveExe)
            {
                // Nothing to open, but the folder itself can still say Vulkan (SPIR-V / Vulkan files, DLLs that use it).
                var folderHint = GraphicsApiDetector.DetectFromSiblings(dir);
                if (folderHint != null)
                {
                    reason = folderHint.Reason;
                    return folderHint.Api == ApiKind.Vulkan;
                }
                reason = "game executable not found";
                return false;
            }

            // 3. What the user picked inside the emulator, then the emulator's usual API
            if (isEmulator)
            {
                string configuredReason;
                var configured = EmulatorDetection.ReadConfiguredApi(exe, out configuredReason);
                if (configured != ApiKind.Unknown)
                {
                    reason = configuredReason ?? "renderer set in the emulator";
                    return configured == ApiKind.Vulkan;
                }

                var preferred = EmulatorDetection.PreferredApiForEmulatorExe(exe);
                if (preferred != ApiKind.Unknown)
                {
                    reason = "usual API of this emulator";
                    return preferred == ApiKind.Vulkan;
                }
            }

            // 4. Vulkan loaded at run time / imported with no Direct3D
            var vulkan = GraphicsApiDetector.DetectVulkan(exe, false);
            if (vulkan != null)
            {
                reason = vulkan.Reason;
                return vulkan.Api == ApiKind.Vulkan;
            }

            // 5. Import table: a Vulkan loader import
            try
            {
                var imports = PeImportReader.ReadImportedDllNames(exe);
                foreach (var list in new[] { imports.Regular, imports.Delay })
                {
                    if (list == null) continue;
                    if (list.Any(n => string.Equals(n, "vulkan-1.dll", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(n, "vulkan.dll", StringComparison.OrdinalIgnoreCase)))
                    {
                        reason = "the exe imports the Vulkan loader";
                        return true;
                    }
                }
            }
            catch { }

            // 6. A thin exe whose renderer lives in a DLL next to it (not for emulators: their folder may be shared)
            if (!isEmulator)
            {
                var sibling = GraphicsApiDetector.DetectFromSiblings(dir);
                if (sibling != null)
                {
                    reason = sibling.Reason;
                    return sibling.Api == ApiKind.Vulkan;
                }
            }

            // 7. The resolved exe may just be a launcher: check the game's other executables.
            if (!isEmulator)
            {
                var tree = GraphicsApiDetector.DetectVulkanInTree(dir, exe);
                if (tree != null)
                {
                    reason = tree.Reason;
                    return true;
                }
            }

            reason = "no Vulkan evidence in the exe, its imports or its settings";
            return false;
        }

        private static readonly Regex SettingsApiRegex = new Regex(
            "[\"']?(graphics[_\\- ]?api|render[_\\- ]?api|rendering[_\\- ]?api|renderer|gpu[_\\- ]?api|gfx[_\\- ]?api|backend|rhi|video[_\\- ]?driver)[\"']?\\s*[:=]\\s*[\"']?\\s*" +
            "(vulkan|vk|d3d ?12|dx ?12|directx ?12|d3d ?11|dx ?11|direct3d ?11|opengl|gl)\\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Looks for a small settings file in the game folder (two levels deep) that states the renderer,
        /// e.g. "graphics_api": "Vulkan" or Renderer=D3D12. Unknown when nothing says so.
        /// </summary>
        private static ApiKind ReadGameSettingsApi(string dir, out string fileName)
        {
            fileName = null;
            try
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return ApiKind.Unknown;
                var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { ".json", ".toml", ".ini", ".cfg", ".conf", ".yml", ".yaml", ".xml" };

                var seen = 0;
                var queue = new Queue<Tuple<string, int>>();
                queue.Enqueue(Tuple.Create(dir, 0));
                while (queue.Count > 0 && seen < 400)
                {
                    var item = queue.Dequeue();
                    IEnumerable<string> entries;
                    try { entries = Directory.EnumerateFileSystemEntries(item.Item1).ToList(); }
                    catch { continue; }

                    foreach (var entry in entries)
                    {
                        if (++seen > 400) break;
                        var name = Path.GetFileName(entry) ?? string.Empty;
                        if (Directory.Exists(entry))
                        {
                            var lower = name.ToLowerInvariant();
                            if (item.Item2 < 2 && lower != "shaders" && lower != "reshade-shaders" && lower != "textures" &&
                                lower != "assets" && lower != "data" && lower != "logs")
                                queue.Enqueue(Tuple.Create(entry, item.Item2 + 1));
                            continue;
                        }

                        if (!extensions.Contains(Path.GetExtension(name))) continue;
                        var lowerName = name.ToLowerInvariant();
                        if (lowerName.Contains("reshade") || lowerName.StartsWith("dxvk") || lowerName.Contains("specialk")) continue;

                        FileInfo fi;
                        try { fi = new FileInfo(entry); } catch { continue; }
                        if (fi.Length == 0 || fi.Length > 256 * 1024) continue;

                        string text;
                        try { text = File.ReadAllText(entry); } catch { continue; }
                        var m = SettingsApiRegex.Match(text);
                        if (!m.Success) continue;

                        var v = m.Groups[2].Value.ToLowerInvariant().Replace(" ", string.Empty);
                        fileName = name;
                        if (v == "vulkan" || v == "vk") return ApiKind.Vulkan;
                        if (v.Contains("12")) return ApiKind.D3D12;
                        if (v.Contains("11")) return ApiKind.D3D11;
                        return ApiKind.OpenGL;
                    }
                }
            }
            catch { }
            fileName = null;
            return ApiKind.Unknown;
        }

        /// <summary>The most likely game exe in a folder: name matches the game, then size; helpers and installers are skipped.</summary>
        private static string GuessExe(string dir, string gameName)
        {
            try
            {
                var tokens = Regex.Split((gameName ?? string.Empty).ToLowerInvariant(), "[^a-z0-9]+").Where(t => t.Length >= 3).ToList();
                var files = Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).ToList();
                if (files.Count == 0)
                    files = Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories).Take(200).ToList();
                var candidates = files.Where(f => !EmulatorDetection.IsNonGameExecutable(f)).ToList();
                if (candidates.Count == 0) return null;

                return candidates
                    .Select(f =>
                    {
                        long size = 0;
                        try { size = new FileInfo(f).Length; } catch { }
                        var n = (Path.GetFileNameWithoutExtension(f) ?? string.Empty).ToLowerInvariant();
                        var score = tokens.Count(t => n.Contains(t)) * 100;
                        return new { File = f, Score = score, Size = size };
                    })
                    .OrderByDescending(x => x.Score)
                    .ThenByDescending(x => x.Size)
                    .Select(x => x.File)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        private static bool IsDxvkPresent(string gameDir)
        {
            if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir)) return false;
            return ProductName(Path.Combine(gameDir, "d3d9.dll")) == "DXVK"
                || ProductName(Path.Combine(gameDir, "dxgi.dll")) == "DXVK"
                || ProductName(Path.Combine(gameDir, "d3d11.dll")) == "DXVK";
        }

        private static string ProductName(string path)
        {
            try { return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).ProductName : null; }
            catch { return null; }
        }
    }
}
