using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FPSLimiter
{
    public partial class GameSettingsWindow : Window
    {
        private readonly LimiterService limiterService;
        private readonly List<Game> games;
        private bool suppressEvents;

        public GameSettingsWindow(LimiterService limiterService, List<Game> games)
        {
            InitializeComponent();
            this.limiterService = limiterService;
            this.games = games ?? new List<Game>();

            var title = games.Count == 1
                ? games[0].Name
                : games.Count + " games selected";
            Title = "FPS Limiter 3000 - " + title;
            GameTitleText.Text = title;

            suppressEvents = true;
            BuildPresets();
            PopulateSkModes();
            PopulateSyncModes();
            RefreshFromService();
            suppressEvents = false;
        }

        private void BuildPresets()
        {
            PresetList.Items.Clear();
            foreach (var preset in (games != null && games.Count > 0 ? limiterService.GetPresets(games[0]) : limiterService.GetPresets()))
            {
                var fps = preset;
                var btn = new Button
                {
                    Content = FormatFps(fps),
                    Margin = new Thickness(3),
                    Padding = new Thickness(8, 10, 8, 10),
                    MinHeight = 36,
                    Tag = fps,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                btn.Click += (_, __) =>
                {
                    limiterService.SetGameLimit(games, fps);
                    RefreshFromService();
                };
                PresetList.Items.Add(btn);
            }
        }

        private void PopulateSkModes()
        {
            SkModeCombo.Items.Clear();
            SkModeCombo.Items.Add(new ComboBoxItem { Content = "Use global", Tag = null });
            foreach (SpecialKFrameLimiterMode mode in Enum.GetValues(typeof(SpecialKFrameLimiterMode)))
            {
                SkModeCombo.Items.Add(new ComboBoxItem
                {
                    Content = SpecialKFrameLimiterModeNames.GetDisplayName(mode),
                    Tag = mode
                });
            }
        }

        private void PopulateSyncModes()
        {
            SyncModeCombo.Items.Clear();
            foreach (FpsSyncMode mode in Enum.GetValues(typeof(FpsSyncMode)))
            {
                if (mode == FpsSyncMode.UseGlobal)
                {
                    SyncModeCombo.Items.Add(new ComboBoxItem
                    {
                        Content = FpsSyncModeNames.GetMethodName(mode),
                        Tag = mode,
                        ToolTip = FpsSyncModeNames.GetDisplayName(mode)
                    });
                    continue;
                }

                SyncModeCombo.Items.Add(new ComboBoxItem
                {
                    Content = FpsSyncModeNames.GetMethodName(mode),
                    Tag = mode,
                    ToolTip = FpsSyncModeNames.GetDisplayName(mode)
                });
            }
        }

        private void RefreshFromService()
        {
            if (games.Count == 0)
            {
                return;
            }

            var game = games[0];
            var useSk = limiterService.ShouldUseSpecialKBackend(game);
            UseSpecialKToggle.IsChecked = useSk;
            BackendStatusText.Text = useSk ? "Current method: Special K" : "Current method: RTSS";

            var profile = limiterService.GetGameProfile(game);
            var silent = profile != null && profile.HasSteamLogSilentOverride
                ? profile.SpecialKSteamLogSilent
                : limiterService.Settings.SpecialKSteamLogSilent;
            var previousSuppress = suppressEvents;
            suppressEvents = true;
            SteamSilentToggle.IsChecked = silent;
            suppressEvents = previousSuppress;

            var limit = limiterService.GetEffectiveLimit(game);
            if (limit.HasValue)
            {
                CurrentFpsText.Text = "Current: " + FormatFps(limit.Value);
                CurrentFpsText.Opacity = 1.0;
            }
            else
            {
                CurrentFpsText.Text = "Current: none (global / disabled)";
                CurrentFpsText.Opacity = 0.75;
            }

            foreach (Button btn in PresetList.Items.OfType<Button>())
            {
                var fps = (double)btn.Tag;
                var active = limit.HasValue && Math.Abs(limit.Value - fps) < 0.01;
                if (active)
                {
                    btn.Content = "✓ " + FormatFps(fps);
                    btn.FontWeight = FontWeights.Bold;
                    btn.Opacity = 1.0;
                    btn.BorderThickness = new Thickness(2);
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
                }
                else
                {
                    btn.Content = FormatFps(fps);
                    btn.FontWeight = FontWeights.Normal;
                    btn.Opacity = 0.9;
                    btn.BorderThickness = new Thickness(1);
                    btn.ClearValue(Border.BorderBrushProperty);
                }
            }

            if (profile != null && profile.HasSpecialKLimiterModeOverride)
            {
                SelectComboTag(SkModeCombo, profile.SpecialKLimiterMode);
            }
            else
            {
                SelectComboTag(SkModeCombo, null);
            }

            UpdateLocalInjectionStatus();

            var modeLimit = profile?.GetMode(limiterService.CurrentMode);
            var sync = modeLimit?.SyncMode ?? FpsSyncMode.UseGlobal;
            SelectComboTag(SyncModeCombo, sync);
            SyncModeCombo.ToolTip = FpsSyncModeNames.GetDisplayName(sync);
        }

        private void UpdateLocalInjectionStatus()
        {
            if (LocalInjectionStatusText == null || games.Count == 0) return;
            try
            {
                var has = limiterService.HasLocalSpecialKInjection(games[0]);
                LocalInjectionStatusText.Text = has
                    ? "Local Special K DLL is present in the game folder."
                    : "No local Special K DLL detected.";
            }
            catch
            {
                LocalInjectionStatusText.Text = "Could not check local injection status.";
            }
        }

        private static void SelectComboTag(ComboBox combo, object tag)
        {
            for (var i = 0; i < combo.Items.Count; i++)
            {
                var item = combo.Items[i] as ComboBoxItem;
                if (item == null)
                {
                    continue;
                }

                if (tag == null && item.Tag == null)
                {
                    combo.SelectedIndex = i;
                    return;
                }

                if (tag != null && item.Tag != null && tag.Equals(item.Tag))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }

            if (combo.Items.Count > 0)
            {
                combo.SelectedIndex = 0;
            }
        }

        private void OnUseSpecialKChanged(object sender, RoutedEventArgs e)
        {
            if (suppressEvents)
            {
                return;
            }

            var on = UseSpecialKToggle.IsChecked == true;
            foreach (var game in games)
            {
                var profile = limiterService.Settings.GetOrCreateGameProfile(game.Id);
                profile.Backend = on ? GameBackendPreference.SpecialK : GameBackendPreference.Rtss;
            }

            limiterService.SaveSettingsPublic();
            foreach (var game in games)
            {
                limiterService.ApplySteamLogSilentToGamePublic(game, silent);
                limiterService.ReapplyIfRunningPublic(game);
            }

            BackendStatusText.Text = on ? "Current method: Special K" : "Current method: RTSS";
        }

        private void OnSteamSilentChanged(object sender, RoutedEventArgs e)
        {
            if (suppressEvents)
            {
                return;
            }

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
                limiterService.ReapplyIfRunningPublic(game);
            }
        }

        private void OnSkModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressEvents || SkModeCombo.SelectedItem == null)
            {
                return;
            }

            var item = SkModeCombo.SelectedItem as ComboBoxItem;
            if (item == null)
            {
                return;
            }

            if (item.Tag == null)
            {
                limiterService.SetGameSpecialKLimiterMode(games, SpecialKFrameLimiterMode.Normal, useGlobal: true);
            }
            else
            {
                limiterService.SetGameSpecialKLimiterMode(games, (SpecialKFrameLimiterMode)item.Tag, useGlobal: false);
            }
        }

        private void OnLocalInjectClick(object sender, RoutedEventArgs e)
        {
            if (suppressEvents || games.Count == 0) return;
            try
            {
                foreach (var game in games)
                {
                    var dest = limiterService.InstallLocalSpecialKInjection(game);
                    MessageBox.Show($"Installed local Special K:\n{dest}", "FPS Limiter 3000");
                }
                UpdateLocalInjectionStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Inject failed:\n" + ex.Message, "FPS Limiter 3000");
            }
        }

        private void OnLocalRemoveClick(object sender, RoutedEventArgs e)
        {
            if (suppressEvents || games.Count == 0) return;
            try
            {
                var total = 0;
                foreach (var game in games)
                    total += limiterService.RemoveLocalSpecialKInjection(game);

                MessageBox.Show(total > 0
                    ? $"Removed {total} Special K wrapper DLL(s)."
                    : "No Special K wrapper DLLs found to remove.", "FPS Limiter 3000");
                UpdateLocalInjectionStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Remove failed:\n" + ex.Message, "FPS Limiter 3000");
            }
        }

        private void OnSyncModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressEvents || SyncModeCombo.SelectedItem == null)
            {
                return;
            }

            var item = SyncModeCombo.SelectedItem as ComboBoxItem;
            if (item?.Tag is FpsSyncMode mode)
            {
                SyncModeCombo.ToolTip = FpsSyncModeNames.GetDisplayName(mode);
                limiterService.SetGameSyncMode(games, mode);
            }
        }

        private void OnCurrentFpsMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return;
            e.Handled = true;
            OnCustomClick(sender, e);
        }

        private void OnCustomClick(object sender, RoutedEventArgs e)
        {
            var input = new TextBox { Text = LimitUnits.FormatNumber(60), Width = 120, Margin = new Thickness(0, 8, 0, 0) };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = LimitUnits.IsFrametime ? "Enter frametime cap in ms (e.g. 16.67 for 60 FPS):" : "Enter FPS cap (e.g. 60 or 59.9):" });
            panel.Children.Add(input);
            var dialog = new Window
            {
                Title = LimitUnits.IsFrametime ? "Custom frametime" : "Custom FPS",
                Content = panel,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };
            var ok = new Button { Content = "OK", IsDefault = true, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
            ok.Click += (_, __) => dialog.DialogResult = true;
            panel.Children.Add(ok);
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            if (!LimitUnits.TryParseLimit(input.Text, false, out var cap) || cap > 1000)
            {
                MessageBox.Show("Enter a number between 1 and 1000.", "FPS Limiter 3000");
                return;
            }

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

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private static string FormatFps(double fps)
        {
            return LimitUnits.Format(fps);
        }
    }
}
