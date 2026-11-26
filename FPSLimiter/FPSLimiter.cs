using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FPSLimiter
{
    public class FPSLimiter : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string ActiveMenuPrefix = "\u2713 ";
        // Used for the Global sync mode menu, which has no parent to fall back to.
        private static readonly FpsSyncMode[] SyncModes =
        {
            FpsSyncMode.Async,
            FpsSyncMode.FrontEdgeSync,
            FpsSyncMode.BackEdgeSync,
            FpsSyncMode.ReflexSync,
            FpsSyncMode.ScanlineSync,
            FpsSyncMode.ScanlineSyncHalf
        };

        // Used for per-game sync mode menus (game context menu, top bar button), which can also
        // track whichever Global sync mode is currently active instead of a fixed choice.
        private static readonly FpsSyncMode[] GameSyncModes =
        {
            FpsSyncMode.UseGlobal,
            FpsSyncMode.Async,
            FpsSyncMode.FrontEdgeSync,
            FpsSyncMode.BackEdgeSync,
            FpsSyncMode.ReflexSync,
            FpsSyncMode.ScanlineSync,
            FpsSyncMode.ScanlineSyncHalf
        };

        // Used for the Global Reflex marker injection menu, which has no parent to fall back to.
        private static readonly ReflexMarkerMode[] ReflexMarkerModes =
        {
            ReflexMarkerMode.Enabled,
            ReflexMarkerMode.Disabled
        };

        // Used for per-game Reflex marker injection menus, which can also track whichever Global
        // setting is currently active instead of a fixed choice.
        private static readonly ReflexMarkerMode[] GameReflexMarkerModes =
        {
            ReflexMarkerMode.UseGlobal,
            ReflexMarkerMode.Enabled,
            ReflexMarkerMode.Disabled
        };

        private FPSLimiterSettingsViewModel settings;
        private LimiterService limiterService;
        private SpecialKUpdateService specialKUpdateService;
        private ExtensionUpdateService extensionUpdateService;
        private HotkeyManager hotkeyManager;
        private System.Windows.Threading.DispatcherTimer monitorTimer;


        // Older versions kept a recovery copy of config.json outside the extension's data folder and
        // silently restored it after a reinstall, so presets and hotkeys survived an uninstall. That copy is
        // no longer written; remove any left over so a clean install starts from the default settings.
        private string LegacyRecoveryDirectory => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PlayniteExtensionSettingsRecovery", Id.ToString("D"));

        private void RecoverSettingsIfNeeded()
        {
            try
            {
                if (Directory.Exists(LegacyRecoveryDirectory))
                    Directory.Delete(LegacyRecoveryDirectory, true);
            }
            catch (Exception ex) { logger.Warn("Could not remove the old settings recovery copy: " + ex.Message); }
        }

        // Kept as a no-op so existing call sites stay valid; settings are no longer copied outside the data folder.
        private void PreserveCurrentSettings() { }

        public void SaveSettingsSafely(FPSLimiterSettings value)
        {
            SavePluginSettings(value);
        }

        public override Guid Id { get; } = Guid.Parse("08c6a58f-461c-4629-850c-d07a820e26c1");

        public FPSLimiter(IPlayniteAPI api) : base(api)
        {
            RecoverSettingsIfNeeded();
            settings = new FPSLimiterSettingsViewModel(this, api);
            limiterService = new LimiterService(api, settings);
            specialKUpdateService = new SpecialKUpdateService(settings, limiterService, GetPluginUserDataPath(), api);
            extensionUpdateService = new ExtensionUpdateService(settings, limiterService, api);
            // Seed Global\default_SpecialK.ini on load so toggles are ready before first launch.
            try { limiterService.SyncSpecialKGlobalDefaults(); } catch { }
            try { BrandIcons.EnsureExtracted(GetPluginUserDataPath()); } catch { try { BrandIcons.EnsureExtracted(); } catch { } }

            Properties = new GenericPluginProperties
            {
                HasSettings = true
            };

            // Keep the top-bar icon in sync with the selected game's effective backend.
            try
            {
                settings.Settings.PropertyChanged += (s, e) =>
                {
                    if (e == null) return;
                    if (e.PropertyName == nameof(FPSLimiterSettings.UseSpecialKBackend) ||
                        e.PropertyName == nameof(FPSLimiterSettings.DontUseSpecialKForVulkanGames) ||
                        e.PropertyName == nameof(FPSLimiterSettings.ShowTopBarIcon))
                    {
                        if (e.PropertyName == nameof(FPSLimiterSettings.DontUseSpecialKForVulkanGames))
                        {
                            limiterService.InvalidateVulkanDetectionCache();
                        }

                        PlayniteApi.MainView?.UIDispatcher?.Invoke(() =>
                        {
                            ApplyTopBarIcon();
                        });
                    }
                };
            }
            catch { /* UIDispatcher may not exist yet during construction */ }
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            Task.Run(async () =>
            {
                try
                {
                    await specialKUpdateService.CheckIfDueAsync();
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Scheduled Special K update check failed.");
                }
            });

            Task.Run(async () =>
            {
                try
                {
                    await extensionUpdateService.CheckIfDueAsync();
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Scheduled FPS Limiter 3000 update check failed.");
                }
            });

            Task.Run(() => limiterService.EnsureRtssGlobalDefaults());

            try { limiterService.InitDisplayModeRecovery(Path.Combine(GetPluginUserDataPath(), "displaymode-restore.txt")); }
            catch (Exception ex) { logger.Warn("Display-mode recovery setup failed: " + ex.Message); }
            try { limiterService.InitFreeSyncRecovery(Path.Combine(GetPluginUserDataPath(), "freesync-restore.txt")); }
            catch (Exception ex) { logger.Warn("FreeSync recovery setup failed: " + ex.Message); }

            if (settings.Settings.ActiveSessions.Any())
            {
                limiterService.RestoreAllActiveSessions(true);
            }

            InitializeHotkeys();
            try
            {
                InputManager.Current.PreProcessInput += OnPreProcessInput;
            }
            catch (Exception ex)
            {
                logger.Warn("Could not register Playnite settings shortcuts: " + ex.Message);
            }
            monitorTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            monitorTimer.Tick += (sender, e) => limiterService.UpdateRunningGameMonitors();
            monitorTimer.Start();

            // Top-bar icon is static (extension default backend); no per-game switching.
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            monitorTimer?.Stop();
            try { InputManager.Current.PreProcessInput -= OnPreProcessInput; } catch { }
            PreserveCurrentSettings();
            limiterService.RestoreAllActiveSessions(false);
            limiterService.RestoreAllFreeSync();
            limiterService.RestoreAllDisplayModes();
            hotkeyManager?.Dispose();
        }

        private bool gameSettingsOpen;
        private Window openGameSettingsWindow;
        private Window openGlobalSettingsWindow;

        private void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
        {
            var keyEvent = e?.StagingItem?.Input as KeyEventArgs;
            if (keyEvent == null) return;
            // Playnite Fullscreen turns controller buttons into synthetic key events, and unmapped buttons
            // arrive as Key.None. Reading IsDown on those makes WPF validate the key and throw
            // InvalidEnumArgumentException, which Playnite reports as a crash. Ignore them.
            try
            {
                var rawKey = keyEvent.Key == Key.System ? keyEvent.SystemKey : keyEvent.Key;
                if (rawKey == Key.None) return;
                if (!keyEvent.IsDown || keyEvent.IsRepeat) return;
            }
            catch { return; }
            var focused = Keyboard.FocusedElement;
            var focusedElement = focused as FrameworkElement;
            // Let hotkey-capture boxes receive their own key presses. For normal controls,
            // still allow modified shortcuts (Ctrl/Alt/Win) and function keys to toggle the window.
            if (focusedElement != null &&
                (focusedElement.Name == "PlayniteMenuHotkeyCapture" ||
                 focusedElement.Name == "GlobalSettingsHotkeyCapture")) return;
            if (focused is TextBoxBase || focused is PasswordBox || focused is ComboBox)
            {
                var modifiers = Keyboard.Modifiers;
                var key = keyEvent.Key == Key.System ? keyEvent.SystemKey : keyEvent.Key;
                bool modified = (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0;
                bool functionKey = (int)key >= (int)Key.F1 && (int)key <= (int)Key.F24;
                if (!modified && !functionKey) return;
            }

            var globalHotkey = settings?.Settings?.GlobalSettingsHotkey;
            if (!string.IsNullOrWhiteSpace(globalHotkey) &&
                !string.Equals(globalHotkey, "None", StringComparison.OrdinalIgnoreCase) &&
                PlayniteHotkey.Matches(globalHotkey, keyEvent))
            {
                keyEvent.Handled = true;
                try
                {
                    if (openGlobalSettingsWindow != null && openGlobalSettingsWindow.IsActive)
                        openGlobalSettingsWindow.Close();
                    else
                        OpenGlobalSettingsWindow();
                }
                catch (Exception ex) { logger.Warn("FPS Limiter: global settings shortcut failed: " + ex.Message); }
                return;
            }

            var configured = settings?.Settings?.PlayniteMenuHotkey;
            if (string.IsNullOrWhiteSpace(configured) || string.Equals(configured, "None", StringComparison.OrdinalIgnoreCase)) return;
            if (!PlayniteHotkey.Matches(configured, keyEvent)) return;
            if (openGameSettingsWindow != null && openGameSettingsWindow.IsActive)
            {
                keyEvent.Handled = true;
                try { openGameSettingsWindow.Close(); }
                catch (Exception ex) { logger.Warn("FPS Limiter: closing game settings shortcut failed: " + ex.Message); }
                return;
            }
            if (gameSettingsOpen && openGameSettingsWindow == null)
            {
                keyEvent.Handled = true;
                gameSettingsOpen = false;
                return;
            }
            if (PlayniteApi.MainView.SelectedGames?.FirstOrDefault() == null) return;
            keyEvent.Handled = true;
            try { OpenGameSettingsWindow(GetSelectedOrRunningGames()); }
            catch (Exception ex) { logger.Warn("FPS Limiter: per-game settings shortcut failed: " + ex.Message); }
        }

        private static void CenterOnWorkArea(Window window)
        {
            if (window == null) return;
            try
            {
                var work = SystemParameters.WorkArea;
                window.Left = work.Left + Math.Max(0, (work.Width - window.ActualWidth) / 2);
                window.Top = work.Top + Math.Max(0, (work.Height - window.ActualHeight) / 2);
            }
            catch { }
        }

        public void OpenGlobalSettingsWindow()
        {
            if (openGlobalSettingsWindow != null)
            {
                try
                {
                    if (openGlobalSettingsWindow.IsVisible)
                    {
                        openGlobalSettingsWindow.Activate();
                        return;
                    }
                }
                catch { }
                openGlobalSettingsWindow = null;
            }

            var fullscreen = PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Fullscreen;
            settings.BeginEdit();
            Window window = null;
            System.ComponentModel.PropertyChangedEventHandler themeChanged = null;
            FPSLimiterSettings appearanceSettings = settings.Settings;
            try
            {
                window = PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = true
                });
                var shownWindow = window;
                shownWindow.Title = "FPS Limiter 3000 — Settings";
                shownWindow.Width = 760;
                shownWindow.Height = 820;
                shownWindow.MinWidth = 620;
                shownWindow.MinHeight = 520;
                shownWindow.ResizeMode = ResizeMode.CanResize;
                AppearancePalette.Apply(shownWindow, settings.Settings.ThemePreset, settings.Settings.GlassEffect);
                shownWindow.SetResourceReference(Control.BackgroundProperty, "ReShadeBg");
                themeChanged = (sender, e) =>
                {
                    if (string.IsNullOrEmpty(e.PropertyName) ||
                        e.PropertyName == nameof(FPSLimiterSettings.ThemePreset) ||
                        e.PropertyName == nameof(FPSLimiterSettings.GlassEffect))
                        AppearancePalette.Apply(shownWindow, settings.Settings.ThemePreset, settings.Settings.GlassEffect);
                };
                appearanceSettings.PropertyChanged += themeChanged;
                shownWindow.AllowsTransparency = false;
                var dock = new DockPanel();
                var buttons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(10)
                };
                var save = new Button { Content = "Save", MinWidth = 90, Margin = new Thickness(4) };
                var cancel = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(4) };
                var finished = false;
                Action saveAndClose = () =>
                {
                    if (finished) return;
                    finished = true;
                    settings.EndEdit();
                    shownWindow.DialogResult = true;
                    shownWindow.Close();
                };
                save.Click += (s, e) => saveAndClose();
                cancel.Click += (s, e) =>
                {
                    if (finished) return;
                    finished = true;
                    settings.CancelEdit();
                    shownWindow.DialogResult = false;
                    shownWindow.Close();
                };
                buttons.Children.Add(save);
                buttons.Children.Add(cancel);
                DockPanel.SetDock(buttons, Dock.Bottom);
                dock.Children.Add(buttons);
                var settingsView = new global::FPSLimiterSettingsUI.FPSLimiterSettingsView { DataContext = settings };
                var settingsButtonStyle = settingsView.TryFindResource(typeof(Button)) as Style;
                if (settingsButtonStyle != null)
                {
                    save.Style = settingsButtonStyle;
                    save.SetResourceReference(Control.BackgroundProperty, "UiPrimary");
                    save.SetResourceReference(Control.ForegroundProperty, "UiPrimaryText");
                    cancel.Style = settingsButtonStyle;
                }
                dock.Children.Add(settingsView);
                shownWindow.Content = dock;

                // Same window in Desktop and Fullscreen: the layout, size and buttons are identical. The only
                // Fullscreen-specific part is input. A / B arrive as Playnite controller events (Key.None), which
                // only exist in Fullscreen, so this handler never fires on Desktop. A activates the focused
                // control; B goes back and keeps the changes, like Playnite's own Fullscreen settings.
                shownWindow.KeyDown += (s, e) => FullscreenController.HandleControllerKey(e, saveAndClose);
                if (fullscreen)
                {
                    // D-pad arrives as real arrow keys; combo boxes / sliders would swallow them. Controller
                    // starting focus: Fullscreen opens dialogs with nothing focused.
                    shownWindow.PreviewKeyDown += (s, e) => FullscreenController.TryMoveFocusFromArrow(e);
                    FullscreenController.FocusFirst(settingsView, shownWindow);
                }

                shownWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                shownWindow.Loaded += (_, __) => CenterOnWorkArea(shownWindow);
                openGlobalSettingsWindow = shownWindow;
                shownWindow.Closed += (s, e) =>
                {
                    if (ReferenceEquals(openGlobalSettingsWindow, shownWindow)) openGlobalSettingsWindow = null;
                };
                shownWindow.ShowDialog();
                if (shownWindow.DialogResult != true) settings.CancelEdit();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Could not open the FPS Limiter global settings window.");
                try { settings.CancelEdit(); } catch { }
                openGlobalSettingsWindow = null;
                try
                {
                    PlayniteApi.Dialogs.ShowErrorMessage(
                        "Could not open global settings:\n" + ex.Message,
                        "FPS Limiter 3000");
                }
                catch { }
            }
            finally
            {
                if (themeChanged != null) appearanceSettings.PropertyChanged -= themeChanged;
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
        private static extern IntPtr ForegroundWindowForHotkeys();
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
        private static extern uint GetWindowThreadProcessIdForHotkeys(IntPtr hwnd, out uint pid);

        private void InitializeHotkeys()
        {
            try
            {
                hotkeyManager = new HotkeyManager();
                hotkeyManager.IsAllowedForeground = () =>
                {
                    IntPtr hwnd = ForegroundWindowForHotkeys();
                    if (hwnd == IntPtr.Zero) return false;
                    GetWindowThreadProcessIdForHotkeys(hwnd, out uint pid);
                    return pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id ||
                        limiterService.IsForegroundTrackedGame((int)pid);
                };
                hotkeyManager.IsGameSessionActive = () => limiterService.HasRunningGames;
                hotkeyManager.HotkeyPressed += OnHotkeyPressed;

                var window = Application.Current?.MainWindow;
                if (window == null)
                {
                    if (Application.Current != null)
                    {
                        Application.Current.Activated += OnApplicationActivatedForHotkeys;
                    }

                    return;
                }

                if (window.IsLoaded)
                {
                    AttachHotkeys(window);
                }
                else
                {
                    window.SourceInitialized += (s, e) => AttachHotkeys(window);
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "FPS Limiter could not initialize hotkeys.");
            }
        }

        private void OnApplicationActivatedForHotkeys(object sender, EventArgs e)
        {
            if (Application.Current != null)
            {
                Application.Current.Activated -= OnApplicationActivatedForHotkeys;
            }

            var window = Application.Current?.MainWindow;
            if (window != null)
            {
                AttachHotkeys(window);
            }
        }

        private void AttachHotkeys(Window window)
        {
            if (hotkeyManager != null && hotkeyManager.Attach(window))
            {
                RefreshHotkeys();
            }
        }

        /// <summary>Re-registers all enabled hotkeys from current settings. Call after settings change.</summary>
        public void RefreshHotkeys()
        {
            hotkeyManager?.UpdateBindings(settings.Settings.Hotkeys);
        }

        private void OnHotkeyPressed(HotkeyBinding binding)
        {
            try
            {
                if (binding.Action == HotkeyAction.ToggleFrameColorIndicator)
                {
                    // Global RTSS option: works with or without a running/selected game. File and
                    // RTSS work runs off the keyboard-hook thread so a slow call can't stall input.
                    var label = binding.DisplayText;
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            var enabled = limiterService.ToggleRtssFrameColorIndicator();
                            logger.Info($"FPS Limiter hotkey {label}: RTSS FCAT frame color indicator {(enabled ? "enabled" : "disabled")}.");
                        }
                        catch (Exception ex)
                        {
                            logger.Error(ex, "FPS Limiter could not toggle the RTSS FCAT frame color indicator.");
                        }
                    });
                    return;
                }

                // These hotkeys are the RTSS ones; Special K games use their own macro hotkeys.
                var games = ResolveHotkeyTargetGames().Where(g => !limiterService.ShouldUseSpecialKBackend(g)).ToList();
                if (!games.Any())
                {
                    logger.Debug($"FPS Limiter hotkey {binding.DisplayText} pressed but no running or selected RTSS game to target.");
                    return;
                }

                switch (binding.Action)
                {
                    case HotkeyAction.DisableCap:
                        limiterService.DisableGameLimit(games);
                        logger.Info($"FPS Limiter hotkey {binding.DisplayText}: cap disabled for {DescribeGames(games)}.");
                        break;

                    case HotkeyAction.SetSyncMode:
                        limiterService.SetGameSyncMode(games, binding.SyncMode);
                        logger.Info($"FPS Limiter hotkey {binding.DisplayText}: sync mode set to {FpsSyncModeNames.GetDisplayName(binding.SyncMode)} for {DescribeGames(games)}.");
                        break;

                    default:
                        limiterService.SetGameLimit(games, binding.FrameLimit);
                        logger.Info($"FPS Limiter hotkey {binding.DisplayText}: {FormatFps(binding.FrameLimit)} FPS applied to {DescribeGames(games)}.");
                        break;
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "FPS Limiter failed to apply a hotkey.");
            }
        }

        /// <summary>
        /// Targets whichever game(s) are actually running right now; if none are running, falls
        /// back to the game(s) selected in the library.
        /// </summary>
        private List<Game> ResolveHotkeyTargetGames()
        {
            var runningGameIds = limiterService.GetRunningGameIds().Distinct().ToList();
            if (runningGameIds.Any())
            {
                return runningGameIds
                    .Select(id => PlayniteApi.Database.Games.Get(id))
                    .Where(g => g != null)
                    .ToList();
            }

            return PlayniteApi.MainView.SelectedGames?.ToList() ?? new List<Game>();
        }

        private static string DescribeGames(List<Game> games)
        {
            return games.Count == 1 ? games[0].Name : $"{games.Count} games";
        }

        public void SyncSpecialKGlobalDefaults()
        {
            limiterService.SyncSpecialKGlobalDefaults();
        }

        public Task UpdateSpecialKNowAsync() => specialKUpdateService.UpdateNowAsync();

        public Task CheckExtensionUpdatesNowAsync() => extensionUpdateService.CheckNowAsync();

        public Task UpdateExtensionNowAsync() => extensionUpdateService.UpdateNowAsync();

        public Task CheckSpecialKUpdatesNowAsync()
        {
            return specialKUpdateService.CheckNowAsync();
        }

        public void ApplyEditedGlobalSettings()
        {
            ApplyTopBarIcon();
            limiterService.ApplyEditedGlobalSettings();
        }

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            // Switch to the monitor's forced resolution/refresh BEFORE the game opens its window.
            limiterService.ApplyForcedDisplayMode(args.Game);
            limiterService.PrepareSpecialKInjection(args.Game, args.SourceAction);
            limiterService.TryApplyForGame(args.Game, args.SourceAction, null, true);
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            limiterService.NotifyGameStarted(args.Game.Id, args.StartedProcessId);
            limiterService.TryApplyForGame(args.Game, args.SourceAction, args.StartedProcessId, false);
            RefreshTopBarIconOnUi();
            Task.Run(() => limiterService.RetargetAfterLaunch(args.Game, args.SourceAction, args.StartedProcessId));
        }

        public override void OnGameSelected(OnGameSelectedEventArgs args)
        {
            base.OnGameSelected(args);
            var selectedGames = args?.NewValue?.Where(game => game != null).ToList() ?? new List<Game>();
            try
            {
                PlayniteApi.MainView?.UIDispatcher?.Invoke(() => ApplyTopBarIcon(selectedGames));
            }
            catch (Exception ex)
            {
                logger.Warn("Could not update the top-bar icon for the selected game: " + ex.Message);
            }
        }

        public override void OnGameStartupCancelled(OnGameStartupCancelledEventArgs args)
        {
            var game = args.Game;
            limiterService.MarkSpecialKLaunchFailed(game);
            limiterService.NotifyGameStopped(game.Id);
            RefreshTopBarIconOnUi();

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    limiterService.RestoreActiveSessionAfterGameStopped(game, false);
                }
                catch (Exception e)
                {
                    logger.Error(e, $"Failed to restore limiter session after startup cancellation for {game?.Name}.");
                }
                limiterService.ReleaseForcedDisplayMode(game.Id);
                try
                {
                    limiterService.CleanupSpecialKInjection(game);
                }
                catch (Exception e)
                {
                    logger.Error(e, $"Failed to clean up Special K injection after {game?.Name} startup was cancelled.");
                }
            });
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            // Keep Playnite's game-stop callback non-blocking.
            //
            // RestoreActiveSession may touch RTSS/Special K profiles and, importantly,
            // ChangeDisplaySettingsEx when restoring the pre-game refresh rate. Doing
            // that synchronously here can freeze Playnite with the game still shown
            // as RUNNING until the display/driver call returns.
            var game = args.Game;

            limiterService.NotifyGameStopped(game.Id, true);
            RefreshTopBarIconOnUi();

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    limiterService.RestoreActiveSessionAfterGameStopped(game, true);
                }
                catch (Exception e)
                {
                    logger.Error(e, $"Failed to restore limiter session after {game?.Name} stopped.");
                }

                limiterService.ReleaseForcedDisplayMode(game.Id);

                try
                {
                    limiterService.CleanupSpecialKInjection(game);
                }
                catch (Exception e)
                {
                    logger.Error(e, $"Failed to clean up Special K injection after {game?.Name} stopped.");
                }
            });
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            var games = args.Games ?? new List<Game>();
            if (!games.Any())
            {
                yield break;
            }

            var modeLabel = CurrentMode == PlayniteUiMode.Fullscreen ? "Fullscreen" : "Desktop";
            var menuRoot = $"FPS Limiter 3000 ({modeLabel})";

            // Everything (presets, custom cap, sync mode, Reflex markers, target executable, ...) lives in the
            // game settings window; the context menu only opens it.
            yield return new GameMenuItem
            {
                MenuSection = menuRoot,
                Description = "Game settings…",
                Action = _ => OpenGameSettingsWindow(games.ToList())
            };
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            // The per-monitor "Global FPS limits" / sync mode / Reflex marker menus were removed; global
            // settings live in the plugin's settings page. No entry is added to Playnite's main menu.
            yield break;
        }

        private IEnumerable<MainMenuItem> BuildGlobalFpsMenuItems(string menuRoot, bool windowsGames, string device)
        {
            var category = windowsGames ? "Windows games" : "Non-Windows games";
            var section = menuRoot + "|" + category;
            foreach (var preset in limiterService.GetPresetsForMonitor(device))
            {
                var selectedPreset = preset;
                yield return new MainMenuItem
                {
                    MenuSection = section,
                    Description = GetGlobalPresetMenuText(selectedPreset, windowsGames, device),
                    Action = _ => limiterService.SetGlobalLimit(selectedPreset, windowsGames, device)
                };
            }
            yield return new MainMenuItem
            {
                MenuSection = section,
                Description = GetGlobalCustomMenuText(windowsGames, device),
                Action = _ => SetGlobalCustomCap(windowsGames, device)
            };
            yield return new MainMenuItem
            {
                MenuSection = section,
                Description = "Disable global FPS cap",
                Action = _ => limiterService.DisableGlobalLimit(windowsGames, device)
            };
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        private TopPanelItem topPanelItem;
        private TopPanelItem rtssTopPanelItem;
        private TopPanelItem skifTopPanelItem;
        private bool topPanelVisibilitySupported;

        public override IEnumerable<TopPanelItem> GetTopPanelItems()
        {
            rtssTopPanelItem = new TopPanelItem
            {
                Icon = GetIconPath(false),
                Title = "FPS Limiter 3000 (apply to selected game)",
                Activated = () => ShowTopPanelMenu()
            };
            skifTopPanelItem = new TopPanelItem
            {
                Icon = GetIconPath(true),
                Title = "FPS Limiter 3000 (apply to selected game)",
                Activated = () => ShowTopPanelMenu()
            };
            topPanelVisibilitySupported = CanSetTopPanelVisibility(rtssTopPanelItem) &&
                CanSetTopPanelVisibility(skifTopPanelItem);

            if (topPanelVisibilitySupported)
            {
                // Keep one fixed-icon item for each backend and switch visibility.
                // Playnite may cache an item's icon after it is first created.
                ApplyTopBarIcon();
                yield return rtssTopPanelItem;
                yield return skifTopPanelItem;
                yield break;
            }

            // Older SDKs without a writable visibility property need a restart.
            var useSk = ShouldUseSpecialKForTopBar(null);
            topPanelItem = useSk ? skifTopPanelItem : rtssTopPanelItem;
            if (settings?.Settings?.ShowTopBarIcon != false)
                yield return topPanelItem;
        }

        private static bool CanSetTopPanelVisibility(TopPanelItem item)
        {
            try
            {
                var property = item?.GetType().GetProperty("Visible");
                return property != null && property.CanWrite && property.PropertyType == typeof(bool);
            }
            catch { return false; }
        }

        internal void ApplyTopBarIcon()
        {
            ApplyTopBarIcon(null);
        }

        private void ApplyTopBarIcon(IEnumerable<Game> selectedGames)
        {
            try
            {
                var show = settings?.Settings?.ShowTopBarIcon != false;
                var useSk = ShouldUseSpecialKForTopBar(selectedGames);
                if (topPanelVisibilitySupported)
                {
                    SetTopPanelVisibility(rtssTopPanelItem, show && !useSk);
                    SetTopPanelVisibility(skifTopPanelItem, show && useSk);
                }
                else if (topPanelItem != null)
                {
                    // Compatibility path for SDKs that cannot switch item visibility.
                    topPanelItem.Icon = GetIconPath(useSk);
                    SetTopPanelVisibility(topPanelItem, show);
                }
            }
            catch (Exception ex) { logger.Warn("Could not update FPS Limiter top-bar icon: " + ex.Message); }
        }

        private void SetTopPanelVisibility(TopPanelItem item, bool visible)
        {
            var property = item?.GetType().GetProperty("Visible");
            if (property != null && property.CanWrite && property.PropertyType == typeof(bool))
                property.SetValue(item, visible, null);
        }

        private bool ShouldUseSpecialKForTopBar(IEnumerable<Game> selectedGames)
        {
            var games = selectedGames?.Where(game => game != null).ToList()
                ?? PlayniteApi.MainView?.SelectedGames?.Where(game => game != null).ToList()
                ?? new List<Game>();
            // When nothing is selected, use the currently running game if there is one.
            if (games.Count == 0)
            {
                games = limiterService.GetRunningGameIds()
                    .Select(id => PlayniteApi.Database.Games.Get(id))
                    .Where(game => game != null)
                    .ToList();
            }

            // For multi-selection, use a per-game icon only if every selected game
            // resolves to the same effective backend; otherwise show the global default.
            var backends = games
                .Select(game => limiterService.ShouldUseSpecialKBackend(game))
                .Distinct()
                .ToList();
            var useSk = backends.Count == 1
                ? backends[0]
                : settings?.Settings?.UseSpecialKBackend == true;
            return useSk;
        }

        private string GetIconPath(bool useSk)
        {
            if (useSk)
            {
                var sk = BrandIcons.TopBarSpecialKPath();
                if (!string.IsNullOrEmpty(sk) && File.Exists(sk))
                    return sk;
            }
            else
            {
                var rt = BrandIcons.TopBarRtssPath();
                if (!string.IsNullOrEmpty(rt) && File.Exists(rt))
                    return rt;
            }
            var assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var directory = Path.GetDirectoryName(assemblyLocation) ?? string.Empty;
            return Path.Combine(directory, "icon.png");
        }

        private void RefreshTopBarIconOnUi()
        {
            try
            {
                PlayniteApi.MainView?.UIDispatcher?.Invoke(() => ApplyTopBarIcon());
            }
            catch (Exception ex) { logger.Warn("Could not refresh the top-bar icon: " + ex.Message); }
        }

        private void ShowTopPanelMenu()
        {
            try
            {
                var games = GetSelectedOrRunningGames();
                ApplyTopBarIcon(games); // reflect the same target the menu will edit
                OpenGameSettingsWindow(games);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Top panel FPS Limiter button failed");
                PlayniteApi.Dialogs.ShowErrorMessage(ex.Message, "FPS Limiter 3000");
            }
        }

        private List<Game> GetSelectedOrRunningGames()
        {
            var games = PlayniteApi.MainView.SelectedGames?.ToList() ?? new List<Game>();
            if (games.Count == 0)
            {
                games = ResolveHotkeyTargetGames();
            }
            return games;
        }

        private void OpenGameSettingsWindow(List<Game> games)
        {
            if (gameSettingsOpen) return;

            if (games == null || games.Count == 0)
            {
                PlayniteApi.Dialogs.ShowErrorMessage("Select a game first.", "FPS Limiter 3000");
                return;
            }

            try
            {
                gameSettingsOpen = true;
                var view = new GameSettingsView(limiterService, games);
                var window = PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false
                });

                view.OpenGlobalSettingsRequested = () =>
                {
                    // Close the per-game modal before opening the centered global settings window.
                    window.Close();
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { OpenGlobalSettingsWindow(); }
                        catch (Exception ex) { logger.Error(ex, "Could not open global FPS settings."); }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                };
                view.CreateDialogWindow = () => PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false
                });
                view.OnBackendChanged = () => ApplyTopBarIcon(games);
                var title = games.Count == 1 ? games[0].Name : games.Count + " games";
                window.Title = "FPS Limiter 3000 — " + title;
                window.Content = view;
                // Fullscreen is viewed from a couch: scale the whole panel up (kept within the screen height).
                double uiScale = 1.0;
                if (PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Fullscreen)
                {
                    var work = SystemParameters.WorkArea;
                    uiScale = Math.Max(1.0, work.Height / 1080.0) * 1.3;
                    uiScale = Math.Max(1.0, Math.Min(uiScale, work.Height * 0.92 / 700.0));
                    view.LayoutTransform = new ScaleTransform(uiScale, uiScale);
                }
                window.Width = 480 * uiScale;
                window.MinWidth = 420 * uiScale;
                window.SizeToContent = SizeToContent.Height;
                window.ResizeMode = ResizeMode.NoResize;
                var gameAppearanceSettings = settings.Settings;
                AppearancePalette.Apply(window, gameAppearanceSettings.ThemePreset, gameAppearanceSettings.GlassEffect);
                window.SetResourceReference(Control.BackgroundProperty, "ReShadeBg");
                System.ComponentModel.PropertyChangedEventHandler gameThemeChanged = (sender, e) =>
                {
                    if (string.IsNullOrEmpty(e.PropertyName) ||
                        e.PropertyName == nameof(FPSLimiterSettings.ThemePreset) ||
                        e.PropertyName == nameof(FPSLimiterSettings.GlassEffect))
                        AppearancePalette.Apply(window, gameAppearanceSettings.ThemePreset, gameAppearanceSettings.GlassEffect);
                };
                gameAppearanceSettings.PropertyChanged += gameThemeChanged;
                // Match OptiScaler Injector: center on the work area after layout.
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Loaded += (_, __) => CenterOnWorkArea(window);

                view.CloseRequested = () =>
                {
                    try { window.Close(); } catch { }
                };

                openGameSettingsWindow = window;
                window.Closed += (_, __) =>
                {
                    gameAppearanceSettings.PropertyChanged -= gameThemeChanged;
                    gameSettingsOpen = false;
                    if (ReferenceEquals(openGameSettingsWindow, window)) openGameSettingsWindow = null;
                };
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                gameSettingsOpen = false;
                logger.Error(ex, "Failed to open FPS Limiter game settings");
                PlayniteApi.Dialogs.ShowErrorMessage(
                    "Could not open game settings:\n" + ex.Message,
                    "FPS Limiter 3000");
            }
        }

        private void ShowTopPanelSyncModeMenu(List<Game> games)
        {
            var options = GameSyncModes
                .Select(mode => new GenericItemOption(GetSyncModeMenuText(games, mode), string.Empty))
                .ToList();

            var caption = games.Count == 1
                ? $"FPS Limiter 3000 Sync Mode - {games[0].Name}"
                : $"FPS Limiter 3000 Sync Mode - {games.Count} games selected";

            var selected = PlayniteApi.Dialogs.ChooseItemWithSearch(
                options,
                _ => options,
                null,
                caption);

            if (selected == null)
            {
                return;
            }

            var index = options.IndexOf(selected);
            if (index >= 0 && index < GameSyncModes.Length)
            {
                limiterService.SetGameSyncMode(games, GameSyncModes[index]);
            }
        }

        private string GetTopPanelSyncModeMenuText(List<Game> games)
        {
            if (games.Count == 1)
            {
                return $"Sync mode: {GetSyncModeDisplayName(GetSelectedSyncMode(games[0]), games[0])}";
            }

            return "Sync mode...";
        }

        /// <summary>The sync mode currently stored on the game's profile (UseGlobal if none set yet).</summary>
        private FpsSyncMode GetSelectedSyncMode(Game game)
        {
            var profile = limiterService.GetGameProfile(game);
            return profile?.GetMode(CurrentMode).SyncMode ?? FpsSyncMode.UseGlobal;
        }

        /// <summary>Display text for a sync mode option, spelling out what "Use Global" currently resolves to.</summary>
        private string GetSyncModeDisplayName(FpsSyncMode mode, Game game = null)
        {
            if (mode == FpsSyncMode.UseGlobal)
            {
                var globalMode = game != null
                    ? limiterService.GetEffectiveGameSyncMode(game)
                    : limiterService.GlobalSyncMode;
                return $"Use Global Sync Mode ({FpsSyncModeNames.GetDisplayName(globalMode)})";
            }

            return FpsSyncModeNames.GetDisplayName(mode);
        }

        /// <summary>The Reflex marker setting currently stored on the game's profile (UseGlobal if none set yet).</summary>
        private ReflexMarkerMode GetSelectedReflexMarkers(Game game)
        {
            var profile = limiterService.GetGameProfile(game);
            return profile?.GetMode(CurrentMode).ReflexMarkers ?? ReflexMarkerMode.UseGlobal;
        }

        /// <summary>Display text for a Reflex marker option, spelling out what "Use Global" currently resolves to.</summary>
        private string GetReflexMarkerDisplayName(ReflexMarkerMode mode, Game game = null)
        {
            if (mode == ReflexMarkerMode.UseGlobal)
            {
                var globalMode = game != null
                    ? limiterService.GetEffectiveGameReflexMarkers(game)
                    : limiterService.GlobalReflexMarkers;
                return $"Use Global Setting ({ReflexMarkerModeNames.GetDisplayName(globalMode)})";
            }

            return ReflexMarkerModeNames.GetDisplayName(mode);
        }

        private PlayniteUiMode CurrentMode =>
            PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Fullscreen
                ? PlayniteUiMode.Fullscreen
                : PlayniteUiMode.Desktop;

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new global::FPSLimiterSettingsUI.FPSLimiterSettingsView();
        }

        private string GetPresetMenuText(List<Game> games, double preset)
        {
            var presetText = FormatFps(preset);
            if (games.Count == 1)
            {
                var profile = limiterService.GetGameProfile(games[0]);
                var mode = profile?.GetMode(CurrentMode);
                if (mode != null && mode.Enabled && mode.FrameLimit == preset)
                {
                    return $"{ActiveMenuPrefix}{presetText} FPS";
                }
            }

            return $"{presetText} FPS";
        }

        private string GetCustomMenuText(List<Game> games)
        {
            if (games.Count == 1)
            {
                var profile = limiterService.GetGameProfile(games[0]);
                var mode = profile?.GetMode(CurrentMode);
                if (mode != null &&
                    mode.Enabled &&
                    mode.FrameLimit > 0 &&
                    !limiterService.GetPresets(games[0]).Contains(mode.FrameLimit))
                {
                    return $"{ActiveMenuPrefix}Custom FPS cap...";
                }
            }

            return "Custom FPS cap...";
        }

        /// <summary>
        /// Per-game "Use global FPS cap" label. Checked when the game has no per-game override
        /// (so launch/apply will follow the matching Windows/non-Windows Global). Shows the
        /// current Global value when enabled.
        /// </summary>
        private string GetUseGlobalFpsMenuText(List<Game> games)
        {
            string label;
            if (games.Count == 1)
            {
                var game = games[0];
                var isWindows = FPSLimiterSettings.IsWindowsGame(game);
                var category = isWindows ? "Windows" : "Non-Windows";
                if (limiterService.IsGlobalLimitEnabledForGame(game) &&
                    limiterService.GlobalFrameLimitForGame(game) > 0)
                {
                    label = $"Use global FPS cap ({category}: {FormatFps(limiterService.GlobalFrameLimitForGame(game))} FPS)";
                }
                else
                {
                    label = $"Use global FPS cap ({category}: Global off)";
                }

                var profile = limiterService.GetGameProfile(game);
                var mode = profile?.GetMode(CurrentMode);
                var hasPerGameLimit = mode != null && mode.Enabled && mode.FrameLimit > 0;
                if (!hasPerGameLimit)
                {
                    return $"{ActiveMenuPrefix}{label}";
                }

                return label;
            }

            return "Use global FPS cap";
        }

        private void SetCustomCap(List<Game> games)
        {
            var defaultValue = "60";
            if (games.Count == 1)
            {
                var profile = limiterService.GetGameProfile(games[0]);
                var mode = profile?.GetMode(CurrentMode);
                if (mode != null && mode.Enabled && mode.FrameLimit > 0)
                {
                    defaultValue = FormatFps(mode.FrameLimit);
                }
            }

            var result = PlayniteApi.Dialogs.SelectString("Enter an FPS cap between 1 and 1000. Decimals are allowed, e.g. 59.9.", "FPS Limiter 3000", defaultValue);
            if (!result.Result)
            {
                return;
            }

            double cap;
            if (!double.TryParse(result.SelectedString, NumberStyles.Float, CultureInfo.InvariantCulture, out cap) || cap <= 0 || cap > 1000)
            {
                PlayniteApi.Dialogs.ShowErrorMessage("Enter a number between 1 and 1000, e.g. 60 or 59.9.", "FPS Limiter 3000");
                return;
            }

            limiterService.SetGameLimit(games, Math.Round(cap, 4));
        }

        private static string FormatFps(double value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private void ChooseManualExecutable(List<Game> games)
        {
            if (games.Count != 1)
            {
                PlayniteApi.Dialogs.ShowErrorMessage("Choose exactly one game before setting a manual target executable.", "FPS Limiter 3000");
                return;
            }

            var executable = PlayniteApi.Dialogs.SelectFile("Executable files (*.exe)|*.exe|All files (*.*)|*.*");
            if (string.IsNullOrWhiteSpace(executable))
            {
                return;
            }

            if (!File.Exists(executable) || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                PlayniteApi.Dialogs.ShowErrorMessage("The target executable must be an .exe file.", "FPS Limiter 3000");
                return;
            }

            limiterService.SetManualExecutable(games[0], executable);
        }

        private void ClearManualExecutable(List<Game> games)
        {
            if (games.Count != 1)
            {
                PlayniteApi.Dialogs.ShowErrorMessage("Choose exactly one game before clearing a manual target executable.", "FPS Limiter 3000");
                return;
            }

            limiterService.ClearManualExecutable(games[0]);
        }

        private string GetSyncModeMenuText(List<Game> games, FpsSyncMode mode)
        {
            var game = games.Count == 1 ? games[0] : null;
            var name = GetSyncModeDisplayName(mode, game);

            if (game != null && GetSelectedSyncMode(game) == mode)
            {
                return $"{ActiveMenuPrefix}{name}";
            }

            return name;
        }

        private string GetReflexMarkerMenuText(List<Game> games, ReflexMarkerMode mode)
        {
            var game = games.Count == 1 ? games[0] : null;
            var name = GetReflexMarkerDisplayName(mode, game);

            if (game != null && GetSelectedReflexMarkers(game) == mode)
            {
                return $"{ActiveMenuPrefix}{name}";
            }

            return name;
        }

        private string GetGlobalPresetMenuText(double preset, bool windowsGames, string device = null)
        {
            var presetText = FormatFps(preset);
            if (limiterService.IsGlobalLimitEnabledFor(windowsGames, device) &&
                limiterService.GlobalFrameLimitFor(windowsGames, device) == preset)
            {
                return $"{ActiveMenuPrefix}{presetText} FPS";
            }

            return $"{presetText} FPS";
        }

        private string GetGlobalCustomMenuText(bool windowsGames, string device = null)
        {
            var limit = limiterService.GlobalFrameLimitFor(windowsGames, device);
            if (limiterService.IsGlobalLimitEnabledFor(windowsGames, device) &&
                limit > 0 &&
                !limiterService.GetPresetsForMonitor(device).Contains(limit))
            {
                return $"{ActiveMenuPrefix}Custom FPS cap...";
            }

            return "Custom FPS cap...";
        }

        private void SetGlobalCustomCap(bool windowsGames, string device = null)
        {
            var current = limiterService.GlobalFrameLimitFor(windowsGames, device);
            var defaultValue = current > 0 ? FormatFps(current) : "60";
            var category = windowsGames ? "Windows games" : "Non-Windows games";
            var result = PlayniteApi.Dialogs.SelectString(
                $"Enter a global FPS cap for {category} (1–1000). Decimals are allowed, e.g. 59.9.",
                "FPS Limiter 3000",
                defaultValue);
            if (!result.Result)
            {
                return;
            }

            double cap;
            if (!double.TryParse(result.SelectedString, NumberStyles.Float, CultureInfo.InvariantCulture, out cap) || cap <= 0 || cap > 1000)
            {
                PlayniteApi.Dialogs.ShowErrorMessage("Enter a number between 1 and 1000, e.g. 60 or 59.9.", "FPS Limiter 3000");
                return;
            }

            limiterService.SetGlobalLimit(Math.Round(cap, 4), windowsGames, device);
        }

        private string GetGlobalSyncModeMenuText(FpsSyncMode mode, bool windowsGames, string device = null)
        {
            var name = FpsSyncModeNames.GetDisplayName(mode);
            return limiterService.GlobalSyncModeFor(windowsGames, device) == mode ? $"{ActiveMenuPrefix}{name}" : name;
        }

        private string GetGlobalReflexMarkerMenuText(ReflexMarkerMode mode, bool windowsGames, string device = null)
        {
            var name = ReflexMarkerModeNames.GetDisplayName(mode);
            return limiterService.GlobalReflexMarkersFor(windowsGames, device) == mode ? $"{ActiveMenuPrefix}{name}" : name;
        }

    }
}
