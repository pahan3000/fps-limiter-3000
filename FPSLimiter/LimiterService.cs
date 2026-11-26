using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;

namespace FPSLimiter
{
    public class LimiterService
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string DxvkManagerPluginId = "d3434642-aab1-4800-b66f-4f7503b029bb";
        private static readonly TimeSpan VulkanDetectionCacheDuration = TimeSpan.FromSeconds(20);

        private sealed class VulkanDetectionCacheEntry
        {
            public DateTime CheckedAtUtc { get; set; }
            public bool IsVulkanOrDxvk { get; set; }
        }

        private readonly IPlayniteAPI playniteApi;
        private readonly FPSLimiterSettingsViewModel settingsViewModel;
        private readonly RtssLimiterBackend rtssBackend;
        private readonly SpecialKLimiterBackend specialKBackend;
        private readonly GameTargetResolver targetResolver;
        private readonly FreeSyncSessionManager freeSync = new FreeSyncSessionManager();
        internal VrrDisplayVendor GetGameVrrVendor(Game game) =>
            VrrDisplayRouter.ForMonitor(GetGameMonitorDeviceName(game));
        /// <summary>True when an AMD GPU is installed (including hybrid laptops that also have an NVIDIA GPU).</summary>
        public bool HasAmdGpu => GpuInventory.HasAmdGpu;
        public bool IsFreeSyncAvailable => HasAmdGpu && freeSync.Available;
        public void RefreshFreeSyncForGame(Game game)
        {
            if (game == null || !runningGameIds.Contains(game.Id)) return;
            // FreeSync control is AMD-only: with no AMD GPU installed (NVIDIA/Intel-only PCs) there is
            // nothing to control. A laptop with both an AMD and an NVIDIA GPU still counts as AMD-capable.
            if (!HasAmdGpu)
            {
                freeSync.Update(game, false); // release a previous AMD override if one was held
                return;
            }
            var sync = GetEffectiveGameSyncMode(game);
            var scanline = sync == FpsSyncMode.ScanlineSync || sync == FpsSyncMode.ScanlineSyncHalf;
            var disable = scanline || (Settings.GetGameProfile(game.Id)?.DisableFreeSyncForGame ?? false);
            freeSync.Update(game, disable);
        }
        // UI-only state: scanline forces a temporary FreeSync override. Never save it
        // into DisableFreeSyncForGame (which is the user's persistent preference).
        public bool IsScanlineFreeSyncOverride(Game game)
        {
            if (game == null) return false;
            var mode = GetEffectiveGameSyncMode(game);
            return mode == FpsSyncMode.ScanlineSync || mode == FpsSyncMode.ScanlineSyncHalf;
        }
        /// <summary>True while at least one game is running (fast, no allocation).</summary>
        public bool HasRunningGames => runningGameIds.Count > 0;
        public bool IsFreeSyncGameRunning(Game game) => game != null && runningGameIds.Contains(game.Id);
        public bool? ReadFreeSyncEnabled() => HasAmdGpu ? freeSync.GetCurrentState() : null;

        public void RestoreAllFreeSync() => freeSync.RestoreAll();

        /// <summary>Toggles RTSS's FCAT frame color indicator. Returns the new state.</summary>
        public bool ToggleRtssFrameColorIndicator() => rtssBackend.ToggleFrameColorIndicator();

        /// <summary>Restores FreeSync left off by a previous session that ended without restoring it.</summary>
        public void InitFreeSyncRecovery(string stateFile)
        {
            freeSync.StateFile = stateFile;
            System.Threading.Tasks.Task.Run(() => freeSync.RecoverPending());
        }

        // Tracks which games are actually running right now, independent of whether they currently
        // have an active RTSS cap session. ActiveSessions is *not* a reliable "is this game running"
        // signal on its own: disabling the cap (or any restore) removes the game's entry from
        // ActiveSessions even though the game keeps running, which used to make later attempts to
        // re-apply a cap silently no-op because ReapplyIfRunning/hotkey targeting thought nothing
        // was running anymore.
        private readonly HashSet<Guid> runningGameIds = new HashSet<Guid>();
        private readonly Dictionary<Guid, int> runningProcessIds = new Dictionary<Guid, int>();
        private readonly Dictionary<Guid, string> gameMonitorIds = new Dictionary<Guid, string>();
        // Presets written to Special K's macros.ini at launch, per game (macro slot N = preset N).
        // Special K only reads macros.ini at startup, so live switching must match THIS list.
        private readonly Dictionary<Guid, List<double>> specialKMacroPresets = new Dictionary<Guid, List<double>>();
        // Newest requested cap per game, the cap Special K currently runs, and which games have a press worker.
        private readonly Dictionary<Guid, double> specialKPendingMacroCap = new Dictionary<Guid, double>();
        private readonly Dictionary<Guid, double> specialKCurrentCap = new Dictionary<Guid, double>();
        private readonly HashSet<Guid> specialKMacroWorkers = new HashSet<Guid>();
        private readonly object specialKMacroLock = new object();
        // Compare both display identity and its active Windows mode: exclusive fullscreen games
        // can change resolution without ever moving their windows to a different monitor.
        private readonly Dictionary<Guid, string> gameDisplayContexts = new Dictionary<Guid, string>();
        private readonly object vulkanDetectionCacheLock = new object();
        private readonly Dictionary<Guid, VulkanDetectionCacheEntry> vulkanDetectionCache =
            new Dictionary<Guid, VulkanDetectionCacheEntry>();
        /// <summary>
        /// Games waiting for Special K to load before the extension stops its own service.
        /// </summary>
        private readonly HashSet<Guid> pendingPostInjectStop = new HashSet<Guid>();
        // Games currently relying on the Special K global service. This prevents one game's
        // injection/exit from stopping it while another game is still launching.
        private readonly HashSet<Guid> managedSpecialKGames = new HashSet<Guid>();
        // A Special K launch that reached the injection stage but did not start.
        // The next launch of that same game can use RTSS when the user enabled fallback.
        private readonly HashSet<Guid> specialKLaunchPendingGames = new HashSet<Guid>();
        private readonly HashSet<Guid> specialKFallbackNextLaunchGames = new HashSet<Guid>();
        private readonly object postInjectLock = new object();

        private static string GetDisplayContext(string deviceName)
        {
            var resolution = MonitorManager.GetCurrentResolution(deviceName);
            return (deviceName ?? string.Empty) + "|" +
                (resolution == null ? "unknown" : resolution.Width + "x" + resolution.Height) + "|" +
                RefreshRateManager.GetCurrentPreciseRefreshRate(deviceName).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        }

        // Used by the game settings UI to list modes for the screen hosting this game.
        public string GetGameMonitorDeviceName(Game game)
        {
            return game != null ? ResolveGameMonitorDeviceName(game.Id) : ResolvePreferredOrPrimaryMonitor();
        }

        /// <summary>Writes the monitor's FPS presets to Special K's Global\macros.ini (before launch).</summary>
        private void SyncSpecialKPresetMacros(Game game, string executablePath)
        {
            if (game == null) return;
            try
            {
                var presets = Settings.GetPresetValuesForMonitor(ResolveGameMonitorDeviceName(game.Id));
                var userHotkeys = Settings.SpecialKMacroHotkeys
                    .Where(h => h.IsAssigned)
                    .Select(h => new KeyValuePair<string, double>(h.MacroKeyText, h.FrameLimit))
                    .ToList();
                specialKMacroPresets[game.Id] = specialKBackend.WritePresetMacros(presets, userHotkeys);
            }
            catch (Exception e) { logger.Warn(e, "Could not write Special K preset macros for " + game.Name + "."); }
        }

        /// <summary>
        /// Requests a live cap change through Special K's preset macro hotkey. Requests are coalesced (the overlay
        /// applies a cap in two steps) and delayed while the game is paused by the game overlay, so only the final
        /// cap is pressed, once, when the game can actually receive it.
        /// </summary>
        private void TriggerSpecialKPresetMacro(Game game, double cap)
        {
            if (game == null) return;
            if (!specialKMacroPresets.ContainsKey(game.Id))
            {
                logger.Info("Special K: no preset macros registered for " + game.Name + "; " + cap + " FPS applies on the next launch.");
                return;
            }
            lock (specialKMacroLock)
            {
                specialKPendingMacroCap[game.Id] = cap;
                if (!specialKMacroWorkers.Add(game.Id)) return; // a worker is already running; it picks up the newest cap
            }
            var gameId = game.Id;
            var gameName = game.Name;
            Task.Run(() => RunSpecialKMacroWorker(gameId, gameName));
        }

        private int ResolveSpecialKInputProcess(Guid gameId, int trackedProcessId)
        {
            try
            {
                var game = playniteApi.Database.Games.Get(gameId);
                var profile = Settings.GetGameProfile(gameId);
                var exe = profile?.LastResolvedExecutable;
                if (string.IsNullOrWhiteSpace(exe)) exe = profile?.ManualExecutablePath;
                string installDir = game?.InstallDirectory;
                if (game != null && !string.IsNullOrWhiteSpace(installDir))
                {
                    try { installDir = playniteApi.ExpandGameVariables(game, installDir); } catch { }
                }
                string detail;
                var pid = MonitorManager.FindGameWindowProcess(trackedProcessId, exe, installDir, out detail);
                logger.Info("Special K: key target pid " + pid + " — " + detail);
                return pid;
            }
            catch { return trackedProcessId; }
        }

        private void RunSpecialKMacroWorker(Guid gameId, string gameName)
        {
            bool exitedCleanly = false;
            try
            {
                while (true)
                {
                    Thread.Sleep(300); // debounce: overlay applies "clear forced Hz" then "set cap"
                    int processId;
                    if (!runningGameIds.Contains(gameId) || !runningProcessIds.TryGetValue(gameId, out processId))
                    {
                        logger.Info("Special K: no running process is tracked for " + gameName + "; the pending cap change was dropped.");
                        lock (specialKMacroLock) { specialKPendingMacroCap.Remove(gameId); specialKMacroWorkers.Remove(gameId); }
                        exitedCleanly = true;
                        return;
                    }

                    // Playnite's started-process id can be a launcher stub without a window: find the real game process.
                    var windowProcessId = ResolveSpecialKInputProcess(gameId, processId);
                    if (windowProcessId != processId)
                    {
                        logger.Info("Special K: " + gameName + " window belongs to pid " + windowProcessId + " (Playnite tracked pid " + processId + ").");
                        processId = windowProcessId;
                    }

                    // The game overlay soft-pauses the game while it is open: wait until it runs again.
                    bool waited = false;
                    var deadline = DateTime.UtcNow.AddSeconds(180);
                    while (runningGameIds.Contains(gameId) && DateTime.UtcNow < deadline &&
                           MonitorManager.IsProcessSuspended(processId))
                    {
                        waited = true;
                        Thread.Sleep(250);
                    }
                    if (waited) Thread.Sleep(600); // let the game wake up and take focus

                    double cap;
                    lock (specialKMacroLock)
                    {
                        if (!specialKPendingMacroCap.TryGetValue(gameId, out cap))
                        {
                            specialKMacroWorkers.Remove(gameId);
                            exitedCleanly = true;
                            return;
                        }
                        specialKPendingMacroCap.Remove(gameId);
                    }

                    double current;
                    bool sameCap;
                    lock (specialKMacroLock) sameCap = specialKCurrentCap.TryGetValue(gameId, out current) && Math.Abs(current - cap) < 0.005;
                    if (sameCap)
                    {
                        logger.Info("Special K: " + gameName + " is already at " + cap + " FPS as far as the extension knows; no key pressed.");
                        continue;
                    }

                    List<double> written;
                    if (!specialKMacroPresets.TryGetValue(gameId, out written)) continue;
                    char key;
                    if (cap <= 0)
                    {
                        key = SpecialKLimiterBackend.DisableMacroKey; // "no cap"
                    }
                    else
                    {
                        var index = SpecialKLimiterBackend.FindPresetIndex(written, cap);
                        if (index < 0)
                        {
                            logger.Info("Special K: " + cap + " FPS is not one of the presets in macros.ini; it applies on the next launch.");
                            continue;
                        }
                        key = SpecialKLimiterBackend.PresetMacroKey(index);
                    }
                    string keyFailure;
                    if (MonitorManager.SendCtrlShiftKeyToProcess(processId, key, out keyFailure))
                    {
                        lock (specialKMacroLock) specialKCurrentCap[gameId] = cap;
                        logger.Info("Special K: pressed Ctrl+Shift+" + key + " for " + gameName + (cap <= 0 ? " (no cap)." : " (" + cap + " FPS)."));
                    }
                    else
                    {
                        logger.Warn("Special K: could not bring " + gameName + " to the front to press Ctrl+Shift+" + key + ": " + keyFailure + ".");
                    }
                }
            }
            catch (Exception e) { logger.Warn("Special K macro worker failed: " + e.Message); }
            finally
            {
                if (!exitedCleanly) lock (specialKMacroLock) { specialKMacroWorkers.Remove(gameId); }
            }
        }

        /// <summary>
        /// The display the user asked this game to open on (per-game choice, else the global
        /// "Preferred monitor for games"), or null when none is set or it is not connected.
        /// </summary>
        private string GetConfiguredGameMonitor(Guid gameId)
        {
            var wanted = Settings.GetGameProfile(gameId)?.PreferredMonitorDeviceName;
            if (string.IsNullOrWhiteSpace(wanted)) wanted = Settings.PreferredGameMonitorDeviceName;
            if (string.IsNullOrWhiteSpace(wanted)) return null;
            var match = MonitorManager.GetDisplays().FirstOrDefault(d =>
                string.Equals(d.DeviceName, wanted, StringComparison.OrdinalIgnoreCase));
            return match?.DeviceName;
        }

        /// <summary>
        /// Watches a freshly started game and moves its window to the preferred display as soon as it
        /// appears (new windows, e.g. launcher then game, are each moved once). Exclusive-fullscreen
        /// games that pick their output themselves may ignore this.
        /// </summary>
        private void StartWindowPlacementWatch(Guid gameId)
        {
            Task.Run(() =>
            {
                try
                {
                    var handled = new HashSet<long>();
                    var game = playniteApi.Database.Games.Get(gameId);
                    var deadline = DateTime.UtcNow.AddSeconds(120);
                    while (DateTime.UtcNow < deadline)
                    {
                        Thread.Sleep(400);
                        int processId;
                        if (!runningGameIds.Contains(gameId)) return;
                        if (!runningProcessIds.TryGetValue(gameId, out processId)) continue;
                        var target = GetConfiguredGameMonitor(gameId);
                        if (string.IsNullOrEmpty(target)) return;
                        string detail;
                        if (MonitorManager.TryMoveProcessWindowToMonitor(processId, target, handled, out detail))
                            logger.Info("FPS Limiter: " + (game?.Name ?? gameId.ToString()) + " " + detail + ".");
                    }
                }
                catch (Exception e) { logger.Warn("FPS Limiter: window placement watch failed: " + e.Message); }
            });
        }

        /// <summary>
        /// Preferred monitor from global settings when still connected; otherwise Windows primary.
        /// </summary>
        private string ResolvePreferredOrPrimaryMonitor()
        {
            var preferred = Settings?.PreferredGameMonitorDeviceName;
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                var connected = MonitorManager.GetDisplays()
                    .Any(d => string.Equals(d.DeviceName, preferred, StringComparison.OrdinalIgnoreCase));
                if (connected)
                    return preferred;
            }
            return MonitorManager.PrimaryDeviceName;
        }

        // Before a game's window is visible: last known display, else preferred monitor, else primary.
        private string ResolveGameMonitorDeviceName(Guid gameId)
        {
            var profile = Settings?.GetGameProfile(gameId);
            var preferredForGame = profile?.PreferredMonitorDeviceName;
            if (!string.IsNullOrWhiteSpace(preferredForGame) &&
                MonitorManager.GetDisplays().Any(d =>
                    string.Equals(d.DeviceName, preferredForGame, StringComparison.OrdinalIgnoreCase)))
                return preferredForGame;

            int processId;
            if (runningProcessIds.TryGetValue(gameId, out processId))
            {
                var detected = MonitorManager.TryGetProcessMonitor(processId);
                if (!string.IsNullOrEmpty(detected))
                {
                    gameMonitorIds[gameId] = detected;
                    return detected;
                }
            }
            // Not running: use the display set to run games (per-game, else global) before the last known one.
            var configured = GetConfiguredGameMonitor(gameId);
            if (!string.IsNullOrEmpty(configured)) return configured;
            string lastKnown;
            if (gameMonitorIds.TryGetValue(gameId, out lastKnown) && !string.IsNullOrEmpty(lastKnown))
                return lastKnown;
            return ResolvePreferredOrPrimaryMonitor();
        }

        /// <summary>Reapply when the game moves monitors OR the display resolution changes.</summary>
        public void UpdateRunningGameMonitors()
        {
            Settings.EnsureDetectedMonitors(CurrentMode);
            foreach (var gameId in runningGameIds.ToList())
            {
                int processId;
                if (!runningProcessIds.TryGetValue(gameId, out processId)) continue;
                var detected = MonitorManager.TryGetProcessMonitor(processId);
                if (string.IsNullOrWhiteSpace(detected)) continue;

                var runningGame = playniteApi.Database.Games.Get(gameId);
                if (runningGame != null) ApplyForcedDisplayMode(runningGame, detected);

                var context = GetDisplayContext(detected);
                string previous;
                if (gameDisplayContexts.TryGetValue(gameId, out previous) &&
                    string.Equals(previous, context, StringComparison.OrdinalIgnoreCase)) continue;

                gameDisplayContexts[gameId] = context;
                gameMonitorIds[gameId] = detected;
                var game = playniteApi.Database.Games.Get(gameId);
                if (game == null) continue;
                logger.Info($"FPS Limiter: {game.Name} display mode changed to {context}; updating its FPS profile.");
                try
                {
                    var profile = Settings.GetGameProfile(gameId);
                    var gameLimit = profile?.GetMode(CurrentMode);
                    var global = Settings.GetGlobalForMonitor(detected, FPSLimiterSettings.IsWindowsGame(game), CurrentMode);
                    var hasOverride = gameLimit != null && gameLimit.Enabled;
                    var hasForcedHz = Settings.PerGameRefreshRateEnabled && (profile?.ForcedRefreshRateHz ?? 0) > 0;
                    if (!hasOverride && !hasForcedHz && (!global.Enabled || global.FrameLimit <= 0) &&
                        (ShouldUseSpecialKBackend(game) || !ResolveFlipModel(profile).HasValue))
                        RestoreActiveSession(game, false);
                    else
                        ReapplyIfRunning(game); // preserves Special K live updates
                }
                catch (Exception e) { logger.Error(e, $"Monitor FPS switch failed for {game.Name}."); }
            }
        }

        public LimiterService(IPlayniteAPI playniteApi, FPSLimiterSettingsViewModel settingsViewModel)
        {
            this.playniteApi = playniteApi;
            this.settingsViewModel = settingsViewModel;
            rtssBackend = new RtssLimiterBackend(settingsViewModel.Settings);
            specialKBackend = new SpecialKLimiterBackend(settingsViewModel.Settings);
            targetResolver = new GameTargetResolver(playniteApi);
        }

        public FPSLimiterSettings Settings => settingsViewModel.Settings;

        public PlayniteUiMode CurrentMode =>
            playniteApi.ApplicationInfo.Mode == ApplicationMode.Fullscreen
                ? PlayniteUiMode.Fullscreen
                : PlayniteUiMode.Desktop;

        /// <summary>Presets of the display set to run games (primary display when none is set or connected).</summary>
        public IEnumerable<double> GetPresets()
        {
            return Settings.GetPresetValuesForMonitor(ResolvePreferredOrPrimaryMonitor());
        }

        /// <summary>Presets of the monitor a game runs on (its last known display, else primary).</summary>
        public IEnumerable<double> GetPresets(Game game)
        {
            return Settings.GetPresetValuesForMonitor(game != null ? ResolveGameMonitorDeviceName(game.Id) : null);
        }

        /// <summary>Presets of one specific display.</summary>
        public IEnumerable<double> GetPresetsForMonitor(string deviceName)
        {
            return Settings.GetPresetValuesForMonitor(deviceName);
        }

        /// <summary>The per-monitor profile (display type, presets, forced mode) for a game's display.</summary>
        public MonitorGlobalSettings GetMonitorProfileFor(Game game)
        {
            return Settings.GetMonitorProfile(game != null ? ResolveGameMonitorDeviceName(game.Id) : null);
        }

        /// <summary>True when the game's display is configured as fixed-refresh (not VRR).</summary>
        public bool IsFixedRefreshDisplay(Game game)
        {
            var monitor = GetMonitorProfileFor(game);
            return monitor != null ? monitor.FixedRefreshDisplayMode : Settings.FixedRefreshDisplayMode;
        }

        private int GetVrrTargetHzFor(Game game)
        {
            var profile = game != null ? Settings.GetGameProfile(game.Id) : null;
            if (profile != null && profile.VrrTargetHzOverride > 0) return profile.VrrTargetHzOverride;
            var monitor = GetMonitorProfileFor(game);
            return monitor != null ? monitor.VrrTargetHz : Settings.VrrTargetHz;
        }

        private double GetVrrThresholdFor(Game game)
        {
            var profile = game != null ? Settings.GetGameProfile(game.Id) : null;
            if (profile != null && profile.VrrFpsThresholdOverride > 0) return profile.VrrFpsThresholdOverride;
            var monitor = GetMonitorProfileFor(game);
            return monitor != null ? monitor.VrrFpsThreshold : Settings.VrrFpsThreshold;
        }

        public void EnsureRtssGlobalDefaults()
        {
            rtssBackend.EnsureGlobalDefaults();
        }

        public string ResolveRtssPath()
        {
            return rtssBackend.ResolveRtssPath();
        }

        public string ResolveSpecialKPath()
        {
            return specialKBackend.ResolveSpecialKRoot();
        }

        /// <summary>
        /// Resolve the effective backend preference for a game (per-game override → global default).
        /// </summary>
        public LimiterBackendPreference ResolveBackendPreference(Game game = null)
        {
            if (game != null)
            {
                var profile = Settings.GetGameProfile(game.Id);
                if (profile != null && profile.Backend != GameBackendPreference.UseGlobal)
                {
                    switch (profile.Backend)
                    {
                        case GameBackendPreference.Rtss:
                            return LimiterBackendPreference.Rtss;
                        case GameBackendPreference.SpecialK:
                            return LimiterBackendPreference.SpecialK;
                        case GameBackendPreference.Auto:
                            return LimiterBackendPreference.Auto;
                    }
                }
            }

            return Settings.DefaultBackend;
        }

        private static bool IsAntiCheatMarker(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var value = name.ToLowerInvariant();
            // Riot Vanguard's driver / tray names are short, so match them at the start of the name only.
            if (value.StartsWith("vgk") || value.StartsWith("vgc")) return true;
            return value.Contains("anticheat") || value.Contains("battleye") ||
                   value.Contains("beclient") || value.Contains("beservice") ||
                   value.Contains("vanguard") ||
                   value.Contains("ricochet") || value.Contains("gameguard") ||
                   value.Contains("nprotect") || value.Contains("xigncode") ||
                   value.Contains("punkbuster") || value.Contains("equ8") ||
                   value.Contains("mhyprot") || value.Contains("faceitclient") ||
                   value.Contains("uncheater");
        }

        // The folder scan is not free and ShouldUseSpecialKBackend runs on every UI refresh, so remember the answer.
        private readonly Dictionary<Guid, KeyValuePair<DateTime, bool>> antiCheatCache =
            new Dictionary<Guid, KeyValuePair<DateTime, bool>>();
        private readonly object antiCheatCacheLock = new object();
        private static readonly TimeSpan AntiCheatCacheLifetime = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Conservative anti-cheat detection based on common component names in the game folder.
        /// It is intentionally bounded so a large library cannot stall launch handling; the result is cached briefly.
        /// </summary>
        public bool IsLikelyAntiCheatGame(Game game)
        {
            if (game == null) return false;
            lock (antiCheatCacheLock)
            {
                KeyValuePair<DateTime, bool> cached;
                if (antiCheatCache.TryGetValue(game.Id, out cached) &&
                    DateTime.UtcNow - cached.Key < AntiCheatCacheLifetime)
                    return cached.Value;
            }

            var found = ScanForAntiCheat(game);
            lock (antiCheatCacheLock)
                antiCheatCache[game.Id] = new KeyValuePair<DateTime, bool>(DateTime.UtcNow, found);
            if (found) logger.Info($"Anti-cheat components found for {game.Name}; Special K will not be injected.");
            return found;
        }

        private bool ScanForAntiCheat(Game game)
        {
            var root = game?.InstallDirectory;
            if (string.IsNullOrWhiteSpace(root)) return false;
            try { root = playniteApi.ExpandGameVariables(game, root); } catch { }
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return false;
            try
            {
                // Breadth-first, a few levels deep (Unreal games keep BattlEye / EAC under <Game>\Binaries\Win64).
                // The budget counts directories as well as entries so one huge asset folder cannot hide the rest.
                const int maxDepth = 4;
                var pending = new Queue<Tuple<string, int>>();
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                pending.Enqueue(Tuple.Create(root, 0));
                int inspected = 0;
                while (pending.Count > 0 && inspected < 20000)
                {
                    var item = pending.Dequeue();
                    if (!visited.Add(item.Item1)) continue;
                    foreach (var entry in Directory.EnumerateFileSystemEntries(item.Item1))
                    {
                        if (++inspected > 20000) break;
                        if (IsAntiCheatMarker(Path.GetFileName(entry))) return true;
                        if (item.Item2 < maxDepth && Directory.Exists(entry))
                            pending.Enqueue(Tuple.Create(entry, item.Item2 + 1));
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "Could not inspect the game folder for anti-cheat components.");
            }
            return false;
        }

        /// <summary>
        /// Starts SKIF when using Special K backend (Selective/Global + stop mode).
        /// </summary>
        public SpecialKInjectionMode ResolveSpecialKInjectionMode(Game game)
        {
            var profile = game != null ? Settings.GetGameProfile(game.Id) : null;
            if (profile != null)
            {
                switch (profile.SpecialKInjection)
                {
                    case GameSpecialKInjectionPreference.Local:
                        return SpecialKInjectionMode.Local;
                    case GameSpecialKInjectionPreference.Selective:
                        return SpecialKInjectionMode.Selective;
                    case GameSpecialKInjectionPreference.Global:
                        // Do not allow a per-game profile to re-enable global injection.
                        return SpecialKInjectionMode.Selective;
                }
            }
            return Settings.SpecialKInjectionMode;
        }

        /// <summary>
        /// Frame cap Special K should start with for this game (game profile, else the global cap), 0 = none.
        /// </summary>
        private double ResolveSpecialKFrameLimit(Game game)
        {
            var profile = Settings.GetGameProfile(game.Id);
            var modeLimit = profile?.GetMode(CurrentMode);
            var windowsGame = FPSLimiterSettings.IsWindowsGame(game);
            var globalForMode = Settings.GetGlobalForMonitor(ResolveGameMonitorDeviceName(game.Id), windowsGame, CurrentMode);
            var hasGameProfile = modeLimit != null && modeLimit.Enabled;
            var useGlobalFallback = !hasGameProfile && globalForMode.Enabled && globalForMode.FrameLimit > 0;
            return hasGameProfile ? modeLimit.FrameLimit
                : (useGlobalFallback ? globalForMode.FrameLimit : 0);
        }

        /// <summary>
        /// The exe the local wrapper goes next to. Steam / Epic / GOG games only have a launcher link before their
        /// first run, so the install folder is searched for the real exe.
        /// </summary>
        private string ResolveLocalWrapperTarget(Game game, GameLimitProfile profile, GameAction sourceAction)
        {
            var target = targetResolver.ResolveLocalInjectionExecutable(game, profile, sourceAction);
            if (!string.IsNullOrWhiteSpace(target))
                logger.Info($"Local Special K: wrapper for {game.Name} goes next to {target}.");
            return target;
        }

        /// <summary>
        /// Local injection: puts the Special K wrapper DLL in the game's exe folder and makes sure the wrapper's own
        /// ini (dxgi.ini ...) sits next to it with the cap and the extension's settings. Returns null on success,
        /// otherwise the reason it is not active (also raised as a notification).
        /// </summary>
        private string EnsureLocalSpecialKWrapper(Game game, string exePath, double frameLimit)
        {
            string problem = null;
            try
            {
                var result = SpecialKLocalWrapper.Install(exePath, ResolveSpecialKBinaryDirectoryPublic());
                if (!result.Ready)
                {
                    problem = result.Message;
                }
                else
                {
                    var profile = Settings.GetOrCreateGameProfile(game.Id);

                    // The wrapper used to be put somewhere else (wrong folder): take that one back first.
                    var oldDll = profile.LocalWrapperPath;
                    var oldIni = profile.LocalWrapperIniPath;
                    var oldDir = !string.IsNullOrWhiteSpace(oldDll) ? Path.GetDirectoryName(oldDll) : null;
                    var newDir = Path.GetDirectoryName(result.DllPath);
                    if (!string.IsNullOrWhiteSpace(oldDir) &&
                        !string.Equals(oldDir.TrimEnd('\\'), newDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    {
                        SpecialKLocalWrapper.Remove(oldDll, profile.LocalWrapperMarkerCreated);
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(oldIni) && File.Exists(oldIni) &&
                                string.Equals(Path.GetDirectoryName(oldIni)?.TrimEnd('\\'), oldDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                                File.Delete(oldIni);
                        }
                        catch { }
                        profile.LocalWrapperPath = null;
                        profile.LocalWrapperIniPath = null;
                        profile.LocalWrapperMarkerCreated = false;
                        logger.Info($"Removed the local Special K wrapper from the wrong folder: {oldDir}");
                    }

                    if (result.Copied) profile.LocalWrapperPath = result.DllPath;
                    // else: a Special K DLL that was already there. Not ours to delete later, but still configured.

                    // A SpecialK.central marker we created in an older version would hide the ini in Profiles\<game>.
                    if (profile.LocalWrapperMarkerCreated)
                    {
                        try
                        {
                            var marker = Path.Combine(Path.GetDirectoryName(exePath) ?? string.Empty, SpecialKLocalWrapper.CentralMarkerName);
                            if (File.Exists(marker)) File.Delete(marker);
                            profile.LocalWrapperMarkerCreated = false;
                        }
                        catch (Exception ex) { logger.Debug(ex, "Could not remove the old SpecialK.central marker."); }
                    }

                    var ini = specialKBackend.EnsureWrapperIni(game, exePath, Path.GetFileName(result.DllPath), frameLimit);
                    if (string.IsNullOrWhiteSpace(ini))
                    {
                        problem = "the wrapper's ini could not be created in the game folder";
                    }
                    else
                    {
                        profile.LocalWrapperIniPath = ini;
                        logger.Info($"Local Special K for {game.Name}: wrapper {result.DllPath}, config {ini}.");
                    }
                    settingsViewModel.SaveSettings();
                }
            }
            catch (UnauthorizedAccessException)
            {
                problem = "the game folder is read-only for Playnite (run Playnite as administrator once, or install the DLL by hand)";
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Local Special K setup failed for {game.Name}.");
                problem = ex.Message;
            }

            if (problem != null)
            {
                logger.Warn($"Local Special K is not active for {game.Name}: {problem}.");
                try
                {
                    playniteApi.Notifications.Add("fpslimiter-sk-local-" + game.Id,
                        "Special K local injection is not active for " + game.Name + ": " + problem + ".",
                        NotificationType.Error);
                }
                catch { }
            }
            return problem;
        }

        /// <summary>
        /// Takes back the local wrapper this extension installed: the DLL together with its ini (and an old
        /// SpecialK.central marker). Returns the folder it was removed from, or null when nothing was removed.
        /// A Special K DLL the user put there themselves is left alone, with its ini.
        /// </summary>
        private string RemoveInstalledLocalWrapper(Game game)
        {
            var profile = Settings.GetGameProfile(game.Id);
            if (profile == null ||
                (string.IsNullOrWhiteSpace(profile.LocalWrapperPath) && string.IsNullOrWhiteSpace(profile.LocalWrapperIniPath)))
                return null;

            var weOwnTheDll = !string.IsNullOrWhiteSpace(profile.LocalWrapperPath);
            var folder = weOwnTheDll ? Path.GetDirectoryName(profile.LocalWrapperPath) : null;

            if (weOwnTheDll &&
                !SpecialKLocalWrapper.Remove(profile.LocalWrapperPath, profile.LocalWrapperMarkerCreated))
                return null; // could not remove it (in use?): keep the record and try again later

            if (weOwnTheDll && !string.IsNullOrWhiteSpace(profile.LocalWrapperIniPath))
            {
                try
                {
                    if (File.Exists(profile.LocalWrapperIniPath))
                    {
                        File.Delete(profile.LocalWrapperIniPath);
                        logger.Info("Removed local Special K ini: " + profile.LocalWrapperIniPath);
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn("Could not remove the local Special K ini " + profile.LocalWrapperIniPath + ": " + ex.Message);
                }
            }

            profile.LocalWrapperPath = null;
            profile.LocalWrapperMarkerCreated = false;
            profile.LocalWrapperIniPath = null;
            settingsViewModel.SaveSettings();
            return folder;
        }

        /// <summary>
        /// Called right after the injection method of games was changed in the UI: installs the wrapper DLL + ini in
        /// the game folder now when the game is on local injection, or takes an installed one back when it is not.
        /// Returns a message for the UI (null when there is nothing to report).
        /// </summary>
        public string ApplyLocalInjectionChoice(Game game, out bool failed)
        {
            failed = false;
            if (game == null) return null;
            try
            {
                if (ResolveSpecialKInjectionMode(game) != SpecialKInjectionMode.Local)
                {
                    var removedFrom = RemoveInstalledLocalWrapper(game);
                    return removedFrom == null
                        ? null
                        : "Special K local DLL and ini removed from " + removedFrom;
                }

                if (!ShouldUseSpecialKBackend(game))
                {
                    // Not on Special K right now; the wrapper is placed at launch when it is.
                    return null;
                }

                var profile = Settings.GetOrCreateGameProfile(game.Id);
                var target = ResolveLocalWrapperTarget(game, profile, null);
                if (string.IsNullOrWhiteSpace(target))
                {
                    failed = true;
                    var msg = "the game's executable could not be found. Set it manually in the game's settings, or start the game once.";
                    try
                    {
                        playniteApi.Notifications.Add("fpslimiter-sk-local-" + game.Id,
                            "Special K local injection is not active for " + game.Name + ": " + msg,
                            NotificationType.Error);
                    }
                    catch { }
                    return msg;
                }

                var frameLimit = ResolveSpecialKFrameLimit(game);
                // Make sure the central SpecialK.ini (the template for the wrapper ini) exists first.
                specialKBackend.EnsureProfileBeforeLaunch(game, target, frameLimit);
                var problem = EnsureLocalSpecialKWrapper(game, target, frameLimit);
                if (problem != null) { failed = true; return problem; }

                profile = Settings.GetOrCreateGameProfile(game.Id);
                profile.LastResolvedExecutable = target;
                settingsViewModel.SaveSettings();
                try { playniteApi.Notifications.Remove("fpslimiter-sk-local-" + game.Id); } catch { }
                return "Special K local injection is set up for " + game.Name + ": " + profile.LocalWrapperIniPath;
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Applying local Special K injection for {game.Name} failed.");
                failed = true;
                return ex.Message;
            }
        }

        public void SetGameSpecialKInjection(IEnumerable<Game> games, GameSpecialKInjectionPreference preference)
        {
            foreach (var game in games)
            {
                Settings.GetOrCreateGameProfile(game.Id).SpecialKInjection = preference;
            }
            settingsViewModel.SaveSettings();
        }

        public void PrepareSpecialKInjection(Game game, GameAction sourceAction = null)
        {
            if (game == null)
            {
                return;
            }

            var useSpecialK = Settings.GetGameProfile(game.Id)?.SkipLimiterInjection != true && ShouldUseSpecialKBackend(game);
            if (!useSpecialK || ResolveSpecialKInjectionMode(game) != SpecialKInjectionMode.Local)
            {
                // No longer local (or not Special K at all): take back a wrapper this extension installed earlier.
                RemoveInstalledLocalWrapper(game);
            }

            if (!useSpecialK)
            {
                return;
            }

            // Write SpecialK.ini before the game starts (no delays).
            try
            {
                var profile = Settings.GetGameProfile(game.Id);
                var frameLimit = ResolveSpecialKFrameLimit(game);

                var isLocal = ResolveSpecialKInjectionMode(game) == SpecialKInjectionMode.Local;
                var target = isLocal
                    ? ResolveLocalWrapperTarget(game, profile, sourceAction)
                    : targetResolver.ResolveTarget(game, profile, sourceAction, null);
                specialKBackend.EnsureProfileBeforeLaunch(game, target, frameLimit);
                if (ResolveSpecialKInjectionMode(game) == SpecialKInjectionMode.Local)
                    EnsureLocalSpecialKWrapper(game, target, frameLimit);
                SyncSpecialKPresetMacros(game, target);
                // Global injection only activates for whitelisted executables (what SKIF does
                // when a game is added to its library). Must be in place before the game starts.
                if (ResolveSpecialKInjectionMode(game) != SpecialKInjectionMode.Local)
                    specialKBackend.EnsureGameWhitelisted(game, target);
                if (!string.IsNullOrWhiteSpace(target))
                {
                    var savedProfile = Settings.GetOrCreateGameProfile(game.Id);
                    savedProfile.LastResolvedExecutable = target;
                    settingsViewModel.SaveSettings();
                }
            }
            catch (Exception e)
            {
                logger.Error(e, $"Pre-launch Special K profile setup failed for {game?.Name}.");
            }

            var mode = ResolveSpecialKInjectionMode(game);
            var stopMode = Settings.SpecialKServiceStopMode;
            var temporary = stopMode == SpecialKServiceStopMode.AfterSuccessfulInjection;

            if (mode == SpecialKInjectionMode.Local)
            {
                lock (postInjectLock)
                    specialKLaunchPendingGames.Add(game.Id);
                logger.Info($"Special K injection mode is Local for {game?.Name} — not starting injection service (local DLL only).");
                return;
            }

            if (mode == SpecialKInjectionMode.Selective || mode == SpecialKInjectionMode.Global)
            {
                bool startedByExtension;
                var kind = specialKBackend.StartInjectionService(temporary, out startedByExtension);
                lock (postInjectLock)
                {
                    if (game != null && kind != SpecialKLimiterBackend.InjectionStartKind.Failed &&
                        Settings.SpecialKServiceStopMode != SpecialKServiceStopMode.Never)
                    {
                        managedSpecialKGames.Add(game.Id);
                        if (temporary)
                            pendingPostInjectStop.Add(game.Id);
                    }
                    if (game != null)
                        // The game may fail because the selected Special K path was
                        // attempted, even when the injector could not start.
                        specialKLaunchPendingGames.Add(game.Id);
                }

                if (kind == SpecialKLimiterBackend.InjectionStartKind.Failed && game != null)
                    logger.Warn($"Special K could not start for {game.Name}; the game will continue without Special K injection.");
                else if (!startedByExtension && kind != SpecialKLimiterBackend.InjectionStartKind.Failed)
                    logger.Info("Using an already-running Special K service; the selected lifetime setting will still be applied.");
            }
        }

        /// <summary>
        /// Records a failed Playnite launch after Special K had been prepared. The
        /// fallback is deliberately deferred to the next launch; it is not used merely
        /// because the injector service could not be started.
        /// </summary>
        public void MarkSpecialKLaunchFailed(Game game)
        {
            if (game == null || !Settings.FallbackToRtssIfSpecialKFails)
                return;

            lock (postInjectLock)
            {
                if (!specialKLaunchPendingGames.Remove(game.Id))
                    return;
                specialKFallbackNextLaunchGames.Add(game.Id);
            }

            logger.Warn($"Special K launch for {game.Name} did not start; the next launch will use RTSS.");
        }

        public void CleanupSpecialKInjection(Game game)
        {
            if (game == null)
                return;

            bool stopService;
            lock (postInjectLock)
            {
                pendingPostInjectStop.Remove(game.Id);
                specialKLaunchPendingGames.Remove(game.Id);
                var wasManaged = managedSpecialKGames.Remove(game.Id);
                stopService = wasManaged && ShouldStopSpecialKService();
            }

            // In "after injection" mode, game exit is also a cleanup fallback if injection
            // never completed. In "when game closes" mode this is the normal stop point.
            if (stopService)
                StopManagedSpecialKService();
        }

        /// <summary>
        /// After the game process exists, poll until Special K is loaded (or a short fallback
        /// delay elapses), then stop the global injection service — mirrors SKIF "Start Temp".
        /// </summary>
        public void BeginPostInjectStopWatch(Game game, int processId)
        {
            if (game == null || processId <= 0)
                return;

            bool watch;
            lock (postInjectLock)
                watch = pendingPostInjectStop.Contains(game.Id);
            if (!watch)
                return;

            var gameId = game.Id;
            var gameName = game.Name;
            Task.Run(() =>
            {
                const int pollMs = 500;
                const int maxWaitMs = 90_000;
                const int fallbackAfterWindowMs = 8_000; // if modules can't be read, stop after window is up
                var sw = Stopwatch.StartNew();
                var windowSeenAt = (TimeSpan?)null;

                while (sw.ElapsedMilliseconds < maxWaitMs)
                {
                    lock (postInjectLock)
                    {
                        if (!pendingPostInjectStop.Contains(gameId))
                            return; // cancelled (game stopped or already handled)
                    }

                    if (!runningGameIds.Contains(gameId))
                    {
                        FinishPostInjectStop(gameId);
                        return;
                    }

                    // Primary signal: SpecialK*.dll is mapped into the game process.
                    if (SpecialKLimiterBackend.IsSpecialKLoadedInProcess(processId))
                    {
                        logger.Info($"Special K detected in {gameName} (pid {processId}) after {sw.ElapsedMilliseconds} ms — stopping injection service.");
                        FinishPostInjectStop(gameId);
                        return;
                    }

                    // Fallback: process has a main window for a while (modules often unreadable cross-bitness).
                    try
                    {
                        using (var p = Process.GetProcessById(processId))
                        {
                            if (p.MainWindowHandle != IntPtr.Zero)
                            {
                                if (windowSeenAt == null)
                                    windowSeenAt = sw.Elapsed;
                                else if ((sw.Elapsed - windowSeenAt.Value).TotalMilliseconds >= fallbackAfterWindowMs)
                                {
                                    logger.Info($"Special K module not readable for {gameName}; stopping injection service after window was up {fallbackAfterWindowMs} ms.");
                                    FinishPostInjectStop(gameId);
                                    return;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // process exited
                        FinishPostInjectStop(gameId);
                        return;
                    }

                    Thread.Sleep(pollMs);
                }

                logger.Warn($"Timed out waiting for Special K inject into {gameName}; stopping the service to avoid leaving it running.");
                // Avoid leaving a background service orphaned indefinitely if injection
                // never occurs.
                FinishPostInjectStop(gameId);
            });
        }

        private void FinishPostInjectStop(Guid gameId)
        {
            bool stopService;
            lock (postInjectLock)
            {
                pendingPostInjectStop.Remove(gameId);
                var wasManaged = managedSpecialKGames.Remove(gameId);
                stopService = wasManaged && ShouldStopSpecialKService();
            }

            if (stopService)
                StopManagedSpecialKService();
        }

        private void StopManagedSpecialKService()
        {
            try
            {
                specialKBackend.StopInjectionService();
            }
            catch (Exception e)
            {
                logger.Error(e, "Stopping the managed Special K service failed.");
            }
        }

        // Caller must hold postInjectLock.
        private bool ShouldStopSpecialKService()
        {
            if (managedSpecialKGames.Count > 0 ||
                Settings.SpecialKServiceStopMode == SpecialKServiceStopMode.Never)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// True when "Don't use Special K for Vulkan games" is on and this game is
        /// detected as native Vulkan / DXVK. Special K is not offered for such games.
        /// </summary>
        public bool IsSpecialKHiddenForGame(Game game)
        {
            return game != null && Settings.DontUseSpecialKForVulkanGames && IsVulkanOrDxvkGame(game);
        }

        public void ToggleGameBackend(IEnumerable<Game> games)
        {
            foreach (var game in games)
            {
                if (IsSpecialKHiddenForGame(game)) continue;
                var current = ResolveBackendPreference(game);
                // Resolve effective RTSS vs SpecialK (Auto counts as SpecialK when SK would be used).
                var usingSk = ShouldUseSpecialKBackend(game);
                var next = usingSk ? GameBackendPreference.Rtss : GameBackendPreference.SpecialK;
                var profile = Settings.GetOrCreateGameProfile(game.Id);
                profile.Backend = next;
            }

            settingsViewModel.SaveSettings();
            foreach (var game in games)
            {
                ReapplyIfRunning(game);
            }
        }

        public string GetEffectiveBackendLabel(Game game)
        {
            return ShouldUseSpecialKBackend(game) ? "Special K" : "RTSS";
        }

        public void InvalidateVulkanDetectionCache()
        {
            lock (vulkanDetectionCacheLock)
            {
                vulkanDetectionCache.Clear();
            }
        }

        private bool IsVulkanOrDxvkGame(Game game)
        {
            if (game == null) return false;

            var now = DateTime.UtcNow;
            lock (vulkanDetectionCacheLock)
            {
                VulkanDetectionCacheEntry cached;
                if (vulkanDetectionCache.TryGetValue(game.Id, out cached) &&
                    now - cached.CheckedAtUtc < VulkanDetectionCacheDuration)
                {
                    return cached.IsVulkanOrDxvk;
                }
            }

            var detected = IsDxvkManagerEnabled(game.Id) || ImportsVulkan(game);
            lock (vulkanDetectionCacheLock)
            {
                vulkanDetectionCache[game.Id] = new VulkanDetectionCacheEntry
                {
                    CheckedAtUtc = now,
                    IsVulkanOrDxvk = detected
                };
            }

            return detected;
        }

        /// <summary>
        /// Reads DXVK Manager's per-game enabled flag, if that extension is installed.
        /// This is deliberately a read-only, optional integration; no dependency on its DLL.
        /// </summary>
        private bool IsDxvkManagerEnabled(Guid gameId)
        {
            try
            {
                var extensionsDataPath = playniteApi?.Paths?.ExtensionsDataPath;
                if (string.IsNullOrWhiteSpace(extensionsDataPath)) return false;

                var configPath = Path.Combine(
                    extensionsDataPath,
                    DxvkManagerPluginId,
                    "dxvk_game_configs.json");
                if (!File.Exists(configPath))
                {
                    // Support alternate Playnite builds that choose a folder name instead
                    // of the extension GUID for plugin user data.
                    configPath = Directory.Exists(extensionsDataPath)
                        ? Directory.EnumerateDirectories(extensionsDataPath)
                            .Select(directory => Path.Combine(directory, "dxvk_game_configs.json"))
                            .FirstOrDefault(File.Exists)
                        : null;
                }
                if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath)) return false;

                var serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
                var configs = serializer.DeserializeObject(File.ReadAllText(configPath))
                    as IDictionary<string, object>;
                if (configs == null) return false;

                object gameConfig;
                if (!configs.TryGetValue(gameId.ToString(), out gameConfig))
                {
                    var key = configs.Keys.FirstOrDefault(k =>
                    {
                        Guid parsed;
                        return Guid.TryParse(k, out parsed) && parsed == gameId;
                    });
                    if (key == null || !configs.TryGetValue(key, out gameConfig)) return false;
                }

                var values = gameConfig as IDictionary<string, object>;
                if (values == null) return false;

                object enabled;
                if (!values.TryGetValue("Enabled", out enabled))
                {
                    var key = values.Keys.FirstOrDefault(k =>
                        string.Equals(k, "Enabled", StringComparison.OrdinalIgnoreCase));
                    if (key == null || !values.TryGetValue(key, out enabled)) return false;
                }

                if (enabled is bool) return (bool)enabled;
                bool parsedBool;
                return bool.TryParse(Convert.ToString(enabled), out parsedBool) && parsedBool;
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "Could not read DXVK Manager's game configuration.");
                return false;
            }
        }

        private bool ImportsVulkan(Game game)
        {
            try
            {
                var profile = Settings.GetGameProfile(game.Id);
                var playAction = game.GameActions?.FirstOrDefault(action => action != null && action.IsPlayAction);
                var executable = targetResolver.ResolveTarget(game, profile, playAction, null);

                // Launch arguments, DXVK, the emulator's own renderer setting, Vulkan loaded at run time, imports, game DLLs.
                string reason;
                var vulkan = VulkanGameDetector.IsVulkan(playniteApi, game, executable, out reason);
                logger.Info($"Special K Vulkan check for \"{game.Name}\": {(vulkan ? "Vulkan" : "not Vulkan")} " +
                            $"(exe: {executable ?? "not resolved"}; {reason ?? "no reason"}).");
                return vulkan;
            }
            catch (Exception ex)
            {
                logger.Debug(ex, $"Could not detect Vulkan for {game.Name}.");
                return false;
            }
        }

        private static bool IsVulkanExecutable(string executable)
        {
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return false;

            try
            {
                // Import tables are normally near the start of a PE image. Bound the read so
                // checking a very large game executable cannot allocate unbounded memory.
                const int maxBytesToRead = 96 * 1024 * 1024;
                byte[] data;
                using (var stream = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var length = (int)Math.Min(stream.Length, maxBytesToRead);
                    if (length < 256) return false;
                    data = new byte[length];
                    var totalRead = 0;
                    while (totalRead < data.Length)
                    {
                        var read = stream.Read(data, totalRead, data.Length - totalRead);
                        if (read <= 0) break;
                        totalRead += read;
                    }
                    if (totalRead < data.Length) Array.Resize(ref data, totalRead);
                }

                if (data.Length < 64 || data[0] != (byte)'M' || data[1] != (byte)'Z') return false;
                var peOffset = ReadInt32(data, 0x3c);
                if (!HasRange(data, peOffset, 24) ||
                    data[peOffset] != (byte)'P' || data[peOffset + 1] != (byte)'E' ||
                    data[peOffset + 2] != 0 || data[peOffset + 3] != 0)
                {
                    return false;
                }

                var sectionCount = ReadUInt16(data, peOffset + 6);
                var optionalHeaderSize = ReadUInt16(data, peOffset + 20);
                var optionalHeader = peOffset + 24;
                if (!HasRange(data, optionalHeader, optionalHeaderSize) || sectionCount == 0) return false;

                var magic = ReadUInt16(data, optionalHeader);
                var isPe32Plus = magic == 0x20b;
                if (!isPe32Plus && magic != 0x10b) return false;

                var imageBase = isPe32Plus
                    ? ReadUInt64(data, optionalHeader + 24)
                    : (ulong)ReadUInt32(data, optionalHeader + 28);
                var dataDirectoryOffset = optionalHeader + (isPe32Plus ? 112 : 96);
                if (!HasRange(data, dataDirectoryOffset, 14 * 8)) return false;

                var sectionTable = optionalHeader + optionalHeaderSize;
                if (!HasRange(data, sectionTable, sectionCount * 40)) return false;

                var importRva = ReadUInt32(data, dataDirectoryOffset + 8); // IMAGE_DIRECTORY_ENTRY_IMPORT
                if (HasVulkanImport(data, sectionTable, sectionCount, importRva, 20, 12, false, imageBase))
                    return true;

                var delayImportRva = ReadUInt32(data, dataDirectoryOffset + (13 * 8));
                return HasVulkanImport(data, sectionTable, sectionCount, delayImportRva, 32, 4, true, imageBase);
            }
            catch (Exception ex)
            {
                logger.Debug(ex, $"Could not inspect executable imports: {ex.Message}");
                return false;
            }
        }

        private static bool HasVulkanImport(
            byte[] data,
            int sectionTable,
            int sectionCount,
            uint directoryRva,
            int descriptorSize,
            int nameOffset,
            bool delayImport,
            ulong imageBase)
        {
            if (directoryRva == 0) return false;
            var descriptorOffset = RvaToFileOffset(data, sectionTable, sectionCount, directoryRva);
            if (!descriptorOffset.HasValue) return false;

            for (var i = 0; i < 4096; i++)
            {
                var current = descriptorOffset.Value + (i * descriptorSize);
                if (!HasRange(data, current, descriptorSize)) return false;

                var nameRva = ReadUInt32(data, current + nameOffset);
                if (nameRva == 0) return false;
                if (delayImport)
                {
                    // Delay descriptor's Attributes bit 0 means the name is already an RVA.
                    var attributes = ReadUInt32(data, current);
                    if ((attributes & 1) == 0)
                    {
                        if (imageBase > uint.MaxValue || nameRva < imageBase) continue;
                        nameRva -= (uint)imageBase;
                    }
                }

                var nameOffsetInFile = RvaToFileOffset(data, sectionTable, sectionCount, nameRva);
                if (!nameOffsetInFile.HasValue) continue;
                var name = ReadNullTerminatedAscii(data, nameOffsetInFile.Value, 260);
                if (string.Equals(name, "vulkan-1.dll", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "vulkan.dll", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static int? RvaToFileOffset(byte[] data, int sectionTable, int sectionCount, uint rva)
        {
            for (var i = 0; i < sectionCount; i++)
            {
                var section = sectionTable + (i * 40);
                if (!HasRange(data, section, 40)) return null;

                var virtualSize = ReadUInt32(data, section + 8);
                var virtualAddress = ReadUInt32(data, section + 12);
                var rawSize = ReadUInt32(data, section + 16);
                var rawAddress = ReadUInt32(data, section + 20);
                var mappedSize = Math.Max(virtualSize, rawSize);
                if (rva < virtualAddress || (ulong)rva >= (ulong)virtualAddress + mappedSize) continue;

                var offset = (ulong)rawAddress + (rva - virtualAddress);
                return offset < (ulong)data.Length ? (int)offset : (int?)null;
            }

            return null;
        }

        private static string ReadNullTerminatedAscii(byte[] data, int offset, int maxLength)
        {
            if (!HasRange(data, offset, 1)) return string.Empty;
            var end = offset;
            var limit = Math.Min(data.Length, offset + maxLength);
            while (end < limit && data[end] != 0) end++;
            return end == offset ? string.Empty : System.Text.Encoding.ASCII.GetString(data, offset, end - offset);
        }

        private static bool HasRange(byte[] data, int offset, int length)
        {
            return offset >= 0 && length >= 0 && offset <= data.Length && length <= data.Length - offset;
        }

        private static ushort ReadUInt16(byte[] data, int offset) =>
            HasRange(data, offset, 2) ? BitConverter.ToUInt16(data, offset) : (ushort)0;

        private static uint ReadUInt32(byte[] data, int offset) =>
            HasRange(data, offset, 4) ? BitConverter.ToUInt32(data, offset) : 0;

        private static ulong ReadUInt64(byte[] data, int offset) =>
            HasRange(data, offset, 8) ? BitConverter.ToUInt64(data, offset) : 0;

        private static int ReadInt32(byte[] data, int offset) =>
            HasRange(data, offset, 4) ? BitConverter.ToInt32(data, offset) : -1;

        public bool ShouldUseSpecialKBackend(Game game = null)
        {
            // One-shot RTSS fallback after a failed Special K launch. It holds for the whole next session
            // (it is only consumed when that session ends), and is ignored once the option is switched off.
            if (game != null && Settings.FallbackToRtssIfSpecialKFails)
            {
                bool fallbackActive;
                lock (postInjectLock) fallbackActive = specialKFallbackNextLaunchGames.Contains(game.Id);
                if (fallbackActive) return false;
            }
            if (IsSpecialKHiddenForGame(game))
                return false;
            if (game != null && Settings.NeverInjectSpecialKForAntiCheatGames && IsLikelyAntiCheatGame(game))
                return false;

            var preference = ResolveBackendPreference(game);
            switch (preference)
            {
                case LimiterBackendPreference.Rtss:
                    return false;
                case LimiterBackendPreference.SpecialK:
                    // No Helper required — only need a resolvable Special K install.
                    return !string.IsNullOrWhiteSpace(specialKBackend.ResolveSpecialKRoot());
                default:
                    if (!Settings.PreferSpecialKWhenAvailable)
                    {
                        return false;
                    }

                    if (game != null)
                    {
                        return specialKBackend.IsSpecialKEnabledForGame(game);
                    }

                    return specialKBackend.IsSpecialKServiceRunning();
            }
        }

        public GameBackendPreference GetGameBackendPreference(Game game)
        {
            var profile = Settings.GetGameProfile(game.Id);
            return profile?.Backend ?? GameBackendPreference.UseGlobal;
        }

        public void SetGameBackendPreference(IEnumerable<Game> games, GameBackendPreference preference)
        {
            foreach (var game in games)
            {
                var profile = Settings.GetOrCreateGameProfile(game.Id);
                profile.Backend = preference;
                if (preference == GameBackendPreference.UseGlobal && !profile.HasAnyLimit() &&
                    string.IsNullOrWhiteSpace(profile.ManualExecutablePath) &&
                    string.IsNullOrWhiteSpace(profile.LastResolvedExecutable))
                {
                    Settings.RemoveEmptyGameProfile(game.Id);
                }
            }

            settingsViewModel.SaveSettings();

            foreach (var game in games)
            {
                // Special K is now used for this game: create SpecialK.ini (or fill an empty one) right away.
                if (ShouldUseSpecialKBackend(game))
                    EnsureSpecialKIniForGamePublic(game);
                ReapplyIfRunning(game);
            }
        }

        public SpecialKFrameLimiterMode ResolveSpecialKLimiterMode(Game game = null)
        {
            if (game != null)
            {
                var profile = Settings.GetGameProfile(game.Id);
                if (profile != null && profile.HasSpecialKLimiterModeOverride)
                {
                    return profile.SpecialKLimiterMode;
                }

                // No per-game override: inherit the global Special K limiter behavior.
                return Settings.SpecialKFrameLimiterMode;
            }

            return Settings.SpecialKFrameLimiterMode;
        }

        public void SetGameSpecialKLimiterMode(IEnumerable<Game> games, SpecialKFrameLimiterMode mode, bool useGlobal)
        {
            foreach (var game in games)
            {
                var profile = Settings.GetOrCreateGameProfile(game.Id);
                if (useGlobal)
                {
                    profile.HasSpecialKLimiterModeOverride = false;
                }
                else
                {
                    profile.HasSpecialKLimiterModeOverride = true;
                    profile.SpecialKLimiterMode = mode;
                }
            }

            if (!useGlobal && mode == SpecialKFrameLimiterMode.LatentSync)
                {
                    foreach (var game in games)
                    {
                        var hz = GetRefreshRateForFpsMatch(game);
                        if (hz > 0)
                        {
                            var ml = Settings.GetOrCreateGameProfile(game.Id).GetMode(CurrentMode);
                            ml.Enabled = true;
                            ml.FrameLimit = hz;
                        }
                    }
                }
                settingsViewModel.SaveSettings();
            foreach (var game in games)
            {
                ReapplyIfRunning(game);
            }
        }

        public void SetGameSpecialKReflexMode(IEnumerable<Game> games, SpecialKReflexMode mode, bool useGlobal)
        {
            foreach (var game in games)
            {
                var profile = Settings.GetOrCreateGameProfile(game.Id);
                if (useGlobal)
                {
                    profile.HasSpecialKReflexModeOverride = false;
                }
                else
                {
                    profile.HasSpecialKReflexModeOverride = true;
                    profile.SpecialKReflexModeOverride = mode;
                }
            }
            settingsViewModel.SaveSettings();
            foreach (var game in games)
                ReapplyIfRunning(game);
        }

        public void SetGameSpecialKReflexTrigger(IEnumerable<Game> games, SpecialKReflexTrigger trigger, bool useGlobal)
        {
            foreach (var game in games)
            {
                var profile = Settings.GetOrCreateGameProfile(game.Id);
                if (useGlobal)
                {
                    profile.HasSpecialKReflexTriggerOverride = false;
                }
                else
                {
                    profile.HasSpecialKReflexTriggerOverride = true;
                    profile.SpecialKReflexTriggerOverride = trigger;
                }
            }
            settingsViewModel.SaveSettings();
            foreach (var game in games)
                ReapplyIfRunning(game);
        }

        public void SetGameSpecialKEnergy(IEnumerable<Game> games, SpecialKEnergyEfficiency energy, bool useGlobal, double? busyPct = null, double? busyBias = null)
        {
            foreach (var game in games)
            {
                var profile = Settings.GetOrCreateGameProfile(game.Id);
                if (useGlobal)
                {
                    profile.HasSpecialKEnergyOverride = false;
                }
                else
                {
                    profile.HasSpecialKEnergyOverride = true;
                    profile.SpecialKEnergyOverride = energy;
                    if (busyPct.HasValue) profile.SpecialKBusyWaitPercentOverride = busyPct.Value;
                    if (busyBias.HasValue) profile.SpecialKBusyWaitBiasOverride = busyBias.Value;
                }
            }
            settingsViewModel.SaveSettings();
            foreach (var game in games)
                ReapplyIfRunning(game);
        }

        /// <summary>
        /// Latent Sync (SK) and Scanline Sync (RTSS) work best when FPS == display Hz.
        /// </summary>
        private bool ShouldMatchFpsToRefreshRate(Game game, FpsSyncMode syncMode)
        {
            if (syncMode == FpsSyncMode.ScanlineSync || syncMode == FpsSyncMode.ScanlineSyncHalf)
                return true;
            if (ShouldUseSpecialKBackend(game))
            {
                var skMode = ResolveSpecialKLimiterMode(game);
                if (skMode == SpecialKFrameLimiterMode.LatentSync)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Preferred Hz for FPS matching: forced per-game rate, else VRR target, else current display Hz.
        /// </summary>
        private double GetRefreshRateForFpsMatch(Game game)
        {
            var profile = game != null ? Settings.GetGameProfile(game.Id) : null;
            var monitor = game != null ? ResolveGameMonitorDeviceName(game.Id) : MonitorManager.PrimaryDeviceName;

            // Exact user override wins when a custom AMD/NVIDIA timing isn't exposed by Windows.
            if (profile != null && profile.ExactSyncRefreshHz >= 10 && profile.ExactSyncRefreshHz <= 1000)
                return profile.ExactSyncRefreshHz;

            // Global exact timing is saved independently for each nominal refresh rate.
            // Do not apply a selected mode's override at a different refresh rate.
            // A per-game forced nominal refresh is used if selected; otherwise check the
            // VRR nominal target, then the active mode on this game's monitor.
            var nominalModeHz = profile != null && Settings.PerGameRefreshRateEnabled && profile.ForcedRefreshRateHz > 0
                ? profile.ForcedRefreshRateHz
                : IsVrrEnabledForGame(game) && GetVrrTargetHzFor(game) > 0
                    ? GetVrrTargetHzFor(game)
                    : MonitorManager.GetRefreshRate(monitor);
            var monitorExactHz = Settings.GetExactSyncRefreshHzForMonitor(monitor, nominalModeHz);
            if (monitorExactHz >= 10 && monitorExactHz <= 1000 &&
                !double.IsNaN(monitorExactHz) && !double.IsInfinity(monitorExactHz))
                return monitorExactHz;

            // Forced refresh selection is a NOMINAL Windows mode (int), not the FPS precision.
            // If its mode is currently active, use its precise CCD vSync rate instead.
            if (profile != null && profile.ForcedRefreshRateHz > 0)
                return RefreshRateManager.GetPreciseCapForNominalMode(profile.ForcedRefreshRateHz, monitor);

            if (IsVrrEnabledForGame(game) && GetVrrTargetHzFor(game) > 0)
            {
                return RefreshRateManager.GetPreciseCapForNominalMode(GetVrrTargetHzFor(game), monitor);
            }
            return RefreshRateManager.GetCurrentPreciseRefreshRate(monitor);
        }

        public void SetDefaultSpecialKLimiterMode(SpecialKFrameLimiterMode mode)
        {
            Settings.SpecialKFrameLimiterMode = mode;
            settingsViewModel.SaveSettings();
            ReapplyAllRunning();
        }

        public void SetDefaultBackend(LimiterBackendPreference preference)
        {
            Settings.DefaultBackend = preference;
            settingsViewModel.SaveSettings();
            ReapplyAllRunning();
        }

        /// <summary>Windows-game global for the current UI mode (legacy convenience).</summary>
        public bool IsGlobalLimitEnabled => IsGlobalLimitEnabledFor(true);

        public double GlobalFrameLimit => GlobalFrameLimitFor(true);

        public FpsSyncMode GlobalSyncMode => GlobalSyncModeFor(true);

        public ReflexMarkerMode GlobalReflexMarkers => GlobalReflexMarkersFor(true);

        public bool IsGlobalLimitEnabledFor(bool windowsGames, string monitor = null) =>
            Settings.GetGlobalForMonitor(monitor ?? MonitorManager.PrimaryDeviceName, windowsGames, CurrentMode).Enabled;

        public double GlobalFrameLimitFor(bool windowsGames, string monitor = null) =>
            Settings.GetGlobalForMonitor(monitor ?? MonitorManager.PrimaryDeviceName, windowsGames, CurrentMode).FrameLimit;

        public FpsSyncMode GlobalSyncModeFor(bool windowsGames, string monitor = null) =>
            Settings.GetGlobalForMonitor(monitor ?? MonitorManager.PrimaryDeviceName, windowsGames, CurrentMode).SyncMode;

        public ReflexMarkerMode GlobalReflexMarkersFor(bool windowsGames, string monitor = null) =>
            Settings.GetGlobalForMonitor(monitor ?? MonitorManager.PrimaryDeviceName, windowsGames, CurrentMode).ReflexMarkers;

        public bool IsGlobalLimitEnabledForGame(Game game) =>
            Settings.GetGlobalForMonitor(ResolveGameMonitorDeviceName(game.Id), FPSLimiterSettings.IsWindowsGame(game), CurrentMode).Enabled;

        public double GlobalFrameLimitForGame(Game game) =>
            Settings.GetGlobalForMonitor(ResolveGameMonitorDeviceName(game.Id), FPSLimiterSettings.IsWindowsGame(game), CurrentMode).FrameLimit;

        public void SetGlobalLimit(double frameLimit, bool windowsGames, string monitor = null)
        {
            var global = Settings.GetGlobalForMonitor(monitor ?? MonitorManager.PrimaryDeviceName, windowsGames, CurrentMode);
            global.Enabled = true;
            global.FrameLimit = frameLimit;
            settingsViewModel.SaveSettings();
            ReapplyAllRunning();
        }

        /// <summary>Sets the Windows-game global (backward-compatible entry point).</summary>
        public void SetGlobalLimit(double frameLimit)
        {
            SetGlobalLimit(frameLimit, windowsGames: true);
        }

        public void DisableGlobalLimit(bool windowsGames, string monitor = null)
        {
            var global = Settings.GetGlobalForMonitor(monitor ?? MonitorManager.PrimaryDeviceName, windowsGames, CurrentMode);
            global.Enabled = false;
            global.FrameLimit = 0;
            settingsViewModel.SaveSettings();
            foreach (var gameId in runningGameIds.ToList())
            {
                var game = playniteApi.Database.Games.Get(gameId);
                if (game != null && FPSLimiterSettings.IsWindowsGame(game) == windowsGames &&
                    string.Equals(ResolveGameMonitorDeviceName(gameId), monitor ?? MonitorManager.PrimaryDeviceName, StringComparison.OrdinalIgnoreCase) &&
                    !(Settings.GetGameProfile(gameId)?.GetMode(CurrentMode)?.Enabled ?? false))
                    RestoreActiveSession(game, false);
            }
            ReapplyAllRunning();
        }

        public void DisableGlobalLimit()
        {
            DisableGlobalLimit(windowsGames: true);
        }

        public void SetGlobalSyncMode(FpsSyncMode syncMode, bool windowsGames, string monitor = null)
        {
            Settings.GetGlobalForMonitor(monitor ?? MonitorManager.PrimaryDeviceName, windowsGames, CurrentMode).SyncMode = syncMode;
            settingsViewModel.SaveSettings();
            foreach (var id in runningGameIds.ToList())
            {
                var running = playniteApi.Database.Games.Get(id);
                if (running != null) RefreshFreeSyncForGame(running);
            }
            ReapplyAllRunning();
        }

        public void SetGlobalSyncMode(FpsSyncMode syncMode)
        {
            SetGlobalSyncMode(syncMode, windowsGames: true);
        }

        public void SetGlobalReflexMarkers(ReflexMarkerMode mode, bool windowsGames, string monitor = null)
        {
            Settings.GetGlobalForMonitor(monitor ?? MonitorManager.PrimaryDeviceName, windowsGames, CurrentMode).ReflexMarkers = mode;
            settingsViewModel.SaveSettings();
            ReapplyAllRunning();
        }

        public void SetGlobalReflexMarkers(ReflexMarkerMode mode)
        {
            SetGlobalReflexMarkers(mode, windowsGames: true);
        }

        /// <summary>Apply edited global settings to games already running.</summary>
        public void ApplyEditedGlobalSettings()
        {
            foreach (var gameId in runningGameIds.ToList())
            {
                var game = playniteApi.Database.Games.Get(gameId);
                if (game == null) continue;
                var profile = Settings.GetGameProfile(gameId);
                var gameLimit = profile?.GetMode(CurrentMode);
                var global = Settings.GetGlobalForMonitor(ResolveGameMonitorDeviceName(gameId),
                    FPSLimiterSettings.IsWindowsGame(game), CurrentMode);
                if (!(gameLimit != null && gameLimit.Enabled) &&
                    (!global.Enabled || global.FrameLimit <= 0) &&
                    !(Settings.PerGameRefreshRateEnabled && (profile?.ForcedRefreshRateHz ?? 0) > 0) &&
                    (ShouldUseSpecialKBackend(game) || !ResolveFlipModel(profile).HasValue))
                    RestoreActiveSession(game, false);
                else
                    ReapplyIfRunning(game);
            }
        }

        /// <summary>
        /// Re-applies the limiter for every currently running game so a Global setting change
        /// (limit, sync mode, or Reflex markers) takes effect immediately instead of waiting for
        /// the next launch. Games with their own explicit per-game override effectively no-op here
        /// (TryApplyForGame resolves the same values it already applied), so this is safe to call
        /// unconditionally rather than trying to track which running games actually use the Global
        /// fallback profile.
        /// </summary>
        private void ReapplyAllRunning()
        {
            foreach (var gameId in runningGameIds.ToList())
            {
                var game = playniteApi.Database.Games.Get(gameId);
                if (game != null)
                {
                    ReapplyIfRunning(game);
                }
            }
        }

        public GameLimitProfile GetGameProfile(Game game)
        {
            return Settings.GetGameProfile(game.Id);
        }

        public void SaveSettingsPublic()
        {
            settingsViewModel.SaveSettings();
        }

        public void ApplySpecialKCompatibilityToGamePublic(Game game)
        {
            try
            {
                specialKBackend.ApplySpecialKCompatibilityToGame(game);
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to apply Special K compatibility settings for {game?.Name}.");
            }
        }

        public string FindSpecialKIniPathPublic(Game game)
        {
            try { return specialKBackend.FindSpecialKIniPath(game); }
            catch { return null; }
        }

        /// <summary>
        /// Makes sure the game's SpecialK.ini exists and is not empty. A missing or empty file is created
        /// with every global Special K option from the settings (this game's overrides applied).
        /// Returns the ini path, or null when it could not be created.
        /// </summary>
        public string EnsureSpecialKIniForGamePublic(Game game)
        {
            if (game == null) return null;
            try
            {
                var profile = Settings.GetGameProfile(game.Id);
                var modeLimit = profile?.GetMode(CurrentMode);
                var windowsGame = FPSLimiterSettings.IsWindowsGame(game);
                var globalForMode = Settings.GetGlobalForMonitor(ResolveGameMonitorDeviceName(game.Id), windowsGame, CurrentMode);
                var hasGameProfile = modeLimit != null && modeLimit.Enabled;
                var useGlobalFallback = !hasGameProfile && globalForMode.Enabled && globalForMode.FrameLimit > 0;
                var frameLimit = hasGameProfile ? modeLimit.FrameLimit
                    : (useGlobalFallback ? globalForMode.FrameLimit : 0);

                var target = targetResolver.ResolveTarget(game, profile, null, null);
                return specialKBackend.EnsureGameIni(game, target, frameLimit, out _);
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to create SpecialK.ini for {game?.Name}.");
                return null;
            }
        }

        public void ApplySteamLogSilentToGamePublic(Game game, bool value)
        {
            try
            {
                specialKBackend.ApplySteamLogSilentToGame(game, value);
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to apply Special K Steam.Log setting for {game?.Name}.");
            }
        }

        public string ResolveSpecialKRootPublic()
        {
            return specialKBackend.ResolveSpecialKRoot();
        }

        public string ResolveSpecialKBinaryDirectoryPublic()
        {
            return specialKBackend.ResolveSpecialKBinaryDirectory();
        }

        /// <summary>FPS cap currently stored by Special K for this game (changes made in SK's control panel included), or null.</summary>
        public double? ReadSpecialKLiveTargetFps(Game game)
        {
            if (game == null || !ShouldUseSpecialKBackend(game)) return null;
            return specialKBackend.ReadCurrentTargetFps(game);
        }

        public bool HasRunningGamesPublic()
        {
            return runningGameIds.Count > 0;
        }

        public bool IsSpecialKServiceHostRunningPublic()
        {
            return specialKBackend.IsInjectionServiceHostRunning();
        }

        public bool StopSpecialKServicePublic()
        {
            return specialKBackend.StopInjectionService();
        }

        /// <summary>
        /// Write Global\default_SpecialK.ini from current extension toggles so SK applies them
        /// on the next new game profile.
        /// </summary>
        public void SyncSpecialKGlobalDefaults()
        {
            try
            {
                specialKBackend.WriteGlobalDefaultSpecialKIni();
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to sync Special K Global\\default_SpecialK.ini");
            }
        }

        public void ReapplyIfRunningPublic(Game game)
        {
            ReapplyIfRunning(game);
        }

        /// <summary>
        /// Shared entry point for the overlay and other integrations. Scanline target is a single
        /// FPS Limiter setting; save it here and let LimiterService re-apply active sessions so the
        /// RTSS profile, active-session snapshot, and both UIs stay consistent.
        /// </summary>
        public bool SetRtssScanlineSyncTargetPublic(Game game, int value)
        {
            value = Math.Max(FPSLimiterSettings.MinRtssScanlineSyncTarget,
                Math.Min(FPSLimiterSettings.MaxRtssScanlineSyncTarget, value));
            Settings.RtssScanlineSyncTarget = value;
            settingsViewModel.SaveSettings();

            // Reapply all tracked games. The normal same-settings fast path deliberately checks
            // AppliedScanlineTarget, so Scanline sessions reload when this value alone changes.
            ReapplyAllRunning();

            // If Playnite started the game before our lifecycle hook registered it, an active
            // limiter session still gives us a safe, authoritative target to re-apply.
            var untrackedSessionIds = Settings.ActiveSessions
                .Where(session => session != null && !runningGameIds.Contains(session.GameId))
                .Select(session => session.GameId)
                .Distinct()
                .ToList();
            if (game != null && !runningGameIds.Contains(game.Id) &&
                Settings.ActiveSessions.Any(session => session.GameId == game.Id) &&
                !untrackedSessionIds.Contains(game.Id))
                untrackedSessionIds.Add(game.Id);

            foreach (var gameId in untrackedSessionIds)
            {
                var candidate = playniteApi.Database.Games.Get(gameId);
                if (candidate == null) continue;
                // Only force an untracked re-apply for a game with a known active limiter session.
                try { TryApplyForGame(candidate, null, null, false); }
                catch (Exception e) { logger.Error(e, $"Failed to apply shared Scanline target for {candidate.Name}."); }
            }
            return true;
        }

        /// <summary>Null when disabled / no active cap.</summary>
        public double? GetEffectiveLimit(Game game)
        {
            if (game == null)
            {
                return null;
            }

            var profile = Settings.GetGameProfile(game.Id);
            var modeLimit = profile?.GetMode(CurrentMode);
            if (profile?.SkipLimiterInjection == true) return null;
            // An explicit zero overrides global and Scanline-derived caps.
            if (modeLimit != null && modeLimit.Enabled && modeLimit.FrameLimit == 0) return 0;

            // Display the effective precise cap for Scanline/Latent Sync rather than the
            // rounded nominal display mode (e.g. 29.9001 rather than 30 FPS).
            var globalCap = Settings.GetGlobalForMonitor(ResolveGameMonitorDeviceName(game.Id),
                FPSLimiterSettings.IsWindowsGame(game), CurrentMode);
            var configuredCap = (modeLimit != null && modeLimit.Enabled && modeLimit.FrameLimit > 0) ||
                                (globalCap != null && globalCap.Enabled && globalCap.FrameLimit > 0) ||
                                (Settings.PerGameRefreshRateEnabled && (profile?.ForcedRefreshRateHz ?? 0) > 0);
            var activeSync = ResolveEffectiveSyncMode(modeLimit, CurrentMode,
                FPSLimiterSettings.IsWindowsGame(game), game.Id);
            if (configuredCap && ShouldMatchFpsToRefreshRate(game, activeSync))
            {
                var syncCap = GetRefreshRateForFpsMatch(game);
                if (syncCap > 0) return activeSync == FpsSyncMode.ScanlineSyncHalf ? syncCap / 2.0 : syncCap;
            }
            if (Settings.PerGameRefreshRateEnabled && profile != null && profile.ForcedRefreshRateHz > 0)
                return RefreshRateManager.GetPreciseCapForNominalMode(profile.ForcedRefreshRateHz,
                    ResolveGameMonitorDeviceName(game.Id));

            if (modeLimit != null && modeLimit.Enabled && modeLimit.FrameLimit > 0)
            {
                return modeLimit.FrameLimit;
            }

            // Special K: if no extension preset, show live SpecialK.ini TargetFPS.
            if (ShouldUseSpecialKBackend(game))
            {
                try
                {
                    var fromIni = specialKBackend.ReadProfileTargetFps(game, null);
                    if (fromIni.HasValue && fromIni.Value > 0)
                        return fromIni.Value;
                }
                catch { }
            }

            var globalForMode = Settings.GetGlobalForMonitor(ResolveGameMonitorDeviceName(game.Id), FPSLimiterSettings.IsWindowsGame(game), CurrentMode);
            if (modeLimit != null && modeLimit.Enabled && modeLimit.FrameLimit <= 0)
            {
                return null;
            }

            if (globalForMode != null && globalForMode.Enabled && globalForMode.FrameLimit > 0)
            {
                return globalForMode.FrameLimit;
            }

            return null;
        }

        public bool ResolveSteamLogSilent(Game game)
        {
            var profile = game != null ? Settings.GetGameProfile(game.Id) : null;
            if (profile != null && profile.HasSteamLogSilentOverride)
            {
                return profile.SpecialKSteamLogSilent;
            }

            return Settings.SpecialKSteamLogSilent;
        }

        /// <summary>
        /// Resolves the sync mode a per-game ModeLimitSettings actually applies: its own explicit
        /// choice, or the matching Global sync mode (UI mode + Windows/non-Windows category) when
        /// the game is left on <see cref="FpsSyncMode.UseGlobal"/>.
        /// </summary>
        private FpsSyncMode ResolveEffectiveSyncMode(ModeLimitSettings modeLimit, PlayniteUiMode mode, bool windowsGames, Guid gameId)
        {
            if (modeLimit != null && modeLimit.SyncMode != FpsSyncMode.UseGlobal)
            {
                return modeLimit.SyncMode;
            }

            return Settings.GetGlobalForMonitor(ResolveGameMonitorDeviceName(gameId), windowsGames, mode).SyncMode;
        }

        /// <summary>The sync mode a single game will actually apply right now, resolving "Use Global" if set.</summary>
        public FpsSyncMode GetEffectiveGameSyncMode(Game game)
        {
            var profile = GetGameProfile(game);
            return ResolveEffectiveSyncMode(profile?.GetMode(CurrentMode), CurrentMode, FPSLimiterSettings.IsWindowsGame(game), game.Id);
        }

        /// <summary>
        /// Resolves the Reflex marker setting a per-game ModeLimitSettings actually applies: its own
        /// explicit choice, or the matching Global Reflex marker setting when the game is left on
        /// <see cref="ReflexMarkerMode.UseGlobal"/>.
        /// </summary>
        private ReflexMarkerMode ResolveEffectiveReflexMarkers(ModeLimitSettings modeLimit, PlayniteUiMode mode, bool windowsGames, Guid gameId)
        {
            if (modeLimit != null && modeLimit.ReflexMarkers != ReflexMarkerMode.UseGlobal)
            {
                return modeLimit.ReflexMarkers;
            }

            return Settings.GetGlobalForMonitor(ResolveGameMonitorDeviceName(gameId), windowsGames, mode).ReflexMarkers;
        }

        /// <summary>The Reflex marker setting a single game will actually apply right now, resolving "Use Global" if set.</summary>
        public ReflexMarkerMode GetEffectiveGameReflexMarkers(Game game)
        {
            var profile = GetGameProfile(game);
            return ResolveEffectiveReflexMarkers(profile?.GetMode(CurrentMode), CurrentMode, FPSLimiterSettings.IsWindowsGame(game), game.Id);
        }

        /// <summary>Whether the VRR low-fps refresh-rate switch is currently enabled.</summary>
        public bool VrrRefreshRateEnabled
        {
            get { var m = Settings.GetMonitorProfile(null); return m != null ? m.VrrRefreshRateEnabled : Settings.VrrRefreshRateEnabled; }
        }

        /// <summary>Toggles VRR low-fps refresh-rate switching on/off, e.g. from the top bar button.</summary>
        public void ToggleVrrRefreshRateEnabled()
        {
            var m = Settings.GetMonitorProfile(null);
            if (m != null) m.VrrRefreshRateEnabled = !m.VrrRefreshRateEnabled;
            else Settings.VrrRefreshRateEnabled = !Settings.VrrRefreshRateEnabled;
            settingsViewModel.SaveSettings();
        }

        /// <summary>Marks a game as currently running. Call once the game process has actually started.</summary>
        public void NotifyGameStarted(Guid gameId, int? startedProcessId = null)
        {
            // The one-shot RTSS fallback must stay active while this game runs: TryApplyForGame, the post-launch
            // retarget and live preset changes all ask ShouldUseSpecialKBackend again. NotifyGameStopped consumes it.
            runningGameIds.Add(gameId);
            if (startedProcessId.HasValue && startedProcessId.Value > 0)
                runningProcessIds[gameId] = startedProcessId.Value;
            StartWindowPlacementWatch(gameId);
            var monitor = ResolveGameMonitorDeviceName(gameId);
            gameDisplayContexts[gameId] = GetDisplayContext(monitor);
            var startedGame = playniteApi.Database.Games.Get(gameId);
            if (startedGame != null) RefreshFreeSyncForGame(startedGame);

            // After successful injection: poll until SK is in the process, then stop SKIFsvc.
            if (startedGame != null && startedProcessId.HasValue && startedProcessId.Value > 0)
                BeginPostInjectStopWatch(startedGame, startedProcessId.Value);
        }

        /// <summary>Marks a game as no longer running (stopped, or the launch was cancelled).</summary>
        public void NotifyGameStopped(Guid gameId, bool consumeSpecialKFallback = false)
        {
            if (consumeSpecialKFallback)
            {
                // The session that used the one-shot RTSS fallback has ended; Special K is tried again next launch.
                lock (postInjectLock) specialKFallbackNextLaunchGames.Remove(gameId);
            }
            freeSync.Stop(gameId);
            runningGameIds.Remove(gameId);
            runningProcessIds.Remove(gameId);
            gameMonitorIds.Remove(gameId);
            gameDisplayContexts.Remove(gameId);
            specialKMacroPresets.Remove(gameId);
            lock (specialKMacroLock)
            {
                specialKPendingMacroCap.Remove(gameId);
                specialKCurrentCap.Remove(gameId);
            }
            lock (postInjectLock)
            {
                pendingPostInjectStop.Remove(gameId);
                specialKLaunchPendingGames.Remove(gameId);
            }
        }

        /// <summary>Games currently known to be running, used to target hotkeys at the right game(s).</summary>
        /// <summary>Whether the foreground process is one of Playnite's tracked game processes.</summary>
        public bool IsForegroundTrackedGame(int processId)
        {
            return processId > 0 && runningProcessIds.Values.Contains(processId);
        }

        public IEnumerable<Guid> GetRunningGameIds()
        {
            return runningGameIds.ToList();
        }

        public void SetGameLimit(IEnumerable<Game> games, double frameLimit)
        {
            var gameList = games.ToList();
            foreach (var game in gameList)
            {
                var profile = Settings.GetOrCreateGameProfile(game.Id);
                profile.SkipLimiterInjection = false;
                // Apply to both UI modes so Desktop/Fullscreen launches both get the cap.
                foreach (PlayniteUiMode uiMode in Enum.GetValues(typeof(PlayniteUiMode)))
                {
                    var mode = profile.GetMode(uiMode);
                    mode.Enabled = true;
                    mode.FrameLimit = frameLimit;
                }
            }

            settingsViewModel.SaveSettings();

            foreach (var game in gameList)
            {
                // Always push TargetFPS into SpecialK.ini when using SK (even if game is not running),
                // so the profile is ready before the next launch.
                if (ShouldUseSpecialKBackend(game))
                {
                    try
                    {
                        specialKBackend.WriteTargetFpsForGame(game, null, frameLimit);
                    }
                    catch (Exception e)
                    {
                        logger.Error(e, $"Failed to write Special K TargetFPS for {game.Name}.");
                    }
                }

                ReapplyIfRunning(game);
            }
        }

        private bool? ResolveFlipModel(GameLimitProfile profile)
        {
            var preference = profile != null && profile.RtssForceFlipModel != RtssFlipPreference.UseDefault
                ? profile.RtssForceFlipModel : Settings.RtssForceFlipModel;
            return preference == RtssFlipPreference.Enabled ? (bool?)true
                : preference == RtssFlipPreference.Disabled ? (bool?)false : null;
        }

        public void SetGameFlipPreference(IEnumerable<Game> games, RtssFlipPreference preference)
        {
            var list = games.ToList();
            foreach (var game in list)
                Settings.GetOrCreateGameProfile(game.Id).RtssForceFlipModel = preference;
            settingsViewModel.SaveSettings();
            foreach (var game in list) ReapplyIfRunning(game);
        }

        public void SetGamePreferredMonitor(IEnumerable<Game> games, string deviceName)
        {
            var list = games?.ToList() ?? new List<Game>();
            var selected = string.IsNullOrWhiteSpace(deviceName) ? string.Empty : deviceName;
            if (!string.IsNullOrEmpty(selected) &&
                !MonitorManager.GetDisplays().Any(d =>
                    string.Equals(d.DeviceName, selected, StringComparison.OrdinalIgnoreCase)))
                selected = string.Empty;

            foreach (var game in list)
                Settings.GetOrCreateGameProfile(game.Id).PreferredMonitorDeviceName = selected;

            settingsViewModel.SaveSettings();
            foreach (var game in list) ReapplyIfRunning(game);
        }

        public void SetGameSyncMode(IEnumerable<Game> games, FpsSyncMode syncMode)
        {
            var gameList = games.ToList();
            foreach (var game in gameList)
            {
                var profile = Settings.GetOrCreateGameProfile(game.Id);
                profile.GetMode(CurrentMode).SyncMode = syncMode;
                // A Scanline Hz selection must not pin the display after changing to
                // another sync method. Keep it only while the effective mode is Scanline.
                var effective = GetEffectiveGameSyncMode(game);
                if (effective != FpsSyncMode.ScanlineSync && effective != FpsSyncMode.ScanlineSyncHalf)
                    profile.ForcedRefreshRateHz = 0;
            }

            settingsViewModel.SaveSettings();

            foreach (var game in gameList)
            {
                RefreshFreeSyncForGame(game);
                ReapplyIfRunning(game);
            }
        }

        public void SetGameReflexMarkers(IEnumerable<Game> games, ReflexMarkerMode mode)
        {
            var gameList = games.ToList();
            foreach (var game in gameList)
            {
                var profile = Settings.GetOrCreateGameProfile(game.Id);
                profile.GetMode(CurrentMode).ReflexMarkers = mode;
            }

            settingsViewModel.SaveSettings();

            foreach (var game in gameList)
            {
                ReapplyIfRunning(game);
            }
        }

        /// <summary>
        /// If the game is currently running, immediately re-applies the limiter with the latest
        /// settings so the change takes effect live instead of waiting for the next launch. This is
        /// keyed off actual running state, not off whether a cap session is currently tracked, so it
        /// keeps working even right after the cap was disabled while the game is still running.
        /// </summary>
        private void ReapplyIfRunning(Game game)
        {
            if (!runningGameIds.Contains(game.Id))
            {
                return;
            }

            try
            {
                int processId;
                int? runningPid = runningProcessIds.TryGetValue(game.Id, out processId) ? (int?)processId : null;
                if (!TryApplyForGame(game, null, runningPid, false))
                {
                    var current = Settings.ActiveSessions.FirstOrDefault(a => a.GameId == game.Id);
                    if (current != null && current.AppliedFlipOnly) RestoreActiveSession(game, false);
                }
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to live-update FPS limit for {game.Name}.");
            }
        }

        public void DisableGameLimit(IEnumerable<Game> games)
        {
            // An explicit zero is distinct from "use global" and does not opt out of injection.
            SetGameLimit(games, 0);
        }

        public void SkipGameInjection(IEnumerable<Game> games)
        {
            var list = games.ToList();
            foreach (var game in list)
            {
                Settings.GetOrCreateGameProfile(game.Id).SkipLimiterInjection = true;
                // Release only the session/service managed by this extension. A running process
                // cannot be uninjected; external RTSS/Special K injection is outside our control.
                RestoreActiveSession(game, true);
                CleanupSpecialKInjection(game);
            }
            settingsViewModel.SaveSettings();
        }

        /// <summary>
        /// Clears the per-game FPS override so the game follows the Global FPS limit
        /// (for the current Playnite UI mode). If the game is running, re-applies immediately:
        /// when Global is enabled the global cap takes effect; when Global is off the
        /// active RTSS session is restored/cleared.
        /// </summary>
        public void UseGlobalGameLimit(IEnumerable<Game> games)
        {
            var gameList = games.ToList();
            foreach (var game in gameList)
            {
                var profile = Settings.GetOrCreateGameProfile(game.Id);
                profile.SkipLimiterInjection = false;
                foreach (PlayniteUiMode uiMode in Enum.GetValues(typeof(PlayniteUiMode)))
                {
                    var mode = profile.GetMode(uiMode);
                    mode.Enabled = false;
                    mode.FrameLimit = 0;
                }
                // Keep the profile: it may contain backend, VRR or other per-game choices.
            }

            settingsViewModel.SaveSettings();

            foreach (var game in gameList)
            {
                if (!runningGameIds.Contains(game.Id))
                {
                    continue;
                }

                // If the matching Global (Windows vs non-Windows) is on, re-apply so it takes over
                // live. If that Global is off, clear any existing session so the game is uncapped.
                var global = Settings.GetGlobalForMonitor(ResolveGameMonitorDeviceName(game.Id), FPSLimiterSettings.IsWindowsGame(game), CurrentMode);
                if (global.Enabled && global.FrameLimit > 0)
                {
                    ReapplyIfRunning(game);
                }
                else
                {
                    RestoreActiveSession(game, true);
                    ReapplyIfRunning(game);
                }
            }
        }

        public void SetManualExecutable(Game game, string executablePath)
        {
            var profile = Settings.GetOrCreateGameProfile(game.Id);
            profile.ManualExecutablePath = executablePath;
            profile.LastResolvedExecutable = executablePath;
            settingsViewModel.SaveSettings();
        }

        public void ClearManualExecutable(Game game)
        {
            var profile = Settings.GetOrCreateGameProfile(game.Id);
            profile.ManualExecutablePath = null;
            Settings.RemoveEmptyGameProfile(game.Id);
            settingsViewModel.SaveSettings();
        }

        public bool TryApplyForGame(Game game, GameAction sourceAction, int? startedProcessId, bool showError)
        {
            return TryApplyForGame(game, sourceAction, startedProcessId, showError, true);
        }

        private bool TryApplyForGame(Game game, GameAction sourceAction, int? startedProcessId, bool showError, bool allowPermissionSetup)
        {
            if (game == null || Settings.GetGameProfile(game.Id)?.SkipLimiterInjection == true)
                return false;
            if (startedProcessId.HasValue && startedProcessId.Value > 0)
            {
                runningProcessIds[game.Id] = startedProcessId.Value;
                // Process id may arrive here after NotifyGameStarted had none — start post-inject watch.
                BeginPostInjectStopWatch(game, startedProcessId.Value);
            }
            var profile = Settings.GetGameProfile(game.Id);
            var modeLimit = profile?.GetMode(CurrentMode);
            var hasGameProfile = modeLimit != null && modeLimit.Enabled;
            var explicitZero = hasGameProfile && modeLimit.FrameLimit == 0;
            var windowsGame = FPSLimiterSettings.IsWindowsGame(game);
            var globalForMode = Settings.GetGlobalForMonitor(ResolveGameMonitorDeviceName(game.Id), windowsGame, CurrentMode);
            var useGlobalFallback = !hasGameProfile && globalForMode.Enabled && globalForMode.FrameLimit > 0;

            // Resolve sync mode before deciding whether a per-game forced Hz is active.
            // A forced Hz is a fixed-sync aid for Scanline Sync / Special K Latent Sync; when the
            // user switches back to another sync method we must stop requesting that fixed Hz so
            // the live session can restore the display's original refresh rate.
            var earlySync = ResolveEffectiveSyncMode(modeLimit, CurrentMode, windowsGame, game.Id);
            var forcedHz = Settings.PerGameRefreshRateEnabled && ShouldMatchFpsToRefreshRate(game, earlySync)
                ? (profile?.ForcedRefreshRateHz ?? 0)
                : 0;

            var flipModel = ShouldUseSpecialKBackend(game) ? (bool?)null : ResolveFlipModel(profile);
            var needsFlipWithoutCap = !hasGameProfile && !useGlobalFallback && flipModel.HasValue;
            if (!hasGameProfile && !useGlobalFallback && forcedHz <= 0 && !needsFlipWithoutCap)
            {
                return false;
            }

            // May be 0 when only a fixed-sync refresh rate is requested.
            var frameLimit = hasGameProfile ? modeLimit.FrameLimit
                : (useGlobalFallback ? globalForMode.FrameLimit : 0);

            // Scanline/Latent Sync use FPS == display Hz.
            if (!explicitZero && (hasGameProfile || useGlobalFallback || forcedHz > 0) && ShouldMatchFpsToRefreshRate(game, earlySync))
            {
                var hz = GetRefreshRateForFpsMatch(game);
                if (hz > 0)
                {
                    frameLimit = earlySync == FpsSyncMode.ScanlineSyncHalf ? hz / 2.0 : hz;
                    logger.Info($"Latent/Scanline sync: matching FPS cap to {frameLimit} FPS for {game?.Name}.");
                }
            }
            var flipOnly = needsFlipWithoutCap && frameLimit <= 0;
            // Sync mode and Reflex markers are resolved independently of whether the game has its
            // own frame limit enabled: a per-game override (set via the game's right-click menu)
            // should still apply even when the game is only capped via the Global FPS limit
            // fallback. ResolveEffectiveSyncMode/ResolveEffectiveReflexMarkers already fall back to
            // the matching Global (Windows vs non-Windows) when modeLimit is null or left on UseGlobal.
            var syncMode = ResolveEffectiveSyncMode(modeLimit, CurrentMode, windowsGame, game.Id);
            var reflexMarkers = ResolveEffectiveReflexMarkers(modeLimit, CurrentMode, windowsGame, game.Id);
            var disableReflexMarkers = reflexMarkers == ReflexMarkerMode.Disabled;
            var forceGlobalProfile = true;

            string target = null;
            if (hasGameProfile || flipModel.HasValue &&
                (needsFlipWithoutCap || (profile != null && profile.RtssForceFlipModel != RtssFlipPreference.UseDefault)))
            {
                target = targetResolver.ResolveTarget(game, profile, sourceAction, startedProcessId);
                // Prefer an executable-specific zero cap so this game does not uncap other games.
                if (explicitZero && !string.IsNullOrWhiteSpace(target)) forceGlobalProfile = false;
                // Per-game overrides must not modify RTSS Global and affect other games.
                if (flipModel.HasValue && !string.IsNullOrWhiteSpace(target) &&
                    (needsFlipWithoutCap || (profile != null && profile.RtssForceFlipModel != RtssFlipPreference.UseDefault)))
                    forceGlobalProfile = false;
                if (flipModel.HasValue && string.IsNullOrWhiteSpace(target) &&
                    profile != null && profile.RtssForceFlipModel != RtssFlipPreference.UseDefault)
                {
                    if (showError) playniteApi.Dialogs.ShowErrorMessage(
                        $"FPS Limiter 3000 could not find an executable for {game.Name}. Set a manual target executable before using the per-game RTSS override.", "FPS Limiter 3000");
                    return false;
                }
                if (string.IsNullOrWhiteSpace(target) && !forceGlobalProfile)
                {
                    if (showError)
                    {
                        playniteApi.Dialogs.ShowErrorMessage(
                            $"FPS Limiter 3000 could not find a target executable for {game.Name}. Set a manual target executable from the game context menu.",
                            "FPS Limiter 3000");
                    }

                    return false;
                }
            }

            var existingSession = Settings.ActiveSessions.FirstOrDefault(a => a.GameId == game.Id);

            // Display refresh only (no FPS cap).
            if (frameLimit <= 0 && forcedHz > 0 && !flipOnly && !explicitZero)
            {
                LimitSessionSnapshot prior = existingSession != null && existingSession.RefreshRateChanged
                    ? existingSession
                    : null;
                var session = existingSession ?? new LimitSessionSnapshot
                {
                    GameId = game.Id,
                    GameName = game.Name,
                    ExecutablePath = target,
                    ProfileName = "RefreshRateOnly",
                    AppliedLimit = 0,
                    Backend = ShouldUseSpecialKBackend(game) ? LimiterBackendKind.SpecialK : LimiterBackendKind.Rtss
                };
                TrySwitchRefreshRateForCap(session, 0, prior, game, earlySync);
                Settings.ActiveSessions.RemoveAll(a => a.GameId == game.Id);
                Settings.ActiveSessions.Add(session);
                settingsViewModel.SaveSettings();
                return true;
            }
            var profileName = forceGlobalProfile ? "Global" : Path.GetFileName(target);
            // Preserve prior refresh-rate tracking across a live reapply so we don't bounce the
            // display back to native Hz and then immediately to the same target again.
            LimitSessionSnapshot priorRefreshState = null;
            if (existingSession != null)
            {
                var desiredBackend = ShouldUseSpecialKBackend(game) ? LimiterBackendKind.SpecialK : LimiterBackendKind.Rtss;
                if (string.Equals(existingSession.ProfileName, profileName, StringComparison.OrdinalIgnoreCase) &&
                    existingSession.AppliedLimit == frameLimit &&
                    existingSession.AppliedSyncMode == syncMode &&
                    // A scanline-offset change requires an RTSS profile reload even if FPS/Sync are unchanged.
                    (desiredBackend != LimiterBackendKind.Rtss || !(syncMode == FpsSyncMode.ScanlineSync || syncMode == FpsSyncMode.ScanlineSyncHalf) ||
                     existingSession.AppliedScanlineTarget == Settings.RtssScanlineSyncTarget) &&
                    existingSession.AppliedReflexMarkersDisabled == disableReflexMarkers &&
                    existingSession.AppliedForceFlipModel == flipModel &&
                    existingSession.AppliedFlipOnly == flipOnly &&
                    existingSession.Backend == desiredBackend)
                {
                    if (hasGameProfile && !string.IsNullOrWhiteSpace(target))
                    {
                        profile.LastResolvedExecutable = target;
                    }

                    settingsViewModel.SaveSettings();
                    return true;
                }

                if (existingSession.RefreshRateChanged)
                {
                    priorRefreshState = existingSession;
                }

                var willUseSpecialK = ShouldUseSpecialKBackend(game);
                // Live Special K FPS switch only rewrites TargetFPS — skip full profile restore.
                // For RTSS (or backend changes) restore the previous profile first.
                if (!(willUseSpecialK && existingSession.Backend == LimiterBackendKind.SpecialK))
                {
                    RestoreSession(existingSession, false, true);
                }
            }

            try
            {
                LimitSessionSnapshot session;
                var useSpecialK = ShouldUseSpecialKBackend(game);

                if (useSpecialK)
                {
                    // Special K path: do not touch RTSS at all (avoids double injection).
                    // On-the-fly FPS switches reuse the existing session and only rewrite TargetFPS
                    // so we can change caps while the game is running without clobbering the rest
                    // of the SK profile.
                    if (existingSession != null && existingSession.Backend == LimiterBackendKind.SpecialK &&
                        !string.IsNullOrWhiteSpace(existingSession.ProfileFilePath))
                    {
                        session = specialKBackend.UpdateLiveLimit(existingSession, frameLimit, ResolveSpecialKLimiterMode(game));
                        // prior refresh state already captured; don't call RestoreSession again
                        priorRefreshState = existingSession.RefreshRateChanged ? existingSession : priorRefreshState;
                        logger.Info($"Live-switched {game.Name} Special K FPS cap to {frameLimit}.");
                        // The preset list is only known after the launch-time sync; redo it if this session lost it.
                        if (!specialKMacroPresets.ContainsKey(game.Id)) SyncSpecialKPresetMacros(game, target);
                        TriggerSpecialKPresetMacro(game, frameLimit);
                    }
                    else
                    {
                        specialKBackend.EnsureProfileBeforeLaunch(game, target, frameLimit);
                        if (!specialKMacroPresets.ContainsKey(game.Id)) SyncSpecialKPresetMacros(game, target);
                        session = specialKBackend.ApplyLimit(game.Id, game.Name, target, frameLimit, game, ResolveSpecialKLimiterMode(game));
                        lock (specialKMacroLock) specialKCurrentCap[game.Id] = frameLimit; // what Special K starts with
                        logger.Info($"Applied {frameLimit} FPS limit to {game.Name} via Special K profile {session.ProfileName} (RTSS skipped).");
                    }
                }
                else
                {
                    session = rtssBackend.ApplyLimit(game.Id, game.Name, target, frameLimit, syncMode, forceGlobalProfile, disableReflexMarkers, flipModel, !flipOnly);
                    logger.Info($"Applied {frameLimit} FPS limit ({syncMode}) to {game.Name} via RTSS profile {session.ProfileName}.");
                }

                Settings.ActiveSessions.RemoveAll(a => a.GameId == game.Id);

                // Switch display refresh rate when capping to a low FPS target (skips if already there).
                // Works for both backends — VRR switching is display-side, not injector-side.
                TrySwitchRefreshRateForCap(session, frameLimit, priorRefreshState, game, syncMode);

                Settings.ActiveSessions.Add(session);
                if (profile != null && !string.IsNullOrWhiteSpace(target))
                {
                    profile.LastResolvedExecutable = target;
                }

                if (session.StartedRtssProcess)
                {
                    Settings.RtssStartedByExtension = true;
                }

                settingsViewModel.SaveSettings();
                return true;
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to apply FPS limit to {game.Name}.");

                if (showError)
                {
                    if (!ShouldUseSpecialKBackend(game) && allowPermissionSetup && ShouldOfferRtssAccessSetup(e))
                    {
                        if (OfferRtssAccessSetup(game, frameLimit, e))
                        {
                            return TryApplyForGame(game, sourceAction, startedProcessId, showError, false);
                        }
                    }
                    else
                    {
                        playniteApi.Dialogs.ShowErrorMessage(
                            $"FPS Limiter 3000 could not apply the {frameLimit} FPS cap to {game.Name}.\n\n{e.Message}",
                            "FPS Limiter 3000");
                    }
                }

                return false;
            }
        }

        private bool OfferRtssAccessSetup(Game game, double frameLimit, Exception originalError)
        {
            var setupOption = new MessageBoxOption("Set up RTSS access", true, false);
            var continueOption = new MessageBoxOption("Continue without cap", false, true);
            var selected = playniteApi.Dialogs.ShowMessage(
                $"FPS Limiter 3000 could not apply the {frameLimit} FPS cap to {game.Name}.\n\n" +
                "Windows is blocking write access to RTSS profile files, so RTSS kept its previous settings.\n\n" +
                "Set up RTSS access to allow this Windows user to edit RTSS profiles, or continue launching the game without a cap.\n\n" +
                $"Details:\n{originalError.Message}",
                "FPS Limiter 3000",
                MessageBoxImage.Warning,
                new List<MessageBoxOption> { setupOption, continueOption });

            if (selected != setupOption)
            {
                return false;
            }

            try
            {
                rtssBackend.GrantProfilesModifyPermissionToCurrentUser();
                var result = rtssBackend.TestProfileWriteAccess();

                if (result.Success)
                {
                    playniteApi.Dialogs.ShowMessage(
                        "RTSS access is ready. FPS Limiter 3000 will try the cap again.",
                        "FPS Limiter 3000");
                    return true;
                }

                playniteApi.Dialogs.ShowErrorMessage(
                    $"FPS Limiter 3000 ran the RTSS access setup, but the write test still failed.\n\n{result.Message}",
                    "FPS Limiter 3000");
                return false;
            }
            catch (Exception e)
            {
                playniteApi.Dialogs.ShowErrorMessage(
                    $"FPS Limiter 3000 could not set up RTSS access.\n\n{e.Message}",
                    "FPS Limiter 3000");
                return false;
            }
        }

        private static bool ShouldOfferRtssAccessSetup(Exception error)
        {
            var current = error;
            while (current != null)
            {
                if (current is UnauthorizedAccessException)
                {
                    return true;
                }

                var message = current.Message ?? string.Empty;
                if (message.IndexOf("RTSS Profiles folder", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("cannot write", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("access", StringComparison.OrdinalIgnoreCase) >= 0 && message.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                current = current.InnerException;
            }

            return false;
        }

        /// <summary>
        /// Special K may name its profile folder differently from what we guessed. After launch,
        /// find the folder it really writes to, remember it, and write the cap there too.
        /// </summary>
        private void LearnSpecialKProfileFolder(Game game, double frameLimit, DateTime launchedUtc)
        {
            try
            {
                for (var i = 0; i < 30 && runningGameIds.Contains(game.Id); i++)
                {
                    Thread.Sleep(2000);
                    var dir = specialKBackend.FindActiveProfileFolder(launchedUtc);
                    if (string.IsNullOrWhiteSpace(dir))
                        continue;

                    var name = Path.GetFileName(dir);
                    var profile = Settings.GetOrCreateGameProfile(game.Id);
                    var alreadyKnown = string.Equals(profile.SpecialKProfileFolder, name, StringComparison.OrdinalIgnoreCase);
                    profile.SpecialKProfileFolder = name;
                    settingsViewModel.SaveSettings();

                    var ini = Path.Combine(dir, "SpecialK.ini");
                    if (File.Exists(ini))
                        specialKBackend.ApplyToExistingIni(ini, frameLimit > 0 ? (double?)frameLimit : null, false);
                    specialKBackend.RemoveDecoyProfileFolders(game, profile.ManualExecutablePath ?? profile.LastResolvedExecutable, name);
                    logger.Info($"Special K is using profile folder '{name}' for {game.Name}" +
                        (alreadyKnown ? "." : "; remembered it. The FPS cap is fully applied from the next launch."));
                    return;
                }
                logger.Warn($"Could not detect which Special K profile folder {game.Name} uses.");
            }
            catch (Exception e)
            {
                logger.Warn(e, "Learning the Special K profile folder failed.");
            }
        }

        public void RetargetAfterLaunch(Game game, GameAction sourceAction, int? startedProcessId)
        {
            var launchedUtc = DateTime.UtcNow.AddSeconds(-5);
            var profile = Settings.GetGameProfile(game.Id);
            var modeLimit = profile?.GetMode(CurrentMode);
            var windowsGame = FPSLimiterSettings.IsWindowsGame(game);
            var globalForMode = Settings.GetGlobalForMonitor(ResolveGameMonitorDeviceName(game.Id), windowsGame, CurrentMode);
            var hasGameProfile = modeLimit != null && modeLimit.Enabled;
            var useGlobalFallback = !hasGameProfile && globalForMode.Enabled && globalForMode.FrameLimit > 0;
            var frameLimit = hasGameProfile ? modeLimit.FrameLimit
                : (useGlobalFallback ? globalForMode.FrameLimit : 0);

            // Special K: already applied in TryApplyForGame / PrepareSpecialKInjection (before launch).
            // No delayed post-launch writes — SK loads the ini at inject time.
            if (ShouldUseSpecialKBackend(game) ||
                (Settings.ActiveSessions.FirstOrDefault(a => a.GameId == game.Id)?.Backend == LimiterBackendKind.SpecialK))
            {
                LearnSpecialKProfileFolder(game, frameLimit, launchedUtc);
                return;
            }

            var currentSession = Settings.ActiveSessions.FirstOrDefault(a => a.GameId == game.Id);
            if (currentSession == null || currentSession.UsesGlobalProfile ||
                currentSession.Backend != LimiterBackendKind.Rtss)
                return;
            if (!hasGameProfile && !useGlobalFallback && !currentSession.AppliedFlipOnly)
                return;

            var currentExecutable = currentSession.ExecutablePath ?? profile?.LastResolvedExecutable;
            var actualTarget = targetResolver.ResolveLaunchedGameProcessTarget(game, startedProcessId, currentExecutable);

            if (string.IsNullOrWhiteSpace(actualTarget))
            {
                logger.Debug($"FPS Limiter did not find a better running process target for {game.Name} from PID {startedProcessId} or install folder.");
                return;
            }

            if (string.Equals(
                Path.GetFileName(actualTarget),
                Path.GetFileName(currentExecutable),
                StringComparison.OrdinalIgnoreCase))
            {
                logger.Debug($"FPS Limiter child process target for {game.Name} still resolves to {Path.GetFileName(actualTarget)}.");
                return;
            }

            // The search may take seconds. Do not rewrite profiles after the game stopped or
            // a different settings change replaced the session during the search.
            if (!runningGameIds.Contains(game.Id) ||
                !ReferenceEquals(currentSession, Settings.ActiveSessions.FirstOrDefault(a => a.GameId == game.Id)))
                return;

            try
            {
                LimitSessionSnapshot priorRefreshState = null;
                if (currentSession != null)
                {
                    if (currentSession.RefreshRateChanged)
                    {
                        priorRefreshState = currentSession;
                    }

                    RestoreSession(currentSession, false, true);
                }

                var syncMode = ResolveEffectiveSyncMode(modeLimit, CurrentMode, windowsGame, game.Id);
                var disableReflexMarkers = ResolveEffectiveReflexMarkers(modeLimit, CurrentMode, windowsGame, game.Id) == ReflexMarkerMode.Disabled;
                var retargetFrameLimit = frameLimit;
                if (!currentSession.AppliedFlipOnly && !(hasGameProfile && modeLimit.FrameLimit == 0) && ShouldMatchFpsToRefreshRate(game, syncMode))
                {
                    var hz = GetRefreshRateForFpsMatch(game);
                    if (hz > 0)
                        retargetFrameLimit = hz;
                }

                var session = rtssBackend.ApplyLimit(game.Id, game.Name, actualTarget, retargetFrameLimit, syncMode, false, disableReflexMarkers, ResolveFlipModel(profile), !currentSession.AppliedFlipOnly);
                Settings.ActiveSessions.RemoveAll(a => a.GameId == game.Id);

                TrySwitchRefreshRateForCap(session, retargetFrameLimit, priorRefreshState, game, syncMode);

                Settings.ActiveSessions.Add(session);
                if (profile != null) profile.LastResolvedExecutable = actualTarget;

                if (session.StartedRtssProcess)
                {
                    Settings.RtssStartedByExtension = true;
                }

                settingsViewModel.SaveSettings();

                logger.Info($"Retargeted {game.Name} FPS limit from {Path.GetFileName(currentExecutable)} to {session.ProfileName}.");
            }
            catch (Exception e)
            {
                logger.Error(e, $"Failed to retarget FPS limit for {game.Name} to {actualTarget}.");
            }
        }

        public void RestoreActiveSession(Game game, bool showError)
        {
            var snapshot = Settings.ActiveSessions.FirstOrDefault(a => a.GameId == game.Id);
            if (snapshot != null)
            {
                RestoreSession(snapshot, showError);
            }
        }

        /// <summary>
        /// Restores a game's limiter session and retries the original display refresh rate after
        /// the game exits. Exclusive-fullscreen games can temporarily reject ChangeDisplaySettingsEx
        /// while Windows is transitioning back to the desktop, so a single immediate restore is not
        /// reliable enough for game-stop cleanup.
        /// </summary>
        public void RestoreActiveSessionAfterGameStopped(Game game, bool showError)
        {
            if (game == null)
            {
                return;
            }

            // Keep a local copy because RestoreSession may remove it from ActiveSessions.
            var snapshot = Settings.ActiveSessions.FirstOrDefault(a => a.GameId == game.Id);
            RestoreActiveSession(game, showError);

            if (snapshot == null || !snapshot.RefreshRateChanged || snapshot.OriginalRefreshRate <= 0)
            {
                return;
            }

            const int maxAttempts = 10;
            const int retryDelayMilliseconds = 500;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var currentHz = RefreshRateManager.GetCurrentRefreshRate(snapshot.RefreshDeviceName);
                if (currentHz == snapshot.OriginalRefreshRate)
                {
                    if (attempt > 1)
                    {
                        logger.Info($"Display refresh rate returned to {snapshot.OriginalRefreshRate} Hz after {game.Name} stopped.");
                    }
                    return;
                }

                System.Threading.Thread.Sleep(retryDelayMilliseconds);

                currentHz = RefreshRateManager.GetCurrentRefreshRate(snapshot.RefreshDeviceName);
                if (currentHz == snapshot.OriginalRefreshRate)
                {
                    logger.Info($"Display refresh rate returned to {snapshot.OriginalRefreshRate} Hz after {game.Name} stopped.");
                    return;
                }

                if (RefreshRateManager.SetRefreshRate(snapshot.OriginalRefreshRate, snapshot.RefreshDeviceName))
                {
                    logger.Info($"Restored display refresh rate to {snapshot.OriginalRefreshRate} Hz after {game.Name} stopped (retry {attempt}/{maxAttempts}).");
                    return;
                }
            }

            logger.Warn($"Could not restore display refresh rate to {snapshot.OriginalRefreshRate} Hz after {game.Name} stopped; tried {maxAttempts} times. Check the display mode manually if it remains changed.");
        }

        // ── Forced display mode ─────────────────────────────────────────────────────────
        // When a monitor has a forced resolution + refresh rate, every game on it runs at that mode.
        // The mode that was active before the first game started is restored when the last one stops.
        private readonly object displayModeLock = new object();
        private readonly Dictionary<string, RefreshRateManager.DisplayModeInfo> forcedOriginals =
            new Dictionary<string, RefreshRateManager.DisplayModeInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> forcedHolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string displayModeStateFile;

        private static string HolderKey(Guid gameId, string device) { return gameId.ToString("N") + "|" + device; }

        private void PersistDisplayModes()
        {
            try
            {
                if (string.IsNullOrEmpty(displayModeStateFile)) return;
                if (forcedOriginals.Count == 0)
                {
                    if (System.IO.File.Exists(displayModeStateFile)) System.IO.File.Delete(displayModeStateFile);
                    return;
                }
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(displayModeStateFile));
                System.IO.File.WriteAllLines(displayModeStateFile, forcedOriginals.Select(kv =>
                    kv.Key + "|" + kv.Value.Width + "|" + kv.Value.Height + "|" + kv.Value.Hz));
            }
            catch (Exception ex) { logger.Warn("Could not persist display-mode restore state: " + ex.Message); }
        }

        /// <summary>
        /// Startup recovery: if Playnite or the game host died while a forced mode was active, put
        /// the displays back to the mode they had before.
        /// </summary>
        public void InitDisplayModeRecovery(string stateFile)
        {
            displayModeStateFile = stateFile;
            System.Threading.Tasks.Task.Run(() =>
            {
                lock (displayModeLock)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(stateFile) || !System.IO.File.Exists(stateFile)) return;
                        foreach (var line in System.IO.File.ReadAllLines(stateFile))
                        {
                            var parts = line.Split('|');
                            int w, h, hz;
                            if (parts.Length != 4 || !int.TryParse(parts[1], out w) || !int.TryParse(parts[2], out h) ||
                                !int.TryParse(parts[3], out hz) || forcedHolders.Any(k => k.EndsWith("|" + parts[0], StringComparison.OrdinalIgnoreCase)))
                                continue;
                            if (RefreshRateManager.SetDisplayMode(parts[0], w, h, hz))
                                logger.Info("Display mode restored after the previous session ended without restoring it: " + parts[0] + " " + w + "x" + h + " @ " + hz + " Hz.");
                        }
                        System.IO.File.Delete(stateFile);
                    }
                    catch (Exception ex) { logger.Warn("Display-mode recovery error: " + ex.Message); }
                }
            });
        }

        /// <summary>Applies the forced mode of the game's monitor (null device = its last known display).</summary>
        public void ApplyForcedDisplayMode(Game game, string deviceName = null)
        {
            if (game == null) return;
            try
            {
                var device = !string.IsNullOrEmpty(deviceName) ? deviceName : ResolveGameMonitorDeviceName(game.Id);
                var monitor = Settings.GetMonitorProfile(device);
                if (monitor == null || monitor.ForcedWidth <= 0 || monitor.ForcedHeight <= 0) return;

                lock (displayModeLock)
                {
                    var key = HolderKey(game.Id, device);
                    if (forcedHolders.Contains(key)) return;

                    var current = RefreshRateManager.GetCurrentMode(device);
                    if (current == null) return;
                    var hz = monitor.ForcedRefreshHz > 0 ? monitor.ForcedRefreshHz : current.Hz;
                    var alreadyThere = current.Width == monitor.ForcedWidth && current.Height == monitor.ForcedHeight && current.Hz == hz;

                    if (!alreadyThere)
                    {
                        var recordOriginal = !forcedOriginals.ContainsKey(device);
                        if (recordOriginal)
                        {
                            forcedOriginals[device] = current;
                            PersistDisplayModes();
                        }
                        if (!RefreshRateManager.SetDisplayMode(device, monitor.ForcedWidth, monitor.ForcedHeight, hz))
                        {
                            logger.Warn($"FPS Limiter: could not force {monitor.ForcedWidth}x{monitor.ForcedHeight} @ {hz} Hz on {device} for {game.Name}.");
                            if (recordOriginal && !forcedHolders.Any(k => k.EndsWith("|" + device, StringComparison.OrdinalIgnoreCase)))
                            {
                                forcedOriginals.Remove(device);
                                PersistDisplayModes();
                            }
                            return;
                        }
                        logger.Info($"FPS Limiter: forced {monitor.ForcedWidth}x{monitor.ForcedHeight} @ {hz} Hz on {device} for {game.Name} (was {current}).");
                    }
                    forcedHolders.Add(key);
                }
            }
            catch (Exception ex) { logger.Error(ex, "Forced display mode failed for " + game.Name + "."); }
        }

        /// <summary>Releases the forced mode held for a game; restores the original mode when it was the last.</summary>
        public void ReleaseForcedDisplayMode(Guid gameId)
        {
            try
            {
                lock (displayModeLock)
                {
                    var prefix = gameId.ToString("N") + "|";
                    var released = forcedHolders.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (var key in released)
                    {
                        forcedHolders.Remove(key);
                        var device = key.Substring(prefix.Length);
                        if (forcedHolders.Any(k => k.EndsWith("|" + device, StringComparison.OrdinalIgnoreCase))) continue;
                        RefreshRateManager.DisplayModeInfo original;
                        if (!forcedOriginals.TryGetValue(device, out original)) continue;
                        if (RefreshRateManager.SetDisplayMode(device, original.Width, original.Height, original.Hz))
                        {
                            forcedOriginals.Remove(device);
                            logger.Info($"FPS Limiter: restored {device} to {original}.");
                        }
                        else logger.Warn($"FPS Limiter: could not restore {device} to {original}; it will be retried on the next start.");
                    }
                    PersistDisplayModes();
                }
            }
            catch (Exception ex) { logger.Error(ex, "Restoring the forced display mode failed."); }
        }

        /// <summary>Puts every display back to its pre-game mode (Playnite is closing).</summary>
        public void RestoreAllDisplayModes()
        {
            lock (displayModeLock)
            {
                forcedHolders.Clear();
                foreach (var kv in forcedOriginals.ToList())
                {
                    if (RefreshRateManager.SetDisplayMode(kv.Key, kv.Value.Width, kv.Value.Height, kv.Value.Hz))
                        forcedOriginals.Remove(kv.Key);
                }
                PersistDisplayModes();
            }
        }

        public void RestoreAllActiveSessions(bool showError)
        {
            foreach (var snapshot in Settings.ActiveSessions.ToList())
            {
                RestoreSession(snapshot, showError);
            }
        }

        /// <summary>
        /// Computes the display Hz this FPS cap wants, or 0 if no forced switch is needed
        /// (leave / restore native rate).
        /// </summary>
        private bool IsVrrEnabledForGame(Game game)
        {
            var profile = game != null ? Settings.GetGameProfile(game.Id) : null;
            if (profile != null)
            {
                if (profile.VrrPreference == GameVrrPreference.Disabled)
                    return false;
                if (profile.VrrPreference == GameVrrPreference.Enabled)
                    return true;
            }
            var monitor = GetMonitorProfileFor(game);
            return monitor != null ? monitor.VrrRefreshRateEnabled : Settings.VrrRefreshRateEnabled;
        }

        public void SetGameVrrPreference(IEnumerable<Game> games, GameVrrPreference preference, int targetHzOverride = 0, double fpsThresholdOverride = 0)
        {
            foreach (var g in games)
            {
                var p = Settings.GetOrCreateGameProfile(g.Id);
                p.VrrPreference = preference;
                p.VrrTargetHzOverride = targetHzOverride;
                p.VrrFpsThresholdOverride = fpsThresholdOverride;
            }
            settingsViewModel.SaveSettings();
            foreach (var g in games)
                ReapplyIfRunning(g);
        }

        public void SetGameExactSyncRefreshRate(IEnumerable<Game> games, double hz)
        {
            if (hz != 0 && (hz < 10 || hz > 1000 || double.IsNaN(hz) || double.IsInfinity(hz)))
                return;
            foreach (var g in games)
                Settings.GetOrCreateGameProfile(g.Id).ExactSyncRefreshHz = hz;
            settingsViewModel.SaveSettings();
            foreach (var g in games)
                ReapplyIfRunning(g);
        }

        public void SetGameForcedRefreshRate(IEnumerable<Game> games, int hz)
        {
            foreach (var g in games)
            {
                var p = Settings.GetOrCreateGameProfile(g.Id);
                p.ForcedRefreshRateHz = hz > 0 ? hz : 0;
                if (hz > 0) p.SkipLimiterInjection = false;
                // Choosing a display Hz also sets the FPS cap to the same value.
                if (hz > 0)
                {
                    var modeLimit = p.GetMode(CurrentMode);
                    modeLimit.Enabled = true;
                    modeLimit.FrameLimit = RefreshRateManager.GetPreciseCapForNominalMode(
                        hz, ResolveGameMonitorDeviceName(g.Id));

                    // Special K UI reads TargetFPS from SpecialK.ini — keep it in sync.
                    if (ShouldUseSpecialKBackend(g))
                    {
                        try
                        {
                            specialKBackend.WriteTargetFpsForGame(g, null, modeLimit.FrameLimit, forceSeedDefaults: false);
                        }
                        catch (Exception e)
                        {
                            logger.Error(e, $"Failed to write TargetFPS={hz} for forced refresh on {g?.Name}.");
                        }
                    }
                }
            }
            settingsViewModel.SaveSettings();
            foreach (var g in games)
                ReapplyIfRunning(g);
        }

        private int GetDesiredRefreshRateForCap(double frameLimit, Game game = null, FpsSyncMode syncMode = FpsSyncMode.Async)
        {
            // 1) Per-game fixed refresh rate for fixed-sync modes only.
            // This is deliberately not sticky across Async / Front Edge / Back Edge / Reflex: if
            // Scanline/Latent Sync changed the display, leaving that mode should restore native Hz.
            if (Settings.PerGameRefreshRateEnabled && game != null && ShouldMatchFpsToRefreshRate(game, syncMode))
            {
                var profile = Settings.GetGameProfile(game.Id);
                if (profile != null && profile.ForcedRefreshRateHz > 0)
                    return profile.ForcedRefreshRateHz;
            }

            if (frameLimit <= 0)
            {
                return 0;
            }

            // 2) Low-FPS refresh switching (per-game or per-monitor). The old "match Hz to FPS"
            // multiplier search is gone: presets are derived from the monitor's Hz instead.
            if (IsVrrEnabledForGame(game))
            {
                var threshold = GetVrrThresholdFor(game);
                var targetHz = GetVrrTargetHzFor(game);
                return frameLimit <= threshold ? targetHz : 0;
            }

            // 3) Fixed-refresh display with several modes (e.g. 78 / 72 / 60 Hz): lock the cap by
            // running the display in the mode the cap divides evenly into (30 FPS -> 60 Hz, 39 -> 78 Hz,
            // 36 -> 72 Hz). The previous mode is restored when the game stops or the cap changes.
            if (game != null && IsFixedRefreshDisplay(game))
            {
                var monitor = GetMonitorProfileFor(game);
                if (monitor != null && monitor.MatchPresetsAllModes &&
                    !(monitor.ForcedWidth > 0 && monitor.ForcedRefreshHz > 0))
                {
                    var device = ResolveGameMonitorDeviceName(game.Id);
                    var currentHz = RefreshRateManager.GetCurrentRefreshRate(device);
                    return RefreshRateManager.FindModeForCap(device, frameLimit, currentHz);
                }
            }

            return 0;
        }

        /// <summary>
        /// Applies (or clears) the display refresh-rate switch for a new/updated cap session.
        /// <paramref name="priorRefreshState"/> is the previous live session's refresh tracking when
        /// this is a reapply — used so we keep the same original Hz and avoid flickering the display
        /// when the desired target rate has not actually changed (e.g. 40→35→30 FPS all map to 48 Hz,
        /// or a sync-mode-only change with the same FPS).
        /// </summary>
        private void TrySwitchRefreshRateForCap(LimitSessionSnapshot session, double frameLimit, LimitSessionSnapshot priorRefreshState = null, Game game = null, FpsSyncMode syncMode = FpsSyncMode.Async)
        {
            var desiredHz = GetDesiredRefreshRateForCap(frameLimit, game, syncMode);
            var hasForced = Settings.PerGameRefreshRateEnabled && game != null &&
                ShouldMatchFpsToRefreshRate(game, syncMode) &&
                (Settings.GetGameProfile(game.Id)?.ForcedRefreshRateHz ?? 0) > 0;
            var vrrOn = IsVrrEnabledForGame(game);
            var hasPriorRefreshToRestore = priorRefreshState != null &&
                priorRefreshState.RefreshRateChanged &&
                priorRefreshState.OriginalRefreshRate > 0;

            // Do not return just because the NEW mode has no refresh-rate request. A live
            // Scanline/Latent -> Async/Front/Back/Reflex switch can still have an OLD refresh
            // rate to restore. Restore is intentionally deferred until here to avoid flicker.
            if (desiredHz <= 0 && !vrrOn && !hasForced && !hasPriorRefreshToRestore)
            {
                return;
            }
            // Switch the display the game runs on, not always the primary one.
            var device = priorRefreshState != null && priorRefreshState.RefreshRateChanged && !string.IsNullOrEmpty(priorRefreshState.RefreshDeviceName)
                ? priorRefreshState.RefreshDeviceName
                : (game != null ? ResolveGameMonitorDeviceName(game.Id) : MonitorManager.PrimaryDeviceName);
            var currentHz = RefreshRateManager.GetCurrentRefreshRate(device);

            // Carry forward the true pre-session native rate across live reapplies.
            var originalHz = priorRefreshState != null && priorRefreshState.RefreshRateChanged && priorRefreshState.OriginalRefreshRate > 0
                ? priorRefreshState.OriginalRefreshRate
                : currentHz;

            if (desiredHz > 0)
            {
                if (currentHz > 0 && currentHz == desiredHz)
                {
                    // Already at the right rate — do not call SetRefreshRate (avoids flicker).
                    // Still record tracking so game-stop can restore native later.
                    session.RefreshRateChanged = true;
                    session.RefreshDeviceName = device;
                    session.OriginalRefreshRate = originalHz > 0 ? originalHz : currentHz;
                    logger.Debug($"Display already at {desiredHz} Hz for {frameLimit} FPS on {session.GameName}; skipping refresh-rate change.");
                    return;
                }

                if (currentHz <= 0)
                {
                    return;
                }

                if (RefreshRateManager.SetRefreshRate(desiredHz, device))
                {
                    session.RefreshRateChanged = true;
                    session.RefreshDeviceName = device;
                    session.OriginalRefreshRate = originalHz;
                    logger.Info($"Switched display to {desiredHz} Hz for {frameLimit} FPS cap on {session.GameName} (was {currentHz} Hz).");
                }
                else
                {
                    logger.Warn($"Could not switch display to {desiredHz} Hz for {session.GameName}.");
                }

                return;
            }

            // Cap no longer wants a forced rate (e.g. raised above VRR threshold). Restore native
            // only if we previously changed it — and only if we're not already there.
            if (priorRefreshState != null && priorRefreshState.RefreshRateChanged && priorRefreshState.OriginalRefreshRate > 0)
            {
                if (currentHz != priorRefreshState.OriginalRefreshRate)
                {
                    if (RefreshRateManager.SetRefreshRate(priorRefreshState.OriginalRefreshRate, device))
                    {
                        logger.Info($"Restored display refresh rate to {priorRefreshState.OriginalRefreshRate} Hz after raising FPS on {session.GameName}.");
                    }
                    else
                    {
                        logger.Warn($"Failed to restore display refresh rate to {priorRefreshState.OriginalRefreshRate} Hz for {session.GameName}.");
                    }
                }

                session.RefreshRateChanged = false;
                session.OriginalRefreshRate = 0;
            }
        }

        private void RestoreSession(LimitSessionSnapshot snapshot, bool showError)
        {
            RestoreSession(snapshot, showError, false);
        }

        /// <param name="liveReapply">
        /// When true, this restore is immediately followed by applying a new limit to the same
        /// still-running game. Skip restoring the display refresh rate (the new apply decides
        /// whether the rate still needs to change) and skip the "no caps left → close RTSS" path
        /// so neither the display nor RTSS flash mid-session.
        /// </param>
        private void RestoreSession(LimitSessionSnapshot snapshot, bool showError, bool liveReapply)
        {
            try
            {
                if (snapshot.Backend == LimiterBackendKind.SpecialK)
                {
                    specialKBackend.RestoreLimit(snapshot);
                    logger.Info($"Restored Special K profile {snapshot.ProfileName} after {snapshot.GameName}.");
                }
                else
                {
                    rtssBackend.RestoreLimit(snapshot);
                    logger.Info($"Restored RTSS profile {snapshot.ProfileName} after {snapshot.GameName}.");
                }

                Settings.ActiveSessions.RemoveAll(a => a.GameId == snapshot.GameId && string.Equals(a.ProfileName, snapshot.ProfileName, StringComparison.OrdinalIgnoreCase));

                // On a live reapply, leave the refresh rate alone — TrySwitchRefreshRateForCap will
                // only change it if the *desired* target Hz for the new cap is different.
                if (!liveReapply && snapshot.RefreshRateChanged && snapshot.OriginalRefreshRate > 0)
                {
                    if (RefreshRateManager.SetRefreshRate(snapshot.OriginalRefreshRate, snapshot.RefreshDeviceName))
                    {
                        logger.Info($"Restored display refresh rate to {snapshot.OriginalRefreshRate} Hz after {snapshot.GameName}.");
                    }
                    else
                    {
                        logger.Warn($"Failed to restore display refresh rate to {snapshot.OriginalRefreshRate} Hz after {snapshot.GameName}.");
                    }
                }

                if (!liveReapply &&
                    Settings.AutoCloseRtss &&
                    snapshot.Backend != LimiterBackendKind.SpecialK &&
                    Settings.RtssStartedByExtension &&
                    !Settings.ActiveSessions.Any(s => s.Backend != LimiterBackendKind.SpecialK))
                {
                    rtssBackend.CloseRtssProcess();
                    Settings.RtssStartedByExtension = false;
                }

                settingsViewModel.SaveSettings();
            }
            catch (Exception e)
            {
                var backendLabel = snapshot.Backend == LimiterBackendKind.SpecialK ? "Special K" : "RTSS";
                logger.Error(e, $"Failed to restore {backendLabel} profile {snapshot.ProfileName}.");

                if (showError)
                {
                    playniteApi.Dialogs.ShowErrorMessage(
                        $"FPS Limiter 3000 could not restore the previous {backendLabel} profile for {snapshot.GameName}.\n\n{e.Message}",
                        "FPS Limiter 3000");
                }
            }
        }
    }
}
