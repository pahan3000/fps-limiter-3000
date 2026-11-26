using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace FPSLimiter
{
    /// <summary>
    /// Keeps FPS Limiter 3000 itself up to date. Looks at the latest GitHub release of
    /// pahan3000/fps-limiter-3000, downloads its .pext, verifies it, and hands it to Playnite's own
    /// extension installer (Playnite asks for confirmation and a restart). Nothing is ever replaced
    /// behind Playnite's back.
    /// </summary>
    public sealed class ExtensionUpdateService
    {
        private const string Owner = "pahan3000";
        private const string Repo = "fps-limiter-3000";
        private const string ExtensionId = "FPSLimiter_08c6a58f-461c-4629-850c-d07a820e26c1";
        private const string TempFolderName = "FPSLimiter-ExtensionUpdate";

        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly Uri LatestReleaseApi =
            new Uri("https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest");
        private static readonly SemaphoreSlim checkLock = new SemaphoreSlim(1, 1);

        private readonly FPSLimiterSettingsViewModel settingsViewModel;
        private readonly LimiterService limiterService;
        private readonly IPlayniteAPI playniteApi;
        private readonly Dispatcher dispatcher;

        // ---- GitHub API payload ----------------------------------------------------------
        private sealed class GitHubRelease
        {
            [SerializationPropertyName("tag_name")] public string TagName { get; set; }
            [SerializationPropertyName("name")] public string Name { get; set; }
            [SerializationPropertyName("html_url")] public string HtmlUrl { get; set; }
            [SerializationPropertyName("body")] public string Body { get; set; }
            [SerializationPropertyName("draft")] public bool Draft { get; set; }
            [SerializationPropertyName("prerelease")] public bool Prerelease { get; set; }
            [SerializationPropertyName("assets")] public List<GitHubAsset> Assets { get; set; }
        }

        private sealed class GitHubAsset
        {
            [SerializationPropertyName("name")] public string Name { get; set; }
            [SerializationPropertyName("size")] public long Size { get; set; }
            [SerializationPropertyName("digest")] public string Digest { get; set; }
            [SerializationPropertyName("browser_download_url")] public string DownloadUrl { get; set; }
        }

        private sealed class ReleaseInfo
        {
            public Version Version { get; set; }
            public string Tag { get; set; }
            public string PageUrl { get; set; }
            public string Notes { get; set; }
            public Uri PextUri { get; set; }
            public string PextName { get; set; }
            public long Size { get; set; }
            public string Sha256 { get; set; }
        }

        public ExtensionUpdateService(
            FPSLimiterSettingsViewModel settingsViewModel,
            LimiterService limiterService,
            IPlayniteAPI api)
        {
            this.settingsViewModel = settingsViewModel;
            this.limiterService = limiterService;
            playniteApi = api;
            dispatcher = Application.Current != null
                ? Application.Current.Dispatcher
                : Dispatcher.CurrentDispatcher;
            // Leftover downloads from an earlier session are no longer needed.
            TryDeleteDirectory(Path.Combine(Path.GetTempPath(), TempFolderName));
        }

        // ---- Version helpers ---------------------------------------------------------------

        /// <summary>
        /// Installed version: the higher of extension.yaml's Version (what Playnite shows) and the
        /// DLL's assembly version, so a forgotten bump in one of them never causes a bogus update.
        /// </summary>
        public static Version GetInstalledVersion()
        {
            Version best = null;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                best = Normalize(asm.GetName().Version);

                string dir = Path.GetDirectoryName(asm.Location);
                string yaml = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "extension.yaml");
                if (yaml != null && File.Exists(yaml))
                {
                    var m = Regex.Match(File.ReadAllText(yaml), @"^\s*Version\s*:\s*(.+?)\s*$", RegexOptions.Multiline);
                    Version fromYaml;
                    if (m.Success && TryParseVersion(m.Groups[1].Value, out fromYaml) &&
                        (best == null || fromYaml > best))
                    {
                        best = fromYaml;
                    }
                }
            }
            catch (Exception e)
            {
                logger.Warn("Could not determine the installed extension version: " + e.Message);
            }

            return best ?? new Version(0, 0, 0, 0);
        }

        public static string FormatVersion(Version v)
        {
            if (v == null) return string.Empty;
            // 1.0.3.0 -> 1.0.3, 1.0.3.1 stays as is.
            return v.Revision > 0 ? v.ToString(4) : v.ToString(3);
        }

        public static bool TryParseVersion(string text, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var m = Regex.Match(text.Trim(), @"^[vV]?(\d+(?:\.\d+){0,3})");
            Version parsed;
            if (!m.Success || !Version.TryParse(m.Groups[1].Value.Contains(".") ? m.Groups[1].Value : m.Groups[1].Value + ".0", out parsed))
            {
                return false;
            }

            version = Normalize(parsed);
            return true;
        }

        private static Version Normalize(Version v)
        {
            if (v == null) return null;
            return new Version(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
        }

        /// <summary>True when <paramref name="candidate"/> (a tag or version string) is newer than the installed build.</summary>
        public static bool IsNewerThanInstalled(string candidate)
        {
            Version v;
            return TryParseVersion(candidate, out v) && v > GetInstalledVersion();
        }

        // ---- Scheduling --------------------------------------------------------------------

        /// <summary>Runs a check at startup when the chosen schedule says one is due.</summary>
        public async Task CheckIfDueAsync()
        {
            var settings = settingsViewModel.Settings;
            if (settings == null || settings.ExtensionUpdateFrequency == ExtensionUpdateInterval.Never)
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            DateTime last = settings.ExtensionUpdateLastCheckUtc;
            if (last != DateTime.MinValue && last.Kind != DateTimeKind.Utc)
            {
                last = DateTime.SpecifyKind(last, DateTimeKind.Utc);
            }

            bool due = last == DateTime.MinValue;
            if (!due)
            {
                switch (settings.ExtensionUpdateFrequency)
                {
                    case ExtensionUpdateInterval.OnLaunch:
                        due = true;
                        break;
                    case ExtensionUpdateInterval.Weekly:
                        due = now >= last.AddDays(7);
                        break;
                    case ExtensionUpdateInterval.Monthly:
                        due = now >= last.AddMonths(1);
                        break;
                }
            }

            if (due)
            {
                await CheckAsync(false, false).ConfigureAwait(false);
            }
        }

        /// <summary>Manual check from the settings page; always runs and always reports the result.</summary>
        public Task CheckNowAsync() { return CheckAsync(true, false); }

        /// <summary>Manual check followed by download + hand-off to Playnite's installer.</summary>
        public Task UpdateNowAsync() { return CheckAsync(true, true); }

        // ---- Core --------------------------------------------------------------------------

        private async Task CheckAsync(bool manual, bool install)
        {
            if (!await checkLock.WaitAsync(0).ConfigureAwait(false))
            {
                if (!manual) return;
                await checkLock.WaitAsync().ConfigureAwait(false);
            }

            string workDir = null;
            try
            {
                var settings = settingsViewModel.Settings;
                ReleaseInfo latest = await FetchLatestReleaseAsync().ConfigureAwait(false);
                settings.ExtensionUpdateLastCheckUtc = DateTime.UtcNow;

                Version installed = GetInstalledVersion();
                if (latest.Version <= installed)
                {
                    settings.ExtensionUpdateAvailableVersion = string.Empty;
                    settingsViewModel.SaveSettings();
                    if (manual)
                    {
                        ShowMessage("FPS Limiter 3000 is up to date (" + FormatVersion(installed) + ").",
                            "FPS Limiter 3000 updates", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return;
                }

                string tag = FormatVersion(latest.Version);
                bool alreadyNotified = string.Equals(tag, settings.ExtensionUpdateLastNotifiedVersion,
                    StringComparison.OrdinalIgnoreCase);
                settings.ExtensionUpdateAvailableVersion = tag;
                settingsViewModel.SaveSettings();

                if (!install)
                {
                    if (settings.ExtensionNotifyUpdateAvailable && (manual || !alreadyNotified))
                    {
                        settings.ExtensionUpdateLastNotifiedVersion = tag;
                        settingsViewModel.SaveSettings();
                        NotifyUpdateAvailable(tag);
                    }

                    if (manual)
                    {
                        ShowMessage("FPS Limiter 3000 " + tag + " is available (you have " + FormatVersion(installed) +
                            ").\n\nUse \"Update FPS Limiter 3000\" to download and install it.",
                            "FPS Limiter 3000 updates", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return;
                }

                if (limiterService.HasRunningGamesPublic())
                {
                    ShowMessage("A game launched through Playnite is still running. Close it first - installing an " +
                        "extension update requires restarting Playnite.",
                        "FPS Limiter 3000 updates", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                workDir = Path.Combine(Path.GetTempPath(), TempFolderName);
                Directory.CreateDirectory(workDir);
                string pextPath = Path.Combine(workDir, latest.PextName);
                if (File.Exists(pextPath)) File.Delete(pextPath);

                await DownloadAsync(latest.PextUri, pextPath).ConfigureAwait(false);
                ValidatePext(pextPath, latest);

                LaunchPlayniteInstaller(pextPath);
                // The downloaded file must stay until Playnite has read it.
                workDir = null;
                logger.Info("Handed FPS Limiter 3000 " + tag + " to Playnite's extension installer.");
            }
            catch (Exception e)
            {
                logger.Error(e, "Extension update check/install failed.");
                if (manual)
                {
                    ShowMessage("Could not check or install the FPS Limiter 3000 update.\n\n" + e.Message,
                        "FPS Limiter 3000 updates", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(workDir)) TryDeleteDirectory(workDir);
                checkLock.Release();
            }
        }

        private async Task<ReleaseInfo> FetchLatestReleaseAsync()
        {
            string json;
            using (var client = CreateHttpClient(TimeSpan.FromSeconds(15)))
            using (var response = await client.GetAsync(LatestReleaseApi).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new InvalidOperationException("No published release was found on GitHub yet.");
                }

                if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
                {
                    throw new InvalidOperationException("GitHub refused the request (rate limit). Try again later.");
                }

                response.EnsureSuccessStatusCode();
                json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }

            var release = Serialization.FromJson<GitHubRelease>(json);
            if (release == null || release.Draft || release.Prerelease)
            {
                throw new InvalidOperationException("GitHub did not return a usable release.");
            }

            Version version;
            if (!TryParseVersion(release.TagName, out version))
            {
                throw new InvalidOperationException("The latest release tag \"" + release.TagName + "\" is not a version number.");
            }

            var asset = (release.Assets ?? new List<GitHubAsset>())
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.Name) &&
                            a.Name.EndsWith(".pext", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.Name.IndexOf("FPS", StringComparison.OrdinalIgnoreCase) >= 0)
                .FirstOrDefault();
            if (asset == null)
            {
                throw new InvalidOperationException("Release " + release.TagName + " has no .pext file attached.");
            }

            Uri uri;
            if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out uri) || !IsAllowedDownloadHost(uri) ||
                !uri.AbsolutePath.StartsWith("/" + Owner + "/" + Repo + "/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The release download link points somewhere unexpected.");
            }

            string safeName = Regex.Replace(Path.GetFileName(asset.Name), @"[^A-Za-z0-9._ -]", "_");
            string sha = null;
            if (!string.IsNullOrWhiteSpace(asset.Digest) &&
                asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                sha = asset.Digest.Substring(7).Trim();
            }

            return new ReleaseInfo
            {
                Version = version,
                Tag = release.TagName,
                PageUrl = release.HtmlUrl,
                Notes = release.Body,
                PextUri = uri,
                PextName = safeName,
                Size = asset.Size,
                Sha256 = sha
            };
        }

        private static async Task DownloadAsync(Uri uri, string destination)
        {
            const long maxSize = 200L * 1024L * 1024L;
            using (var client = CreateHttpClient(TimeSpan.FromMinutes(5)))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
            using (var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                Uri finalUri = response.RequestMessage != null ? response.RequestMessage.RequestUri : uri;
                if (!IsAllowedDownloadHost(finalUri))
                {
                    throw new InvalidOperationException("The download redirected to an unexpected host.");
                }

                long? length = response.Content.Headers.ContentLength;
                if (length.HasValue && (length.Value < 1024 || length.Value > maxSize))
                {
                    throw new InvalidOperationException("The downloaded update has an unexpected file size.");
                }

                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await input.CopyToAsync(output, 81920, timeout.Token).ConfigureAwait(false);
                }
            }

            var info = new FileInfo(destination);
            if (info.Length < 1024 || info.Length > maxSize)
            {
                throw new InvalidOperationException("The downloaded update has an unexpected file size.");
            }
        }

        /// <summary>Checks hash (when GitHub provides one), zip structure, extension id and version.</summary>
        private static void ValidatePext(string path, ReleaseInfo release)
        {
            if (!string.IsNullOrWhiteSpace(release.Sha256))
            {
                string actual;
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(path))
                {
                    actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
                }

                if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The downloaded file failed its SHA-256 check. Nothing was installed.");
                }
            }

            string yaml = null;
            bool hasDll = false;
            using (var archive = ZipFile.OpenRead(path))
            {
                foreach (var entry in archive.Entries)
                {
                    string name = (entry.FullName ?? string.Empty).Replace('\\', '/');
                    if (name.IndexOf("..", StringComparison.Ordinal) >= 0 || name.StartsWith("/", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("The update package contains an unsafe file path.");
                    }

                    if (string.Equals(name, "extension.yaml", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var reader = new StreamReader(entry.Open()))
                        {
                            yaml = reader.ReadToEnd();
                        }
                    }
                    else if (string.Equals(name, "FPSLimiter.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        hasDll = true;
                    }
                }
            }

            if (yaml == null || !hasDll)
            {
                throw new InvalidOperationException("The downloaded file is not a valid FPS Limiter 3000 package.");
            }

            var id = Regex.Match(yaml, @"^\s*Id\s*:\s*(\S+)", RegexOptions.Multiline);
            if (!id.Success || !string.Equals(id.Groups[1].Value, ExtensionId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The downloaded package is for a different extension. Nothing was installed.");
            }

            var ver = Regex.Match(yaml, @"^\s*Version\s*:\s*(.+?)\s*$", RegexOptions.Multiline);
            Version packaged;
            if (!ver.Success || !TryParseVersion(ver.Groups[1].Value, out packaged) || packaged < GetInstalledVersion())
            {
                throw new InvalidOperationException("The downloaded package is not newer than the installed version. Nothing was installed.");
            }
        }

        private void LaunchPlayniteInstaller(string pextPath)
        {
            // Preferred: ask the running Playnite to install the package (it shows its own confirmation).
            try
            {
                if (playniteApi.ApplicationInfo.Mode == ApplicationMode.Desktop)
                {
                    string exe = Path.Combine(playniteApi.Paths.ApplicationPath, "Playnite.DesktopApp.exe");
                    if (File.Exists(exe))
                    {
                        Process.Start(new ProcessStartInfo(exe, "\"" + pextPath + "\"") { UseShellExecute = false });
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                logger.Warn("Could not start Playnite directly for the update: " + e.Message);
            }

            // Fallback: the .pext file association that Playnite registers.
            try
            {
                Process.Start(new ProcessStartInfo(pextPath) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                logger.Warn("Could not open the .pext file: " + e.Message);
                try { Process.Start("explorer.exe", "/select,\"" + pextPath + "\""); } catch { }
                throw new InvalidOperationException(
                    "The update was downloaded to:\n" + pextPath + "\n\nDrag it onto Playnite to install it.");
            }
        }

        // ---- Notification ------------------------------------------------------------------

        private void NotifyUpdateAvailable(string tag)
        {
            var notifications = playniteApi != null ? playniteApi.Notifications : null;
            if (notifications == null) return;

            Action add = () => notifications.Add(new NotificationMessage(
                "fpslimiter-extension-update-available",
                "FPS Limiter 3000 " + tag + " is available. Click to install it.",
                NotificationType.Info,
                () => { var ignored = PromptAndInstallAsync(); }));

            if (dispatcher.CheckAccess()) add(); else dispatcher.BeginInvoke(add);
        }

        private async Task PromptAndInstallAsync()
        {
            try
            {
                var answer = ShowMessage(
                    "Install the FPS Limiter 3000 update now?\n\nPlaynite will ask you to confirm and then needs to restart.",
                    "FPS Limiter 3000 updates", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Yes)
                {
                    await UpdateNowAsync().ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Extension update from notification failed.");
            }
        }

        // ---- Plumbing ----------------------------------------------------------------------

        private static bool IsAllowedDownloadHost(Uri uri)
        {
            if (uri == null || uri.Scheme != Uri.UriSchemeHttps) return false;
            string host = uri.Host.ToLowerInvariant();
            return host == "github.com" ||
                   host == "api.github.com" ||
                   host == "githubusercontent.com" ||
                   host.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
        }

        private static HttpClient CreateHttpClient(TimeSpan timeout)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Playnite-FPSLimiter3000-Updater");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
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
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch (Exception e)
            {
                logger.Warn("Could not remove temporary update files: " + e.Message);
            }
        }
    }
}
