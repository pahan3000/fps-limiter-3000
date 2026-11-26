using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FPSLimiter
{
    public partial class GameSettingsView : UserControl
    {
        private readonly LimiterService limiterService;
        private readonly List<Game> games;
        private bool suppressEvents;
        private bool loadingGamePreferredMonitor;
        public Action CloseRequested { get; set; }
        public Action OpenGlobalSettingsRequested { get; set; }
        // The vertical scrollbar appears once sections are opened. Widen the window by exactly the
        // scrollbar width so the content area never gets narrower when a section is opened.
        private double scrollbarGutterAdded;
        // Accordion: opening a section closes its siblings and scrolls the opened one into view.
        private void WireAccordion(Panel panel)
        {
            if (panel == null) return;
            foreach (var exp in panel.Children.OfType<Expander>())
                exp.Expanded += AccordionSection_Expanded;
        }

        private void AccordionSection_Expanded(object sender, RoutedEventArgs e)
        {
            if (!(sender is Expander current) || !ReferenceEquals(e.OriginalSource, current)) return;
            if (current.Parent is Panel siblings)
            {
                foreach (var other in siblings.Children.OfType<Expander>())
                    if (!ReferenceEquals(other, current)) other.IsExpanded = false;
            }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var viewer = MainScroll;
                    var content = viewer?.Content as UIElement;
                    if (viewer == null || content == null || viewer.ScrollableHeight <= 0) { current.BringIntoView(); return; }
                    viewer.UpdateLayout();
                    var offset = current.TranslatePoint(new Point(0, 0), content).Y;
                    viewer.ScrollToVerticalOffset(Math.Max(0, offset - 6));
                }
                catch { /* cosmetic */ }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void OnMainScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ViewportHeightChange == 0 && e.ExtentHeightChange == 0) return;
            var window = Window.GetWindow(this);
            if (window == null) return;
            var needed = MainScroll.ComputedVerticalScrollBarVisibility == Visibility.Visible
                ? SystemParameters.VerticalScrollBarWidth : 0.0;
            var delta = needed - scrollbarGutterAdded;
            if (Math.Abs(delta) < 0.5) return;
            scrollbarGutterAdded = needed;
            window.MinWidth = Math.Max(0, window.MinWidth + delta);
            window.Width += delta;
            window.Left -= delta / 2;
        }

        private void OnGlobalSettingsClick(object sender, RoutedEventArgs e) => OpenGlobalSettingsRequested?.Invoke();

        public Action OnBackendChanged { get; set; }

        public GameSettingsView(LimiterService limiterService, List<Game> games)
        {
            InitializeComponent();
            this.limiterService = limiterService;
            AppearancePalette.Apply(this, limiterService.Settings.ThemePreset, limiterService.Settings.GlassEffect);
            AppearancePalette.ApplyUnifiedSectionGlass(this, limiterService.Settings.ThemePreset, limiterService.Settings.GlassEffect);
            this.games = games ?? new List<Game>();
            ApplyBrandIcons();
            WireAccordion(RtssPanel?.Parent as Panel);
            WireAccordion((SpecialKPanel?.Content as Panel));

            suppressEvents = true;
            BuildPresets();
            PopulateSyncModes();
            PopulateSpecialKCompatibilityOptions();
            RefreshFromService();
            suppressEvents = false;
            ApplySpecialKOverrideState();

            Loaded += (_, __) =>
            {
                ApplySpecialKOverrideState();
                limiterService.Settings.PropertyChanged -= OnLimiterSettingsPropertyChanged;
                limiterService.Settings.PropertyChanged += OnLimiterSettingsPropertyChanged;
                RefreshScanlineTargetUi();
                StartLiveSpecialKFpsWatch();
            };
            Unloaded += (_, __) =>
            {
                limiterService.Settings.PropertyChanged -= OnLimiterSettingsPropertyChanged;
                liveSkFpsTimer?.Stop();
            };
        }

        // ---- Live Special K FPS (changed from SK's own control panel) ----
        private System.Windows.Threading.DispatcherTimer liveSkFpsTimer;
        // Text for the "Selected" label when no live Special K value applies (RTSS / not yet created ini).
        private string baseFpsText = string.Empty;

        private void SetFpsText(string text)
        {
            baseFpsText = text;
            UpdateLiveSpecialKFpsText();
        }

        private void StartLiveSpecialKFpsWatch()
        {
            if (liveSkFpsTimer == null)
            {
                liveSkFpsTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                liveSkFpsTimer.Tick += (_, __) => UpdateLiveSpecialKFpsText();
            }
            liveSkFpsTimer.Start();
            UpdateLiveSpecialKFpsText();
        }

        /// <summary>
        /// For Special K games shows only the cap currently stored in SpecialK.ini (which SK's control
        /// panel edits) as "Current: ..." so there is a single, unambiguous number. Other backends keep
        /// the normal "Current: ..." text.
        /// </summary>
        private void UpdateLiveSpecialKFpsText()
        {
            try
            {
                if (CurrentFpsText == null) return;
                var text = baseFpsText ?? string.Empty;
                if (IsVisible && games != null && games.Count > 0)
                {
                    var live = limiterService.ReadSpecialKLiveTargetFps(games[0]);
                    // Special K: show only what is actually in SpecialK.ini.
                    if (live.HasValue)
                        text = "Current: " + (live.Value <= 0 ? "no cap" : FormatFps(live.Value));
                }
                if (!string.Equals(CurrentFpsText.Text, text, StringComparison.Ordinal))
                    CurrentFpsText.Text = text;
                if (IsVisible) UpdatePresetHighlight();
            }
            catch { /* cosmetic */ }
        }


        /// <summary>
        /// Mirrors the global "Override Special K settings" switch: when it is off, the Special K latency and
        /// compatibility options are greyed out here just like in the global settings panel.
        /// Injection and the "Edit/Open SpecialK.ini" buttons stay available.
        /// </summary>
        private void ApplySpecialKOverrideState()
        {
            try
            {
                var enabled = limiterService?.Settings?.ApplySpecialKSettingsToExistingProfiles ?? false;
                if (SkLatencyOptionsPanel != null) SkLatencyOptionsPanel.IsEnabled = enabled;
                if (SkCompatOptionsPanel != null) SkCompatOptionsPanel.IsEnabled = enabled;
                if (SkUseGlobalCompatButton != null) SkUseGlobalCompatButton.IsEnabled = enabled;
            }
            catch { /* cosmetic */ }
        }

        private void OnLimiterSettingsPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e == null) return;
            if (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName == nameof(FPSLimiterSettings.ThemePreset) ||
                e.PropertyName == nameof(FPSLimiterSettings.GlassEffect))
            {
                AppearancePalette.Apply(this, limiterService.Settings.ThemePreset, limiterService.Settings.GlassEffect);
            AppearancePalette.ApplyUnifiedSectionGlass(this, limiterService.Settings.ThemePreset, limiterService.Settings.GlassEffect);
            }
            if (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName == nameof(FPSLimiterSettings.ApplySpecialKSettingsToExistingProfiles))
            {
                Dispatcher.BeginInvoke(new Action(ApplySpecialKOverrideState));
            }
            if (e.PropertyName != nameof(FPSLimiterSettings.RtssScanlineSyncTarget)) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsLoaded) RefreshScanlineTargetUi();
            }));
        }

        // Scanline modes select a display timing, not an arbitrary FPS preset.
        // Keep this strictly in the game-settings UI; LimiterService owns the RTSS logic.
        private bool? showingRefreshRatePresets;
        private FpsSyncMode? showingPresetSyncMode;
        private string showingPresetMonitor;

        private bool UseScanlineRefreshRates()
        {
            if (games.Count == 0 || limiterService.ShouldUseSpecialKBackend(games[0]))
                return false;
            var mode = limiterService.GetEffectiveGameSyncMode(games[0]);
            return mode == FpsSyncMode.ScanlineSync || mode == FpsSyncMode.ScanlineSyncHalf;
        }

        /// <summary>Display type of the monitor the (first) selected game runs on.</summary>
        private bool IsFixedRefreshForGames()
        {
            return games.Count > 0
                ? limiterService.IsFixedRefreshDisplay(games[0])
                : limiterService.Settings.FixedRefreshDisplayMode;
        }

        private IEnumerable<double> PresetsForGames()
        {
            return games.Count > 0 ? limiterService.GetPresets(games[0]) : limiterService.GetPresets();
        }

        private void BuildPresets()
        {
            if (limiterService == null || PresetList == null) return;
            var scanline = IsFixedRefreshForGames() && UseScanlineRefreshRates();
            var currentSync = games.Count == 0 ? FpsSyncMode.Async : limiterService.GetEffectiveGameSyncMode(games[0]);
            // The presets belong to the display the game uses, so a change of display must rebuild them.
            var monitorKey = games.Count > 0
                ? limiterService.GetGameMonitorDeviceName(games[0])
                : MonitorManager.PrimaryDeviceName;
            if (showingRefreshRatePresets == scanline && showingPresetSyncMode == currentSync &&
                string.Equals(showingPresetMonitor, monitorKey, StringComparison.OrdinalIgnoreCase) && PresetList.Items.Count != 0) return;
            showingRefreshRatePresets = scanline;
            showingPresetSyncMode = currentSync;
            showingPresetMonitor = monitorKey;
            PresetList.Items.Clear();
            if (FpsCapHeading != null)
                FpsCapHeading.Text = scanline ? "Display refresh (Scanline)" : "FPS cap";
            var normalStyle = TryFindResource("FpsPresetButton") as Style;
            if (scanline)
            {
                var half = limiterService.GetEffectiveGameSyncMode(games[0]) == FpsSyncMode.ScanlineSyncHalf;
                var monitor = games.Count > 0
                    ? limiterService.GetGameMonitorDeviceName(games[0])
                    : MonitorManager.PrimaryDeviceName;
                foreach (var hz in RefreshRateManager.GetSupportedRefreshRates(monitor)
                    .Where(rate => rate > 0).Distinct().OrderBy(rate => rate))
                {
                    var selectedHz = hz;
                    var btn = new Button
                    {
                        Content = (half ? selectedHz / 2.0 : selectedHz).ToString("0.####", CultureInfo.InvariantCulture) + " FPS",
                        Tag = (double)selectedHz,
                        Style = normalStyle
                    };
                    btn.Click += (_, __) =>
                    {
                        limiterService.SetGameForcedRefreshRate(games, selectedHz);
                        RefreshFromService();
                    };
                    PresetList.Items.Add(btn);
                }
            }
            else
            {
                foreach (var preset in PresetsForGames())
                {
                    var fps = preset;
                    var btn = new Button
                    {
                        Content = FormatFps(fps),
                        Tag = fps,
                        Style = normalStyle
                    };
                    btn.Click += (_, __) =>
                    {
                        limiterService.SetGameLimit(games, fps);
                        RefreshFromService();
                    };
                    PresetList.Items.Add(btn);
                }
            }
        }


        private void PopulateSyncModes()
        {
            SyncModeCombo.Items.Clear();
            foreach (FpsSyncMode mode in Enum.GetValues(typeof(FpsSyncMode)))
            {
                if (!IsFixedRefreshForGames() &&
                    (mode == FpsSyncMode.ScanlineSync || mode == FpsSyncMode.ScanlineSyncHalf)) continue;
                SyncModeCombo.Items.Add(new ComboBoxItem
                {
                    Content = FpsSyncModeNames.GetMethodName(mode),
                    Tag = mode,
                    ToolTip = FpsSyncModeNames.GetDisplayName(mode)
                });
            }
        }

        private void PopulateSpecialKCompatibilityOptions()
        {
            SkWindowBehaviorCombo.Items.Clear();
            SkWindowBehaviorCombo.Items.Add(new ComboBoxItem { Content = "Use global setting", Tag = "global" });
            SkWindowBehaviorCombo.Items.Add(new ComboBoxItem { Content = "No Preference", Tag = SpecialKWindowLayering.NoPreference });
            SkWindowBehaviorCombo.Items.Add(new ComboBoxItem { Content = "Prevent Always-On-Top", Tag = SpecialKWindowLayering.PreventAlwaysOnTop });
            SkWindowBehaviorCombo.Items.Add(new ComboBoxItem { Content = "Force Always-On-Top", Tag = SpecialKWindowLayering.ForceAlwaysOnTop });
            SkWindowBehaviorCombo.Items.Add(new ComboBoxItem { Content = "Keep above other windows while multitasking", Tag = SpecialKWindowLayering.MultitaskingOnTop });
        }

        private void RefreshFromService()
        {
            if (games.Count == 0) return;

            var game = games[0];
            var useSk = limiterService.ShouldUseSpecialKBackend(game);
            UpdateBackendVisuals(useSk);
            var fixedRefresh = IsFixedRefreshForGames();
            var freeSyncRowVisible = false;

            var profile = limiterService.GetGameProfile(game);
            if (DisableFreeSyncCheck != null)
            {
                var previous = suppressEvents;
                suppressEvents = true;
                // Reflect the automatic Scanline override without saving it as a
                // permanent game preference. Lock the checkbox while Scanline owns it.
                var autoScanline = limiterService.IsScanlineFreeSyncOverride(game);
                var manuallyDisabled = profile?.DisableFreeSyncForGame == true;
                DisableFreeSyncCheck.IsChecked = manuallyDisabled || autoScanline;
                DisableFreeSyncCheck.IsEnabled = !autoScanline;
                // AMD FreeSync control only appears when an AMD GPU is installed (hidden on NVIDIA-only PCs,
                // still shown on laptops with both) and the helper can actually read the state.
                var actual = limiterService.HasAmdGpu ? limiterService.ReadFreeSyncEnabled() : null;
                var freeSyncUsable = actual.HasValue;
                DisableFreeSyncCheck.Visibility = freeSyncUsable ? Visibility.Visible : Visibility.Collapsed;
                FreeSyncStatusText.Visibility = freeSyncUsable ? Visibility.Visible : Visibility.Collapsed;
                freeSyncRowVisible = freeSyncUsable;
                if (freeSyncUsable)
                {
                    if (autoScanline && limiterService.IsFreeSyncGameRunning(game))
                        FreeSyncStatusText.Text = actual.Value ? "FreeSync is still on." : "FreeSync is off while Scanline Sync is used.";
                    else if (autoScanline)
                        FreeSyncStatusText.Text = "FreeSync turns off for this game (Scanline Sync).";
                    else
                        FreeSyncStatusText.Text = actual.Value ? "FreeSync is on." : "FreeSync is off.";
                }
                suppressEvents = previous;
            }
            // FreeSync now lives under "Manage refresh rate": show that section when either the
            // refresh-rate rows apply (fixed-refresh display) or FreeSync can be controlled.
            if (FreeSyncRow != null) FreeSyncRow.Visibility = freeSyncRowVisible ? Visibility.Visible : Visibility.Collapsed;
            if (RefreshRatesRows != null) RefreshRatesRows.Visibility = fixedRefresh ? Visibility.Visible : Visibility.Collapsed;
            if (PerGameRefreshPanel != null)
                PerGameRefreshPanel.Visibility = fixedRefresh || freeSyncRowVisible ? Visibility.Visible : Visibility.Collapsed;
            var silent = profile != null && profile.HasSteamLogSilentOverride
                ? profile.SpecialKSteamLogSilent
                : limiterService.Settings.SpecialKSteamLogSilent;
            LoadInjectionRadios();
            LoadVrrRadios();
            LoadForcedRefreshCombo();
            LoadSkLimiterRadios();
            LoadSkLatencyExtraRadios();
            LoadPreferredMonitorCombo();
            var previousSuppress = suppressEvents;
            suppressEvents = true;
            SteamSilentToggle.IsChecked = silent;
            suppressEvents = previousSuppress;

            var previousCompatibilitySuppress = suppressEvents;
            suppressEvents = true;
            SelectComboTag(SkWindowBehaviorCombo, profile?.HasSpecialKWindowLayeringOverride == true
                ? (object)profile.SpecialKWindowLayeringOverride : "global");
            SkDisableRumbleToggle.IsChecked = profile?.HasSpecialKDisableRumbleOverride == true
                ? profile.SpecialKDisableRumbleOverride : limiterService.Settings.SpecialKDisableRumble;
            SkPreloadOverlayToggle.IsChecked = profile?.HasSpecialKPreloadSteamOverlayOverride == true
                ? profile.SpecialKPreloadSteamOverlayOverride : limiterService.Settings.SpecialKPreloadSteamOverlay;
            SkDisableOverlayToggle.IsChecked = profile?.HasSpecialKDisableSteamOverlayOverride == true
                ? profile.SpecialKDisableSteamOverlayOverride : limiterService.Settings.SpecialKDisableSteamOverlay;
            SkUseFlipDiscardToggle.IsChecked = profile?.HasSpecialKUseFlipDiscardOverride == true
                ? profile.SpecialKUseFlipDiscardOverride : limiterService.Settings.SpecialKUseFlipDiscard;
            suppressEvents = previousCompatibilitySuppress;

            var limit = limiterService.GetEffectiveLimit(game);
            SetFpsText(limit.HasValue
                ? "Current: " + FormatFps(limit.Value)
                : "Current: none (global / disabled)");

            var modeLimit = profile?.GetMode(limiterService.CurrentMode);
            var selectedSyncMode = modeLimit?.SyncMode ?? FpsSyncMode.UseGlobal;
            SelectComboTag(SyncModeCombo, selectedSyncMode);
            SyncModeCombo.ToolTip = FpsSyncModeNames.GetDisplayName(selectedSyncMode);
            var priorSuppression = suppressEvents;
            suppressEvents = true;
            // Toggle shows the effective value: the game override if set, otherwise the global setting.
            var flipPreference = profile != null && profile.RtssForceFlipModel != RtssFlipPreference.UseDefault
                ? profile.RtssForceFlipModel : limiterService.Settings.RtssForceFlipModel;
            FlipModelToggle.IsChecked = flipPreference == RtssFlipPreference.Enabled;
            suppressEvents = priorSuppression;
            BuildPresets();
            RefreshPresetSelection();
            RefreshScanlineTargetUi();
        }

        /// <summary>
        /// Highlights a preset only when the cap actually in effect equals that preset exactly.
        /// For Special K games the cap is whatever is really in SpecialK.ini (it can be edited in SK's own
        /// control panel), so a different value such as 56 FPS highlights nothing.
        /// </summary>
        private void UpdatePresetHighlight()
        {
            if (games == null || games.Count == 0 || PresetList == null) return;
            var scanline = showingRefreshRatePresets == true;
            var currentLimit = limiterService.GetEffectiveLimit(games[0]);
            var profile = limiterService.GetGameProfile(games[0]);
            var forcedHz = profile != null ? profile.ForcedRefreshRateHz : 0;

            if (!scanline)
            {
                var live = limiterService.ReadSpecialKLiveTargetFps(games[0]);
                if (live.HasValue) currentLimit = live.Value;
            }

            var activeStyle = TryFindResource("FpsPresetButtonActive") as Style;
            var normalStyle = TryFindResource("FpsPresetButton") as Style;
            foreach (var btn in PresetList.Items.OfType<Button>())
            {
                var number = (double)btn.Tag;
                var active = profile?.SkipLimiterInjection != true && currentLimit.HasValue && currentLimit.Value > 0 &&
                    (scanline ? forcedHz > 0 && Math.Abs(number - forcedHz) < 0.01
                              : Math.Abs(number - currentLimit.Value) < 0.01);
                var wanted = active && activeStyle != null ? activeStyle : normalStyle;
                if (wanted != null && !ReferenceEquals(btn.Style, wanted)) btn.Style = wanted;
            }
        }

        private void RefreshPresetSelection()
        {
            if (games.Count == 0) return;
            var scanline = showingRefreshRatePresets == true;
            var half = scanline && limiterService.GetEffectiveGameSyncMode(games[0]) == FpsSyncMode.ScanlineSyncHalf;
            var currentLimit = limiterService.GetEffectiveLimit(games[0]);
            var profile = limiterService.GetGameProfile(games[0]);
            var forcedHz = profile != null ? profile.ForcedRefreshRateHz : 0;
            UpdatePresetHighlight();
            foreach (var btn in PresetList.Items.OfType<Button>())
            {
                var number = (double)btn.Tag;
                btn.Content = scanline
                    ? (half ? number / 2.0 : number).ToString("0.####", CultureInfo.InvariantCulture) + " FPS"
                    : FormatFps(number);
            }
            if (CurrentFpsText != null)
                SetFpsText(profile != null && profile.SkipLimiterInjection ? "Current: Don't inject"
                    : scanline && forcedHz > 0 && !(profile?.GetMode(limiterService.CurrentMode)?.Enabled == true && profile.GetMode(limiterService.CurrentMode).FrameLimit == 0)
                    ? "Current: " + (half ? forcedHz / 2.0 : forcedHz).ToString("0.####", CultureInfo.InvariantCulture) + " FPS"
                    : currentLimit.HasValue ? "Current: " + FormatFps(currentLimit.Value)
                    : "Current: none (global / disabled)");
        }

        private void ApplyBrandIcons()
        {
            var rtss = BrandIcons.Rtss(18);
            if (RtssButtonIcon != null && rtss != null) RtssButtonIcon.Source = rtss;
            if (RtssPanelIcon != null && rtss != null) RtssPanelIcon.Source = rtss;
            var sk = BrandIcons.SpecialK(18);
            if (SpecialKButtonIcon != null && sk != null) SpecialKButtonIcon.Source = sk;
            if (SpecialKPanelIcon != null && sk != null) SpecialKPanelIcon.Source = sk;
        }

        private void UpdateBackendVisuals(bool useSpecialK)
        {
            BackendStatusText.Text = useSpecialK ? "Current method: Special K" : "Current method: RTSS";
            var selectedStyle = TryFindResource("FpsBackendSelected") as Style;
            var optionStyle = TryFindResource("FpsBackendOption") as Style;
            if (selectedStyle != null && optionStyle != null)
            {
                UseSpecialKButton.Style = useSpecialK ? selectedStyle : optionStyle;
                UseRtssButton.Style = useSpecialK ? optionStyle : selectedStyle;
            }
            System.Windows.Automation.AutomationProperties.SetHelpText(UseSpecialKButton, useSpecialK ? "Selected" : "Not selected");
            System.Windows.Automation.AutomationProperties.SetHelpText(UseRtssButton, useSpecialK ? "Not selected" : "Selected");
            // Vulkan/DXVK games with "Don't use Special K for Vulkan games" on: hide Special K entirely.
            var hideSpecialK = games.Count > 0 && games.All(g => limiterService.IsSpecialKHiddenForGame(g));
            UseSpecialKButton.Visibility = hideSpecialK ? Visibility.Collapsed : Visibility.Visible;
            SpecialKButtonColumn.Width = hideSpecialK ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            BackendButtonSpacerColumn.Width = new GridLength(hideSpecialK ? 0 : 10);
            if (hideSpecialK)
                BackendStatusText.Text = "Current method: RTSS (Special K is disabled for Vulkan games)";
            SpecialKPanel.Visibility = useSpecialK && !hideSpecialK ? Visibility.Visible : Visibility.Collapsed;
            if (EditSkIniButton != null)
                EditSkIniButton.Visibility = useSpecialK && !hideSpecialK ? Visibility.Visible : Visibility.Collapsed;
            RtssPanel.Visibility = useSpecialK ? Visibility.Collapsed : Visibility.Visible;
            if (PresetList != null)
                PresetList.IsEnabled = true;
        }

        private void LoadPreferredMonitorCombo()
        {
            if (GamePreferredMonitorPanel == null || GamePreferredMonitorCombo == null)
                return;

            var displays = MonitorManager.GetDisplays();
            GamePreferredMonitorPanel.Visibility = displays.Count > 1
                ? Visibility.Visible : Visibility.Collapsed;
            if (displays.Count <= 1)
                return;

            loadingGamePreferredMonitor = true;
            try
            {
                GamePreferredMonitorCombo.Items.Clear();
                GamePreferredMonitorCombo.Items.Add(new ComboBoxItem
                {
                    Content = "Use global setting",
                    Tag = string.Empty
                });
                foreach (var display in displays)
                {
                    GamePreferredMonitorCombo.Items.Add(new ComboBoxItem
                    {
                        Content = display.Label,
                        Tag = display.DeviceName
                    });
                }

                var profile = games.Count > 0 ? limiterService.GetGameProfile(games[0]) : null;
                var selected = profile?.PreferredMonitorDeviceName ?? string.Empty;
                var selectedIndex = 0;
                for (var i = 1; i < GamePreferredMonitorCombo.Items.Count; i++)
                {
                    var item = GamePreferredMonitorCombo.Items[i] as ComboBoxItem;
                    var tag = item?.Tag as string;
                    if (string.Equals(tag, selected, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedIndex = i;
                        break;
                    }
                }
                GamePreferredMonitorCombo.SelectedIndex = selectedIndex;
            }
            finally
            {
                loadingGamePreferredMonitor = false;
            }
        }

        private void OnGamePreferredMonitorChanged(object sender, SelectionChangedEventArgs e)
        {
            if (loadingGamePreferredMonitor || suppressEvents || games == null || games.Count == 0)
                return;

            var item = GamePreferredMonitorCombo.SelectedItem as ComboBoxItem;
            limiterService.SetGamePreferredMonitor(games, item?.Tag as string ?? string.Empty);
            showingPresetMonitor = null; // force the presets of the newly selected display to show right away
            BuildPresets();
            RefreshPresetSelection();
            RefreshFromService();
        }

        private static void SelectComboTag(ComboBox combo, object tag)
        {
            for (var i = 0; i < combo.Items.Count; i++)
            {
                var item = combo.Items[i] as ComboBoxItem;
                if (item == null) continue;
                if (tag == null && item.Tag == null) { combo.SelectedIndex = i; return; }
                if (tag != null && item.Tag != null && tag.Equals(item.Tag)) { combo.SelectedIndex = i; return; }
            }
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        private void ApplyBackend(bool useSpecialK)
        {
            foreach (var game in games)
            {
                var profile = limiterService.Settings.GetOrCreateGameProfile(game.Id);
                profile.Backend = useSpecialK ? GameBackendPreference.SpecialK : GameBackendPreference.Rtss;
            }
            limiterService.SaveSettingsPublic();
            foreach (var game in games)
                limiterService.ReapplyIfRunningPublic(game);
            UpdateBackendVisuals(useSpecialK);
            BuildPresets();
            RefreshPresetSelection();
            try { OnBackendChanged?.Invoke(); } catch { }
        }

        private void OnUseSpecialKButtonClick(object sender, RoutedEventArgs e)
        {
            if (suppressEvents) return;
            ApplyBackend(true);
        }

        private void OnUseRtssButtonClick(object sender, RoutedEventArgs e)
        {
            if (suppressEvents) return;
            ApplyBackend(false);
        }

        private void OnSteamSilentChanged(object sender, RoutedEventArgs e)
        {
            if (suppressEvents) return;
            var silent = SteamSilentToggle.IsChecked == true;
            foreach (var game in games)
            {
                var profile = limiterService.Settings.GetOrCreateGameProfile(game.Id);
                profile.HasSteamLogSilentOverride = silent != limiterService.Settings.SpecialKSteamLogSilent;
                profile.SpecialKSteamLogSilent = silent;
            }
            limiterService.SaveSettingsPublic();
            foreach (var game in games)
            {
                limiterService.ApplySteamLogSilentToGamePublic(game, silent);
                limiterService.ReapplyIfRunningPublic(game);
            }
        }

        private void OnSkWindowBehaviorChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressEvents || limiterService == null || games == null || SkWindowBehaviorCombo.SelectedItem == null) return;
            var item = SkWindowBehaviorCombo.SelectedItem as ComboBoxItem;
            foreach (var game in games)
            {
                var profile = limiterService.Settings.GetOrCreateGameProfile(game.Id);
                if (item?.Tag is SpecialKWindowLayering layering)
                {
                    profile.HasSpecialKWindowLayeringOverride = true;
                    profile.SpecialKWindowLayeringOverride = layering;
                }
                else
                {
                    profile.HasSpecialKWindowLayeringOverride = false;
                }
            }
            limiterService.SaveSettingsPublic();
            foreach (var game in games) limiterService.ApplySpecialKCompatibilityToGamePublic(game);
            RefreshFromService();
        }

        private void OnSpecialKCompatibilityToggleChanged(object sender, RoutedEventArgs e)
        {
            if (suppressEvents || limiterService == null || games == null) return;
            foreach (var game in games)
            {
                var profile = limiterService.Settings.GetOrCreateGameProfile(game.Id);
                if (sender == SkDisableRumbleToggle)
                {
                    profile.HasSpecialKDisableRumbleOverride = true;
                    profile.SpecialKDisableRumbleOverride = SkDisableRumbleToggle.IsChecked == true;
                }
                else if (sender == SkPreloadOverlayToggle)
                {
                    profile.HasSpecialKPreloadSteamOverlayOverride = true;
                    profile.SpecialKPreloadSteamOverlayOverride = SkPreloadOverlayToggle.IsChecked == true;
                }
                else if (sender == SkDisableOverlayToggle)
                {
                    profile.HasSpecialKDisableSteamOverlayOverride = true;
                    profile.SpecialKDisableSteamOverlayOverride = SkDisableOverlayToggle.IsChecked == true;
                }
                else if (sender == SkUseFlipDiscardToggle)
                {
                    profile.HasSpecialKUseFlipDiscardOverride = true;
                    profile.SpecialKUseFlipDiscardOverride = SkUseFlipDiscardToggle.IsChecked == true;
                }
            }
            limiterService.SaveSettingsPublic();
            foreach (var game in games) limiterService.ApplySpecialKCompatibilityToGamePublic(game);
            RefreshFromService();
        }

        private void OnUseGlobalSpecialKCompatibilityClick(object sender, RoutedEventArgs e)
        {
            if (limiterService == null || games == null) return;
            foreach (var game in games)
            {
                var profile = limiterService.Settings.GetGameProfile(game.Id);
                if (profile == null) continue;
                profile.HasSteamLogSilentOverride = false;
                profile.HasSpecialKWindowLayeringOverride = false;
                profile.HasSpecialKDisableRumbleOverride = false;
                profile.HasSpecialKPreloadSteamOverlayOverride = false;
                profile.HasSpecialKDisableSteamOverlayOverride = false;
                profile.HasSpecialKUseFlipDiscardOverride = false;
            }
            limiterService.SaveSettingsPublic();
            foreach (var game in games)
            {
                limiterService.ApplySteamLogSilentToGamePublic(game, limiterService.Settings.SpecialKSteamLogSilent);
                limiterService.ApplySpecialKCompatibilityToGamePublic(game);
            }
            RefreshFromService();
        }

        /// <summary>The game's SpecialK.ini. Created (with the global options) when missing or empty.</summary>
        private string GetOrCreateGameSpecialKIni()
        {
            if (games == null || games.Count == 0) return null;
            var path = limiterService.FindSpecialKIniPathPublic(games[0]);
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path) ||
                SpecialKLimiterBackend.IsIniEmptyOrMissing(path))
                path = limiterService.EnsureSpecialKIniForGamePublic(games[0]);
            return path;
        }

        private void OnOpenGameSpecialKIniClick(object sender, RoutedEventArgs e)
        {
            var path = GetOrCreateGameSpecialKIni();
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                MessageBox.Show("SpecialK.ini could not be created. Check that Special K is installed and its folder is set in the FPS Limiter 3000 settings.", "FPS Limiter 3000", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        private void OnEditGameSpecialKIniClick(object sender, RoutedEventArgs e)
        {
            var path = GetOrCreateGameSpecialKIni();
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                MessageBox.Show("SpecialK.ini could not be created. Check that Special K is installed and its folder is set in the FPS Limiter 3000 settings.", "FPS Limiter 3000", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var name = games.Count == 1 ? games[0].Name : "game";
            SpecialKConfigEditor.Open(CreateDialogWindow, Window.GetWindow(this), limiterService.Settings,
                path, "SpecialK.ini editor — " + name);
            RefreshFromService();
        }

        private void OnFreeSyncChanged(object sender, RoutedEventArgs e)
        {
            if (suppressEvents || DisableFreeSyncCheck == null) return;
            foreach (var game in games)
            {
                limiterService.Settings.GetOrCreateGameProfile(game.Id).DisableFreeSyncForGame = DisableFreeSyncCheck.IsChecked == true;
                limiterService.RefreshFreeSyncForGame(game);
            }
            limiterService.SaveSettingsPublic();
            RefreshFromService();
        }

        private void OnFlipModelToggleChanged(object sender, RoutedEventArgs e)
        {
            if (suppressEvents || limiterService == null || games == null) return;
            limiterService.SetGameFlipPreference(games, FlipModelToggle.IsChecked == true
                ? RtssFlipPreference.Enabled : RtssFlipPreference.Disabled);
        }

        private void OnSyncModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressEvents || SyncModeCombo.SelectedItem == null) return;
            var item = SyncModeCombo.SelectedItem as ComboBoxItem;
            if (item?.Tag is FpsSyncMode mode)
            {
                if (!IsFixedRefreshForGames() &&
                    (mode == FpsSyncMode.ScanlineSync || mode == FpsSyncMode.ScanlineSyncHalf)) return;
                SyncModeCombo.ToolTip = FpsSyncModeNames.GetDisplayName(mode);
                limiterService.SetGameSyncMode(games, mode);
                // Scanline's temporary FreeSync override is shown immediately.
                // The saved manual preference is never changed by this refresh.
                RefreshFromService();
                // Update the display-Hz selector immediately after leaving Scanline.
                LoadForcedRefreshCombo();
                BuildPresets();
                RefreshPresetSelection();
                RefreshScanlineTargetUi();
            }
        }

        private void RefreshScanlineTargetUi()
        {
            if (ScanlineTargetPanel == null || SyncModeCombo == null) return;

            var selected = SyncModeCombo.SelectedItem as ComboBoxItem;
            var mode = selected?.Tag is FpsSyncMode value ? value : FpsSyncMode.UseGlobal;
            var effective = mode;
            if (effective == FpsSyncMode.UseGlobal && games.Count > 0)
                effective = limiterService.GetEffectiveGameSyncMode(games[0]);

            ScanlineTargetPanel.Visibility = (effective == FpsSyncMode.ScanlineSync || effective == FpsSyncMode.ScanlineSyncHalf)
                ? Visibility.Visible
                : Visibility.Collapsed;

            suppressEvents = true;
            ScanlineTargetSlider.Value = limiterService.Settings.RtssScanlineSyncTarget;
            ScanlineTargetBox.Text = limiterService.Settings.RtssScanlineSyncTarget.ToString(CultureInfo.InvariantCulture);
            suppressEvents = false;
        }

        private void OnScanlineTargetChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (suppressEvents) return;
            var value = (int)Math.Round(e.NewValue, MidpointRounding.AwayFromZero);
            if (ScanlineTargetBox != null)
                ScanlineTargetBox.Text = value.ToString(CultureInfo.InvariantCulture);
            limiterService.SetRtssScanlineSyncTargetPublic(games.FirstOrDefault(), value);
        }

        private void OnScanlineTargetTextChanged(object sender, RoutedEventArgs e)
        {
            if (suppressEvents) return;
            if (!int.TryParse(ScanlineTargetBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                RefreshScanlineTargetUi();
                return;
            }

            value = Math.Max(FPSLimiterSettings.MinRtssScanlineSyncTarget, Math.Min(FPSLimiterSettings.MaxRtssScanlineSyncTarget, value));
            limiterService.SetRtssScanlineSyncTargetPublic(games.FirstOrDefault(), value);
            RefreshScanlineTargetUi();
        }

        private void OnResetScanlineTargetClick(object sender, RoutedEventArgs e)
        {
            limiterService.SetRtssScanlineSyncTargetPublic(games.FirstOrDefault(), FPSLimiterSettings.DefaultRtssScanlineSyncTarget);
            RefreshScanlineTargetUi();
        }

        /// <summary>Set by the extension so dialogs get Playnite's dark window chrome.</summary>
        public Func<Window> CreateDialogWindow { get; set; }

        private void OnCustomClick(object sender, RoutedEventArgs e)
        {
            double? current = games.Count > 0 ? limiterService.GetEffectiveLimit(games[0]) : (double?)null;
            var input = new TextBox
            {
                Text = (current ?? 60).ToString("0.####", CultureInfo.InvariantCulture),
                MinHeight = 36,
                Padding = new Thickness(10, 6, 10, 6),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            var prompt = new TextBlock
            {
                Text = "FPS cap (e.g. 60 or 59.9)",
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap
            };
            var error = new TextBlock
            {
                Text = "Enter a number between 1 and 1000.",
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            error.SetResourceReference(TextBlock.ForegroundProperty, "FpsDanger");

            var flat = TryFindResource("FpsActionButton") as Style;
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88, Margin = new Thickness(0, 0, 8, 0), Style = flat };
            var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 88, Margin = new Thickness(0), Style = flat };
            ok.SetResourceReference(Control.BackgroundProperty, "UiPrimary");
            ok.SetResourceReference(Control.BorderBrushProperty, "UiPrimary");
            ok.SetResourceReference(Control.ForegroundProperty, "UiPrimaryText");

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);

            var stack = new StackPanel();
            stack.Children.Add(prompt);
            stack.Children.Add(input);
            stack.Children.Add(error);
            stack.Children.Add(buttons);

            // Share this view's resources so the dialog uses the same theme / glass brushes.
            var root = new Border { Padding = new Thickness(18), Child = stack, Resources = Resources };
            root.SetResourceReference(Border.BackgroundProperty, "ReShadeBg");

            Window dialog = null;
            try { dialog = CreateDialogWindow?.Invoke(); } catch { }
            if (dialog == null) dialog = new Window();
            dialog.Title = "Custom FPS";
            dialog.Content = root;
            dialog.Width = 340;
            dialog.SizeToContent = SizeToContent.Height;
            dialog.ResizeMode = ResizeMode.NoResize;
            dialog.ShowInTaskbar = false;
            dialog.Owner = Window.GetWindow(this);
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            double cap = 0;
            ok.Click += (_, __) =>
            {
                var text = (input.Text ?? "").Trim().Replace(',', '.');
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out cap) || cap <= 0 || cap > 1000)
                {
                    error.Visibility = Visibility.Visible;
                    input.Focus();
                    input.SelectAll();
                    return;
                }
                dialog.DialogResult = true;
            };
            input.TextChanged += (_, __) => error.Visibility = Visibility.Collapsed;
            dialog.Loaded += (_, __) => { input.Focus(); input.SelectAll(); };

            if (dialog.ShowDialog() != true) return;
            limiterService.SetGameLimit(games, Math.Round(cap, 4));
            RefreshFromService();
        }

        private void OnUseGlobalClick(object sender, RoutedEventArgs e)
        {
            limiterService.UseGlobalGameLimit(games);
            RefreshFromService();
        }

        private void OnDisableClick(object sender, RoutedEventArgs e)
        {
            limiterService.DisableGameLimit(games);
            RefreshFromService();
        }

        private void OnDontInjectClick(object sender, RoutedEventArgs e)
        {
            limiterService.SkipGameInjection(games);
            RefreshFromService();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            CloseRequested?.Invoke();
            Window.GetWindow(this)?.Close();
        }

        private static string FormatFps(double fps)
        {
            return fps.ToString("0.####", CultureInfo.InvariantCulture) + " FPS";
        }
        private bool loadingInjection;

        private void LoadInjectionRadios()
        {
            loadingInjection = true;
            try
            {
                var pref = GameSpecialKInjectionPreference.UseGlobal;
                if (games != null && games.Count > 0)
                {
                    var p = limiterService.GetGameProfile(games[0]);
                    if (p != null)
                        pref = p.SpecialKInjection;
                }
                // Per-game menu only exposes Global (follow extension) and Local.
                // Map legacy Selective / Global prefs back to UseGlobal.
                var useLocal = pref == GameSpecialKInjectionPreference.Local;
                InjUseGlobalRadio.IsChecked = !useLocal;
                InjLocalRadio.IsChecked = useLocal;

                var globalMode = limiterService.Settings.SpecialKInjectionMode;
                string modeLabel;
                switch (globalMode)
                {
                    case SpecialKInjectionMode.Local:
                        modeLabel = "Local";
                        break;
                    case SpecialKInjectionMode.Global:
                        modeLabel = "Global";
                        break;
                    default:
                        modeLabel = "Selective";
                        break;
                }
                InjUseGlobalRadio.Content = "Global";
                if (InjGlobalMethodHint != null)
                    InjGlobalMethodHint.Text = "Currently: " + modeLabel + " — follows the extension-wide injection mode.";
            }
            finally
            {
                loadingInjection = false;
            }
        }

        private bool loadingVrr;

        private void LoadVrrRadios()
        {
            loadingVrr = true;
            try
            {
                var pref = GameVrrPreference.UseGlobal;
                int hz = 0;
                double thr = 0;
                if (games != null && games.Count > 0)
                {
                    var p = limiterService.GetGameProfile(games[0]);
                    if (p != null)
                    {
                        pref = p.VrrPreference;
                        hz = p.VrrTargetHzOverride;
                        thr = p.VrrFpsThresholdOverride;
                    }
                }
                VrrUseGlobalRadio.IsChecked = pref == GameVrrPreference.UseGlobal;
                VrrEnabledRadio.IsChecked = pref == GameVrrPreference.Enabled;
                VrrDisabledRadio.IsChecked = pref == GameVrrPreference.Disabled;
                VrrTargetHzBox.Text = hz > 0 ? hz.ToString() : "";
                VrrThresholdBox.Text = thr > 0 ? thr.ToString("0.####", CultureInfo.InvariantCulture) : "";
            }
            finally { loadingVrr = false; }
        }

        private void OnVrrChanged(object sender, RoutedEventArgs e)
        {
            if (loadingVrr || games == null || games.Count == 0 || limiterService == null)
                return;
            GameVrrPreference pref;
            if (VrrEnabledRadio.IsChecked == true)
                pref = GameVrrPreference.Enabled;
            else if (VrrDisabledRadio.IsChecked == true)
                pref = GameVrrPreference.Disabled;
            else
                pref = GameVrrPreference.UseGlobal;

            int.TryParse(VrrTargetHzBox.Text, out var hz);
            double.TryParse(VrrThresholdBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var thr);
            limiterService.SetGameVrrPreference(games, pref, hz, thr);
        }

        private void OnVrrOverrideChanged(object sender, RoutedEventArgs e)
        {
            OnVrrChanged(sender, e);
        }

        private bool loadingForcedRefresh;

        private void LoadForcedRefreshCombo()
        {
            loadingForcedRefresh = true;
            try
            {
                var deviceName = games != null && games.Count > 0
                    ? limiterService.GetGameMonitorDeviceName(games[0]) : MonitorManager.PrimaryDeviceName;
                var rates = RefreshRateManager.GetSupportedRefreshRates(deviceName)
                    .OrderByDescending(r => r).ToList();
                ForcedRefreshCombo.Items.Clear();
                ForcedRefreshCombo.Items.Add(new ComboBoxItem { Content = "Default (no force)", Tag = 0 });
                var realHz = RefreshRateManager.GetCurrentPreciseRefreshRate(deviceName);
                var nominalHz = MonitorManager.GetRefreshRate(deviceName);
                foreach (var hz in rates)
                {
                    var text = hz.ToString(CultureInfo.InvariantCulture) + " Hz";
                    if (hz == nominalHz && Math.Abs(realHz - nominalHz) > 0.00005 &&
                        Math.Abs(realHz - nominalHz) < 1.0)
                    {
                        text = realHz.ToString("0.####", CultureInfo.InvariantCulture) +
                            " Hz (" + hz + " Hz mode, current)";
                    }
                    ForcedRefreshCombo.Items.Add(new ComboBoxItem { Content = text, Tag = hz });
                }

                var selected = 0;
                if (games != null && games.Count > 0)
                {
                    var p = limiterService.GetGameProfile(games[0]);
                    if (p != null && p.ForcedRefreshRateHz > 0)
                    {
                        for (int i = 1; i < ForcedRefreshCombo.Items.Count; i++)
                        {
                            var item = ForcedRefreshCombo.Items[i] as ComboBoxItem;
                            if (item != null && (int)item.Tag == p.ForcedRefreshRateHz)
                            {
                                selected = i;
                                break;
                            }
                        }
                    }
                }
                ForcedRefreshCombo.SelectedIndex = selected;

                var profile = games != null && games.Count > 0
                    ? limiterService.GetGameProfile(games[0]) : null;
                ExactSyncHzBox.Text = profile != null && profile.ExactSyncRefreshHz > 0
                    ? profile.ExactSyncRefreshHz.ToString("0.####", CultureInfo.InvariantCulture)
                    : "";
            }
            finally { loadingForcedRefresh = false; }
        }

        private void OnForcedRefreshChanged(object sender, SelectionChangedEventArgs e)
        {
            if (loadingForcedRefresh || games == null || games.Count == 0 || limiterService == null)
                return;
            var item = ForcedRefreshCombo.SelectedItem as ComboBoxItem;
            var hz = item != null && item.Tag is int ? (int)item.Tag : 0;
            limiterService.SetGameForcedRefreshRate(games, hz);
            RefreshFromService();
        }

        private void OnExactSyncHzChanged(object sender, RoutedEventArgs e)
        {
            if (loadingForcedRefresh || suppressEvents || games == null || games.Count == 0 || limiterService == null)
                return;
            var text = (ExactSyncHzBox.Text ?? "").Trim().Replace(',', '.');
            double hz;
            if (text.Length == 0) hz = 0;
            else if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out hz) ||
                     hz < 10 || hz > 1000 || double.IsNaN(hz) || double.IsInfinity(hz))
            {
                MessageBox.Show("Enter a refresh rate between 10 and 1000 Hz (e.g. 29.9001), or leave blank for automatic detection.",
                    "Exact refresh rate", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            limiterService.SetGameExactSyncRefreshRate(games, hz);
            RefreshFromService();
        }

        private bool loadingSkLimiter;

        private void LoadSkLimiterRadios()
        {
            loadingSkLimiter = true;
            try
            {
                var useGlobal = true;
                var mode = SpecialKFrameLimiterMode.Normal;
                if (games != null && games.Count > 0)
                {
                    var p = limiterService.GetGameProfile(games[0]);
                    if (p != null && p.HasSpecialKLimiterModeOverride)
                    {
                        useGlobal = false;
                        mode = p.SpecialKLimiterMode;
                    }
                }
                SkLimiterUseGlobalRadio.Content = "Use global (" +
                    SpecialKFrameLimiterModeNames.GetDisplayName(limiterService.Settings.SpecialKFrameLimiterMode) + ")";
                SkLimiterUseGlobalRadio.IsChecked = useGlobal;
                SkLimiterNormalRadio.IsChecked = !useGlobal && mode == SpecialKFrameLimiterMode.Normal;
                SkLimiterLowLatRadio.IsChecked = !useGlobal && mode == SpecialKFrameLimiterMode.LowLatency;
                SkLimiterLatentRadio.IsChecked = !useGlobal && mode == SpecialKFrameLimiterMode.LatentSync;
                SkLimiterReflexRadio.IsChecked = !useGlobal && mode == SpecialKFrameLimiterMode.NvidiaReflex;
            }
            finally { loadingSkLimiter = false; }
        }

        private void OnSkLimiterModeChanged(object sender, RoutedEventArgs e)
        {
            if (loadingSkLimiter || games == null || games.Count == 0 || limiterService == null)
                return;
            if (SkLimiterUseGlobalRadio.IsChecked == true)
            {
                limiterService.SetGameSpecialKLimiterMode(games, SpecialKFrameLimiterMode.Normal, useGlobal: true);
                foreach (var game in games) limiterService.ApplySpecialKCompatibilityToGamePublic(game);
                RefreshFromService();
                return;
            }
            SpecialKFrameLimiterMode mode;
            if (SkLimiterLowLatRadio.IsChecked == true)
                mode = SpecialKFrameLimiterMode.LowLatency;
            else if (SkLimiterLatentRadio.IsChecked == true)
                mode = SpecialKFrameLimiterMode.LatentSync;
            else if (SkLimiterReflexRadio.IsChecked == true)
                mode = SpecialKFrameLimiterMode.NvidiaReflex;
            else
                mode = SpecialKFrameLimiterMode.Normal;
            limiterService.SetGameSpecialKLimiterMode(games, mode, useGlobal: false);
            foreach (var game in games) limiterService.ApplySpecialKCompatibilityToGamePublic(game);
            RefreshFromService();
        }

        private bool loadingSkLatencyExtra;

        private void LoadSkLatencyExtraRadios()
        {
            loadingSkLatencyExtra = true;
            try
            {
                var p = (games != null && games.Count > 0) ? limiterService.GetGameProfile(games[0]) : null;
                var g = limiterService.Settings;

                // Reflex mode
                if (SkReflexUseGlobalRadio != null)
                {
                    var useG = p == null || !p.HasSpecialKReflexModeOverride;
                    var mode = useG ? g.SpecialKReflexMode : p.SpecialKReflexModeOverride;
                    SkReflexUseGlobalRadio.IsChecked = useG;
                    SkReflexOffRadio.IsChecked = !useG && mode == SpecialKReflexMode.Off;
                    SkReflexLowLatRadio.IsChecked = !useG && mode == SpecialKReflexMode.LowLatency;
                    SkReflexBoostRadio.IsChecked = !useG && mode == SpecialKReflexMode.LowLatencyBoost;
                }

                // Reflex engagement
                if (SkReflexEngUseGlobalRadio != null)
                {
                    var useG = p == null || !p.HasSpecialKReflexTriggerOverride;
                    var trig = useG ? g.SpecialKReflexTrigger : p.SpecialKReflexTriggerOverride;
                    SkReflexEngUseGlobalRadio.IsChecked = useG;
                    SkReflexEngEndRadio.IsChecked = !useG && trig == SpecialKReflexTrigger.EndOfFrame;
                    SkReflexEngStartRadio.IsChecked = !useG && trig == SpecialKReflexTrigger.StartOfFrame;
                }

                // Energy / frame pacing
                if (SkEnergyUseGlobalRadio != null)
                {
                    var useG = p == null || !p.HasSpecialKEnergyOverride;
                    var energy = useG ? g.SpecialKEnergyEfficiency : p.SpecialKEnergyOverride;
                    SkEnergyUseGlobalRadio.IsChecked = useG;
                    SkEnergyDefaultRadio.IsChecked = !useG && energy == SpecialKEnergyEfficiency.Default;
                    SkEnergyPowerRadio.IsChecked = !useG && energy == SpecialKEnergyEfficiency.PowerSaving;
                    SkEnergyMaxRadio.IsChecked = !useG && energy == SpecialKEnergyEfficiency.MaxAccuracy;
                    SkEnergyCustomRadio.IsChecked = !useG && energy == SpecialKEnergyEfficiency.Custom;

                    var pct = useG ? g.SpecialKBusyWaitPercent : p.SpecialKBusyWaitPercentOverride;
                    var bias = useG ? g.SpecialKBusyWaitBias : p.SpecialKBusyWaitBiasOverride;
                    if (SkBusyWaitPercentBox != null)
                        SkBusyWaitPercentBox.Text = pct.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                    if (SkBusyWaitBiasBox != null)
                        SkBusyWaitBiasBox.Text = bias.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                    var custom = !useG && energy == SpecialKEnergyEfficiency.Custom;
                    if (SkBusyWaitPercentBox != null) SkBusyWaitPercentBox.IsEnabled = custom;
                    if (SkBusyWaitBiasBox != null) SkBusyWaitBiasBox.IsEnabled = custom;
                }
            }
            finally { loadingSkLatencyExtra = false; }
        }

        private void OnSkReflexModeChanged(object sender, RoutedEventArgs e)
        {
            if (loadingSkLatencyExtra || games == null || games.Count == 0 || limiterService == null) return;
            if (SkReflexUseGlobalRadio.IsChecked == true)
            {
                limiterService.SetGameSpecialKReflexMode(games, SpecialKReflexMode.Off, useGlobal: true);
            }
            else
            {
                SpecialKReflexMode mode;
                if (SkReflexOffRadio.IsChecked == true) mode = SpecialKReflexMode.Off;
                else if (SkReflexBoostRadio.IsChecked == true) mode = SpecialKReflexMode.LowLatencyBoost;
                else mode = SpecialKReflexMode.LowLatency;
                limiterService.SetGameSpecialKReflexMode(games, mode, useGlobal: false);
            }
            foreach (var game in games) limiterService.ApplySpecialKCompatibilityToGamePublic(game);
            RefreshFromService();
        }

        private void OnSkReflexEngChanged(object sender, RoutedEventArgs e)
        {
            if (loadingSkLatencyExtra || games == null || games.Count == 0 || limiterService == null) return;
            if (SkReflexEngUseGlobalRadio.IsChecked == true)
            {
                limiterService.SetGameSpecialKReflexTrigger(games, SpecialKReflexTrigger.EndOfFrame, useGlobal: true);
            }
            else
            {
                var trig = SkReflexEngStartRadio.IsChecked == true
                    ? SpecialKReflexTrigger.StartOfFrame
                    : SpecialKReflexTrigger.EndOfFrame;
                limiterService.SetGameSpecialKReflexTrigger(games, trig, useGlobal: false);
            }
            foreach (var game in games) limiterService.ApplySpecialKCompatibilityToGamePublic(game);
            RefreshFromService();
        }

        private void OnSkEnergyChanged(object sender, RoutedEventArgs e)
        {
            if (loadingSkLatencyExtra || games == null || games.Count == 0 || limiterService == null) return;
            if (SkEnergyUseGlobalRadio.IsChecked == true)
            {
                limiterService.SetGameSpecialKEnergy(games, SpecialKEnergyEfficiency.Default, useGlobal: true);
            }
            else
            {
                SpecialKEnergyEfficiency energy;
                if (SkEnergyPowerRadio.IsChecked == true) energy = SpecialKEnergyEfficiency.PowerSaving;
                else if (SkEnergyMaxRadio.IsChecked == true) energy = SpecialKEnergyEfficiency.MaxAccuracy;
                else if (SkEnergyCustomRadio.IsChecked == true) energy = SpecialKEnergyEfficiency.Custom;
                else energy = SpecialKEnergyEfficiency.Default;

                double? pct = null, bias = null;
                if (energy == SpecialKEnergyEfficiency.Custom)
                {
                    if (double.TryParse(SkBusyWaitPercentBox?.Text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var pVal)) pct = pVal;
                    if (double.TryParse(SkBusyWaitBiasBox?.Text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var bVal)) bias = bVal;
                }
                limiterService.SetGameSpecialKEnergy(games, energy, useGlobal: false, pct, bias);
            }
            foreach (var game in games) limiterService.ApplySpecialKCompatibilityToGamePublic(game);
            RefreshFromService();
        }

        private void OnSkBusyWaitChanged(object sender, RoutedEventArgs e)
        {
            if (loadingSkLatencyExtra || games == null || games.Count == 0 || limiterService == null) return;
            if (SkEnergyCustomRadio?.IsChecked != true) return;
            double? pct = null, bias = null;
            if (double.TryParse(SkBusyWaitPercentBox?.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var pVal)) pct = pVal;
            if (double.TryParse(SkBusyWaitBiasBox?.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var bVal)) bias = bVal;
            limiterService.SetGameSpecialKEnergy(games, SpecialKEnergyEfficiency.Custom, useGlobal: false, pct, bias);
            foreach (var game in games) limiterService.ApplySpecialKCompatibilityToGamePublic(game);
        }

        private void OnInjectionChanged(object sender, RoutedEventArgs e)
        {
            if (loadingInjection || games == null || games.Count == 0 || limiterService == null)
                return;
            var pref = InjLocalRadio.IsChecked == true
                ? GameSpecialKInjectionPreference.Local
                : GameSpecialKInjectionPreference.UseGlobal;
            limiterService.SetGameSpecialKInjection(games, pref);

            // Picking Local must put the wrapper DLL + ini into the game folder right away (not only at the next launch);
            // picking "use global" takes an installed wrapper back again.
            var previousCursor = Mouse.OverrideCursor;
            Mouse.OverrideCursor = Cursors.Wait;
            string report = null;
            var anyFailed = false;
            try
            {
                foreach (var game in games)
                {
                    var msg = limiterService.ApplyLocalInjectionChoice(game, out var failed);
                    if (failed) anyFailed = true;
                    if (!string.IsNullOrWhiteSpace(msg))
                        report = report == null ? msg : report + Environment.NewLine + msg;
                }
            }
            finally
            {
                Mouse.OverrideCursor = previousCursor;
            }

            if (!string.IsNullOrWhiteSpace(report))
            {
                if (InjGlobalMethodHint != null) InjGlobalMethodHint.Text = report;
                if (anyFailed)
                    MessageBox.Show("Special K local injection could not be set up:\n\n" + report,
                        "FPS Limiter 3000", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

    }
}
