using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace FPSLimiter
{
    public class GameTargetResolver
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly IPlayniteAPI playniteApi;

        public GameTargetResolver(IPlayniteAPI playniteApi)
        {
            this.playniteApi = playniteApi;
        }

        public string ResolveTarget(Game game, GameLimitProfile profile, GameAction sourceAction = null, int? startedProcessId = null)
        {
            var manualPath = NormalizeExecutablePath(profile?.ManualExecutablePath, game, sourceAction);
            if (IsValidExecutable(manualPath))
            {
                return manualPath;
            }

            var processPath = ResolveStartedProcessPath(startedProcessId);
            if (IsValidExecutable(processPath))
            {
                return processPath;
            }

            // Emulated games have no exe of their own: the emulator is the process that renders.
            var emulatorPath = EmulatorDetection.ResolveExe(playniteApi, game, sourceAction);
            if (IsValidExecutable(emulatorPath))
            {
                return emulatorPath;
            }

            var actionPath = ResolveActionExecutable(game, sourceAction);
            if (IsValidExecutable(actionPath))
            {
                return actionPath;
            }

            var trackingPath = NormalizeExecutablePath(sourceAction?.TrackingPath, game, sourceAction);
            if (IsValidExecutable(trackingPath))
            {
                return trackingPath;
            }

            var lastPath = NormalizeExecutablePath(profile?.LastResolvedExecutable, game, sourceAction);
            if (IsValidExecutable(lastPath))
            {
                return lastPath;
            }

            return null;
        }

        /// <summary>
        /// Where a local (DLL-in-game-folder) wrapper goes: next to the exe that really renders, the same way the
        /// DXVK Manager does it. Manual exe, then the game's Play Action when it is a real file, then a scan of the
        /// install folder. Cached "last seen" paths and tracking paths are deliberately NOT used: they can point at a
        /// launcher stub in the install root, and a wrapper dropped there never loads.
        /// </summary>
        public string ResolveLocalInjectionExecutable(Game game, GameLimitProfile profile, GameAction sourceAction = null)
        {
            var manual = NormalizeExecutablePath(profile?.ManualExecutablePath, game, sourceAction);
            if (IsValidExecutable(manual)) return manual;

            try
            {
                var emulator = EmulatorDetection.ResolveExe(playniteApi, game, sourceAction);
                if (IsValidExecutable(emulator)) return emulator;
            }
            catch { }

            string fromAction = null;
            var actions = new List<GameAction>();
            if (sourceAction != null) actions.Add(sourceAction);
            if (game?.GameActions != null) actions.AddRange(game.GameActions.Where(a => a != null && a.IsPlayAction));
            foreach (var action in actions)
            {
                var raw = action.Path;
                if (string.IsNullOrWhiteSpace(raw) || raw.Contains("://")) continue; // steam://rungameid/... is not an exe
                var normalized = NormalizeExecutablePath(raw, game, action);
                if (IsValidExecutable(normalized)) { fromAction = normalized; break; }
            }

            var guessed = GuessExecutableFromInstallDirectory(game);
            if (fromAction == null) return guessed;

            // A Play Action that points at a small launcher stub (no graphics API) while the install folder holds
            // the real game exe (e.g. Binaries\Win64\Game-Win64-Shipping.exe): use the real one.
            if (!string.IsNullOrWhiteSpace(guessed) &&
                !string.Equals(guessed, fromAction, StringComparison.OrdinalIgnoreCase) &&
                !LinksGraphicsApi(fromAction) && LinksGraphicsApi(guessed))
            {
                logger.Info($"Local Special K: {fromAction} links no graphics API; using {guessed} instead.");
                return guessed;
            }
            return fromAction;
        }

        private static bool LinksGraphicsApi(string exePath)
        {
            try { return GraphicsApiDetector.DetectLinkedApi(exePath) != ApiKind.Unknown; }
            catch { return false; }
        }

        private static readonly string[] GuessNameBlacklist =
        {
            "unins", "redist", "vcredist", "vc_redist", "dotnetfx", "directx", "dxsetup", "dxwebsetup", "setup",
            "crashpad", "crashhandler", "crashreporter", "crashreportclient", "unrealcefsubprocess",
            "easyanticheat", "eac_launcher", "battleye", "oalinst", "vulkanrt", "prereq", "helper", "updater",
            "installer", "benchmark", "wmplayer", "quicktime", "notification_helper", "unitycrash", "download",
            "register", "bootstrap", "patch", "splash", "support", "wmp"
        };

        private static readonly string[] GuessDirBlacklist =
        {
            "_commonredist", "commonredist", "redist", "easyanticheat", "battleye", "__installer", "miles", "support"
        };

        /// <summary>
        /// Best guess at the game's real exe when Playnite only has a launcher link (steam://rungameid/..., Epic, GOG)
        /// and the game has not run yet. Needed by local Special K injection, which has to put its DLL next to the exe.
        /// Same idea as DXVK Manager: skip installers / crash handlers / anti-cheat, prefer a name that matches the
        /// game, then an exe that links Direct3D or OpenGL (Unreal "-Shipping" builds first), then the biggest file.
        /// </summary>
        public string GuessExecutableFromInstallDirectory(Game game)
        {
            try
            {
                var root = game?.InstallDirectory;
                if (string.IsNullOrWhiteSpace(root)) return null;
                try { root = playniteApi.ExpandGameVariables(game, root); } catch { }
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
                root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                var all = new List<string>();
                CollectExecutables(root, 0, all);
                var candidates = all.Where(p => !IsGuessBlacklisted(p, root)).ToList();
                if (candidates.Count == 0) return null;

                var name = NormalizeName(game.Name);
                var nameMatches = string.IsNullOrEmpty(name)
                    ? new List<string>()
                    : candidates.Where(p =>
                    {
                        var n = NormalizeName(Path.GetFileNameWithoutExtension(p));
                        return n.Length > 0 && (n.Contains(name) || name.Contains(n));
                    }).ToList();

                var pool = nameMatches.Count > 0 ? nameMatches : candidates;
                var withApi = pool
                    .Where(p => GraphicsApiDetector.DetectLinkedApi(p) != ApiKind.Unknown)
                    .OrderByDescending(p => Path.GetFileName(p).IndexOf("shipping", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ThenByDescending(p => SafeLength(p))
                    .FirstOrDefault();
                if (withApi != null) return withApi;

                return pool
                    .OrderByDescending(p => Path.GetFileName(p).IndexOf("shipping", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ThenBy(p => RelativeDepth(p, root))
                    .ThenByDescending(p => SafeLength(p))
                    .FirstOrDefault();
            }
            catch (Exception e)
            {
                logger.Debug(e, $"Could not guess the executable of {game?.Name} from its install folder.");
                return null;
            }
        }

        private static void CollectExecutables(string dir, int depth, List<string> found)
        {
            if (found.Count >= 3000 || depth > 6) return;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
                    found.Add(file);
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    try
                    {
                        if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }
                    CollectExecutables(sub, depth + 1, found);
                }
            }
            catch { /* locked folder: skip it */ }
        }

        private static bool IsGuessBlacklisted(string filePath, string root)
        {
            var file = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
            if (GuessNameBlacklist.Any(file.Contains)) return true;
            var relDir = (Path.GetDirectoryName(filePath) ?? string.Empty);
            relDir = (relDir.Length > root.Length ? relDir.Substring(root.Length) : string.Empty).ToLowerInvariant();
            return GuessDirBlacklist.Any(relDir.Contains);
        }

        private static int RelativeDepth(string filePath, string root)
        {
            var rel = filePath.Length > root.Length ? filePath.Substring(root.Length) : string.Empty;
            return rel.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);
        }

        private static long SafeLength(string path)
        {
            try { return new FileInfo(path).Length; } catch { return 0L; }
        }

        private static string NormalizeName(string s)
        {
            return new string((s ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        }

        public string ResolveStartedProcessPath(int? startedProcessId)
        {
            if (!startedProcessId.HasValue || startedProcessId.Value <= 0)
            {
                return null;
            }

            // Playnite is a 32-bit app, so Process.MainModule throws for 64-bit games.
            // QueryFullProcessImageName works across bitness.
            var native = QueryProcessImagePath(startedProcessId.Value);
            if (!string.IsNullOrWhiteSpace(native))
            {
                return native;
            }

            try
            {
                using (var process = Process.GetProcessById(startedProcessId.Value))
                {
                    return process.MainModule.FileName;
                }
            }
            catch (Exception e)
            {
                logger.Debug(e, $"Could not resolve process path for PID {startedProcessId.Value}.");
                return null;
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder exeName, ref int size);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        internal static string QueryProcessImagePath(int processId)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, processId);
                if (handle == IntPtr.Zero) return null;
                var buffer = new System.Text.StringBuilder(1024);
                var size = buffer.Capacity;
                return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (handle != IntPtr.Zero) CloseHandle(handle);
            }
        }

        public string ResolveLaunchedGameProcessTarget(Game game, int? startedProcessId, string currentExecutablePath, int waitMilliseconds = 18000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(waitMilliseconds);
            while (DateTime.UtcNow <= deadline)
            {
                var candidate = startedProcessId.HasValue && startedProcessId.Value > 0
                    ? GetBestDescendantExecutable(startedProcessId.Value, currentExecutablePath, game.Name)
                    : null;

                if (IsValidExecutable(candidate))
                {
                    return candidate;
                }

                candidate = GetBestInstallDirectoryExecutable(game, currentExecutablePath);
                if (IsValidExecutable(candidate))
                {
                    return candidate;
                }

                candidate = GetBestWindowExecutable(game, currentExecutablePath);
                if (IsValidExecutable(candidate))
                {
                    return candidate;
                }

                Thread.Sleep(500);
            }

            return null;
        }

        private string ResolveActionExecutable(Game game, GameAction sourceAction)
        {
            if (sourceAction == null)
            {
                return null;
            }

            return NormalizeExecutablePath(sourceAction.Path, game, sourceAction);
        }

        private string GetBestDescendantExecutable(int rootProcessId, string currentExecutablePath, string gameName)
        {
            try
            {
                var processes = QueryProcesses();
                var descendants = GetDescendants(processes, rootProcessId)
                    .Where(a => IsValidExecutable(a.ExecutablePath))
                    .ToList();

                if (!descendants.Any())
                {
                    return null;
                }

                var currentFile = string.IsNullOrWhiteSpace(currentExecutablePath)
                    ? null
                    : Path.GetFileName(currentExecutablePath);

                return descendants
                    .OrderByDescending(a => ScoreProcess(a, currentFile, gameName))
                    .ThenByDescending(a => a.ProcessId)
                    .Select(a => a.ExecutablePath)
                    .FirstOrDefault();
            }
            catch (Exception e)
            {
                logger.Debug(e, $"Could not inspect child processes for PID {rootProcessId}.");
                return null;
            }
        }

        private string GetBestInstallDirectoryExecutable(Game game, string currentExecutablePath)
        {
            try
            {
                var installRoot = ResolveInstallRoot(game, currentExecutablePath);
                var processes = QueryProcesses()
                    .Where(a => IsValidExecutable(a.ExecutablePath))
                    .ToList();

                if (!string.IsNullOrWhiteSpace(installRoot))
                {
                    processes = processes
                        .Where(a => IsUnderDirectory(a.ExecutablePath, installRoot))
                        .ToList();
                }
                else
                {
                    var words = GetSignificantWords(game?.Name).ToList();
                    processes = processes
                        .Where(a => words.Any(word =>
                            a.ExecutablePath.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            Path.GetFileName(a.ExecutablePath).IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0))
                        .ToList();
                }

                if (!processes.Any())
                {
                    return null;
                }

                var currentFile = string.IsNullOrWhiteSpace(currentExecutablePath)
                    ? null
                    : Path.GetFileName(currentExecutablePath);

                var best = processes
                    .OrderByDescending(a => ScoreProcess(a, currentFile, game?.Name))
                    .ThenByDescending(a => a.ProcessId)
                    .FirstOrDefault();

                if (best != null)
                {
                    logger.Debug($"FPS Limiter install-folder target candidate for {game?.Name}: PID={best.ProcessId}, EXE={best.ExecutablePath}.");
                }

                return best?.ExecutablePath;
            }
            catch (Exception e)
            {
                logger.Debug(e, $"Could not inspect running processes for {game?.Name}.");
                return null;
            }
        }

        private string GetBestWindowExecutable(Game game, string currentExecutablePath)
        {
            try
            {
                var installRoot = ResolveInstallRoot(game, currentExecutablePath);
                var words = GetSignificantWords(game?.Name).ToList();
                var windows = QueryWindowProcesses()
                    .Where(a => IsValidExecutable(a.ExecutablePath))
                    .Where(a =>
                        (!string.IsNullOrWhiteSpace(installRoot) && IsUnderDirectory(a.ExecutablePath, installRoot)) ||
                        words.Any(word =>
                            (a.WindowTitle ?? string.Empty).IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            (a.Name ?? string.Empty).IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            (a.ExecutablePath ?? string.Empty).IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0))
                    .ToList();

                if (!windows.Any())
                {
                    return null;
                }

                var currentFile = string.IsNullOrWhiteSpace(currentExecutablePath)
                    ? null
                    : Path.GetFileName(currentExecutablePath);

                var best = windows
                    .OrderByDescending(a => ScoreProcess(a, currentFile, game?.Name))
                    .ThenByDescending(a => a.ProcessId)
                    .FirstOrDefault();

                if (best != null)
                {
                    logger.Debug($"FPS Limiter window target candidate for {game?.Name}: PID={best.ProcessId}, EXE={best.ExecutablePath}, TITLE={best.WindowTitle}.");
                }

                return best?.ExecutablePath;
            }
            catch (Exception e)
            {
                logger.Debug(e, $"Could not inspect top-level windows for {game?.Name}.");
                return null;
            }
        }

        private static List<ProcessInfo> QueryProcesses()
        {
            var result = new List<ProcessInfo>();
            using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, ExecutablePath, Name FROM Win32_Process"))
            using (var collection = searcher.Get())
            {
                foreach (ManagementObject process in collection)
                {
                    var executablePath = process["ExecutablePath"] as string;
                    if (string.IsNullOrWhiteSpace(executablePath))
                    {
                        continue;
                    }

                    result.Add(new ProcessInfo
                    {
                        ProcessId = Convert.ToInt32((uint)process["ProcessId"]),
                        ParentProcessId = Convert.ToInt32((uint)process["ParentProcessId"]),
                        ExecutablePath = executablePath,
                        Name = process["Name"] as string
                    });
                }
            }

            return result;
        }

        private static List<ProcessInfo> QueryWindowProcesses()
        {
            var result = new List<ProcessInfo>();

            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd))
                {
                    return true;
                }

                var title = GetWindowTitle(hWnd);
                if (string.IsNullOrWhiteSpace(title))
                {
                    return true;
                }

                uint processId;
                GetWindowThreadProcessId(hWnd, out processId);
                if (processId == 0)
                {
                    return true;
                }

                try
                {
                    using (var process = Process.GetProcessById((int)processId))
                    {
                        result.Add(new ProcessInfo
                        {
                            ProcessId = (int)processId,
                            ParentProcessId = 0,
                            ExecutablePath = ResolveProcessPath(process),
                            Name = process.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                ? process.ProcessName
                                : process.ProcessName + ".exe",
                            WindowTitle = title
                        });
                    }
                }
                catch
                {
                    // Window may belong to a process that exited between enumeration and lookup.
                }

                return true;
            }, IntPtr.Zero);

            return result
                .GroupBy(a => a.ProcessId)
                .Select(a => a.First())
                .ToList();
        }

        private static string ResolveProcessPath(Process process)
        {
            try
            {
                return process.MainModule.FileName;
            }
            catch
            {
                return process.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? process.ProcessName
                    : process.ProcessName + ".exe";
            }
        }

        private static IEnumerable<ProcessInfo> GetDescendants(List<ProcessInfo> processes, int rootProcessId)
        {
            var childrenByParent = processes
                .GroupBy(a => a.ParentProcessId)
                .ToDictionary(a => a.Key, a => a.ToList());

            var seen = new HashSet<int>();
            var queue = new Queue<int>();
            queue.Enqueue(rootProcessId);

            while (queue.Any())
            {
                var parentId = queue.Dequeue();
                List<ProcessInfo> children;
                if (!childrenByParent.TryGetValue(parentId, out children))
                {
                    continue;
                }

                foreach (var child in children)
                {
                    if (!seen.Add(child.ProcessId))
                    {
                        continue;
                    }

                    yield return child;
                    queue.Enqueue(child.ProcessId);
                }
            }
        }

        private static int ScoreProcess(ProcessInfo process, string currentFile, string gameName)
        {
            var score = 0;
            var fileName = Path.GetFileName(process.ExecutablePath);

            if (!string.Equals(fileName, currentFile, StringComparison.OrdinalIgnoreCase))
            {
                score += 50;
            }

            if (HasMainWindow(process.ProcessId))
            {
                score += 100;
            }

            if (fileName.IndexOf("shipping", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 25;
            }

            foreach (var word in GetSignificantWords(gameName))
            {
                if (fileName.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    process.ExecutablePath.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (process.WindowTitle ?? string.Empty).IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 10;
                }
            }

            return score;
        }

        private static string ResolveInstallRoot(Game game, string currentExecutablePath)
        {
            if (!string.IsNullOrWhiteSpace(game?.InstallDirectory) && Directory.Exists(game.InstallDirectory))
            {
                return Path.GetFullPath(game.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            if (!string.IsNullOrWhiteSpace(currentExecutablePath) && File.Exists(currentExecutablePath))
            {
                return Path.GetDirectoryName(Path.GetFullPath(currentExecutablePath));
            }

            return null;
        }

        private static bool IsUnderDirectory(string path, string directory)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(fullPath, fullDirectory, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasMainWindow(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    return process.MainWindowHandle != IntPtr.Zero;
                }
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<string> GetSignificantWords(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                yield break;
            }

            var chars = value.Select(a => char.IsLetterOrDigit(a) ? a : ' ').ToArray();
            foreach (var word in new string(chars).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length >= 4)
                {
                    yield return word;
                }
            }
        }

        private string NormalizeExecutablePath(string rawPath, Game game, GameAction sourceAction)
        {
            return NormalizePath(rawPath, sourceAction?.WorkingDir, game?.InstallDirectory);
        }

        private string NormalizePath(string rawPath, params string[] baseDirectories)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                return null;
            }

            var path = rawPath.Trim().Trim('"');
            path = Environment.ExpandEnvironmentVariables(path);

            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile)
            {
                return null;
            }

            if (Path.IsPathRooted(path))
            {
                return Path.GetFullPath(path);
            }

            foreach (var baseDirectory in baseDirectories.Where(a => !string.IsNullOrWhiteSpace(a)))
            {
                try
                {
                    var candidate = Path.GetFullPath(Path.Combine(baseDirectory, path));
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception e)
                {
                    logger.Debug(e, $"Could not combine executable path '{path}' with '{baseDirectory}'.");
                }
            }

            return path;
        }

        private static bool IsValidExecutable(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) &&
                   (!Path.IsPathRooted(path) || File.Exists(path));
        }

        private static string GetWindowTitle(IntPtr hWnd)
        {
            var length = GetWindowTextLength(hWnd);
            if (length <= 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(length + 1);
            GetWindowText(hWnd, builder, builder.Capacity);
            return builder.ToString();
        }

        private class ProcessInfo
        {
            public int ProcessId { get; set; }
            public int ParentProcessId { get; set; }
            public string ExecutablePath { get; set; }
            public string Name { get; set; }
            public string WindowTitle { get; set; }
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    }
}
