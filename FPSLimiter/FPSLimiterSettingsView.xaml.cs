using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System;
using System.Linq;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using global::FPSLimiter;

namespace FPSLimiterSettingsUI
{
    public partial class FPSLimiterSettingsView : UserControl
    {
        public FPSLimiterSettingsView()
        {
            InitializeComponent();
            Loaded += (_, __) =>
            {
                HookAppearance();
                ApplyBrandIcons();
                RefreshDownloadInstallStatus();
            };
            Unloaded += (_, __) =>
            {
                HotkeyManager.CaptureActive = false;
                UnhookAppearance();
            };
            DataContextChanged += (_, __) => HookAppearance();
            // Pause the global hotkeys while a key-capture box has focus so already-assigned combos can be captured.
            AddHandler(GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((s, e) =>
            {
                if (IsCaptureBox(e.OriginalSource)) HotkeyManager.CaptureActive = true;
            }), true);
            AddHandler(LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((s, e) =>
            {
                if (IsCaptureBox(e.OriginalSource)) HotkeyManager.CaptureActive = false;
            }), true);
        }

        private bool IsCaptureBox(object source)
        {
            return source is TextBox box && box.IsReadOnly && ReferenceEquals(box.Style, TryFindResource("HotkeyCaptureTextBox"));
        }

        private void MainSection_Expanded(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || !(sender is Expander current))
            {
                return;
            }

            foreach (var section in new[]
            {
                GeneralSection,
                DownloadsSection,
                UpdatesSection,
                NotificationsSection,
                RtssSection,
                SpecialKSection,
                GlobalFpsLimitsSection,
                HotkeysSection,
                InstalledSection
            })
            {
                if (section != null && !ReferenceEquals(section, current))
                {
                    section.IsExpanded = false;
                }
            }
            ScrollSectionIntoFocus(current);
        }

        /// <summary>After the other sections collapse, scrolls so the opened section's header sits at the top.</summary>
        private void ScrollSectionIntoFocus(FrameworkElement section)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    DependencyObject node = section;
                    ScrollViewer viewer = null;
                    while (node != null && viewer == null)
                    {
                        node = System.Windows.Media.VisualTreeHelper.GetParent(node);
                        viewer = node as ScrollViewer;
                    }
                    var content = viewer?.Content as UIElement;
                    if (viewer == null || content == null || viewer.ScrollableHeight <= 0)
                    {
                        section.BringIntoView();
                        return;
                    }
                    viewer.UpdateLayout();
                    var offset = section.TranslatePoint(new Point(0, 0), content).Y;
                    viewer.ScrollToVerticalOffset(Math.Max(0, offset - 6));
                }
                catch { /* purely cosmetic */ }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void SpecialKSubsection_Expanded(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || !(sender is Expander current))
            {
                return;
            }

            foreach (var section in new[]
            {
                SpecialKGeneralSection,
                SpecialKLatencySection,
                SpecialKCompatibilitySection
            })
            {
                if (section != null && !ReferenceEquals(section, current))
                {
                    section.IsExpanded = false;
                }
            }
            ScrollSectionIntoFocus(current);
        }

        private FPSLimiterSettingsViewModel appearanceVm;
        private FPSLimiterSettings appearanceSettings;
        private void UnhookAppearance()
        {
            if (appearanceVm != null) appearanceVm.PropertyChanged -= AppearanceVmChanged;
            if (appearanceSettings != null) appearanceSettings.PropertyChanged -= AppearanceSettingsChanged;
            appearanceVm = null; appearanceSettings = null;
        }
        private void HookAppearance()
        {
            UnhookAppearance();
            appearanceVm = DataContext as FPSLimiterSettingsViewModel;
            if (appearanceVm != null)
            {
                appearanceVm.PropertyChanged += AppearanceVmChanged;
                appearanceSettings = appearanceVm.Settings;
                if (appearanceSettings != null) appearanceSettings.PropertyChanged += AppearanceSettingsChanged;
            }
            AppearancePalette.Apply(this, appearanceSettings?.ThemePreset, appearanceSettings?.GlassEffect == true);
            RefreshDownloadInstallStatus();
        }
        private void AppearanceVmChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(FPSLimiterSettingsViewModel.Settings)) HookAppearance();
        }
        private void AppearanceSettingsChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName == nameof(FPSLimiterSettings.ThemePreset) ||
                e.PropertyName == nameof(FPSLimiterSettings.GlassEffect))
            {
                AppearancePalette.Apply(this, appearanceSettings?.ThemePreset, appearanceSettings?.GlassEffect == true);
            }
            if (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName == nameof(FPSLimiterSettings.RtssPath) ||
                e.PropertyName == nameof(FPSLimiterSettings.SpecialKPath))
            {
                RefreshDownloadInstallStatus();
            }
        }

        private void RefreshDownloadInstallStatus()
        {
            bool rtssInstalled = appearanceSettings != null &&
                !string.IsNullOrWhiteSpace(appearanceSettings.RtssPath) &&
                File.Exists(appearanceSettings.RtssPath);
            bool specialKInstalled = appearanceSettings != null &&
                !string.IsNullOrWhiteSpace(appearanceSettings.SpecialKPath) &&
                Directory.Exists(appearanceSettings.SpecialKPath);

            if (RtssInstalledBadge != null) RtssInstalledBadge.Visibility = rtssInstalled ? Visibility.Visible : Visibility.Collapsed;
            if (RtssNotInstalledText != null) RtssNotInstalledText.Visibility = rtssInstalled ? Visibility.Collapsed : Visibility.Visible;
            if (DownloadRtssButton != null) DownloadRtssButton.Tag = rtssInstalled;
            if (SpecialKInstalledBadge != null) SpecialKInstalledBadge.Visibility = specialKInstalled ? Visibility.Visible : Visibility.Collapsed;
            if (SpecialKNotInstalledText != null) SpecialKNotInstalledText.Visibility = specialKInstalled ? Visibility.Collapsed : Visibility.Visible;
            if (DownloadSpecialKButton != null) DownloadSpecialKButton.Tag = specialKInstalled;
            var ovl = RtssOverlayTargetPath();
            bool overlayInstalled = ovl != null && File.Exists(ovl);
            if (RtssOverlayBadge != null)
                RtssOverlayBadge.Visibility = overlayInstalled ? Visibility.Visible : Visibility.Collapsed;
            if (InstallRtssOverlayButton != null)
                InstallRtssOverlayButton.Content = overlayInstalled ? "Reinstall overlay preset" : "Install overlay preset";
            RefreshInstalledComponents();
        }

        private void DownloadsTab_Checked(object sender, RoutedEventArgs e)
        {
            if (RtssDownloadCard == null || SpecialKDownloadCard == null || DownloadsTabRtss == null) return;
            bool rtss = DownloadsTabRtss.IsChecked == true;
            RtssDownloadCard.Visibility = rtss ? Visibility.Visible : Visibility.Collapsed;
            SpecialKDownloadCard.Visibility = rtss ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OpenRtssHome_Click(object sender, RoutedEventArgs e)
        {
            OpenWebPage("https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/", "RTSS download page");
        }

        private void OpenSpecialKHome_Click(object sender, RoutedEventArgs e)
        {
            OpenWebPage(SpecialKHome, "Special K website");
        }

        private static void OpenWebPage(string url, string label)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open the " + label + ":\n" + ex.Message,
                    "FPS Limiter 3000", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ---------------- Installed components ----------------

        private void InstalledSection_Expanded(object sender, RoutedEventArgs e)
        {
            RefreshInstalledComponents();
            MainSection_Expanded(sender, e);
        }

        private string RtssOverlayTargetPath()
        {
            var exe = appearanceSettings?.RtssPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return null;
            return Path.Combine(Path.GetDirectoryName(exe) ?? string.Empty, "Plugins", "Client", "Overlays", RtssOverlayFileName);
        }

        /// <summary>Rebuilds the "Installed components" list: only things that are actually installed are shown.</summary>
        private void RefreshInstalledComponents()
        {
            if (InstalledComponentsPanel == null) return;
            InstalledComponentsPanel.Children.Clear();

            bool rtss = appearanceSettings != null && !string.IsNullOrWhiteSpace(appearanceSettings.RtssPath) && File.Exists(appearanceSettings.RtssPath);
            bool sk = appearanceSettings != null && !string.IsNullOrWhiteSpace(appearanceSettings.SpecialKPath) && Directory.Exists(appearanceSettings.SpecialKPath);
            var overlayPath = RtssOverlayTargetPath();
            bool overlay = overlayPath != null && File.Exists(overlayPath);

            if (rtss) AddInstalledRow("RivaTuner Statistics Server (RTSS)", RemoveRtss_Click);
            if (overlay) AddInstalledRow("RTSS overlay preset (" + RtssOverlayFileName + ")", RemoveRtssOverlay_Click);
            if (sk) AddInstalledRow("Special K + SKIF", RemoveSpecialK_Click);

            if (InstalledComponentsPanel.Children.Count == 0)
            {
                var empty = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 5) };
                empty.SetResourceReference(Border.BackgroundProperty, "UiCard");
                empty.Child = new TextBlock { Text = "Nothing installed yet. Use the Downloads section to install RTSS or Special K.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
                InstalledComponentsPanel.Children.Add(empty);
            }
        }

        private void AddInstalledRow(string name, RoutedEventHandler onRemove)
        {
            var card = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 5) };
            card.SetResourceReference(Border.BackgroundProperty, "UiCard");
            var grid = new Grid { MinHeight = 34 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var btn = new Button { Content = "Remove", MinHeight = 36, MinWidth = 80, VerticalAlignment = VerticalAlignment.Center };
            var style = TryFindResource("CardSecondaryButton") as Style;
            if (style != null) btn.Style = style;
            btn.Click += onRemove;
            Grid.SetColumn(btn, 1);
            grid.Children.Add(btn);
            card.Child = grid;
            InstalledComponentsPanel.Children.Add(card);
        }

        private void RemoveRtssOverlay_Click(object sender, RoutedEventArgs e)
        {
            var target = RtssOverlayTargetPath();
            if (target == null || !File.Exists(target)) { RefreshInstalledComponents(); return; }
            if (MessageBox.Show("Remove the overlay preset " + RtssOverlayFileName + " from RTSS?\n\n" + target + "\n\nIf a backup (.bak) of a previous file exists, it will be restored.",
                    "Installed components", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try
            {
                try { DeleteOrRestoreOverlay(target); }
                catch (UnauthorizedAccessException)
                {
                    var args = "/c del /F /Q \"" + target + "\" & if exist \"" + target + ".bak\" move /Y \"" + target + ".bak\" \"" + target + "\"";
                    using (var p = Process.Start(new ProcessStartInfo("cmd.exe", args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }))
                        p?.WaitForExit();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not remove the overlay preset:\n" + ex.Message, "Installed components", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            RefreshDownloadInstallStatus();
        }

        private static void DeleteOrRestoreOverlay(string target)
        {
            File.Delete(target);
            var bak = target + ".bak";
            if (File.Exists(bak)) File.Move(bak, target);
        }

        private static void StopRtssProcesses()
        {
            foreach (var name in new[] { "RTSS", "RTSSHooksLoader64", "RTSSHooksLoader", "EncoderServer", "EncoderServer64" })
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        try { p.Kill(); p.WaitForExit(3000); } catch { }
                        finally { p.Dispose(); }
                    }
                }
                catch { }
            }
        }

        private static void RemoveLeftoverOverlayPreset(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return;
            string overlaysDir = Path.GetDirectoryName(target);
            try
            {
                if (File.Exists(target))
                {
                    try { File.Delete(target); }
                    catch (UnauthorizedAccessException)
                    {
                        var args = "/c del /F /Q \"" + target + "\"";
                        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }))
                            p?.WaitForExit();
                    }
                }
                // Tidy up empty leftover folders (Overlays, Client, Plugins, RTSS root) without touching anything else.
                var dir = overlaysDir;
                for (int i = 0; i < 4 && !string.IsNullOrEmpty(dir) && Directory.Exists(dir); i++)
                {
                    if (Directory.EnumerateFileSystemEntries(dir).Any()) break;
                    try { Directory.Delete(dir); } catch { break; }
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch { }
        }

        private async void RemoveRtss_Click(object sender, RoutedEventArgs e)
        {
            var exe = appearanceSettings?.RtssPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) { RefreshInstalledComponents(); return; }
            string dir = Path.GetDirectoryName(exe) ?? string.Empty;
            string uninstaller = new[] { "Uninstall.exe", "unins000.exe" }
                .Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists);
            if (uninstaller == null)
            {
                MessageBox.Show("No RTSS uninstaller was found in:\n" + dir + "\n\nRemove RTSS from Windows Settings > Apps instead.",
                    "Installed components", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show("Uninstall RTSS?\n\nNote: MSI Afterburner uses RTSS too. Windows may ask for administrator permission.",
                    "Installed components", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                SetActionButtonsEnabled(false);
                string overlayLeftover = RtssOverlayTargetPath();
                // RTSS keeps its files locked while running, so close it first.
                StopRtssProcesses();
                await LaunchInstallerAndWaitAsync(uninstaller, null);

                // The NSIS uninstaller copies itself to %TEMP% (Au_.exe) and relaunches, so the process we
                // started exits immediately while the real removal is still running. Wait for it, and for
                // RTSS.exe to actually disappear, before refreshing the status.
                for (int i = 0; i < 120; i++)
                {
                    bool uninstallerRunning = Process.GetProcessesByName("Au_").Length > 0 ||
                        Process.GetProcessesByName("Uninstall").Length > 0 ||
                        Process.GetProcessesByName("unins000").Length > 0;
                    if (!uninstallerRunning && !File.Exists(exe)) break;
                    if (!uninstallerRunning && i >= 6) break; // uninstaller finished but file remains
                    await System.Threading.Tasks.Task.Delay(500);
                }

                if (!File.Exists(exe))
                {
                    // Forget the stale path so the badge, the installed list and auto-detect agree.
                    appearanceSettings.RtssPath = string.Empty;

                    // The RTSS uninstaller does not delete files it didn't install, so our overlay preset
                    // would be left behind and show up as "installed" after a fresh RTSS install.
                    RemoveLeftoverOverlayPreset(overlayLeftover);
                }
                else
                {
                    MessageBox.Show("The uninstaller finished but RTSS.exe is still present:\n" + exe +
                        "\n\nThe uninstall may have been cancelled, or the file is locked. Close RTSS/MSI Afterburner and try again, or remove RTSS from Windows Settings > Apps.",
                        "Installed components", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not start the RTSS uninstaller:\n" + ex.Message, "Installed components", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetActionButtonsEnabled(true);
                RefreshDownloadInstallStatus();
            }
        }

        private void RemoveSpecialK_Click(object sender, RoutedEventArgs e)
        {
            var dir = appearanceSettings?.SpecialKPath;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) { RefreshInstalledComponents(); return; }
            if (MessageBox.Show("Delete the Special K folder?\n\n" + dir + "\n\nThis removes Special K, SKIF and every config/profile stored in that folder. Close SKIF and any running games first.",
                    "Installed components", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                Directory.Delete(dir, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not fully remove Special K (is SKIF or a game still running?):\n" + ex.Message,
                    "Installed components", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            RefreshDownloadInstallStatus();
        }

        private static string SetIniKey(string text, string section, string key, string value)
        {
            var lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
            if (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            int secIdx = lines.FindIndex(l => l.Trim().Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase));
            if (secIdx < 0) { lines.Add("[" + section + "]"); lines.Add(key + "=" + value); return string.Join("\r\n", lines) + "\r\n"; }
            int end = lines.Count;
            for (int i = secIdx + 1; i < lines.Count; i++) if (lines[i].TrimStart().StartsWith("[")) { end = i; break; }
            for (int i = secIdx + 1; i < end; i++)
            {
                int eq = lines[i].IndexOf('=');
                if (eq > 0 && lines[i].Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) { lines[i] = key + "=" + value; return string.Join("\r\n", lines) + "\r\n"; }
            }
            lines.Insert(end, key + "=" + value);
            return string.Join("\r\n", lines) + "\r\n";
        }

        /// <summary>
        /// Writes several RTSS config files. Files that need administrator rights are written with ONE elevated
        /// copy (a single UAC prompt), then every file is read back so a cancelled prompt is reported, not ignored.
        /// </summary>
        private static void WriteRtssFiles(IList<KeyValuePair<string, string>> files, bool killRtssElevated = false)
        {
            var needElevation = new List<KeyValuePair<string, string>>();
            foreach (var f in files)
            {
                // If an elevated RTSS is still running it must be killed before ANY write: RTSS rewrites its
                // Config from memory when it exits, which would wipe the plugin ticks we just wrote.
                if (killRtssElevated) { needElevation.Add(f); continue; }
                try { File.WriteAllText(f.Key, f.Value, System.Text.Encoding.Default); }
                catch (UnauthorizedAccessException) { needElevation.Add(f); }
            }

            if (needElevation.Count > 0)
            {
                var cmd = new System.Text.StringBuilder("/c ");
                if (killRtssElevated)
                    cmd.Append("taskkill /F /IM RTSS.exe /IM RTSSHooksLoader64.exe /IM RTSSHooksLoader.exe /IM EncoderServer.exe /IM EncoderServer64.exe & ping -n 3 127.0.0.1 >nul & ");
                for (int i = 0; i < needElevation.Count; i++)
                {
                    string tmp = Path.Combine(Path.GetTempPath(), "FPSLimiter-" + i + "-" + Path.GetFileName(needElevation[i].Key));
                    File.WriteAllText(tmp, needElevation[i].Value, System.Text.Encoding.Default);
                    if (i > 0) cmd.Append(" & ");
                    cmd.Append("copy /Y \"" + tmp + "\" \"" + needElevation[i].Key + "\"");
                }
                try
                {
                    using (var p = Process.Start(new ProcessStartInfo("cmd.exe", cmd.ToString()) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }))
                        p?.WaitForExit();
                }
                catch (System.ComponentModel.Win32Exception) { /* UAC cancelled: caught by the read-back below */ }
            }

            var failed = new List<string>();
            foreach (var f in files)
            {
                string actual = null;
                try { actual = File.ReadAllText(f.Key, System.Text.Encoding.Default); } catch { }
                if (actual == null || actual.Replace("\r\n", "\n") != f.Value.Replace("\r\n", "\n")) failed.Add(f.Key);
            }
            if (failed.Count > 0)
                throw new InvalidOperationException("These RTSS files could not be written (administrator permission cancelled?):\n" + string.Join("\n", failed));
        }

        /// <summary>Makes OverlayEditor load MyOverlay.ovl, ticks both RTSS plugins and binds the overlay toggle hotkey. RTSS is restarted if it was running, because it rewrites these files on exit.</summary>
        private static void ConfigureRtssPluginSettings(string rtssExe, string overlayHotkey)
        {
            string rtssDir = Path.GetDirectoryName(rtssExe) ?? string.Empty;
            string clientDir = Path.Combine(rtssDir, "Plugins", "Client");
            bool wasRunning = Process.GetProcessesByName("RTSS").Length > 0;
            if (wasRunning) StopRtssProcesses();
            // An elevated RTSS cannot be killed from Playnite (Kill fails silently); detect that and kill it elevated.
            bool stillRunning = Process.GetProcessesByName("RTSS").Length > 0;

            string editorCfg = Path.Combine(clientDir, "OverlayEditor.cfg");
            string hotkeyCfg = Path.Combine(clientDir, "HotkeyHandler.cfg");
            string mainCfg = Path.Combine(rtssDir, "Profiles", "Config"); // holds [Plugins] (the Setup > Plugins tick boxes)
            string ed = File.Exists(editorCfg) ? File.ReadAllText(editorCfg, System.Text.Encoding.Default) : string.Empty;
            string hk = File.Exists(hotkeyCfg) ? File.ReadAllText(hotkeyCfg, System.Text.Encoding.Default) : string.Empty;
            // A freshly installed RTSS has no Config yet; create it so the plugin ticks are not silently skipped.
            string mc = File.Exists(mainCfg) ? File.ReadAllText(mainCfg, System.Text.Encoding.Default) : string.Empty;
            mc = SetIniKey(mc, "Plugins", "HotkeyHandler.dll", "1");
            mc = SetIniKey(mc, "Plugins", "OverlayEditor.dll", "1");

            try
            {
                if (!Directory.Exists(clientDir)) Directory.CreateDirectory(clientDir);
            }
            catch (UnauthorizedAccessException) { }

            WriteRtssFiles(new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(editorCfg, SetIniKey(ed, "Settings", "Layout", RtssOverlayFileName)),
                new KeyValuePair<string, string>(hotkeyCfg, SetIniKey(hk, "Settings", "OSDToggleHotkey", RtssHotkeyCodec.ToRtssValue(overlayHotkey))),
                new KeyValuePair<string, string>(mainCfg, mc)
            }, stillRunning);

            if (wasRunning)
            {
                try { Process.Start(new ProcessStartInfo(rtssExe) { UseShellExecute = true, WorkingDirectory = rtssDir }); } catch { }
            }
        }

        private const string RtssOverlayFileName = "MyOverlay.ovl";

        /// <summary>Copies the overlay preset shipped with this extension into RTSS's overlay folder.</summary>
        private void InstallRtssOverlay_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as global::FPSLimiter.FPSLimiterSettingsViewModel;
            var rtssExe = vm?.Settings?.RtssPath;
            if (string.IsNullOrWhiteSpace(rtssExe) || !File.Exists(rtssExe))
            {
                MessageBox.Show("RTSS is not installed or its RTSS.exe path is not set. Install RTSS (or select RTSS.exe) first.",
                    "RTSS overlay preset", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Prefer a loose copy next to the extension; otherwise use the copy compiled into the DLL.
            string extDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            string source = Path.Combine(extDir, "Overlays", RtssOverlayFileName);
            if (!File.Exists(source)) source = Path.Combine(extDir, RtssOverlayFileName);
            if (!File.Exists(source))
            {
                source = null;
                try
                {
                    source = Path.Combine(Path.GetTempPath(), "FPSLimiter-" + RtssOverlayFileName);
                    File.WriteAllBytes(source, RtssOverlayPreset.GetBytes());
                }
                catch { source = null; }
            }
            if (source == null || !File.Exists(source))
            {
                MessageBox.Show("The overlay preset (" + RtssOverlayFileName + ") is missing from this build of the extension.",
                    "RTSS overlay preset", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string targetDir = Path.Combine(Path.GetDirectoryName(rtssExe) ?? string.Empty, "Plugins", "Client", "Overlays");
            string target = Path.Combine(targetDir, RtssOverlayFileName);
            try
            {
                if (File.Exists(target) && !FilesEqual(source, target))
                {
                    var ask = MessageBox.Show(RtssOverlayFileName + " already exists in RTSS and is different. Replace it?\n(The old file is kept as " + RtssOverlayFileName + ".bak)",
                        "RTSS overlay preset", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (ask != MessageBoxResult.Yes) return;
                }
                try
                {
                    Directory.CreateDirectory(targetDir);
                    if (File.Exists(target) && !FilesEqual(source, target)) File.Copy(target, target + ".bak", true);
                    File.Copy(source, target, true);
                }
                catch (UnauthorizedAccessException)
                {
                    // RTSS usually lives under Program Files: retry once with administrator rights.
                    var args = "/c mkdir \"" + targetDir + "\" 2>nul & copy /Y \"" + source + "\" \"" + target + "\"";
                    using (var p = Process.Start(new ProcessStartInfo("cmd.exe", args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }))
                        p?.WaitForExit();
                    if (!File.Exists(target)) throw new InvalidOperationException("Administrator copy was cancelled or failed.");
                }
                // The preset uses Oswald Medium: install the font (per-user, no admin needed) and make it RTSS's OSD font.
                string fontNote;
                try
                {
                    OswaldFont.Install();
                    new global::FPSLimiter.RtssLimiterBackend(vm.Settings).EnsureGlobalDefaults(rtssExe);
                    fontNote = "\n\nThe Oswald Medium font is installed and set as the RTSS font. If RTSS is open, restart it once so it picks the font up.";
                }
                catch (Exception fontEx)
                {
                    fontNote = "\n\nThe preset was installed, but the Oswald Medium font could not be installed:\n" + fontEx.Message;
                }

                string pluginNote;
                try
                {
                    ConfigureRtssPluginSettings(rtssExe, vm.Settings.RtssOverlayHotkey);
                    pluginNote = "\n\nBoth plugins are enabled, the overlay editor loads " + RtssOverlayFileName + " and the overlay toggle hotkey is set to " + vm.Settings.RtssOverlayHotkeyDisplay + ".";
                }
                catch (Exception cfgEx)
                {
                    pluginNote = "\n\nCould not set the overlay layout / hotkey automatically:\n" + cfgEx.Message;
                }

                RefreshDownloadInstallStatus();
                MessageBox.Show("Installed to:\n" + target + fontNote + pluginNote + "\n\nHotkeyHandler.dll and OverlayEditor.dll are ticked in RTSS > Setup > Plugins.",
                    "RTSS overlay preset", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not install the overlay preset:\n" + ex.Message + "\n\nYou can copy it manually from:\n" + source,
                    "RTSS overlay preset", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static bool FilesEqual(string a, string b)
        {
            try { return File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b)); }
            catch { return false; }
        }

        private async void DownloadRtss_Click(object sender, RoutedEventArgs e)
        {
            await DownloadAndInstallRtssAsync();
        }

        private async void DownloadSpecialK_Click(object sender, RoutedEventArgs e)
        {
            await DownloadAndInstallSpecialKAsync();
        }

        private async void UpdateSpecialKNow_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as global::FPSLimiter.FPSLimiterSettingsViewModel;
            if (vm == null) return;
            SetActionButtonsEnabled(false);
            SetUpdatesStatus("Updating Special K core DLLs…", true);
            try
            {
                await vm.UpdateSpecialKNowAsync();
                SetUpdatesStatus(vm.Settings.HasAvailableSpecialKUpdate
                    ? "An update is still available. See the message for details."
                    : "Update action finished. See the message for details.", false);
            }
            catch (Exception ex)
            {
                SetUpdatesStatus("Update failed: " + ex.Message, false);
            }
            finally { SetActionButtonsEnabled(true); }
        }

        private async void CheckSpecialKUpdatesNow_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = DataContext as global::FPSLimiter.FPSLimiterSettingsViewModel;
            if (viewModel == null)
            {
                MessageBox.Show("Could not access FPS Limiter 3000 settings. Reopen the settings window and try again.",
                    "Special K updates", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            SetActionButtonsEnabled(false);
            SetUpdatesStatus("Checking Special K for updates…", true);
            try
            {
                await viewModel.CheckSpecialKUpdatesNowAsync();
                var settings = viewModel.Settings;
                string status = settings.HasAvailableSpecialKUpdate
                    ? "Update available: " + settings.SpecialKUpdateLastCheckedArtifact
                    : !string.IsNullOrWhiteSpace(settings.SpecialKUpdateLastCheckedArtifact)
                        ? "Special K is up to date (" + settings.SpecialKUpdateLastCheckedArtifact + ")."
                        : "Check finished. See the message for details.";
                SetUpdatesStatus(status, false);
            }
            catch (Exception ex) { SetUpdatesStatus("Update check failed: " + ex.Message, false); }
            finally
            {
                SetActionButtonsEnabled(true);
            }
        }

        private void OpenSpecialKFolder_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as global::FPSLimiter.FPSLimiterSettingsViewModel;
            var configured = vm?.Settings?.SpecialKPath;
            var path = configured;

            try
            {
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open the Special K folder:\n" + ex.Message,
                    "Special K folder", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BrowseSpecialKFolder_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as global::FPSLimiter.FPSLimiterSettingsViewModel;
            if (vm?.Settings == null) return;

            using (var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select the Special K install folder",
                SelectedPath = vm.Settings.SpecialKPath
            })
            {
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK &&
                    !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                {
                    vm.Settings.SpecialKPath = dialog.SelectedPath;
                }
            }
        }

        private void BrowseRtssPath_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as global::FPSLimiter.FPSLimiterSettingsViewModel;
            if (vm?.Settings == null) return;

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select RTSS.exe",
                Filter = "RTSS executable (RTSS.exe)|RTSS.exe|Executable files (*.exe)|*.exe",
                CheckFileExists = true,
                FileName = string.IsNullOrWhiteSpace(vm.Settings.RtssPath)
                    ? "RTSS.exe"
                    : vm.Settings.RtssPath
            };
            if (dialog.ShowDialog() == true)
                vm.Settings.RtssPath = dialog.FileName;
        }

        private void OpenRtssFolder_Click(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as global::FPSLimiter.FPSLimiterSettingsViewModel;
            var configured = vm?.Settings?.RtssPath;
            var path = !string.IsNullOrWhiteSpace(configured)
                ? (File.Exists(configured) ? Path.GetDirectoryName(configured) : configured)
                : null;

            if (string.IsNullOrWhiteSpace(path))
            {
                MessageBox.Show("Select the RTSS.exe path first.", "RTSS folder",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open the RTSS folder:\n" + ex.Message,
                    "RTSS folder", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static readonly Uri RtssArchiveUri = new Uri("https://ftp.nluug.nl/pub/games/PC/guru3d/afterburner/%5BGuru3D%5D-RTSSSetup737Build28314.zip");
        private const string RtssArchiveSha256 = "9B084A8CB3E53EC1A673894D0B66E22B16C9FD8785636B020B2D422F3F2A820E";
        private const string SpecialKHome = "https://www.special-k.info/";

        private async System.Threading.Tasks.Task DownloadAndInstallRtssAsync()
        {
            string workDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FPSLimiter-RTSS-" + Guid.NewGuid().ToString("N"));
            string archivePath = System.IO.Path.Combine(workDir, "RTSS.zip");
            try
            {
                Directory.CreateDirectory(workDir);
                SetActionButtonsEnabled(false);
                SetDownloadsStatus("Downloading and verifying the RTSS installer…", true);
                using (var client = CreateDownloadClient())
                using (var response = await client.GetAsync(RtssArchiveUri, System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    using (var input = await response.Content.ReadAsStreamAsync())
                    using (var output = new System.IO.FileStream(archivePath, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write, System.IO.FileShare.None))
                        await input.CopyToAsync(output);
                }

                string hash;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var stream = System.IO.File.OpenRead(archivePath))
                    hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
                if (!string.Equals(hash, RtssArchiveSha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteDirectory(workDir);
                    throw new InvalidOperationException("The downloaded RTSS archive failed SHA-256 verification. It was deleted and will not be run.");
                }

                System.IO.Compression.ZipFile.ExtractToDirectory(archivePath, workDir);
                string installer = Directory.GetFiles(workDir, "RTSSSetup737.exe", System.IO.SearchOption.AllDirectories).FirstOrDefault();
                if (installer == null)
                    throw new InvalidOperationException("The verified RTSS archive did not contain RTSSSetup737.exe.");

                SetDownloadsStatus("RTSS archive verified. Waiting for installer confirmation…", true);
                var result = MessageBox.Show("RTSS 7.3.7 has been downloaded and verified. Install it now? The installer runs silently (no wizard), and Windows will ask for administrator permission.",
                    "FPS Limiter 3000", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes)
                {
                    TryDeleteDirectory(workDir);
                    SetDownloadsStatus("RTSS installation cancelled.", false);
                    return;
                }

                SetDownloadsStatus("Waiting for the RTSS installer to finish…", true);
                // NSIS silent install. Skips the "searching for open programs" page, whose LockedList64.dll
                // helper (run through rundll32) fails here with "The specified module could not be found".
                bool installed = await LaunchInstallerAndWaitAsync(installer, "/S");
                if (installed)
                {
                    TryDeleteDirectory(workDir);
                    SetDownloadsStatus("RTSS installer completed successfully.", false);
                    MessageBox.Show("RTSS installer exited successfully. The downloaded archive and extracted installer were deleted.", "FPS Limiter 3000", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    SetDownloadsStatus("The RTSS installer did not report success. Review the message for the retained files.", false);
                    MessageBox.Show("The RTSS installer did not report a successful exit. The temporary files were kept at:\n" + workDir,
                        "FPS Limiter 3000", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                SetDownloadsStatus("RTSS download/install failed: " + ex.Message, false);
                MessageBox.Show("Could not download or install RTSS.\n\n" + ex.Message, "FPS Limiter 3000", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetActionButtonsEnabled(true);
                SetDownloadsProgress(false);
                RefreshDownloadInstallStatus();
                if (Directory.Exists(workDir) && !File.Exists(archivePath)) TryDeleteDirectory(workDir);
            }
        }

        private async System.Threading.Tasks.Task DownloadAndInstallSpecialKAsync()
        {
            string workDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FPSLimiter-SpecialK-" + Guid.NewGuid().ToString("N"));
            string installerPath = System.IO.Path.Combine(workDir, "SpecialK-Setup.exe");
            try
            {
                Directory.CreateDirectory(workDir);
                SetActionButtonsEnabled(false);
                SetDownloadsStatus("Finding and downloading the Special K installer (Discord branch)…", true);
                // Default: newest build of SKIF's "Discord (updates regularly)" branch, SHA-256 verified.
                // Fallback: the Stable installer linked on special-k.info.
                Uri installerUri;
                string expectedSha = null;
                string branchLabel = "Discord branch";
                try
                {
                    var discord = await global::FPSLimiter.SpecialKUpdateService.GetLatestDiscordInstallerAsync();
                    installerUri = discord.Item1;
                    expectedSha = discord.Item2;
                    branchLabel = "Discord branch " + discord.Item3;
                }
                catch (Exception manifestError)
                {
                    installerUri = await FindSpecialKInstallerAsync();
                    branchLabel = "Stable (Discord manifest unavailable: " + manifestError.Message + ")";
                }
                using (var client = CreateDownloadClient())
                using (var response = await client.GetAsync(installerUri, System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    Uri finalUri = response.RequestMessage != null ? response.RequestMessage.RequestUri : installerUri;
                    if (finalUri == null || finalUri.Scheme != Uri.UriSchemeHttps ||
                        !(finalUri.Host.Equals("special-k.info", StringComparison.OrdinalIgnoreCase) || finalUri.Host.EndsWith(".special-k.info", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("The installer download redirected to an unexpected host; refusing to run it.");
                    if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value < 100000)
                        throw new InvalidOperationException("The official download returned an unexpectedly small file; it was not run.");
                    using (var input = await response.Content.ReadAsStreamAsync())
                    using (var output = new System.IO.FileStream(installerPath, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write, System.IO.FileShare.None))
                        await input.CopyToAsync(output);
                }
                if (new System.IO.FileInfo(installerPath).Length < 100000)
                {
                    TryDeleteDirectory(workDir);
                    throw new InvalidOperationException("The downloaded file is too small to be a valid installer; it was deleted.");
                }
                if (!string.IsNullOrWhiteSpace(expectedSha))
                {
                    string actualSha;
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                    using (var hashStream = System.IO.File.OpenRead(installerPath))
                        actualSha = BitConverter.ToString(sha.ComputeHash(hashStream)).Replace("-", string.Empty);
                    if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDeleteDirectory(workDir);
                        throw new InvalidOperationException("The installer failed its SHA-256 check; it was deleted and not run.");
                    }
                }
                using (var file = System.IO.File.OpenRead(installerPath))
                {
                    if (file.ReadByte() != 0x4D || file.ReadByte() != 0x5A)
                    {
                        TryDeleteDirectory(workDir);
                        throw new InvalidOperationException("The official download did not return a Windows executable. The file was deleted.");
                    }
                }

                SetDownloadsStatus("Special K installer verified. Waiting for installer confirmation…", true);
                var result = MessageBox.Show("The Special K + SKIF installer (" + branchLabel + ") has been downloaded. Install it now? The installer runs silently (no wizard), and Windows may ask for permission.",
                    "FPS Limiter 3000", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes)
                {
                    TryDeleteDirectory(workDir);
                    SetDownloadsStatus("Special K installation cancelled.", false);
                    return;
                }

                SetDownloadsStatus("Waiting for the Special K installer to finish…", true);
                // Fully silent install, like RTSS: no wizard and no progress window. This also skips the
                // wizard's own "Launch Special K" checkbox, which runs rundll32 on SpecialK64.dll and fails
                // with "The specified module could not be found".
                bool installed = await LaunchInstallerAndWaitAsync(installerPath, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL", false, true);
                if (installed)
                {
                    TryDeleteDirectory(workDir);
                    SetDownloadsStatus("Special K installer completed successfully.", false);
                    MessageBox.Show("The Special K installer exited successfully. The downloaded installer was deleted.", "FPS Limiter 3000", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    SetDownloadsStatus("The Special K installer did not report success. Review the message for the retained file.", false);
                    MessageBox.Show("The Special K installer did not report a successful exit. The installer was kept at:\n" + installerPath,
                        "FPS Limiter 3000", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                SetDownloadsStatus("Special K download/install failed: " + ex.Message, false);
                MessageBox.Show("Could not download or install Special K.\n\n" + ex.Message, "FPS Limiter 3000", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetActionButtonsEnabled(true);
                SetDownloadsProgress(false);
                RefreshDownloadInstallStatus();
                if (Directory.Exists(workDir) && !File.Exists(installerPath)) TryDeleteDirectory(workDir);
            }
        }

        private static async System.Threading.Tasks.Task<Uri> FindSpecialKInstallerAsync()
        {
            using (var client = CreateDownloadClient())
            {
                string html = await client.GetStringAsync(SpecialKHome);
                // Select the anchor whose visible label is "Installer" in the official Stable section.
                var matches = System.Text.RegularExpressions.Regex.Matches(html,
                    "<a\\b[^>]*href\\s*=\\s*[\\\"'](?<url>[^\\\"']+)[\\\"'][^>]*>(?<text>.*?)</a>",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    string label = System.Text.RegularExpressions.Regex.Replace(match.Groups["text"].Value, "<.*?>", string.Empty).Trim();
                    label = System.Net.WebUtility.HtmlDecode(label);
                    if (!string.Equals(label, "Installer", StringComparison.OrdinalIgnoreCase)) continue;
                    Uri candidate;
                    if (!Uri.TryCreate(new Uri(SpecialKHome), System.Net.WebUtility.HtmlDecode(match.Groups["url"].Value), out candidate)) continue;
                    string host = candidate.Host.ToLowerInvariant();
                    if (candidate.Scheme != Uri.UriSchemeHttps || !(host == "special-k.info" || host.EndsWith(".special-k.info")))
                        throw new InvalidOperationException("The official Special K page pointed to an unexpected installer host; refusing to download it.");
                    return candidate;
                }
            }
            throw new InvalidOperationException("Could not find the Stable Installer link on the official Special K website. The site layout may have changed.");
        }

        private static System.Net.Http.HttpClient CreateDownloadClient()
        {
            var handler = new System.Net.Http.HttpClientHandler { AllowAutoRedirect = true };
            return new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        }

        private static async System.Threading.Tasks.Task<bool> LaunchInstallerAndWaitAsync(string path, string arguments, bool forceElevate = true, bool dismissSpecialKRunDllError = false)
        {
            // Forced "runas" makes the installer's child processes run elevated, and a non-elevated
            // Playnite cannot close their windows. The Special K installer is per-user, so it is
            // started normally (it asks for elevation itself only if it needs it).
            var start = new ProcessStartInfo(path) { UseShellExecute = true };
            if (forceElevate) start.Verb = "runas";
            if (!string.IsNullOrEmpty(arguments)) start.Arguments = arguments;
            using (var process = Process.Start(start))
            {
                if (process == null) return false;
                var cts = new System.Threading.CancellationTokenSource();
                System.Threading.Tasks.Task watcher = dismissSpecialKRunDllError
                    ? System.Threading.Tasks.Task.Run(() => DismissSpecialKRunDllErrors(cts.Token))
                    : System.Threading.Tasks.Task.FromResult(0);
                try
                {
                    await System.Threading.Tasks.Task.Run(() => process.WaitForExit());
                    // The installer's post-install step can pop the error just after the installer exits.
                    if (dismissSpecialKRunDllError) await System.Threading.Tasks.Task.Delay(8000);
                }
                finally { cts.Cancel(); try { await watcher; } catch { } }
                return process.ExitCode == 0;
            }
        }

        // The Special K installer's own post-install step runs rundll32 on SpecialK64.dll and can show
        // "There was a problem starting SpecialK64.dll / The specified module could not be found."
        // The install itself is fine, so close just that dialog.
        private static void DismissSpecialKRunDllErrors(System.Threading.CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    IntPtr after = IntPtr.Zero, hwnd;
                    while ((hwnd = FindWindowEx(IntPtr.Zero, after, "#32770", "RunDLL")) != IntPtr.Zero)
                    {
                        after = hwnd;
                        uint pid; GetWindowThreadProcessId(hwnd, out pid);
                        string owner;
                        try { using (var p = Process.GetProcessById((int)pid)) owner = p.ProcessName; } catch { continue; }
                        if (!string.Equals(owner, "rundll32", StringComparison.OrdinalIgnoreCase)) continue;
                        var text = new System.Text.StringBuilder();
                        EnumChildWindows(hwnd, (child, l) =>
                        {
                            var sb = new System.Text.StringBuilder(512);
                            GetWindowText(child, sb, sb.Capacity);
                            text.Append(sb).Append(' ');
                            return true;
                        }, IntPtr.Zero);
                        if (text.ToString().IndexOf("SpecialK", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            ShowWindow(hwnd, 0 /* SW_HIDE */); // hide first so it never stays on screen
                            PostMessage(hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                        }
                    }
                }
                catch { }
                try { token.WaitHandle.WaitOne(10); } catch { return; }
            }
        }

        private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int maxCount);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        private void SetActionButtonsEnabled(bool enabled)
        {
            if (DownloadRtssButton != null) DownloadRtssButton.IsEnabled = enabled;
            if (DownloadSpecialKButton != null) DownloadSpecialKButton.IsEnabled = enabled;
            if (CheckSpecialKUpdatesButton != null) CheckSpecialKUpdatesButton.IsEnabled = enabled;
            if (UpdateSpecialKNowButton != null) UpdateSpecialKNowButton.IsEnabled = enabled;
        }

        private void SetDownloadsStatus(string message, bool busy)
        {
            if (DownloadsStatusText != null) DownloadsStatusText.Text = message ?? string.Empty;
            if (DownloadsStatusCard != null)
                DownloadsStatusCard.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
            SetDownloadsProgress(busy);
        }

        private void SetDownloadsProgress(bool busy)
        {
            if (DownloadsProgressBar != null)
                DownloadsProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetUpdatesStatus(string message, bool busy)
        {
            if (UpdatesStatusText != null) UpdatesStatusText.Text = message ?? string.Empty;
            if (UpdatesStatusCard != null)
                UpdatesStatusCard.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
            if (UpdatesProgressBar != null)
                UpdatesProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch { /* Leave temporary files in place rather than reporting a false cleanup success. */ }
        }

        private void MonitorVrrSafeCap_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is MonitorResolutionEditor editor)
            {
                editor.ApplyVrrSafeCap();
            }
        }

        private void AddMonitorPreset_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is MonitorResolutionEditor editor)
            {
                editor.TryAddPreset();
            }
        }

        private void MonitorPresetInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Return)
                return;

            e.Handled = true;
            if ((sender as FrameworkElement)?.DataContext is MonitorResolutionEditor editor)
            {
                editor.TryAddPreset();
            }
        }

        private void RemoveMonitorPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button &&
                button.Tag is MonitorResolutionEditor editor &&
                button.DataContext is string preset)
            {
                editor.RemovePreset(preset);
            }
        }

        private void ResetScanlineTarget_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is FPSLimiterSettingsViewModel viewModel)
            {
                viewModel.Settings.RtssScanlineSyncTarget = FPSLimiterSettings.DefaultRtssScanlineSyncTarget;
            }
        }

        /// <summary>
        /// Captures the next key combo pressed while a hotkey box has focus and stores it on the
        /// HotkeyBinding bound to that row. Escape clears the assigned combo instead.
        /// </summary>
        private void HotkeyCaptureBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;

            if (!(sender is FrameworkElement element) || !(element.DataContext is HotkeyBinding binding))
            {
                return;
            }

            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LWin || key == Key.RWin ||
                key == Key.System || key == Key.None)
            {
                return;
            }

            if (key == Key.Escape)
            {
                binding.Key = Key.None;
                binding.Modifiers = ModifierKeys.None;
                return;
            }

            binding.Modifiers = Keyboard.Modifiers;
            binding.Key = key;
        }

        private void SpecialKHotkeyCaptureBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            if (!(sender is FrameworkElement element) || !(element.DataContext is SpecialKMacroHotkey macro)) return;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin ||
                key == Key.System || key == Key.None) return;

            if (key == Key.Escape)
            {
                macro.Key = Key.None;
                macro.Modifiers = ModifierKeys.None;
                return;
            }

            macro.Modifiers = Keyboard.Modifiers;
            macro.Key = key;
        }

        private FPSLimiterSettingsViewModel SettingsVm => DataContext as FPSLimiterSettingsViewModel;

        private void CapturePlayniteMenuHotkey_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            var vm = SettingsVm;
            if (vm?.Settings == null) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape)
            {
                vm.Settings.PlayniteMenuHotkey = "None";
                return;
            }
            if (PlayniteHotkey.TryCapture(e, out var binding))
                vm.Settings.PlayniteMenuHotkey = binding;
        }

        private void ClearPlayniteMenuHotkey_Click(object sender, RoutedEventArgs e)
        {
            var vm = SettingsVm;
            if (vm?.Settings != null) vm.Settings.PlayniteMenuHotkey = "None";
        }

        private void CaptureGlobalSettingsHotkey_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            var vm = SettingsVm;
            if (vm?.Settings == null) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape)
            {
                vm.Settings.GlobalSettingsHotkey = "None";
                return;
            }
            if (PlayniteHotkey.TryCapture(e, out var binding))
                vm.Settings.GlobalSettingsHotkey = binding;
        }

        private void ClearGlobalSettingsHotkey_Click(object sender, RoutedEventArgs e)
        {
            var vm = SettingsVm;
            if (vm?.Settings != null) vm.Settings.GlobalSettingsHotkey = "None";
        }

        private void ApplyRtssOverlayHotkey(FPSLimiterSettingsViewModel vm)
        {
            var rtssExe = vm?.Settings?.RtssPath;
            if (string.IsNullOrWhiteSpace(rtssExe) || !File.Exists(rtssExe)) return;
            try { ConfigureRtssPluginSettings(rtssExe, vm.Settings.RtssOverlayHotkey); }
            catch (Exception ex)
            {
                MessageBox.Show("Could not write the RTSS overlay hotkey:\n" + ex.Message, "RTSS overlay hotkey", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CaptureRtssOverlayHotkey_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            var vm = SettingsVm;
            if (vm?.Settings == null) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape)
            {
                vm.Settings.RtssOverlayHotkey = "None";
                ApplyRtssOverlayHotkey(vm);
                return;
            }
            if (PlayniteHotkey.TryCapture(e, out var binding))
            {
                if (!RtssHotkeyCodec.IsSupported(binding))
                {
                    MessageBox.Show("RTSS hotkeys can't use the Windows key. Pick a different combination.", "RTSS overlay hotkey", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                vm.Settings.RtssOverlayHotkey = binding;
                ApplyRtssOverlayHotkey(vm);
            }
        }

        private void ClearRtssOverlayHotkey_Click(object sender, RoutedEventArgs e)
        {
            var vm = SettingsVm;
            if (vm?.Settings == null) return;
            vm.Settings.RtssOverlayHotkey = "None";
            ApplyRtssOverlayHotkey(vm);
        }

        private void ApplyBrandIcons()
        {
            try
            {
                var rtss = global::FPSLimiter.BrandIcons.Rtss(18);
                if (RtssSectionIcon != null && rtss != null) RtssSectionIcon.Source = rtss;
                var sk = global::FPSLimiter.BrandIcons.SpecialK(18);
                if (SpecialKSectionIcon != null && sk != null) SpecialKSectionIcon.Source = sk;
            }
            catch { }
        }
    }
}
