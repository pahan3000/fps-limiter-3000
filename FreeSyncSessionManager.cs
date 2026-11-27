using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace FPSLimiter
{
    /// <summary>
    /// Isolated AMD FreeSync control. Never writes to RTSS, the registry, or Adrenalin files.
    /// FreeSyncControl.exe is an optional AMD ADLX-backed companion; no driver change occurs
    /// when the companion is missing or reports unsupported/multiple displays.
    /// </summary>
    internal sealed class FreeSyncSessionManager
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly object gate = new object();
        private readonly HashSet<Guid> requestingGames = new HashSet<Guid>();
        private bool? originalState;
        private static string HelperPath => Path.Combine(Path.GetDirectoryName(typeof(FreeSyncSessionManager).Assembly.Location), "FreeSyncControl.exe");

        public bool Available => File.Exists(HelperPath) && ReadState().HasValue;
        public bool? GetCurrentState() => ReadState();

        private bool? ReadState()
        {
            string output;
            if (!Run("state", out output)) return null;
            if (output.Trim() == "1") return true;
            if (output.Trim() == "0") return false;
            return null;
        }

        private bool WriteState(bool enabled)
        {
            string output;
            // The helper can time out or exit non-zero after the driver already applied the
            // change, so the re-read below is the source of truth, not the helper's exit code.
            Run(enabled ? "on" : "off", out output);
            return ReadState() == enabled;
        }

        private static bool Run(string command, out string output)
        {
            output = null;
            if (!File.Exists(HelperPath)) return false;
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo(HelperPath, command)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = Path.GetDirectoryName(HelperPath)
                    };
                    if (!process.Start()) return false;
                    if (!process.WaitForExit(4000))
                    {
                        try { process.Kill(); } catch { }
                        logger.Warn("FreeSync companion timed out; no state can be confirmed.");
                        return false;
                    }
                    output = process.StandardOutput.ReadToEnd().Trim();
                    if (process.ExitCode == 0) return true;
                    logger.Warn("FreeSync companion refused '" + command + "' (code " + process.ExitCode + ").");
                }
            }
            catch (Exception ex) { logger.Warn("FreeSync companion error: " + ex.Message); }
            return false;
        }

        /// <summary>
        /// File that records the FreeSync state to put back while the plugin is holding it off.
        /// It survives a Playnite crash/kill, so the next start can restore the driver setting.
        /// </summary>
        public string StateFile { get; set; }

        private void PersistOriginalState(bool state)
        {
            try
            {
                if (string.IsNullOrEmpty(StateFile)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(StateFile));
                File.WriteAllText(StateFile, state ? "1" : "0");
            }
            catch (Exception ex) { logger.Warn("Could not persist FreeSync restore state: " + ex.Message); }
        }

        private void ClearPersistedState()
        {
            try { if (!string.IsNullOrEmpty(StateFile) && File.Exists(StateFile)) File.Delete(StateFile); }
            catch (Exception ex) { logger.Warn("Could not clear FreeSync restore state: " + ex.Message); }
        }

        /// <summary>Tries to put the original state back. Keeps the pending state if it fails.</summary>
        private bool TryRestore()
        {
            if (!originalState.HasValue) return true;
            if (!WriteState(originalState.Value)) return false;
            originalState = null;
            ClearPersistedState();
            return true;
        }

        /// <summary>
        /// Startup recovery: if a previous session ended (crash, kill, power loss) while FreeSync
        /// was being held off, put it back as it was before that session changed it.
        /// </summary>
        public void RecoverPending()
        {
            lock (gate)
            {
                try
                {
                    if (string.IsNullOrEmpty(StateFile) || !File.Exists(StateFile)) return;
                    if (requestingGames.Count > 0) return;
                    var saved = File.ReadAllText(StateFile).Trim();
                    if (saved != "1" && saved != "0") { ClearPersistedState(); return; }
                    var wanted = saved == "1";
                    var current = ReadState();
                    if (!current.HasValue) return; // Helper unavailable; keep the marker and retry next start.
                    if (current.Value == wanted || WriteState(wanted))
                    {
                        ClearPersistedState();
                        logger.Info("FreeSync restored after the previous session ended without restoring it.");
                    }
                    else logger.Warn("FreeSync recovery failed. Restore the original setting in AMD Software.");
                }
                catch (Exception ex) { logger.Warn("FreeSync recovery error: " + ex.Message); }
            }
        }

        /// <summary>Called on launch, a mode change, and a per-game toggle update.</summary>
        public void Update(Game game, bool requestDisable)
        {
            if (game == null) return;
            lock (gate)
            {
                if (requestDisable)
                {
                    if (requestingGames.Contains(game.Id)) return;
                    var current = ReadState();
                    if (!current.HasValue) return; // Unsupported: do not make speculative changes.
                    // A restore that failed earlier is still pending: keep its original state instead
                    // of mistaking our own "off" for the user's setting.
                    if (!originalState.HasValue)
                    {
                        originalState = current.Value;
                        if (current.Value) PersistOriginalState(true);
                    }
                    if (current.Value && !WriteState(false))
                    {
                        // No successful change; don't claim the game owns a FreeSync override.
                        if (requestingGames.Count == 0)
                        {
                            originalState = null;
                            ClearPersistedState();
                        }
                        return;
                    }
                    requestingGames.Add(game.Id);
                    logger.Info("FreeSync disabled/held for " + game.Name + ".");
                }
                else
                {
                    Release(game.Id);
                }
            }
        }

        public void Stop(Guid gameId)
        {
            lock (gate) Release(gameId);
        }

        private void Release(Guid id)
        {
            var wasHolding = requestingGames.Remove(id);
            if (requestingGames.Count > 0) return;
            // Also retries a restore that failed earlier, even if this game was not the holder.
            if (!originalState.HasValue) return;
            if (!TryRestore() && !TryRestore())
                logger.Warn("FreeSync restoration failed. It will be retried; restore it in AMD Software if it stays off.");
            else
                logger.Info("FreeSync previous state restored after game/session ended.");
        }

        public void RestoreAll()
        {
            lock (gate)
            {
                requestingGames.Clear();
                if (originalState.HasValue && !TryRestore() && !TryRestore())
                    logger.Warn("FreeSync restoration on exit failed; it will be retried on the next start.");
            }
        }
    }
}
