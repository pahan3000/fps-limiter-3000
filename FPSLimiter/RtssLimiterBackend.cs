using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace FPSLimiter
{
    public class RtssLimiterBackend
    {
        private const string GlobalProfileDisplayName = "Global";
        private const string GlobalProfileFileName = "Global";
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly FPSLimiterSettings settings;

        public RtssLimiterBackend(FPSLimiterSettings settings)
        {
            this.settings = settings;
        }

        public string ResolveRtssPath()
        {
            if (!string.IsNullOrWhiteSpace(settings.RtssPath) && File.Exists(settings.RtssPath))
            {
                return settings.RtssPath;
            }

            foreach (var process in Process.GetProcessesByName("RTSS"))
            {
                try
                {
                    var path = process.MainModule.FileName;
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
                catch (Exception e)
                {
                    logger.Debug(e, "Could not read RTSS process path.");
                }
            }

            var defaultPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "RivaTuner Statistics Server",
                "RTSS.exe");

            if (File.Exists(defaultPath))
            {
                return defaultPath;
            }

            return null;
        }

        public string ResolveProfilesDirectory()
        {
            var rtssPath = ResolveRequiredRtssPath();
            var profilesDirectory = GetProfilesDirectory(rtssPath);
            if (!Directory.Exists(profilesDirectory))
            {
                throw new DirectoryNotFoundException($"RTSS Profiles folder was not found: {profilesDirectory}");
            }

            return profilesDirectory;
        }

        public RtssProfilePermissionTestResult TestProfileWriteAccess()
        {
            string testPath = null;

            try
            {
                var rtssPath = ResolveRequiredRtssPath();
                var profilesDirectory = GetProfilesDirectory(rtssPath);
                if (!Directory.Exists(profilesDirectory))
                {
                    return RtssProfilePermissionTestResult.Fail(profilesDirectory, $"RTSS Profiles folder was not found: {profilesDirectory}");
                }

                testPath = Path.Combine(profilesDirectory, $"FPSLimiterPermissionTest_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(testPath, "fps-limiter permission test", Encoding.ASCII);
                var readback = File.ReadAllText(testPath, Encoding.ASCII);
                File.Delete(testPath);
                testPath = null;

                if (!string.Equals(readback, "fps-limiter permission test", StringComparison.Ordinal))
                {
                    return RtssProfilePermissionTestResult.Fail(profilesDirectory, "Temporary file read-back did not match.");
                }

                var globalPath = EnsureGlobalProfileFile(rtssPath);
                var globalText = File.ReadAllText(globalPath, Encoding.Default);
                File.WriteAllText(globalPath, globalText, Encoding.Default);

                return RtssProfilePermissionTestResult.Ok(profilesDirectory);
            }
            catch (Exception e)
            {
                var profilesDirectory = SafeGetProfilesDirectory();
                return RtssProfilePermissionTestResult.Fail(profilesDirectory, e.Message);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(testPath) && File.Exists(testPath))
                {
                    try
                    {
                        File.Delete(testPath);
                    }
                    catch
                    {
                    }
                }
            }
        }

        public void GrantProfilesModifyPermissionToCurrentUser()
        {
            var profilesDirectory = ResolveProfilesDirectory();
            var identity = WindowsIdentity.GetCurrent();
            var sid = identity?.User?.Value;
            if (string.IsNullOrWhiteSpace(sid))
            {
                throw new InvalidOperationException("Could not resolve the current Windows user SID.");
            }

            var arguments = $"\"{profilesDirectory}\" /grant *{sid}:(OI)(CI)M /T /C";
            var icaclsPath = Path.Combine(Environment.SystemDirectory, "icacls.exe");
            try
            {
                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = icaclsPath,
                    Arguments = arguments,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    if (process == null)
                    {
                        throw new InvalidOperationException("Windows did not start the elevated permission command.");
                    }

                    process.WaitForExit();
                    if (process.ExitCode != 0)
                    {
                        throw new InvalidOperationException($"The elevated permission command exited with code {process.ExitCode}.");
                    }
                }
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223)
            {
                throw new InvalidOperationException("Administrator approval was cancelled.", e);
            }
        }

        public bool EnsureRtssRunning(string rtssPath)
        {
            if (Process.GetProcessesByName("RTSS").Any())
            {
                return false;
            }

            if (!settings.AutoStartRtss)
            {
                return false;
            }

            if (!File.Exists(rtssPath))
            {
                throw new FileNotFoundException("RTSS.exe was not found.", rtssPath);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = rtssPath,
                WorkingDirectory = Path.GetDirectoryName(rtssPath),
                UseShellExecute = true
            });

            Thread.Sleep(1000);
            return true;
        }

        // Helper processes RTSS spawns on demand to load hooks into games and to run its video
        // capture pipeline. Under normal circumstances RTSS shuts these down itself when it exits,
        // but that only happens on a clean exit. Process.Kill() below is a hard, immediate
        // termination that gives RTSS no chance to clean up after itself, so these helpers are
        // orphaned and keep running indefinitely (visible in Task Manager as "RTSS Hooks Loader"
        // and "RTSS Encoder Server") even though the parent RTSS.exe is gone. Kill them explicitly
        // too so closing RTSS doesn't leave stragglers behind.
        private static readonly string[] RtssHelperProcessNames =
        {
            "RTSSHooksLoader64",
            "RTSSHooksLoader32",
            "RTSSHooksLoader",
            "EncoderServer",
            "EncoderServer32",
            "EncoderServer64"
        };

        public void CloseRtssProcess()
        {
            foreach (var process in Process.GetProcessesByName("RTSS"))
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(3000);
                    logger.Info("Closed RTSS because no FPS Limiter caps are active anymore.");
                }
                catch (Exception e)
                {
                    logger.Debug(e, "Could not close RTSS process.");
                }
                finally
                {
                    process.Dispose();
                }
            }

            CloseRtssHelperProcesses();
        }

        private static void CloseRtssHelperProcesses()
        {
            foreach (var name in RtssHelperProcessNames)
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(2000);
                        logger.Info($"Closed orphaned RTSS helper process {name} after closing RTSS.");
                    }
                    catch (Exception e)
                    {
                        logger.Debug(e, $"Could not close RTSS helper process {name}.");
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
        }

        public LimitSessionSnapshot ApplyLimit(Guid gameId, string gameName, string executablePath, double frameLimit, FpsSyncMode syncMode, bool forceGlobalProfile, bool disableReflexMarkers = false, bool? forceFlipModel = null, bool applyFramerate = true)
        {
            var rtssPath = ResolveRtssPath();
            if (string.IsNullOrWhiteSpace(rtssPath))
            {
                throw new InvalidOperationException("RTSS.exe was not found. Set the RTSS path in FPS Limiter 3000 settings.");
            }

            var startedRtss = EnsureRtssRunning(rtssPath);

            // The caller has already resolved the global-profile preference; an explicit
            // per-game override must be allowed to select the executable profile instead.
            var usesGlobalProfile = forceGlobalProfile;
            var profileName = usesGlobalProfile ? GlobalProfileDisplayName : Path.GetFileName(executablePath);
            var apiProfileName = usesGlobalProfile ? string.Empty : profileName;
            if (!usesGlobalProfile && string.IsNullOrWhiteSpace(profileName))
            {
                throw new InvalidOperationException("Could not resolve an RTSS profile name for the target executable.");
            }

            // Make the permanent Global defaults (Detours, Oswald Medium) stick before the snapshot is
            // taken, so the end-of-session restore can never roll them back.
            EnsureGlobalDefaults(rtssPath);

            var profileExisted = File.Exists(GetProfileFilePath(rtssPath, apiProfileName));
            var fileSnapshot = CaptureProfileFile(rtssPath, apiProfileName);

            try
            {
                ApplyProfileFileLimit(rtssPath, apiProfileName, frameLimit, syncMode, disableReflexMarkers, forceFlipModel, applyFramerate);
                RefreshRtssProfiles(rtssPath);
                var fileLimit = ReadProfileFileLimit(rtssPath, apiProfileName);
                logger.Info($"RTSS profile file {profileName} after apply: Limit={FormatFileReadback(fileLimit)}, Sync={syncMode}, ReflexMarkersDisabled={disableReflexMarkers}.");

                if (applyFramerate && (!fileLimit.HasValue || Math.Abs(fileLimit.Value - frameLimit) > 0.01))
                {
                    throw CreateProfileFilePersistenceException(rtssPath, profileName, frameLimit, null);
                }
                var written = File.ReadAllText(GetProfileFilePath(rtssPath, apiProfileName), Encoding.Default);
                if (GetProfileValue(written, "Hooking", "EnableHooking") != "1" ||
                    (forceFlipModel.HasValue &&
                     (GetProfileValue(written, "OverrideDXGI", "ForceFlipModel") != (forceFlipModel.Value ? "1" : "0") ||
                      GetProfileValue(written, "OverrideDXGI", "ForceAllowTearing") != (forceFlipModel.Value ? "1" : "0"))))
                    throw new InvalidOperationException("RTSS profile settings could not be verified after writing: " + profileName);
            }
            catch (Exception e)
            {
                // Do not leave a partially changed profile behind if writing or verification fails.
                try
                {
                    RestoreProfileFile(new LimitSessionSnapshot
                    {
                        ProfileName = profileName,
                        ProfileFilePath = fileSnapshot.Path,
                        OriginalProfileFileExisted = fileSnapshot.Exists,
                        OriginalProfileFileContent = fileSnapshot.Content
                    });
                    RefreshRtssProfiles(rtssPath);
                }
                catch (Exception restoreError)
                {
                    logger.Warn("Could not restore RTSS profile after a failed write: " + restoreError.Message);
                }
                if (e is InvalidOperationException) throw;
                if (!applyFramerate)
                    throw new InvalidOperationException("FPS Limiter 3000 could not update RTSS flip-model settings in profile " + profileName + ". Check RTSS profile-folder access. " + e.Message, e);
                throw CreateProfileFilePersistenceException(rtssPath, profileName, frameLimit, e);
            }

            return new LimitSessionSnapshot
            {
                GameId = gameId,
                GameName = gameName,
                ExecutablePath = executablePath,
                ProfileName = profileName,
                AppliedLimit = frameLimit,
                AppliedSyncMode = syncMode,
                AppliedScanlineTarget = (syncMode == FpsSyncMode.ScanlineSync || syncMode == FpsSyncMode.ScanlineSyncHalf)
                    ? (int?)(settings != null ? settings.RtssScanlineSyncTarget : -1) : null,
                AppliedReflexMarkersDisabled = disableReflexMarkers,
                AppliedForceFlipModel = forceFlipModel,
                AppliedFlipOnly = !applyFramerate,
                UsesGlobalProfile = usesGlobalProfile,
                ProfileExisted = profileExisted,
                FileFallbackUsed = true,
                ProfileFilePath = fileSnapshot.Path,
                OriginalProfileFileExisted = fileSnapshot.Exists,
                OriginalProfileFileContent = fileSnapshot.Content,
                StartedRtssProcess = startedRtss,
                StartedAt = DateTime.UtcNow,
                Backend = LimiterBackendKind.Rtss
            };
        }

        public void RestoreLimit(LimitSessionSnapshot snapshot)
        {
            var rtssPath = ResolveRtssPath();
            if (string.IsNullOrWhiteSpace(rtssPath))
            {
                throw new InvalidOperationException("RTSS.exe was not found. FPS Limiter 3000 cannot restore the previous RTSS profile yet.");
            }

            RestoreProfileFile(snapshot);

            if (Process.GetProcessesByName("RTSS").Any())
            {
                RefreshRtssProfiles(rtssPath);
            }

            logger.Info($"Restored RTSS profile file {snapshot.ProfileName} after {snapshot.GameName}.");
        }

        private static string FormatFileReadback(double? value)
        {
            return value.HasValue ? value.Value.ToString("0.####", CultureInfo.InvariantCulture) : "unavailable";
        }

        // RTSS Setup > Injection properties > "Use Microsoft Detours API hooking".
        private const string DetoursSection = "Hooking";
        private const string DetoursKey = "UseDetours";

        private const string FontSection = "Font";

        /// <summary>
        /// Keeps RTSS's Global profile on the plugin's permanent defaults:
        /// "Use Microsoft Detours API hooking" on, and (once the overlay preset and the Oswald font are both
        /// installed) the OSD font set to Oswald Medium. Does nothing when everything is already set or when
        /// RTSS / its Global profile cannot be found. Never throws.
        /// </summary>
        /// <summary>
        /// Keeps the optional overlay setup permanent once the overlay preset has been installed: both RTSS plugins ticked,
        /// OverlayEditor loading the preset, and the user's overlay toggle hotkey. Only acts when the preset is installed,
        /// RTSS is NOT running (RTSS rewrites these files from memory on exit) and the files are writable without
        /// administrator rights; otherwise "Install overlay preset" in settings does it with a UAC prompt. Never throws.
        /// </summary>
        public void EnsureOverlaySetup(string rtssPath = null)
        {
            try
            {
                rtssPath = rtssPath ?? ResolveRtssPath();
                if (string.IsNullOrWhiteSpace(rtssPath) || !IsOverlayPresetInstalled(rtssPath)) return;
                if (Process.GetProcessesByName("RTSS").Any()) return;

                var rtssDir = Path.GetDirectoryName(rtssPath);
                var clientDir = Path.Combine(rtssDir, "Plugins", "Client");
                if (!Directory.Exists(clientDir)) return;

                string Read(string path) => File.Exists(path) ? File.ReadAllText(path, Encoding.Default) : string.Empty;
                void Fix(string path, string section, params string[] keyValues)
                {
                    var text = Read(path);
                    var updated = text;
                    for (int i = 0; i + 1 < keyValues.Length; i += 2)
                    {
                        if (GetProfileValue(updated, section, keyValues[i]) != keyValues[i + 1])
                            updated = SetProfileValue(updated, section, keyValues[i], keyValues[i + 1]);
                    }
                    if (string.Equals(updated, text, StringComparison.Ordinal)) return;
                    try
                    {
                        File.WriteAllText(path, updated, Encoding.Default);
                        logger.Info("Restored RTSS overlay setup in " + Path.GetFileName(path));
                    }
                    catch (UnauthorizedAccessException)
                    {
                        logger.Info("RTSS overlay setup differs in " + Path.GetFileName(path) + " but needs administrator rights; use Install overlay preset to repair it.");
                    }
                }

                Fix(Path.Combine(rtssDir, "Profiles", "Config"), "Plugins", "HotkeyHandler.dll", "1", "OverlayEditor.dll", "1");
                Fix(Path.Combine(clientDir, "OverlayEditor.cfg"), "Settings", "Layout", RtssOverlayPreset.FileName);
                Fix(Path.Combine(clientDir, "HotkeyHandler.cfg"), "Settings", "OSDToggleHotkey", RtssHotkeyCodec.ToRtssValue(settings.RtssOverlayHotkey));
            }
            catch (Exception ex)
            {
                logger.Warn("Could not verify the RTSS overlay setup: " + ex.Message);
            }
        }

        public void EnsureGlobalDefaults(string rtssPath = null)
        {
            try
            {
                rtssPath = rtssPath ?? ResolveRtssPath();
                if (string.IsNullOrWhiteSpace(rtssPath)) return;
                EnsureOverlaySetup(rtssPath);

                var globalPath = EnsureGlobalProfileFile(rtssPath);
                if (!File.Exists(globalPath)) return;

                var text = File.ReadAllText(globalPath, Encoding.Default);
                var updated = text;

                if (GetProfileValue(updated, DetoursSection, DetoursKey) != "1")
                {
                    updated = SetProfileValue(updated, DetoursSection, DetoursKey, "1");
                }

                if (IsOverlayPresetInstalled(rtssPath) && OswaldFont.IsInstalled())
                {
                    if (GetProfileValue(updated, FontSection, "Face") != OswaldFont.FaceName)
                        updated = SetProfileValue(updated, FontSection, "Face", OswaldFont.FaceName);
                    if (GetProfileValue(updated, FontSection, "Weight") != OswaldFont.Weight)
                        updated = SetProfileValue(updated, FontSection, "Weight", OswaldFont.Weight);
                }

                if (string.Equals(updated, text, StringComparison.Ordinal)) return;

                File.WriteAllText(globalPath, updated, Encoding.Default);
                logger.Info("Updated RTSS Global profile defaults (Detours hooking / Oswald Medium font).");
                try { RefreshRtssProfiles(rtssPath); }
                catch (Exception ex) { logger.Debug(ex, "RTSS did not pick up the Global profile change immediately."); }
            }
            catch (Exception ex)
            {
                logger.Warn("Could not update RTSS Global profile defaults: " + ex.Message);
            }
        }

        private static bool IsOverlayPresetInstalled(string rtssPath)
        {
            try
            {
                return File.Exists(Path.Combine(Path.GetDirectoryName(rtssPath), "Plugins", "Client", "Overlays", RtssOverlayPreset.FileName));
            }
            catch
            {
                return false;
            }
        }

        private static void RefreshRtssProfiles(string rtssPath)
        {
            using (var refresher = new RtssProfileRefresher(rtssPath))
            {
                refresher.Initialize();
                refresher.UpdateProfiles();
            }
        }

        private string ResolveRequiredRtssPath()
        {
            var rtssPath = ResolveRtssPath();
            if (string.IsNullOrWhiteSpace(rtssPath))
            {
                throw new InvalidOperationException("RTSS.exe was not found. Set the RTSS path in FPS Limiter 3000 settings.");
            }

            return rtssPath;
        }

        private string SafeGetProfilesDirectory()
        {
            try
            {
                var rtssPath = ResolveRtssPath();
                return string.IsNullOrWhiteSpace(rtssPath) ? null : GetProfilesDirectory(rtssPath);
            }
            catch
            {
                return null;
            }
        }

        private static InvalidOperationException CreateProfileFilePersistenceException(
            string rtssPath,
            string profileName,
            double requestedLimit,
            Exception innerException)
        {
            var profilesDirectory = GetProfilesDirectory(rtssPath);
            var message =
                $"FPS Limiter 3000 could not write the requested {requestedLimit.ToString("0.####", CultureInfo.InvariantCulture)} FPS limit to RTSS profile {profileName}. " +
                $"This usually means Playnite cannot write to the RTSS Profiles folder: {profilesDirectory}. " +
                "Use FPS Limiter 3000's RTSS access setup, grant Modify permission to that folder, or install RTSS in a writable location.";

            return innerException == null
                ? new InvalidOperationException(message)
                : new InvalidOperationException(message, innerException);
        }

        private static ProfileFileSnapshot CaptureProfileFile(string rtssPath, string apiProfileName)
        {
            var path = GetProfileFilePath(rtssPath, apiProfileName);
            if (!File.Exists(path))
            {
                return new ProfileFileSnapshot { Path = path };
            }

            return new ProfileFileSnapshot
            {
                Path = path,
                Exists = true,
                Content = File.ReadAllText(path, Encoding.Default)
            };
        }

        // RTSS profile key used for the "Inject NVIDIA Reflex latency markers" checkbox in RTSS's
        // own Setup dialog (Injection properties). Confirmed by diffing a real RTSS profile .cfg
        // file: it's ReflexSetLatencyMarker under [Framerate], 1 = enabled (RTSS's default), 0 =
        // disabled.
        private const string ReflexMarkersSection = "Framerate";
        private const string ReflexMarkersKey = "ReflexSetLatencyMarker";

        private void ApplyProfileFileLimit(string rtssPath, string apiProfileName, double frameLimit, FpsSyncMode syncMode, bool disableReflexMarkers, bool? forceFlipModel, bool applyFramerate)
        {
            var path = GetProfileFilePath(rtssPath, apiProfileName);
            var profileText = File.Exists(path)
                ? File.ReadAllText(path, Encoding.Default)
                : ReadDefaultProfileText(rtssPath);

            if (applyFramerate)
            {
                int numerator;
                int denominator;
                ToFraction(frameLimit, out numerator, out denominator);

                profileText = SetProfileValue(profileText, "Framerate", "Limit", numerator.ToString(CultureInfo.InvariantCulture));
                profileText = SetProfileValue(profileText, "Framerate", "LimitDenominator", denominator.ToString(CultureInfo.InvariantCulture));

                if (syncMode == FpsSyncMode.ScanlineSync || syncMode == FpsSyncMode.ScanlineSyncHalf)
                {
                    // Scanline sync pairs best with Front Edge Sync (hybrid).
                    profileText = SetProfileValue(profileText, "Framerate", "SyncLimiter", ((int)FpsSyncMode.FrontEdgeSync).ToString());
                    var scanline = settings != null ? settings.RtssScanlineSyncTarget : -1;
                    profileText = SetProfileValue(profileText, "Framerate", "SyncScanline0", scanline.ToString(CultureInfo.InvariantCulture));
                    profileText = SetProfileValue(profileText, "Framerate", "SyncScanline1", "0");
                    profileText = SetProfileValue(profileText, "Framerate", "SyncPeriods", syncMode == FpsSyncMode.ScanlineSyncHalf ? "1" : "0");
                    profileText = SetProfileValue(profileText, "Framerate", "SyncFlush", "1");
                }
                else
                {
                    profileText = SetProfileValue(profileText, "Framerate", "SyncLimiter", ((int)syncMode).ToString());
                    profileText = SetProfileValue(profileText, "Framerate", "SyncScanline0", "0");
                    profileText = SetProfileValue(profileText, "Framerate", "SyncScanline1", "0");
                    profileText = SetProfileValue(profileText, "Framerate", "SyncPeriods", "0");
                }

                profileText = SetProfileValue(profileText, ReflexMarkersSection, ReflexMarkersKey, disableReflexMarkers ? "0" : "1");
            }
            profileText = SetProfileValue(profileText, "Hooking", "EnableHooking", "1");
            // "Use Microsoft Detours API hooking" (RTSS Setup > Injection properties) is on by
            // default for every profile this plugin writes. Confirmed by diffing two real Global
            // profiles: it is UseDetours under [Hooking], 1 = enabled, 0 = disabled.
            profileText = SetProfileValue(profileText, DetoursSection, DetoursKey, "1");
            if (forceFlipModel.HasValue)
            {
                var value = forceFlipModel.Value ? "1" : "0";
                profileText = SetProfileValue(profileText, "OverrideDXGI", "ForceFlipModel", value);
                profileText = SetProfileValue(profileText, "OverrideDXGI", "ForceAllowTearing", value);
            }

            // FCAT is a global RTSS preference. Applying or changing an FPS cap must not
            // silently reset the indicator based on a stale per-game profile. Mirror the
            // current Global profile value into the profile being edited.
            try
            {
                var globalPath = GetProfileFilePath(rtssPath, string.Empty);
                if (!string.Equals(path, globalPath, StringComparison.OrdinalIgnoreCase) && File.Exists(globalPath))
                {
                    var globalText = File.ReadAllText(globalPath, Encoding.Default);
                    var fcatValue = GetProfileValue(globalText, FrameColorBarSection, FrameColorBarKey);
                    if (fcatValue != null)
                    {
                        profileText = SetProfileValue(profileText, FrameColorBarSection, FrameColorBarKey, fcatValue);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "Could not preserve the global RTSS FCAT indicator setting while applying a cap.");
            }

            File.WriteAllText(path, profileText, Encoding.Default);
        }

        // RTSS stores the limiter target as Limit / LimitDenominator, which lets it represent
        // fractional caps (e.g. 59.9, 23.976) precisely instead of only whole numbers.
        private static void ToFraction(double frameLimit, out int numerator, out int denominator)
        {
            // Preserve AMD/custom mode timing such as 29.9001 Hz (4 decimal places).
            denominator = 10000;
            numerator = (int)Math.Round(frameLimit * denominator, MidpointRounding.AwayFromZero);

            // RTSS expects a power-of-ten denominator (the same form its own UI writes, e.g.
            // 299014/10000). A general GCD reduction (149507/5000) is read back with lost
            // precision (29.901), so only strip trailing decimal zeros.
            while (denominator > 1 && numerator % 10 == 0)
            {
                numerator /= 10;
                denominator /= 10;
            }

            if (denominator <= 0)
            {
                denominator = 1;
            }
        }

        private static int Gcd(int a, int b)
        {
            a = Math.Abs(a);
            b = Math.Abs(b);
            while (b != 0)
            {
                var t = b;
                b = a % b;
                a = t;
            }

            return a == 0 ? 1 : a;
        }

        private static double? ReadProfileFileLimit(string rtssPath, string apiProfileName)
        {
            var path = GetProfileFilePath(rtssPath, apiProfileName);
            if (!File.Exists(path))
            {
                return null;
            }

            var profileText = File.ReadAllText(path, Encoding.Default);
            var limitValue = GetProfileValue(profileText, "Framerate", "Limit");
            var denominatorValue = GetProfileValue(profileText, "Framerate", "LimitDenominator");

            int limit;
            if (!int.TryParse(limitValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit))
            {
                return null;
            }

            int denominator;
            if (string.IsNullOrWhiteSpace(denominatorValue) ||
                !int.TryParse(denominatorValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out denominator) ||
                denominator <= 0)
            {
                denominator = 1;
            }

            return (double)limit / denominator;
        }

        private static void RestoreProfileFile(LimitSessionSnapshot snapshot)
        {
            if (string.IsNullOrWhiteSpace(snapshot.ProfileFilePath))
            {
                throw new InvalidOperationException($"RTSS profile file path was not captured for {snapshot.ProfileName}.");
            }

            if (snapshot.OriginalProfileFileExisted)
            {
                var content = snapshot.OriginalProfileFileContent ?? string.Empty;

                // The snapshot was taken before the limit was applied. If the FCAT frame color
                // indicator was toggled (hotkey or RTSS UI) since then, keep the current value
                // instead of rolling it back to the stale one in the snapshot.
                try
                {
                    if (File.Exists(snapshot.ProfileFilePath))
                    {
                        var currentFcat = GetProfileValue(File.ReadAllText(snapshot.ProfileFilePath, Encoding.Default), FrameColorBarSection, FrameColorBarKey);
                        var snapshotFcat = GetProfileValue(content, FrameColorBarSection, FrameColorBarKey);
                        if (currentFcat != null && currentFcat != snapshotFcat)
                        {
                            content = SetProfileValue(content, FrameColorBarSection, FrameColorBarKey, currentFcat);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.Debug(ex, "Could not carry the FCAT indicator state over the profile restore.");
                }

                // "Use Microsoft Detours API hooking" stays on permanently: never restore a stale 0.
                if (GetProfileValue(content, DetoursSection, DetoursKey) != "1")
                {
                    content = SetProfileValue(content, DetoursSection, DetoursKey, "1");
                }

                File.WriteAllText(snapshot.ProfileFilePath, content, Encoding.Default);
            }
            else if (File.Exists(snapshot.ProfileFilePath))
            {
                File.Delete(snapshot.ProfileFilePath);
            }
        }

        // RTSS Setup > General > FCAT properties > "Enable frame color indicator".
        // Confirmed from a real Global profile: [OSD] EnableFrameColorBar (1 = on, 0 = off).
        // FrameColorBarMode (0 = color sequence) is deliberately left untouched.
        private const string FrameColorBarSection = "OSD";
        private const string FrameColorBarKey = "EnableFrameColorBar";

        /// <summary>
        /// Flips RTSS's FCAT frame color indicator and asks RTSS to reload its profiles.
        /// Returns the new state (true = enabled).
        /// </summary>
        public bool ToggleFrameColorIndicator()
        {
            var rtssPath = ResolveRequiredRtssPath();
            var globalPath = EnsureGlobalProfileFile(rtssPath);

            var globalText = File.ReadAllText(globalPath, Encoding.Default);
            var enable = GetProfileValue(globalText, FrameColorBarSection, FrameColorBarKey) != "1";
            var value = enable ? "1" : "0";

            File.WriteAllText(globalPath, SetProfileValue(globalText, FrameColorBarSection, FrameColorBarKey, value), Encoding.Default);

            // Per-game profiles created from the Global template carry their own copy of the key
            // and would otherwise keep the old state while that game is running.
            foreach (var file in Directory.GetFiles(GetProfilesDirectory(rtssPath), "*.cfg"))
            {
                try
                {
                    var text = File.ReadAllText(file, Encoding.Default);
                    var current = GetProfileValue(text, FrameColorBarSection, FrameColorBarKey);
                    if (current == null || current == value) continue;
                    File.WriteAllText(file, SetProfileValue(text, FrameColorBarSection, FrameColorBarKey, value), Encoding.Default);
                }
                catch (Exception ex)
                {
                    logger.Warn("FCAT toggle: could not update " + Path.GetFileName(file) + ": " + ex.Message);
                }
            }

            RefreshRtssProfiles(rtssPath);
            return enable;
        }

        private static string ReadDefaultProfileText(string rtssPath)
        {
            var globalPath = EnsureGlobalProfileFile(rtssPath);
            return File.ReadAllText(globalPath, Encoding.Default);
        }

        /// <summary>
        /// A fresh RTSS install has a Profiles folder but no Global profile until RTSS saves its settings once.
        /// Creates an (effectively) empty Global file in that case (RTSS treats missing keys as its defaults) so the limiter
        /// works out of the box. Returns the Global profile path. Throws if the file cannot be created.
        /// </summary>
        private static string EnsureGlobalProfileFile(string rtssPath)
        {
            var globalPath = GetProfileFilePath(rtssPath, string.Empty);
            if (!File.Exists(globalPath) && Directory.Exists(GetProfilesDirectory(rtssPath)))
            {
                File.WriteAllText(globalPath, "\r\n", Encoding.Default); // effectively empty; the CRLF keeps later INI edits in Windows line endings
            }

            return globalPath;
        }

        private static string GetProfilesDirectory(string rtssPath)
        {
            return Path.Combine(Path.GetDirectoryName(rtssPath), "Profiles");
        }

        private static string GetProfileFilePath(string rtssPath, string apiProfileName)
        {
            var fileName = string.IsNullOrWhiteSpace(apiProfileName)
                ? GlobalProfileFileName
                : apiProfileName.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)
                    ? apiProfileName
                    : apiProfileName + ".cfg";

            return Path.Combine(GetProfilesDirectory(rtssPath), fileName);
        }

        private static string SetProfileValue(string text, string section, string key, string value)
        {
            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var normalized = text.Replace("\r\n", "\n");
            var lines = normalized.Split('\n').ToList();
            var sectionHeader = "[" + section + "]";
            var inSection = false;
            var sectionFound = false;

            for (var i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    if (inSection)
                    {
                        lines.Insert(i, key + "=" + value);
                        return string.Join(newline, lines);
                    }

                    inSection = string.Equals(trimmed, sectionHeader, StringComparison.OrdinalIgnoreCase);
                    sectionFound = sectionFound || inSection;
                    continue;
                }

                var equals = trimmed.IndexOf('=');
                if (inSection && equals > 0 && string.Equals(trimmed.Substring(0, equals).Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = key + "=" + value;
                    return string.Join(newline, lines);
                }
            }

            if (sectionFound)
            {
                lines.Add(key + "=" + value);
            }
            else
            {
                if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines.Last()))
                {
                    lines.Add(string.Empty);
                }

                lines.Add(sectionHeader);
                lines.Add(key + "=" + value);
            }

            return string.Join(newline, lines);
        }

        private static string GetProfileValue(string text, string section, string key)
        {
            var normalized = text.Replace("\r\n", "\n");
            var lines = normalized.Split('\n');
            var sectionHeader = "[" + section + "]";
            var inSection = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    inSection = string.Equals(trimmed, sectionHeader, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                var equals = trimmed.IndexOf('=');
                if (inSection && equals > 0 && string.Equals(trimmed.Substring(0, equals).Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed.Substring(equals + 1).Trim();
                }
            }

            return null;
        }

        private class ProfileFileSnapshot
        {
            public string Path { get; set; }
            public bool Exists { get; set; }
            public string Content { get; set; }
        }

        public class RtssProfilePermissionTestResult
        {
            public bool Success { get; private set; }
            public string ProfilesDirectory { get; private set; }
            public string Message { get; private set; }

            public static RtssProfilePermissionTestResult Ok(string profilesDirectory)
            {
                return new RtssProfilePermissionTestResult
                {
                    Success = true,
                    ProfilesDirectory = profilesDirectory,
                    Message = "OK"
                };
            }

            public static RtssProfilePermissionTestResult Fail(string profilesDirectory, string message)
            {
                return new RtssProfilePermissionTestResult
                {
                    Success = false,
                    ProfilesDirectory = profilesDirectory,
                    Message = message
                };
            }
        }

        private class RtssProfileRefresher : IDisposable
        {
            private readonly string rtssPath;
            private IntPtr libraryHandle;
            private UpdateProfilesDelegate updateProfiles;

            public RtssProfileRefresher(string rtssPath)
            {
                this.rtssPath = rtssPath;
            }

            public void Initialize()
            {
                var rtssDirectory = Path.GetDirectoryName(rtssPath);
                var hooksName = Environment.Is64BitProcess ? "RTSSHooks64.dll" : "RTSSHooks.dll";
                var hooksPath = Path.Combine(rtssDirectory, hooksName);

                if (!File.Exists(hooksPath) && Environment.Is64BitProcess)
                {
                    hooksPath = Path.Combine(rtssDirectory, "RTSSHooks.dll");
                }

                if (!File.Exists(hooksPath))
                {
                    throw new FileNotFoundException("RTSS hooks DLL was not found.", hooksPath);
                }

                libraryHandle = LoadLibrary(hooksPath);
                if (libraryHandle == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"Could not load {hooksPath}.");
                }

                updateProfiles = GetExport<UpdateProfilesDelegate>("UpdateProfiles");
            }

            public void UpdateProfiles()
            {
                updateProfiles();
            }

            public void Dispose()
            {
                if (libraryHandle != IntPtr.Zero)
                {
                    FreeLibrary(libraryHandle);
                    libraryHandle = IntPtr.Zero;
                }
            }

            private T GetExport<T>(string name) where T : class
            {
                var pointer = GetProcAddress(libraryHandle, name);
                if (pointer == IntPtr.Zero)
                {
                    throw new MissingMethodException($"RTSS hooks DLL does not export {name}.");
                }

                return Marshal.GetDelegateForFunctionPointer(pointer, typeof(T)) as T;
            }

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void UpdateProfilesDelegate();

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern IntPtr LoadLibrary(string lpFileName);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
            private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool FreeLibrary(IntPtr hModule);
        }
    }
}
