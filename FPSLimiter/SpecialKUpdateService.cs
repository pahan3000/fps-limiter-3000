using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace FPSLimiter
{
    /// <summary>
    /// Checks the public Special K Windows workflow artifact and, with user approval,
    /// updates the installed Special K core DLLs while keeping a rollback copy.
    /// SKIF itself is not part of the supplied workflow artifact.
    /// </summary>
    public sealed class SpecialKUpdateService
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly Uri WorkflowPage =
            new Uri("https://nightly.link/SpecialKO/SpecialK/workflows/build-windows/main");
        // Same manifest SKIF reads for its Discord / Website / Ancient branches.
        private static readonly Uri DiscordManifest =
            new Uri("https://sk-data.special-k.info/repository.json");
        private static readonly SemaphoreSlim checkLock = new SemaphoreSlim(1, 1);

        private readonly FPSLimiterSettingsViewModel settingsViewModel;
        private readonly LimiterService limiterService;
        private readonly string pluginDataPath;
        private readonly INotificationsAPI notifications;
        private readonly Dispatcher dispatcher;
        private readonly IPlayniteAPI playniteApi;

        private sealed class BuildArtifact
        {
            public string Name { get; set; }
            public Uri DownloadUri { get; set; }
            /// <summary>True for an Inno Setup installer (Discord branch); false for a zip of DLLs (nightly).</summary>
            public bool IsInstaller { get; set; }
            public string Sha256 { get; set; }
        }

        private sealed class ManifestRoot { public ManifestMain Main { get; set; } }
        private sealed class ManifestMain { public List<ManifestVersion> Versions { get; set; } }
        private sealed class ManifestVersion
        {
            public string Name { get; set; }
            public List<string> Branches { get; set; }
            public string Installer { get; set; }
            public string SHA256 { get; set; }
        }

        private sealed class StagedDll
        {
            public string FileName { get; set; }
            public string StagedPath { get; set; }
            public string TargetPath { get; set; }
        }

        public SpecialKUpdateService(
            FPSLimiterSettingsViewModel settingsViewModel,
            LimiterService limiterService,
            string pluginDataPath,
            IPlayniteAPI api)
        {
            this.settingsViewModel = settingsViewModel;
            this.limiterService = limiterService;
            this.pluginDataPath = pluginDataPath;
            playniteApi = api;
            PurgeOldBackups();
            notifications = api != null ? api.Notifications : null;
            dispatcher = Application.Current != null
                ? Application.Current.Dispatcher
                : Dispatcher.CurrentDispatcher;
        }

        public async Task CheckIfDueAsync()
        {
            var settings = settingsViewModel.Settings;
            if (settings == null)
            {
                return;
            }

            bool autoInstallOnLaunch = settings.SpecialKAutoUpdateOnLaunch;
            if (!autoInstallOnLaunch && settings.SpecialKUpdateFrequency == SpecialKUpdateInterval.Never)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            DateTime last = settings.SpecialKUpdateLastCheckUtc;
            if (last.Kind != DateTimeKind.Utc && last != DateTime.MinValue)
            {
                last = DateTime.SpecifyKind(last, DateTimeKind.Utc);
            }

            bool due = autoInstallOnLaunch || last == DateTime.MinValue;
            if (!due)
            {
                switch (settings.SpecialKUpdateFrequency)
                {
                    case SpecialKUpdateInterval.OnLaunch:
                        due = true;
                        break;
                    case SpecialKUpdateInterval.Daily:
                        due = now >= last.AddDays(1);
                        break;
                    case SpecialKUpdateInterval.Weekly:
                        due = now >= last.AddDays(7);
                        break;
                    case SpecialKUpdateInterval.Monthly:
                        due = now >= last.AddMonths(1);
                        break;
                }
            }

            if (due)
            {
                await CheckAsync(false, autoInstallOnLaunch).ConfigureAwait(false);
            }
        }

        public Task CheckNowAsync()
        {
            // A manual check always runs, even when scheduled checks are disabled.
            return CheckAsync(true, false);
        }

        public Task UpdateNowAsync()
        {
            return CheckAsync(true, true);
        }

        private async Task CheckAsync(bool manual, bool autoInstall)
        {
            if (!await checkLock.WaitAsync(0).ConfigureAwait(false))
            {
                if (!manual)
                {
                    return;
                }

                // A scheduled launch check may already be running when the user
                // presses "Check for updates now". Queue the manual request behind
                // it instead of returning a dead-end "already in progress" message.
                await checkLock.WaitAsync().ConfigureAwait(false);
            }

            string workDir = null;
            bool installing = false;
            try
            {
                var settings = settingsViewModel.Settings;
                string installDir = limiterService.ResolveSpecialKBinaryDirectoryPublic();
                if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
                {
                    if (manual)
                    {
                        ShowMessage("A global Special K installation was not found. Install Special K + SKIF first.",
                            "Special K updates", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return;
                }

                var installedDlls = Directory.GetFiles(installDir, "SpecialK*.dll", SearchOption.TopDirectoryOnly);
                if (installedDlls.Length == 0)
                {
                    if (manual)
                    {
                        ShowMessage("No global Special K DLLs were found in:\n" + installDir +
                            "",
                            "Special K updates", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return;
                }

                bool discord = settings.SpecialKUpdateChannel == SpecialKUpdateChannel.Discord;
                BuildArtifact latest = discord
                    ? await FindLatestDiscordBuildAsync().ConfigureAwait(false)
                    : await FindLatestArtifactAsync().ConfigureAwait(false);
                settings.SpecialKUpdateLastCheckUtc = DateTime.UtcNow;

                if (string.Equals(latest.Name, settings.SpecialKUpdateLastInstalledArtifact,
                    StringComparison.OrdinalIgnoreCase))
                {
                    settings.SpecialKUpdateLastCheckedArtifact = latest.Name;
                    settingsViewModel.SaveSettings();
                    if (manual)
                    {
                        ShowMessage("Special K is up to date (" + latest.Name + ").",
                            "Special K updates", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return;
                }

                bool alreadyPrompted = string.Equals(
                    latest.Name, settings.SpecialKUpdateLastCheckedArtifact, StringComparison.OrdinalIgnoreCase);
                settings.SpecialKUpdateLastCheckedArtifact = latest.Name;
                settingsViewModel.SaveSettings();

                if (!manual && alreadyPrompted && !autoInstall)
                {
                    return;
                }

                if ((!alreadyPrompted || manual) && settings.SpecialKNotifyUpdateAvailable)
                {
                    Notify("specialk-update-available",
                        "Special K has an update available: " + latest.Name, NotificationType.Info);
                }

                if (!autoInstall)
                {
                    if (manual)
                        ShowMessage("Special K: latest build " + latest.Name +
                            ".\n\nUse Update now to install it.",
                            "FPS Limiter 3000 updates", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (limiterService.HasRunningGamesPublic())
                {
                    if (manual)
                    {
                        ShowMessage("A Special K build is available, but a game launched through Playnite is still running. Close it before installing.",
                            "Special K update available", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else logger.Info("Special K update deferred because a Playnite-launched game is still running.");
                    return;
                }

                installing = true;
                if (limiterService.HasRunningGamesPublic())
                {
                    throw new InvalidOperationException("A game started while the update was beginning. Close it and try again.");
                }

                workDir = Path.Combine(Path.GetTempPath(), "FPSLimiter-SpecialKUpdate-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workDir);
                if (latest.IsInstaller)
                {
                    string exePath = Path.Combine(workDir, latest.Name + ".exe");
                    await DownloadArtifactAsync(latest.DownloadUri, exePath).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(latest.Sha256) ||
                        !string.Equals(ComputeSha256(exePath), latest.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "The downloaded Special K installer failed its SHA-256 check (or the manifest has no hash). Nothing was installed.");
                    }

                    if (limiterService.IsSpecialKServiceHostRunningPublic() &&
                        !limiterService.StopSpecialKServicePublic())
                    {
                        throw new InvalidOperationException(
                            "The Special K injection service did not stop. Close SKIF and games using Special K, then try again.");
                    }

                    string installerBackup = await RunInstallerWithBackupAsync(exePath, installDir, latest.Name).ConfigureAwait(false);
                    settings.SpecialKUpdateLastInstalledArtifact = latest.Name;
                    settings.SpecialKUpdateLastCheckedArtifact = latest.Name;
                    settingsViewModel.SaveSettings();
                    logger.Info("Special K updated to " + latest.Name + ".");
                    string localSummary1 = SyncLocalSpecialKDlls(installDir);
                    if (manual) ShowMessage("Special K updated to " + latest.Name + "." + localSummary1, "FPS Limiter 3000 updates", MessageBoxButton.OK, MessageBoxImage.Information);
                    if (settings.SpecialKNotifyUpdateInstalled)
                    {
                        Notify("specialk-update-installed",
                            "Special K has been updated to " + latest.Name, NotificationType.Info);
                    }
                    return;
                }

                string zipPath = Path.Combine(workDir, latest.Name + ".zip");
                await DownloadArtifactAsync(latest.DownloadUri, zipPath).ConfigureAwait(false);
                List<StagedDll> dlls = ExtractAndValidateDlls(zipPath, workDir, installDir);
                bool updateAvailable = dlls.Any(dll =>
                    !File.Exists(dll.TargetPath) ||
                    !string.Equals(ComputeSha256(dll.StagedPath), ComputeSha256(dll.TargetPath), StringComparison.OrdinalIgnoreCase));
                if (!updateAvailable)
                {
                    settings.SpecialKUpdateLastInstalledArtifact = latest.Name;
                    settings.SpecialKUpdateLastCheckedArtifact = latest.Name;
                    settingsViewModel.SaveSettings();
                    string localSummary3 = SyncLocalSpecialKDlls(installDir);
                    if (manual)
                    {
                        ShowMessage("The downloaded build matches the Special K DLLs already installed. No global files were changed." + localSummary3,
                            "Special K updates", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return;
                }

                if (limiterService.IsSpecialKServiceHostRunningPublic() &&
                    !limiterService.StopSpecialKServicePublic())
                {
                    throw new InvalidOperationException(
                        "The Special K injection service did not stop. Close SKIF and games using Special K, then try again.");
                }

                string backupDir = InstallWithBackup(dlls, latest.Name);
                settings.SpecialKUpdateLastInstalledArtifact = latest.Name;
                settings.SpecialKUpdateLastCheckedArtifact = latest.Name;
                settingsViewModel.SaveSettings();
                logger.Info("Special K updated to " + latest.Name + ".");
                string localSummary2 = SyncLocalSpecialKDlls(installDir);
                if (manual) ShowMessage("Special K core DLLs updated to " + latest.Name + "." + localSummary2, "FPS Limiter 3000 updates", MessageBoxButton.OK, MessageBoxImage.Information);

                if (settings.SpecialKNotifyUpdateInstalled)
                {
                    Notify("specialk-update-installed",
                        "Special K has been updated to " + latest.Name, NotificationType.Info);
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Special K update check/install failed.");
                if (installing)
                {
                    settingsViewModel.Settings.SpecialKUpdateLastCheckedArtifact = string.Empty;
                    settingsViewModel.SaveSettings();
                }
                if (manual)
                {
                    ShowMessage("Could not check or install the Special K update.\n\n" + e.Message,
                        "Special K updates", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else logger.Warn("Special K update check/install failed: " + e.Message);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(workDir))
                {
                    TryDeleteDirectory(workDir);
                }
                checkLock.Release();
            }
        }

        private void Notify(string id, string message, NotificationType type)
        {
            if (notifications == null)
            {
                return;
            }

            Action addNotification = () => notifications.Add(id, message, type);
            if (dispatcher.CheckAccess())
            {
                addNotification();
            }
            else
            {
                dispatcher.BeginInvoke(addNotification);
            }
        }

        private static async Task<BuildArtifact> FindLatestArtifactAsync()
        {
            using (var client = CreateHttpClient(TimeSpan.FromSeconds(10)))
            {
                string html = await client.GetStringAsync(WorkflowPage).ConfigureAwait(false);
                var workflowDirectory = new Uri(WorkflowPage.AbsoluteUri.TrimEnd('/') + "/");
                var matches = Regex.Matches(html,
                    "href\\s*=\\s*[\\\"'](?<url>[^\\\"']+\\.zip(?:\\?[^\\\"']*)?)[\\\"']",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);

                foreach (Match match in matches)
                {
                    string href = WebUtility.HtmlDecode(match.Groups["url"].Value);
                    Uri candidate;
                    if (!Uri.TryCreate(workflowDirectory, href, out candidate) ||
                        candidate.Scheme != Uri.UriSchemeHttps ||
                        !candidate.Host.Equals("nightly.link", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string path = Uri.UnescapeDataString(candidate.AbsolutePath);
                    const string expectedPrefix = "/SpecialKO/SpecialK/workflows/build-windows/main/";
                    if (!path.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string fileName = path.Substring(expectedPrefix.Length);
                    if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string artifactName = fileName.Substring(0, fileName.Length - 4);
                    if (!artifactName.StartsWith("SpecialK_", StringComparison.OrdinalIgnoreCase) ||
                        artifactName.IndexOf("debug_symbols", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }

                    if (!Regex.IsMatch(artifactName, @"^SpecialK_[A-Za-z0-9._-]+$"))
                    {
                        throw new InvalidOperationException("The workflow returned an unexpected artifact name.");
                    }

                    return new BuildArtifact { Name = artifactName, DownloadUri = candidate };
                }
            }

            throw new InvalidOperationException(
                "Could not find the Special K Windows build artifact on nightly.link. The workflow page may have changed.");
        }

        /// <summary>
        /// Reads the same repository.json SKIF uses and returns the newest build on the "Discord" branch
        /// (the manifest lists newest first; only entries whose Branches contain exactly "Discord" count,
        /// so Discord-NOCEGUI / -Experimental / Testing builds are skipped).
        /// </summary>
        /// <summary>Newest Discord-branch installer: URL, SHA-256 (may be empty) and version name.</summary>
        public static async Task<Tuple<Uri, string, string>> GetLatestDiscordInstallerAsync()
        {
            var build = await FindLatestDiscordBuildAsync().ConfigureAwait(false);
            return Tuple.Create(build.DownloadUri, build.Sha256, build.Name.Replace("SpecialK-Discord-", string.Empty));
        }

        private static async Task<BuildArtifact> FindLatestDiscordBuildAsync()
        {
            string json;
            using (var client = CreateHttpClient(TimeSpan.FromSeconds(20)))
            {
                json = await client.GetStringAsync(DiscordManifest).ConfigureAwait(false);
            }

            var root = Playnite.SDK.Data.Serialization.FromJson<ManifestRoot>(json);
            var versions = root != null && root.Main != null ? root.Main.Versions : null;
            if (versions != null)
            {
                foreach (var v in versions)
                {
                    if (v == null || v.Branches == null ||
                        !v.Branches.Any(b => string.Equals(b, "Discord", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    string name = v.Name;
                    Uri uri;
                    if (string.IsNullOrWhiteSpace(name) || !Regex.IsMatch(name, @"^[0-9A-Za-z._-]+$") ||
                        !Uri.TryCreate(v.Installer, UriKind.Absolute, out uri) ||
                        uri.Scheme != Uri.UriSchemeHttps ||
                        !uri.Host.Equals("sk-data.special-k.info", StringComparison.OrdinalIgnoreCase) ||
                        !uri.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return new BuildArtifact
                    {
                        Name = "SpecialK-Discord-" + name,
                        DownloadUri = uri,
                        IsInstaller = true,
                        Sha256 = v.SHA256
                    };
                }
            }

            throw new InvalidOperationException("Could not find a Discord-branch build in the Special K repository manifest.");
        }

        /// <summary>
        /// Backs up the installed SpecialK*.dll files, runs the official installer silently into the
        /// existing install folder, and restores the backup if the installer reports failure.
        /// </summary>
        private async Task<string> RunInstallerWithBackupAsync(string exePath, string installDir, string artifactName)
        {
            // Temporary rollback copy only: it is deleted when the update finishes (success or failure).
            string backupDir = Path.Combine(Path.GetTempPath(), "FPSLimiter-SKRollback-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backupDir);
            try
            {
            var backedUp = new List<string>();
            foreach (var dll in Directory.GetFiles(installDir, "SpecialK*.dll", SearchOption.TopDirectoryOnly))
            {
                File.Copy(dll, Path.Combine(backupDir, Path.GetFileName(dll)), false);
                backedUp.Add(Path.GetFileName(dll));
            }

            int exitCode;
            var psi = new ProcessStartInfo(exePath,
                "/SILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL /DIR=\"" + installDir.TrimEnd('\\') + "\"")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exePath)
            };
            using (var proc = Process.Start(psi))
            {
                if (proc == null) throw new InvalidOperationException("Could not start the Special K installer.");
                using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10)))
                {
                    while (!proc.HasExited)
                    {
                        if (timeout.IsCancellationRequested)
                        {
                            try { proc.Kill(); } catch { }
                            throw new InvalidOperationException("The Special K installer timed out.");
                        }
                        await Task.Delay(500).ConfigureAwait(false);
                    }
                }
                exitCode = proc.ExitCode;
            }

            if (exitCode != 0)
            {
                foreach (var name in backedUp)
                {
                    try { File.Copy(Path.Combine(backupDir, name), Path.Combine(installDir, name), true); }
                    catch (Exception e) { logger.Error(e, "Could not restore " + name + " after installer failure."); }
                }
                throw new InvalidOperationException("The Special K installer exited with code " + exitCode + ". Your previous DLLs were restored.");
            }

            return string.Empty;
            }
            finally { TryDeleteDirectory(backupDir); }
        }

        /// <summary>Removes rollback folders left by earlier versions of the extension.</summary>
        private void PurgeOldBackups()
        {
            try { TryDeleteDirectory(Path.Combine(pluginDataPath, "SpecialKBackups")); }
            catch { }
        }

        private static async Task DownloadArtifactAsync(Uri uri, string destination)
        {
            using (var client = CreateHttpClient(TimeSpan.FromMinutes(3)))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
            using (var response = await client.GetAsync(
                uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                Uri finalUri = response.RequestMessage != null ? response.RequestMessage.RequestUri : uri;
                if (!IsAllowedDownloadHost(finalUri))
                {
                    throw new InvalidOperationException("The build download redirected to an unexpected host.");
                }

                const long maxSize = 512L * 1024L * 1024L;
                if (response.Content.Headers.ContentLength.HasValue &&
                    (response.Content.Headers.ContentLength.Value < 1024 || response.Content.Headers.ContentLength.Value > maxSize))
                {
                    throw new InvalidOperationException("The downloaded build has an unexpected file size.");
                }

                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await input.CopyToAsync(output, 81920, timeout.Token).ConfigureAwait(false);
                }

                var info = new FileInfo(destination);
                if (info.Length < 1024 || info.Length > maxSize)
                {
                    throw new InvalidOperationException("The downloaded build has an unexpected file size.");
                }
            }
        }

        private static bool IsAllowedDownloadHost(Uri uri)
        {
            if (uri == null || uri.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }

            string host = uri.Host.ToLowerInvariant();
            return host == "nightly.link" ||
                   host == "sk-data.special-k.info" ||
                   host == "github.com" ||
                   host == "api.github.com" ||
                   host == "githubusercontent.com" ||
                   host.EndsWith(".githubusercontent.com", StringComparison.Ordinal) ||
                   host.EndsWith(".githubassets.com", StringComparison.Ordinal) ||
                   host.EndsWith(".blob.core.windows.net", StringComparison.Ordinal) ||
                   host == "github-releases.githubusercontent.com" ||
                   host == "release-assets.githubusercontent.com";
        }

        private static List<StagedDll> ExtractAndValidateDlls(string zipPath, string workDir, string installDir)
        {
            string stageDir = Path.Combine(workDir, "staged");
            Directory.CreateDirectory(stageDir);
            var staged = new List<StagedDll>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    string normalized = (entry.FullName ?? string.Empty).Replace('\\', '/');
                    if (string.IsNullOrWhiteSpace(normalized) || normalized.EndsWith("/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string fileName = normalized.Split('/').Last();
                    if (!fileName.StartsWith("SpecialK", StringComparison.OrdinalIgnoreCase) ||
                        !fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (normalized.Split('/').Length > 2 ||
                        !Regex.IsMatch(fileName, @"^SpecialK[A-Za-z0-9._-]*\.dll$", RegexOptions.IgnoreCase) ||
                        !names.Add(fileName))
                    {
                        throw new InvalidOperationException("The build archive contains an unexpected Special K DLL path or duplicate.");
                    }

                    string stagedPath = Path.Combine(stageDir, fileName);
                    using (var input = entry.Open())
                    using (var output = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        input.CopyTo(output);
                    }

                    var info = new FileInfo(stagedPath);
                    if (info.Length < 100 * 1024 || info.Length > 200 * 1024 * 1024 || !HasPortableExecutableHeader(stagedPath))
                    {
                        throw new InvalidOperationException("A downloaded Special K DLL failed file validation.");
                    }

                    staged.Add(new StagedDll
                    {
                        FileName = fileName,
                        StagedPath = stagedPath,
                        TargetPath = Path.Combine(installDir, fileName)
                    });
                }
            }

            if (staged.Count == 0)
            {
                throw new InvalidOperationException("The build archive did not contain any SpecialK*.dll files.");
            }

            return staged;
        }

        private static bool HasPortableExecutableHeader(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                return stream.Length >= 2 && stream.ReadByte() == 0x4D && stream.ReadByte() == 0x5A;
            }
        }

        private string InstallWithBackup(List<StagedDll> dlls, string artifactName)
        {
            // Temporary rollback copy only: it is deleted when the update finishes (success or failure).
            string backupDir = Path.Combine(Path.GetTempPath(), "FPSLimiter-SKRollback-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backupDir);

            var replaced = new List<StagedDll>();
            try
            {
                foreach (var dll in dlls)
                {
                    if (File.Exists(dll.TargetPath))
                    {
                        File.Copy(dll.TargetPath, Path.Combine(backupDir, dll.FileName), false);
                    }
                }

                foreach (var dll in dlls)
                {
                    string tempTarget = dll.TargetPath + ".fpslimiter-new-" + Guid.NewGuid().ToString("N");
                    File.Copy(dll.StagedPath, tempTarget, false);
                    try
                    {
                        if (File.Exists(dll.TargetPath))
                        {
                            File.Replace(tempTarget, dll.TargetPath, null);
                        }
                        else
                        {
                            File.Move(tempTarget, dll.TargetPath);
                        }
                        replaced.Add(dll);
                    }
                    finally
                    {
                        if (File.Exists(tempTarget))
                        {
                            File.Delete(tempTarget);
                        }
                    }
                }
            }
            catch
            {
                foreach (var dll in replaced.AsEnumerable().Reverse())
                {
                    string backup = Path.Combine(backupDir, dll.FileName);
                    try
                    {
                        if (File.Exists(backup))
                        {
                            File.Copy(backup, dll.TargetPath, true);
                        }
                        else if (File.Exists(dll.TargetPath))
                        {
                            File.Delete(dll.TargetPath);
                        }
                    }
                    catch (Exception rollbackError)
                    {
                        logger.Error(rollbackError, "Could not roll back a Special K DLL after update failure.");
                    }
                }

                throw;
            }
            finally { TryDeleteDirectory(backupDir); }

            return string.Empty;
        }

        // ---- Per-game local Special K DLLs -------------------------------------------------

        private static readonly HashSet<string> LocalWrapperNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "dxgi.dll", "d3d11.dll", "d3d9.dll", "d3d8.dll", "ddraw.dll", "opengl32.dll",
            "dinput8.dll", "SpecialK64.dll", "SpecialK32.dll"
        };

        /// <summary>
        /// Replaces older locally injected Special K DLLs inside Playnite games' install folders
        /// with the freshly updated global ones. Returns text to append to the update message
        /// (empty when nothing was found).
        /// </summary>
        private string SyncLocalSpecialKDlls(string installDir)
        {
            int dllCount = 0, gameCount = 0, failed = 0;
            try
            {
                string src64 = Path.Combine(installDir, "SpecialK64.dll");
                string src32 = Path.Combine(installDir, "SpecialK32.dll");
                string hash64 = File.Exists(src64) ? ComputeSha256(src64) : null;
                string hash32 = File.Exists(src32) ? ComputeSha256(src32) : null;
                if (hash64 == null && hash32 == null) return string.Empty;

                var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var games = playniteApi?.Database?.Games;
                if (games == null) return string.Empty;
                foreach (var game in games.ToList())
                {
                    string dir = game?.InstallDirectory;
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    try
                    {
                        dir = Path.GetFullPath(dir);
                        if (Directory.Exists(dir)) roots.Add(dir.TrimEnd('\\'));
                    }
                    catch { }
                }

                string globalNorm = Path.GetFullPath(installDir).TrimEnd('\\');
                foreach (string root in roots)
                {
                    int before = dllCount;
                    foreach (string file in EnumerateLocalCandidates(root, 0))
                    {
                        string folder = Path.GetDirectoryName(file) ?? string.Empty;
                        if (string.Equals(folder.TrimEnd('\\'), globalNorm, StringComparison.OrdinalIgnoreCase)) continue;

                        try
                        {
                            int bits = GetPeBitness(file);
                            if (bits == 0 || !IsSpecialKDll(file)) continue;
                            string src = bits == 64 ? src64 : src32;
                            string srcHash = bits == 64 ? hash64 : hash32;
                            if (srcHash == null) continue;
                            if (string.Equals(ComputeSha256(file), srcHash, StringComparison.OrdinalIgnoreCase)) continue;

                            string temp = file + ".fpslimiter-new-" + Guid.NewGuid().ToString("N");
                            File.Copy(src, temp, false);
                            try { File.Replace(temp, file, null); }
                            finally { if (File.Exists(temp)) File.Delete(temp); }
                            dllCount++;
                            logger.Info("Updated local Special K DLL: " + file);
                        }
                        catch (Exception e)
                        {
                            failed++;
                            logger.Warn("Could not update local Special K DLL " + file + ": " + e.Message);
                        }
                    }
                    if (dllCount > before) gameCount++;
                }
            }
            catch (Exception e)
            {
                logger.Warn("Local Special K DLL sync failed: " + e.Message);
                return string.Empty;
            }

            string text = string.Empty;
            if (dllCount > 0)
                text = "\n\nUpdated " + dllCount + " local Special K DLL" + (dllCount == 1 ? "" : "s") +
                       " in " + gameCount + " game" + (gameCount == 1 ? "" : "s") + ".";
            if (failed > 0)
                text += "\n" + failed + " local DLL" + (failed == 1 ? " could" : "s could") +
                        " not be updated (in use or read-only). Close the game and update again.";
            return text;
        }

        private static IEnumerable<string> EnumerateLocalCandidates(string dir, int depth)
        {
            var found = new List<string>();
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    if (LocalWrapperNames.Contains(Path.GetFileName(f))) found.Add(f);
                }
                if (depth < 3)
                {
                    foreach (string sub in Directory.EnumerateDirectories(dir))
                    {
                        try
                        {
                            if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                        }
                        catch { continue; }
                        found.AddRange(EnumerateLocalCandidates(sub, depth + 1));
                    }
                }
            }
            catch { }
            return found;
        }

        /// <summary>Returns 32, 64, or 0 if the file is not a valid PE image.</summary>
        private static int GetPeBitness(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var br = new BinaryReader(fs))
            {
                if (fs.Length < 1024 * 1024 || br.ReadUInt16() != 0x5A4D) return 0;
                fs.Position = 0x3C;
                int pe = br.ReadInt32();
                if (pe <= 0 || pe > fs.Length - 6) return 0;
                fs.Position = pe;
                if (br.ReadUInt32() != 0x00004550) return 0;
                ushort machine = br.ReadUInt16();
                return machine == 0x8664 ? 64 : machine == 0x014C ? 32 : 0;
            }
        }

        private static bool IsSpecialKDll(string path)
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                string meta = (vi.ProductName + "|" + vi.FileDescription + "|" + vi.InternalName + "|" + vi.OriginalFilename)
                    .Replace(" ", string.Empty);
                if (meta.IndexOf("SpecialK", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            catch { }

            // Fallback: SK wrappers embed their own module name as ASCII.
            byte[][] needles =
            {
                System.Text.Encoding.ASCII.GetBytes("SpecialK64.dll"),
                System.Text.Encoding.ASCII.GetBytes("SpecialK32.dll")
            };
            byte[] data = File.ReadAllBytes(path);
            foreach (var n in needles)
            {
                for (int i = 0; i <= data.Length - n.Length; i++)
                {
                    if (data[i] != n[0]) continue;
                    int j = 1;
                    while (j < n.Length && data[i + j] == n[j]) j++;
                    if (j == n.Length) return true;
                }
            }
            return false;
        }

        private static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
            }
        }

        private static HttpClient CreateHttpClient(TimeSpan timeout)
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = true };
            var client = new HttpClient(handler) { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Playnite-FPSLimiter-SpecialK-Updater");
            return client;
        }

        private MessageBoxResult ShowMessage(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
        {
            Func<MessageBoxResult> show = () => MessageBox.Show(message, title, buttons, image);
            return dispatcher.CheckAccess() ? show() : dispatcher.Invoke(show);
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch (Exception e)
            {
                logger.Warn("Could not remove temporary Special K update files: " + e.Message);
            }
        }
    }
}