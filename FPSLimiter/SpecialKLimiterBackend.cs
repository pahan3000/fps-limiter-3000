using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FPSLimiter
{
    /// <summary>
    /// Applies / restores FPS limits via Special K game profiles
    /// (<c>[Render.FrameRate] TargetFPS=...</c> in SpecialK.ini).
    ///
    /// Used when Special K is (or will be) injected into a game so RTSS is not also injected —
    /// the two injectors conflict. Works without the Special K Helper Playnite extension:
    /// set backend to Special K (or Auto) and inject via SKIF / local DLL yourself.
    /// Optional Helper feature tags are still respected when present.
    /// </summary>
    public class SpecialKLimiterBackend
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        // Common process names used by Special K global injection / frontend.
        private static readonly string[] SpecialKProcessNames =
        {
            "SKIF",
            "SKIFsvc32",
            "SKIFsvc64",
            "SpecialK32",
            "SpecialK64",
            "SK_InjectHelper32",
            "SK_InjectHelper64"
        };

        private readonly FPSLimiterSettings settings;

        public SpecialKLimiterBackend(FPSLimiterSettings settings)
        {
            this.settings = settings;
        }

        // Optional Playnite features written by Special K Helper (darklinkpower).
        // The extension works without Helper — these tags are only used when present.
        public const string SelectiveEnableFeatureName = "[SK] Selective Mode Enable";
        public const string GlobalDisableFeatureName = "[SK] Global Mode Disable";

        /// <summary>
        /// Whether Special K should be used for this game when backend preference is Auto.
        /// Does not require the Special K Helper Playnite extension.
        /// Order: optional Helper feature tags → SKIF/SK service running → valid SK install path.
        /// Forced backend "Special K" bypasses this and only needs a resolvable install path.
        /// </summary>
        public bool IsSpecialKEnabledForGame(Game game)
        {
            if (game != null)
            {
                if (GameHasFeature(game, GlobalDisableFeatureName))
                {
                    return false;
                }

                if (GameHasFeature(game, SelectiveEnableFeatureName))
                {
                    return true;
                }
            }

            // User started SKIF services without Helper.
            if (IsSpecialKServiceRunning())
            {
                return true;
            }

            // Independent mode: SK is installed. User injects via SKIF whitelist or local DLL.
            return !string.IsNullOrWhiteSpace(ResolveSpecialKRoot());
        }

        /// <summary>
        /// Legacy machine-wide check. Prefer <see cref="IsSpecialKEnabledForGame"/> for per-game decisions.
        /// </summary>
        public bool IsSpecialKInjectionLikelyActive()
        {
            return IsSpecialKServiceRunning();
        }

        public bool IsSpecialKServiceRunning()
        {
            try
            {
                foreach (var name in SpecialKProcessNames)
                {
                    if (Process.GetProcessesByName(name).Any())
                    {
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                logger.Debug(e, "Failed to enumerate Special K processes.");
            }

            return false;
        }

        private static bool GameHasFeature(Game game, string featureName)
        {
            if (game?.Features == null || string.IsNullOrWhiteSpace(featureName))
            {
                return false;
            }

            try
            {
                return game.Features.Any(f =>
                    f != null &&
                    string.Equals(f.Name, featureName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Special K's global injector only initialises inside whitelisted processes
        /// (Global\whitelist.ini). SKIF writes this entry when a game is added to its library;
        /// without it, launching from Playnite is never injected. One pattern per line.
        /// </summary>
        public void EnsureGameWhitelisted(Game game, string executablePath)
        {
            try
            {
                var root = ResolveSpecialKRoot();
                if (string.IsNullOrWhiteSpace(root))
                    return;

                var patterns = new List<string>();
                void AddExe(string exeCandidate)
                {
                    if (string.IsNullOrWhiteSpace(exeCandidate)) return;
                    string name;
                    try { name = Path.GetFileName(exeCandidate.Trim().Trim('"')); }
                    catch { return; }
                    if (!string.IsNullOrWhiteSpace(name) &&
                        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        !patterns.Contains(name, StringComparer.OrdinalIgnoreCase))
                        patterns.Add(name);
                }

                AddExe(executablePath);
                var saved = game != null ? settings?.GetGameProfile(game.Id) : null;
                AddExe(saved?.ManualExecutablePath);
                AddExe(saved?.LastResolvedExecutable);

                // Nothing known about the real executable: fall back to the install folder name.
                if (patterns.Count == 0 && !string.IsNullOrWhiteSpace(game?.InstallDirectory))
                {
                    var leaf = Path.GetFileName(game.InstallDirectory.TrimEnd((char)92, '/'));
                    if (!string.IsNullOrWhiteSpace(leaf) && leaf.Length >= 4)
                        patterns.Add(leaf);
                }

                if (patterns.Count == 0)
                    return;

                var globalDir = Path.Combine(root, "Global");
                Directory.CreateDirectory(globalDir);
                var path = Path.Combine(globalDir, "whitelist.ini");

                var lines = File.Exists(path) ? ReadIniLines(path) : new List<string>();
                var added = false;
                foreach (var pattern in patterns)
                {
                    if (lines.Any(l => string.Equals(l.Trim(), pattern, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    lines.Add(pattern);
                    added = true;
                }

                if (!added)
                    return;

                WriteIniLines(path, lines.Where(l => l != null));
                logger.Info($"Added {string.Join(", ", patterns)} to Special K whitelist ({path}).");
            }
            catch (Exception e)
            {
                logger.Warn(e, "Could not update the Special K whitelist.");
            }
        }

        /// <summary>
        /// Blocks (briefly) until the injector host process is running so the global hook is
        /// registered before the game process is created. Returns false on timeout.
        /// </summary>
        public bool WaitForInjectionServiceReady(int timeoutMs = 10000)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < timeoutMs)
            {
                if (IsSkifServiceHostRunning())
                {
                    // Give the hook a moment to register after the host appears.
                    System.Threading.Thread.Sleep(750);
                    logger.Info($"Special K injection service ready after {timer.ElapsedMilliseconds} ms.");
                    return true;
                }
                System.Threading.Thread.Sleep(100);
            }
            logger.Warn("Timed out waiting for the Special K injection service to start.");
            return false;
        }

        /// <summary>
        /// How the injection service was started. <see cref="SkifTemp"/> means SKIF will
        /// auto-stop after a successful inject; other paths need the caller to stop.
        /// </summary>
        public enum InjectionStartKind
        {
            Failed = 0,
            /// <summary>SKIF.exe with "Start Temp" — SKIF auto-stops after inject.</summary>
            SkifTemp = 1,
            /// <summary>SKIFsvc / SKIF permanent / rundll — caller manages the service lifetime.</summary>
            ManualStopRequired = 2
        }

        /// <summary>
        /// Starts the Special K global injection service. Prefers SKIFsvc32/64; if unavailable,
        /// temporary mode uses SKIF's Start Temp command. Falls back to SKIF.exe and then
        /// rundll32 RunDLL_InjectionManager.
        /// </summary>
        /// <returns>How the service was started (so callers know if Temp auto-stop applies).</returns>
        public InjectionStartKind StartInjectionService(bool temporary)
        {
            bool startedByExtension;
            return StartInjectionService(temporary, out startedByExtension);
        }

        /// <summary>
        /// Starts or reuses the injection helper and reports whether this call launched it.
        /// The caller applies the user's selected service-lifetime policy either way.
        /// </summary>
        public InjectionStartKind StartInjectionService(bool temporary, out bool startedByExtension)
        {
            startedByExtension = false;

            // Reuse an already-running service instead of starting duplicate service hosts.
            // The caller still applies the user's explicit service-lifetime choice.
            if (IsSkifServiceHostRunning())
            {
                logger.Info("Special K service is already running; no duplicate start will be issued. The selected lifetime policy will be applied after launch.");
                return InjectionStartKind.ManualStopRequired;
            }

            // Prefer the headless injector host (SKIFsvc) so the SKIF window never opens.
            // The extension's post-injection watcher stops it after Special K is detected.
            if (TryStartSkifSvc())
            {
                startedByExtension = true;
                WaitForInjectionServiceReady();
                return InjectionStartKind.ManualStopRequired;
            }

            // Fallback: SKIF's documented temporary mode (opens SKIF, but it stops itself).
            if (temporary && TryStartSkif("Start Temp"))
            {
                startedByExtension = true;
                WaitForInjectionServiceReady();
                return InjectionStartKind.SkifTemp;
            }

            // Fall back to SKIF's persistent mode, then the documented DLL bootstrap.
            if (TryStartSkif("Start"))
            {
                startedByExtension = true;
                WaitForInjectionServiceReady();
                return InjectionStartKind.ManualStopRequired;
            }

            if (TryStartViaRundll())
            {
                startedByExtension = true;
                WaitForInjectionServiceReady();
                return InjectionStartKind.ManualStopRequired;
            }

            logger.Warn("Could not start Special K injection service (SKIFsvc, SKIF.exe, and rundll all unavailable).");
            return InjectionStartKind.Failed;
        }

        private bool TryStartSkif(string arguments)
        {
            var skif = ResolveSkifExecutable();
            if (string.IsNullOrWhiteSpace(skif))
                return false;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = skif,
                    Arguments = arguments,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                logger.Info($"Started Special K injection service via SKIF ({arguments}).");
                return true;
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to start SKIF injection service ({arguments}).");
                return false;
            }
        }
        /// <summary>
        /// True when the process has a Special K module loaded (global or local inject).
        /// May return false for cross-bitness processes if modules cannot be enumerated.
        /// </summary>
        public static bool IsSpecialKLoadedInProcess(int processId)
        {
            if (processId <= 0) return false;
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    foreach (ProcessModule module in process.Modules)
                    {
                        var name = module.ModuleName ?? string.Empty;
                        if (name.IndexOf("SpecialK", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                }
            }
            catch (Exception)
            {
                // Access denied / cross-bitness / process exited — treat as unknown.
            }
            return false;
        }

        public bool StopInjectionService()
        {
            // Ask the installed helper to stop, then verify that its background host exited.
            // Process.Start returning successfully is not proof that the service actually stopped.
            // Stop the headless hosts first. SKIF.exe is only used as a last resort because
            // launching it (even with "Stop") opens the SKIF window.
            TryStopSkifSvc();
            if (WaitForSkifServiceHostToStop(3000))
            {
                logger.Info("Special K injection service stopped.");
                return true;
            }

            TryStopSkif();

            if (WaitForSkifServiceHostToStop(5000))
            {
                logger.Info("Special K injection service stopped (via SKIF).");
                return true;
            }

            logger.Warn("Special K stop was requested, but a SKIF service host is still running.");
            return false;
        }

        private bool TryStopSkif()
        {
            var skif = ResolveSkifExecutable();
            if (string.IsNullOrWhiteSpace(skif))
                return false;

            try
            {
                using (var command = Process.Start(new ProcessStartInfo
                {
                    FileName = skif,
                    Arguments = "Stop",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    if (command == null)
                        return false;
                    command.WaitForExit(5000);
                }
                logger.Info("Requested Special K service stop via SKIF.");
                return true;
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to request Special K service stop via SKIF.");
                return false;
            }
        }

        private static bool IsSkifServiceHostRunning()
        {
            try
            {
                foreach (var name in new[] { "SKIFsvc32", "SKIFsvc64" })
                {
                    foreach (var process in Process.GetProcessesByName(name))
                    {
                        using (process)
                        {
                            if (!process.HasExited)
                                return true;
                        }
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        public bool IsInjectionServiceHostRunning()
        {
            return IsSkifServiceHostRunning();
        }

        private static bool WaitForSkifServiceHostToStop(int timeoutMs)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < timeoutMs)
            {
                if (!IsSkifServiceHostRunning())
                    return true;
                System.Threading.Thread.Sleep(200);
            }
            return !IsSkifServiceHostRunning();
        }

        /// <summary>
        /// Locate SKIFsvc32/64 next to Special K install or SKIF.
        /// </summary>
        public string ResolveSkifSvcExecutable(bool prefer64 = true)
        {
            foreach (var dir in EnumerateSpecialKInstallDirs())
            {
                var names = prefer64
                    ? new[] { "SKIFsvc64.exe", "SKIFsvc32.exe" }
                    : new[] { "SKIFsvc32.exe", "SKIFsvc64.exe" };
                foreach (var name in names)
                {
                    var path = Path.Combine(dir, name);
                    if (File.Exists(path))
                        return path;
                }
            }
            return null;
        }

        public string ResolveSkifExecutable()
        {
            var root = ResolveSpecialKRoot();
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            var candidates = new[]
            {
                Path.Combine(root, "SKIF.exe"),
                Path.Combine(root, "Special K", "SKIF.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Special K", "SKIF.exe"),
            };
            foreach (var c in candidates)
            {
                if (File.Exists(c))
                {
                    return c;
                }
            }

            // If user path points at SKIF.exe directly
            if (root.EndsWith("SKIF.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(root))
            {
                return root;
            }

            return null;
        }

        private bool TryStartSkifSvc()
        {
            // Start both hosts when available so 32-bit and 64-bit games are covered.
            var started = false;
            foreach (var prefer64 in new[] { true, false })
            {
                var svc = ResolveSkifSvcExecutable(prefer64);
                if (string.IsNullOrWhiteSpace(svc))
                    continue;
                // Avoid double-starting the same bitness if only one file exists
                if (!prefer64 && string.Equals(svc, ResolveSkifSvcExecutable(true), StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = svc,
                        Arguments = "Start",
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        WorkingDirectory = Path.GetDirectoryName(svc) ?? string.Empty
                    });
                    logger.Info($"Started Special K injection service via {Path.GetFileName(svc)} (no SKIF UI).");
                    started = true;
                }
                catch (Exception e)
                {
                    logger.Error(e, $"Failed to start {svc}.");
                }
            }
            return started;
        }

        private bool TryStopSkifSvc()
        {
            var requested = false;
            foreach (var prefer64 in new[] { true, false })
            {
                var svc = ResolveSkifSvcExecutable(prefer64);
                if (string.IsNullOrWhiteSpace(svc))
                    continue;
                if (!prefer64 && string.Equals(svc, ResolveSkifSvcExecutable(true), StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    using (var command = Process.Start(new ProcessStartInfo
                    {
                        FileName = svc,
                        Arguments = "Stop",
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        WorkingDirectory = Path.GetDirectoryName(svc) ?? string.Empty
                    }))
                    {
                        if (command != null)
                        {
                            command.WaitForExit(5000);
                            requested = true;
                        }
                    }
                }
                catch (Exception e)
                {
                    logger.Error(e, $"Failed to stop {svc}.");
                }
            }
            return requested;
        }

        private bool TryStartViaRundll()
        {
            var root = ResolveSpecialKRoot();
            if (string.IsNullOrWhiteSpace(root))
                return false;

            // Prefer 64-bit DLL on 64-bit OS
            var dlls = new[]
            {
                Path.Combine(root, "SpecialK64.dll"),
                Path.Combine(root, "SpecialK32.dll"),
                Path.Combine(root, "SpecialK.dll"),
            };
            foreach (var dll in dlls)
            {
                if (!File.Exists(dll))
                    continue;
                try
                {
                    // Special K documents RunDLL_InjectionManager as the self-bootstrap entry point.
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = Path.Combine(Environment.SystemDirectory, "rundll32.exe"),
                        Arguments = "\"" + dll + "\",RunDLL_InjectionManager",
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        WorkingDirectory = Path.GetDirectoryName(dll) ?? string.Empty
                    });
                    logger.Info($"Started Special K injection manager via rundll32 ({Path.GetFileName(dll)}).");
                    return true;
                }
                catch (Exception e)
                {
                    logger.Error(e, $"rundll32 injection manager failed for {dll}.");
                }
            }
            return false;
        }

        private IEnumerable<string> EnumerateSpecialKInstallDirs()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                try
                {
                    var full = Path.GetFullPath(path);
                    if (Directory.Exists(full))
                        seen.Add(full);
                }
                catch { /* ignore bad paths */ }
            }

            var root = ResolveSpecialKRoot();
            Add(root);
            if (!string.IsNullOrWhiteSpace(root))
            {
                Add(Path.Combine(root, "Servlet"));
                Add(Path.GetDirectoryName(root));
            }

            var skif = ResolveSkifExecutable();
            if (!string.IsNullOrWhiteSpace(skif))
            {
                Add(Path.GetDirectoryName(skif));
                Add(Path.Combine(Path.GetDirectoryName(skif) ?? "", "Servlet"));
            }

            Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Special K"));
            Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Mods", "SpecialK"));

            return seen;
        }

        public string ResolveSpecialKRoot()
        {
            // Prefer a root that actually contains Profiles\ (where SpecialK.ini lives).
            foreach (var candidate in EnumerateSpecialKRootCandidates())
            {
                var profiles = Path.Combine(candidate, "Profiles");
                if (Directory.Exists(profiles))
                {
                    return candidate;
                }
            }

            // Fall back to any existing install folder (Profiles may be created later).
            foreach (var candidate in EnumerateSpecialKRootCandidates())
            {
                return candidate;
            }

            return null;
        }

        public string ResolveSpecialKBinaryDirectory()
        {
            foreach (var directory in EnumerateSpecialKInstallDirs())
            {
                try
                {
                    if (Directory.Exists(directory) &&
                        Directory.GetFiles(directory, "SpecialK*.dll", SearchOption.TopDirectoryOnly).Length > 0)
                    {
                        return directory;
                    }
                }
                catch (Exception e)
                {
                    logger.Debug(e, $"Could not inspect Special K binary directory {directory}.");
                }
            }

            return ResolveSpecialKRoot();
        }

        private IEnumerable<string> EnumerateSpecialKRootCandidates()
        {
            if (!string.IsNullOrWhiteSpace(settings.SpecialKPath))
            {
                var configured = settings.SpecialKPath.Trim();
                if (Directory.Exists(configured))
                {
                    yield return configured;
                }
                else if (File.Exists(configured))
                {
                    var dir = Path.GetDirectoryName(configured);
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    {
                        yield return dir;
                    }
                }
            }

            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                // Modern SKIF install (user's path): %LOCALAPPDATA%\Programs\Special K
                Path.Combine(localAppData, "Programs", "Special K"),
                Path.Combine(localAppData, "Programs", "SpecialK"),
                Path.Combine(localAppData, "Special K"),
                Path.Combine(localAppData, "SpecialK"),
                // Classic Documents location
                Path.Combine(documents, "My Mods", "SpecialK"),
                Path.Combine(documents, "My Mods", "Special K"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Special K"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Special K"),
            };

            foreach (var candidate in candidates)
            {
                if (Directory.Exists(candidate))
                {
                    yield return candidate;
                }
            }
        }

        public string ResolveProfilesDirectory()
        {
            // Prefer Profiles folders that already contain game profiles.
            foreach (var candidate in EnumerateProfilesDirectoryCandidates())
            {
                try
                {
                    if (Directory.GetDirectories(candidate).Length > 0)
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }

            // Any existing Profiles folder.
            foreach (var candidate in EnumerateProfilesDirectoryCandidates())
            {
                return candidate;
            }

            // Last resort: root\Profiles even if not created yet.
            var root = ResolveSpecialKRoot();
            if (!string.IsNullOrWhiteSpace(root))
            {
                return Path.Combine(root, "Profiles");
            }

            return null;
        }

        private IEnumerable<string> EnumerateProfilesDirectoryCandidates()
        {
            foreach (var root in EnumerateSpecialKRootCandidates())
            {
                var profiles = Path.Combine(root, "Profiles");
                if (Directory.Exists(profiles))
                {
                    yield return profiles;
                }
            }

            // Direct known Profiles paths (in case root detection differs)
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var direct = new[]
            {
                Path.Combine(localAppData, "Programs", "Special K", "Profiles"),
                Path.Combine(localAppData, "Programs", "SpecialK", "Profiles"),
                Path.Combine(documents, "My Mods", "SpecialK", "Profiles"),
                Path.Combine(documents, "My Mods", "Special K", "Profiles"),
            };
            foreach (var path in direct)
            {
                if (Directory.Exists(path))
                {
                    yield return path;
                }
            }
        }

        /// <summary>
        /// Best-effort resolve of the Special K profile folder for a Playnite game.
        /// SK names profiles after the executable title / product name; we try several heuristics.
        /// </summary>
        public string ResolveProfileDirectory(Game game, string executablePath)
        {
            var profilesRoot = ResolveProfilesDirectory();
            if (string.IsNullOrWhiteSpace(profilesRoot)) return null;

            // Special K uses one profile per executable. Reuse the launch target saved by
            // FPS Limiter when this is called later from the per-game settings menu.
            if (string.IsNullOrWhiteSpace(executablePath) && game != null)
            {
                var saved = settings?.GetGameProfile(game.Id);
                executablePath = saved?.ManualExecutablePath ?? saved?.LastResolvedExecutable;
            }

            // A folder learned from a previous real run always wins.
            var learned = game != null ? settings?.GetGameProfile(game.Id)?.SpecialKProfileFolder : null;
            if (!string.IsNullOrWhiteSpace(learned))
            {
                var learnedPath = Path.Combine(profilesRoot, learned);
                if (Directory.Exists(learnedPath)) return learnedPath;
            }

            // Special K names the profile after the executable's product/description string
            // (what its control panel title shows), falling back to the exe file name.
            string friendlyName = null;
            if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
            {
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(executablePath);
                    friendlyName = SanitizeProfileName(
                        !string.IsNullOrWhiteSpace(vi.ProductName) ? vi.ProductName : vi.FileDescription);
                }
                catch { }
            }
            var executableName = !string.IsNullOrWhiteSpace(executablePath)
                ? SanitizeProfileName(Path.GetFileNameWithoutExtension(executablePath))
                : null;
            var gameName = !string.IsNullOrWhiteSpace(game?.Name)
                ? SanitizeProfileName(game.Name)
                : null;

            // Reuse an existing folder if one matches. Order mirrors how Special K names
            // profiles: product name, then the game's title, then the exe file name.
            // Emulated games run under the emulator, so Special K's profile is named after the
            // emulator, never after the game.
            var emulated = game != null && !FPSLimiterSettings.IsWindowsGame(game);
            if (emulated) gameName = null;

            foreach (var candidate in new[] { friendlyName, gameName, executableName })
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try
                {
                    var existing = Directory.GetDirectories(profilesRoot)
                        .FirstOrDefault(d => string.Equals(Path.GetFileName(d), candidate, StringComparison.OrdinalIgnoreCase));
                    if (existing != null) return existing;
                }
                catch (Exception e) { logger.Debug(e, "Failed to look for an existing Special K profile folder."); }
            }

            // Nothing exists yet: use Special K's own naming (product name / game title)
            // instead of the exe file name, so we do not create a decoy folder next to it.
            var preferred = friendlyName ?? gameName ?? executableName;
            if (string.IsNullOrWhiteSpace(preferred)) return null;
            return Path.Combine(profilesRoot, preferred);
        }

        /// <summary>
        /// Removes decoy profile folders this extension may have created (exe-name / game-name
        /// guesses) when they contain nothing but our SpecialK.ini and are not the real folder.
        /// </summary>
        public void RemoveDecoyProfileFolders(Game game, string executablePath, string realFolderName)
        {
            try
            {
                var profilesRoot = ResolveProfilesDirectory();
                if (string.IsNullOrWhiteSpace(profilesRoot) || string.IsNullOrWhiteSpace(realFolderName)) return;

                var names = new List<string>();
                if (!string.IsNullOrWhiteSpace(executablePath))
                    names.Add(SanitizeProfileName(Path.GetFileNameWithoutExtension(executablePath)));
                if (!string.IsNullOrWhiteSpace(game?.Name))
                    names.Add(SanitizeProfileName(game.Name));

                foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (string.Equals(name, realFolderName, StringComparison.OrdinalIgnoreCase)) continue;
                    var dir = Path.Combine(profilesRoot, name);
                    if (!Directory.Exists(dir)) continue;
                    var entries = Directory.GetFileSystemEntries(dir);
                    if (entries.Length == 0 ||
                        (entries.Length == 1 && string.Equals(Path.GetFileName(entries[0]), "SpecialK.ini", StringComparison.OrdinalIgnoreCase)))
                    {
                        Directory.Delete(dir, true);
                        logger.Info($"Removed unused Special K profile folder '{name}' (real folder is '{realFolderName}').");
                    }
                }
            }
            catch (Exception e)
            {
                logger.Debug(e, "Removing decoy Special K profile folders failed.");
            }
        }

        /// <summary>
        /// Finds the profile folder Special K actually used for a running game: a folder whose
        /// logs/other files (not SpecialK.ini) were written after the game launched.
        /// </summary>
        public string FindActiveProfileFolder(DateTime sinceUtc)
        {
            try
            {
                var profilesRoot = ResolveProfilesDirectory();
                if (string.IsNullOrWhiteSpace(profilesRoot) || !Directory.Exists(profilesRoot))
                    return null;

                string best = null;
                var bestTime = DateTime.MinValue;
                foreach (var dir in Directory.GetDirectories(profilesRoot))
                {
                    var name = Path.GetFileName(dir) ?? string.Empty;
                    if (string.Equals(name, "AppCache", StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        if (string.Equals(Path.GetFileName(file), "SpecialK.ini", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var t = File.GetLastWriteTimeUtc(file);
                        if (t >= sinceUtc && t > bestTime)
                        {
                            best = dir;
                            bestTime = t;
                        }
                    }
                }
                return best;
            }
            catch (Exception e)
            {
                logger.Debug(e, "FindActiveProfileFolder failed.");
                return null;
            }
        }

        public LimitSessionSnapshot ApplyLimit(Guid gameId, string gameName, string executablePath, double frameLimit, Game game, SpecialKFrameLimiterMode? limiterModeOverride = null)
        {
            var profilesRoot = ResolveProfilesDirectory();
            if (string.IsNullOrWhiteSpace(profilesRoot))
            {
                throw new InvalidOperationException(
                    "Special K Profiles folder was not found. Install Special K / SKIF or set the Special K path in FPS Limiter 3000 settings.");
            }

            var profileDir = ResolveProfileDirectory(game, executablePath);
            if (string.IsNullOrWhiteSpace(profileDir))
            {
                throw new InvalidOperationException(
                    $"Could not resolve a Special K profile folder for {gameName}.");
            }

            // Special K is in use for this game: make sure SpecialK.ini exists and is not empty.
            // A missing or empty file is filled with the global options from the extension settings.
            try { EnsureGameIni(game, executablePath, frameLimit, out _); }
            catch (Exception e) { logger.Warn(e, $"Could not create SpecialK.ini for {gameName}."); }
            // With a local wrapper the ini that counts is the one next to the DLL in the game folder.
            var iniPath = ResolveActiveIniPath(game, profileDir);
            var profileExisted = File.Exists(iniPath);
            string originalContent = null;
            if (profileExisted)
            {
                originalContent = ReadIniText(iniPath);
                // FPS Limiter owns TargetFPS and, when enabled, the other settings exposed in its UI.
                WriteTargetFps(iniPath, frameLimit);
                SeedOptionalSpecialKDefaults(
                    iniPath,
                    force: settings?.ApplySpecialKSettingsToExistingProfiles ?? false,
                    steamLogSilentOverride: GetSteamLogSilentOverride(game),
                    game: game);

                var readback = ReadTargetFps(iniPath);
                if (readback.HasValue)
                {
                    frameLimit = readback.Value;
                }

                logger.Info($"Applied Special K TargetFPS for {gameName}: {frameLimit.ToString("0.###", CultureInfo.InvariantCulture)} ({iniPath}).");
            }
            else
            {
                logger.Warn($"SpecialK.ini still not present for {gameName} ({iniPath}); TargetFPS will be applied once the file appears.");
            }

            return new LimitSessionSnapshot
            {
                GameId = gameId,
                GameName = gameName,
                ExecutablePath = executablePath,
                ProfileName = Path.GetFileName(profileDir),
                AppliedLimit = frameLimit,
                AppliedSyncMode = FpsSyncMode.Async, // SK does not mirror RTSS sync modes 1:1
                AppliedReflexMarkersDisabled = false,
                UsesGlobalProfile = false,
                ProfileExisted = profileExisted,
                FileFallbackUsed = true,
                ProfileFilePath = iniPath,
                OriginalProfileFileExisted = profileExisted,
                OriginalProfileFileContent = originalContent,
                StartedRtssProcess = false,
                StartedAt = DateTime.UtcNow,
                Backend = LimiterBackendKind.SpecialK
            };
        }

        /// <summary>
        /// Live FPS switch for an already-running Special K session: only rewrites TargetFPS
        /// in the profile INI (keeps the original-content snapshot for a clean restore later).
        /// Special K often picks up TargetFPS from the profile when the config is re-read;
        /// writing the value on every extension-driven change is the supported external path.
        /// </summary>
        public LimitSessionSnapshot UpdateLiveLimit(LimitSessionSnapshot existing, double frameLimit, SpecialKFrameLimiterMode? limiterModeOverride = null)
        {
            if (existing == null || string.IsNullOrWhiteSpace(existing.ProfileFilePath))
            {
                throw new InvalidOperationException("No active Special K session to update.");
            }

            var iniPath = existing.ProfileFilePath;
            if (!File.Exists(iniPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(iniPath) ?? ".");
            }

            WriteTargetFps(iniPath, frameLimit);

            if (limiterModeOverride.HasValue)
            {
                var lines = ReadIniLines(iniPath);
                var mode = limiterModeOverride.Value;
                var policy = SpecialKFrameLimiterModeNames.ToEnforcementPolicy(mode);
                SetIniValue(
                    lines,
                    "Render.FrameRate",
                    "LimitEnforcementPolicy",
                    policy.ToString(CultureInfo.InvariantCulture));
                if (mode == SpecialKFrameLimiterMode.LatentSync)
                    SetIniValue(lines, "Render.FrameRate", "PresentationInterval", "0");
                else
                    SetIniValue(lines, "Render.FrameRate", "PresentationInterval", "-1");

                if (mode == SpecialKFrameLimiterMode.NvidiaReflex)
                {
                    SetIniValue(lines, "NVIDIA.Reflex", "Enable", "true");
                    SetIniValue(lines, "NVIDIA.Reflex", "LowLatency", "true");
                    SetIniValue(lines, "NVIDIA.Reflex", "UseFramerateLimiter", "true");
                    SetIniValue(lines, "NVIDIA.Reflex", "OverrideNativeMode", "true");
                    SetIniValue(lines, "NVIDIA.Reflex", "DisableNative", "true");
                }
                else
                {
                    SetIniValue(lines, "NVIDIA.Reflex", "UseFramerateLimiter", "false");
                }
                WriteIniLines(iniPath, lines);
            }

            var readback = ReadTargetFps(iniPath);
            if (!readback.HasValue || Math.Abs(readback.Value - frameLimit) > 0.01)
            {
                throw new InvalidOperationException(
                    $"Failed to live-update TargetFPS={frameLimit.ToString("0.###", CultureInfo.InvariantCulture)} in {iniPath}.");
            }

            logger.Info($"Live-updated Special K TargetFPS to {frameLimit.ToString("0.###", CultureInfo.InvariantCulture)} for {existing.GameName} ({iniPath}).");

            existing.AppliedLimit = frameLimit;
            existing.AppliedSyncMode = FpsSyncMode.Async;
            return existing;
        }

        public void RestoreLimit(LimitSessionSnapshot snapshot)
        {
            // Leave SpecialK.ini completely alone when the game stops.
            // TargetFPS and every other SK control-panel setting stay as the user left them.
            if (snapshot == null)
            {
                return;
            }

            logger.Info($"Special K session ended for {snapshot.GameName} — profile not modified.");
        }

        private static string SanitizeProfileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name.Trim())
            {
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? ' ' : c);
            }

            // Collapse whitespace
            var cleaned = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
        }

        /// <summary>
        /// Writes TargetFPS plus Special K limiter mode and optional Steam / NVIDIA.Reflex sections.
        /// </summary>


        internal static Encoding GetIniEncoding(string iniPath)
        {
            if (!File.Exists(iniPath))
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            var bytes = File.ReadAllBytes(iniPath);
            if (bytes.Length >= 2)
            {
                if (bytes[0] == 0xFF && bytes[1] == 0xFE)
                    return Encoding.Unicode;
                if (bytes[0] == 0xFE && bytes[1] == 0xFF)
                    return Encoding.BigEndianUnicode;
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                    return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            }

            // Some Special K builds write UTF-16 without a BOM. Detect the usual
            // zero-byte pattern so those profiles are not rewritten as broken UTF-8.
            var sampleLength = Math.Min(bytes.Length, 256);
            var zeroAtEven = 0;
            var zeroAtOdd = 0;
            for (var i = 0; i < sampleLength; i++)
            {
                if (bytes[i] != 0) continue;
                if ((i & 1) == 0) zeroAtEven++;
                else zeroAtOdd++;
            }
            if (zeroAtEven > 8 || zeroAtOdd > 8)
                return zeroAtOdd > zeroAtEven ? Encoding.Unicode : Encoding.BigEndianUnicode;

            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        internal static string ReadIniText(string iniPath)
        {
            using (var reader = new StreamReader(iniPath, GetIniEncoding(iniPath), detectEncodingFromByteOrderMarks: true))
            {
                return reader.ReadToEnd();
            }
        }

        private static List<string> ReadIniLines(string iniPath)
        {
            var lines = new List<string>();
            using (var reader = new StringReader(ReadIniText(iniPath)))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                    lines.Add(line);
            }
            return lines;
        }

        internal static void WriteIniText(string iniPath, string text)
        {
            var directory = Path.GetDirectoryName(iniPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(iniPath, text, GetIniEncoding(iniPath));
        }

        private static void WriteIniLines(string iniPath, IEnumerable<string> lines)
        {
            var directory = Path.GetDirectoryName(iniPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllLines(iniPath, lines, GetIniEncoding(iniPath));
        }

        private static void WriteSpecialKProfile(string iniPath, double frameLimit, SpecialKFrameLimiterMode limiterMode, FPSLimiterSettings settings, bool? steamLogSilentOverride = null)
        {
            // ONLY TargetFPS on every apply. Optionally seed Window Layering when missing.
            // Never rewrite Rumble / Reflex / Steam.Log / limiter policy / other SK panel keys.
            var lines = File.Exists(iniPath)
                ? ReadIniLines(iniPath)
                : new List<string>();

            if (frameLimit > 0)
            {
                SetIniValue(lines, "Render.FrameRate", "TargetFPS", frameLimit.ToString("0.###", CultureInfo.InvariantCulture));
            }

            // Window.System AlwaysOnTop:
            //  -1 = No Preference, 0 = Prevent Always-On-Top, 1 = Force Always-On-Top, 2 = Multitasking-On-Top (matches SpecialKWindowLayering enum)
            // Seed only when the key is absent so user SK panel choices are never overwritten.
            if (settings != null && !HasIniKey(lines, "Window.System", "AlwaysOnTop"))
            {
                var layer = (int)settings.SpecialKWindowLayering;
                SetIniValue(lines, "Window.System", "AlwaysOnTop", layer.ToString(CultureInfo.InvariantCulture));
            }

            var directory = Path.GetDirectoryName(iniPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            WriteIniLines(iniPath, lines);
        }


        /// <summary>
        /// Applies FPS Limiter's Special K settings to the keys exposed in its settings panel.
        /// force=true overwrites those managed keys; force=false fills only missing keys.
        /// </summary>
        private void SeedOptionalSpecialKDefaults(
            string iniPath,
            bool force = false,
            bool? steamLogSilentOverride = null,
            Game game = null,
            bool forceLimiterMode = false)
        {
            if (settings == null || string.IsNullOrWhiteSpace(iniPath) || !File.Exists(iniPath))
            {
                return;
            }

            var lines = ReadIniLines(iniPath);
            var changed = false;
            var profile = game != null ? settings.GetGameProfile(game.Id) : null;
            var layering = profile?.HasSpecialKWindowLayeringOverride == true
                ? profile.SpecialKWindowLayeringOverride : settings.SpecialKWindowLayering;
            var disableRumble = profile?.HasSpecialKDisableRumbleOverride == true
                ? profile.SpecialKDisableRumbleOverride : settings.SpecialKDisableRumble;
            var preloadOverlay = profile?.HasSpecialKPreloadSteamOverlayOverride == true
                ? profile.SpecialKPreloadSteamOverlayOverride : settings.SpecialKPreloadSteamOverlay;
            var disableOverlay = profile?.HasSpecialKDisableSteamOverlayOverride == true
                ? profile.SpecialKDisableSteamOverlayOverride : settings.SpecialKDisableSteamOverlay;
            var useFlipDiscard = profile?.HasSpecialKUseFlipDiscardOverride == true
                ? profile.SpecialKUseFlipDiscardOverride : settings.SpecialKUseFlipDiscard;

            void SetIf(string section, string key, string value, bool forceKey = false)
            {
                if (force || forceKey || !HasIniKey(lines, section, key))
                {
                    SetIniValue(lines, section, key, value);
                    changed = true;
                }
            }

            // Window Layering — Multitasking-On-Top by default (AlwaysOnTop=2)
            SetIf("Window.System", "AlwaysOnTop", ((int)layering).ToString(CultureInfo.InvariantCulture),
                forceKey: profile?.HasSpecialKWindowLayeringOverride == true);

            if (force || settings.SpecialKDisableRumble || profile?.HasSpecialKDisableRumbleOverride == true)
            {
                // SK's real key is [Input.Gamepad] DisableRumble (there is no EnableRumble key).
                SetIf("Input.Gamepad", "DisableRumble", disableRumble ? "true" : "false",
                    forceKey: profile?.HasSpecialKDisableRumbleOverride == true);
            }

            var steamLogSilent = steamLogSilentOverride ?? settings.SpecialKSteamLogSilent;
            if (force || steamLogSilentOverride.HasValue || settings.SpecialKSteamLogSilent)
            {
                SetIf(
                    "Steam.Log",
                    "Silent",
                    steamLogSilent ? "true" : "false",
                    forceKey: steamLogSilentOverride.HasValue);
            }

            SetIf("Steam.System", "PreLoadSteamOverlay", preloadOverlay ? "true" : "false",
                forceKey: profile?.HasSpecialKPreloadSteamOverlayOverride == true);
            SetIf("Steam.System", "NoDrawOverlay", disableOverlay ? "true" : "false",
                forceKey: profile?.HasSpecialKDisableSteamOverlayOverride == true);
            SetIf("Render.DXGI", "UseFlipDiscard", useFlipDiscard ? "true" : "false",
                forceKey: profile?.HasSpecialKUseFlipDiscardOverride == true);

            var limiterMode = profile?.HasSpecialKLimiterModeOverride == true
                ? profile.SpecialKLimiterMode
                : settings.SpecialKFrameLimiterMode;
            var policy = SpecialKFrameLimiterModeNames.ToEnforcementPolicy(limiterMode);
            SetIf(
                "Render.FrameRate",
                "LimitEnforcementPolicy",
                policy.ToString(CultureInfo.InvariantCulture),
                forceKey: forceLimiterMode || profile?.HasSpecialKLimiterModeOverride == true);

            // Latent Sync uses PresentationInterval=0; other modes leave the SK default (-1).
            if (limiterMode == SpecialKFrameLimiterMode.LatentSync)
            {
                SetIf("Render.FrameRate", "PresentationInterval", "0",
                    forceKey: forceLimiterMode || profile?.HasSpecialKLimiterModeOverride == true);
            }
            else if (forceLimiterMode || profile?.HasSpecialKLimiterModeOverride == true)
            {
                // Restore default when switching away from Latent Sync under force.
                SetIf("Render.FrameRate", "PresentationInterval", "-1", forceKey: true);
            }

            var energy = profile?.HasSpecialKEnergyOverride == true
                ? profile.SpecialKEnergyOverride
                : settings.SpecialKEnergyEfficiency;
            double busyPct;
            double busyBias;
            switch (energy)
            {
                case SpecialKEnergyEfficiency.PowerSaving:
                    busyPct = 9.0;
                    busyBias = 0.32;
                    break;
                case SpecialKEnergyEfficiency.MaxAccuracy:
                    busyPct = 100.0;
                    busyBias = 10.0;
                    break;
                case SpecialKEnergyEfficiency.Custom:
                    busyPct = profile?.HasSpecialKEnergyOverride == true
                        ? profile.SpecialKBusyWaitPercentOverride
                        : settings.SpecialKBusyWaitPercent;
                    busyBias = profile?.HasSpecialKEnergyOverride == true
                        ? profile.SpecialKBusyWaitBiasOverride
                        : settings.SpecialKBusyWaitBias;
                    break;
                default:
                    busyPct = -1.0;
                    busyBias = -1.0;
                    break;
            }
            SetIf("Render.FrameRate", "BusyWaitPercent", busyPct.ToString("0.###", CultureInfo.InvariantCulture),
                forceKey: profile?.HasSpecialKEnergyOverride == true);
            SetIf("Render.FrameRate", "BusyWaitBias", busyBias.ToString("0.###", CultureInfo.InvariantCulture),
                forceKey: profile?.HasSpecialKEnergyOverride == true);
            SetIf("Render.FrameRate", "UseAMDMWAITX", settings.SpecialKUseAmdMwaitx ? "true" : "false");
            SetIf("NVIDIA.API", "EnableVulkanBridge", settings.SpecialKVulkanBridgeEnabled ? "1" : "0");

            {
                // When the FPS limiter behavior is NVIDIA Reflex, force Reflex on as the limiter
                // (matches Special K "NVIDIA Reflex" preset: Enable + LowLatency + UseFramerateLimiter).
                var mode = profile?.HasSpecialKReflexModeOverride == true
                    ? profile.SpecialKReflexModeOverride
                    : settings.SpecialKReflexMode;
                if (limiterMode == SpecialKFrameLimiterMode.NvidiaReflex)
                {
                    if (mode == SpecialKReflexMode.Off)
                        mode = SpecialKReflexMode.LowLatency;
                }
                else if (mode == SpecialKReflexMode.Off && settings.SpecialKUseReflexMode
                         && profile?.HasSpecialKReflexModeOverride != true)
                {
                    mode = SpecialKReflexMode.LowLatency;
                }
                bool enable = mode != SpecialKReflexMode.Off || limiterMode == SpecialKFrameLimiterMode.NvidiaReflex;
                bool lowLat = mode == SpecialKReflexMode.LowLatency || mode == SpecialKReflexMode.LowLatencyBoost
                    || limiterMode == SpecialKFrameLimiterMode.NvidiaReflex;
                bool boost = mode == SpecialKReflexMode.LowLatencyBoost;
                var forceReflex = forceLimiterMode || profile?.HasSpecialKLimiterModeOverride == true
                    || profile?.HasSpecialKReflexModeOverride == true;
                SetIf("NVIDIA.Reflex", "Enable", enable ? "true" : "false", forceKey: forceReflex);
                SetIf("NVIDIA.Reflex", "LowLatency", lowLat ? "true" : "false", forceKey: forceReflex);
                SetIf("NVIDIA.Reflex", "LowLatencyBoost", boost ? "true" : "false", forceKey: forceReflex);
                SetIf("NVIDIA.Reflex", "OverrideNativeMode", "true");
                SetIf("NVIDIA.Reflex", "DisableNative", "true");
                SetIf("NVIDIA.Reflex", "UseFramerateLimiter",
                    limiterMode == SpecialKFrameLimiterMode.NvidiaReflex ? "true" : "false",
                    forceKey: forceLimiterMode || profile?.HasSpecialKLimiterModeOverride == true);
                var trigger = profile?.HasSpecialKReflexTriggerOverride == true
                    ? profile.SpecialKReflexTriggerOverride
                    : settings.SpecialKReflexTrigger;
                var engagement = trigger == SpecialKReflexTrigger.StartOfFrame ? 1 : 0;
                SetIf("NVIDIA.Reflex", "EngagementPolicy", engagement.ToString(CultureInfo.InvariantCulture),
                    forceKey: profile?.HasSpecialKReflexTriggerOverride == true);
            }

            if (changed)
            {
                WriteIniLines(iniPath, lines);
                logger.Info($"Applied Special K extension defaults (force={force}) in {iniPath}");
            }
        }


        /// <summary>
        /// All SpecialK.ini paths under Profiles that match the game name (handles duplicate
        /// folders like "Dawnwalker" and "The Blood of Dawnwalker").
        /// </summary>
        public List<string> FindAllMatchingSpecialKIniPaths(Game game)
        {
            var results = new List<string>();
            try
            {
                var profilesRoot = ResolveProfilesDirectory();
                if (string.IsNullOrWhiteSpace(profilesRoot) || !Directory.Exists(profilesRoot))
                {
                    return results;
                }

                var tokens = new List<string>();
                if (!string.IsNullOrWhiteSpace(game?.Name))
                {
                    tokens.Add(game.Name);
                    foreach (var part in game.Name.Split(new[] { ' ', ':', '-', '_' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (part.Length >= 4)
                            tokens.Add(part);
                    }
                }

                foreach (var dir in Directory.GetDirectories(profilesRoot))
                {
                    var folder = Path.GetFileName(dir) ?? "";
                    if (string.Equals(folder, "AppCache", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var ini = Path.Combine(dir, "SpecialK.ini");
                    if (!File.Exists(ini))
                        continue;

                    var nameHit = tokens.Count == 0 || tokens.Any(t =>
                        folder.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        t.IndexOf(folder, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (nameHit)
                    {
                        results.Add(ini);
                    }
                }

                // Fallback: newest ini only
                if (results.Count == 0)
                {
                    string best = null;
                    var bestTime = DateTime.MinValue;
                    foreach (var dir in Directory.GetDirectories(profilesRoot))
                    {
                        var folder = Path.GetFileName(dir) ?? "";
                        if (string.Equals(folder, "AppCache", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var ini = Path.Combine(dir, "SpecialK.ini");
                        if (!File.Exists(ini)) continue;
                        var writeTime = File.GetLastWriteTimeUtc(ini);
                        if (writeTime >= bestTime)
                        {
                            best = ini;
                            bestTime = writeTime;
                        }
                    }
                    if (best != null)
                        results.Add(best);
                }
            }
            catch (Exception e)
            {
                logger.Debug(e, "FindAllMatchingSpecialKIniPaths failed");
            }

            return results;
        }

        public string FindNewestSpecialKIniPath(Game game)
        {
            var all = FindAllMatchingSpecialKIniPaths(game);
            if (all == null || all.Count == 0) return null;
            return all
                .OrderByDescending(p => File.GetLastWriteTimeUtc(p))
                .FirstOrDefault();
        }

        /// <summary>
        /// Write TargetFPS (optional) and apply or seed FPS Limiter's managed settings
        /// into an existing SpecialK.ini path.
        /// </summary>
        public void ApplyToExistingIni(string iniPath, double? frameLimit, bool forceSeedDefaults)
        {
            if (string.IsNullOrWhiteSpace(iniPath) || !File.Exists(iniPath))
            {
                throw new FileNotFoundException("SpecialK.ini not found", iniPath);
            }

            if (frameLimit.HasValue && frameLimit.Value > 0)
            {
                WriteTargetFps(iniPath, frameLimit.Value);
            }

            // An explicit request or the settings-panel option makes FPS Limiter's exposed
            // Special K settings authoritative for the managed keys.
            SeedOptionalSpecialKDefaults(
                iniPath,
                force: forceSeedDefaults || (settings?.ApplySpecialKSettingsToExistingProfiles ?? false));
        }


        /// <summary>
        /// Create/update the game's SpecialK.ini BEFORE the game starts so SK loads our values at inject.
        /// No delays. Writes TargetFPS (if set) and extension defaults.
        /// Also mirrors into all existing matching profile folders.
        /// </summary>
        
        /// <summary>
        /// Special K loads Global\default_SpecialK.ini the first time a game profile is created.
        /// Keep that file in sync with extension defaults (Window Layering, Reflex, etc.).
        /// Existing per-game SpecialK.ini files get TargetFPS and, if enabled, the
        /// settings exposed by FPS Limiter.
        /// </summary>
        public string WriteGlobalDefaultSpecialKIni()
        {
            var root = ResolveSpecialKRoot();
            if (string.IsNullOrWhiteSpace(root))
            {
                var fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "Special K");
                if (Directory.Exists(fallback))
                    root = fallback;
                else
                    throw new InvalidOperationException("Special K install path not found.");
            }

            var globalDir = Path.Combine(root, "Global");
            Directory.CreateDirectory(globalDir);
            var path = Path.Combine(globalDir, "default_SpecialK.ini");

            var layer = settings != null
                ? ((int)settings.SpecialKWindowLayering).ToString(CultureInfo.InvariantCulture)
                : "2";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("; Written by Playnite FPS Limiter 3000 — applied by Special K on first profile create");
            sb.AppendLine("[Window.System]");
            sb.AppendLine("AlwaysOnTop=" + layer);
            sb.AppendLine();

            if (settings != null && settings.SpecialKDisableRumble)
            {
                sb.AppendLine("[Input.Gamepad]");
                sb.AppendLine("DisableRumble=true");
                sb.AppendLine();
            }

            if (settings != null && settings.SpecialKSteamLogSilent)
            {
                sb.AppendLine("[Steam.Log]");
                sb.AppendLine("Silent=true");
                sb.AppendLine();
            }

            if (settings != null)
            {
                sb.AppendLine("[Steam.System]");
                sb.AppendLine("PreLoadSteamOverlay=" + (settings.SpecialKPreloadSteamOverlay ? "true" : "false"));
                sb.AppendLine("NoDrawOverlay=" + (settings.SpecialKDisableSteamOverlay ? "true" : "false"));
                sb.AppendLine();
            }

            if (settings != null)
            {
                var limMode = settings.SpecialKFrameLimiterMode;
                var policy = SpecialKFrameLimiterModeNames.ToEnforcementPolicy(limMode);
                sb.AppendLine("[Render.FrameRate]");
                sb.AppendLine("LimitEnforcementPolicy=" + policy.ToString(CultureInfo.InvariantCulture));
                if (limMode == SpecialKFrameLimiterMode.LatentSync)
                    sb.AppendLine("PresentationInterval=0");

                // Energy Efficiency → BusyWaitPercent / BusyWaitBias / UseAMDMWAITX
                double busyPct;
                double busyBias;
                switch (settings.SpecialKEnergyEfficiency)
                {
                    case SpecialKEnergyEfficiency.PowerSaving:
                        busyPct = 9.0;
                        busyBias = 0.32;
                        break;
                    case SpecialKEnergyEfficiency.MaxAccuracy:
                        busyPct = 100.0;
                        busyBias = 10.0;
                        break;
                    case SpecialKEnergyEfficiency.Custom:
                        busyPct = settings.SpecialKBusyWaitPercent;
                        busyBias = settings.SpecialKBusyWaitBias;
                        break;
                    default: // Default = SK internal (-1)
                        busyPct = -1.0;
                        busyBias = -1.0;
                        break;
                }
                sb.AppendLine("BusyWaitPercent=" + busyPct.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("BusyWaitBias=" + busyBias.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("UseAMDMWAITX=" + (settings.SpecialKUseAmdMwaitx ? "true" : "false"));
                sb.AppendLine();
            }

            if (settings != null)
            {
                var engagement = settings.SpecialKReflexTrigger == SpecialKReflexTrigger.StartOfFrame ? 1 : 0;
                var limMode = settings.SpecialKFrameLimiterMode;
                var mode = settings.SpecialKReflexMode;
                // Prefer explicit mode; fall back to legacy UseReflexMode bool
                if (limMode == SpecialKFrameLimiterMode.NvidiaReflex && mode == SpecialKReflexMode.Off)
                    mode = SpecialKReflexMode.LowLatency;
                else if (mode == SpecialKReflexMode.Off && settings.SpecialKUseReflexMode)
                    mode = SpecialKReflexMode.LowLatency;

                bool enable = mode != SpecialKReflexMode.Off || limMode == SpecialKFrameLimiterMode.NvidiaReflex;
                bool lowLat = mode == SpecialKReflexMode.LowLatency || mode == SpecialKReflexMode.LowLatencyBoost
                    || limMode == SpecialKFrameLimiterMode.NvidiaReflex;
                bool boost = mode == SpecialKReflexMode.LowLatencyBoost;

                sb.AppendLine("[NVIDIA.Reflex]");
                sb.AppendLine("Enable=" + (enable ? "true" : "false"));
                sb.AppendLine("LowLatency=" + (lowLat ? "true" : "false"));
                sb.AppendLine("LowLatencyBoost=" + (boost ? "true" : "false"));
                sb.AppendLine("OverrideNativeMode=true");
                sb.AppendLine("DisableNative=true");
                sb.AppendLine("UseFramerateLimiter=" + (limMode == SpecialKFrameLimiterMode.NvidiaReflex ? "true" : "false"));
                sb.AppendLine("EngagementPolicy=" + engagement.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine();
            }

            if (settings != null)
            {
                sb.AppendLine("[NVIDIA.API]");
                sb.AppendLine("EnableVulkanBridge=" + (settings.SpecialKVulkanBridgeEnabled ? "1" : "0"));
                sb.AppendLine();
                sb.AppendLine("[Render.DXGI]");
                sb.AppendLine("UseFlipDiscard=" + (settings.SpecialKUseFlipDiscard ? "true" : "false"));
                sb.AppendLine();
            }

            WriteIniText(path, sb.ToString());
            logger.Info($"Updated Special K global defaults → {path}");
            return path;
        }

        // ── FPS preset macros ─────────────────────────────────────────────────────────────
        // Special K reads Global\macros.ini when the game starts. Each preset is bound to
        // Ctrl+Shift+<1..9,0> as "TargetFPS <value>", so the extension can switch caps in a running game
        // by pressing that hotkey. Only the managed section below is rewritten; user macros are kept.
        public const string PresetMacroSection = "Macro.FPSLimiter_Presets";
        public const int MaxPresetMacros = 9;
        /// <summary>Ctrl+Shift+0 removes the cap ("TargetFPS 0").</summary>
        public const char DisableMacroKey = '0';

        /// <summary>Digit key for preset slot 0..8 (keys 1-9).</summary>
        public static char PresetMacroKey(int index) => (char)('1' + index);

        public static int FindPresetIndex(IList<double> presets, double cap)
        {
            if (presets == null) return -1;
            for (int i = 0; i < presets.Count && i < MaxPresetMacros; i++)
                if (Math.Abs(presets[i] - cap) < 0.005) return i;
            return -1;
        }

        public const string UserHotkeySection = "Macro.FPSLimiter_Hotkeys";

        /// <summary>
        /// Writes the FPS macros to Special K's Global\macros.ini: the automatic Ctrl+Shift+1-9/0 preset keys
        /// (used for live switching) and the user's own preset hotkeys. Returns the presets actually written.
        /// </summary>
        public List<double> WritePresetMacros(IList<double> presets, IList<KeyValuePair<string, double>> userHotkeys = null, string localWrapperDir = null)
        {
            var written = new List<double>();
            if (presets == null) return written;
            var root = ResolveSpecialKRoot();
            if (string.IsNullOrWhiteSpace(root))
            {
                var fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Special K");
                if (Directory.Exists(fallback)) root = fallback;
            }
            if (!string.IsNullOrWhiteSpace(root))
                written = WriteMacrosFile(Path.Combine(root, "Global", "macros.ini"), presets, userHotkeys);

            // A local wrapper (dxgi.dll + dxgi.ini in the game folder, no SpecialK.central marker) runs as a
            // local install and does not necessarily read the central Global\macros.ini. Mirror the same
            // managed macros next to the wrapper so Ctrl+Shift+N works there too.
            if (!string.IsNullOrWhiteSpace(localWrapperDir) && Directory.Exists(localWrapperDir))
            {
                foreach (var path in LocalMacroPaths(localWrapperDir))
                {
                    try
                    {
                        var local = WriteMacrosFile(path, presets, userHotkeys);
                        if (written.Count == 0) written = local;
                    }
                    catch (Exception e) { logger.Warn(e, "Could not write local Special K macros to " + path + "."); }
                }
            }
            return written;
        }

        /// <summary>Macro files a local wrapper may read, next to the wrapper in the game folder.</summary>
        public static IEnumerable<string> LocalMacroPaths(string wrapperDir)
        {
            yield return Path.Combine(wrapperDir, "macros.ini");
            yield return Path.Combine(wrapperDir, "Global", "macros.ini");
        }

        /// <summary>Removes only the managed sections from the macro files written next to a local wrapper; deletes files that end up empty.</summary>
        public void RemoveLocalMacros(string wrapperDir)
        {
            if (string.IsNullOrWhiteSpace(wrapperDir)) return;
            foreach (var path in LocalMacroPaths(wrapperDir))
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var kept = StripManagedMacroSections(ReadIniLines(path));
                    if (kept.Any(l => !string.IsNullOrWhiteSpace(l)))
                        WriteIniLines(path, kept);
                    else
                    {
                        File.Delete(path);
                        var dir = Path.GetDirectoryName(path);
                        if (string.Equals(Path.GetFileName(dir), "Global", StringComparison.OrdinalIgnoreCase) &&
                            Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                            Directory.Delete(dir);
                    }
                }
                catch (Exception e) { logger.Debug(e, "Could not clean local Special K macros " + path + "."); }
            }
        }

        private static List<string> StripManagedMacroSections(IList<string> lines)
        {
            var kept = new List<string>();
            bool skipping = false;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    skipping = string.Equals(trimmed, "[" + PresetMacroSection + "]", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(trimmed, "[" + UserHotkeySection + "]", StringComparison.OrdinalIgnoreCase);
                if (!skipping) kept.Add(line);
            }
            while (kept.Count > 0 && string.IsNullOrWhiteSpace(kept[kept.Count - 1])) kept.RemoveAt(kept.Count - 1);
            return kept;
        }

        private List<double> WriteMacrosFile(string macrosPath, IList<double> presets, IList<KeyValuePair<string, double>> userHotkeys)
        {
            var written = new List<double>();
            var lines = File.Exists(macrosPath) ? ReadIniLines(macrosPath) : new List<string>();
            var kept = StripManagedMacroSections(lines);

            var section = new List<string> { "[" + PresetMacroSection + "]" };
            foreach (var preset in presets.Where(v => v > 0).Take(MaxPresetMacros))
            {
                section.Add("Ctrl+Shift+" + PresetMacroKey(written.Count) + "=TargetFPS " +
                            preset.ToString("0.###", CultureInfo.InvariantCulture));
                written.Add(preset);
            }
            if (written.Count == 0) return written;
            section.Add("Ctrl+Shift+" + DisableMacroKey + "=TargetFPS 0");

            if (kept.Count > 0) kept.Add(string.Empty);
            kept.AddRange(section);

            var userLines = (userHotkeys ?? new List<KeyValuePair<string, double>>())
                .Where(h => !string.IsNullOrWhiteSpace(h.Key) && h.Value > 0)
                .Select(h => h.Key.Trim() + "=TargetFPS " + h.Value.ToString("0.###", CultureInfo.InvariantCulture))
                .ToList();
            if (userLines.Count > 0)
            {
                kept.Add(string.Empty);
                kept.Add("[" + UserHotkeySection + "]");
                kept.AddRange(userLines);
            }

            // Skip the write when nothing changed (the central file is shared by every game).
            if (!File.Exists(macrosPath) || !lines.SequenceEqual(kept))
            {
                WriteIniLines(macrosPath, kept);
                logger.Info("Wrote " + written.Count + " FPS preset macros and " + userLines.Count + " hotkey macros to " + macrosPath);
            }
            return written;
        }

        public void EnsureProfileBeforeLaunch(Game game, string executablePath, double frameLimit)
        {
            try
            {
                WriteGlobalDefaultSpecialKIni();
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to write Global\\default_SpecialK.ini");
            }

            var profilesRoot = ResolveProfilesDirectory();
            if (string.IsNullOrWhiteSpace(profilesRoot))
            {
                // Last chance: force known AppData path even if not detected
                var fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "Special K", "Profiles");
                Directory.CreateDirectory(fallback);
                profilesRoot = fallback;
                logger.Warn($"Special K Profiles path was not auto-detected; using {profilesRoot}");
            }

            Directory.CreateDirectory(profilesRoot);

            string iniPath;
            bool listedGlobals;
            try
            {
                iniPath = EnsureGameIni(game, executablePath, frameLimit, out listedGlobals);
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to create SpecialK.ini for {game?.Name}.");
                return;
            }

            if (string.IsNullOrWhiteSpace(iniPath)) return;

            if (!listedGlobals)
            {
                // Existing, non-empty profile: preserve Special K choices unless the user enabled
                // FPS Limiter management of the settings exposed in its UI.
                if (frameLimit > 0)
                    WriteTargetFps(iniPath, frameLimit);
                SeedOptionalSpecialKDefaults(
                    iniPath,
                    force: settings?.ApplySpecialKSettingsToExistingProfiles ?? false,
                    steamLogSilentOverride: GetSteamLogSilentOverride(game),
                    game: game);
            }

            logger.Info($"Pre-launch SK profile for {game?.Name}: fps={frameLimit.ToString("0.###", CultureInfo.InvariantCulture)}, created/filled={listedGlobals} → {iniPath}");
        }

        /// <summary>
        /// The wrapper's own ini in the game folder (dxgi.ini ...), or null when this game has no local
        /// wrapper ini yet. When it exists, it (not Profiles\&lt;game&gt;\SpecialK.ini) is what Special K reads.
        /// </summary>
        public string ResolveLocalIniPath(Game game)
        {
            try
            {
                var path = game != null ? settings?.GetGameProfile(game.Id)?.LocalWrapperIniPath : null;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
            }
            catch { }
            return null;
        }

        /// <summary>The ini the extension must write for this game: the local wrapper ini when there is one, else SpecialK.ini in the profile folder.</summary>
        private string ResolveActiveIniPath(Game game, string profileDir)
        {
            return ResolveLocalIniPath(game) ?? Path.Combine(profileDir, "SpecialK.ini");
        }

        /// <summary>
        /// Creates / refreshes the ini of a local wrapper, in the game folder next to the DLL (dxgi.ini ...).
        /// Seeds it from the extension-managed SpecialK.ini in the Profiles folder (same values global injection
        /// uses); when it already exists (the user may have tuned it in Special K), only the keys the extension
        /// owns are refreshed. Returns the ini path, or null when it could not be created.
        /// </summary>
        public string EnsureWrapperIni(Game game, string executablePath, string wrapperDllName, double frameLimit)
        {
            if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(wrapperDllName)) return null;
            var gameDir = Path.GetDirectoryName(executablePath);
            if (string.IsNullOrWhiteSpace(gameDir)) return null;
            var wrapperIni = Path.Combine(gameDir, Path.GetFileNameWithoutExtension(wrapperDllName) + ".ini");

            if (IsIniEmptyOrMissing(wrapperIni))
            {
                string baseIni = null;
                try
                {
                    var profileDir = ResolveProfileDirectory(game, executablePath);
                    if (!string.IsNullOrWhiteSpace(profileDir))
                        baseIni = Path.Combine(profileDir, "SpecialK.ini");
                }
                catch (Exception e) { logger.Debug(e, "Could not resolve the profile folder for the wrapper ini template."); }

                if (baseIni != null && !IsIniEmptyOrMissing(baseIni))
                    File.Copy(baseIni, wrapperIni, true);
                else
                    File.WriteAllText(wrapperIni, string.Empty);
            }

            if (frameLimit > 0)
                WriteTargetFps(wrapperIni, frameLimit);
            SeedOptionalSpecialKDefaults(
                wrapperIni,
                force: settings?.ApplySpecialKSettingsToExistingProfiles ?? false,
                steamLogSilentOverride: GetSteamLogSilentOverride(game),
                game: game);
            return File.Exists(wrapperIni) ? wrapperIni : null;
        }

        /// <summary>
        /// True when SpecialK.ini does not exist, is zero bytes, or contains nothing but blank lines
        /// and comments (no [Section] and no key=value lines).
        /// </summary>
        public static bool IsIniEmptyOrMissing(string iniPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(iniPath) || !File.Exists(iniPath)) return true;
                if (new FileInfo(iniPath).Length == 0) return true;
                foreach (var raw in ReadIniLines(iniPath))
                {
                    var trimmed = raw.Trim().TrimStart('\uFEFF', '\0');
                    if (trimmed.Length == 0) continue;
                    if (trimmed.StartsWith(";", StringComparison.Ordinal) ||
                        trimmed.StartsWith("#", StringComparison.Ordinal)) continue;
                    return false;
                }
                return true;
            }
            catch
            {
                return false; // unreadable: never overwrite it
            }
        }

        /// <summary>
        /// Makes sure the game's SpecialK.ini exists and is not empty. A missing or empty file is
        /// (re)written with every global option from the extension settings (with this game's
        /// overrides applied) plus TargetFPS. A file that already has content is not modified.
        /// Returns the ini path, or null when no Profiles folder could be resolved.
        /// <paramref name="listedGlobalOptions"/> is true when the global options were just written.
        /// </summary>
        public string EnsureGameIni(Game game, string executablePath, double frameLimit, out bool listedGlobalOptions)
        {
            listedGlobalOptions = false;

            var profilesRoot = ResolveProfilesDirectory();
            if (string.IsNullOrWhiteSpace(profilesRoot))
            {
                // Last chance: force the known AppData path even if it was not detected.
                profilesRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "Special K", "Profiles");
                logger.Warn($"Special K Profiles path was not auto-detected; using {profilesRoot}");
            }
            Directory.CreateDirectory(profilesRoot);

            var profileDir = ResolveProfileDirectory(game, executablePath);
            if (string.IsNullOrWhiteSpace(profileDir))
            {
                var fallbackName = !string.IsNullOrWhiteSpace(executablePath)
                    ? SanitizeProfileName(Path.GetFileNameWithoutExtension(executablePath))
                    : SanitizeProfileName(game?.Name);
                if (string.IsNullOrWhiteSpace(fallbackName)) fallbackName = "Unknown";
                profileDir = Path.Combine(profilesRoot, fallbackName);
            }
            Directory.CreateDirectory(profileDir);
            var iniPath = Path.Combine(profileDir, "SpecialK.ini");

            if (!IsIniEmptyOrMissing(iniPath))
                return iniPath;

            var layer = settings != null
                ? ((int)settings.SpecialKWindowLayering).ToString(CultureInfo.InvariantCulture)
                : "2";
            var fpsStr = frameLimit > 0
                ? frameLimit.ToString("0.###", CultureInfo.InvariantCulture)
                : "0.0";

            var wasMissing = !File.Exists(iniPath);
            var template =
                "[Render.FrameRate]" + Environment.NewLine +
                "TargetFPS=" + fpsStr + Environment.NewLine +
                Environment.NewLine +
                "[Window.System]" + Environment.NewLine +
                "AlwaysOnTop=" + layer + Environment.NewLine;
            WriteIniText(iniPath, template);

            // List every global option from the settings (per-game overrides applied).
            SeedOptionalSpecialKDefaults(
                iniPath,
                force: true,
                steamLogSilentOverride: GetSteamLogSilentOverride(game),
                game: game,
                forceLimiterMode: true);

            listedGlobalOptions = true;
            logger.Info($"{(wasMissing ? "Created" : "Filled empty")} SpecialK.ini for {game?.Name} with the global options → {iniPath}");
            return iniPath;
        }


        public void ApplySpecialKCompatibilityToGame(Game game)
        {
            if (game == null) return;
            var profileDir = ResolveProfileDirectory(game, null);
            if (string.IsNullOrWhiteSpace(profileDir)) return;
            var iniPath = ResolveActiveIniPath(game, profileDir);
            if (IsIniEmptyOrMissing(iniPath))
            {
                EnsureGameIni(game, null, 0, out _);
                return;
            }
            SeedOptionalSpecialKDefaults(iniPath, force: false, game: game, forceLimiterMode: true);
        }

        public string FindSpecialKIniPath(Game game, string executablePath = null)
        {
            try
            {
                var local = ResolveLocalIniPath(game);
                if (local != null) return local;
                var profilesRoot = ResolveProfilesDirectory();
                if (string.IsNullOrWhiteSpace(profilesRoot)) return null;
                var profileDir = ResolveProfileDirectory(game, executablePath);
                if (string.IsNullOrWhiteSpace(profileDir)) return null;
                var iniPath = Path.Combine(profileDir, "SpecialK.ini");
                return File.Exists(iniPath) ? iniPath : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool HasIniKey(List<string> lines, string sectionName, string key)
        {
            var sectionHeader = "[" + sectionName + "]";
            var inSection = false;
            for (var i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                {
                    if (inSection)
                    {
                        return false;
                    }

                    inSection = string.Equals(trimmed, sectionHeader, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inSection)
                {
                    continue;
                }

                var eq = trimmed.IndexOf('=');
                if (eq > 0 && string.Equals(trimmed.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }


        /// <summary>Read TargetFPS from a game profile; null if missing.</summary>
        public double? ReadProfileTargetFps(Game game, string executablePath)
        {
            try
            {
                var local = ResolveLocalIniPath(game);
                if (local != null) return ReadTargetFps(local);
                var profilesRoot = ResolveProfilesDirectory();
                if (string.IsNullOrWhiteSpace(profilesRoot)) return null;
                var profileDir = ResolveProfileDirectory(game, executablePath);
                if (string.IsNullOrWhiteSpace(profileDir)) return null;
                var iniPath = Path.Combine(profileDir, "SpecialK.ini");
                if (!File.Exists(iniPath)) return null;
                return ReadTargetFps(iniPath);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Write TargetFPS into the game's SpecialK.ini. A missing or empty ini is created and filled
        /// with the global options from the extension settings first.
        /// Returns null if the ini could not be created.
        /// </summary>
        public string WriteTargetFpsForGame(Game game, string executablePath, double frameLimit, bool forceSeedDefaults = false)
        {
            var profilesRoot = ResolveProfilesDirectory();
            if (string.IsNullOrWhiteSpace(profilesRoot))
            {
                throw new InvalidOperationException(
                    "Special K Profiles folder was not found. Set the Special K path in FPS Limiter 3000 settings.");
            }

            var profileDir = ResolveProfileDirectory(game, executablePath);
            if (string.IsNullOrWhiteSpace(profileDir))
            {
                logger.Info($"Special K profile folder not resolved yet for {game?.Name ?? "game"} — waiting for SK defaults.");
                return null;
            }

            var iniPath = ResolveActiveIniPath(game, profileDir);
            if (IsIniEmptyOrMissing(iniPath))
            {
                // Create SpecialK.ini now (or fill an empty one) with the global options from the
                // extension settings plus this FPS cap, instead of waiting for Special K to do it.
                EnsureGameIni(game, executablePath, frameLimit, out _);
                if (!File.Exists(iniPath)) return null;
                return iniPath;
            }

            WriteTargetFps(iniPath, frameLimit);
            // Apply settings if requested, or force them when this is the profile's first appearance.
            SeedOptionalSpecialKDefaults(
                iniPath,
                force: forceSeedDefaults || (settings?.ApplySpecialKSettingsToExistingProfiles ?? false),
                steamLogSilentOverride: GetSteamLogSilentOverride(game),
                game: game);

            var readback = ReadTargetFps(iniPath);
            logger.Info(
                $"Wrote Special K TargetFPS={frameLimit.ToString("0.###", CultureInfo.InvariantCulture)} → {iniPath} (readback={readback?.ToString("0.###", CultureInfo.InvariantCulture) ?? "null"})");
            return iniPath;
        }

        private bool? GetSteamLogSilentOverride(Game game)
        {
            if (settings == null || game == null)
            {
                return null;
            }

            var profile = settings.GetGameProfile(game.Id);
            return profile != null && profile.HasSteamLogSilentOverride
                ? (bool?)profile.SpecialKSteamLogSilent
                : null;
        }

        /// <summary>
        /// Apply an explicit per-game Steam.Log choice to the existing profile without
        /// overwriting the other Special K settings managed by this extension.
        /// </summary>
        public void ApplySteamLogSilentToGame(Game game, bool value)
        {
            if (game == null)
            {
                return;
            }

            var profileDir = ResolveProfileDirectory(game, null);
            if (string.IsNullOrWhiteSpace(profileDir))
            {
                return;
            }

            var iniPath = ResolveActiveIniPath(game, profileDir);
            if (!File.Exists(iniPath))
            {
                return;
            }

            SeedOptionalSpecialKDefaults(iniPath, steamLogSilentOverride: value);
        }

        private static void SetIniValue(List<string> lines, string sectionName, string key, string value)
        {
            var sectionHeader = "[" + sectionName + "]";
            var sectionIndex = -1;
            var keyIndex = -1;

            for (var i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                {
                    if (sectionIndex >= 0 && i > sectionIndex)
                    {
                        break;
                    }

                    if (string.Equals(trimmed, sectionHeader, StringComparison.OrdinalIgnoreCase))
                    {
                        sectionIndex = i;
                    }
                }
                else if (sectionIndex >= 0 && keyIndex < 0)
                {
                    var eq = trimmed.IndexOf('=');
                    if (eq > 0)
                    {
                        var k = trimmed.Substring(0, eq).Trim();
                        if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                        {
                            keyIndex = i;
                        }
                    }
                }
            }

            var newLine = key + "=" + value;
            if (sectionIndex < 0)
            {
                if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[lines.Count - 1]))
                {
                    lines.Add(string.Empty);
                }

                lines.Add(sectionHeader);
                lines.Add(newLine);
            }
            else if (keyIndex >= 0)
            {
                lines[keyIndex] = newLine;
            }
            else
            {
                lines.Insert(sectionIndex + 1, newLine);
            }
        }

        private static void WriteTargetFps(string iniPath, double frameLimit)
        {
            var lines = File.Exists(iniPath)
                ? ReadIniLines(iniPath)
                : new List<string>();

            var sectionIndex = -1;
            var targetLineIndex = -1;
            var nextSectionIndex = lines.Count;

            for (var i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                {
                    if (sectionIndex >= 0 && i > sectionIndex)
                    {
                        nextSectionIndex = i;
                        break;
                    }

                    if (string.Equals(trimmed, "[Render.FrameRate]", StringComparison.OrdinalIgnoreCase))
                    {
                        sectionIndex = i;
                    }
                }
                else if (sectionIndex >= 0 && targetLineIndex < 0)
                {
                    var eq = trimmed.IndexOf('=');
                    if (eq > 0)
                    {
                        var key = trimmed.Substring(0, eq).Trim();
                        if (string.Equals(key, "TargetFPS", StringComparison.OrdinalIgnoreCase))
                        {
                            targetLineIndex = i;
                        }
                    }
                }
            }

            var valueText = frameLimit.ToString("0.###", CultureInfo.InvariantCulture);
            var newLine = $"TargetFPS={valueText}";

            if (sectionIndex < 0)
            {
                if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[lines.Count - 1]))
                {
                    lines.Add(string.Empty);
                }

                lines.Add("[Render.FrameRate]");
                lines.Add(newLine);
            }
            else if (targetLineIndex >= 0)
            {
                lines[targetLineIndex] = newLine;
            }
            else
            {
                lines.Insert(sectionIndex + 1, newLine);
            }

            var directory = Path.GetDirectoryName(iniPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            WriteIniLines(iniPath, lines);
        }

        /// <summary>Current [Render.FrameRate] TargetFPS in the game's newest SpecialK.ini (null if unavailable).
        /// Special K's in-game control panel writes the cap there, so this reflects changes made in the panel.</summary>
        public double? ReadCurrentTargetFps(Game game)
        {
            try
            {
                var path = ResolveLocalIniPath(game) ?? FindNewestSpecialKIniPath(game);
                return string.IsNullOrWhiteSpace(path) ? (double?)null : ReadTargetFps(path);
            }
            catch { return null; }
        }

        private static double? ReadTargetFps(string iniPath)
        {
            if (!File.Exists(iniPath))
            {
                return null;
            }

            var inSection = false;
            foreach (var raw in ReadIniLines(iniPath))
            {
                var trimmed = raw.Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
                {
                    inSection = string.Equals(trimmed, "[Render.FrameRate]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inSection)
                {
                    continue;
                }

                var eq = trimmed.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                var key = trimmed.Substring(0, eq).Trim();
                if (!string.Equals(key, "TargetFPS", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = trimmed.Substring(eq + 1).Trim();
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
                {
                    return fps;
                }
            }

            return null;
        }
    }

    public enum LimiterBackendKind
    {
        Rtss = 0,
        SpecialK = 1
    }
}
