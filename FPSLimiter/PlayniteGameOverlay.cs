using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System.ComponentModel;

namespace PlayniteGameOverlay
{
    public class PlayniteGameOverlay : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private OverlaySettings settings;

        private GameOverlayData GameOverlayData;

        private OverlayWindow overlayWindow;
        private OverlaySettingsView _openGlobalSettingsView;
        private IPlayniteAPI playniteAPI;
        private GlobalKeyboardHook keyboardHook;
        private System.Windows.Threading.DispatcherTimer gameHotkeyPidTimer;
        private System.Windows.Threading.DispatcherTimer masterVolumeFocusTimer;
        private Guid _masterVolumeTrackedGameId;
        private bool _masterVolumeTrackedGameFocused;
        private int _masterVolumeFocusVersion;
        private int cachedActualGamePid = -1;
        private int resolvingGameHotkeyPid;

        private ControllerState controllerState = new ControllerState();
        private XInputPoller xInputPoller;
        private ControllerHotkeyTracker controllerHotkeys;
        private SonyHidGuidePoller sonyHidPoller;
        private System.Windows.Threading.DispatcherTimer _padWindowWatch;
        private DateTime _lastPadActivityUtc = DateTime.MinValue;
        private System.Windows.Window _padInitWindow;
        private CursorAutoHide cursorAutoHide;
        private DateTime _lastControllerShortcutUtc = DateTime.MinValue;
        /// <summary>Ignore Guide close for a short window after open (Playnite + XInput double-fire).</summary>
        private DateTime _overlayOpenedUtc = DateTime.MinValue;
        private DateTime _lastSuspendToggleUtc = DateTime.MinValue;
        private int _suspendToggleBusy;
        private int _applyingInstantPlayPolicy;
        private CompleteUninstallManager _completeUninstall;
        private InstallSizeFiller _installSize;

        public override Guid Id { get; } = Guid.Parse("fc75626e-ec69-4287-972a-b86298555ebb");

        DateTime gameStarted;

        // Achievement plugin integration
        // SuccessStory (Lacro59) and PlayniteAchievements (justin-delano / Santodan fork)
        private bool isSuccessStoryAvailable = false;
        private bool isPlayniteAchievementsAvailable = false;
        private Guid successStoryId = Guid.Parse("cebe6d32-8c46-4459-b993-5a5189d60788");
        private Guid playniteAchievementsId = Guid.Parse("e6aad2c9-6e06-4d8d-ac55-ac3b252b5f7b");

        private void CheckAchievementPluginAvailability()
        {
            try
            {
                var plugins = playniteAPI.Addons.Plugins;
                isSuccessStoryAvailable = plugins.Any(p => p.Id == successStoryId);
                isPlayniteAchievementsAvailable = plugins.Any(p => p.Id == playniteAchievementsId);

                if (isPlayniteAchievementsAvailable)
                    log("PlayniteAchievements plugin detected, achievement integration enabled");
                if (isSuccessStoryAvailable)
                    log("SuccessStory plugin detected, achievement integration enabled");
                if (!isPlayniteAchievementsAvailable && !isSuccessStoryAvailable)
                    log("No achievement plugin found (PlayniteAchievements / SuccessStory), achievement panel disabled");
            }
            catch (Exception ex)
            {
                log($"Error checking for achievement plugins: {ex.Message}", "ERROR");
                isSuccessStoryAvailable = false;
                isPlayniteAchievementsAvailable = false;
            }
        }


        public override void OnControllerButtonStateChanged(OnControllerButtonStateChangedArgs args)
        {
            // An exception escaping here lands in Playnite's controller event dispatch and can take
            // the whole app down (the game keeps running because it is a separate process).
            try { OnControllerButtonStateChangedCore(args); }
            catch (Exception ex)
            {
                try { logger.Error($"OnControllerButtonStateChanged failed: {ex}"); } catch { }
            }
        }

        private void OnControllerButtonStateChangedCore(OnControllerButtonStateChangedArgs args)
        {
            var mostRecentPress = controllerState.Update(args);
            var startBackCombo = false;
            var guideActivated = false;

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (overlayWindow != null && overlayWindow.IsActive)
                {
                    log($"Window Recieved {args.State.ToString()}: {args.Button.ToString()}", "SDL_INPUT");
                    try { overlayWindow.NoteInputSource(true); } catch { }

                    // When an XInput pad is connected the XInput poller owns overlay navigation
                    if (XInputOwnsPad())
                        return;

                    if (mostRecentPress == null)
                        return;

                    switch (mostRecentPress)
                    {
                        case ControllerInput.DPadUp:
                        case ControllerInput.LeftStickUp:
                            log("Navigating UP", "SDL_NAV");
                            overlayWindow.FocusUp();
                            break;
                        case ControllerInput.DPadDown:
                        case ControllerInput.LeftStickDown:
                            log("Navigating DOWN", "SDL_NAV");
                            overlayWindow.FocusDown();
                            break;
                        case ControllerInput.DPadLeft:
                        case ControllerInput.LeftStickLeft:
                            log("Navigating LEFT", "SDL_NAV");
                            overlayWindow.FocusLeft();
                            break;
                        case ControllerInput.DPadRight:
                        case ControllerInput.LeftStickRight:
                            log("Navigating RIGHT", "SDL_NAV");
                            overlayWindow.FocusRight();
                            break;
                        case ControllerInput.LeftShoulder:
                            log("LB — previous item", "SDL_NAV");
                            overlayWindow.FocusPreviousItem();
                            break;
                        case ControllerInput.RightShoulder:
                            log("RB — next item", "SDL_NAV");
                            overlayWindow.FocusNextItem();
                            break;
                        case ControllerInput.TriggerLeft:
                            log("LT — previous panel", "SDL_NAV");
                            overlayWindow.FocusPreviousPanel();
                            break;
                        case ControllerInput.TriggerRight:
                            log("RT — next panel", "SDL_NAV");
                            overlayWindow.FocusNextPanel();
                            break;
                        case ControllerInput.A:
                            log("Button A pressed - clicking focused element", "SDL_NAV");
                            overlayWindow.ClickFocusedElement();
                            break;
                        case ControllerInput.B:
                            if (!overlayWindow.TryCloseAchievementBrowser())
                            {
                                log("Button B pressed - Hiding overlay", "SDL_NAV");
                                overlayWindow.Hide();
                            }
                            else
                            {
                                log("Button B pressed - Closed achievement browser", "SDL_NAV");
                            }
                            break;
                    }
                }
                else
                {
                    log($"Background Listener Recieved {args.State.ToString()}: {args.Button.ToString()}", "SDL_INPUT");
                    if (mostRecentPress == null)
                        return;

                    // Fullscreen mode: dialogs / windows Playnite does not drive itself (backup if XInput misses the pad)
                    if (!IsDesktopMode && Settings?.EnableDesktopControllerNav != false && !XInputOwnsPad())
                    {
                        var fsWindow = GetPadSettingsWindow();
                        if (fsWindow != null && RoutePadToWindow(mostRecentPress, fsWindow)) return;
                    }

                    // Desktop nav via SDL when overlay is closed (backup if XInput misses the pad)
                    if (Settings?.EnableDesktopControllerNav != false &&
                        DesktopControllerNav.IsPlayniteDesktopForeground())
                    {
                        // Dialogs / settings windows: same window-style navigation as the XInput path
                        // (only when XInput is not running, otherwise both paths would fire).
                        if (!XInputOwnsPad())
                        {
                            var padWindow = GetPadSettingsWindow();
                            if (padWindow != null)
                            {
                                switch (mostRecentPress)
                                {
                                    case ControllerInput.DPadUp:
                                    case ControllerInput.LeftStickUp:
                                        SettingsPadNav.Move(padWindow, System.Windows.Input.FocusNavigationDirection.Up); return;
                                    case ControllerInput.DPadDown:
                                    case ControllerInput.LeftStickDown:
                                        SettingsPadNav.Move(padWindow, System.Windows.Input.FocusNavigationDirection.Down); return;
                                    case ControllerInput.DPadLeft:
                                    case ControllerInput.LeftStickLeft:
                                        SettingsPadNav.Move(padWindow, System.Windows.Input.FocusNavigationDirection.Left); return;
                                    case ControllerInput.DPadRight:
                                    case ControllerInput.LeftStickRight:
                                        SettingsPadNav.Move(padWindow, System.Windows.Input.FocusNavigationDirection.Right); return;
                                    case ControllerInput.A: SettingsPadNav.Confirm(padWindow); return;
                                    case ControllerInput.B: SettingsPadNav.Back(padWindow); return;
                                    case ControllerInput.LeftShoulder: SettingsPadNav.Section(padWindow, -1); return;
                                    case ControllerInput.RightShoulder: SettingsPadNav.Section(padWindow, 1); return;
                                }
                            }
                        }

                        switch (mostRecentPress)
                        {
                            case ControllerInput.DPadUp:
                            case ControllerInput.LeftStickUp:
                                DesktopControllerNav.Up();
                                return;
                            case ControllerInput.DPadDown:
                            case ControllerInput.LeftStickDown:
                                DesktopControllerNav.Down();
                                return;
                            case ControllerInput.DPadLeft:
                            case ControllerInput.LeftStickLeft:
                                DesktopControllerNav.Left();
                                return;
                            case ControllerInput.DPadRight:
                            case ControllerInput.LeftStickRight:
                                DesktopControllerNav.Right();
                                return;
                            case ControllerInput.A:
                                DesktopControllerNav.Confirm();
                                return;
                            case ControllerInput.B:
                                DesktopControllerNav.Back();
                                return;
                        }
                    }

                    if (mostRecentPress == ControllerInput.Start || mostRecentPress == ControllerInput.Back || mostRecentPress == ControllerInput.Guide)
                    {
                        startBackCombo = Settings.ControllerShortcut == ControllerShortcut.StartBack &&
                                             controllerState.ButtonStart && controllerState.ButtonBack;

                        guideActivated = Settings.ControllerShortcut == ControllerShortcut.Guide &&
                                             controllerState.ButtonGuide;

                        log($"sb:{startBackCombo}, g:{guideActivated}");
                        if (startBackCombo || guideActivated)
                        {
                            System.Windows.Application.Current.Dispatcher.BeginInvoke(
                                new Action(HandleControllerShortcutToggle));
                        }
                    }
                }
            });
        }

        public PlayniteGameOverlay(IPlayniteAPI api) : base(api)
        {
            // Register before any JSON/config parsing. Playnite hosts multiple add-ins
            // in the same .NET Framework AppDomain and does not necessarily probe
            // the extension installation folder for transitive assemblies.
            OverlayAssemblyResolver.Install();
            playniteAPI = api;
            Properties = new GenericPluginProperties { HasSettings = true };
            RecoverSettingsIfNeeded();
            OverlayWindowViewModel.GameMuteApplied += OnOverlayGameMuteApplied;
        }


        /// <summary>
        /// True when Playnite is running in Desktop mode (not Fullscreen app).
        /// </summary>
        private bool IsDesktopMode =>
            playniteAPI?.ApplicationInfo?.Mode != ApplicationMode.Fullscreen;

        /// <summary>
        /// Shared path for Playnite controller events and XInput fallback.
        /// </summary>
        private void HandleControllerShortcutToggle()
        {
            try
            {
                // Debounce: Fullscreen may raise Playnite event and XInput in the same press
                var now = DateTime.UtcNow;
                if ((now - _lastControllerShortcutUtc).TotalMilliseconds < 400)
                    return;
                _lastControllerShortcutUtc = now;

                log($"Controller shortcut: parked={WindowHelper.IsParked}, frozen={WindowHelper.IsAnyProcessFrozen}, " +
                    $"overlayVisible={overlayWindow?.IsVisible}, overlayActive={overlayWindow?.Active}", "INFO");

                // While parked: guide / Start+Back only resumes the game — do not open overlay.
                // Opening the overlay on a just-resumed (or still-waking) process was a common
                // path to AttachThreadInput hangs and games stuck frozen.
                if (WindowHelper.IsParked)
                {
                    try
                    {
                        // ResumeAndRestore already releases the mouse lock/cursors. Doing it again
                        // here meant extra synchronous cursor reloads on the UI thread while the
                        // game was still waking up.
                        WindowHelper.ResumeAndRestore();
                        try { cursorAutoHide?.ForceShow(); } catch { }
                        log("Controller shortcut: resumed parked game (no overlay)", "INFO");
                    }
                    catch (Exception ex)
                    {
                        logger.Error($"Controller resume-from-park failed: {ex.Message}");
                        try { WindowHelper.ResumeAndRestore(); } catch { }
                    }
                    return;
                }

                var runningGame = playniteAPI.Database.Games.FirstOrDefault(g => g.IsRunning);
                if (runningGame != null)
                {
                    // Preserve the original overlay close/resume path exactly.
                    // Background-game focus handling only applies while the overlay is closed.
                    if (overlayWindow != null && overlayWindow.IsVisible && overlayWindow.Active)
                    {
                        if ((DateTime.UtcNow - _overlayOpenedUtc).TotalMilliseconds < 700)
                        {
                            log("Controller shortcut: ignoring close (open grace period)", "DEBUG");
                            return;
                        }
                        overlayWindow.Hide();
                        return;
                    }

                    // If a game is running but is not currently foreground, the shortcut should
                    // only restore/focus that game. Do not open or suspend the overlay on this press.
                    if (TryFocusRunningGame(runningGame))
                    {
                        log("Controller shortcut: focused background game (no overlay)", "INFO");
                        return;
                    }

                    // Original working open path: ShowGameOverlay -> OverlayWindow.ShowOverlay.
                    try { cursorAutoHide?.ForceShow(); } catch { }
                    ShowGameOverlay(runningGame);
                    _overlayOpenedUtc = DateTime.UtcNow;
                }
                else
                {
                    // No game running: just bring the Playnite that is already running to the front.
                    FocusRunningPlaynite();
                }
            }
            catch (Exception ex)
            {
                logger.Error($"HandleControllerShortcutToggle failed: {ex.Message}");
            }
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            logger.Info("Starting Overlay Extension...");
            UpdateChecker.StartAutoCheck(this, playniteAPI, logger); // update-checks

            DesktopControllerNav.Initialize(playniteAPI);

            try
            {
                Breadcrumb.DirectoryPath = GetPluginUserDataPath();
                Breadcrumb.Write("---- Playnite started, overlay extension loaded ---- (nav-trace build 2026-11-27d)");
                StallProbe.Start();
            }
            catch { }
            var startupClock = Stopwatch.StartNew();
            Action<string> startupMark = name => Breadcrumb.Write("Startup: " + name + " done at +" + startupClock.ElapsedMilliseconds + " ms");

            // A previous session may have ended before the original fullscreen-optimization values were restored.
            try { WindowHelper.RestoreFakeFullscreen(FakeFullscreenBackupPath()); } catch { }

            // Detect installed achievement plugins (PlayniteAchievements preferred, SuccessStory fallback)
            CheckAchievementPluginAvailability();

            if (Settings != null && !string.IsNullOrWhiteSpace(Settings.RtssPath))
                OverlayRtssController.ConfiguredRtssPath = Settings.RtssPath;

            // Initialize overlay window
            overlayWindow = new OverlayWindow(Settings);
            overlayWindow.SaveSettingsCallback = () =>
            {
                try { SaveSettingsSafely(Settings); }
                catch { /* ignore */ }
            };
            WireOverlayFpsIntegration(overlayWindow);
            startupMark("overlay window created");
            overlayWindow.Prewarm();
            startupMark("overlay Prewarm");
            overlayWindow.Hide();

            // Set up show Playnite handler
            overlayWindow.OnShowPlayniteRequested += ShowPlaynite;

            // Initialize global keyboard hook
            keyboardHook = new GlobalKeyboardHook();
            keyboardHook.KeyPressed += OnKeyPressed;
            // Launchers often hand off to another PID. Resolve the actual game window
            // periodically, never from the low-level keyboard hook callback.
            gameHotkeyPidTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            gameHotkeyPidTimer.Tick += (sender, e) => StallProbe.Time("RefreshGameHotkeyPid", RefreshGameHotkeyPid);
            gameHotkeyPidTimer.Start();
            RefreshGameHotkeyPid();

            // Capture each game's system-master preference only in Master mode. Per-game mode
            // keeps control on the app audio session and must not change the Windows endpoint.
            masterVolumeFocusTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            masterVolumeFocusTimer.Tick += (sender, e) => StallProbe.Time("TrackGameMasterVolumeFocus", TrackGameMasterVolumeFocus);
            masterVolumeFocusTimer.Start();
            TrackGameMasterVolumeFocus();

            // After sleep/hibernate Windows often silently drops WH_KEYBOARD_LL hooks
            // and system cursors can stay blank if CursorAutoHide hid them. Recover both.
            try
            {
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
                SystemEvents.SessionSwitch += OnSessionSwitch;
            }
            catch (Exception ex)
            {
                logger.Error($"Failed to subscribe power/session events: {ex.Message}");
            }

            // Desktop mode often does not raise OnControllerButtonStateChanged.
            // XInput polling handles toggle + full overlay navigation there.
            // Do not hide the cursor while the game is parked (desktop should stay usable)
            // ...and only hide it while Playnite or a game is in the foreground: the cursor is a
            // system-wide setting, so browsers / other apps must keep a normal cursor.
            cursorAutoHide = new CursorAutoHide(
                () => Settings?.HideCursorOnGamepad != false && !WindowHelper.IsParked && IsCursorHideContext());
            cursorAutoHide.Start();
            startupMark("timers + cursor hide");


            xInputPoller = new XInputPoller(
                () => Settings?.ControllerShortcut ?? ControllerShortcut.StartBack,
                HandleControllerShortcutToggle,
                HandleControllerShortcutToggle,
                isOverlayOpen: () => overlayWindow != null && overlayWindow.IsVisible && overlayWindow.Active && !overlayWindow.InputPaused,
                onUp: () => HandlePadDirection(DesktopControllerNav.Up, () => overlayWindow?.FocusUp(), System.Windows.Input.FocusNavigationDirection.Up),
                onDown: () => HandlePadDirection(DesktopControllerNav.Down, () => overlayWindow?.FocusDown(), System.Windows.Input.FocusNavigationDirection.Down),
                onLeft: () => HandlePadDirection(DesktopControllerNav.Left, () => overlayWindow?.FocusLeft(), System.Windows.Input.FocusNavigationDirection.Left),
                onRight: () => HandlePadDirection(DesktopControllerNav.Right, () => overlayWindow?.FocusRight(), System.Windows.Input.FocusNavigationDirection.Right),
                onA: () => HandlePadConfirm(),
                onB: () => HandlePadBack(),
                onLb: () => { if (TryPadSettings(w => SettingsPadNav.Section(w, -1))) return; DispatchOverlayNav(() => overlayWindow?.FocusPreviousItem()); },
                onRb: () => { if (TryPadSettings(w => SettingsPadNav.Section(w, 1))) return; DispatchOverlayNav(() => overlayWindow?.FocusNextItem()); },
                onAnyPadActivity: () => { cursorAutoHide?.NotifyGamepadActivity(); _lastPadActivityUtc = DateTime.UtcNow; try { overlayWindow?.NoteInputSource(true); } catch { } },
                intervalMs: 16,
                onLt: () => { if (TryPadSettings(w => SettingsPadNav.Jump(w, false))) return; DispatchOverlayNav(() => overlayWindow?.FocusPreviousPanel()); },
                onRt: () => { if (TryPadSettings(w => SettingsPadNav.Jump(w, true))) return; DispatchOverlayNav(() => overlayWindow?.FocusNextPanel()); },
                onLbDesktop: () => { if (TryPadSettings(w => SettingsPadNav.Section(w, -1))) return; HandlePadTopBar(-1); },
                onRbDesktop: () => { if (TryPadSettings(w => SettingsPadNav.Section(w, 1))) return; HandlePadTopBar(1); },
                onLtDesktop: () => { if (TryPadSettings(w => SettingsPadNav.Jump(w, false))) return; HandlePadTopBarJump(-1); },
                onRtDesktop: () => { if (TryPadSettings(w => SettingsPadNav.Jump(w, true))) return; HandlePadTopBarJump(1); },
                onSquare: () => HandlePadSquare(),
                onStart: () => HandlePadStart());
            xInputPoller.Start();
            startupMark("input pollers started");

            // User-defined controller hotkeys (settings: Hotkeys > Controller). Overlay works any time;
            // the others only while a game is running.
            controllerHotkeys = new ControllerHotkeyTracker();
            controllerHotkeys.Add(() => Settings?.ControllerHotkeyOverlay,
                () => DispatchHotkeyAction(HandleControllerShortcutToggle));
            controllerHotkeys.Add(() => Settings?.ControllerHotkeyScreenshot, () =>
            {
                if (GameOverlayData == null || Settings == null) return;
                var combo = Settings.PrintScreenRemapCombo;
                DispatchHotkeyAction(() => ButtonActions.SendComboAsync(combo));
            });
            controllerHotkeys.Add(() => Settings?.ControllerHotkeySuspend, () =>
            {
                if (GameOverlayData == null || Settings == null || !Settings.EnableSuspendMinimize) return;
                DispatchHotkeyAction(() =>
                {
                    try { ToggleSuspendMinimizeGame(); }
                    catch (Exception ex) { logger.Error($"Controller suspend hotkey failed: {ex.Message}"); }
                });
            });
            controllerHotkeys.Add(() => Settings?.ControllerHotkeyMute, () =>
            {
                if (GameOverlayData == null) return;
                DispatchHotkeyAction(() =>
                {
                    try { ToggleRunningGameMute(); }
                    catch (Exception ex) { logger.Error($"Controller mute hotkey failed: {ex.Message}"); }
                });
            });
            HotkeysLayout.AddPadVolume(controllerHotkeys, () => Settings, AdjustVolume, DispatchHotkeyAction, m => logger.Error(m)); // hotkeys-layout
            xInputPoller.MaskPolled += controllerHotkeys.Update;

            // DualShock 4 / DualSense are not XInput pads and Desktop mode rarely raises Playnite's
            // controller events for them. Read them straight from HID and feed the state into the XInput
            // poller, so the overlay toggle (PS button / Share+Options) and all navigation work natively
            // without DS4Windows or Steam Input. Button state is merged before edge detection, so a pad that
            // is also exposed over XInput still fires each press only once.
            try
            {
                sonyHidPoller = new SonyHidGuidePoller { Log = m => log(m) };
                sonyHidPoller.Start();
                xInputPoller.SonyPad = sonyHidPoller;
                log("Sony HID poller started (DualShock 4 / DualSense: toggle + navigation via XInput poller)");
            }
            catch (Exception ex) { logger.Warn("Sony HID poller failed to start: " + ex.Message); }
            StartPadWindowWatch();
            log(xInputPoller.IsAvailable
                ? "XInput gamepad poller started (overlay + Desktop nav + cursor auto-hide)"
                : "XInput not available — controller navigation limited");


            // The temporary stability build deliberately switched Instant Play off.
            // Restore it once for existing installs, then leave future user choices alone.
            if (Settings != null && !Settings.InstantPlayRestoreApplied)
            {
                Settings.EnableSteamInstantPlay = true;
                Settings.InstantPlayRestoreApplied = true;
                try { SaveSettingsSafely(Settings); } catch { }
                log("Steam Instant Play restored after stability rollback", "INFO");
            }

            // Exclusive Instant Play hides Steam's normal library action, leaving one
            // PlayController and therefore no Playnite "Select action" dialog.
            ApplySteamInstantPlayPolicy();

            // Steam library initialization can run just after OnApplicationStarted and
            // restore IncludeLibraryPluginAction=true. Do one delayed policy pass after
            // startup so Instant Play's exclusive Play action survives a Playnite restart.
            if (ShouldRunInstantPlayPolicy())
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        await System.Threading.Tasks.Task.Delay(3000);
                        playniteAPI.MainView.UIDispatcher.BeginInvoke(
                            new Action(() => ApplySteamInstantPlayPolicy()));
                    }
                    catch (Exception ex)
                    {
                        logger.Error($"Delayed Steam Instant Play policy apply failed: {ex.Message}");
                    }
                });
            }

            // If the user later adds another Play action in Edit Game, immediately
            // demote it to a secondary action so the main Play button stays direct.
            try { playniteAPI.Database.Games.ItemUpdated += OnInstantPlayGameItemUpdated; } catch { }
            try { playniteAPI.Database.Games.ItemCollectionChanged += OnGamesCollectionChangedForLinks; } catch { }
            try
            {
                _localStore = new LocalGamesStore(playniteAPI, logger, () => Settings, GetPluginUserDataPath());
                // Metadata and links are fetched together: the store asks the plugin for the link
                // sources up front and adds the links in the same database update as the metadata.
                var nexusCachePath = NexusCachePath();
                PcGamingWikiPages.Configure(PcgwCachePath());
                _localStore.PrepareLinks = () => NexusGames.EnsureLoaded(nexusCachePath, m => logger.Warn(m));
                _localStore.ApplyLinks = AddMissingLinks;
                _localStore.RequestAchievements = RequestAchievementsRefresh;
                _localStore.IsPcgwUnresolved = g =>
                {
                    string ignored;
                    return PcGamingWikiPages.Get(PcgwKey(g), out ignored) == PcGamingWikiPages.PageState.Unknown;
                };
                // Runs the PCGamingWiki page check and save-location lookup for a batch of new games before their
                // metadata is written, so every link lands in the same update (these were only checked by the
                // separate background pass before, which new games had to wait for).
                _localStore.PrepareLinksForGames = (games, ct) =>
                {
                    if (games == null || games.Count == 0) return;
                    if (Settings?.AutoAddGameLinks == true)
                    {
                        try
                        {
                            PcGamingWikiPages.Configure(PcgwCachePath());
                            var pcgwKeys = games.Select(PcgwKey).ToList();
                            PcGamingWikiPages.Prepare(pcgwKeys, m => { logger.Warn(m); Breadcrumb.Write("PCGW: " + m); }, ct);
                            foreach (var key in pcgwKeys)
                            {
                                string pageUrl;
                                var state = PcGamingWikiPages.Get(key, out pageUrl);
                                Breadcrumb.Write("PCGW: '" + key.Name + "' -> " + state + (pageUrl != null ? " " + pageUrl : ""));
                            }
                        }
                        catch (Exception ex) { logger.Warn($"PCGamingWiki page check: {ex.Message}"); }
                    }
                    if (Settings?.AutoAddSaveLinks == true)
                    {
                        try
                        {
                            SaveLocations.Prepare(GetPluginUserDataPath(),
                                games.Select(g => new SaveLocations.GameKey { Id = g.Id, Name = g.Name }).ToList(),
                                m => logger.Warn(m));
                        }
                        catch (Exception ex) { logger.Warn($"Save locations: {ex.Message}"); }
                    }
                };
                // Provider-supplied PCGamingWiki links are held back: AddMissingLinks adds the verified page link.
                _localStore.AcceptLink = l => !(Settings?.AutoAddGameLinks == true && IsAutoPcgwLink(l));
                playniteAPI.Database.Games.ItemCollectionChanged += OnGamesCollectionChangedForLocalStore;
                playniteAPI.Database.Games.ItemUpdated += OnLocalStoreGameItemUpdated;
                _localStore.OnApplicationStarted();
            }
            catch (Exception ex) { logger.Error($"Local Games Store failed to start: {ex.Message}"); }
            ApplyAutoGameLinks();

            // Diagnostics: if Playnite dies from an unhandled managed exception that involves this
            // extension, leave a stack trace in <extension data>/crash-trace.log (Playnite's own
            // log is often not flushed when the process is torn down).
            try
            {
                AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
                System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            }
            catch { }

            // Complete uninstall: finish pending leftover cleanups once the normal uninstall is done.
            // Desktop mode only: its dialogs/startup sweep are not safe inside the Fullscreen app.
            if (IsDesktopMode)
            try
            {
                _completeUninstall = new CompleteUninstallManager(playniteAPI, logger, GetPluginUserDataPath());
                playniteAPI.Database.Games.ItemUpdated += OnCompleteUninstallGameItemUpdated;
                _completeUninstall.CheckPending(false);
            }
            catch (Exception ex) { logger.Warn("Complete uninstall init failed: " + ex.Message); }

            // Install size for games without one (added by hand, emulated, launcher gave none).
            if (IsDesktopMode)
            try
            {
                _installSize = new InstallSizeFiller(playniteAPI, logger);
                playniteAPI.Database.Games.ItemUpdated += OnInstallSizeGameItemUpdated;
            }
            catch (Exception ex) { logger.Warn("Install size init failed: " + ex.Message); }
        }

        private void OnInstallSizeGameItemUpdated(object sender, ItemUpdatedEventArgs<Game> e)
        {
            try
            {
                if (e?.UpdatedItems == null || _installSize == null) return;
                foreach (var u in e.UpdatedItems)
                {
                    if (u?.NewData != null && u.NewData.IsInstalled && (u.OldData == null || !u.OldData.IsInstalled))
                        _installSize.Queue(u.NewData.Id);
                }
            }
            catch { }
        }

        private void WriteCrashTrace(string kind, string text, bool terminating)
        {
            try
            {
                if (string.IsNullOrEmpty(text) ||
                    text.IndexOf("PlayniteGameOverlay", StringComparison.OrdinalIgnoreCase) < 0)
                    return; // not ours
                var dir = GetPluginUserDataPath();
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash-trace.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind} (terminating={terminating}){Environment.NewLine}" +
                    text + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            WriteCrashTrace("Unhandled exception", (e?.ExceptionObject as Exception)?.ToString() ?? e?.ExceptionObject?.ToString(), e?.IsTerminating ?? false);
        }

        private void OnUnobservedTaskException(object sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
        {
            WriteCrashTrace("Unobserved task exception", e?.Exception?.ToString(), false);
        }

        private void OnCompleteUninstallGameItemUpdated(object sender, ItemUpdatedEventArgs<Game> e)
        {
            try
            {
                if (e?.UpdatedItems == null) return;
                _completeUninstall?.OnGamesUpdated(e.UpdatedItems.Select(u => u?.NewData));
            }
            catch (Exception ex) { logger.Warn("Complete uninstall update hook failed: " + ex.Message); }
        }

        private void DispatchOverlayNav(Action action)
        {
            if (action == null) return;
            try
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(action);
            }
            catch { /* ignore */ }
        }

        /// <summary>
        /// When a dialog / settings window opens while the pad was the last thing used, select its first
        /// control right away so the outline is visible and the first press already navigates.
        /// </summary>
        private void StartPadWindowWatch()
        {
            try
            {
                _padWindowWatch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _padWindowWatch.Tick += (s, e) =>
                {
                    try
                    {
                        System.Windows.Window w = null;
                        StallProbe.Time("GetPadSettingsWindow(watch)", () => { w = GetPadSettingsWindow(); });
                        if (w == null) { _padInitWindow = null; return; }
                        if (ReferenceEquals(w, _padInitWindow)) return;
                        if ((DateTime.UtcNow - _lastPadActivityUtc).TotalSeconds > 8) return;   // opened with mouse/keyboard
                        if (!w.IsLoaded) return;                                                  // try again next tick
                        _padInitWindow = w;
                        SettingsPadNav.SelectInitial(w);
                    }
                    catch { }
                };
                _padWindowWatch.Start();
            }
            catch { }
        }

        private string _lastPadRouteLog;
        // The dialog the pad last drove. After an alt-tab Windows can hand the focus to the library window while that
        // dialog is still open; the pad then silently drove the game list. See GetPadSettingsWindow.
        private System.Windows.Window _lastPadDialog;
        private DateTime _lastPadDialogUtc = DateTime.MinValue;

        /// <summary>Writes a routing decision to breadcrumbs.log, only when it changes (the pad polls at 60 Hz).</summary>
        private void LogPadRoute(string message)
        {
            if (message == _lastPadRouteLog) return;
            _lastPadRouteLog = message;
            Breadcrumb.Write("PadRoute: " + message);
        }

        /// <summary>True when an XInput pad is actually connected, so the XInput poller already handles the input.</summary>
        private bool XInputOwnsPad() =>
            xInputPoller != null && xInputPoller.IsAvailable && xInputPoller.AnyConnected;

        private static class PadNative
        {
            [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr hWnd);
            [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
            [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);
            public const uint GW_OWNER = 4;
        }

        /// <summary>
        /// The window the gamepad should drive with window-style navigation, when it is the focused window:
        /// this extension's global / per-game settings windows, or (Playnite Desktop only) any other
        /// secondary Playnite window - Playnite's own settings, add-ons, edit game, message boxes, etc.
        /// In Fullscreen mode the same applies to windows Playnite does not drive itself (an extension's window,
        /// e.g. FPS Limiter's settings): Playnite's own Fullscreen windows (Playnite.FullscreenApp.*) keep their
        /// native controller handling and are never taken over here.
        /// Returns null for the main library window and the overlay.
        /// </summary>
        private System.Windows.Window GetPadSettingsWindow()
        {
            try
            {
                IntPtr fg = WindowHelper.GetForegroundWindow();
                if (fg == IntPtr.Zero) return null;
                foreach (var w in new[] { _openGlobalSettingsWindow, _openPerGameSettingsWindow })
                {
                    if (w != null && w.IsVisible &&
                        new System.Windows.Interop.WindowInteropHelper(w).Handle == fg)
                    {
                        LogPadRoute("own settings window " + w.GetType().Name + " \"" + w.Title + "\"");
                        return w;
                    }
                }

                // Other Playnite Desktop windows (dialogs and menus-as-windows).
                if (Settings != null && !Settings.EnableDesktopControllerNav) { LogPadRoute("skip: desktop controller nav disabled in settings"); return null; }
                if (IsOverlayNavActive()) return null;
                // Desktop: Playnite Desktop must be in the foreground. Fullscreen: this (Fullscreen) process must be,
                // so a game or another app in front never gets its pad input taken.
                bool fullscreenMode = !IsDesktopMode;
                if (fullscreenMode ? !IsOwnProcessWindow(fg) : !DesktopControllerNav.IsPlayniteDesktopForeground())
                {
                    LogPadRoute("skip: foreground is not Playnite " + (fullscreenMode ? "Fullscreen" : "Desktop") + " - " + DescribeForeground(fg));
                    return null;
                }

                var app = System.Windows.Application.Current;
                if (app == null) return null;
                var main = app.MainWindow;
                // A stale MainWindow (splash screen, closed window) must not be trusted.
                if (main != null && !main.IsVisible) main = null;
                var ownAssembly = GetType().Assembly;

                // Snapshot of Playnite's visible windows. A modal dialog (Playnite settings, edit game ...)
                // disables the windows below it at the Win32 level, which identifies it even when the
                // dialog has no Owner and Application.MainWindow is not the library window.
                var windows = new List<System.Windows.Window>();
                foreach (System.Windows.Window w in app.Windows)
                    if (w != null && w.IsVisible) windows.Add(w);

                bool someWindowDisabled = false;
                foreach (var w in windows)
                {
                    var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                    if (h != IntPtr.Zero && !PadNative.IsWindowEnabled(h)) { someWindowDisabled = true; break; }   // includes the foreground window: after alt-tab it can be the disabled library window
                }

                var seen = new System.Text.StringBuilder();
                foreach (var w in windows)
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                    bool isFg = hwnd == fg;
                    bool owned = w.Owner != null || (hwnd != IntPtr.Zero && PadNative.GetWindow(hwnd, PadNative.GW_OWNER) != IntPtr.Zero);
                    seen.Append(w.GetType().Name).Append(isFg ? "*" : "").Append(ReferenceEquals(w, main) ? "(main)" : "")
                        .Append(owned ? "(owned)" : "").Append("; ");
                    if (!isFg) continue;
                    if (ReferenceEquals(w, main) || ReferenceEquals(w, overlayWindow)) continue;
                    if (w.GetType().Assembly == ownAssembly) continue;          // our own windows handle themselves
                    if (fullscreenMode && IsNativeFullscreenWindow(w)) continue; // Playnite drives its own Fullscreen windows
                    // With a known library window, any other focused window is a dialog. Without one, the library
                    // window is recognised by having no owner and nothing disabled beneath it.
                    if (main == null && !owned && !someWindowDisabled) continue;
                    LogPadRoute("dialog window " + w.GetType().FullName + " \"" + w.Title + "\"");
                    _lastPadDialog = w; _lastPadDialogUtc = DateTime.UtcNow;
                    return w;
                }

                // A modal window (Playnite settings, add-ons, an extension's window ...) is open - the library window below
                // it is disabled - but the focus sits on something of Playnite's that is not a window (a closed dropdown,
                // a popup, a tooltip). The pad used to fall through to the game list. Bring the dialog back and keep
                // driving it. Only while the pad is being used, and only when Playnite itself holds the focus.
                // A dialog the pad was driving a moment ago that is still open (e.g. not modal, or after an alt-tab) also
                // keeps the pad: it ends when the dialog is closed (B closes it).
                bool lastDialogOpen = _lastPadDialog != null && _lastPadDialog.IsVisible && !ReferenceEquals(_lastPadDialog, main) &&
                    _lastPadDialog.WindowState != System.Windows.WindowState.Minimized &&
                    (DateTime.UtcNow - _lastPadDialogUtc).TotalSeconds < 120;
                if (!lastDialogOpen) _lastPadDialog = null;
                if ((someWindowDisabled || lastDialogOpen) && (DateTime.UtcNow - _lastPadActivityUtc).TotalSeconds < 3)
                {
                    System.Windows.Window modal = null;
                    foreach (var w in windows)
                    {
                        if (ReferenceEquals(w, main) || ReferenceEquals(w, overlayWindow)) continue;
                        if (w.GetType().Assembly == ownAssembly) continue;
                        if (fullscreenMode && IsNativeFullscreenWindow(w)) continue;
                        if (w.WindowState == System.Windows.WindowState.Minimized) continue;
                        var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                        if (h == IntPtr.Zero || !PadNative.IsWindowEnabled(h)) continue;
                        modal = w;   // the newest enabled window is the top-most dialog
                    }
                    if (lastDialogOpen && (modal == null || !someWindowDisabled)) modal = _lastPadDialog;
                    if (modal != null)
                    {
                        _lastPadDialog = modal; _lastPadDialogUtc = DateTime.UtcNow;
                        try { modal.Activate(); modal.Focus(); } catch { }
                        LogPadRoute("refocused modal window " + modal.GetType().Name + " \"" + modal.Title + "\" (focus was on " + DescribeForeground(fg) + ")");
                        return modal;
                    }
                }
                LogPadRoute("library/main (no dialog matched). windows: " + seen);
            }
            catch (Exception ex) { LogPadRoute("error: " + ex.Message); }
            return null;
        }

        /// <summary>True when the window belongs to this process (Playnite Fullscreen, when running in that mode).</summary>
        private static bool IsOwnProcessWindow(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return false;
                uint pid;
                PadNative.GetWindowThreadProcessId(hwnd, out pid);
                return pid != 0 && (int)pid == _ownPid;
            }
            catch { return false; }
        }

        /// <summary>
        /// Playnite's own Fullscreen windows (main view, menus, message boxes ...) are built for controllers and handle the
        /// pad themselves; driving them as well would move every press twice. Everything else (an extension's window) has no
        /// pad support of its own and is handed to SettingsPadNav, as in Desktop mode.
        /// </summary>
        private static bool IsNativeFullscreenWindow(System.Windows.Window w)
        {
            try
            {
                var name = w?.GetType().FullName;
                return name != null && name.StartsWith("Playnite.FullscreenApp.", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        /// <summary>SDL / Playnite controller event -> window navigation (Fullscreen backup path when no XInput pad is present).</summary>
        private static bool RoutePadToWindow(ControllerInput? press, System.Windows.Window w)
        {
            switch (press)
            {
                case ControllerInput.DPadUp:
                case ControllerInput.LeftStickUp:
                    SettingsPadNav.Move(w, System.Windows.Input.FocusNavigationDirection.Up); return true;
                case ControllerInput.DPadDown:
                case ControllerInput.LeftStickDown:
                    SettingsPadNav.Move(w, System.Windows.Input.FocusNavigationDirection.Down); return true;
                case ControllerInput.DPadLeft:
                case ControllerInput.LeftStickLeft:
                    SettingsPadNav.Move(w, System.Windows.Input.FocusNavigationDirection.Left); return true;
                case ControllerInput.DPadRight:
                case ControllerInput.LeftStickRight:
                    SettingsPadNav.Move(w, System.Windows.Input.FocusNavigationDirection.Right); return true;
                case ControllerInput.A: SettingsPadNav.Confirm(w); return true;
                case ControllerInput.B: SettingsPadNav.Back(w); return true;
                case ControllerInput.LeftShoulder: SettingsPadNav.Section(w, -1); return true;
                case ControllerInput.RightShoulder: SettingsPadNav.Section(w, 1); return true;
            }
            return false;
        }

        /// <summary>Process, class and title of a window, for the routing log (who took the focus away).</summary>
        private static string DescribeForeground(IntPtr hwnd)
        {
            try
            {
                uint pid;
                PadNative.GetWindowThreadProcessId(hwnd, out pid);
                string proc = "?";
                try { using (var p = System.Diagnostics.Process.GetProcessById((int)pid)) proc = p.ProcessName; } catch { }
                var cls = new System.Text.StringBuilder(128);
                var title = new System.Text.StringBuilder(128);
                PadNative.GetClassName(hwnd, cls, cls.Capacity);
                PadNative.GetWindowText(hwnd, title, title.Capacity);
                return "process=" + proc + " class=" + cls + " title=\"" + title + "\"";
            }
            catch { return "unknown window"; }
        }

        /// <summary>Runs a pad action on the focused settings window. False = no settings window has focus.</summary>
        private bool TryPadSettings(Action<System.Windows.Window> action)
        {
            var w = GetPadSettingsWindow();
            if (w == null) return false;
            try { action(w); } catch { }
            return true;
        }

        private bool IsOverlayNavActive() =>
            overlayWindow != null && overlayWindow.IsVisible && overlayWindow.Active && !overlayWindow.InputPaused;

        /// <summary>
        /// Overlay open → overlay focus move. Else if Playnite Desktop is focused → arrow key.
        /// </summary>
        private void HandlePadDirection(Action desktopKey, Action overlayAction,
            System.Windows.Input.FocusNavigationDirection direction = System.Windows.Input.FocusNavigationDirection.Down)
        {
            if (TryPadSettings(w => SettingsPadNav.Move(w, direction))) return;
            if (IsOverlayNavActive())
            {
                DispatchOverlayNav(overlayAction);
                return;
            }
            // null Settings → allow; only skip when explicitly disabled
            if (Settings != null && !Settings.EnableDesktopControllerNav)
                return;
            if (!DesktopControllerNav.IsPlayniteDesktopForeground())
                return;
            try
            {
                DesktopControllerNav.EnsureLibraryFocus();
                desktopKey?.Invoke();
            }
            catch { }
        }

        /// <summary>Square / X in Playnite Desktop: collapse or expand the selected game's group.</summary>
        /// <summary>Start (on its own): open / close Playnite's main menu in Desktop mode.</summary>
        private void HandlePadStart()
        {
            if (TryPadSettings(w => { })) return;       // a settings window owns the pad
            if (IsOverlayNavActive()) return;            // the in-game overlay does not use Start on its own
            if (Settings != null && !Settings.EnableDesktopControllerNav)
                return;
            if (!IsDesktopMode) return;                  // Fullscreen mode has its own menu button
            if (!DesktopControllerNav.IsPlayniteDesktopForeground())
                return;
            try { DesktopControllerNav.ToggleMainMenu(); }
            catch { }
        }

        private void HandlePadSquare()
        {
            if (TryPadSettings(w => { })) return;       // a settings window owns the pad
            if (IsOverlayNavActive()) return;            // the in-game overlay does not use Square
            if (Settings != null && !Settings.EnableDesktopControllerNav)
                return;
            if (!DesktopControllerNav.IsPlayniteDesktopForeground())
                return;
            try
            {
                DesktopControllerNav.EnsureLibraryFocus();
                DesktopControllerNav.ToggleGroup();
            }
            catch { }
        }

        private void HandlePadConfirm()
        {
            if (TryPadSettings(w => SettingsPadNav.Confirm(w))) return;
            if (IsOverlayNavActive())
            {
                DispatchOverlayNav(() => overlayWindow?.ClickFocusedElement());
                return;
            }
            if (Settings != null && !Settings.EnableDesktopControllerNav)
                return;
            if (!DesktopControllerNav.IsPlayniteDesktopForeground())
                return;
            try
            {
                DesktopControllerNav.EnsureLibraryFocus();
                DesktopControllerNav.Confirm();
            }
            catch { }
        }

        /// <summary>LB / RB with the overlay closed: step through the Playnite Desktop top bar icons.</summary>
        private void HandlePadTopBar(int delta)
        {
            if (IsOverlayNavActive()) return;
            if (Settings != null && !Settings.EnableDesktopControllerNav)
                return;
            if (!DesktopControllerNav.IsPlayniteDesktopForeground())
                return;
            try { DesktopControllerNav.TopBarStep(delta); } catch { }
        }

        /// <summary>LT / RT with the overlay closed: jump between the top bar's left and right icon groups.</summary>
        private void HandlePadTopBarJump(int direction)
        {
            if (IsOverlayNavActive()) return;
            if (Settings != null && !Settings.EnableDesktopControllerNav)
                return;
            if (!DesktopControllerNav.IsPlayniteDesktopForeground())
                return;
            try { DesktopControllerNav.TopBarJump(direction); } catch { }
        }

        private void HandlePadBack()
        {
            if (TryPadSettings(w => SettingsPadNav.Back(w))) return;
            if (IsOverlayNavActive())
            {
                DispatchOverlayNav(() =>
                {
                    if (overlayWindow == null) return;
                    if (!overlayWindow.TryCloseAchievementBrowser())
                        overlayWindow.Hide();
                });
                return;
            }
            if (Settings != null && !Settings.EnableDesktopControllerNav)
            {
                Breadcrumb.Write("PadB ignored: desktop controller navigation is disabled in settings");
                return;
            }
            if (!DesktopControllerNav.IsPlayniteDesktopForeground())
            {
                Breadcrumb.Write("PadB ignored: Playnite Desktop is not the foreground window");
                return;
            }
            try { DesktopControllerNav.Back(); } catch { }
        }

        public void ReloadOverlay(OverlaySettings settings)
        {
            overlayWindow.Close(); //close old window
            overlayWindow = new OverlayWindow(settings != null ? settings : Settings); //open new one
            overlayWindow.SaveSettingsCallback = () =>
            {
                try { SaveSettingsSafely(Settings); }
                catch { /* ignore */ }
            };
            WireOverlayFpsIntegration(overlayWindow);
            overlayWindow.Prewarm();
            overlayWindow.Hide();
            if (GameOverlayData != null)
            {
                overlayWindow.UpdateGameOverlay(GameOverlayData);
            }

            // Set up show Playnite handler again
            overlayWindow.OnShowPlayniteRequested += ShowPlaynite;
        }

        /// <summary>
        /// Seed the overlay FPS panel from saved settings and persist changes back
        /// so this extension fully replaces a separate FPS Limiter install for overlay use.
        /// </summary>
        private void WireOverlayFpsIntegration(OverlayWindow window)
        {
            if (window?.ViewModel == null || Settings == null)
                return;

            // Prefer live values from FPS Limiter extension config for THIS game
            Guid? gameId = null;
            if (GameOverlayData != null && GameOverlayData.GameId != Guid.Empty)
                gameId = GameOverlayData.GameId;

            var snap = FpsLimiterSettingsSync.Read(playniteAPI, gameId);
            if (snap.Found || snap.UseSpecialK || snap.DefaultBackend == 2)
            {
                if (snap.Found)
                {
                    window.ViewModel.SelectedFpsLimit = snap.Enabled ? snap.FrameLimit : 0;
                    window.ViewModel.SelectedSyncMode = snap.SyncMode;
                    window.ViewModel.ReflexMarkersEnabled = snap.ReflexMarkersEnabled;
                    Settings.FpsLimit = window.ViewModel.SelectedFpsLimit;
                    Settings.FpsSyncMode = (int)window.ViewModel.SelectedSyncMode;
                    Settings.FpsReflexMarkers = window.ViewModel.ReflexMarkersEnabled;
                }
                else
                {
                    window.ViewModel.SelectedFpsLimit = Settings.FpsLimit;
                    window.ViewModel.SelectedSyncMode = (OverlayFpsSyncMode)Settings.FpsSyncMode;
                    window.ViewModel.ReflexMarkersEnabled = Settings.FpsReflexMarkers;
                }

                // Backend from config/session first (matches extension UI for this game)
                window.ViewModel.SelectedFpsBackend = snap.UseSpecialK
                    ? OverlayFpsBackend.SpecialK
                    : OverlayFpsBackend.Rtss;
            }
            else
            {
                window.ViewModel.SelectedFpsLimit = Settings.FpsLimit;
                window.ViewModel.SelectedSyncMode = (OverlayFpsSyncMode)Settings.FpsSyncMode;
                window.ViewModel.ReflexMarkersEnabled = Settings.FpsReflexMarkers;
            }

            // Live effective sync (resolves UseGlobal → ScanlineSync etc.)
            try
            {
                if (gameId.HasValue && FpsLimiterBridge.IsAvailable(playniteAPI))
                {
                    var g = playniteAPI.Database.Games.Get(gameId.Value);
                    if (g != null)
                    {
                        var eff = FpsLimiterBridge.GetEffectiveGameSyncMode(playniteAPI, g);
                        if (eff.HasValue && eff.Value >= 0 && eff.Value <= 4)
                            window.ViewModel.SelectedSyncMode = (OverlayFpsSyncMode)eff.Value;
                    }
                }
            }
            catch { }

            // Scanline Sync target: prefer the live FPS Limiter setting, otherwise use overlay setting.
            OverlayRtssController.ScanlineSyncTarget = Settings.FpsScanlineSyncTarget;
            try
            {
                var liveScanline = FpsLimiterBridge.GetRtssScanlineSyncTarget(playniteAPI);
                if (liveScanline.HasValue)
                {
                    Settings.FpsScanlineSyncTarget = liveScanline.Value;
                    OverlayRtssController.ScanlineSyncTarget = liveScanline.Value;
                }
            }
            catch { }
            window.ViewModel.SetScanlineTargetFromBridge(Settings.FpsScanlineSyncTarget);

            // Keep the overlay's visible slider in sync if the FPS Limiter UI changes the shared setting.
            Action<int> scanlineTargetChangedHandler = value =>
            {
                try
                {
                    window.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (Settings.FpsScanlineSyncTarget != value)
                        {
                            Settings.FpsScanlineSyncTarget = value;
                            SaveSettingsSafely(Settings);
                        }
                        OverlayRtssController.ScanlineSyncTarget = value;
                        window.ViewModel?.SetScanlineTargetFromBridge(value);
                    }));
                }
                catch (Exception ex) { logger.Error(ex, "Failed to sync Scanline target into overlay UI."); }
            };
            FpsLimiterBridge.ScanlineTargetChanged += scanlineTargetChangedHandler;
            window.Closed += (_, __) => FpsLimiterBridge.ScanlineTargetChanged -= scanlineTargetChangedHandler;

            window.ViewModel.RtssAvailable = OverlayRtssController.IsRtssAvailable();
            bool fpsExt = FpsLimiterSettingsSync.IsFpsLimiterInstalled(playniteAPI);
            if (!window.ViewModel.RtssAvailable)
                window.ViewModel.FpsStatusText = "RTSS not found — install RivaTuner";
            else if (fpsExt)
                window.ViewModel.FpsStatusText = "Synced with FPS Limiter extension";
            else
                window.ViewModel.FpsStatusText = "RTSS ready (install FPS Limiter to sync menus)";
            window.ViewModel.FpsSettingsChanged += (limit, sync, reflex) =>
            {
                // limit path also fires FpsActionRequested; keep for backward compat
            };

            window.ViewModel.FpsActionRequested += (action, payload) =>
            {
                try
                {
                    switch (action)
                    {
                        case "limit":
                            double lim = payload is double d ? d : Convert.ToDouble(payload);
                            ApplyFpsViaLimiter(lim, window.ViewModel.SelectedSyncMode,
                                window.ViewModel.ReflexMarkersEnabled, disableCap: lim <= 0, toggleVrr: false);
                            break;
                        case "disable":
                            ApplyFpsViaLimiter(0, window.ViewModel.SelectedSyncMode,
                                window.ViewModel.ReflexMarkersEnabled, disableCap: true, toggleVrr: false);
                            break;
                        case "sync":
                            {
                                int sync = payload is int si ? si : Convert.ToInt32(payload);
                                if (sync < 0) sync = 0;
                                if (sync > 5) sync = 0;
                                window.ViewModel.SelectedSyncMode = (OverlayFpsSyncMode)sync;
                                Game g = null;
                                if (GameOverlayData != null && GameOverlayData.GameId != Guid.Empty)
                                    g = playniteAPI.Database.Games.Get(GameOverlayData.GameId);
                                // Sync changes must NOT reinterpret the old FPS cap as a
                                // display Hz. Only the rate buttons request a forced Hz.
                                if (FpsLimiterBridge.IsAvailable(playniteAPI) && g != null)
                                {
                                    if (!FpsLimiterBridge.SetGameSyncMode(playniteAPI, g, sync))
                                        logger.Error($"Overlay sync change failed for {g.Name}: {sync}");
                                    var currentCap = FpsLimiterBridge.GetGameLimit(playniteAPI, g);
                                    if (currentCap.HasValue)
                                        window.ViewModel.SelectedFpsLimit = currentCap.Value;
                                }
                                else
                                {
                                    ApplyFpsViaLimiter(window.ViewModel.SelectedFpsLimit,
                                        (OverlayFpsSyncMode)sync,
                                        window.ViewModel.ReflexMarkersEnabled,
                                        disableCap: window.ViewModel.SelectedFpsLimit <= 0,
                                        toggleVrr: false);
                                }
                                break;
                            }
                        case "scanlineTarget":
                            {
                                int target = payload is int ti ? ti : Convert.ToInt32(payload);
                                target = Math.Max(-500, Math.Min(500, target));
                                Settings.FpsScanlineSyncTarget = target;
                                OverlayRtssController.ScanlineSyncTarget = target;
                                SaveSettingsSafely(Settings);

                                Game g = null;
                                if (GameOverlayData != null && GameOverlayData.GameId != Guid.Empty)
                                    g = playniteAPI.Database.Games.Get(GameOverlayData.GameId);

                                bool bridgeAvailable = FpsLimiterBridge.IsAvailable(playniteAPI);
                                bool bridgeOk = bridgeAvailable && FpsLimiterBridge.SetRtssScanlineSyncTarget(playniteAPI, g, target);

                                bool scanlineActive = window.ViewModel.SelectedSyncMode == OverlayFpsSyncMode.ScanlineSync ||
                                    window.ViewModel.SelectedSyncMode == OverlayFpsSyncMode.ScanlineSyncHalf;
                                if (bridgeAvailable)
                                {
                                    // FPS Limiter is the source of truth. It owns the setting, RTSS profile
                                    // update, active-session snapshot, and notification to both UIs.
                                    window.ViewModel.FpsStatusText = bridgeOk
                                        ? (scanlineActive ? $"Scanline target {target} synced with FPS Limiter" : $"Scanline target {target} saved in FPS Limiter")
                                        : "Could not sync Scanline target with FPS Limiter";
                                }
                                else if (scanlineActive)
                                {
                                    // Standalone fallback only when FPS Limiter isn't installed.
                                    try
                                    {
                                        var profiles = OverlayRtssController.ApplyScanlineTarget(GameOverlayData?.ExecutablePath, target);
                                        window.ViewModel.FpsStatusText = $"Scanline target {target} applied to RTSS ({profiles})";
                                    }
                                    catch (Exception ex)
                                    {
                                        logger.Error(ex, $"Standalone overlay Scanline target update failed for {target}.");
                                        window.ViewModel.FpsStatusText = $"Scanline target update failed: {ex.Message}";
                                    }
                                }
                                else
                                {
                                    window.ViewModel.FpsStatusText = $"Scanline target {target} saved in overlay";
                                }
                                break;
                            }
                        case "reflex":
                            {
                                bool reflex = payload is bool b && b;
                                window.ViewModel.ReflexMarkersEnabled = reflex;
                                Game g = null;
                                if (GameOverlayData != null && GameOverlayData.GameId != Guid.Empty)
                                    g = playniteAPI.Database.Games.Get(GameOverlayData.GameId);
                                if (FpsLimiterBridge.IsAvailable(playniteAPI))
                                {
                                    if (g != null)
                                        FpsLimiterBridge.SetGameReflexMarkers(playniteAPI, g, reflex);
                                    else
                                        FpsLimiterBridge.SetGlobalReflexMarkers(playniteAPI, reflex ? 0 : 1);
                                }
                                else
                                    ApplyFpsViaLimiter(window.ViewModel.SelectedFpsLimit, window.ViewModel.SelectedSyncMode,
                                        reflex, false, false);
                                break;
                            }
                        case "vrr":
                            ApplyFpsViaLimiter(window.ViewModel.SelectedFpsLimit, window.ViewModel.SelectedSyncMode,
                                window.ViewModel.ReflexMarkersEnabled, disableCap: false, toggleVrr: true);
                            break;
                        case "backend":
                            {
                                // payload: "SpecialK" or "Rtss" (or int 2 / 1)
                                int pref = 1; // Rtss
                                if (payload is int pi)
                                    pref = pi;
                                else if (payload != null)
                                {
                                    var s = payload.ToString();
                                    if (s.IndexOf("Special", StringComparison.OrdinalIgnoreCase) >= 0 || s == "2")
                                        pref = 2; // SpecialK
                                    else if (s.IndexOf("Auto", StringComparison.OrdinalIgnoreCase) >= 0 || s == "0")
                                        pref = 0;
                                    else
                                        pref = 1;
                                }
                                Game g = null;
                                if (GameOverlayData != null && GameOverlayData.GameId != Guid.Empty)
                                    g = playniteAPI.Database.Games.Get(GameOverlayData.GameId);
                                if (FpsLimiterBridge.IsAvailable(playniteAPI))
                                {
                                    if (g != null)
                                        FpsLimiterBridge.SetGameBackendPreference(playniteAPI, g, pref);
                                    else
                                        FpsLimiterBridge.SetDefaultBackend(playniteAPI, pref == 0 ? 0 : pref);
                                    // Refresh UI from effective backend
                                    bool useSk = FpsLimiterBridge.ShouldUseSpecialKBackend(playniteAPI, g);
                                    window.ViewModel.SelectedFpsBackend = useSk
                                        ? OverlayFpsBackend.SpecialK
                                        : OverlayFpsBackend.Rtss;
                                    window.ViewModel.FpsStatusText =
                                        "Backend: " + FpsLimiterBridge.GetEffectiveBackendLabel(playniteAPI, g);
                                    // Re-apply current limit with new backend
                                    ApplyFpsViaLimiter(window.ViewModel.SelectedFpsLimit, window.ViewModel.SelectedSyncMode,
                                        window.ViewModel.ReflexMarkersEnabled, disableCap: false, toggleVrr: false);
                                }
                                else
                                {
                                    window.ViewModel.SelectedFpsBackend = pref == 2
                                        ? OverlayFpsBackend.SpecialK
                                        : OverlayFpsBackend.Rtss;
                                    window.ViewModel.FpsStatusText = "FPS Limiter not connected — backend is local UI only";
                                }
                                break;
                            }
                    }
                }
                catch (Exception ex)
                {
                    logger.Error($"FpsActionRequested failed: {ex.Message}");
                    window.ViewModel.FpsStatusText = "FPS Limiter action failed: " + ex.Message;
                }
            };

            // Load presets + refresh rates from FPS Limiter (same lists as extension)
            try
            {
                Game g0 = null;
                if (gameId.HasValue)
                    g0 = playniteAPI.Database.Games.Get(gameId.Value);

                window.ViewModel.FixedRefreshDisplayMode = FpsLimiterBridge.IsFixedRefreshDisplay(playniteAPI);
                var presets = FpsLimiterBridge.GetPresets(playniteAPI, g0);
                window.ViewModel.LoadPresets(presets);
                var rates = FpsLimiterBridge.GetSupportedRefreshRates(playniteAPI);
                if (rates != null && rates.Count > 0)
                    window.ViewModel.SetRefreshRates(rates);

                // Prefer live bridge when available; keep config snap as fallback
                if (FpsLimiterBridge.IsAvailable(playniteAPI))
                {
                    bool useSk = FpsLimiterBridge.ShouldUseSpecialKBackend(playniteAPI, g0);
                    // If bridge says SK, trust it. If bridge says RTSS but config says SK, keep SK
                    // (SK path resolve can fail transiently while profile still targets Special K).
                    if (useSk || snap.UseSpecialK)
                        window.ViewModel.SelectedFpsBackend = OverlayFpsBackend.SpecialK;
                    else
                        window.ViewModel.SelectedFpsBackend = OverlayFpsBackend.Rtss;

                    window.ViewModel.FpsStatusText =
                        "Backend: " + FpsLimiterBridge.GetEffectiveBackendLabel(playniteAPI, g0) +
                        (snap.UseSpecialK && !useSk ? " (config)" : "");
                }
                else if (snap.UseSpecialK)
                {
                    window.ViewModel.SelectedFpsBackend = OverlayFpsBackend.SpecialK;
                }
            }
            catch { }

            // Status
            if (FpsLimiterBridge.IsAvailable(playniteAPI) && string.IsNullOrEmpty(window.ViewModel.FpsStatusText))
                window.ViewModel.FpsStatusText = "FPS Limiter connected — same apply path as extension";
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            Breadcrumb.Write("OnApplicationStopped begin");
            // Put everything we changed back before Playnite goes away.
            try { WindowHelper.StopBorderlessWatch(); } catch { }
            try { WindowHelper.RestoreFakeFullscreen(FakeFullscreenBackupPath()); } catch { }
            try { _unmuteRetryTimer?.Stop(); } catch { }
            try { OverlayWindowViewModel.GameMuteApplied -= OnOverlayGameMuteApplied; } catch { }
            try { UnmuteGameIfMutedByOverlay("app stopping"); } catch { }
            try
            {
                // Never leave a game frozen when Playnite exits.
                if (WindowHelper.IsParked) WindowHelper.ResumeAndRestore();
                else if (WindowHelper.IsSoftPausedForOverlay) WindowHelper.SoftResumeFromOverlay();
                else WindowHelper.ResumeGameProcess();
            }
            catch { try { WindowHelper.ForceClearAllSuspendState(); } catch { } }
            _gameBananaStopping = true;
            try { _deferredLinkTimer?.Stop(); } catch { }
            try { playniteAPI.Database.Games.ItemCollectionChanged -= OnGamesCollectionChangedForLinks; } catch { }
            try { playniteAPI.Database.Games.ItemCollectionChanged -= OnGamesCollectionChangedForLocalStore; } catch { }
            try { playniteAPI.Database.Games.ItemUpdated -= OnLocalStoreGameItemUpdated; } catch { }
            try { playniteAPI.Database.Games.ItemUpdated -= OnInstantPlayGameItemUpdated; } catch { }
            try { playniteAPI.Database.Games.ItemUpdated -= OnCompleteUninstallGameItemUpdated; } catch { }
            try { playniteAPI.Database.Games.ItemUpdated -= OnInstallSizeGameItemUpdated; } catch { }
            try { AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException; } catch { }
            try { System.Threading.Tasks.TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException; } catch { }
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
            try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
            try { gameHotkeyPidTimer?.Stop(); } catch { }
            try
            {
                if (_masterVolumeTrackedGameFocused)
                    SaveMasterVolumeProfile(_masterVolumeTrackedGameId);
                masterVolumeFocusTimer?.Stop();
            }
            catch { }
            PreserveCurrentSettings();
            try { _padWindowWatch?.Stop(); } catch { }
            try { _linkRetryTimer?.Stop(); } catch { }
            try { if (xInputPoller != null) xInputPoller.SonyPad = null; } catch { }
            try { sonyHidPoller?.Dispose(); } catch { }
            sonyHidPoller = null;
            try { if (xInputPoller != null && controllerHotkeys != null) xInputPoller.MaskPolled -= controllerHotkeys.Update; } catch { }
            controllerHotkeys = null;
            try { xInputPoller?.Stop(); } catch { }
            try { xInputPoller?.Dispose(); } catch { }
            try { cursorAutoHide?.Dispose(); } catch { }
            xInputPoller = null;
            logger.Info("Stopping Overlay...");

            // Cleanup resources
            overlayWindow?.Close();
            keyboardHook?.Dispose();
            Breadcrumb.Write("OnApplicationStopped end");
            Breadcrumb.Flush();
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            // NtSuspendProcess across sleep/hibernate often kills the game on wake
            // (GPU reset while threads are frozen). Unfreeze before the OS sleeps.
            if (e.Mode == PowerModes.Suspend)
            {
                try
                {
                    log("PowerMode.Suspend: releasing game freeze before sleep", "INFO");
                    try
                    {
                        if (overlayWindow != null && overlayWindow.IsVisible)
                            overlayWindow.Hide();
                    }
                    catch { }
                    try
                    {
                        if (WindowHelper.IsParked)
                            WindowHelper.ResumeAndRestore();
                        else if (WindowHelper.IsSoftPausedForOverlay)
                            WindowHelper.SoftResumeFromOverlay();
                        else
                            WindowHelper.ResumeGameProcess();
                    }
                    catch
                    {
                        try { WindowHelper.ForceClearAllSuspendState(); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    logger.Error($"Pre-sleep unfreeze failed: {ex.Message}");
                    try { WindowHelper.ForceClearAllSuspendState(); } catch { }
                }
                return;
            }

            if (e.Mode == PowerModes.Resume)
            {
                // Ensure no leftover freeze flags after wake
                try { WindowHelper.ForceClearAllSuspendState(); } catch { }
                RecoverAfterSleepOrIdle("PowerMode.Resume");
            }
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock ||
                e.Reason == SessionSwitchReason.ConsoleConnect)
                RecoverAfterSleepOrIdle("SessionSwitch." + e.Reason);
        }

        /// <summary>
        /// Re-install keyboard hook and restore system cursor after sleep / unlock.
        /// WH_KEYBOARD_LL is often dropped across power transitions; blank system
        /// cursors from CursorAutoHide can also stick until SPI_SETCURSORS.
        /// </summary>
        private void RecoverAfterSleepOrIdle(string reason)
        {
            try
            {
                log($"Recovering input after {reason}", "INFO");
                try { keyboardHook?.Reinstall(); } catch (Exception ex)
                {
                    logger.Error($"Keyboard hook reinstall failed: {ex.Message}");
                }
                try { cursorAutoHide?.ForceShow(); } catch { }
                try { WindowHelper.ReleaseMouseLock(nudgeCursor: false); } catch { }
            }
            catch (Exception ex)
            {
                logger.Error($"RecoverAfterSleepOrIdle failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Low-level keyboard hook callback. MUST return quickly — Windows silently
        /// removes WH_KEYBOARD_LL hooks that take too long. Only match the hotkey here;
        /// dispatch actual work to the UI thread.
        /// </summary>
        /// <summary>Resolve launcher -> game handoffs without blocking Windows' keyboard hook.</summary>
        private void RefreshGameHotkeyPid()
        {
            if (GameOverlayData == null || GameOverlayData.GameId == Guid.Empty)
            {
                cachedActualGamePid = -1;
                return;
            }
            try
            {
                var game = playniteAPI?.Database?.Games?.Get(GameOverlayData.GameId);
                if (game == null || !game.IsRunning)
                {
                    cachedActualGamePid = -1;
                    return;
                }
                if (System.Threading.Interlocked.CompareExchange(ref resolvingGameHotkeyPid, 1, 0) != 0)
                    return;
                int preferred = GameOverlayData.ProcessId;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var proc = FindRunningGameProcess(game, preferred > 0 ? (int?)preferred : null);
                        if (proc != null && !proc.HasExited)
                            System.Threading.Interlocked.Exchange(ref cachedActualGamePid, proc.Id);
                    }
                    catch { }
                    finally { System.Threading.Interlocked.Exchange(ref resolvingGameHotkeyPid, 0); }
                });
            }
            catch { /* Retain the last known valid game PID. */ }
        }

        private int _fgGameCachePid;
        private int _fgGameCacheTick;
        private bool _fgGameCacheResult;
        private int _hotkeyGamePid;

        /// <summary>Cheap pre-check: is this key one of the (enabled) volume / mute hotkeys while a game runs?</summary>
        private bool IsVolumeHotkey(Keys key, bool alt)
        {
            if (Settings == null || GameOverlayData == null || GameOverlayData.GameId == Guid.Empty)
                return false;
            bool shift = WindowHelper.IsKeyDownAsync(0x10);
            bool ctrl = WindowHelper.IsKeyDownAsync(0x11);
            return (!string.IsNullOrWhiteSpace(Settings.HotkeyVolumeUp) && HotkeyMatches(Settings.HotkeyVolumeUp, key, alt, shift, ctrl)) ||
                   (!string.IsNullOrWhiteSpace(Settings.HotkeyVolumeDown) && HotkeyMatches(Settings.HotkeyVolumeDown, key, alt, shift, ctrl)) ||
                   (!string.IsNullOrWhiteSpace(Settings.HotkeyVolumeMute) && HotkeyMatches(Settings.HotkeyVolumeMute, key, alt, shift, ctrl));
        }

        /// <summary>
        /// The focused process was started by Playnite (emulators, launcher-started games) or lives in the
        /// running game's folder. Cached for a few seconds so typing elsewhere stays cheap.
        /// </summary>
        private bool IsRunningGameForeground(int pid)
        {
            int now = Environment.TickCount;
            if (pid == _fgGameCachePid && unchecked(now - _fgGameCacheTick) < 3000)
                return _fgGameCacheResult;

            bool result = false;
            try
            {
                result = pid > 4 && !WindowHelper.IsProtectedProcess(pid) &&
                         (WindowHelper.IsDescendantOfCurrentProcess(pid) || IsProcessInActiveGameFolder(pid));
            }
            catch { }
            _fgGameCachePid = pid;
            _fgGameCacheTick = now;
            _fgGameCacheResult = result;
            if (result) _hotkeyGamePid = pid;
            return result;
        }

        private void TrackGameMasterVolumeFocus()
        {
            try
            {
                if (Settings?.UsesPerGameVolume == true)
                {
                    if (_masterVolumeTrackedGameId == Guid.Empty && !_masterVolumeTrackedGameFocused)
                        return;
                    _masterVolumeTrackedGameId = Guid.Empty;
                    _masterVolumeTrackedGameFocused = false;
                    System.Threading.Interlocked.Increment(ref _masterVolumeFocusVersion);
                    return;
                }
                var game = playniteAPI?.Database?.Games?.FirstOrDefault(g => g.IsRunning);
                if (game == null)
                {
                    if (_masterVolumeTrackedGameFocused)
                        SaveMasterVolumeProfile(_masterVolumeTrackedGameId);
                    _masterVolumeTrackedGameId = Guid.Empty;
                    _masterVolumeTrackedGameFocused = false;
                    System.Threading.Interlocked.Increment(ref _masterVolumeFocusVersion);
                    return;
                }

                if (_masterVolumeTrackedGameId != game.Id)
                {
                    if (_masterVolumeTrackedGameFocused)
                        SaveMasterVolumeProfile(_masterVolumeTrackedGameId);
                    _masterVolumeTrackedGameId = game.Id;
                    _masterVolumeTrackedGameFocused = false;
                    System.Threading.Interlocked.Increment(ref _masterVolumeFocusVersion);
                }

                bool focused = IsRunningGameOrOverlayForeground();
                if (focused == _masterVolumeTrackedGameFocused)
                    return;

                _masterVolumeTrackedGameFocused = focused;
                int version = System.Threading.Interlocked.Increment(ref _masterVolumeFocusVersion);
                if (focused)
                    RestoreMasterVolumeProfile(game.Id, version);
                else
                    SaveMasterVolumeProfile(game.Id);
            }
            catch { }
        }

        private static readonly int _ownPid = Process.GetCurrentProcess().Id;

        /// <summary>True when the foreground window belongs to Playnite (incl. the overlay) or to the running game.</summary>
        private bool IsCursorHideContext()
        {
            try
            {
                IntPtr hwnd = WindowHelper.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return false;
                WindowHelper.GetWindowThreadProcessId(hwnd, out uint fgPid);
                if (fgPid <= 4) return false;
                if ((int)fgPid == _ownPid) return true;
                return IsRunningGameForeground((int)fgPid);
            }
            catch { return false; }
        }

        private bool IsRunningGameOrOverlayForeground()
        {
            try
            {
                IntPtr hwnd = WindowHelper.GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                    return false;

                WindowHelper.GetWindowThreadProcessId(hwnd, out uint foregroundPid);
                if (foregroundPid > 4 && IsRunningGameForeground((int)foregroundPid))
                    return true;

                return overlayWindow != null && overlayWindow.IsVisible && overlayWindow.Active &&
                       WindowHelper.IsOwnProcessForeground();
            }
            catch { return false; }
        }

        private void SaveMasterVolumeProfile(Guid gameId)
        {
            if (gameId == Guid.Empty || Settings?.UsesPerGameVolume == true)
                return;
            try
            {
                if (GameAudioSession.TryGetMasterVolume(out float volume, out _))
                    MasterVolumeProfiles.Save(gameId, volume);
            }
            catch { }
        }

        private void RestoreMasterVolumeProfile(Guid gameId, int version)
        {
            if (gameId == Guid.Empty || Settings?.UsesPerGameVolume == true)
                return;
            System.Threading.ThreadPool.QueueUserWorkItem(workerState =>
            {
                if (version != System.Threading.Interlocked.CompareExchange(ref _masterVolumeFocusVersion, 0, 0))
                    return;

                if (MasterVolumeProfiles.TryGet(gameId, out float saved))
                    GameAudioSession.TrySetMasterVolume(saved);
                else if (GameAudioSession.TryGetMasterVolume(out float current, out _))
                    MasterVolumeProfiles.Save(gameId, current);

                if (version != System.Threading.Interlocked.CompareExchange(ref _masterVolumeFocusVersion, 0, 0))
                    return;
                try
                {
                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    if (dispatcher != null)
                    {
                        dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (version == System.Threading.Interlocked.CompareExchange(ref _masterVolumeFocusVersion, 0, 0) &&
                                overlayWindow?.IsVisible == true && overlayWindow.ViewModel?.GameId == gameId)
                                overlayWindow?.ViewModel?.RefreshAudioState();
                        }), System.Windows.Threading.DispatcherPriority.Background);
                    }
                }
                catch { }
            });
        }

        private bool OnKeyPressed(Keys key, bool altPressed)
        {
            // Local Games Store: manual scan hotkey (user assigned; empty = none). Playnite must be in front,
            // so it never steals a key from a running game.
            if (Settings != null && !string.IsNullOrWhiteSpace(Settings.HotkeyScanLocalGames) &&
                WindowHelper.IsOwnProcessForeground() && overlayWindow?.IsVisible != true &&
                HotkeyMatches(Settings.HotkeyScanLocalGames, key, altPressed,
                    WindowHelper.IsKeyDownAsync(0x10), WindowHelper.IsKeyDownAsync(0x11)))
            {
                DispatchHotkeyAction(() =>
                {
                    try { _localStore?.RequestScanManual("hotkey"); }
                    catch (Exception ex) { logger.Error($"Local-store scan hotkey failed: {ex.Message}"); }
                });
                return true;
            }

            // Settings-window hotkeys: only while Playnite itself has focus (never steal keys from a game).
            // The matching shortcut also closes its window when that window is already open.
            if (Settings != null &&
                (!string.IsNullOrWhiteSpace(Settings.HotkeyGameSettings) || !string.IsNullOrWhiteSpace(Settings.HotkeyGlobalSettings)) &&
                WindowHelper.IsOwnProcessForeground() && overlayWindow?.IsVisible != true)
            {
                bool sShift = WindowHelper.IsKeyDownAsync(0x10);
                bool sCtrl = WindowHelper.IsKeyDownAsync(0x11);
                bool perGameSettingsOpen = _perGameWindowOpen;
                bool globalSettingsOpen = _openGlobalSettingsWindow != null;
                if (!string.IsNullOrWhiteSpace(Settings.HotkeyGameSettings) &&
                    !globalSettingsOpen &&
                    HotkeyMatches(Settings.HotkeyGameSettings, key, altPressed, sShift, sCtrl))
                {
                    DispatchHotkeyAction(() =>
                    {
                        try { OpenPerGameSettings(); }
                        catch (Exception ex) { logger.Error($"Game-settings hotkey failed: {ex.Message}"); }
                    });
                    return true;
                }
                if (!string.IsNullOrWhiteSpace(Settings.HotkeyGlobalSettings) &&
                    !perGameSettingsOpen &&
                    HotkeyMatches(Settings.HotkeyGlobalSettings, key, altPressed, sShift, sCtrl))
                {
                    DispatchHotkeyAction(() =>
                    {
                        try { OpenGlobalSettingsWindow(); }
                        catch (Exception ex) { logger.Error($"Global-settings hotkey failed: {ex.Message}"); }
                    });
                    return true;
                }
            }

            // A parked game's window is minimized and cannot own foreground focus.
            // Permit ONLY the configured resume-capable hotkeys from another window
            // while a game is parked. All other shortcuts remain foreground-limited.
            if (WindowHelper.IsParked && Settings != null)
            {
                bool parkedShift = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
                bool parkedCtrl = (Control.ModifierKeys & Keys.Control) == Keys.Control;
                bool suspendShortcut = Settings.EnableSuspendMinimize &&
                    HotkeyMatches(Settings.HotkeySuspend, key, altPressed, parkedShift, parkedCtrl);
                bool overlayShortcut = HotkeyMatches(Settings.HotkeyOverlayToggle ?? "Alt+Oem3",
                    key, altPressed, parkedShift, parkedCtrl);
                if (overlayShortcut)
                {
                    // Same path as the Guide button.
                    DispatchHotkeyAction(() =>
                    {
                        try { HandleControllerShortcutToggle(); }
                        catch (Exception ex) { logger.Error($"Keyboard resume failed: {ex.Message}"); }
                    });
                    return true;
                }
                if (suspendShortcut)
                {
                    DispatchHotkeyAction(() =>
                    {
                        try
                        {
                            if (WindowHelper.IsParked)
                            {
                                WindowHelper.ResumeAndRestore();
                                try { cursorAutoHide?.ForceShow(); } catch { }
                                try { WindowHelper.ReleaseMouseLock(nudgeCursor: false); } catch { }
                                log("Keyboard hotkey: resumed parked game", "INFO");
                            }
                        }
                        catch (Exception ex) { logger.Error($"Keyboard resume failed: {ex.Message}"); }
                    });
                    return true;
                }
            }

            // Overlay toggle must work before its first controller activation. A game
            // may render in a launcher/child process that does not match cached PIDs.
            // When no game is active, the same binding restores/focuses Playnite from tray.
            bool toggleShift = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
            bool toggleCtrl = (Control.ModifierKeys & Keys.Control) == Keys.Control;
            if (Settings != null &&
                HotkeyMatches(Settings.HotkeyOverlayToggle ?? "Alt+Oem3", key, altPressed, toggleShift, toggleCtrl))
            {
                // Reuse the controller path: it focuses Playnite only when there is no
                // running/launching/parked game, and otherwise handles the overlay/game.
                DispatchHotkeyAction(() =>
                {
                    try { HandleControllerShortcutToggle(); }
                    catch (Exception ex) { logger.Error($"Keyboard overlay toggle failed: {ex.Message}"); }
                });
                return true;
            }

            // Only intercept when Playnite/its overlay or the tracked game owns focus.
            // In particular Alt+F4 must be delivered normally to unrelated windows.
            try
            {
                var hwnd = WindowHelper.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return false;
                WindowHelper.GetWindowThreadProcessId(hwnd, out uint foregroundPid);
                int pid = (int)foregroundPid;
                int gamePid = GameOverlayData != null ? GameOverlayData.ProcessId : -1;
                int viewModelPid = overlayWindow?.ViewModel?.ProcessId ?? -1;
                if (pid != Process.GetCurrentProcess().Id && pid != gamePid &&
                    pid != cachedActualGamePid && pid != viewModelPid)
                {
                    // Emulator / launcher games: the focused window often belongs to a process this
                    // extension has not identified yet (it was only learned once the overlay had been
                    // opened). Let the volume/mute hotkeys through when the focused process belongs to
                    // the running game. Every other shortcut keeps the strict rule.
                    if (!IsVolumeHotkey(key, altPressed) || !IsRunningGameForeground(pid))
                        return false;
                }
            }
            catch { return false; }
            // Escape always hides if somehow visible
            if (key == Keys.Escape && overlayWindow != null && overlayWindow.IsVisible)
            {
                DispatchHotkeyAction(() =>
                {
                    try { overlayWindow?.Hide(); } catch { }
                });
                return true;
            }

            // Real-time key state: Control.ModifierKeys goes stale while another application has focus.
            bool shiftPressed = WindowHelper.IsKeyDownAsync(0x10);
            bool ctrlPressed = WindowHelper.IsKeyDownAsync(0x11);

            // PrintScreen remap: the game (or Playnite) has focus here, so replace a bare PrintScreen
            // with the configured combo. Lets AMD Software / GeForce screenshot hotkeys be triggered by
            // PrintScreen even though their hotkey editors refuse to bind it.
            if (Settings != null && Settings.EnablePrintScreenRemap && key == Keys.PrintScreen &&
                !altPressed && !shiftPressed && !ctrlPressed &&
                !string.IsNullOrWhiteSpace(Settings.PrintScreenRemapCombo) &&
                Settings.PrintScreenRemapCombo.IndexOf("PRTSC", StringComparison.OrdinalIgnoreCase) < 0 &&
                Settings.PrintScreenRemapCombo.IndexOf("PRINTSCREEN", StringComparison.OrdinalIgnoreCase) < 0)
            {
                string combo = Settings.PrintScreenRemapCombo;
                DispatchHotkeyAction(() =>
                {
                    try { ButtonActions.SendComboAsync(combo); }
                    catch (Exception ex) { logger.Error($"PrintScreen remap failed: {ex.Message}"); }
                });
                return true;
            }

            // Never turn Alt+F4 into a Playnite force-close. While a game session
            // is active, consume the binding over Playnite's own UI rather than
            // letting Windows close the Playnite window by accident.
            if (Settings != null && Settings.EnableAltF4InstantClose &&
                HotkeyMatches(Settings.HotkeyForceClose, key, altPressed, shiftPressed, ctrlPressed))
            {
                int foregroundProcess = 0;
                try
                {
                    var foregroundWindow = WindowHelper.GetForegroundWindow();
                    if (foregroundWindow != IntPtr.Zero)
                    {
                        WindowHelper.GetWindowThreadProcessId(foregroundWindow, out uint foregroundId);
                        foregroundProcess = (int)foregroundId;
                    }
                }
                catch { return false; }

                // The Playnite main window must never be a force-close target.
                // An explicitly visible overlay may still close the tracked game.
                if (foregroundProcess == Process.GetCurrentProcess().Id &&
                    (overlayWindow == null || !overlayWindow.IsVisible))
                    return true;

                int trackedGame = GameOverlayData != null ? GameOverlayData.ProcessId : -1;
                int overlayGame = overlayWindow?.ViewModel?.ProcessId ?? -1;
                bool isKnownGame = foregroundProcess > 4 &&
                    (foregroundProcess == trackedGame ||
                     foregroundProcess == cachedActualGamePid ||
                     foregroundProcess == overlayGame ||
                     IsProcessInActiveGameFolder(foregroundProcess)) &&
                    !WindowHelper.IsProtectedProcess(foregroundProcess);
                if (!isKnownGame && (overlayWindow == null || !overlayWindow.IsVisible))
                    return false;

                DispatchHotkeyAction(() =>
                {
                    try { InstantForceCloseRunningGame(); }
                    catch (Exception ex) { logger.Error($"Force-close hotkey failed: {ex.Message}"); }
                });
                return true;
            }

            // Configurable suspend/park hotkey (default Pause)
            if (Settings != null && Settings.EnableSuspendMinimize &&
                HotkeyMatches(Settings.HotkeySuspend, key, altPressed, shiftPressed, ctrlPressed))
            {
                DispatchHotkeyAction(() =>
                {
                    try { ToggleSuspendMinimizeGame(); }
                    catch (Exception ex) { logger.Error($"Suspend hotkey failed: {ex.Message}"); }
                });
                return true;
            }

            // Volume hotkeys (work while a game is running; empty binding = disabled)
            if (Settings != null)
            {
                if (!string.IsNullOrWhiteSpace(Settings.HotkeyVolumeUp) &&
                    HotkeyMatches(Settings.HotkeyVolumeUp, key, altPressed, shiftPressed, ctrlPressed))
                {
                    DispatchHotkeyAction(() =>
                    {
                        try { AdjustVolume(+Settings.VolumeHotkeyStep); }
                        catch (Exception ex) { logger.Error($"Volume-up hotkey failed: {ex.Message}"); }
                    });
                    return true;
                }
                if (!string.IsNullOrWhiteSpace(Settings.HotkeyVolumeDown) &&
                    HotkeyMatches(Settings.HotkeyVolumeDown, key, altPressed, shiftPressed, ctrlPressed))
                {
                    DispatchHotkeyAction(() =>
                    {
                        try { AdjustVolume(-Settings.VolumeHotkeyStep); }
                        catch (Exception ex) { logger.Error($"Volume-down hotkey failed: {ex.Message}"); }
                    });
                    return true;
                }
                if (!string.IsNullOrWhiteSpace(Settings.HotkeyVolumeMute) &&
                    HotkeyMatches(Settings.HotkeyVolumeMute, key, altPressed, shiftPressed, ctrlPressed))
                {
                    DispatchHotkeyAction(() =>
                    {
                        try { ToggleRunningGameMute(); }
                        catch (Exception ex) { logger.Error($"Volume-mute hotkey failed: {ex.Message}"); }
                    });
                    return true;
                }
            }


            return false;
        }

        /// <summary>Queue work off the WH_KEYBOARD_LL callback so Windows does not drop the hook.</summary>
        private void DispatchHotkeyAction(Action action)
        {
            if (action == null) return;
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null)
                    dispatcher.BeginInvoke(action);
                else
                    action();
            }
            catch
            {
                try { action(); } catch { }
            }
        }

        /// <summary>
        /// SuperF4-style instant terminate. Optimized for latency:
        /// 1) Kill foreground window process immediately (what SuperF4 does).
        /// 2) Also kill tracked game PID if different (covers launcher vs game).
        /// 3) Child cleanup runs on a background thread so it never blocks the key handler.
        /// No Playnite DB / process-tree scan on the hot path.
        /// </summary>
        /// <summary>
        /// The real game process is often not the PID Playnite was handed (launcher stubs, UE
        /// wrappers). Treat any process running from the active Instant Play game's folder as the game.
        /// </summary>
        private static bool IsProcessInActiveGameFolder(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    string path = null;
                    try { path = p.MainModule?.FileName; } catch { return false; }
                    return SteamInstantPlayController.IsPathInActiveInstallRoot(path);
                }
            }
            catch { return false; }
        }

        private void InstantForceCloseRunningGame()
        {
            // No verified game session means force-close must do nothing.
            if (GameOverlayData == null || GameOverlayData.GameId == Guid.Empty)
                return;
            var killed = new System.Collections.Generic.HashSet<int>();
            int selfPid = Process.GetCurrentProcess().Id; // never kill Playnite / this extension host

            // Tell Instant Play to stop tracking this session once the kills below have landed;
            // otherwise Playnite keeps showing the game as RUNNING while any helper process from
            // the install folder is still alive.
            try { SteamInstantPlayController.NotifyForceClosed(GameOverlayData.GameId); } catch { }

            // --- HOT PATH 1: tracked game PID (set in OnGameStarted — no scan) ---
            int trackedPid = (GameOverlayData != null) ? GameOverlayData.ProcessId : -1;
            if (trackedPid > 4 && trackedPid != selfPid && !WindowHelper.IsProtectedProcess(trackedPid))
            {
                if (NativeTerminateProcess(trackedPid))
                    killed.Add(trackedPid);
            }

            // --- HOT PATH 2: foreground window process (true SuperF4 target) ---
            // Never kill Playnite / shell / system even if focus is wrong during launch.
            try
            {
                IntPtr hwnd = WindowHelper.GetForegroundWindow();
                if (hwnd != IntPtr.Zero)
                {
                    WindowHelper.GetWindowThreadProcessId(hwnd, out uint fgPid);
                    int fg = (int)fgPid;
                    // Never kill an arbitrary foreground app (including a second
                    // Playnite process). Only a PID explicitly tracked as game is eligible.
                    bool inGameFolder = false;
                    bool knownGamePid = fg == trackedPid ||
                        fg == cachedActualGamePid ||
                        fg == (overlayWindow?.ViewModel?.ProcessId ?? -1) ||
                        (inGameFolder = IsProcessInActiveGameFolder(fg));
                    if (fg > 4 && knownGamePid && fg != selfPid &&
                        !killed.Contains(fg) && !WindowHelper.IsProtectedProcess(fg))
                    {
                        // Only kill FG when we already know a game session is active
                        if (trackedPid > 4 || killed.Count > 0 || inGameFolder)
                        {
                            if (NativeTerminateProcess(fg))
                                killed.Add(fg);
                        }
                    }
                }
            }
            catch { /* never block Alt+F4 on FG lookup failure */ }

            if (killed.Count == 0)
            {
                // Last-resort fallback (slower): only if hot path found nothing
                try
                {
                    var runningGame = playniteAPI.Database.Games.FirstOrDefault(g => g.IsRunning);
                    if (runningGame != null)
                    {
                        var proc = FindRunningGameProcess(runningGame, trackedPid > 0 ? (int?)trackedPid : null);
                        if (proc != null && !proc.HasExited && proc.Id != selfPid
                            && !WindowHelper.IsProtectedProcess(proc.Id))
                        {
                            NativeTerminateProcess(proc.Id);
                            killed.Add(proc.Id);
                        }
                    }
                }
                catch { }
            }

            // Background: mop up children so launchers don't leave orphans.
            // Must not run on the keyboard-hook thread.
            if (killed.Count > 0)
            {
                var roots = killed.ToArray();
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        foreach (var rootPid in roots)
                        {
                            try
                            {
                                var all = Process.GetProcesses();
                                var map = new System.Collections.Generic.Dictionary<int, Process>(all.Length);
                                foreach (var p in all)
                                {
                                    try { map[p.Id] = p; } catch { }
                                }
                                foreach (var child in GetChildProcesses(rootPid, map))
                                {
                                    try
                                    {
                                        if (child.HasExited) continue;
                                        // Double-check: never mop up Playnite or system processes
                                        if (WindowHelper.IsProtectedProcess(child.Id))
                                            continue;
                                        NativeTerminateProcess(child.Id);
                                    }
                                    catch { }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                });
            }

            // Non-blocking overlay hide
            if (overlayWindow != null && overlayWindow.IsVisible)
            {
                try
                {
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(
                        new Action(() => { try { overlayWindow.Hide(); } catch { } }));
                }
                catch { }
            }
        }

        /// <summary>
        /// Fastest possible process kill: OpenProcess(PROCESS_TERMINATE) + TerminateProcess.
        /// Avoids Process.GetProcessById / managed Kill overhead on the hot path.
        /// </summary>
        private static bool NativeTerminateProcess(int pid)
        {
            if (pid <= 4) return false;
            // Never terminate Playnite / shell / system — a wrong PID here closes Playnite entirely
            try
            {
                if (pid == Process.GetCurrentProcess().Id)
                    return false;
            }
            catch { return false; }
            try
            {
                if (WindowHelper.IsProtectedProcess(pid))
                    return false;
            }
            catch { return false; }

            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(ProcessAccessFlags.Terminate, false, pid);
                if (h == IntPtr.Zero)
                {
                    // Fallback to managed Kill if we lack rights via OpenProcess
                    try
                    {
                        var p = Process.GetProcessById(pid);
                        if (p.HasExited) return false;
                        if (WindowHelper.IsProtectedProcess(p.Id)) return false;
                        p.Kill();
                        return true;
                    }
                    catch { }
                    return false;
                }
                return TerminateProcess(h, 1);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (h != IntPtr.Zero)
                    CloseHandle(h);
            }
        }

        /// <summary>
        /// When Instant Play is enabled, hide Steam library play actions so only our
        /// controller is offered and Playnite does not show the action picker dialog.
        /// When disabled, restore library play actions for Steam games.
        /// </summary>
        
        /// <summary>
        /// Toggle park state: suspend all threads + minimize window, or resume + restore.
        /// </summary>
        private void ToggleSuspendMinimizeGame()
        {
            // Debounce rapid re-entry (hotkey + overlay button + controller can all fire)
            var now = DateTime.UtcNow;
            if ((now - _lastSuspendToggleUtc).TotalMilliseconds < 400)
                return;
            if (System.Threading.Interlocked.CompareExchange(ref _suspendToggleBusy, 1, 0) != 0)
                return;
            _lastSuspendToggleUtc = now;

            try
            {
                if (WindowHelper.IsParked)
                {
                    WindowHelper.ResumeAndRestore();
                    try { cursorAutoHide?.ForceShow(); } catch { }
                    try { WindowHelper.ReleaseMouseLock(nudgeCursor: false); } catch { }
                    try { keyboardHook?.Reinstall(); } catch { }
                    log("Game resumed from suspend+minimize", "INFO");
                    return;
                }

                // Prefer the real game process (same resolution as overlay open).
                int pid = -1;
                try
                {
                    var runningGame = playniteAPI?.Database?.Games?.FirstOrDefault(g => g.IsRunning);
                    int? preferred = (GameOverlayData != null && GameOverlayData.ProcessId > 0)
                        ? (int?)GameOverlayData.ProcessId
                        : null;

                    if (runningGame != null)
                    {
                        var proc = FindRunningGameProcess(runningGame, preferred);
                        if (proc != null && !proc.HasExited)
                            pid = proc.Id;
                    }

                    if (pid > 0 && GameOverlayData != null && GameOverlayData.ProcessId != pid)
                        GameOverlayData.ProcessId = pid;
                }
                catch (Exception ex)
                {
                    logger.Error($"Suspend PID resolve via FindRunningGameProcess failed: {ex.Message}");
                }

                if (pid <= 0 && GameOverlayData != null && GameOverlayData.ProcessId > 0)
                    pid = GameOverlayData.ProcessId;

                if (pid <= 0)
                {
                    // Fallback: foreground window — but NEVER Playnite / system processes
                    try
                    {
                        var hwnd = WindowHelper.GetForegroundWindow();
                        if (hwnd != IntPtr.Zero)
                        {
                            WindowHelper.GetWindowThreadProcessId(hwnd, out uint fgPid);
                            if (fgPid > 0 && !WindowHelper.IsProtectedProcess((int)fgPid))
                                pid = (int)fgPid;
                        }
                    }
                    catch { }
                }

                if (pid <= 0)
                {
                    log("Suspend+minimize: no game process found", "WARNING");
                    return;
                }

                if (WindowHelper.IsProtectedProcess(pid))
                {
                    log($"Suspend+minimize blocked — PID {pid} is Playnite/system (refusing to freeze host)", "WARNING");
                    return;
                }

                // Hide overlay first if open (without resume - SuspendAndMinimize owns freeze)
                try
                {
                    if (overlayWindow != null && overlayWindow.IsVisible)
                    {
                        overlayWindow.Active = false;
                        overlayWindow.Hide();
                    }
                }
                catch { }

                try
                {
                    WindowHelper.SuspendAndMinimize(pid);   // minimize + freeze
                    try { cursorAutoHide?.ForceShow(); } catch { }
                    try { WindowHelper.ReleaseMouseLock(nudgeCursor: false); } catch { }
                    log($"Game suspended (PID {pid}, minimize + freeze)", "INFO");
                }
                catch (Exception ex)
                {
                    logger.Error($"SuspendAndMinimize failed: {ex.Message}");
                    // Best-effort recovery if park failed mid-way
                    try { WindowHelper.ResumeAndRestore(); } catch { }
                }
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _suspendToggleBusy, 0);
            }
        }

// ---- Instant Play: which stores, and who owns the Play action ------------------------
        private bool StoreInstantPlayEnabled(InstantStore store)
        {
            if (Settings == null) return false;
            switch (store)
            {
                case InstantStore.Steam: return Settings.EnableSteamInstantPlay;
                case InstantStore.Gog: return Settings.EnableGogInstantPlay;
                case InstantStore.Epic: return Settings.EnableEpicInstantPlay;
                case InstantStore.Ubisoft: return Settings.EnableUbisoftInstantPlay;
                case InstantStore.Xbox: return Settings.EnableXboxInstantPlay;
                default: return false;
            }
        }

        private bool AnyInstantPlayEnabled()
        {
            return Settings != null && (Settings.EnableSteamInstantPlay || Settings.EnableGogInstantPlay ||
                                        Settings.EnableEpicInstantPlay || Settings.EnableUbisoftInstantPlay ||
                                        Settings.EnableXboxInstantPlay);
        }

        /// <summary>
        /// True when Instant Play should replace the library's Play action for this game. For stores other
        /// than Steam this also requires that the program to start can actually be found, so a game is never
        /// left without a way to launch.
        /// </summary>
        private bool InstantPlayOwnsPlay(Game g)
        {
            var store = SteamInstantPlay.GetStore(g);
            if (store == InstantStore.None || !StoreInstantPlayEnabled(store)) return false;
            if (Settings.IsInstantPlayDisabledFor(g.Id)) return false;
            if (store != InstantStore.Steam)
            {
                try
                {
                    var launch = SteamInstantPlay.ResolveLaunch(g);
                    if (launch == null || string.IsNullOrEmpty(launch.Exe) || !File.Exists(launch.Exe)) return false;
                }
                catch { return false; }
            }
            return true;
        }

        // Non-Steam games whose library Play action this extension switched off (so it can be switched back on).
        private string InstantForcedOffPath()
        {
            try { return Path.Combine(GetPluginUserDataPath(), "instantplay_library_off.txt"); }
            catch { return null; }
        }

        private HashSet<Guid> ReadForcedOff()
        {
            var set = new HashSet<Guid>();
            try
            {
                string p = InstantForcedOffPath();
                if (p != null && File.Exists(p))
                    foreach (var line in File.ReadAllLines(p))
                        if (Guid.TryParse(line.Trim(), out Guid id)) set.Add(id);
            }
            catch { }
            return set;
        }

        private void WriteForcedOff(HashSet<Guid> set)
        {
            try
            {
                string p = InstantForcedOffPath();
                if (p == null) return;
                if (set.Count == 0) { if (File.Exists(p)) File.Delete(p); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllLines(p, set.Select(x => x.ToString()).ToArray());
            }
            catch { }
        }

        private bool ShouldRunInstantPlayPolicy()
        {
            return AnyInstantPlayEnabled() || ReadForcedOff().Count > 0;
        }

        /// <summary>
        /// Bring the game's "include library Play action" flag in line with Instant Play ownership.
        /// Returns true if the game record was changed (the caller saves it).
        /// </summary>
        private bool ApplyInstantPlayLibraryFlag(Game g, HashSet<Guid> forcedOff = null)
        {
            var store = SteamInstantPlay.GetStore(g);
            if (store == InstantStore.None) return false;

            bool ownSet = forcedOff == null;
            var set = forcedOff ?? ReadForcedOff();
            bool owns = InstantPlayOwnsPlay(g);
            bool changed = false;

            if (store == InstantStore.Steam)
            {
                bool include = !owns;
                if (g.IncludeLibraryPluginAction != include)
                {
                    g.IncludeLibraryPluginAction = include;
                    changed = true;
                }
            }
            else if (owns)
            {
                if (g.IncludeLibraryPluginAction)
                {
                    g.IncludeLibraryPluginAction = false;
                    changed = true;
                }
                if (set.Add(g.Id) && ownSet) WriteForcedOff(set);
            }
            else if (set.Contains(g.Id))
            {
                if (!g.IncludeLibraryPluginAction)
                {
                    g.IncludeLibraryPluginAction = true;
                    changed = true;
                }
                set.Remove(g.Id);
                if (ownSet) WriteForcedOff(set);
            }
            return changed;
        }

        internal void ApplySteamInstantPlayPolicy()
        {
            if (System.Threading.Interlocked.Exchange(ref _applyingInstantPlayPolicy, 1) != 0)
                return;

            try
            {
                if (playniteAPI?.Database?.Games == null || Settings == null)
                    return;

                bool exclusive = Settings.EnableSteamInstantPlay;
                bool settingsChanged = false;
                int updated = 0;
                int restoredActions = 0;

                var forcedOff = ReadForcedOff();
                var forcedOffBefore = new HashSet<Guid>(forcedOff);

                foreach (var game in playniteAPI.Database.Games.ToList())
                {
                    if (SteamInstantPlay.GetStore(game) == InstantStore.None)
                        continue;
                    // Never rewrite a record while the game is launching or running.
                    if (game.IsRunning || game.IsLaunching)
                        continue;

                    // Instant Play replaces only the store library's integration action.
                    // User-created Play actions must remain real Play actions in the DB/UI.
                    bool gameChanged = ApplyInstantPlayLibraryFlag(game, forcedOff);

                    // Undo the previous workaround that permanently demoted custom
                    // Play actions. Restore only actions we recorded as suppressed.
                    if (game.GameActions != null && Settings.SuppressedInstantPlayActions != null)
                    {
                        var saved = Settings.SuppressedInstantPlayActions
                            .Where(x => x != null && x.GameId == game.Id)
                            .ToList();

                        foreach (var record in saved)
                        {
                            var action = game.GameActions.FirstOrDefault(
                                a => a != null &&
                                     string.Equals(GetInstantPlayActionKey(a), record.ActionKey,
                                         StringComparison.Ordinal));

                            if (action != null && !action.IsPlayAction)
                            {
                                action.IsPlayAction = true;
                                gameChanged = true;
                                restoredActions++;
                            }

                            Settings.SuppressedInstantPlayActions.Remove(record);
                            settingsChanged = true;
                        }
                    }

                    if (gameChanged)
                    {
                        playniteAPI.Database.Games.Update(game);
                        updated++;
                    }
                }

                if (!forcedOff.SetEquals(forcedOffBefore))
                    WriteForcedOff(forcedOff);

                if (settingsChanged)
                {
                    try { SaveSettingsSafely(Settings); } catch { }
                }

                log($"Steam Instant Play policy: exclusive={exclusive}, gamesUpdated={updated}, customPlayActionsRestored={restoredActions}", "DEBUG");
            }
            catch (Exception ex)
            {
                logger.Error($"ApplySteamInstantPlayPolicy failed: {ex.Message}");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _applyingInstantPlayPolicy, 0);
            }
        }

        private static string GetInstantPlayActionKey(GameAction action)
        {
            if (action == null)
                return string.Empty;

            // Stable enough to find the same action again if Instant Play is turned off.
            return string.Join("\u001F",
                action.Name ?? string.Empty,
                ((int)action.Type).ToString(),
                action.Path ?? string.Empty,
                action.Arguments ?? string.Empty,
                action.AdditionalArguments ?? string.Empty,
                action.WorkingDir ?? string.Empty,
                action.Script ?? string.Empty,
                action.EmulatorId.ToString(),
                action.EmulatorProfileId ?? string.Empty);
        }

        private void OnInstantPlayGameItemUpdated(object sender, ItemUpdatedEventArgs<Game> e)
        {
            try
            {
                if (!ShouldRunInstantPlayPolicy() || e?.UpdatedItems == null)
                    return;

                // Starting a game makes Playnite update its record several times in a row
                // (launching/running/last activity). Re-scanning and rewriting the whole database
                // from inside those updates is risky and pointless, so only react to a game that
                // is NOT starting/running and whose Play-action flag actually needs correcting.
                bool relevant = false;
                foreach (var u in e.UpdatedItems)
                {
                    var g = u?.NewData;
                    if (g == null || g.IsRunning || g.IsLaunching)
                        continue;
                    var gStore = SteamInstantPlay.GetStore(g);
                    if (gStore == InstantStore.None)
                        continue;

                    bool instantOwnsPlay = InstantPlayOwnsPlay(g);
                    bool mismatch = gStore == InstantStore.Steam
                        ? g.IncludeLibraryPluginAction == instantOwnsPlay
                        : (instantOwnsPlay
                            ? g.IncludeLibraryPluginAction
                            : (!g.IncludeLibraryPluginAction && ReadForcedOff().Contains(g.Id)));
                    bool hasSuppressed = Settings.SuppressedInstantPlayActions != null &&
                        Settings.SuppressedInstantPlayActions.Any(x => x != null && x.GameId == g.Id);
                    if (mismatch || hasSuppressed)
                    {
                        relevant = true;
                        break;
                    }
                }

                if (!relevant)
                    return;

                // Run after Playnite finished its own update, not inside the notification.
                playniteAPI.MainView.UIDispatcher.BeginInvoke(
                    new Action(() => ApplySteamInstantPlayPolicy()),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                logger.Error($"Instant Play update handler failed: {ex.Message}");
            }
        }


        private void EnsureInstantPlayOwnsLibraryAction(Game game)
        {
            if (game == null || !InstantPlayOwnsPlay(game))
                return;

            if (!game.IncludeLibraryPluginAction)
                return;

            try
            {
                game.IncludeLibraryPluginAction = false;
                if (SteamInstantPlay.GetStore(game) != InstantStore.Steam)
                {
                    var off = ReadForcedOff();
                    if (off.Add(game.Id)) WriteForcedOff(off);
                }
                Breadcrumb.Write("Games.Update (Steam Instant Play) " + game.Name);
                playniteAPI.Database.Games.Update(game);
                log($"Steam Instant Play: forced library integration action OFF for {game.Name}", "DEBUG");
            }
            catch (Exception ex)
            {
                logger.Error($"Failed to force Steam library action OFF for {game.Name}: {ex.Message}");
            }
        }

        public override void OnGameSelected(OnGameSelectedEventArgs args)
        {
            try
            {
                if (args?.NewValue == null || !AnyInstantPlayEnabled())
                    return;

                foreach (var game in args.NewValue)
                {
                    EnsureInstantPlayOwnsLibraryAction(game);
                }
            }
            catch (Exception ex)
            {
                logger.Error($"Failed to enforce Instant Play policy on game selection: {ex.Message}");
            }
        }

        /// <summary>
        /// SKIF-style Instant Play via a real PlayController so Playnite keeps
        /// IsRunning / playtime tracking until the process (or install-dir children) exit.
        /// </summary>
        public override IEnumerable<PlayController> GetPlayActions(GetPlayActionsArgs args)
        {
            if (Settings == null || args?.Game == null)
                yield break;
            var store = SteamInstantPlay.GetStore(args.Game);
            if (store == InstantStore.None || !StoreInstantPlayEnabled(store))
                yield break;
            if (Settings.IsInstantPlayDisabledFor(args.Game.Id))
                yield break;

            // Keep the normal Steam library action excluded persistently.
            if (store == InstantStore.Steam)
                EnsureInstantPlayOwnsLibraryAction(args.Game);

            // Custom Play actions take priority over Instant Play.
            //
            // If the user explicitly configured a Play action (for example a mod
            // launcher or custom executable), do not register Instant Play for this
            // game at all. With the library action disabled, Playnite then has
            // exactly the user's custom Play action and launches it directly without
            // showing the action chooser.
            try
            {
                if (args.Game.GameActions != null &&
                    args.Game.GameActions.Any(a => a != null && a.IsPlayAction))
                {
                    log($"Instant Play skipped for {args.Game.Name}: custom Play action exists.", "DEBUG");
                    yield break;
                }
            }
            catch (Exception ex)
            {
                logger.Error($"Failed to inspect custom Play actions for {args.Game.Name}: {ex.Message}");
            }

            InstantLaunch launch = null;
            try
            {
                launch = SteamInstantPlay.ResolveLaunch(args.Game);
            }
            catch (Exception ex)
            {
                logger.Error($"Instant Play resolve failed: {ex.Message}");
                yield break;
            }

            if (launch == null || string.IsNullOrEmpty(launch.Exe) || !File.Exists(launch.Exe))
            {
                log($"Instant Play: no exe for {args.Game.Name}; the normal launch is used.", "WARNING");
                yield break;
            }

            // Other stores: only take over the Play action once we know we can start the game ourselves.
            if (store != InstantStore.Steam)
                EnsureInstantPlayOwnsLibraryAction(args.Game);

            log($"Instant Play controller ready ({SteamInstantPlay.StoreName(store)}): {launch.Exe}", "DEBUG");
            yield return new SteamInstantPlayController(args.Game, launch.Exe, launch.Root, logger, launch.Arguments, launch.WorkingDir);
        }



        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args)
        {
            // Local Games Store: optional rescan on every library refresh (F5).
            try { _localStore?.OnLibraryUpdated(); }
            catch (Exception ex) { logger.Error($"Local Games Store library-refresh scan failed: {ex.Message}"); }

            // Games that already existed (or that a library sync just filled in) may still miss links.
            // The pass only touches games that need work, so this is cheap when nothing is missing.
            if (IsDesktopMode)
            {
                try
                {
                    System.Windows.Application.Current?.Dispatcher?.BeginInvoke(
                        new Action(() => { try { ApplyAutoGameLinks(); } catch { } }),
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }
                catch { }
            }

            try
            {
                // Steam/Playnite may restore IncludeLibraryPluginAction during startup
                // or a library sync. Re-assert Instant Play ownership afterwards so the
                // "Include library integration play actions" toggle stays OFF for games
                // using Instant Play. Per-game Instant Play opt-outs remain ON normally.
                if (ShouldRunInstantPlayPolicy())
                {
                    ApplySteamInstantPlayPolicy();
                }
            }
            catch (Exception ex)
            {
                logger.Error($"Failed to re-apply Steam Instant Play policy after library update: {ex.Message}");
            }
        }

        // ---- Top bar icons + per-game settings window ---------------------------------------
        private TopPanelItem _topItemGame;
        private bool _perGameWindowOpen;
        private System.Windows.Window _openPerGameSettingsWindow;

        private static object MakeTopBarIcon(string glyph)
        {
            return new System.Windows.Controls.TextBlock
            {
                Text = glyph,
                FontSize = 20,
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
        }

        public override IEnumerable<TopPanelItem> GetTopPanelItems()
        {
            _topItemGame = new TopPanelItem
            {
                Icon = MakeTopBarIcon("\uE7FC"),
                Title = "Playnite Overlay 3000: settings for the selected game",
                Activated = () =>
                {
                    try { OpenPerGameSettings(); }
                    catch (Exception ex) { logger.Error($"Top bar game settings failed: {ex.Message}"); }
                }
            };
            bool canToggleLive = _topItemGame.GetType().GetProperty("Visible") != null;
            ApplyTopBarVisibility();
            // Without a live "Visible" switch an icon that is off at startup is simply not added.
            if (canToggleLive || Settings?.ShowTopBarGameSettings != false) yield return _topItemGame;
        }

        internal void ApplyTopBarVisibility()
        {
            try
            {
                SetTopItemVisible(_topItemGame, Settings?.ShowTopBarGameSettings != false);
            }
            catch { }
        }

        private static void SetTopItemVisible(TopPanelItem item, bool visible)
        {
            try
            {
                var prop = item?.GetType().GetProperty("Visible");
                if (prop != null && prop.CanWrite && prop.PropertyType == typeof(bool))
                    prop.SetValue(item, visible, null);
            }
            catch { }
        }

        private List<Game> GetSelectedOrRunningGames()
        {
            var list = new List<Game>();
            try { list = playniteAPI.MainView?.SelectedGames?.ToList() ?? new List<Game>(); } catch { }
            if (list.Count == 0)
                list = playniteAPI.Database.Games.Where(g => g.IsRunning).ToList();
            return list;
        }

        private string GetPreviewCoverImagePath()
        {
            try
            {
                if (GameOverlayData != null && !string.IsNullOrWhiteSpace(GameOverlayData.CoverImagePath))
                    return GameOverlayData.CoverImagePath;
                return GetFullCoverImagePath(GetSelectedOrRunningGames().FirstOrDefault());
            }
            catch { return null; }
        }

        private void RefreshGlobalSettingsCoverSource()
        {
            try
            {
                var view = _openGlobalSettingsView;
                if (view == null) return;
                string coverPath = GetPreviewCoverImagePath();
                if (view.Dispatcher.CheckAccess())
                    view.SetPreviewCoverImagePath(coverPath);
                else
                    view.Dispatcher.BeginInvoke(new Action(() => view.SetPreviewCoverImagePath(coverPath)));
            }
            catch { }
        }

        private void SetInstantPlayForGame(Game g, bool useInstantPlay)
        {
            Settings.SetInstantPlayDisabledFor(g.Id, !useInstantPlay);
            if (ApplyInstantPlayLibraryFlag(g))
                playniteAPI.Database.Games.Update(g);
        }

        private static void CenterOnWorkArea(System.Windows.Window window)
        {
            if (window == null) return;
            try
            {
                var work = System.Windows.SystemParameters.WorkArea;
                window.Left = work.Left + Math.Max(0, (work.Width - window.ActualWidth) / 2);
                window.Top = work.Top + Math.Max(0, (work.Height - window.ActualHeight) / 2);
            }
            catch { }
        }

        private System.Windows.Window _openGlobalSettingsWindow;

        /// <summary>
        /// Centered standalone global settings window (same pattern as OptiScaler Injector).
        /// Used by the global-settings hotkey and top-bar button instead of Playnite's embedded page.
        /// </summary>
        public void OpenGlobalSettingsWindow()
        {
            if (_openGlobalSettingsWindow != null)
            {
                try
                {
                    if (_openGlobalSettingsWindow.IsVisible)
                    {
                        _openGlobalSettingsWindow.Close();
                        return;
                    }
                }
                catch { }
                _openGlobalSettingsWindow = null;
            }

            var overlaySettings = Settings;
            overlaySettings.BeginEdit();
            var window = playniteAPI.Dialogs.CreateWindow(new WindowCreationOptions
            {
                ShowMinimizeButton = false,
                ShowMaximizeButton = true
            });
            window.Title = "Playnite Overlay 3000 — Settings";
            window.Width = 1080;
            window.Height = 820;
            window.MinWidth = 900;
            window.MinHeight = 520;
            window.ResizeMode = System.Windows.ResizeMode.CanResize;
            AppearancePalette.Apply(window, overlaySettings.SettingsThemePreset, overlaySettings.SettingsMenuGlass);
            window.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ReShadeBg");
            PropertyChangedEventHandler themeChanged = (sender, args) =>
            {
                if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(OverlaySettings.SettingsThemePreset) ||
                    args.PropertyName == nameof(OverlaySettings.SettingsMenuGlass))
                    AppearancePalette.Apply(window, overlaySettings.SettingsThemePreset, overlaySettings.SettingsMenuGlass);
            };
            overlaySettings.PropertyChanged += themeChanged;
            var dock = new System.Windows.Controls.DockPanel();
            var buttons = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new System.Windows.Thickness(10)
            };
            var save = new System.Windows.Controls.Button { Content = "Save", MinWidth = 90, Margin = new System.Windows.Thickness(4) };
            var cancel = new System.Windows.Controls.Button { Content = "Cancel", MinWidth = 90, Margin = new System.Windows.Thickness(4) };
            save.Click += (s, e) =>
            {
                overlaySettings.EndEdit();
                try { overlayWindow?.ApplyTheme(overlaySettings); } catch { }
                window.DialogResult = true;
                window.Close();
            };
            cancel.Click += (s, e) => { overlaySettings.CancelEdit(); window.DialogResult = false; window.Close(); };
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);
            System.Windows.Controls.DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Bottom);
            dock.Children.Add(buttons);
            var settingsView = new OverlaySettingsView { DataContext = overlaySettings };
            settingsView.SetPreviewCoverImagePath(GetPreviewCoverImagePath());
            _openGlobalSettingsView = settingsView;
            var settingsButtonStyle = settingsView.TryFindResource("GlobalSettingsButton") as System.Windows.Style;
            if (settingsButtonStyle != null)
            {
                save.Style = settingsButtonStyle;
                save.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "UiPrimary");
                save.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "UiPrimaryText");
                cancel.Style = settingsButtonStyle;
            }
            dock.Children.Add(settingsView);
            window.Content = dock;
            window.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            window.Loaded += (_, __) => CenterOnWorkArea(window);
            _openGlobalSettingsWindow = window;
            window.Closed += (s, e) =>
            {
                if (ReferenceEquals(_openGlobalSettingsWindow, window)) _openGlobalSettingsWindow = null;
                if (ReferenceEquals(_openGlobalSettingsView, settingsView)) _openGlobalSettingsView = null;
            };
            try { window.ShowDialog(); }
            finally { overlaySettings.PropertyChanged -= themeChanged; }
            if (window.DialogResult != true) overlaySettings.CancelEdit();
        }

        /// <summary>Per-game settings window (same actions as the right-click menu).</summary>
        private void OpenPerGameSettings()
        {
            if (_perGameWindowOpen)
            {
                try { _openPerGameSettingsWindow?.Close(); } catch { }
                return;
            }
            var games = GetSelectedOrRunningGames();
            if (games.Count == 0)
            {
                playniteAPI.Dialogs.ShowErrorMessage("Select a game first.", "Playnite Overlay 3000");
                return;
            }

            _perGameWindowOpen = true;
            System.Windows.Window window = null;
            try
            {
                window = playniteAPI.Dialogs.CreateWindow(new WindowCreationOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false
                });
                _openPerGameSettingsWindow = window;
                window.Closed += (s, e) =>
                {
                    if (ReferenceEquals(_openPerGameSettingsWindow, window))
                        _openPerGameSettingsWindow = null;
                };
                window.Title = "Playnite Overlay 3000: " + (games.Count == 1 ? games[0].Name : games.Count + " games");
                window.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/PlayniteGameOverlay;component/ExtensionTheme.xaml", UriKind.Absolute)
                });
                AppearancePalette.Apply(window, Settings.SettingsThemePreset, Settings.SettingsMenuGlass);
                var switchStyle = window.TryFindResource("OverlaySettingsSwitch") as System.Windows.Style;
                if (switchStyle != null)
                    window.Resources[typeof(System.Windows.Controls.CheckBox)] = switchStyle;
                window.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ReShadeBg");
                window.Content = BuildPerGameSettingsContent(games, window);
                window.Width = 460;
                window.MinWidth = 400;
                window.MaxHeight = Math.Max(360, System.Windows.SystemParameters.WorkArea.Height - 32);
                window.SizeToContent = System.Windows.SizeToContent.Height;
                window.ResizeMode = System.Windows.ResizeMode.NoResize;
                // Match OptiScaler Injector: center on the work area after layout.
                window.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
                window.Loaded += (_, __) => CenterOnWorkArea(window);
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                logger.Error($"Failed to open per-game settings: {ex}");
                playniteAPI.Dialogs.ShowErrorMessage("Could not open game settings:\n" + ex.Message, "Playnite Overlay 3000");
            }
            finally
            {
                if (ReferenceEquals(_openPerGameSettingsWindow, window))
                    _openPerGameSettingsWindow = null;
                _perGameWindowOpen = false;
            }
        }

        private System.Windows.FrameworkElement BuildPerGameSettingsContent(List<Game> games, System.Windows.Window window)
        {
            // Use the same section and card treatment as the main Appearance settings,
            // without imposing a font family on the user's Playnite theme.
            var layout = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(16) };
            var panel = new System.Windows.Controls.StackPanel();
            bool windowsTheme = string.Equals(Settings.SettingsThemePreset, "Windows 11", StringComparison.OrdinalIgnoreCase);

            Func<string, System.Windows.Controls.TextBlock> textBlock = text =>
            {
                var textElement = new System.Windows.Controls.TextBlock
                {
                    Text = text, FontWeight = System.Windows.FontWeights.Normal,
                    TextWrapping = System.Windows.TextWrapping.Wrap
                };
                textElement.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ReShadeText");
                return textElement;
            };
            Func<string, System.Windows.FrameworkElement> icon = key =>
            {
                if (string.Equals(key, "InstantPlayIcon", StringComparison.Ordinal))
                {
                    var image = new System.Windows.Controls.Image
                    {
                        Width = 18, Height = 18, Stretch = System.Windows.Media.Stretch.Uniform,
                        Margin = new System.Windows.Thickness(0, 0, 13, 0),
                        VerticalAlignment = System.Windows.VerticalAlignment.Center,
                        Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(
                            "pack://application:,,,/PlayniteGameOverlay;component/assets/instant-play-sonic.png", UriKind.Absolute))
                    };
                    return image;
                }
                var shape = new System.Windows.Shapes.Path
                {
                    Width = 18, Height = 18, Stretch = System.Windows.Media.Stretch.Uniform,
                    StrokeThickness = 1.7, StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
                    StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                    StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
                    Margin = new System.Windows.Thickness(0, 0, 13, 0),
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                };
                shape.SetResourceReference(System.Windows.Shapes.Path.DataProperty, key);
                shape.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "ReShadeText");
                return shape;
            };
            Func<string, string, System.Windows.FrameworkElement, System.Windows.Controls.Expander> makeSection = (title, iconKey, content) =>
            {
                var header = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                header.Children.Add(icon(iconKey));
                var label = textBlock(title);
                label.VerticalAlignment = System.Windows.VerticalAlignment.Center;
                header.Children.Add(label);
                var section = new System.Windows.Controls.Expander
                {
                    Header = header, Content = content, IsExpanded = false,
                    Margin = new System.Windows.Thickness(0, 0, 0, 7)
                };
                section.Style = window.TryFindResource("TreeBranchExpander") as System.Windows.Style;
                var sectionTemplate = window.TryFindResource(windowsTheme ? "Windows11SectionTemplate" : "UnifiedSectionTemplate") as System.Windows.Controls.ControlTemplate;
                if (sectionTemplate != null) section.Template = sectionTemplate;
                return section;
            };
            Func<System.Windows.FrameworkElement, System.Windows.Controls.Border> makeRow = content =>
            {
                var row = new System.Windows.Controls.Border
                {
                    Child = content,
                    Padding = new System.Windows.Thickness(16, 12, 16, 12),
                    MinHeight = 56,
                    CornerRadius = new System.Windows.CornerRadius(windowsTheme ? 4 : 2)
                };
                // Steam's expanded section already has a contrasting content surface;
                // Windows uses individual settings cards like the main settings page.
                row.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, windowsTheme ? "UiCard" : "ReShadeBg");
                return row;
            };
            Action<System.Windows.Controls.CheckBox, string, string> setSwitchContent = (box, labelText, noteText) =>
            {
                var labelPanel = new System.Windows.Controls.StackPanel();
                labelPanel.Children.Add(textBlock(labelText));
                var note = textBlock(noteText);
                note.FontSize = 12;
                note.Margin = new System.Windows.Thickness(0, 3, 0, 0);
                note.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ReShadeTextMuted");
                labelPanel.Children.Add(note);
                box.Content = labelPanel;
                box.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            };

            var heading = textBlock(games.Count == 1 ? games[0].Name : games.Count + " selected games");
            heading.FontSize = 16;
            heading.Margin = new System.Windows.Thickness(0, 0, 0, 14);
            panel.Children.Add(heading);

            int forced = games.Count(g => IsGpuForced(g.Id));
            var gpu = new System.Windows.Controls.CheckBox { IsChecked = forced == games.Count };
            setSwitchContent(gpu, "Run on discrete GPU (high performance)" +
                (forced > 0 && forced < games.Count ? $"  ({forced} of {games.Count} on)" : ""),
                "Takes effect the next time the game starts.");
            gpu.Click += (s, e) =>
            {
                bool on = gpu.IsChecked == true;
                foreach (var g in games) SetGpuForced(g, on);
            };
            panel.Children.Add(makeSection("Graphics", "SpeedIconGeometry", makeRow(gpu)));

            var supported = games.Where(g => SteamInstantPlay.GetStore(g) != InstantStore.None).ToList();
            var instantGames = supported.Where(g => StoreInstantPlayEnabled(SteamInstantPlay.GetStore(g))).ToList();
            int usingInstant = instantGames.Count(g => !Settings.IsInstantPlayDisabledFor(g.Id));
            var instant = new System.Windows.Controls.CheckBox
            {
                IsChecked = instantGames.Count > 0 && usingInstant == instantGames.Count,
                IsEnabled = instantGames.Count > 0
            };
            string instantNote = supported.Count == 0
                ? "Only for Steam, GOG, Xbox, Epic and Ubisoft games."
                : instantGames.Count == 0
                    ? "Turn on Instant Play for " + SteamInstantPlay.StoreName(SteamInstantPlay.GetStore(supported[0])) + " in settings first."
                    : "Off = the store app starts the game.";
            setSwitchContent(instant, games.Count == 1 ? "Use Instant Play for this game" : "Use Instant Play for the selected games", instantNote);
            instant.Click += (s, e) =>
            {
                bool use = instant.IsChecked == true;
                foreach (var g in instantGames) SetInstantPlayForGame(g, use);
                SaveSettingsSafely(Settings);
            };
            panel.Children.Add(makeSection("Instant Play", "InstantPlayIcon", makeRow(instant)));

            if (games.Count == 1 && _completeUninstall != null && _completeUninstall.ShouldOfferButton(games[0]))
            {
                var target = games[0];
                // A standalone, theme-matching action rather than a Maintenance section.
                var uninstallContent = new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                };
                var trash = new System.Windows.Shapes.Path
                {
                    Width = 16, Height = 16, Stretch = System.Windows.Media.Stretch.Uniform,
                    StrokeThickness = 1.6, StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
                    StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                    StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    Margin = new System.Windows.Thickness(0, 0, 10, 0),
                    Data = System.Windows.Media.Geometry.Parse("M3,5 L21,5 M9,5 L10,2 L14,2 L15,5 M5,5 L6,20 L18,20 L19,5 M10,9 L10,16 M14,9 L14,16")
                };
                trash.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "ReShadeDanger");
                uninstallContent.Children.Add(trash);
                uninstallContent.Children.Add(textBlock("Complete uninstall"));
                var uninstall = new System.Windows.Controls.Button
                {
                    Content = uninstallContent, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                    MinHeight = 42, Padding = new System.Windows.Thickness(16, 8, 16, 8),
                    Margin = new System.Windows.Thickness(0, 0, 0, 7),
                    FontWeight = System.Windows.FontWeights.Normal, Cursor = System.Windows.Input.Cursors.Hand
                };
                uninstall.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "UiCard");
                uninstall.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "UiBorder");
                uninstall.Click += (s, e) =>
                {
                    window.Close();
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { _completeUninstall?.Start(target); }
                        catch (Exception ex) { logger.Error($"Complete uninstall failed: {ex.Message}"); }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                };
                panel.Children.Add(uninstall);
            }

            var footer = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 9, 0, 0) };
            var globalContent = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            globalContent.Children.Add(icon("GearIconGeometry"));
            globalContent.Children.Add(textBlock("Global settings"));
            var global = new System.Windows.Controls.Button
            {
                Content = globalContent, Padding = new System.Windows.Thickness(12, 8, 12, 8),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left
            };
            global.Click += (s, e) =>
            {
                window.Close();
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { OpenGlobalSettingsWindow(); }
                    catch (Exception ex) { logger.Error($"Could not open global settings: {ex.Message}"); }
                }), System.Windows.Threading.DispatcherPriority.Background);
            };
            var close = new System.Windows.Controls.Button
            {
                Content = "Close", MinWidth = 88, Padding = new System.Windows.Thickness(16, 8, 16, 8),
                IsDefault = true, HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            close.Click += (s, e) => window.Close();
            System.Windows.Controls.DockPanel.SetDock(global, System.Windows.Controls.Dock.Left);
            System.Windows.Controls.DockPanel.SetDock(close, System.Windows.Controls.Dock.Right);
            footer.LastChildFill = false;
            footer.Children.Add(global);
            footer.Children.Add(close);
            System.Windows.Controls.DockPanel.SetDock(footer, System.Windows.Controls.Dock.Bottom);
            layout.Children.Add(footer);
            var scroll = new System.Windows.Controls.ScrollViewer
            {
                Content = panel, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled
            };
            layout.Children.Add(scroll);
            return layout;
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            var games = args?.Games;
            if (games == null || games.Count == 0)
                yield break;

            bool globalOn = Settings?.EnableSteamInstantPlay == true;
            var steamGames = games.Where(SteamInstantPlay.IsSteamGame).ToList();
            var instantGames = games.Where(g =>
            {
                var st = SteamInstantPlay.GetStore(g);
                return st != InstantStore.None && StoreInstantPlayEnabled(st);
            }).ToList();

            yield return new GameMenuItem
            {
                MenuSection = "Playnite Overlay 3000",
                Description = "Game settings window…",
                Action = a => OpenPerGameSettings()
            };

            if (games.Count == 1 && _completeUninstall != null && _completeUninstall.ShouldOfferButton(games[0]))
            {
                var target = games[0];
                yield return new GameMenuItem
                {
                    MenuSection = "Playnite Overlay 3000",
                    Description = "Complete uninstall (remove mods and leftovers)",
                    Action = a => _completeUninstall?.Start(target)
                };
            }

            yield return new GameMenuItem
            {
                MenuSection = "Playnite Overlay 3000|Discrete GPU",
                Description = "Run on discrete GPU (high performance)",
                Action = a =>
                {
                    foreach (var g in games) SetGpuForced(g, true);
                    playniteAPI.Dialogs.ShowMessage("Done. Takes effect the next time the game starts.", "Playnite Overlay 3000");
                }
            };

            if (games.Any(g => IsGpuForced(g.Id)))
            {
                yield return new GameMenuItem
                {
                    MenuSection = "Playnite Overlay 3000|Discrete GPU",
                    Description = "Stop forcing discrete GPU (let Windows decide)",
                    Action = a =>
                    {
                        foreach (var g in games) SetGpuForced(g, false);
                        playniteAPI.Dialogs.ShowMessage("Windows will choose the GPU again.", "Playnite Overlay 3000");
                    }
                };
            }

            // --- Global Instant Play master switch ---
            if (globalOn)
            {
                yield return new GameMenuItem
                {
                    MenuSection = "Playnite Overlay 3000|Instant Play",
                    Description = "Disable Instant Play (all Steam games)",
                    Action = a =>
                    {
                        Settings.EnableSteamInstantPlay = false;
                        SaveSettingsSafely(Settings);
                        ApplySteamInstantPlayPolicy();
                        playniteAPI.Dialogs.ShowMessage(
                            "Steam Instant Play is now OFF for all games.\nSteam will launch games normally again.",
                            "Playnite Overlay 3000");
                    }
                };
            }
            else
            {
                yield return new GameMenuItem
                {
                    MenuSection = "Playnite Overlay 3000|Instant Play",
                    Description = "Enable Instant Play (all Steam games)",
                    Action = a =>
                    {
                        Settings.EnableSteamInstantPlay = true;
                        SaveSettingsSafely(Settings);
                        ApplySteamInstantPlayPolicy();
                        playniteAPI.Dialogs.ShowMessage(
                            "Steam Instant Play is now ON.\nSteam games will launch the .exe directly (skips Steam launch pipeline / cloud sync wait).\nYou can still opt individual games out from this menu.",
                            "Playnite Overlay 3000");
                    }
                };
            }

            // --- Per-game opt-out (only for stores whose Instant Play is on) ---
            if (instantGames.Count == 0)
                yield break;

            {
                bool anyDisabled = instantGames.Any(g => Settings.IsInstantPlayDisabledFor(g.Id));
                bool anyEnabled = instantGames.Any(g => !Settings.IsInstantPlayDisabledFor(g.Id));

                if (anyEnabled)
                {
                    yield return new GameMenuItem
                    {
                        MenuSection = "Playnite Overlay 3000|Instant Play",
                        Description = instantGames.Count == 1
                            ? "Use normal launch for this game"
                            : "Use normal launch for selected",
                        Action = a =>
                        {
                            foreach (var g in instantGames)
                                SetInstantPlayForGame(g, false);
                            SaveSettingsSafely(Settings);
                        }
                    };
                }

                if (anyDisabled)
                {
                    yield return new GameMenuItem
                    {
                        MenuSection = "Playnite Overlay 3000|Instant Play",
                        Description = instantGames.Count == 1
                            ? "Use Instant Play for this game"
                            : "Use Instant Play for selected",
                        Action = a =>
                        {
                            foreach (var g in instantGames)
                                SetInstantPlayForGame(g, true);
                            SaveSettingsSafely(Settings);
                        }
                    };
                }
            }
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                MenuSection = "@Playnite Overlay 3000",
                Description = "Check pending complete uninstalls (leftover files)",
                Action = a => _completeUninstall?.CheckPending(true)
            };
            yield return new MainMenuItem
            {
                MenuSection = "@Playnite Overlay 3000",
                Description = "Scan local games now",
                Action = a => _localStore?.RequestScanManual("menu")
            };
        }

        // ---- Per-game "run on discrete GPU" (context menu only) ------------------------------
        // File lines: <game id>|<exe>|<exe>...  (the exes are what we changed in Windows graphics settings)

        private string GpuGamesPath()
        {
            try { return Path.Combine(GetPluginUserDataPath(), "discrete_gpu_games.txt"); }
            catch { return null; }
        }

        private Dictionary<Guid, List<string>> ReadGpuGames()
        {
            var map = new Dictionary<Guid, List<string>>();
            try
            {
                string p = GpuGamesPath();
                if (p == null || !File.Exists(p)) return map;
                foreach (var line in File.ReadAllLines(p))
                {
                    var parts = line.Split('|');
                    if (parts.Length == 0 || !Guid.TryParse(parts[0], out Guid id)) continue;
                    map[id] = parts.Skip(1).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                }
            }
            catch { }
            return map;
        }

        private void WriteGpuGames(Dictionary<Guid, List<string>> map)
        {
            try
            {
                string p = GpuGamesPath();
                if (p == null) return;
                if (map.Count == 0) { if (File.Exists(p)) File.Delete(p); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllLines(p, map.Select(kv => string.Join("|", new[] { kv.Key.ToString() }.Concat(kv.Value))).ToArray());
            }
            catch { }
        }

        private bool IsGpuForced(Guid gameId)
        {
            try { return ReadGpuGames().ContainsKey(gameId); }
            catch { return false; }
        }

        private void RememberGpuExe(Guid gameId, string exe)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(exe)) return;
                var map = ReadGpuGames();
                if (!map.TryGetValue(gameId, out var list)) return; // no longer forced
                if (!list.Any(x => string.Equals(x, exe, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(exe);
                    WriteGpuGames(map);
                }
            }
            catch { }
        }

        private void SetGpuForced(Game game, bool on)
        {
            if (game == null) return;
            var map = ReadGpuGames();
            if (on)
            {
                if (!map.TryGetValue(game.Id, out var list)) { list = new List<string>(); map[game.Id] = list; }
                string exe = ResolveGameExecutable(game, null);
                if (exe != null)
                {
                    if (!list.Any(x => string.Equals(x, exe, StringComparison.OrdinalIgnoreCase))) list.Add(exe);
                    WindowHelper.SetHighPerformanceGpu(exe);
                }
            }
            else if (map.TryGetValue(game.Id, out var existing))
            {
                foreach (var oldExe in existing) WindowHelper.ClearGpuPreference(oldExe);
                map.Remove(game.Id);
            }
            WriteGpuGames(map);
            Breadcrumb.Write("Discrete GPU " + (on ? "ON" : "OFF") + " for " + game.Name);
        }

        /// <summary>Best-effort path of the program a game action will launch (before it starts).</summary>
        private string ResolveGameExecutable(Game game, GameAction action)
        {
            try
            {
                if (game == null) return null;
                if (action == null)
                    action = game.GameActions?.FirstOrDefault(a => a.IsPlayAction);
                if (action == null) return null;

                if (action.Type == GameActionType.File)
                {
                    string p = playniteAPI.ExpandGameVariables(game, action.Path ?? "");
                    if (string.IsNullOrWhiteSpace(p)) return null;
                    if (!Path.IsPathRooted(p))
                    {
                        string wd = playniteAPI.ExpandGameVariables(game, action.WorkingDir ?? "");
                        if (string.IsNullOrWhiteSpace(wd)) wd = game.InstallDirectory ?? "";
                        p = Path.Combine(wd, p);
                    }
                    return File.Exists(p) ? Path.GetFullPath(p) : null;
                }

                if (action.Type == GameActionType.Emulator)
                {
                    var emu = playniteAPI.Database.Emulators.Get(action.EmulatorId);
                    var prof = emu?.CustomProfiles?.FirstOrDefault(x => x.Id == action.EmulatorProfileId);
                    if (prof != null && !string.IsNullOrWhiteSpace(prof.Executable))
                    {
                        string p = prof.Executable.Replace("{EmulatorDir}", emu.InstallDir ?? "");
                        p = playniteAPI.ExpandGameVariables(game, p);
                        return File.Exists(p) ? Path.GetFullPath(p) : null;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Launchers and emulators start the real game process later, so remember the programs that
        /// own the game window too. This helps the NEXT launch (the running process is already started).
        /// </summary>
        private void ScheduleGpuPreferenceForRunningGame(int startedPid, Guid gameId)
        {
            Action pass = () =>
            {
                try
                {
                    var exes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var self = WindowHelper.GetProcessExePath(startedPid);
                    if (self != null) exes.Add(self);
                    var hwnd = WindowHelper.FindMainWindow(startedPid);
                    var owner = WindowHelper.GetProcessExePath(WindowHelper.GetWindowOwnerPid(hwnd));
                    if (owner != null) exes.Add(owner);
                    foreach (var exe in exes)
                    {
                        RememberGpuExe(gameId, exe); // so "Stop forcing" can undo it later
                        if (WindowHelper.SetHighPerformanceGpu(exe))
                            Breadcrumb.Write("GPU preference set to High performance (applies next launch): " + exe);
                    }
                }
                catch { }
            };

            try { if (startedPid > 0) pass(); } catch { }
            try
            {
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
                t.Tick += (s, e) => { t.Stop(); if (startedPid > 0) pass(); };
                t.Start();
            }
            catch { }
        }

        private string FakeFullscreenBackupPath()
        {
            try { return System.IO.Path.Combine(GetPluginUserDataPath(), "fake_fullscreen_backup.txt"); }
            catch { return null; }
        }

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            // Cover the interval before Playnite sets IsLaunching/IsRunning.
            _linkLaunchGuards[args?.Game?.Id ?? Guid.Empty] = DateTime.UtcNow.AddSeconds(30).Ticks;
            try
            {
                // Must be in place before the game process creates its swap chain.
                if (Settings?.FakeFullscreen == true)
                    WindowHelper.ApplyFakeFullscreen(FakeFullscreenBackupPath());
                if (Settings?.EnableWindowsGameModeRegistration == true)
                    OverlayGameMode.OnStarting(playniteAPI, args?.Game);

                // Windows reads the GPU preference when the program starts, so set it before launch.
                if (args?.Game != null && IsGpuForced(args.Game.Id))
                {
                    string exe = ResolveGameExecutable(args?.Game, args?.SourceAction);
                    if (exe != null && WindowHelper.SetHighPerformanceGpu(exe))
                        Breadcrumb.Write("GPU preference set to High performance: " + exe);
                }
            }
            catch (Exception ex)
            {
                logger.Error($"OnGameStarting failed: {ex}");
            }
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            _linkLaunchGuards[args?.Game?.Id ?? Guid.Empty] = DateTime.UtcNow.AddSeconds(30).Ticks;
            try
            {
                // Clear any leftover park/soft-suspend from a previous session so a new
                // launch never inherits a frozen process tree or bad IsParked state.
                try { WindowHelper.ForceClearAllSuspendState(); } catch { }
                Breadcrumb.Write("OnGameStarted " + (args?.Game?.Name ?? "?"));
                _hotkeyGamePid = 0;
                _fgGameCachePid = 0;
                StartUnmuteRetry();
                MigrateLegacyGameVolumeAtStart(args?.Game?.Id ?? Guid.Empty, args?.StartedProcessId ?? 0);

                cachedActualGamePid = -1;
                gameStarted = DateTime.Now;
                StartUnfocusedTracking();
                int startedPid = args?.StartedProcessId ?? 0;
                if (args?.Game != null && IsGpuForced(args.Game.Id))
                    ScheduleGpuPreferenceForRunningGame(startedPid, args.Game.Id);
                if (Settings?.EnableWindowsGameModeRegistration == true)
                    OverlayGameMode.OnStarted(args?.Game, startedPid);
                // Never track Playnite itself as the "game" process
                if (startedPid > 0 && WindowHelper.IsProtectedProcess(startedPid))
                    startedPid = 0;

                var gameOverlayData = CreateGameOverlayData(args?.Game, startedPid > 0 ? (int?)startedPid : null, gameStarted, loadAchievements: false);
                GameOverlayData = gameOverlayData;
                // Achievement reads use native SQLite interop. Defer them until the user
                // explicitly opens the overlay instead of running a launch-time task.
                RefreshGlobalSettingsCoverSource();

                try
                {
                    if (Settings?.BorderlessWindowedGames == true && startedPid > 0)
                        WindowHelper.StartBorderlessWatch(startedPid, m => log(m, "INFO"));
                    else
                        WindowHelper.StopBorderlessWatch();
                }
                catch { }

                if (overlayWindow != null)
                    overlayWindow.UpdateGameOverlay(gameOverlayData);

                // Only auto-apply RTSS when FPS Limiter extension is NOT installed.
                // When it is installed, it applies the correct Desktop/Fullscreen profile itself.
                try { TryAutoApplyFpsLimit(gameOverlayData); } catch { }
            }
            catch (Exception ex)
            {
                logger.Error($"Error preparing game overlay data: {ex}");
            }
        }

        /// <summary>
        /// Fallback RTSS apply when the standalone FPS Limiter extension is not installed.
        /// Does not override FPS Limiter when that extension is present.
        /// </summary>
        private void TryAutoApplyFpsLimit(GameOverlayData data)
        {
            try
            {
                if (Settings == null || !Settings.FpsAutoApplyOnGameStart)
                    return;

                // FPS Limiter extension handles Desktop + Fullscreen caps — do not fight it
                if (FpsLimiterBridge.IsAvailable(playniteAPI) ||
                    FpsLimiterSettingsSync.IsFpsLimiterInstalled(playniteAPI))
                {
                    log("FPS auto-apply skipped: FPS Limiter extension owns RTSS", "DEBUG");
                    return;
                }

                if (!OverlayRtssController.IsRtssAvailable())
                {
                    log("FPS auto-apply skipped: RTSS not found", "DEBUG");
                    return;
                }

                double limit = Settings.FpsLimit;
                OverlayFpsSyncMode sync = (OverlayFpsSyncMode)Settings.FpsSyncMode;
                bool reflex = Settings.FpsReflexMarkers;

                OverlayRtssController.Apply(data?.ExecutablePath, limit, sync, reflex);
                log($"FPS auto-apply (no FPS Limiter): {limit} FPS, sync={sync}, reflexMarkers={reflex}");
            }
            catch (Exception ex)
            {
                logger.Error($"FPS auto-apply failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply FPS changes through the live FPS Limiter LimiterService (same path as its menus).
        /// Falls back to direct RTSS only if the plugin is not loaded.
        /// </summary>
        internal void ApplyFpsViaLimiter(double limit, OverlayFpsSyncMode sync, bool reflexMarkers, bool disableCap, bool toggleVrr)
        {
            if (Settings != null)
            {
                Settings.FpsLimit = disableCap ? 0 : limit;
                Settings.FpsSyncMode = (int)sync;
                Settings.FpsReflexMarkers = reflexMarkers;
                SaveSettingsSafely(Settings);
            }

            try
            {
                Game game = null;
                if (GameOverlayData != null && GameOverlayData.GameId != Guid.Empty)
                    game = playniteAPI.Database.Games.Get(GameOverlayData.GameId);

                if (FpsLimiterBridge.IsAvailable(playniteAPI))
                {
                    if (toggleVrr)
                    {
                        FpsLimiterBridge.ToggleVrr(playniteAPI);
                        log("FPS Limiter: toggled VRR refresh-rate switching");
                        return;
                    }

                    if (disableCap)
                    {
                        // Disable means an explicit 0 FPS override, not removing the game
                        // override (which would fall back to the global cap).
                        bool ok = game != null
                            ? FpsLimiterBridge.SetGameLimit(playniteAPI, game, 0)
                            : FpsLimiterBridge.SetGlobalLimit(playniteAPI, 0);
                        if (!ok)
                            throw new InvalidOperationException("Could not set the FPS limit to 0 in FPS Limiter.");
                        log("FPS Limiter: set cap to 0 (unlimited)");
                        return;
                    }

                    // Apply limit + sync + reflex through LimiterService (saves + reapply running)
                    if (game != null)
                    {
                        // In RTSS Scanline Sync the presets represent *display Hz*, not arbitrary FPS.
                        // The limiter's SetGameForcedRefreshRate method records the selected display
                        // mode AND resolves the precise cap (e.g. 30 Hz -> 29.9014 FPS). Calling
                        // SetGameLimit afterwards would overwrite that cap or leave it pinned to a
                        // prior mode. For other sync methods keep the original ordinary FPS path.
                        bool useSk = FpsLimiterBridge.ShouldUseSpecialKBackend(playniteAPI, game);
                        bool scanline = !useSk && (sync == OverlayFpsSyncMode.ScanlineSync ||
                            sync == OverlayFpsSyncMode.ScanlineSyncHalf);
                        bool syncOk = useSk || FpsLimiterBridge.SetGameSyncMode(playniteAPI, game, (int)sync);
                        bool capOk;
                        if (scanline)
                        {
                            int hz = (int)Math.Round(limit, MidpointRounding.AwayFromZero);
                            if (hz <= 0 || Math.Abs(limit - hz) > 0.1)
                            {
                                log($"FPS Limiter: invalid Scanline refresh rate {limit}", "ERROR");
                                return;
                            }
                            capOk = FpsLimiterBridge.SetGameForcedRefreshRate(playniteAPI, game, hz);
                            log($"FPS Limiter: Scanline refresh request {hz} Hz (precise FPS cap selected by limiter)");
                        }
                        else
                        {
                            // Do not carry a Scanline forced-mode override into Async/Front/Back.
                            bool cleared = FpsLimiterBridge.SetGameForcedRefreshRate(playniteAPI, game, 0);
                            capOk = cleared && FpsLimiterBridge.SetGameLimit(playniteAPI, game, limit);
                        }
                        bool reflexOk = useSk || FpsLimiterBridge.SetGameReflexMarkers(playniteAPI, game, reflexMarkers);
                        if (!syncOk || !capOk || !reflexOk)
                        {
                            log($"FPS Limiter bridge FAILED: sync={syncOk}, cap={capOk}, reflex={reflexOk}, game={game.Id}", "ERROR");
                            return;
                        }
                        log($"FPS Limiter bridge applied: game={game.Id}, fps={limit}, sync={sync}");
                    }
                    else
                    {
                        FpsLimiterBridge.SetGlobalLimit(playniteAPI, limit);
                        FpsLimiterBridge.SetGlobalSyncMode(playniteAPI, (int)sync);
                        FpsLimiterBridge.SetGlobalReflexMarkers(playniteAPI, reflexMarkers ? 0 : 1);
                    }
                    log($"FPS Limiter service applied: {limit}, {sync}, reflex={reflexMarkers}");
                    return;
                }

                // Do not bypass an installed limiter's saved state with a conflicting RTSS write.
                if (FpsLimiterSettingsSync.IsFpsLimiterInstalled(playniteAPI))
                    throw new InvalidOperationException("FPS Limiter is installed but its service is unavailable. Restart Playnite and try again.");

                // Fallback when FPS Limiter is not installed
                log("FPS Limiter not loaded — falling back to direct RTSS", "DEBUG");
                if (Settings != null)
                    OverlayRtssController.ScanlineSyncTarget = Settings.FpsScanlineSyncTarget;
                OverlayRtssController.Apply(
                    GameOverlayData?.ExecutablePath,
                    disableCap ? 0 : limit,
                    sync,
                    reflexMarkers);
            }
            catch (Exception ex)
            {
                logger.Error($"ApplyFpsViaLimiter failed: {ex.Message}");
                if (disableCap)
                {
                    try { overlayWindow?.RefreshFpsFromLimiter(playniteAPI, GameOverlayData?.GameId); } catch { }
                    try { playniteAPI.Dialogs.ShowErrorMessage(ex.Message, "FPS Limiter"); } catch { }
                }
            }
        }

        internal void PersistFpsSettings(double limit, OverlayFpsSyncMode sync, bool reflexMarkers)
        {
            ApplyFpsViaLimiter(limit, sync, reflexMarkers, disableCap: limit <= 0, toggleVrr: false);
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            Breadcrumb.Write("OnGameStopped " + (args?.Game?.Name ?? "?"));
            long ignoredLinkLaunchGuard;
            _linkLaunchGuards.TryRemove(args?.Game?.Id ?? Guid.Empty, out ignoredLinkLaunchGuard);
            try
            {
                ApplyPlaytimeAdjustments(args);
            }
            catch (Exception ex)
            {
                logger.Error($"Playtime adjustment failed: {ex.Message}");
            }
            finally
            {
                // Unfreeze anything left over (soft-pause / park) BEFORE touching the overlay.
                // Order matters: clearing flags then Hide() used to skip SoftResume.
                try { WindowHelper.StopBorderlessWatch(); } catch { }
                try { WindowHelper.RestoreFakeFullscreen(FakeFullscreenBackupPath()); } catch { }
                try { UnmuteGameIfMutedByOverlay("game stopped"); } catch { }
                // Defer until Playnite has cleared running/launching state for all games.
                try { MarkLinkWorkDeferred(); } catch { }
                try { WindowHelper.SoftResumeFromOverlay(); } catch { }
                try { WindowHelper.ForceClearAllSuspendState(); } catch { }
                try { WindowHelper.ReleaseMouseLock(nudgeCursor: false); } catch { }
                try
                {
                    if (overlayWindow != null)
                    {
                        try { overlayWindow.Dispatcher.Invoke(() =>
                        {
                            try { overlayWindow.Topmost = false; } catch { }
                            if (overlayWindow.IsVisible)
                                overlayWindow.Hide();
                        }); }
                        catch
                        {
                            try { if (overlayWindow.IsVisible) overlayWindow.Hide(); } catch { }
                        }
                    }
                }
                catch { }
            }

            try { overlayWindow?.UpdateGameOverlay(null); } catch { }
            GameOverlayData = null;
            RefreshGlobalSettingsCoverSource();
            cachedActualGamePid = -1;
            Breadcrumb.Write("OnGameStopped done");
            // Do not force-focus Playnite — let the shell handle window activation itself.
        }

        // ---- Playtime: time while the game is not the focused window ----
        private System.Threading.Timer _unfocusedTimer;
        private readonly object _unfocusedGate = new object();
        private long _unfocusedMs;
        private long _unfocusedLastTick;

        private void StartUnfocusedTracking()
        {
            lock (_unfocusedGate)
            {
                try { _unfocusedTimer?.Dispose(); } catch { }
                _unfocusedMs = 0;
                _unfocusedLastTick = (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
                _unfocusedTimer = new System.Threading.Timer(_ => SampleUnfocused(), null, 1000, 1000);
            }
        }

        /// <summary>Stops sampling and returns the unfocused seconds collected for this session.</summary>
        private long StopUnfocusedTracking()
        {
            lock (_unfocusedGate)
            {
                try { _unfocusedTimer?.Dispose(); } catch { }
                _unfocusedTimer = null;
                long sec = _unfocusedMs / 1000;
                _unfocusedMs = 0;
                return sec;
            }
        }

        private void SampleUnfocused()
        {
            try
            {
                lock (_unfocusedGate)
                {
                    if (_unfocusedTimer == null) return;
                    long now = (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
                    long elapsed = Math.Max(0, Math.Min(now - _unfocusedLastTick, 5000));
                    _unfocusedLastTick = now;

                    var game = playniteAPI?.Database?.Games?.FirstOrDefault(g => g.IsRunning);
                    if (game == null) return;

                    if (Settings?.SubtractSuspendedFromPlaytime != true) return;
                    // Parked / paused-by-overlay time is already counted as suspended time.
                    if (WindowHelper.IsParked || WindowHelper.IsSuspendSegmentActive) return;

                    bool focused = false;
                    IntPtr hwnd = WindowHelper.GetForegroundWindow();
                    if (hwnd != IntPtr.Zero)
                    {
                        WindowHelper.GetWindowThreadProcessId(hwnd, out uint fgPid);
                        focused = fgPid > 4 && (int)fgPid != _ownPid && IsRunningGameForeground((int)fgPid);
                    }
                    if (!focused) _unfocusedMs += elapsed;
                }
            }
            catch { }
        }

        /// <summary>
        /// PlayState-style playtime tweaks: subtract suspended time and/or drop short sessions.
        /// </summary>
        private void ApplyPlaytimeAdjustments(OnGameStoppedEventArgs args)
        {
            if (Settings == null || args?.Game == null)
                return;

            var game = args.Game;
            bool isLibrary = game.PluginId != Guid.Empty;

            long deduct = 0;

            // Session length from Playnite (seconds)
            ulong sessionSec = 0;
            try { sessionSec = args.ElapsedSeconds; }
            catch
            {
                try { sessionSec = (ulong)Math.Max(0, (DateTime.Now - gameStarted).TotalSeconds); }
                catch { }
            }

            if (Settings.ExcludeShortSessions)
            {
                int minSec = Math.Max(0, Settings.MinSessionMinutes) * 60;
                if (sessionSec > 0 && (long)sessionSec < minSec)
                    deduct = (long)sessionSec;
            }

            // Time the game window was not in the foreground (alt-tabbed, Playnite or another app focused).
            long unfocused = StopUnfocusedTracking();

            if (Settings.SubtractSuspendedFromPlaytime)
            {
                if (!Settings.SubtractSuspendedOnlyNonLibrary || !isLibrary)
                {
                    long suspended = WindowHelper.ConsumeAccumulatedSuspendedSeconds();
                    if (suspended > 0)
                        deduct = Math.Max(deduct, 0) + suspended;
                    if (unfocused > 0)
                        deduct = Math.Max(deduct, 0) + unfocused;
                }
                else
                {
                    WindowHelper.ConsumeAccumulatedSuspendedSeconds();
                }
            }
            else
            {
                WindowHelper.ConsumeAccumulatedSuspendedSeconds(); // clear tracking
            }

            // Suspended, unfocused and short-session time can overlap: never remove more than the session itself.
            if (sessionSec > 0 && deduct > (long)sessionSec)
                deduct = (long)sessionSec;

            if (deduct <= 0)
                return;

            try
            {
                // Prefer live DB object
                var dbGame = playniteAPI.Database.Games.Get(game.Id) ?? game;
                ulong pt = dbGame.Playtime;
                ulong remove = (ulong)Math.Min((long)pt, deduct);
                if (remove == 0) return;
                dbGame.Playtime = pt - remove;
                Breadcrumb.Write("Games.Update (playtime adjust)");
                playniteAPI.Database.Games.Update(dbGame);
                log($"Playtime adjusted: -{remove}s for {dbGame.Name} (session={sessionSec}s)", "INFO");
            }
            catch (Exception ex)
            {
                logger.Error($"Failed to update playtime: {ex.Message}");
            }
        }

        /// <summary>Resolve the running game process for volume hotkeys.</summary>
        private bool TryGetRunningGameAudioTarget(out int pid, out string exePath)
        {
            pid = 0;
            exePath = null;
            var runningGame = playniteAPI?.Database?.Games?.FirstOrDefault(g => g.IsRunning);
            if (runningGame == null)
                return false;

            // Prefer process already tracked by an open overlay
            if (overlayWindow?.ViewModel != null && overlayWindow.ViewModel.ProcessId > 0)
            {
                pid = overlayWindow.ViewModel.ProcessId;
                exePath = overlayWindow.ViewModel.GameExecutablePath;
                return true;
            }

            var proc = FindRunningGameProcess(runningGame, null);
            if (proc == null)
            {
                // Fall back to the process that had focus when the hotkey was pressed.
                int hp = _hotkeyGamePid;
                if (hp > 0 && WindowHelper.IsRestorableGameProcess(hp))
                {
                    pid = hp;
                    exePath = WindowHelper.GetProcessExePath(hp);
                    return true;
                }
                return false;
            }
            pid = proc.Id;
            try { exePath = proc.MainModule?.FileName; } catch { }
            return pid > 0;
        }

        /// <summary>Restore the selected per-game volume mode once the game's audio session exists.</summary>
        private void MigrateLegacyGameVolumeAtStart(Guid gameId, int startedPid)
        {
            try
            {
                if (gameId == Guid.Empty) return;
                bool perGameMode = Settings?.UsesPerGameVolume == true;
                if (perGameMode)
                    MasterVolumeProfiles.MigrateLegacyToGameVolume(gameId);
                bool hadLegacy = MasterVolumeProfiles.TryGetLegacy(gameId, out _);
                bool hasPerGameProfile = MasterVolumeProfiles.TryGetGameVolume(gameId, out _);
                if (perGameMode ? !hasPerGameProfile : (!hadLegacy && !hasPerGameProfile))
                    return;

                int pid = startedPid;
                string exePath = pid > 0 ? WindowHelper.GetProcessExePath(pid) : null;
                if (pid <= 0)
                {
                    var game = playniteAPI?.Database?.Games?.FirstOrDefault(g => g.Id == gameId);
                    var process = game != null ? FindRunningGameProcess(game, null) : null;
                    if (process != null)
                    {
                        pid = process.Id;
                        try { exePath = process.MainModule?.FileName; } catch { }
                        try { process.Dispose(); } catch { }
                    }
                }
                if (pid <= 0)
                    return;

                int targetPid = pid;
                string targetExe = exePath;
                System.Threading.ThreadPool.QueueUserWorkItem(workerState =>
                {
                    bool useGameVolume = Settings?.UsesPerGameVolume == true;
                    bool hasProfile = MasterVolumeProfiles.TryGetGameVolume(gameId, out float savedGameVolume);
                    float targetVolume = useGameVolume && hasProfile ? savedGameVolume : 1f;
                    bool ok = useGameVolume
                        ? hasProfile && GameAudioSession.TrySetVolume(targetPid, targetExe, targetVolume)
                        : GameAudioSession.TrySetVolume(targetPid, targetExe, 1f);
                    if (ok && hadLegacy && !useGameVolume)
                        MasterVolumeProfiles.RemoveLegacy(gameId);
                    Breadcrumb.Write((useGameVolume ? "Restore per-game volume" : "Normalize app volume for master mode") +
                        ": ok=" + ok);
                });

                var retry = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(4)
                };
                retry.Tick += (s, e) =>
                {
                    retry.Stop();
                    try
                    {
                        var running = playniteAPI?.Database?.Games?.FirstOrDefault(g => g.IsRunning);
                        if (running == null || running.Id != gameId) return;
                        bool useGameVolume = Settings?.UsesPerGameVolume == true;
                        if (useGameVolume)
                            MasterVolumeProfiles.MigrateLegacyToGameVolume(gameId);
                        bool legacy = MasterVolumeProfiles.TryGetLegacy(gameId, out _);
                        bool hasGameProfile = MasterVolumeProfiles.TryGetGameVolume(gameId, out float saved);
                        if (useGameVolume ? !hasGameProfile : (!legacy && !hasGameProfile)) return;

                        int retryPid = targetPid;
                        string retryExe = targetExe;
                        if (TryGetRunningGameAudioTarget(out int resolvedPid, out string resolvedExe))
                        {
                            retryPid = resolvedPid;
                            retryExe = resolvedExe;
                        }

                        System.Threading.ThreadPool.QueueUserWorkItem(state =>
                        {
                            float target = useGameVolume ? saved : 1f;
                            if (GameAudioSession.TrySetVolume(retryPid, retryExe, target))
                            {
                                if (!useGameVolume && legacy)
                                    MasterVolumeProfiles.RemoveLegacy(gameId);
                                Breadcrumb.Write(useGameVolume
                                    ? "Restored per-game volume on retry."
                                    : "Normalized app volume for master mode on retry.");
                            }
                        });
                    }
                    catch { }
                };
                retry.Start();
            }
            catch { }
        }

        private void AdjustVolume(int stepPercent)
        {
            var runningGame = playniteAPI?.Database?.Games?.FirstOrDefault(g => g.IsRunning);
            if (runningGame == null ||
                !TryGetRunningGameAudioTarget(out int processId, out string exePath))
                return;

            Guid gameId = runningGame.Id;
            bool perGame = Settings?.UsesPerGameVolume == true;
            System.Threading.ThreadPool.QueueUserWorkItem(workerState =>
            {
                float volume = 1f;
                bool readable = perGame
                    ? processId > 0 && GameAudioSession.TryGetVolume(processId, exePath, out volume, out _)
                    : GameAudioSession.TryGetMasterVolume(out volume, out _);
                if (!readable) return;

                float next = Math.Max(0f, Math.Min(1f, volume + stepPercent / 100f));
                bool changed = perGame
                    ? GameAudioSession.TrySetVolume(processId, exePath, next)
                    : GameAudioSession.TrySetMasterVolume(next);
                if (!changed)
                    return;
                if (perGame)
                    MasterVolumeProfiles.SaveGameVolume(gameId, next);
                else
                    MasterVolumeProfiles.Save(gameId, next);

                try
                {
                    if (overlayWindow?.IsVisible == true && overlayWindow.ViewModel != null)
                    {
                        overlayWindow.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            overlayWindow.ViewModel.RefreshAudioState();
                        }), System.Windows.Threading.DispatcherPriority.Background);
                    }
                }
                catch { }
            });
        }

        // ---- Per-game mute -------------------------------------------------------------------
        // Mutes only the game's own audio session (like the Windows volume mixer), never the system.
        private bool _gameMuteActive;
        private int _gameMutePid;
        private string _gameMuteExe;
        private System.Windows.Threading.DispatcherTimer _unmuteRetryTimer;

        private string MutedExesPath()
        {
            try { return System.IO.Path.Combine(GetPluginUserDataPath(), "muted_game_exes.txt"); }
            catch { return null; }
        }

        private List<string> ReadMutedExes()
        {
            var list = new List<string>();
            try
            {
                string p = MutedExesPath();
                if (p != null && File.Exists(p))
                    foreach (var line in File.ReadAllLines(p))
                        if (!string.IsNullOrWhiteSpace(line)) list.Add(line.Trim());
            }
            catch { }
            return list;
        }

        private void WriteMutedExes(List<string> list)
        {
            try
            {
                string p = MutedExesPath();
                if (p == null) return;
                if (list.Count == 0) { if (File.Exists(p)) File.Delete(p); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllLines(p, list.ToArray());
            }
            catch { }
        }

        // Windows remembers a muted app between launches, so remember what we muted until it is undone.
        private void RememberMutedExe(string exe)
        {
            if (string.IsNullOrWhiteSpace(exe)) return;
            var list = ReadMutedExes();
            if (!list.Any(x => string.Equals(x, exe, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(exe);
                WriteMutedExes(list);
            }
        }

        private void ForgetMutedExe(string exe)
        {
            if (string.IsNullOrWhiteSpace(exe)) return;
            var list = ReadMutedExes();
            if (list.RemoveAll(x => string.Equals(x, exe, StringComparison.OrdinalIgnoreCase)) > 0)
                WriteMutedExes(list);
        }

        private void ToggleRunningGameMute()
        {
            if (!TryGetRunningGameAudioTarget(out int pid, out string exePath))
                return;

            if (!GameAudioSession.TryGetVolume(pid, exePath, out _, out bool muted))
            {
                log("Mute: the game has no audio session yet (it has not played any sound).", "INFO");
                return;
            }

            bool target = _gameMuteActive ? false : !muted;
            if (!GameAudioSession.TrySetMute(pid, exePath, target))
                return;

            Breadcrumb.Write("Game mute -> " + target);
            if (target)
            {
                _gameMuteActive = true;
                _gameMutePid = pid;
                _gameMuteExe = exePath;
                RememberMutedExe(exePath);
            }
            else
            {
                _gameMuteActive = false;
                ForgetMutedExe(exePath ?? _gameMuteExe);
            }

            if (overlayWindow?.IsVisible == true && overlayWindow.ViewModel != null)
            {
                try
                {
                    overlayWindow.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        overlayWindow.ViewModel.RefreshAudioState();
                    }));
                }
                catch { }
            }
        }

        /// <summary>The overlay's mute button changed the game's mute: track it exactly like the hotkey does.</summary>
        private void OnOverlayGameMuteApplied(int pid, string exe, bool muted)
        {
            try
            {
                if (muted)
                {
                    _gameMuteActive = true;
                    _gameMutePid = pid;
                    _gameMuteExe = exe;
                    RememberMutedExe(exe);
                }
                else
                {
                    _gameMuteActive = false;
                    ForgetMutedExe(exe ?? _gameMuteExe);
                }
                Breadcrumb.Write("Game mute (overlay button) -> " + muted);
            }
            catch { }
        }

        /// <summary>Undo a mute this extension applied (game exit / Playnite exit).</summary>
        private void UnmuteGameIfMutedByOverlay(string reason)
        {
            if (!_gameMuteActive) return;
            _gameMuteActive = false;
            bool ok = false;
            try { ok = GameAudioSession.TrySetMute(_gameMutePid, _gameMuteExe, false); } catch { }
            Breadcrumb.Write("Unmute game (" + reason + ") ok=" + ok);
            // If the session is already gone the exe stays remembered; StartUnmuteRetry clears it next launch.
            if (ok) ForgetMutedExe(_gameMuteExe);
        }

        /// <summary>After a launch, undo any mute Windows may have kept from an earlier session.</summary>
        private void StartUnmuteRetry()
        {
            try
            {
                if (ReadMutedExes().Count == 0) return;
                _unmuteRetryTimer?.Stop();
                int ticks = 0;
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                t.Tick += (s, e) =>
                {
                    ticks++;
                    try
                    {
                        var remembered = ReadMutedExes();
                        // Prefer the running game's real pid; fall back to matching by exe name.
                        int rpid = 0; string rexe = null;
                        bool haveRunning = false;
                        try { haveRunning = TryGetRunningGameAudioTarget(out rpid, out rexe); } catch { }

                        foreach (var exe in remembered)
                        {
                            int usePid = (haveRunning && string.Equals(rexe, exe, StringComparison.OrdinalIgnoreCase)) ? rpid : 0;
                            if (GameAudioSession.TryGetVolume(usePid, exe, out _, out bool m))
                            {
                                if (!m || GameAudioSession.TrySetMute(usePid, exe, false))
                                {
                                    Breadcrumb.Write("Cleared leftover game mute (muted=" + m + ")");
                                    ForgetMutedExe(exe);
                                }
                            }
                        }
                    }
                    catch { }
                    // The game may not play sound for a while: keep trying for ~3 minutes.
                    if (ticks >= 180 || ReadMutedExes().Count == 0) t.Stop();
                };
                _unmuteRetryTimer = t;
                t.Start();
            }
            catch { }
        }

        /// <summary>
        /// Parse hotkeys like "Alt+Shift+S", "Alt+Oem3", "Ctrl+Alt+F4".
        /// </summary>
        private static bool HotkeyMatches(string hotkey, Keys key, bool alt, bool shift, bool ctrl)
        {
            if (string.IsNullOrWhiteSpace(hotkey))
                return false;

            bool needAlt = false, needShift = false, needCtrl = false;
            Keys needKey = Keys.None;

            foreach (var part in hotkey.Split(new[] { '+', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = part.Trim();
                if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase) ||
                    p.Equals("Menu", StringComparison.OrdinalIgnoreCase))
                { needAlt = true; continue; }
                if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                { needShift = true; continue; }
                if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                    p.Equals("Control", StringComparison.OrdinalIgnoreCase))
                { needCtrl = true; continue; }
                if (p.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                    p.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                    continue; // not tracked in our hook

                // Key name
                if (p.Equals("`", StringComparison.OrdinalIgnoreCase) ||
                    p.Equals("Backtick", StringComparison.OrdinalIgnoreCase) ||
                    p.Equals("Tilde", StringComparison.OrdinalIgnoreCase))
                { needKey = Keys.Oem3; continue; }

                if (Enum.TryParse(p, true, out Keys parsed))
                    needKey = parsed;
                else if (p.Length == 1 && Enum.TryParse(p.ToUpperInvariant(), true, out Keys parsed2))
                    needKey = parsed2;
            }

            if (needKey == Keys.None)
                return false;
            if (needAlt != alt) return false;
            if (needShift != shift) return false;
            if (needCtrl != ctrl) return false;
            return key == needKey;
        }


        /// <summary>
        /// If the running game window is minimized or not in the foreground, restore and
        /// force-focus it. Returns true when focus was applied (caller should not open overlay).
        /// Returns false when the game is already focused — overlay toggle may proceed.
        /// </summary>
        private bool TryFocusRunningGame(Game game)
        {
            int pid = 0;
            try
            {
                if (GameOverlayData != null && GameOverlayData.ProcessId > 0)
                    pid = GameOverlayData.ProcessId;
            }
            catch { }
            try
            {
                if (pid <= 0 && overlayWindow?.ViewModel != null && overlayWindow.ViewModel.ProcessId > 0)
                    pid = overlayWindow.ViewModel.ProcessId;
            }
            catch { }
            try
            {
                if (pid <= 0 && game != null)
                {
                    var proc = FindRunningGameProcess(game, null);
                    if (proc != null && !proc.HasExited)
                        pid = proc.Id;
                }
            }
            catch { }
            IntPtr hwnd = IntPtr.Zero;
            if (pid > 0)
            {
                hwnd = WindowHelper.FindMainWindow(pid);
                if (hwnd == IntPtr.Zero)
                {
                    try
                    {
                        using (var proc = System.Diagnostics.Process.GetProcessById(pid))
                            hwnd = proc.MainWindowHandle;
                    }
                    catch { }
                }
            }

            // The tracked PID can be a launcher stub that no longer owns a window: look for the
            // game's window among processes running from its install folder instead.
            if (hwnd == IntPtr.Zero && game != null && !string.IsNullOrWhiteSpace(game.InstallDirectory))
            {
                try
                {
                    string dir = playniteAPI.ExpandGameVariables(game, game.InstallDirectory);
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                        hwnd = WindowHelper.FindMainWindowInDirectory(dir);
                }
                catch { }
            }
            if (hwnd == IntPtr.Zero)
                return false;

            uint hwndPid = 0;
            try { WindowHelper.GetWindowThreadProcessId(hwnd, out hwndPid); } catch { }

            bool minimized = false;
            try { minimized = WindowHelper.IsIconic(hwnd); } catch { }

            bool notForeground = true;
            try
            {
                IntPtr fg = WindowHelper.GetForegroundWindow();
                if (fg == hwnd)
                    notForeground = false;
                else if (fg != IntPtr.Zero)
                {
                    WindowHelper.GetWindowThreadProcessId(fg, out uint fgPid);
                    if (fgPid == (uint)pid || (hwndPid != 0 && fgPid == hwndPid))
                        notForeground = false;
                }
            }
            catch { }

            if (!minimized && !notForeground)
                return false;

            try { WindowHelper.ForceFocusWindow(hwnd); }
            catch
            {
                try { WindowHelper.SafeRestoreAndFocus(hwnd); } catch { }
            }
            try { cursorAutoHide?.ForceShow(); } catch { }
            try { WindowHelper.ReleaseMouseLock(nudgeCursor: false); } catch { }
            return true;
        }

        private void ShowGameOverlay(Game game)

        {
            if (game == null || overlayWindow == null)
                return;

            int? pid = FindRunningGameProcess(game, null)?.Id;
            if ((!pid.HasValue || pid.Value <= 0) && GameOverlayData != null && GameOverlayData.ProcessId > 0)
                pid = GameOverlayData.ProcessId;

            // Fast path: reuse process/session metadata from game start, but always
            // re-read achievements so unlocks during the session appear live.
            if (GameOverlayData != null &&
                GameOverlayData.GameId == game.Id &&
                (pid == null || pid.Value <= 0 || GameOverlayData.ProcessId == pid.Value || GameOverlayData.ProcessId <= 0))
            {
                if (pid.HasValue && pid.Value > 0 && GameOverlayData.ProcessId != pid.Value)
                    GameOverlayData.ProcessId = pid.Value;

                try
                {
                    GameOverlayData.Playtime = TimeSpan.FromSeconds(game.Playtime);
                }
                catch { }

                // Live-sync achievements on every overlay open
                try
                {
                    GameOverlayData.Achievements = GetGameAchievements(game);
                }
                catch (Exception ex)
                {
                    log($"Failed to refresh achievements on overlay open: {ex.Message}", "ERROR");
                }

                RefreshGlobalSettingsCoverSource();
                overlayWindow.UpdateGameOverlay(GameOverlayData);
                overlayWindow.RefreshFpsFromLimiter(playniteAPI, game.Id);
                overlayWindow.ShowOverlay();
                return;
            }

            // Slow path: first open or game/process mismatch — build full data once
            var gameOverlayData = CreateGameOverlayData(game, pid, gameStarted);
            GameOverlayData = gameOverlayData;
            RefreshGlobalSettingsCoverSource();
            overlayWindow.UpdateGameOverlay(gameOverlayData);
            overlayWindow.RefreshFpsFromLimiter(playniteAPI, game.Id);
            overlayWindow.ShowOverlay();
        }

        private GameOverlayData CreateGameOverlayData(Game game, int? processId, DateTime StartTime, bool loadAchievements = true)
        {
            if (game == null) return null;

            var achievements = loadAchievements ? GetGameAchievements(game) : new List<AchievementData>();

            string exePath = null;
            if (processId.HasValue && processId.Value > 0)
            {
                try
                {
                    var proc = Process.GetProcessById(processId.Value);
                    exePath = proc.MainModule?.FileName;
                }
                catch { /* access denied / exited */ }
            }

            return new GameOverlayData
            {
                GameId = game.Id,
                GameName = game.Name,
                ProcessId = processId ?? -1,
                GameStartTime = StartTime,
                Playtime = TimeSpan.FromSeconds(game.Playtime),
                CoverImagePath = GetFullCoverImagePath(game),
                BackgroundImagePath = GetFullBackgroundImagePath(game),
                Achievements = achievements,
                ExecutablePath = exePath
            };
        }

        private List<AchievementData> GetGameAchievements(Game game)
        {
            log($"Retrieving achievements for game {game.Name} (ID: {game.Id}, PlayniteAchievements: {isPlayniteAchievementsAvailable}, SuccessStory: {isSuccessStoryAvailable})");
            var achievements = new List<AchievementData>();

            if (game == null)
                return achievements;

            // Prefer PlayniteAchievements (Santodan / upstream), fall back to SuccessStory
            if (isPlayniteAchievementsAvailable)
            {
                try
                {
                    achievements = GetPlayniteAchievementsData(game);
                    if (achievements.Count > 0)
                    {
                        logger.Info($"Retrieved {achievements.Count} achievements from PlayniteAchievements for {game.Name}");
                        return achievements;
                    }
                }
                catch (Exception ex)
                {
                    logger.Error($"Error retrieving achievements from PlayniteAchievements: {ex.Message}");
                }
            }

            if (isSuccessStoryAvailable)
            {
                try
                {
                    achievements = GetSuccessStoryAchievementsData(game);
                    logger.Info($"Retrieved {achievements.Count} achievements from SuccessStory for {game.Name}");
                }
                catch (Exception ex)
                {
                    logger.Error($"Error retrieving achievements from SuccessStory: {ex.Message}");
                }
            }

            return achievements;
        }

        /// <summary>
        /// Reads achievement data produced by PlayniteAchievements
        /// (https://github.com/Santodan/PlayniteAchievements / justin-delano upstream).
        /// 1) Modern SQLite cache: ExtensionsData/{pluginId}/achievement_cache.db
        /// 2) Legacy per-game JSON: ExtensionsData/{pluginId}/achievement_cache/{gameId}.json
        /// </summary>
        private List<AchievementData> GetPlayniteAchievementsData(Game game)
        {
            var achievements = new List<AchievementData>();

            string pluginDataRoot = Path.Combine(
                playniteAPI.Paths.ExtensionsDataPath, playniteAchievementsId.ToString());

            // 1) Modern SQLite database (primary store since PlayniteAchievements ~1.1.0)
            string dbPath = Path.Combine(pluginDataRoot, "achievement_cache.db");
            if (File.Exists(dbPath))
            {
                string snapshotDir = null;
                try
                {
                    // Read a private copy so this extension never holds locks on, or shares SQLite
                    // state with, the live database PlayniteAchievements is using.
                    string readPath = dbPath;
                    try
                    {
                        Breadcrumb.Write("Reading PlayniteAchievements DB snapshot");
                        snapshotDir = CopyAchievementDbSnapshot(dbPath);
                        if (snapshotDir != null)
                            readPath = Path.Combine(snapshotDir, "achievement_cache.db");
                    }
                    catch { snapshotDir = null; }

                    achievements = PlayniteAchievementsSqliteReader.LoadAchievements(
                        readPath, game.Id.ToString(), pluginDataRoot);
                    Breadcrumb.Write("PlayniteAchievements DB read done count=" + achievements.Count);
                    if (achievements.Count > 0)
                        return achievements;
                }
                catch (Exception ex)
                {
                    logger.Error($"Failed to read PlayniteAchievements SQLite cache: {ex.Message}");
                }
                finally
                {
                    if (snapshotDir != null)
                    {
                        try { Directory.Delete(snapshotDir, true); } catch { }
                    }
                }
            }

            // 2) Legacy JSON cache: achievement_cache/{gameId}.json
            string jsonPath = Path.Combine(pluginDataRoot, "achievement_cache", $"{game.Id}.json");
            if (File.Exists(jsonPath))
            {
                try
                {
                    string json = File.ReadAllText(jsonPath);
                    achievements = ParsePlayniteAchievementsData(json);
                    if (achievements.Count > 0)
                        return achievements;
                }
                catch (Exception ex)
                {
                    logger.Error($"Failed to read PlayniteAchievements JSON cache: {ex.Message}");
                }
            }

            // 3) Alternate JSON locations sometimes used by forks / older builds
            string[] alternatePaths = new[]
            {
                Path.Combine(pluginDataRoot, $"{game.Id}.json"),
                Path.Combine(pluginDataRoot, "cache", $"{game.Id}.json"),
                Path.Combine(pluginDataRoot, "GameCache", $"{game.Id}.json")
            };
            foreach (var alt in alternatePaths)
            {
                if (!File.Exists(alt))
                    continue;
                try
                {
                    string json = File.ReadAllText(alt);
                    achievements = ParsePlayniteAchievementsData(json);
                    if (achievements.Count > 0)
                        return achievements;
                }
                catch (Exception ex)
                {
                    logger.Error($"Failed to read PlayniteAchievements data from {alt}: {ex.Message}");
                }
            }

            if (achievements.Count == 0)
            {
                log($"No PlayniteAchievements data found for game {game.Id} " +
                    $"(checked SQLite at '{dbPath}' and legacy JSON). " +
                    "Refresh the game inside PlayniteAchievements, or use SuccessStory as fallback.", "DEBUG");
            }

            return achievements;
        }

        private static string CopyAchievementDbSnapshot(string dbPath)
        {
            var fi = new FileInfo(dbPath);
            if (!fi.Exists || fi.Length > 256L * 1024 * 1024)
                return null; // too large to copy: read in place instead
            string dir = Path.Combine(Path.GetTempPath(), "PlayniteGameOverlay_pa_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                foreach (var suffix in new[] { "", "-wal" })
                {
                    string src = dbPath + suffix;
                    if (!File.Exists(src)) continue;
                    string dst = Path.Combine(dir, "achievement_cache.db" + suffix);
                    using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var output = new FileStream(dst, FileMode.CreateNew, FileAccess.Write))
                        input.CopyTo(output);
                }
                return dir;
            }
            catch
            {
                try { Directory.Delete(dir, true); } catch { }
                return null;
            }
        }

        private List<AchievementData> GetSuccessStoryAchievementsData(Game game)
        {
            var achievements = new List<AchievementData>();

            string successStoryDir = Path.Combine(
                playniteAPI.Paths.ExtensionsDataPath, successStoryId.ToString(), "SuccessStory");

            if (!Directory.Exists(successStoryDir))
                return achievements;

            string achievementsFile = Path.Combine(successStoryDir, $"{game.Id}.json");
            if (!File.Exists(achievementsFile))
                return achievements;

            string achievementsJson = File.ReadAllText(achievementsFile);
            return ParseSuccessStoryData(achievementsJson);
        }

        // ---- Playnite Links: add PCGamingWiki and Nexus Mods to each game ----
        private static bool HasLinkTo(Game game, string host)
        {
            return FindExistingLink(game, host) != null;
        }

        private static bool LinkMatchesHost(Link link, string host)
        {
            if (link == null || string.IsNullOrWhiteSpace(link.Url)) return false;
            Uri uri;
            if (Uri.TryCreate(link.Url, UriKind.Absolute, out uri))
                return uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
                       uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);
            return link.Url.IndexOf(host, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsPcExclusiveLink(Link link)
        {
            if (link == null) return false;
            bool isPcGamingWiki = LinkMatchesHost(link, "pcgamingwiki.com") &&
                string.Equals((link.Name ?? "").Trim(), "PCGamingWiki", StringComparison.OrdinalIgnoreCase);
            bool isNexus = (LinkMatchesHost(link, "nexusmods.com") &&
                string.Equals((link.Name ?? "").Trim(), "Nexus Mods", StringComparison.OrdinalIgnoreCase)) ||
                IsSearchEngineNexusLink(link);
            bool isVortexShortcut =
                string.Equals(CanonicalManagedLinkName(link.Name), "Vortex", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(link.Url) &&
                link.Url.IndexOf("VortexLinks", StringComparison.OrdinalIgnoreCase) >= 0 &&
                link.Url.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
            return isPcGamingWiki || isNexus || IsScsKillerLink(link) || isVortexShortcut;
        }

        private static bool HasPcExclusiveLinks(Game game)
        {
            return game?.Links != null && game.Links.Any(IsPcExclusiveLink);
        }

        /// <summary>Remove PC-only links left behind after a game's platform changes.</summary>
        private static bool RemovePcExclusiveLinks(Game game)
        {
            if (game?.Links == null) return false;
            var remove = game.Links.Where(IsPcExclusiveLink).ToList();
            foreach (var link in remove)
                game.Links.Remove(link);
            return remove.Count > 0;
        }

        /// <summary>Windows PC game: PC (Windows) platform, or a PC store game with no platform set.</summary>
        private static bool IsPcGame(Game g)
        {
            try
            {
                var plats = g?.Platforms;
                if (plats != null && plats.Count > 0)
                    return plats.Any(p => p != null &&
                        (string.Equals(p.SpecificationId, "pc_windows", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(p.Name, "PC (Windows)", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(p.Name, "PC", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(p.Name, "Windows", StringComparison.OrdinalIgnoreCase)));
                // No platform set (typical for manually added games): treat as PC so it still gets its links.
                return true;
            }
            catch { return false; }
        }

        /// <summary>Gives a game with no platform the PC (Windows) platform. Returns true if it changed.</summary>
        private bool SetPcPlatform(Game g)
        {
            try
            {
                var platform = playniteAPI.Database.Platforms.FirstOrDefault(p =>
                    string.Equals(p.SpecificationId, "pc_windows", StringComparison.OrdinalIgnoreCase)) ??
                    playniteAPI.Database.Platforms.FirstOrDefault(p =>
                    string.Equals(p.Name, "PC (Windows)", StringComparison.OrdinalIgnoreCase));
                if (platform == null)
                {
                    platform = new Platform("PC (Windows)") { SpecificationId = "pc_windows" };
                    playniteAPI.Database.Platforms.Add(platform);
                }
                g.PlatformIds = new List<Guid> { platform.Id };
                return true;
            }
            catch { return false; }
        }

        // SCSKiller works at engine level (Unreal Engine 4/5, Unity, RE Engine, FromSoftware, other DirectX 12 games),
        // so every PC game can get its link. Games on this list, and Unreal Engine games found on disk, are labelled
        // "(recommended)". Non-exhaustive; whole normalized titles are matched, not substrings.
        // Measured by SCSKiller: https://github.com/BlueHeisenberg/SCSKiller#results
        // Additional historical reports: https://www.digitalfoundry.net/articles/digitalfoundry-2022-df-direct-weekly-talks-state-of-play-and-horizon-forbidden-west-post-patch-image-quality
        // https://www.dsogaming.com/articles/crisis-core-final-fantasy-vii-reunion-does-not-require-a-high-end-pc-but-has-shader-compilation-stutters
        // War Thunder, Returnal, Dead Island 2, Monster Hunter Wilds and the other games added in the second block are named in
        // SCSKiller's changelog / releases: https://github.com/BlueHeisenberg/SCSKiller/blob/main/CHANGELOG.md
        private static readonly HashSet<string> ShaderStutterGameNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "finalfantasyviirebirth", "silenthilltownfall", "hogwartslegacy",
            "tinytinaswonderlands", "starwarsjedisurvivor",
            "finalfantasyviiremake", "finalfantasyviiremakeintergrade",
            "eldenring", "shadowwarrior3", "kenabridgeofspirits", "ghostrunner",
            "swordandfairy7", "crisiscorefinalfantasyviireunion", "warthunder",
            // Further documented reports and their links are listed in README.md.
            "dragonballsparkingzero", "sandland", "gungravegore", "clairobscurexpedition33",
            "ghostrunner2", "easportswrc", "untildawn", "taintedgrailthefallofavalon",
            "sackboyabigadventure", "stray", "thewitcher3wildhunt", "thelastofusparti",
            "thelastofuspart1",
            // Named in SCSKiller's changelog and release notes (no anti-cheat games).
            "aliensdarkdescent", "returnal", "deadisland2", "splitfiction",
            "senuassagahellbladeii", "avowed", "evilwest", "stellarblade",
            "daysgone", "styxshardsofdarkness", "abzû", "littlenightmares",
            "monsterhunterwilds", "phantombladezero", "borderlands3",
            "armoredcorevifiresofrubicon"
        };
        private const string ScsKillerUrl = "https://github.com/BlueHeisenberg/SCSKiller";
        // Leading emoji = the link's icon (same mechanism as the Vortex / Save / Config links).
        private const string ScsKillerLinkName = "\u26A1 SCSKiller";

        private static bool HasKnownShaderStutter(Game g)
        {
            if (g == null || string.IsNullOrWhiteSpace(g.Name)) return false;
            // Ignore punctuation, whitespace and trademark symbols, but never remove words.
            var normalized = new string(g.Name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            return ShaderStutterGameNames.Contains(normalized);
        }

        // Unreal Engine 4 and 5 share the same folder layout, so they can't be told apart here.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> UnrealGameCache =
            new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private static bool LooksLikeUnrealGame(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return false;
            try
            {
                if (!Directory.Exists(root)) return false;
                if (Directory.Exists(System.IO.Path.Combine(root, "Engine", "Binaries")) ||
                    Directory.Exists(System.IO.Path.Combine(root, "Engine", "Config")) ||
                    Directory.Exists(System.IO.Path.Combine(root, "Engine", "Content")))
                    return true;
                var platforms = new[] { "Win64", "WinGDK" };
                foreach (var plat in platforms)
                {
                    var bin = System.IO.Path.Combine(root, "Binaries", plat);
                    if (Directory.Exists(bin) && Directory.EnumerateFiles(bin, "*-Shipping.exe").Any()) return true;
                }
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    foreach (var plat in platforms)
                    {
                        var bin = System.IO.Path.Combine(dir, "Binaries", plat);
                        if (Directory.Exists(bin) && Directory.EnumerateFiles(bin, "*-Shipping.exe").Any()) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>Known shader-stutter title, or an installed Unreal Engine game.</summary>
        private bool IsScsKillerRecommended(Game g)
        {
            if (HasKnownShaderStutter(g)) return true;
            var root = ExpandedInstallDir(g);
            if (string.IsNullOrWhiteSpace(root)) return false;
            bool unreal;
            if (!UnrealGameCache.TryGetValue(root, out unreal))
            {
                unreal = LooksLikeUnrealGame(root);
                UnrealGameCache[root] = unreal;
            }
            return unreal;
        }

        /// <summary>Our own SCSKiller link: the project page, or the installed SCSKiller.exe. Anything else is the user's.</summary>
        private static bool IsManagedScsKillerUrl(Link l)
        {
            if (l == null || string.IsNullOrWhiteSpace(l.Url)) return false;
            if (string.Equals(l.Url.TrimEnd('/'), ScsKillerUrl, StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                Uri uri;
                if (!Uri.TryCreate(l.Url, UriKind.Absolute, out uri) || !uri.IsFile) return false;
                var file = System.IO.Path.GetFileName(uri.LocalPath);
                return string.Equals(file, "SCSKiller.exe", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(file, "SCSKiller.lnk", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private string ScsKillerShortcutPath()
        {
            try
            {
                var dataPath = GetPluginUserDataPath();
                if (string.IsNullOrWhiteSpace(dataPath)) return null;
                return System.IO.Path.Combine(dataPath, "ScsKillerLinks", "SCSKiller.lnk");
            }
            catch { return null; }
        }

        /// <summary>
        /// Opens the installed SCSKiller app; falls back to the project page when it isn't installed.
        /// The link points at a small Windows shortcut (like the Vortex links) because Playnite's themes
        /// show the shortcut's icon, but none for a bare .exe. With create = false nothing is written.
        /// </summary>
        private string ScsKillerTargetUrl(bool create)
        {
            var exe = ScsKillerInfo.GetExePath();
            if (string.IsNullOrEmpty(exe)) return ScsKillerUrl;
            var lnk = ScsKillerShortcutPath();
            if (string.IsNullOrEmpty(lnk)) return FolderLinkUrl(exe);
            if (create)
            {
                // Rewritten every time: cheap, and it fixes a moved or updated SCSKiller.exe.
                if (!CreateVortexShortcut(lnk, exe, null)) return FolderLinkUrl(exe);
            }
            return FolderLinkUrl(lnk);
        }

        private bool ScsKillerLinkOutOfDate(Game g)
        {
            string wantedName = ScsKillerLinkName;
            string wantedUrl = ScsKillerTargetUrl(false);
            return g.Links.Any(l => IsManagedScsKillerUrl(l) &&
                (!string.Equals(l.Name, wantedName, StringComparison.Ordinal) ||
                 !string.Equals(l.Url, wantedUrl, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>True when the SCSKiller link is missing, or our own link has the wrong label or target.</summary>
        private bool NeedsScsKillerWork(Game g)
        {
            // Only suggested games (known shader-stutter titles, Unreal Engine games) show the link;
            // our own link on any other game has to go.
            if (!IsScsKillerRecommended(g)) return g.Links != null && g.Links.Any(IsManagedScsKillerLink);
            if (!HasScsKillerLink(g)) return true;
            // The shortcut the link points at must exist (it is created when the link is written).
            var lnk = ScsKillerShortcutPath();
            if (!string.IsNullOrEmpty(ScsKillerInfo.GetExePath()) && !string.IsNullOrEmpty(lnk) && !File.Exists(lnk)) return true;
            return ScsKillerLinkOutOfDate(g);
        }

        /// <summary>Adds the SCSKiller link to a PC game and keeps its label and target right. Returns true if changed.</summary>
        private bool SyncScsKillerLink(Game g)
        {
            if (g.Links == null) g.Links = new System.Collections.ObjectModel.ObservableCollection<Link>();
            if (!IsScsKillerRecommended(g))
            {
                // Not a suggested game: remove the link this extension added (a user's own SCSKiller link stays).
                bool removed = false;
                foreach (var old in g.Links.Where(IsManagedScsKillerLink).ToList())
                {
                    g.Links.Remove(old);
                    removed = true;
                }
                return removed;
            }
            string wantedName = ScsKillerLinkName;
            string wantedUrl = ScsKillerTargetUrl(true);
            if (!HasScsKillerLink(g))
            {
                g.Links.Add(new Link(wantedName, wantedUrl));
                return true;
            }
            bool changed = false;
            for (int i = 0; i < g.Links.Count; i++)
            {
                var l = g.Links[i];
                if (IsManagedScsKillerUrl(l) &&
                    (!string.Equals(l.Name, wantedName, StringComparison.Ordinal) ||
                     !string.Equals(l.Url, wantedUrl, StringComparison.OrdinalIgnoreCase)))
                {
                    g.Links[i] = new Link(wantedName, wantedUrl);
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>A link this extension added: SCSKiller name and the project page / SCSKiller app target.</summary>
        private static bool IsManagedScsKillerLink(Link l)
        {
            return IsScsKillerLink(l) && IsManagedScsKillerUrl(l);
        }

        private static bool HasScsKillerLink(Game g)
        {
            return g?.Links != null && g.Links.Any(IsScsKillerLink);
        }

        private static bool IsScsKillerLink(Link link)
        {
            if (link == null) return false;
            // Skip a leading emoji / symbol (the icon): "⚡ SCSKiller" and "SCSKiller" are the same link.
            var scsName = new string((link.Name ?? "").Trim().SkipWhile(c => !char.IsLetterOrDigit(c)).ToArray());
            if (scsName.StartsWith("SCSKiller", StringComparison.OrdinalIgnoreCase)) return true;
            Uri uri;
            if (!Uri.TryCreate(link.Url, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return false;
            if (uri.Host.Equals("scskiller.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".scskiller.com", StringComparison.OrdinalIgnoreCase)) return true;
            return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
                (uri.AbsolutePath.TrimEnd('/').Equals("/BlueHeisenberg/SCSKiller", StringComparison.OrdinalIgnoreCase) ||
                 uri.AbsolutePath.StartsWith("/BlueHeisenberg/SCSKiller/", StringComparison.OrdinalIgnoreCase));
        }

        // ---- Link order: PCGamingWiki > GameBanana > Nexus Mods > Vortex > SCSKiller > Config location > Save location.
        // Any other link keeps its place (and relative order) in front of these.
        private static int ManagedLinkRank(Link l)
        {
            if (l == null) return -1;
            switch (CanonicalManagedLinkName(l.Name))
            {
                case "Vortex": return 3;
                case "Config location": return 5;
                case "Save location": return 6;
            }
            if (IsScsKillerLink(l)) return 4;
            if (LinkMatchesHost(l, "pcgamingwiki.com")) return 0;
            if (LinkMatchesHost(l, "gamebanana.com")) return 1;
            if (LinkMatchesHost(l, "nexusmods.com")) return 2;
            return -1;
        }

        private static bool LinksOutOfOrder(Game g, out List<Link> sorted)
        {
            sorted = null;
            if (g?.Links == null || g.Links.Count < 2) return false;
            var current = g.Links.ToList();
            var ordered = current.OrderBy(ManagedLinkRank).ToList();   // OrderBy is stable
            for (int i = 0; i < current.Count; i++)
            {
                if (!ReferenceEquals(current[i], ordered[i]))
                {
                    sorted = ordered;
                    return true;
                }
            }
            return false;
        }

        private static bool NeedsLinkSort(Game g)
        {
            List<Link> sorted;
            return LinksOutOfOrder(g, out sorted);
        }

        /// <summary>Puts the managed links into the fixed order. Returns true if the game record changed.</summary>
        private static bool SortManagedLinks(Game g)
        {
            List<Link> sorted;
            if (!LinksOutOfOrder(g, out sorted)) return false;
            g.Links = new System.Collections.ObjectModel.ObservableCollection<Link>(sorted);
            return true;
        }

        /// <summary>Sorts the links of every existing game. Independent of the link options; call on the UI thread.</summary>
        private void SortAllGameLinks()
        {
            try
            {
                var games = playniteAPI?.Database?.Games;
                if (games == null) return;
                var todo = games.Where(NeedsLinkSort).ToList();
                if (todo.Count == 0) return;
                int sortedCount = 0;
                foreach (var g in todo)
                {
                    if (SortManagedLinks(g))
                    {
                        games.Update(g);
                        sortedCount++;
                    }
                }
                Breadcrumb.Write("Sorted links for " + sortedCount + " game(s)");
            }
            catch (Exception ex) { logger.Warn("Link sort: " + ex.Message); }
        }

        /// <summary>Add missing enabled links. Returns true if the game record changed.</summary>
        private bool AddMissingLinks(Game g)
        {
            if (AreGameLinksBlocked()) { MarkLinkWorkDeferred(); return false; }
            if (g == null) return false;
            if (!IsPcGame(g))
            {
                bool removedLinks = RemovePcExclusiveLinks(g);
                // Emulated games: link the emulator's save and config folders (see EmulatorLocations.cs).
                if (Settings?.AutoAddSaveLinks == true && !string.IsNullOrWhiteSpace(g.Name))
                {
                    if (g.Links == null)
                        g.Links = new System.Collections.ObjectModel.ObservableCollection<Link>();
                    removedLinks |= NormalizeManagedLinkIcons(g);
                    removedLinks |= AddSaveLinks(g);
                }
                removedLinks |= SortManagedLinks(g);
                return removedLinks;
            }
            if (string.IsNullOrWhiteSpace(g.Name)) return false;
            bool changed = false;
            if (g.Links == null)
                g.Links = new System.Collections.ObjectModel.ObservableCollection<Link>();
            changed |= NormalizeManagedLinkIcons(g);

            if (Settings?.AutoAddGameLinks == true)
            {
                // Every game gets a PCGamingWiki link, however it was added (manually or by a store extension).
                // The verified page is used when the cached page check has one; otherwise a Steam / GOG id
                // redirect or a wiki search stands in until a page check finds the real page. A link is never
                // removed just because the wiki has no page or could not be reached.
                string pcgwUrl;
                var pcgwState = PcGamingWikiPages.Get(PcgwKey(g), out pcgwUrl);
                if (pcgwState == PcGamingWikiPages.PageState.Page)
                {
                    foreach (var old in g.Links.Where(IsAutoPcgwLink).ToList())
                    {
                        g.Links.Remove(old);
                        changed = true;
                    }
                    if (!HasLinkTo(g, "pcgamingwiki.com"))
                    {
                        g.Links.Add(new Link("PCGamingWiki", pcgwUrl));
                        changed = true;
                    }
                }
                else if (!HasLinkTo(g, "pcgamingwiki.com"))
                {
                    g.Links.Add(new Link("PCGamingWiki", PcgwFallbackUrl(g)));
                    changed = true;
                }
                // Earlier versions added a Google search as the Nexus link: replace it with the real page.
                foreach (var old in g.Links.Where(l => IsSearchEngineNexusLink(l)).ToList())
                {
                    g.Links.Remove(old);
                    changed = true;
                }
                if (!HasLinkTo(g, "nexusmods.com"))
                {
                    var url = BuildNexusModsUrl(g);   // null when the game has no Nexus Mods page
                    if (!string.IsNullOrEmpty(url)) { g.Links.Add(new Link("Nexus Mods", url)); changed = true; }
                }
            }

            // Only suggested games get the link: known shader-stutter titles and Unreal Engine games.
            // Other games lose the link this extension added.
            if (Settings?.AutoAddScsKillerLink == true && SyncScsKillerLink(g))
                changed = true;

            if (Settings?.AutoAddSaveLinks == true && AddSaveLinks(g))
                changed = true;

            // Open Vortex with this game selected (Vortex --game <id>), using its Nexus/Vortex game identifier.
            if (Settings?.AutoAddVortexLink == true)
            {
                // "Vortex --game <nexus id>" opens Vortex with THIS game selected. Every game Nexus lists gets
                // that id (Steam app id first, so Skyrim LE / SE / VR etc. never get mixed up).
                var vortex = VortexInfo.GetExePath();
                var gameId = VortexGameIdFor(g);
                var shortcut = GetVortexShortcutPath(g);
                if (!string.IsNullOrEmpty(gameId))
                {
                    if (string.IsNullOrEmpty(vortex))
                        Breadcrumb.Write("Vortex link skipped for '" + g.Name + "': Vortex.exe not found");
                    else if (string.IsNullOrEmpty(shortcut))
                        Breadcrumb.Write("Vortex link skipped for '" + g.Name + "': no shortcut path");
                    else if (CreateVortexShortcut(shortcut, vortex, gameId))   // always rewritten: fixes stale ids
                    {
                        int linkIndex = FindVortexLinkIndex(g);
                        if (linkIndex < 0)
                        {
                            g.Links.Add(new Link(VortexLinkDisplayName, FolderLinkUrl(shortcut)));
                            changed = true;
                        }
                        else if (!string.Equals(g.Links[linkIndex].Url, FolderLinkUrl(shortcut), StringComparison.OrdinalIgnoreCase) ||
                                 !string.Equals(g.Links[linkIndex].Name, VortexLinkDisplayName, StringComparison.Ordinal))
                        {
                            // Upgrade older generic links and apply the icon label.
                            g.Links[linkIndex] = new Link(VortexLinkDisplayName, FolderLinkUrl(shortcut));
                            changed = true;
                        }
                    }
                }
                else if (!NexusGames.IsLoaded)
                    Breadcrumb.Write("Vortex link skipped for '" + g.Name + "': Nexus game list not loaded");
                // A game Nexus doesn't list: any link already there is left untouched.
            }
            changed |= SortManagedLinks(g);
            return changed;
        }

        /// <summary>Removes the Vortex link (and its generated shortcut) from a game Vortex doesn't support.</summary>
        private bool RemoveVortexLink(Game g)
        {
            int idx = FindVortexLinkIndex(g);
            if (idx < 0) return false;
            try
            {
                var url = g.Links[idx].Url;
                if (!string.IsNullOrEmpty(url) &&
                    url.IndexOf("VortexLinks", StringComparison.OrdinalIgnoreCase) >= 0 && File.Exists(url))
                    File.Delete(url);
            }
            catch { }
            g.Links.RemoveAt(idx);
            return true;
        }

        private const string VortexLinkDisplayName = "🌪️ Vortex";
        private const string SaveLocationLinkDisplayName = "💾 Save location";
        private const string ConfigLocationLinkDisplayName = "\u2699\uFE0F Config location";

        private static string CanonicalManagedLinkName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            var value = name.Trim();
            if (string.Equals(value, "Vortex", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "🌀 Vortex", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, VortexLinkDisplayName, StringComparison.OrdinalIgnoreCase)) return "Vortex";
            if (string.Equals(value, "Save location", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, SaveLocationLinkDisplayName, StringComparison.OrdinalIgnoreCase)) return "Save location";
            if (string.Equals(value, "Config location", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, ConfigLocationLinkDisplayName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "⚙️ Config location", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "📁 Config location", StringComparison.OrdinalIgnoreCase)) return "Config location";
            return value;
        }

        private static string ManagedLinkDisplayName(string canonicalName)
        {
            switch (CanonicalManagedLinkName(canonicalName))
            {
                case "Vortex": return VortexLinkDisplayName;
                case "Save location": return SaveLocationLinkDisplayName;
                case "Config location": return ConfigLocationLinkDisplayName;
                default: return canonicalName;
            }
        }

        private static bool NeedsManagedLinkIconUpgrade(Game g, string canonicalName)
        {
            if (g?.Links == null) return false;
            string displayName = ManagedLinkDisplayName(canonicalName);
            return g.Links.Any(l => l != null &&
                string.Equals(CanonicalManagedLinkName(l.Name), canonicalName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(l.Name, displayName, StringComparison.Ordinal));
        }

        private static bool NormalizeManagedLinkIcons(Game g)
        {
            if (g?.Links == null) return false;
            bool changed = false;
            for (int i = 0; i < g.Links.Count; i++)
            {
                var link = g.Links[i];
                if (link == null) continue;
                string canonical = CanonicalManagedLinkName(link.Name);
                string displayName = ManagedLinkDisplayName(canonical);
                if (!string.Equals(displayName, link.Name, StringComparison.Ordinal))
                {
                    g.Links[i] = new Link(displayName, link.Url);
                    changed = true;
                }
            }
            return changed;
        }

        private static bool HasLinkNamed(Game g, string name)
        {
            return g?.Links != null && g.Links.Any(l => l != null &&
                string.Equals(CanonicalManagedLinkName(l.Name), CanonicalManagedLinkName(name), StringComparison.OrdinalIgnoreCase));
        }

        private string ExpandedInstallDir(Game g)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(g.InstallDirectory)) return null;
                return playniteAPI.ExpandGameVariables(g, g.InstallDirectory);
            }
            catch { return null; }
        }

        private static string FolderLinkUrl(string folder)
        {
            try { return new Uri(folder).AbsoluteUri; }   // file:///C:/... so Playnite treats it as a link
            catch { return folder; }
        }

        /// <summary>Save / config folder links, from folders that exist right now.</summary>
        private bool AddSaveLinks(Game g)
        {
            bool changed = false;
            string save, cfg;
            ResolveSaveAndConfig(g, out save, out cfg);
            if (HasLinkNamed(g, "Save location")) save = null;
            if (HasLinkNamed(g, "Config location")) cfg = null;
            if (save != null)
            {
                g.Links.Add(new Link(SaveLocationLinkDisplayName, FolderLinkUrl(save)));
                changed = true;
            }
            if (cfg != null && !string.Equals(cfg, save, StringComparison.OrdinalIgnoreCase))
            {
                g.Links.Add(new Link(ConfigLocationLinkDisplayName, FolderLinkUrl(cfg)));
                changed = true;
            }
            return changed;
        }

        /// <summary>
        /// Where the game keeps its saves and config: the community (Ludusavi) manifest first, then a look at the
        /// folders on disk for games the manifest doesn't know (unofficial PC ports, recompilations, mods).
        /// </summary>
        private void ResolveSaveAndConfig(Game g, out string save, out string cfg)
        {
            save = null; cfg = null;
            if (!IsPcGame(g) || EmulatorLocations.HasEmulatorAction(g))
            {
                EmulatorLocations.Resolve(g, playniteAPI, out save, out cfg);
                return;
            }
            var install = ExpandedInstallDir(g);
            var t = SaveLocations.Get(g.Id);
            if (t != null)
            {
                save = SaveLocations.ResolveFolder(t.Save, install, g.GameId);
                cfg = SaveLocations.ResolveFolder(t.Config, install, g.GameId);
            }
            if (save == null || cfg == null)
            {
                string gs, gc;
                GuessSaveAndConfig(g, install, out gs, out gc);
                // Recompilations / unofficial ports (memcards, user, issn.ini, sotc.ini ...): see RecompLocations.cs.
                if (gs == null || gc == null)
                {
                    string rs, rc;
                    RecompLocations.Guess(RecompLocations.Roots(g, install, v => playniteAPI.ExpandGameVariables(g, v)), out rs, out rc);
                    if (gs == null) gs = rs;
                    if (gc == null) gc = rc;
                }
                if (save == null) save = gs;
                if (cfg == null) cfg = gc;
            }
        }

        private static readonly string[] SaveDirNames =
            { "userdata", "saves", "save", "savedata", "savegames", "savegame", "save_data", "saved", "profiles", "profile" };
        private static readonly string[] ConfigDirNames =
            { "config", "configs", "configuration", "settings", "cfg" };
        private static readonly string[] ConfigFileStems = { "config", "settings", "options", "preferences" };
        private static readonly string[] ConfigFileExts = { ".toml", ".ini", ".json", ".cfg", ".xml", ".yaml", ".yml", ".conf" };
        // Folder names too generic to mean "this game's data" when they turn up in AppData.
        private static readonly HashSet<string> GenericFolderKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "game", "games", "common", "data", "bin", "release", "debug", "build", "app", "apps", "program",
            "programs", "steam", "microsoft", "windows", "temp", "cache", "packages", "google", "mozilla",
            "nvidia", "intel", "amd", "unity", "unreal", "playnite", "emulators", "mods", "roms"
        };

        private static string NormFolderKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.ToLowerInvariant().Replace("&", "and");
            return System.Text.RegularExpressions.Regex.Replace(s, "[^a-z0-9]", "");
        }

        private static bool HasConfigFile(string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return false;
                foreach (var f in Directory.EnumerateFiles(folder))
                {
                    var ext = Path.GetExtension(f);
                    var stem = Path.GetFileNameWithoutExtension(f);
                    if (ConfigFileExts.Contains(ext, StringComparer.OrdinalIgnoreCase) &&
                        ConfigFileStems.Contains(stem, StringComparer.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return false;
        }

        private static string FirstExistingChild(string parent, string[] names)
        {
            foreach (var n in names)
            {
                try
                {
                    var d = Path.Combine(parent, n);
                    if (Directory.Exists(d)) return d;
                }
                catch { }
            }
            return null;
        }

        // Top-level folders of the usual per-user data locations, listed once and reused for a while.
        private static readonly object AppDataLock = new object();
        private static List<KeyValuePair<string, string>> _appDataFolders;
        private static DateTime _appDataListedUtc;

        private static List<KeyValuePair<string, string>> AppDataFolders()
        {
            lock (AppDataLock)
            {
                if (_appDataFolders != null && (DateTime.UtcNow - _appDataListedUtc).TotalMinutes < 10)
                    return _appDataFolders;
                var list = new List<KeyValuePair<string, string>>();
                try
                {
                    var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    var bases = new List<string>
                    {
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        Path.Combine(docs, "My Games"),
                        docs,
                        Path.Combine(profile, "Saved Games"),
                        Path.Combine(profile, "AppData", "LocalLow")
                    };
                    foreach (var b in bases)
                    {
                        try
                        {
                            if (string.IsNullOrEmpty(b) || !Directory.Exists(b)) continue;
                            foreach (var d in Directory.EnumerateDirectories(b))
                                list.Add(new KeyValuePair<string, string>(NormFolderKey(Path.GetFileName(d)), d));
                        }
                        catch { }
                    }
                }
                catch { }
                _appDataFolders = list;
                _appDataListedUtc = DateTime.UtcNow;
                return list;
            }
        }

        /// <summary>The per-user data folder named after the game (or its install folder), if there is exactly such a folder.</summary>
        private static string FindAppDataFolder(Game g, string install)
        {
            var keys = new List<string>();
            Action<string, int> add = (name, minLen) =>
            {
                var k = NormFolderKey(name);
                if (k.Length >= minLen && !GenericFolderKeys.Contains(k) && !keys.Contains(k)) keys.Add(k);
            };
            add(g?.Name, 3);
            if (!string.IsNullOrEmpty(install))
            {
                string trimmed = install.TrimEnd('\\', '/');
                add(Path.GetFileName(trimmed), 6);
                // Ports are often unpacked into a versioned folder ("ng2recomp-v1.0.21\\NinjaGaiden2"): use the parent without the version.
                try
                {
                    var parent = Path.GetFileName(Path.GetDirectoryName(trimmed) ?? "");
                    parent = System.Text.RegularExpressions.Regex.Replace(parent ?? "", @"[-_ .]*v?\d+(\.\d+)+.*$", "");
                    add(parent, 6);
                }
                catch { }
            }
            var folders = AppDataFolders();
            foreach (var key in keys)
            {
                var hit = folders.FirstOrDefault(f => f.Key == key);
                if (!string.IsNullOrEmpty(hit.Value)) return hit.Value;
            }
            return null;
        }

        /// <summary>
        /// For games with no manifest entry (unofficial PC ports, recompilations): look in the install folder for
        /// the usual userdata / saves / config folders, honour a portable.txt marker, then for an AppData folder
        /// named after the game. Only folders that exist are returned.
        /// </summary>
        private static void GuessSaveAndConfig(Game g, string install, out string save, out string cfg)
        {
            save = null; cfg = null;
            try
            {
                if (!string.IsNullOrEmpty(install) && Directory.Exists(install))
                {
                    save = FirstExistingChild(install, SaveDirNames);
                    cfg = FirstExistingChild(install, ConfigDirNames);
                    if (cfg == null)
                    {
                        if (save != null && HasConfigFile(save)) cfg = save;
                        else if (HasConfigFile(install)) cfg = install;
                    }
                    // "portable.txt" means everything lives next to the game.
                    if (save == null && cfg == null && File.Exists(Path.Combine(install, "portable.txt")))
                        save = install;
                }
                if (save == null)
                {
                    var app = FindAppDataFolder(g, install);
                    if (app != null)
                    {
                        save = app;
                        if (cfg == null)
                            cfg = FirstExistingChild(app, ConfigDirNames) ?? (HasConfigFile(app) ? app : null);
                    }
                }
            }
            catch { }
        }

        private static int FindVortexLinkIndex(Game g)
        {
            if (g?.Links == null) return -1;
            for (int i = 0; i < g.Links.Count; i++)
            {
                if (g.Links[i] != null &&
                    string.Equals(CanonicalManagedLinkName(g.Links[i].Name), "Vortex", StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private string GetVortexShortcutPath(Game g)
        {
            try
            {
                if (g == null) return null;
                var dataPath = GetPluginUserDataPath();
                if (string.IsNullOrWhiteSpace(dataPath)) return null;
                return Path.Combine(dataPath, "VortexLinks", g.Id.ToString("N") + ".lnk");
            }
            catch { return null; }
        }

        /// <summary>The Vortex game id for this game: Steam app id first, then the Nexus/Vortex name list.</summary>
        private static string VortexGameIdFor(Game g)
        {
            if (g == null) return null;
            string steamId = null;
            try
            {
                if (SteamInstantPlay.IsSteamGame(g) && !string.IsNullOrEmpty(g.GameId) && g.GameId.All(char.IsDigit))
                    steamId = g.GameId;
            }
            catch { }
            return NexusGames.GetVortexGameId(g.Name, steamId);
        }

        /// <summary>The Arguments of a shortcut, "" when it has none, null when it cannot be read.</summary>
        private static string ReadShortcutArguments(string shortcutPath)
        {
            object shell = null, shortcut = null;
            try
            {
                if (string.IsNullOrWhiteSpace(shortcutPath) || !File.Exists(shortcutPath)) return null;
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                if (shortcut == null) return null;
                var v = shortcut.GetType().InvokeMember("Arguments",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.GetProperty, null, shortcut, null);
                return ((v as string) ?? "").Trim();
            }
            catch { return null; }
            finally
            {
                try { if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut); } catch { }
                try { if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); } catch { }
            }
        }

        /// <summary>Creates a per-game Windows shortcut so Playnite can pass Vortex's --game argument.</summary>
        private static bool CreateVortexShortcut(string shortcutPath, string vortexExe, string gameId)
        {
            object shell = null;
            object shortcut = null;
            try
            {
                if (string.IsNullOrWhiteSpace(shortcutPath) || string.IsNullOrWhiteSpace(vortexExe)) return false;
                // No game id (Vortex doesn't officially support the game): the shortcut just opens Vortex.
                if (!string.IsNullOrWhiteSpace(gameId) &&
                    !System.Text.RegularExpressions.Regex.IsMatch(gameId, @"^[A-Za-z0-9._-]+$")) gameId = null;
                Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath));
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return false;
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                if (shortcut == null) return false;
                var shortcutType = shortcut.GetType();
                var setProperty = System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.SetProperty;
                shortcutType.InvokeMember("TargetPath", setProperty, null, shortcut, new object[] { vortexExe });
                shortcutType.InvokeMember("Arguments", setProperty, null, shortcut, new object[] { string.IsNullOrWhiteSpace(gameId) ? "" : "--game " + gameId });
                shortcutType.InvokeMember("WorkingDirectory", setProperty, null, shortcut,
                    new object[] { Path.GetDirectoryName(vortexExe) ?? string.Empty });
                shortcutType.InvokeMember("IconLocation", setProperty, null, shortcut, new object[] { vortexExe + ",0" });
                shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
                return File.Exists(shortcutPath);
            }
            catch { return false; }
            finally
            {
                try { if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut); } catch { }
                try { if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); } catch { }
            }
        }

        private static bool IsSearchEngineNexusLink(Link l)
        {
            return l != null && string.Equals(l.Name, "Nexus Mods", StringComparison.OrdinalIgnoreCase) &&
                   !string.IsNullOrEmpty(l.Url) &&
                   l.Url.IndexOf("google.com/search", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsNexusModsSupported(Game g)
        {
            return g != null && !string.IsNullOrWhiteSpace(g.Name) && NexusGames.Find(g.Name) != null;
        }

        private bool NeedsEmulatorSaveLinks(Game g)
        {
            if (Settings?.AutoAddSaveLinks != true || string.IsNullOrWhiteSpace(g?.Name)) return false;
            if (NeedsManagedLinkIconUpgrade(g, "Save location") || NeedsManagedLinkIconUpgrade(g, "Config location")) return true;
            if (HasLinkNamed(g, "Save location") && HasLinkNamed(g, "Config location")) return false;
            string sv, cf;
            ResolveSaveAndConfig(g, out sv, out cf);
            return (!HasLinkNamed(g, "Save location") && sv != null) ||
                   (!HasLinkNamed(g, "Config location") && cf != null &&
                    !string.Equals(cf, sv, StringComparison.OrdinalIgnoreCase));
        }

        private bool NeedsLinkWork(Game g)
        {
            if (g == null) return false;
            if (NeedsLinkSort(g)) return true;
            if (!IsPcGame(g)) return HasPcExclusiveLinks(g) || NeedsEmulatorSaveLinks(g);
            if (Settings == null || string.IsNullOrWhiteSpace(g.Name)) return false;
            bool onNexus = IsNexusModsSupported(g);
            if (Settings.AutoAddScsKillerLink && NeedsScsKillerWork(g)) return true;
            if (Settings.AutoAddVortexLink && NeedsManagedLinkIconUpgrade(g, "Vortex")) return true;
            if (Settings.AutoAddSaveLinks &&
                (NeedsManagedLinkIconUpgrade(g, "Save location") || NeedsManagedLinkIconUpgrade(g, "Config location"))) return true;
            if (Settings.AutoAddGameLinks)
            {
                string pcgwPage;
                var pcgw = PcGamingWikiPages.Get(PcgwKey(g), out pcgwPage);
                if (!HasLinkTo(g, "pcgamingwiki.com")) return true;
                if (pcgw == PcGamingWikiPages.PageState.Page && HasAutoPcgwLink(g)) return true;
                if (g.Links != null && g.Links.Any(IsSearchEngineNexusLink)) return true;
                // Only games that Nexus Mods really lists can get a link.
                if (!HasLinkTo(g, "nexusmods.com") && onNexus) return true;
            }
            if (Settings.AutoAddVortexLink && VortexInfo.GetExePath() != null)
            {
                var vortexId = VortexGameIdFor(g);
                if (!string.IsNullOrEmpty(vortexId))
                {
                    var expectedShortcut = GetVortexShortcutPath(g);
                    int vortexLinkIndex = FindVortexLinkIndex(g);
                    if (!string.IsNullOrEmpty(expectedShortcut))
                    {
                        if (vortexLinkIndex < 0 ||
                            !string.Equals(g.Links[vortexLinkIndex].Url, FolderLinkUrl(expectedShortcut), StringComparison.OrdinalIgnoreCase) ||
                            !File.Exists(expectedShortcut))
                            return true;
                        // The shortcut must pass exactly this game's id (a stale or missing --game opens another game).
                        var args = ReadShortcutArguments(expectedShortcut);
                        if (args != null && !string.Equals(args, "--game " + vortexId, StringComparison.Ordinal))
                            return true;
                    }
                }
            }
            if (Settings.AutoAddSaveLinks && !(HasLinkNamed(g, "Save location") && HasLinkNamed(g, "Config location")))
            {
                string sv, cf;
                ResolveSaveAndConfig(g, out sv, out cf);
                if ((!HasLinkNamed(g, "Save location") && sv != null) ||
                    (!HasLinkNamed(g, "Config location") && cf != null &&
                     !string.Equals(cf, sv, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return false;
        }

        private string NexusCachePath()
        {
            try { return System.IO.Path.Combine(GetPluginUserDataPath(), "nexus_games.json"); }
            catch { return null; }
        }

        private volatile bool _linkPassRunning;
        // A separate guard per game prevents one game's stop from clearing another
        // game's in-progress launch. Expired guards don't block failed launches.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, long> _linkLaunchGuards =
            new System.Collections.Concurrent.ConcurrentDictionary<Guid, long>();
        private bool _linkWorkDeferred;
        private System.Windows.Threading.DispatcherTimer _deferredLinkTimer;

        // Call on the UI thread. Failure to inspect game state must not risk
        // editing links during gameplay. A launch grace period covers the gap
        // before Playnite updates its running flags.
        private bool AreGameLinksBlocked()
        {
            try
            {
                var now = DateTime.UtcNow.Ticks;
                if (_linkLaunchGuards.Values.Any(until => until > now)) return true;
                var games = playniteAPI?.Database?.Games;
                return games == null || games.Any(g => g != null && (g.IsRunning || g.IsLaunching));
            }
            catch { return true; }
        }

        private void MarkLinkWorkDeferred()
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || _gameBananaStopping) return;
            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(MarkLinkWorkDeferred));
                return;
            }
            _linkWorkDeferred = true;
            if (_deferredLinkTimer == null)
            {
                _deferredLinkTimer = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle, dispatcher);
                _deferredLinkTimer.Interval = TimeSpan.FromSeconds(2);
                _deferredLinkTimer.Tick += (_, __) =>
                {
                    if (_gameBananaStopping) { _deferredLinkTimer.Stop(); return; }
                    if (!_linkWorkDeferred) { _deferredLinkTimer.Stop(); return; }
                    if (AreGameLinksBlocked() || _linkPassRunning) return;
                    _linkWorkDeferred = false;
                    _deferredLinkTimer.Stop();
                    ApplyAutoGameLinks();
                };
            }
            _deferredLinkTimer.Start();
        }

        private int _linkRetryCount;
        private System.Windows.Threading.DispatcherTimer _linkRetryTimer;

        /// <summary>
        /// A network failure (GameBanana lookup, Nexus game list) used to leave the affected games without
        /// links until the next Playnite start. Re-run the whole link pass a few times with a growing delay.
        /// </summary>
        private void ScheduleLinkRetry(string reason)
        {
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null || _gameBananaStopping) return;
                if (!dispatcher.CheckAccess())
                {
                    dispatcher.BeginInvoke(new Action(() => ScheduleLinkRetry(reason)));
                    return;
                }
                if (_linkRetryTimer != null && _linkRetryTimer.IsEnabled) return;
                if (_linkRetryCount >= 6) return;
                _linkRetryCount++;
                _linkRetryTimer = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle, dispatcher)
                {
                    Interval = TimeSpan.FromMinutes(2 * _linkRetryCount)
                };
                _linkRetryTimer.Tick += (_, __) =>
                {
                    _linkRetryTimer.Stop();
                    if (_gameBananaStopping) return;
                    ApplyAutoGameLinks();
                };
                _linkRetryTimer.Start();
                Breadcrumb.Write("Links: retry #" + _linkRetryCount + " in " + (2 * _linkRetryCount) + " min (" + reason + ")");
            }
            catch { }
        }

        /// <summary>Runs at startup and when the option is turned on; afterwards only new games need work.</summary>
        internal void ApplyAutoGameLinks()
        {
            if (_gameBananaStopping) return;
            if (AreGameLinksBlocked() || _linkPassRunning)
            {
                MarkLinkWorkDeferred();
                return;
            }
            // Independent of the link options: existing games get their links in the fixed order.
            SortAllGameLinks();
            // Independent of the PC-only link pass: GameBanana supports console games too.
            ScheduleGameBananaLookups();
            try
            {
                bool hasStalePcLinks = false;
                try
                {
                    hasStalePcLinks = playniteAPI.Database.Games.Any(g =>
                        g != null && !IsPcGame(g) && HasPcExclusiveLinks(g));
                }
                catch { }
                if ((Settings?.AutoAddGameLinks != true && Settings?.AutoAddVortexLink != true &&
                     Settings?.AutoAddSaveLinks != true && Settings?.AutoAddScsKillerLink != true &&
                     !hasStalePcLinks) || _linkPassRunning) return;
                _linkPassRunning = true;
                var nexusCache = NexusCachePath();
                var dataDir = GetPluginUserDataPath();
                var dispatcher = System.Windows.Application.Current.Dispatcher;

                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        System.Action<GlobalProgressActionArgs> addLinks = progress =>
                        {
                            if (progress != null)
                            {
                                progress.IsIndeterminate = true;
                                progress.Text = "Preparing link sources…";
                            }

                            // Lists that are downloaded once and cached (Nexus games, save-game locations).
                            try { NexusGames.EnsureLoaded(nexusCache, m => logger.Warn(m)); } catch { }
                            if ((Settings?.AutoAddGameLinks == true || Settings?.AutoAddVortexLink == true) && !NexusGames.IsLoaded)
                                dispatcher.BeginInvoke(new Action(() => ScheduleLinkRetry("Nexus game list not loaded")));
                            if (Settings?.AutoAddSaveLinks == true)
                            {
                                try
                                {
                                    var keys = (List<SaveLocations.GameKey>)dispatcher.Invoke(new Func<List<SaveLocations.GameKey>>(() =>
                                        playniteAPI.Database.Games.Where(IsPcGame).Select(g => new SaveLocations.GameKey
                                        {
                                            Id = g.Id,
                                            Name = g.Name,
                                            SteamId = (SteamInstantPlay.IsSteamGame(g) && !string.IsNullOrEmpty(g.GameId) && g.GameId.All(char.IsDigit)) ? g.GameId : null
                                        }).ToList()));
                                    SaveLocations.Prepare(dataDir, keys, m => logger.Warn(m));
                                }
                                catch (Exception ex) { logger.Warn($"Save locations: {ex.Message}"); }
                            }

                            if (Settings?.AutoAddGameLinks == true)
                            {
                                try
                                {
                                    var pcgwKeys = (List<PcGamingWikiPages.GameKey>)dispatcher.Invoke(new Func<List<PcGamingWikiPages.GameKey>>(() =>
                                        playniteAPI.Database.Games.Where(IsPcGame).Select(PcgwKey).ToList()));
                                    PcGamingWikiPages.Configure(PcgwCachePath());
                                    if (progress != null) progress.Text = "Checking PCGamingWiki pages…";
                                    PcGamingWikiPages.Prepare(pcgwKeys, m => logger.Warn(m),
                                        progress != null ? progress.CancelToken : System.Threading.CancellationToken.None);
                                }
                                catch (Exception ex) { logger.Warn($"PCGamingWiki page check: {ex.Message}"); }
                            }

                            var snapshot = (List<Game>)dispatcher.Invoke(new Func<List<Game>>(() => playniteAPI.Database.Games.ToList()));
                            var todoIds = snapshot.Where(NeedsLinkWork).Select(g => g.Id).ToList();
                            if (progress != null)
                            {
                                progress.IsIndeterminate = false;
                                progress.ProgressMaxValue = Math.Max(1, todoIds.Count);
                                progress.CurrentProgressValue = 0;
                            }
                            if (todoIds.Count == 0)
                            {
                                if (progress != null)
                                {
                                    progress.Text = "No missing links found.";
                                    progress.CurrentProgressValue = progress.ProgressMaxValue;
                                }
                                return;
                            }

                            int updated = 0;
                            int processed = 0;
                            foreach (var id in todoIds)
                            {
                                if (progress != null && progress.CancelToken.IsCancellationRequested)
                                    break;

                                bool blocked = false;
                                dispatcher.Invoke(new Action(() =>
                                {
                                    if (AreGameLinksBlocked())
                                    {
                                        MarkLinkWorkDeferred();
                                        blocked = true;
                                        return;
                                    }
                                    var game = playniteAPI.Database.Games.Get(id);
                                    if (game != null && AddMissingLinks(game))
                                    {
                                        playniteAPI.Database.Games.Update(game);
                                        updated++;
                                    }
                                }));
                                processed++;
                                if (progress != null)
                                {
                                    progress.Text = "Adding links " + processed + " of " + todoIds.Count;
                                    progress.CurrentProgressValue = processed;
                                }
                                if (blocked)
                                    break;
                            }

                            if (progress != null && progress.CancelToken.IsCancellationRequested)
                                logger.Info("Game-link update canceled after " + processed + " game(s).");
                            if (updated > 0)
                                Breadcrumb.Write("Updated game links for " + updated + " game(s)");
                        };

                        OverlayProgressDialog.Run(
                            playniteAPI,
                            addLinks,
                            new GlobalProgressOptions("Adding game links", true));
                    }
                    catch (Exception ex)
                    {
                        logger.Error($"Adding game links failed: {ex.Message}");
                    }
                    finally
                    {
                        _linkPassRunning = false;
                    }
                });
            }
            catch { _linkPassRunning = false; }
        }

        private LocalGamesStore _localStore;

        /// <summary>
        /// Asks the Playnite Achievements extension (Santodan fork) to refresh these games in one batch. The
        /// extension only picks up new games on a library update, which games added by the Local Games Store
        /// never trigger. Uses its batch entry point when it can be reached, else its public single-game call.
        ///
        /// The extension refuses a request while another refresh runs ("Refresh already in progress") and does so
        /// silently. It starts such a refresh itself the moment a new game is saved, so this waits for it to finish,
        /// then asks, then checks what was stored and asks once more for games that still have nothing.
        /// Progress is shown in Playnite's progress dialog (same switch as the metadata download).
        /// </summary>
        private void RequestAchievementsRefresh(List<Guid> ids)
        {
            if (ids == null || ids.Count == 0) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var plugin = playniteAPI.Addons.Plugins.FirstOrDefault(p => p.Id == playniteAchievementsId);
                    if (plugin == null)
                    {
                        Breadcrumb.Write("Achievements: Playnite Achievements extension not installed, skipped");
                        return;
                    }

                    // The metadata dialog must be closed before this one opens.
                    var waitStart = DateTime.UtcNow;
                    while (_localStore != null && _localStore.IsMetadataBusy && (DateTime.UtcNow - waitStart).TotalSeconds < 60)
                        System.Threading.Thread.Sleep(200);

                    bool started = false;
                    Action<GlobalProgressActionArgs> work = progress =>
                    {
                        started = true;
                        RunAchievementsRefreshBlocking(plugin, ids, progress);
                    };
                    try
                    {
                        OverlayProgressDialog.Run(
                            playniteAPI,
                            work,
                            new GlobalProgressOptions("Downloading achievements", true) { IsIndeterminate = true },
                            Settings?.ShowMetadataDownloadProgress == true);
                    }
                    catch (Exception ex)
                    {
                        logger.Warn("Achievements progress dialog unavailable; continuing in background: " + ex.Message);
                        if (!started) RunAchievementsRefreshBlocking(plugin, ids, null);
                    }
                }
                catch (Exception ex)
                {
                    var inner = ex is System.Reflection.TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    logger.Warn("Requesting achievements for Local Games Store games failed: " + inner);
                    try { Breadcrumb.Write("Achievements: refresh FAILED: " + inner.GetType().Name + ": " + inner.Message); } catch { }
                }
            });
        }

        private void RunAchievementsRefreshBlocking(Plugin plugin, List<Guid> ids, GlobalProgressActionArgs progress)
        {
            try
            {
                // Games without achievements can leave the extension's refresh hanging, which kept this dialog
                // open forever. Give up after a hard deadline, or as soon as Cancel is pressed.
                var ct = progress != null ? progress.CancelToken : System.Threading.CancellationToken.None;
                var work = RunAchievementsRefreshAsync(plugin, ids, progress);
                var deadline = DateTime.UtcNow.AddSeconds(45);
                while (!work.IsCompleted)
                {
                    if (ct.IsCancellationRequested || DateTime.UtcNow > deadline)
                    {
                        Breadcrumb.Write("Achievements: " + (ct.IsCancellationRequested ? "cancelled" : "gave up after 45s (the game probably has no achievements)"));
                        work.ContinueWith(t => { var ignored = t.Exception; }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
                        return;
                    }
                    System.Threading.Tasks.Task.WhenAny(work, System.Threading.Tasks.Task.Delay(250)).Wait();
                }
                work.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                var inner = ex is System.Reflection.TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                logger.Warn("Achievements refresh failed: " + inner);
                try { Breadcrumb.Write("Achievements: refresh FAILED: " + inner.GetType().Name + ": " + inner.Message); } catch { }
            }
        }

        private async System.Threading.Tasks.Task RunAchievementsRefreshAsync(Plugin plugin, List<Guid> ids, GlobalProgressActionArgs progress)
        {
            var ct = progress != null ? progress.CancelToken : System.Threading.CancellationToken.None;
            Action<string> status = t => { if (progress != null) { try { progress.Text = t; } catch { } } };

            string label = ids.Count + " games";
            if (ids.Count == 1)
            {
                try { label = "'" + (playniteAPI.Database.Games.Get(ids[0])?.Name ?? "game") + "'"; } catch { label = "game"; }
            }

            var pending = ids.ToList();
            for (int attempt = 1; attempt <= 2 && pending.Count > 0; attempt++)
            {
                if (ct.IsCancellationRequested) { Breadcrumb.Write("Achievements: cancelled by user"); return; }

                // Let the extension's own refresh of the new game finish first, or it rejects the request.
                status("Waiting for Playnite Achievements to finish its current refresh…");
                await WaitForAchievementsIdle(plugin, 30, ct,
                    secs => status("Waiting for Playnite Achievements to finish its current refresh… (" + secs + "s)"));
                if (ct.IsCancellationRequested) { Breadcrumb.Write("Achievements: cancelled by user"); return; }
                await System.Threading.Tasks.Task.Delay(attempt == 1 ? 800 : 2000);

                status("Downloading achievements for " + label + "…");
                if (!await InvokeAchievementsRefresh(plugin, pending, ct)) return;

                status("Checking the downloaded achievements…");
                pending = await ReportAchievementOutcome(pending, ct);
                if (pending.Count > 0 && attempt == 1)
                    Breadcrumb.Write("Achievements: nothing stored yet for " + pending.Count + " game(s), asking again once the extension is idle");
            }
        }

        /// <summary>Waits (up to maxSeconds) until the extension reports that no refresh is running.</summary>
        private static async System.Threading.Tasks.Task WaitForAchievementsIdle(
            Plugin plugin, int maxSeconds, System.Threading.CancellationToken ct, Action<int> onWaiting)
        {
            try
            {
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic;
                var runtime = plugin.GetType().GetProperty("RefreshRuntime", flags)?.GetValue(plugin);
                var busyProp = runtime?.GetType().GetProperty("IsRebuilding", flags);
                if (busyProp == null) return;

                var started = DateTime.UtcNow;
                bool waited = false;
                while ((DateTime.UtcNow - started).TotalSeconds < maxSeconds && !ct.IsCancellationRequested)
                {
                    bool busy;
                    try { busy = (bool)busyProp.GetValue(runtime); } catch { return; }
                    if (!busy) break;
                    waited = true;
                    try { onWaiting?.Invoke((int)(DateTime.UtcNow - started).TotalSeconds); } catch { }
                    await System.Threading.Tasks.Task.Delay(500);
                }
                if (waited)
                    Breadcrumb.Write("Achievements: waited " + (int)(DateTime.UtcNow - started).TotalSeconds + "s for the extension's running refresh to end");
            }
            catch { }
        }

        /// <summary>Awaits a task for at most the given seconds. False on timeout or cancel; the task is left running.</summary>
        private static async System.Threading.Tasks.Task<bool> AwaitWithTimeout(System.Threading.Tasks.Task task, int seconds, System.Threading.CancellationToken ct)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!task.IsCompleted)
            {
                if (ct.IsCancellationRequested || DateTime.UtcNow > until)
                {
                    task.ContinueWith(t => { var ignored = t.Exception; }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
                    return false;
                }
                await System.Threading.Tasks.Task.WhenAny(task, System.Threading.Tasks.Task.Delay(250));
            }
            await task;
            return true;
        }

        /// <summary>Calls the extension's batch refresh (or its single-game call). False when it cannot be called at all.</summary>
        private async System.Threading.Tasks.Task<bool> InvokeAchievementsRefresh(Plugin plugin, List<Guid> ids, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken))
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic;
            var pluginType = plugin.GetType();
            object entry = null;
            try { entry = pluginType.GetProperty("RefreshEntryPoint", flags)?.GetValue(plugin); } catch { }
            if (entry == null)
            {
                try { entry = pluginType.GetField("_refreshCoordinator", flags)?.GetValue(plugin); } catch { }
            }
            var requestType = pluginType.Assembly.GetType("PlayniteAchievements.Models.RefreshRequest");
            var execute = entry == null || requestType == null ? null :
                entry.GetType().GetMethods(flags).FirstOrDefault(m => m.Name == "ExecuteAsync" &&
                    m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == requestType);
            var gameIdsProperty = requestType?.GetProperty("GameIds");
            if (execute != null && gameIdsProperty != null)
            {
                var request = Activator.CreateInstance(requestType);
                gameIdsProperty.SetValue(request, ids);
                var args = execute.GetParameters()
                    .Select((p, i) => i == 0 ? request : (p.HasDefaultValue ? p.DefaultValue : null)).ToArray();
                var task = execute.Invoke(entry, args) as System.Threading.Tasks.Task;
                if (task != null && !await AwaitWithTimeout(task, 20, ct))
                {
                    Breadcrumb.Write("Achievements: the extension did not answer within 20s, stopping (likely no achievements for this game)");
                    return false;
                }
                Breadcrumb.Write("Achievements: batch refresh requested for " + ids.Count + " game(s)");
                return true;
            }

            var single = pluginType.GetMethod("RequestSingleGameRefreshAsync", new[] { typeof(Guid) });
            if (single == null)
            {
                logger.Warn("Playnite Achievements extension has no usable refresh entry point.");
                return false;
            }
            foreach (var id in ids)
            {
                var task = single.Invoke(plugin, new object[] { id }) as System.Threading.Tasks.Task;
                if (task != null && !await AwaitWithTimeout(task, 20, ct))
                {
                    Breadcrumb.Write("Achievements: the extension did not answer within 20s, stopping (likely no achievements for this game)");
                    return false;
                }
            }
            Breadcrumb.Write("Achievements: single-game refresh requested for " + ids.Count + " game(s)");
            return true;
        }

        /// <summary>
        /// Polls what the extension stored (up to ~8 s, stopping as soon as every game has data), logs it and
        /// returns the games that still have nothing.
        /// </summary>
        private async System.Threading.Tasks.Task<List<Guid>> ReportAchievementOutcome(List<Guid> ids, System.Threading.CancellationToken ct)
        {
            var remaining = ids.ToList();
            var counts = new Dictionary<Guid, int>();
            try
            {
                var until = DateTime.UtcNow.AddSeconds(8);
                do
                {
                    foreach (var id in remaining.ToList())
                    {
                        Game game = null;
                        try { game = playniteAPI.Database.Games.Get(id); } catch { }
                        if (game == null) { remaining.Remove(id); continue; }
                        int count = GetPlayniteAchievementsData(game).Count;
                        counts[id] = count;
                        if (count > 0) remaining.Remove(id);
                    }
                    if (remaining.Count == 0 || ct.IsCancellationRequested) break;
                    await System.Threading.Tasks.Task.Delay(1000);
                } while (DateTime.UtcNow < until);

                foreach (var id in ids)
                {
                    Game game = null;
                    try { game = playniteAPI.Database.Games.Get(id); } catch { }
                    if (game == null) continue;
                    int count;
                    counts.TryGetValue(id, out count);
                    Breadcrumb.Write("Achievements: extension holds " + count + " achievement(s) for '" + game.Name + "'");
                }
            }
            catch (Exception ex) { try { Breadcrumb.Write("Achievements: outcome check failed: " + ex.Message); } catch { } }
            return remaining;
        }

        private void OnGamesCollectionChangedForLocalStore(object sender, ItemCollectionChangedEventArgs<Game> e)
        {
            try { _localStore?.OnGamesAdded(e?.AddedItems); } catch { }
        }

        private void OnLocalStoreGameItemUpdated(object sender, ItemUpdatedEventArgs<Game> e)
        {
            try
            {
                var updatedGames = e?.UpdatedItems?.Select(u => u?.NewData)
                    .Where(g => g != null).ToList();
                _localStore?.OnGamesUpdated(e?.UpdatedItems);
                if (updatedGames == null) return;
                foreach (var game in updatedGames)
                {
                    if (IsPcGame(game) || !HasPcExclusiveLinks(game)) continue;
                    if (AreGameLinksBlocked())
                    {
                        MarkLinkWorkDeferred();
                        continue;
                    }
                    if (RemovePcExclusiveLinks(game))
                        playniteAPI.Database.Games.Update(game);
                }
            }
            catch (Exception ex) { logger.Warn("Local Games Store metadata update hook failed: " + ex.Message); }
        }

        private void OnGamesCollectionChangedForLinks(object sender, ItemCollectionChangedEventArgs<Game> e)
        {
            try
            {
                if (Settings == null || e?.AddedItems == null || e.AddedItems.Count == 0) return;
                // A dropped exe is in the database while the Edit Game window is still open: do the platform /
                // link / GameBanana work only after the editor closes, on the game as it was saved.
                foreach (var addedGame in e.AddedItems.ToList())
                {
                    var addedId = addedGame.Id;
                    GameEditorWatch.WhenClosed(() => ProcessAddedGameForLinks(addedId));
                }
            }
            catch { }
        }

        private void ProcessAddedGameForLinks(Guid gameId)
        {
            try
            {
                if (Settings == null) return;
                var g = playniteAPI.Database.Games.Get(gameId);
                try { Breadcrumb.Write("Links: processing added game '" + (g != null ? g.Name : "(gone)") + "'"); } catch { }
                if (g != null)
                {
                    bool changed = false;

                    // A game added by hand (no library, no platform) is a PC game.
                    if (Settings.AutoPcPlatformForNewGames && g.PluginId == Guid.Empty &&
                        (g.PlatformIds == null || g.PlatformIds.Count == 0) &&
                        !(g.GameActions?.Any(a => a != null && a.Type == GameActionType.Emulator) ?? false))
                        changed |= SetPcPlatform(g);

                    bool shouldProcessLinks =
                        Settings.AutoAddGameLinks || Settings.AutoAddVortexLink ||
                        Settings.AutoAddSaveLinks || Settings.AutoAddScsKillerLink ||
                        (!IsPcGame(g) && HasPcExclusiveLinks(g));
                    if (shouldProcessLinks)
                    {
                        if (AreGameLinksBlocked()) MarkLinkWorkDeferred();
                        else changed |= AddMissingLinks(g);
                    }

                    if (changed)
                        playniteAPI.Database.Games.Update(g);
                    QueueGameBananaGame(g);
                    _installSize?.Queue(gameId);
                }
            }
            catch { }
        }

        // GameBanana lookups run for games on every platform, separately from the PC-only
        // PCGamingWiki/Nexus/save-link pass. The API is queried in a single throttled worker.
        private sealed class BananaWork
        {
            public Guid Id;
            public string Name;
            public string InstallDirectory;
            public string Key => Id.ToString("N") + "|" + Name + "|" + InstallDirectory;
        }
        private readonly object _bananaQueueLock = new object();
        private readonly Queue<BananaWork> _bananaQueue = new Queue<BananaWork>();
        private readonly HashSet<string> _bananaQueued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _bananaWorkerRunning;
        private volatile bool _gameBananaStopping;
        private volatile bool _gameBananaEnabled;

        private void ScheduleGameBananaLookups()
        {
            try
            {
                _gameBananaEnabled = Settings?.AutoAddGameBananaLink == true;
                if (_gameBananaStopping || !_gameBananaEnabled) return;
                if (AreGameLinksBlocked()) { MarkLinkWorkDeferred(); return; }
                // This is called at startup or when settings are saved, on Playnite's UI thread.
                foreach (var game in playniteAPI.Database.Games.ToList()) QueueGameBananaGame(game);
            }
            catch (Exception ex) { logger.Warn("GameBanana: could not queue games: " + ex.Message); }
        }

        private void QueueGameBananaGame(Game game)
        {
            if (_gameBananaStopping || !_gameBananaEnabled || game == null ||
                string.IsNullOrWhiteSpace(game.Name) || HasLinkTo(game, "gamebanana.com")) return;
            if (AreGameLinksBlocked()) { MarkLinkWorkDeferred(); return; }
            var work = new BananaWork { Id = game.Id, Name = game.Name, InstallDirectory = game.InstallDirectory };
            lock (_bananaQueueLock)
            {
                if (!_bananaQueued.Add(work.Key)) return;
                _bananaQueue.Enqueue(work);
                if (_bananaWorkerRunning) return;
                _bananaWorkerRunning = true;
            }
            var cachePath = Path.Combine(GetPluginUserDataPath(), "gamebanana_matches.json");
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            System.Threading.Tasks.Task.Run(() =>
            {
                bool finishedEmpty = false;
                bool lookupFailed = false;
                bool cancelled = false;
                int processed = 0;
                try
                {
                    System.Action<GlobalProgressActionArgs> lookup = progress =>
                    {
                        GameBananaLookup.Load(cachePath);
                        int total;
                        lock (_bananaQueueLock) total = _bananaQueue.Count;
                        if (progress != null)
                        {
                            progress.IsIndeterminate = false;
                            progress.ProgressMaxValue = Math.Max(1, total);
                            progress.CurrentProgressValue = 0;
                        }

                        while (!_gameBananaStopping && _gameBananaEnabled)
                        {
                            if (progress != null && progress.CancelToken.IsCancellationRequested)
                            {
                                cancelled = true;
                                break;
                            }

                            // Check on the UI thread before each network request; if a game
                            // starts mid-request, defer the rest until it is safe to continue.
                            bool blocked = dispatcher.Invoke(new Func<bool>(() =>
                            {
                                if (!AreGameLinksBlocked()) return false;
                                MarkLinkWorkDeferred();
                                return true;
                            }));
                            if (blocked) break;

                            BananaWork item;
                            lock (_bananaQueueLock)
                            {
                                if (_bananaQueue.Count == 0)
                                {
                                    GameBananaLookup.Save(cachePath);
                                    _bananaWorkerRunning = false;
                                    finishedEmpty = true;
                                    return;
                                }
                                item = _bananaQueue.Dequeue();
                            }

                            if (progress != null)
                                progress.Text = "Finding GameBanana link for " + item.Name;
                            try
                            {
                                // Null means network/response failure, not "no page".
                                var id = GameBananaLookup.Find(item.Name, item.InstallDirectory);
                                if (id == null)
                                {
                                    logger.Warn("GameBanana: lookup unavailable; will retry on a later scan.");
                                    lookupFailed = true;
                                    break;
                                }
                                if (id.Value > 0 && !_gameBananaStopping)
                                {
                                    int pageId = id.Value;
                                    dispatcher.Invoke(new Action(() =>
                                    {
                                        if (_gameBananaStopping || Settings?.AutoAddGameBananaLink != true) return;
                                        if (AreGameLinksBlocked()) { MarkLinkWorkDeferred(); return; }
                                        var current = playniteAPI.Database.Games.Get(item.Id);
                                        // Never overwrite a manually added link or apply a stale match.
                                        if (current == null || !string.Equals(current.Name, item.Name, StringComparison.Ordinal) ||
                                            !string.Equals(current.InstallDirectory, item.InstallDirectory, StringComparison.OrdinalIgnoreCase) ||
                                            HasLinkTo(current, "gamebanana.com")) return;
                                        if (current.Links == null) current.Links = new System.Collections.ObjectModel.ObservableCollection<Link>();
                                        current.Links.Add(new Link("GameBanana", "https://gamebanana.com/mods/games/" + pageId));
                                        SortManagedLinks(current);
                                        playniteAPI.Database.Games.Update(current);
                                    }));
                                }
                            }
                            finally { lock (_bananaQueueLock) _bananaQueued.Remove(item.Key); }

                            processed++;
                            int projectedTotal;
                            lock (_bananaQueueLock) projectedTotal = processed + _bananaQueue.Count;
                            if (projectedTotal > total)
                            {
                                total = projectedTotal;
                                if (progress != null) progress.ProgressMaxValue = total;
                            }
                            if (progress != null) progress.CurrentProgressValue = processed;
                            if (processed % 20 == 0) GameBananaLookup.Save(cachePath);
                        }
                    };

                    OverlayProgressDialog.Run(
                        playniteAPI,
                        lookup,
                        new GlobalProgressOptions("Finding GameBanana links", true));
                }
                catch (Exception ex)
                {
                    lookupFailed = true;
                    logger.Warn("GameBanana: " + ex.Message);
                }
                finally
                {
                    if (!finishedEmpty)
                    {
                        bool rescan;
                        lock (_bananaQueueLock)
                        {
                            GameBananaLookup.Save(cachePath);
                            rescan = !lookupFailed && !cancelled && !_gameBananaStopping &&
                                _gameBananaEnabled && _bananaQueue.Count > 0;
                            _bananaQueue.Clear();
                            _bananaQueued.Clear();
                            _bananaWorkerRunning = false;
                        }
                        if (rescan) dispatcher.BeginInvoke(new Action(ScheduleGameBananaLookups));
                        if (lookupFailed && !cancelled)
                            dispatcher.BeginInvoke(new Action(() => ScheduleLinkRetry("GameBanana lookup failed")));
                    }
                }
            });
        }

        // ---- Game link URLs (PCGamingWiki / Nexus Mods) ----
        private static string FindExistingLink(Game game, string host)
        {
            try
            {
                var link = game?.Links?.FirstOrDefault(l =>
                {
                    if (l == null || string.IsNullOrWhiteSpace(l.Url)) return false;
                    Uri u;
                    if (Uri.TryCreate(l.Url, UriKind.Absolute, out u))
                        return u.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
                               u.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);
                    return l.Url.IndexOf(host, StringComparison.OrdinalIgnoreCase) >= 0;
                });
                return link?.Url;
            }
            catch { return null; }
        }

        private static string CleanGameName(Game game)
        {
            var name = game?.Name ?? "";
            return System.Text.RegularExpressions.Regex.Replace(name, "[\u2122\u00AE\u00A9]", "").Trim();
        }

        private string PcgwCachePath()
        {
            try { return System.IO.Path.Combine(GetPluginUserDataPath(), "pcgw_pages.json"); }
            catch { return null; }
        }

        private static PcGamingWikiPages.GameKey PcgwKey(Game g)
        {
            string field = null, id = null;
            if (!string.IsNullOrEmpty(g.GameId) && g.GameId.All(char.IsDigit))
            {
                var store = SteamInstantPlay.GetStore(g);
                if (store == InstantStore.Steam) { field = "Steam_AppID"; id = g.GameId; }
                else if (store == InstantStore.Gog) { field = "GOGcom_ID"; id = g.GameId; }
            }
            return new PcGamingWikiPages.GameKey { Id = g.Id, Name = CleanGameName(g), Field = field, StoreId = id, ExtraName = ExistingPcgwPageName(g) };
        }

        /// <summary>The page name in a direct pcgamingwiki.com/wiki/Page link the game already has, if any.</summary>
        private static string ExistingPcgwPageName(Game g)
        {
            try
            {
                var link = g?.Links?.FirstOrDefault(l => l != null && LinkMatchesHost(l, "pcgamingwiki.com") &&
                    (l.Url ?? "").IndexOf("/wiki/", StringComparison.OrdinalIgnoreCase) >= 0);
                if (link == null) return null;
                var u = link.Url;
                int i = u.IndexOf("/wiki/", StringComparison.OrdinalIgnoreCase) + 6;
                var page = u.Substring(i);
                int cut = page.IndexOfAny(new[] { '#', '?' });
                if (cut >= 0) page = page.Substring(0, cut);
                page = Uri.UnescapeDataString(page).Replace('_', ' ').Trim();
                return page.Length > 0 ? page : null;
            }
            catch { return null; }
        }

        /// <summary>PCGamingWiki link used until a page check finds the game's real page: Steam / GOG id redirect, else a wiki search.</summary>
        private static string PcgwFallbackUrl(Game g)
        {
            try
            {
                if (!string.IsNullOrEmpty(g.GameId) && g.GameId.All(char.IsDigit))
                {
                    var store = SteamInstantPlay.GetStore(g);
                    if (store == InstantStore.Steam) return "https://www.pcgamingwiki.com/api/appid.php?appid=" + g.GameId;
                    if (store == InstantStore.Gog) return "https://www.pcgamingwiki.com/api/gog.php?page=" + g.GameId;
                }
            }
            catch { }
            return "https://www.pcgamingwiki.com/w/index.php?search=" + Uri.EscapeDataString(CleanGameName(g) ?? g.Name ?? "");
        }

        private static bool IsPcgwLink(Link l) { return l != null && LinkMatchesHost(l, "pcgamingwiki.com"); }

        /// <summary>A PCGamingWiki link this plugin generated earlier: a wiki search or an id-redirect, not a real page.</summary>
        private static bool IsAutoPcgwLink(Link l)
        {
            if (l == null || !LinkMatchesHost(l, "pcgamingwiki.com")) return false;
            if (!string.Equals((l.Name ?? "").Trim(), "PCGamingWiki", StringComparison.OrdinalIgnoreCase)) return false;
            var u = l.Url ?? "";
            return u.IndexOf("index.php?search=", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   u.IndexOf("/api/appid.php", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   u.IndexOf("/api/gog.php", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HasAutoPcgwLink(Game g)
        {
            return g?.Links != null && g.Links.Any(IsAutoPcgwLink);
        }

        /// <summary>
        /// The game's own PCGamingWiki page, or null when the wiki has none (or has not been checked yet).
        /// A link you added yourself always wins.
        /// </summary>
        private string BuildPcGamingWikiUrl(Game game)
        {
            try
            {
                if (game == null || string.IsNullOrWhiteSpace(game.Name)) return null;
                var existing = FindExistingLink(game, "pcgamingwiki.com");   // a link you already have wins
                if (existing != null) return existing;
                string pageUrl;
                return PcGamingWikiPages.Get(PcgwKey(game), out pageUrl) == PcGamingWikiPages.PageState.Page ? pageUrl : null;
            }
            catch { return null; }
        }

        private string BuildNexusModsUrl(Game game)
        {
            try
            {
                if (game == null || string.IsNullOrWhiteSpace(game.Name)) return null;
                var existing = FindExistingLink(game, "nexusmods.com");
                if (existing != null) return existing;
                return NexusGames.Find(game.Name);   // the game's own Nexus Mods page, or null
            }
            catch { return null; }
        }

        private string GetFullBackgroundImagePath(Game game)
        {
            try
            {
                string bg = game?.BackgroundImage;
                // Not-yet-downloaded art is a URL; the overlay then falls back to the cover.
                if (string.IsNullOrEmpty(bg) || bg.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    return null;
                return playniteAPI.Database.GetFullFilePath(bg);
            }
            catch (Exception ex)
            {
                logger.Error($"Error getting background image path: {ex}");
                return null;
            }
        }

        private string GetFullCoverImagePath(Game game)
        {
            if (string.IsNullOrEmpty(game.CoverImage))
                return null;

            try
            {
                return playniteAPI.Database.GetFullFilePath(game.CoverImage);
            }
            catch (Exception ex)
            {
                logger.Error($"Error getting cover image path: {ex}");
                return null;
            }
        }

        private static Process FindRunningGameProcess(Game game, int? pid)
        {
            if (game == null) return null;

            try
            {
                // Get process tree starting from the provided PID
                var processTree = GetProcessTree(pid);

                // Get all executable files in the game installation directory and subdirectories
                var gameExecutables = GetGameExecutables(game);

                // Get the game name words for title comparison
                string gameName = game.Name;
                string[] gameNameWords = gameName.ToLower().Split(new char[] { ' ', '-', '_', ':', '.', '(', ')', '[', ']' },
                    StringSplitOptions.RemoveEmptyEntries);

                Debug.WriteLine($"Game name for matching: {gameName}, split into {gameNameWords.Length} words");
                Debug.WriteLine($"Process tree contains {processTree.Count} processes");

                // Filter process tree to only include processes with main windows and sufficient memory
                var candidateProcesses = processTree.Where(p =>
                {
                    try
                    {
                        return !p.HasExited &&
                               p.MainWindowHandle != IntPtr.Zero &&
                               !string.IsNullOrEmpty(p.MainWindowTitle) &&
                               p.WorkingSet64 > 100 * 1024 * 1024;
                    }
                    catch
                    {
                        return false;
                    }
                }).ToList();

                Debug.WriteLine($"Found {candidateProcesses.Count} candidate processes in tree");

                var candidates = new List<Process>();
                var nameMatchCandidates = new List<Process>();
                var titleMatchCandidates = new List<(Process Process, int MatchCount)>();
                var inaccessibleCandidates = new List<Process>();

                foreach (var p in candidateProcesses)
                {
                    try
                    {
                        // Check if process name matches any executable in the game folder
                        bool nameMatches = gameExecutables.Any(exe =>
                            string.Equals(exe, p.ProcessName, StringComparison.OrdinalIgnoreCase));

                        // Check window title for matches with game name
                        int titleMatchScore = CalculateTitleMatchScore(p, gameNameWords);

                        try
                        {
                            // Try to access the module info
                            var modulePath = p.MainModule.FileName;
                            if (modulePath.IndexOf(game.InstallDirectory, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                candidates.Add(p);
                            }
                            else if (nameMatches)
                            {
                                nameMatchCandidates.Add(p);
                            }
                            else if (titleMatchScore > 0)
                            {
                                titleMatchCandidates.Add((p, titleMatchScore));
                            }
                        }
                        catch
                        {
                            // Can't access module info (likely due to 32/64 bit mismatch)
                            if (nameMatches)
                            {
                                nameMatchCandidates.Add(p);
                            }
                            else if (titleMatchScore > 0)
                            {
                                titleMatchCandidates.Add((p, titleMatchScore));
                            }
                            else
                            {
                                inaccessibleCandidates.Add(p);
                            }
                        }
                    }
                    catch { /* Skip processes we can't access at all */ }
                }

                // Priority order: direct path match, window title match, process name match, then best guess
                var bestMatch = FindBestMatch(candidates, titleMatchCandidates, nameMatchCandidates, inaccessibleCandidates, pid);

                return bestMatch;
            }
            catch (Exception ex)
            {
                logger.Error($"Error finding game process: {ex.Message}");
                return null;
            }
        }

        private static List<Process> GetProcessTree(int? rootPid)
        {
            var processTree = new List<Process>();

            if (rootPid == null || rootPid <= 0)
            {
                Debug.WriteLine("No root PID provided, using all processes with main windows");
                // Fallback to all processes with main windows if no PID provided
                return Process.GetProcesses()
                    .Where(p =>
                    {
                        try
                        {
                            return p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(p.MainWindowTitle);
                        }
                        catch
                        {
                            return false;
                        }
                    })
                    .ToList();
            }

            try
            {
                // Start with the root process
                var rootProcess = Process.GetProcessById(rootPid.Value);
                if (rootProcess != null && !rootProcess.HasExited)
                {
                    processTree.Add(rootProcess);
                    Debug.WriteLine($"Added root process: {rootProcess.ProcessName} (ID: {rootProcess.Id})");
                }
            }
            catch (Exception ex)
            {
                logger.Warn($"Could not find root process with ID {rootPid}: {ex.Message}");
            }

            // Get all child processes recursively
            var allProcesses = Process.GetProcesses();
            var processesById = allProcesses.ToDictionary(p => p.Id, p => p);
            var childProcesses = GetChildProcesses(rootPid.Value, processesById);

            processTree.AddRange(childProcesses);

            Debug.WriteLine($"Process tree built with {processTree.Count} total processes");
            return processTree;
        }

        private static List<Process> GetChildProcesses(int parentId, Dictionary<int, Process> processesById)
        {
            var children = new List<Process>();

            try
            {
                // Use P/Invoke to get child processes via Windows API
                foreach (var process in processesById.Values)
                {
                    try
                    {
                        if (!process.HasExited && GetParentProcessId(process.Id) == parentId)
                        {
                            children.Add(process);
                            Debug.WriteLine($"Added child process: {process.ProcessName} (ID: {process.Id})");

                            // Recursively get grandchildren
                            children.AddRange(GetChildProcesses(process.Id, processesById));
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error checking process {process.Id}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting child processes for PID {parentId}: {ex.Message}");
            }

            return children;
        }

        private static int GetParentProcessId(int processId)
        {
            try
            {
                var handle = OpenProcess(ProcessAccessFlags.QueryInformation, false, processId);
                if (handle == IntPtr.Zero)
                    return -1;

                try
                {
                    var pbi = new PROCESS_BASIC_INFORMATION();
                    int returnLength;
                    int status = NtQueryInformationProcess(handle, 0, ref pbi, Marshal.SizeOf(pbi), out returnLength);

                    if (status == 0)
                        return pbi.InheritedFromUniqueProcessId.ToInt32();
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting parent PID for process {processId}: {ex.Message}");
            }

            return -1;
        }

        private static List<string> GetGameExecutables(Game game)
        {
            var gameExecutables = new List<string>();
            try
            {
                gameExecutables = Directory.GetFiles(game.InstallDirectory, "*.exe", SearchOption.AllDirectories)
                    .Select(path => Path.GetFileNameWithoutExtension(path))
                    .ToList();

                // Add common variations
                var additionalNames = new List<string>();
                foreach (var exe in gameExecutables)
                {
                    additionalNames.Add(exe.Replace("-", ""));
                    additionalNames.Add(exe.Replace("_", ""));
                    additionalNames.Add(exe.Replace(" ", ""));
                    additionalNames.Add(exe.Replace("-", " "));
                    additionalNames.Add(exe.Replace("_", " "));
                }
                gameExecutables.AddRange(additionalNames);

                Debug.WriteLine($"Found {gameExecutables.Count} potential game executables in {game.InstallDirectory}");
            }
            catch (Exception ex)
            {
                logger.Error($"Error scanning game directory: {ex.Message}");
            }

            return gameExecutables;
        }

        private static int CalculateTitleMatchScore(Process process, string[] gameNameWords)
        {
            if (process.MainWindowHandle == IntPtr.Zero || string.IsNullOrEmpty(process.MainWindowTitle))
                return 0;

            string[] windowTitleWords = process.MainWindowTitle.ToLower().Split(
                new char[] { ' ', '-', '_', ':', '.', '(', ')', '[', ']' },
                StringSplitOptions.RemoveEmptyEntries);

            return gameNameWords.Count(gameWord =>
                windowTitleWords.Any(titleWord => titleWord.Contains(gameWord) || gameWord.Contains(titleWord)));
        }

        private static Process FindBestMatch(
            List<Process> candidates,
            List<(Process Process, int MatchCount)> titleMatchCandidates,
            List<Process> nameMatchCandidates,
            List<Process> inaccessibleCandidates,
            int? originalPid)
        {
            if (candidates.Count > 0)
            {
                var bestMatch = candidates.OrderByDescending(p => p.WorkingSet64).First();
                Debug.WriteLine($"Found process with matching path: {bestMatch.ProcessName} (ID: {bestMatch.Id})");
                return bestMatch;
            }


            if (nameMatchCandidates.Count > 0)
            {
                var bestMatch = nameMatchCandidates.OrderByDescending(p => p.WorkingSet64).First();
                Debug.WriteLine($"Found process with matching name: {bestMatch.ProcessName} (ID: {bestMatch.Id})");
                return bestMatch;
            }

            if (titleMatchCandidates.Count > 0)
            {
                var bestMatch = titleMatchCandidates.OrderByDescending(t => t.MatchCount)
                                                    .ThenByDescending(t => t.Process.WorkingSet64)
                                                    .First().Process;
                Debug.WriteLine($"Found process with matching window title: {bestMatch.ProcessName} (ID: {bestMatch.Id}, Title: {bestMatch.MainWindowTitle})");
                return bestMatch;
            }

            if (inaccessibleCandidates.Count > 0)
            {
                var processFromPlaynite = inaccessibleCandidates.FirstOrDefault(p => p.Id == originalPid);
                if (processFromPlaynite != null)
                {
                    Debug.WriteLine($"Found original process in tree: {processFromPlaynite.ProcessName} (ID: {processFromPlaynite.Id})");
                    return processFromPlaynite;
                }
            }

            return null;
        }

        private void log(string msg, string tag = "DEBUG")
        {
            if (true)
            {
                Debug.WriteLine("GameOverlay[" + tag + "]: " + msg);
            }
            logger.Debug(msg);
        }

        private void ShowPlaynite()
        {
            ShowPlaynite(false);
        }

        /// <summary>
        /// Focus the Playnite instance this extension runs in (Desktop or Fullscreen). Unlike
        /// ShowPlaynite(true) this never starts another Playnite shell: pressing Guide in Desktop
        /// mode used to launch the Fullscreen app.
        /// </summary>
        private void FocusRunningPlaynite()
        {
            try
            {
                // This path is only for idle Playnite navigation. Never take focus away
                // from a running/launching game or a parked game.
                if (IsGameRunningOrLaunching())
                {
                    log("Controller shortcut: not focusing Playnite while a game is active", "DEBUG");
                    return;
                }

                IntPtr hwnd = IntPtr.Zero;
                var main = System.Windows.Application.Current?.MainWindow;
                if (main != null)
                {
                    try
                    {
                        Action restoreMain = () =>
                        {
                            if (!main.IsVisible)
                                main.Show(); // e.g. hidden to tray
                            if (main.WindowState == System.Windows.WindowState.Minimized)
                                main.WindowState = System.Windows.WindowState.Normal;
                            main.Activate();
                            hwnd = new System.Windows.Interop.WindowInteropHelper(main).Handle;
                        };
                        if (main.Dispatcher.CheckAccess())
                            restoreMain();
                        else
                            main.Dispatcher.Invoke(restoreMain);
                    }
                    catch { }
                }

                if (hwnd == IntPtr.Zero)
                {
                    string own = IsDesktopMode ? "Playnite.DesktopApp" : "Playnite.FullscreenApp";
                    string other = IsDesktopMode ? "Playnite.FullscreenApp" : "Playnite.DesktopApp";
                    hwnd = WindowHelper.FindMainWindowByProcessName(own);
                    if (hwnd == IntPtr.Zero) hwnd = WindowHelper.FindMainWindowByProcessName(other);
                    // A hidden-to-tray main window is intentionally excluded by the normal
                    // lookup; only use this fallback after trying the visible windows.
                    if (hwnd == IntPtr.Zero) hwnd = WindowHelper.FindHiddenMainWindowByProcessName(own);
                    if (hwnd == IntPtr.Zero) hwnd = WindowHelper.FindHiddenMainWindowByProcessName(other);
                }

                if (hwnd != IntPtr.Zero)
                {
                    WindowHelper.ForceFocusWindow(hwnd);
                    log("Controller shortcut: focused Playnite (no game running)", "INFO");
                }
                else
                {
                    log("Controller shortcut: no Playnite window found to focus", "WARN");
                }
            }
            catch (Exception ex)
            {
                logger.Error($"FocusRunningPlaynite failed: {ex.Message}");
            }
        }

        private bool IsGameRunningOrLaunching()
        {
            if (WindowHelper.IsParked)
                return true;
            if (GameOverlayData != null && GameOverlayData.GameId != Guid.Empty)
                return true;
            if (overlayWindow?.ViewModel?.IsGameRunning == true)
                return true;
            try
            {
                return playniteAPI?.Database?.Games?.Any(g => g != null && (g.IsRunning || g.IsLaunching)) == true;
            }
            catch { return false; }
        }

        private void ShowPlaynite(bool forceFullscreen = false)
        {
            try
            {
                // Prefer Playnite's actual WPF main window. A shell hidden to the tray
                // can have no discoverable visible top-level HWND until it is shown.
                if (!forceFullscreen)
                {
                    try
                    {
                        var main = System.Windows.Application.Current?.MainWindow;
                        if (main != null)
                        {
                            if (!main.IsVisible)
                                main.Show();
                            var mainHwnd = new System.Windows.Interop.WindowInteropHelper(main).Handle;
                            if (mainHwnd != IntPtr.Zero)
                            {
                                WindowHelper.SafeRestoreAndFocus(mainHwnd);
                                return;
                            }
                        }
                    }
                    catch { }
                }

                // Prefer the shell matching the running Playnite mode; the old
                // Desktop-first lookup could focus a hidden Desktop window instead
                // of the active Fullscreen shell.
                string preferredProcess = forceFullscreen || !IsDesktopMode
                    ? "Playnite.FullscreenApp"
                    : "Playnite.DesktopApp";
                string fallbackProcess = preferredProcess == "Playnite.DesktopApp"
                    ? "Playnite.FullscreenApp"
                    : "Playnite.DesktopApp";
                IntPtr hwnd = WindowHelper.FindMainWindowByProcessName(preferredProcess);
                if (hwnd == IntPtr.Zero)
                    hwnd = WindowHelper.FindMainWindowByProcessName(fallbackProcess);

                if (hwnd != IntPtr.Zero)
                {
                    WindowHelper.SafeRestoreAndFocus(hwnd);
                    return;
                }

                // Only start a shell when no existing Playnite window can be found.
                string exe = forceFullscreen
                    ? "Playnite.FullscreenApp.exe"
                    : (IsDesktopMode ? "Playnite.DesktopApp.exe" : "Playnite.FullscreenApp.exe");
                Process.Start(Path.Combine(playniteAPI.Paths.ApplicationPath, exe));
            }
            catch (Exception ex)
            {
                logger.Error($"Error showing Playnite: {ex}");
            }
        }

        private List<AchievementData> ParseSuccessStoryData(string jsonData)
        {
            var achievements = new List<AchievementData>();

            try
            {
                var successStoryData = JsonSerializer.Deserialize<SuccessStoryData>(jsonData);

                if (successStoryData?.Items == null)
                {
                    logger.Error("Failed to parse SuccessStory data: Items is null");
                    return achievements;
                }

                foreach (var item in successStoryData.Items)
                {
                    bool isUnlocked = !string.IsNullOrEmpty(item.DateUnlockedStr);
                    DateTime? unlockDate = null;

                    if (isUnlocked && DateTime.TryParse(item.DateUnlockedStr, out DateTime parsedDate))
                    {
                        unlockDate = parsedDate;
                    }

                    achievements.Add(new AchievementData
                    {
                        Name = item.Name,
                        Description = item.Description,
                        IsUnlocked = isUnlocked,
                        UnlockDate = unlockDate,
                        IconUrl = isUnlocked ? item.UrlUnlocked : item.UrlLocked,
                        GlobalPercent = item.Percent
                    });
                }

                log($"Successfully parsed {achievements.Count} achievements from SuccessStory");
            }
            catch (Exception ex)
            {
                log($"Error parsing SuccessStory file: {ex.Message}", "ERROR");
            }

            return achievements;
        }

        /// <summary>
        /// Parses PlayniteAchievements GameAchievementData JSON
        /// (Achievements list of AchievementDetail-compatible objects).
        /// </summary>
        private List<AchievementData> ParsePlayniteAchievementsData(string jsonData)
        {
            var achievements = new List<AchievementData>();

            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var data = JsonSerializer.Deserialize<PlayniteAchievementsGameData>(jsonData, options);
                if (data?.Achievements == null || data.Achievements.Count == 0)
                {
                    // Some exports wrap items similarly to SuccessStory
                    var asSuccessStory = JsonSerializer.Deserialize<SuccessStoryData>(jsonData, options);
                    if (asSuccessStory?.Items != null && asSuccessStory.Items.Count > 0)
                        return ParseSuccessStoryData(jsonData);

                    log("PlayniteAchievements data had no achievements list", "DEBUG");
                    return achievements;
                }

                foreach (var item in data.Achievements)
                {
                    bool isUnlocked = item.Unlocked || item.UnlockTimeUtc.HasValue;
                    DateTime? unlockDate = null;

                    if (item.UnlockTimeUtc.HasValue)
                    {
                        var utc = item.UnlockTimeUtc.Value;
                        if (utc != DateTime.MinValue)
                        {
                            if (utc.Kind == DateTimeKind.Unspecified)
                                utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
                            unlockDate = utc.ToLocalTime();
                        }
                    }

                    string icon = isUnlocked
                        ? (item.UnlockedIconPath ?? item.Icon ?? item.UrlUnlocked)
                        : (item.LockedIconPath ?? item.UnlockedIconPath ?? item.Icon ?? item.UrlLocked);

                    achievements.Add(new AchievementData
                    {
                        Name = item.DisplayName ?? item.Name ?? item.ApiName,
                        Description = item.Description,
                        IsUnlocked = isUnlocked,
                        UnlockDate = unlockDate,
                        IconUrl = icon,
                        GlobalPercent = item.GlobalPercentUnlocked ?? item.Percent ?? item.GlobalPercent ?? item.GlobalUnlockPercent
                    });
                }

                log($"Successfully parsed {achievements.Count} achievements from PlayniteAchievements");
            }
            catch (Exception ex)
            {
                log($"Error parsing PlayniteAchievements file: {ex.Message}", "ERROR");
            }

            return achievements;
        }


        // Independent of the installed extension directory: survives uninstall/reinstall.
        private string SettingsRecoveryPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PlayniteExtensionSettingsRecovery", Id.ToString("D"), "config.json");

        internal void RecoverSettingsIfNeeded()
        {
            try
            {
                var path = Path.Combine(GetPluginUserDataPath(), "config.json");
                var backup = SettingsRecoveryPath;
                if (!File.Exists(path) && File.Exists(backup))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.Copy(backup, path, false);
                }
                PreserveCurrentSettings();
            }
            catch (Exception ex) { logger.Warn("Settings recovery unavailable: " + ex.Message); }
        }

        private void PreserveCurrentSettings()
        {
            try
            {
                var src = Path.Combine(GetPluginUserDataPath(), "config.json");
                if (!File.Exists(src)) return;
                string json = File.ReadAllText(src);
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
                }
                var destination = SettingsRecoveryPath;
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                var temp = destination + ".tmp";
                File.WriteAllText(temp, json);
                if (File.Exists(destination)) File.Delete(destination);
                File.Move(temp, destination);
            }
            catch (Exception ex) { logger.Warn("Settings backup unavailable: " + ex.Message); }
        }

        internal void SaveSettingsSafely(OverlaySettings value)
        {
            PreserveCurrentSettings();
            SavePluginSettings(value);
            PreserveCurrentSettings();
        }

        #region Settings
        public override ISettings GetSettings(bool firstRunSettings)
        {
            if (settings == null)
            {
                settings = new OverlaySettings(this);
            }

            return settings;
        }

        public override System.Windows.Controls.UserControl GetSettingsView(bool firstRunSettings)
        {
            return new OverlaySettingsView();
        }

        // You might also want to add a public property to easily access settings
        public OverlaySettings Settings
        {
            get
            {
                if (settings == null)
                {
                    settings = (OverlaySettings)GetSettings(false);
                }

                return settings;
            }
        }
        #endregion

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(ProcessAccessFlags processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
            ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

        [Flags]
        private enum ProcessAccessFlags : uint
        {
            Terminate = 0x0001,
            QueryInformation = 0x00000400
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2_0;
            public IntPtr Reserved2_1;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

    }

    public class AchievementData
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public bool IsUnlocked { get; set; }
        public DateTime? UnlockDate { get; set; }
        public string IconUrl { get; set; }

        /// <summary>
        /// Global unlock percentage (0–100). Null when the source does not provide rarity.
        /// </summary>
        public double? GlobalPercent { get; set; }

        /// <summary>Opacity for list rows (locked achievements are dimmed).</summary>
        public double DisplayOpacity => IsUnlocked ? 1.0 : 0.45;

        public string StatusText
        {
            get
            {
                if (!IsUnlocked)
                    return "Locked";
                if (UnlockDate.HasValue)
                    return UnlockDate.Value.ToString("g");
                return "Unlocked";
            }
        }

        public string UnlockStateText => IsUnlocked ? "UNLOCKED" : "LOCKED";

        /// <summary>Steam-style rarity label + percent, e.g. "Rare · 8.2%".</summary>
        public string RarityText
        {
            get
            {
                if (!GlobalPercent.HasValue)
                    return string.Empty;
                double p = GlobalPercent.Value;
                string tier;
                if (p < 5.0) tier = "Extremely Rare";
                else if (p < 10.0) tier = "Ultra Rare";
                else if (p < 20.0) tier = "Very Rare";
                else if (p < 50.0) tier = "Rare";
                else if (p < 80.0) tier = "Uncommon";
                else tier = "Common";
                return $"{tier} · {p:0.#}%";
            }
        }

        /// <summary>Tier name only, e.g. "Very Rare".</summary>
        public string RarityTierText
        {
            get
            {
                if (!GlobalPercent.HasValue) return string.Empty;
                double p = GlobalPercent.Value;
                if (p < 5.0) return "Extremely Rare";
                if (p < 10.0) return "Ultra Rare";
                if (p < 20.0) return "Very Rare";
                if (p < 50.0) return "Rare";
                if (p < 80.0) return "Uncommon";
                return "Common";
            }
        }

        /// <summary>Console-style rarity line, e.g. "Very Rare | 9.6% of players earned".</summary>
        public string RarityDetailText
        {
            get
            {
                if (!GlobalPercent.HasValue) return string.Empty;
                return $"{RarityTierText} | {GlobalPercent.Value:0.#}% of players earned";
            }
        }

        /// <summary>Brush color hex for the rarity tier (Steam-inspired).</summary>
        public string RarityColor
        {
            get
            {
                if (!GlobalPercent.HasValue)
                    return "#FF8F98A0";
                double p = GlobalPercent.Value;
                if (p < 5.0) return "#FFE4AE39";   // gold
                if (p < 10.0) return "#FFB072FF";  // purple
                if (p < 20.0) return "#FF66C0F4";  // blue
                if (p < 50.0) return "#FF5BAE5B";  // green
                if (p < 80.0) return "#FFA0B0C0";  // light gray-blue
                return "#FF8F98A0";                 // gray
            }
        }
    }

    // Update your GameOverlayData class to include achievements
    public class GameOverlayData
    {
        public Guid GameId { get; set; }
        public string GameName { get; set; }
        public int ProcessId { get; set; }
        public DateTime GameStartTime { get; set; }
        public TimeSpan Playtime { get; set; }
        public string CoverImagePath { get; set; }
        public string BackgroundImagePath { get; set; }
        public List<AchievementData> Achievements { get; set; }
        public string ExecutablePath { get; set; }
    }

    // Define classes that match the JSON structure
    public class SuccessStoryData
    {
        [JsonPropertyName("Items")]
        public List<SuccessStoryAchievement> Items { get; set; }

        [JsonPropertyName("Name")]
        public string Name { get; set; }
    }

    public class SuccessStoryAchievement
    {
        [JsonPropertyName("Name")]
        public string Name { get; set; }

        [JsonPropertyName("Description")]
        public string Description { get; set; }

        [JsonPropertyName("DateUnlocked")]
        public string DateUnlockedStr { get; set; }

        [JsonPropertyName("UrlUnlocked")]
        public string UrlUnlocked { get; set; }

        [JsonPropertyName("UrlLocked")]
        public string UrlLocked { get; set; }

        [JsonPropertyName("Percent")]
        public double? Percent { get; set; }
    }

    /// <summary>
    /// Mirrors PlayniteAchievements.Models.Achievements.GameAchievementData (JSON cache shape).
    /// </summary>
    public class PlayniteAchievementsGameData
    {
        [JsonPropertyName("Achievements")]
        public List<PlayniteAchievementsAchievement> Achievements { get; set; }

        [JsonPropertyName("GameName")]
        public string GameName { get; set; }

        [JsonPropertyName("HasAchievements")]
        public bool HasAchievements { get; set; }
    }

    /// <summary>
    /// Mirrors PlayniteAchievements.Models.Achievements.AchievementDetail fields we care about.
    /// Extra properties are ignored by System.Text.Json when deserializing.
    /// </summary>
    public class PlayniteAchievementsAchievement
    {
        [JsonPropertyName("ApiName")]
        public string ApiName { get; set; }

        [JsonPropertyName("DisplayName")]
        public string DisplayName { get; set; }

        [JsonPropertyName("Name")]
        public string Name { get; set; }

        [JsonPropertyName("Description")]
        public string Description { get; set; }

        [JsonPropertyName("Unlocked")]
        public bool Unlocked { get; set; }

        [JsonPropertyName("UnlockTimeUtc")]
        public DateTime? UnlockTimeUtc { get; set; }

        [JsonPropertyName("UnlockedIconPath")]
        public string UnlockedIconPath { get; set; }

        [JsonPropertyName("LockedIconPath")]
        public string LockedIconPath { get; set; }

        // SuccessStory-compatible / theme aliases sometimes present in exports
        [JsonPropertyName("Icon")]
        public string Icon { get; set; }

        [JsonPropertyName("UrlUnlocked")]
        public string UrlUnlocked { get; set; }

        [JsonPropertyName("UrlLocked")]
        public string UrlLocked { get; set; }

        [JsonPropertyName("Percent")]
        public double? Percent { get; set; }

        [JsonPropertyName("GlobalPercent")]
        public double? GlobalPercent { get; set; }

        [JsonPropertyName("GlobalUnlockPercent")]
        public double? GlobalUnlockPercent { get; set; }

        /// <summary>Canonical PlayniteAchievements field (AchievementDetail.GlobalPercentUnlocked).</summary>
        [JsonPropertyName("GlobalPercentUnlocked")]
        public double? GlobalPercentUnlocked { get; set; }
    }

    public class SuppressedInstantPlayAction
    {
        public Guid GameId { get; set; }
        public string ActionKey { get; set; }
    }

    /// <summary>A user-named snapshot of the overlay's appearance settings.</summary>
    public class OverlayAppearancePreset
    {
        public string Name { get; set; }
        public string AccentColor { get; set; }
        public string PanelBackgroundColor { get; set; }
        public double PanelOpacity { get; set; }
        public double PanelCornerRadius { get; set; }
        public bool PanelGlassBorder { get; set; }
        public double PanelGlassStrength { get; set; } = 0.5;
        public double BackdropDim { get; set; }
        public bool UseWindowsAccent { get; set; }
        public bool UseCoverAccent { get; set; }

        public static OverlayAppearancePreset FromSettings(string name, OverlaySettings settings)
        {
            return new OverlayAppearancePreset
            {
                Name = (name ?? string.Empty).Trim(),
                AccentColor = settings.AccentColor,
                PanelBackgroundColor = settings.PanelBackgroundColor,
                PanelOpacity = settings.PanelOpacity,
                PanelCornerRadius = settings.PanelCornerRadius,
                PanelGlassBorder = settings.PanelGlassBorder,
                PanelGlassStrength = settings.PanelGlassStrength,
                BackdropDim = settings.BackdropDim,
                UseWindowsAccent = settings.UseWindowsAccent,
                UseCoverAccent = settings.UseCoverAccent
            };
        }

        public OverlayAppearancePreset Copy()
        {
            return (OverlayAppearancePreset)MemberwiseClone();
        }

        public bool Matches(OverlaySettings settings)
        {
            return settings != null &&
                string.Equals(AccentColor, settings.AccentColor, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(PanelBackgroundColor, settings.PanelBackgroundColor, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(PanelOpacity - settings.PanelOpacity) < 0.005 &&
                Math.Abs(PanelCornerRadius - settings.PanelCornerRadius) < 0.5 &&
                PanelGlassBorder == settings.PanelGlassBorder &&
                Math.Abs(PanelGlassStrength - settings.PanelGlassStrength) < 0.005 &&
                Math.Abs(BackdropDim - settings.BackdropDim) < 0.005 &&
                UseWindowsAccent == settings.UseWindowsAccent &&
                UseCoverAccent == settings.UseCoverAccent;
        }

        public void ApplyTo(OverlaySettings settings)
        {
            if (settings == null) return;
            settings.UseWindowsAccent = UseWindowsAccent;
            settings.UseCoverAccent = UseCoverAccent;
            settings.AccentColor = AccentColor;
            settings.PanelBackgroundColor = PanelBackgroundColor;
            settings.PanelOpacity = PanelOpacity;
            settings.PanelCornerRadius = PanelCornerRadius;
            settings.PanelGlassBorder = PanelGlassBorder;
            settings.PanelGlassStrength = PanelGlassStrength;
            settings.BackdropDim = BackdropDim;
        }
    }

    /// <summary>Finds the installed SCSKiller app (SCSKiller.exe).</summary>
    internal static class ScsKillerInfo
    {
        private static string _exe;
        private static int _exeTick;

        /// <summary>SCSKiller.exe, or null when it can't be found (e.g. a portable copy in an unusual folder).</summary>
        public static string GetExePath()
        {
            int now = Environment.TickCount;
            // Only a successful lookup is cached for long; a miss is retried after 10 s.
            if (_exeTick != 0 && unchecked(now - _exeTick) < (_exe != null ? 60000 : 10000)) return _exe;
            string path = FromAppPaths() ?? FromUninstallKeys() ?? FromDefaultFolders();
            _exe = path;
            _exeTick = now == 0 ? 1 : now;
            return path;
        }

        private static bool IsScsKillerExe(string p)
        {
            try
            {
                return !string.IsNullOrEmpty(p) && File.Exists(p) &&
                       string.Equals(System.IO.Path.GetFileName(p), "SCSKiller.exe", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string CleanExe(string value)
        {
            var v = (value ?? "").Trim();
            int comma = v.LastIndexOf(',');
            if (comma > 0 && !v.EndsWith("\\")) v = v.Substring(0, comma);
            return v.Trim().Trim('"');
        }

        private static string FromAppPaths()
        {
            var hives = new[] { Registry.CurrentUser, Registry.LocalMachine };
            foreach (var hive in hives)
            {
                try
                {
                    using (var k = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\SCSKiller.exe"))
                    {
                        var p = CleanExe(k?.GetValue("") as string);
                        if (IsScsKillerExe(p)) return p;
                    }
                }
                catch { }
            }
            return null;
        }

        private static string FromUninstallKeys()
        {
            var roots = new[]
            {
                new { Hive = Registry.CurrentUser, Sub = @"Software\Microsoft\Windows\CurrentVersion\Uninstall" },
                new { Hive = Registry.LocalMachine, Sub = @"Software\Microsoft\Windows\CurrentVersion\Uninstall" },
                new { Hive = Registry.LocalMachine, Sub = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" }
            };
            foreach (var r in roots)
            {
                try
                {
                    using (var parent = r.Hive.OpenSubKey(r.Sub))
                    {
                        if (parent == null) continue;
                        foreach (var name in parent.GetSubKeyNames())
                        {
                            try
                            {
                                using (var k = parent.OpenSubKey(name))
                                {
                                    var display = ((k?.GetValue("DisplayName") as string) ?? "").Trim();
                                    if (!display.StartsWith("SCSKiller", StringComparison.OrdinalIgnoreCase)) continue;
                                    var loc = ((k.GetValue("InstallLocation") as string) ?? "").Trim().Trim('"');
                                    if (loc.Length > 0)
                                    {
                                        foreach (var sub in new[] { "", "current" })
                                        {
                                            var exe = System.IO.Path.Combine(loc, sub, "SCSKiller.exe");
                                            if (IsScsKillerExe(exe)) return exe;
                                        }
                                    }
                                    var icon = CleanExe(k.GetValue("DisplayIcon") as string);
                                    if (IsScsKillerExe(icon)) return icon;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        private static string FromDefaultFolders()
        {
            var folders = new List<string>();
            Action<Environment.SpecialFolder, string> add = (f, sub) =>
            {
                try
                {
                    var b = Environment.GetFolderPath(f);
                    if (!string.IsNullOrEmpty(b)) folders.Add(System.IO.Path.Combine(b, sub));
                }
                catch { }
            };
            add(Environment.SpecialFolder.LocalApplicationData, @"Programs\SCSKiller");
            add(Environment.SpecialFolder.LocalApplicationData, @"SCSKiller\current");
            add(Environment.SpecialFolder.LocalApplicationData, "SCSKiller");
            add(Environment.SpecialFolder.ProgramFiles, "SCSKiller");
            add(Environment.SpecialFolder.ProgramFilesX86, "SCSKiller");
            foreach (var f in folders)
            {
                var exe = System.IO.Path.Combine(f, "SCSKiller.exe");
                if (IsScsKillerExe(exe)) return exe;
            }
            return null;
        }
    }

    public partial class OverlaySettings : ObservableObject, ISettings
    {
        private readonly PlayniteGameOverlay plugin;

        // Using properties with setters that notify of changes
        private ControllerShortcut _controllerShortcut = ControllerShortcut.StartBack;
        private ButtonPromptStyle _buttonPromptStyle = ButtonPromptStyle.Auto;
        private CloseBehavior _closeBehavior = CloseBehavior.CloseAndEnd;
        private bool _enableAltF4InstantClose = true;
        private bool _enableSteamInstantPlay = false;
        // One-time migration used to restore Instant Play after the temporary
        // stability build disabled it. Once applied, users can freely turn
        // Instant Play off again without it being forced back on.
        private bool _instantPlayRestoreApplied = false;
        private List<Guid> _instantPlayDisabledGames = new List<Guid>();
        private List<SuppressedInstantPlayAction> _suppressedInstantPlayActions = new List<SuppressedInstantPlayAction>();
        private bool _enableSuspendMinimize = true;
        private bool _enableDesktopControllerNav = true;
        private bool _subtractSuspendedFromPlaytime = true;
        private bool _subtractSuspendedOnlyNonLibrary = false;
        private bool _excludeShortSessions = true;
        private int _minSessionMinutes = 10;
        private string _hotkeyOverlayToggle = "Alt+Oem3";
        private string _hotkeyForceClose = "Alt+F4";
        private string _hotkeySuspend = "Pause";
        private string _hotkeyVolumeUp = "";
        private string _hotkeyVolumeDown = "";
        private string _hotkeyVolumeMute = "";
        private bool _enablePrintScreenRemap = false;
        private string _controllerHotkeyOverlay = "";
        private string _controllerHotkeyScreenshot = "";
        private string _controllerHotkeySuspend = "";
        private string _controllerHotkeyMute = "";
        private string _printScreenRemapCombo = "^+{F12}";
        private string _volumeControlMode = "Master";
        private string _hotkeyGameSettings = "";
        private string _hotkeyGlobalSettings = "";
        private bool _showTopBarGameSettings = true;
        private int _volumeHotkeyStep = 10;
        private bool _debugMode = false;

        // Extension settings windows have their own preset so existing custom in-game
        // overlay colors are preserved until the user chooses an overlay preset.
        private string _settingsThemePreset = "Steam Dark";
        public string SettingsThemePreset
        {
            get => _settingsThemePreset;
            set => SetValue(ref _settingsThemePreset,
                string.Equals(value, "Steam Dark", StringComparison.OrdinalIgnoreCase) ? "Steam Dark" :
                string.Equals(value, "OLED", StringComparison.OrdinalIgnoreCase) ? "OLED" :
                string.Equals(value, "Windows 11", StringComparison.OrdinalIgnoreCase) ? "Windows 11" : "Steam");
        }
        private string _settingsThemePresetBackup;

        // Appearance
        private List<OverlayAppearancePreset> _savedAppearancePresets = new List<OverlayAppearancePreset>();
        public List<OverlayAppearancePreset> SavedAppearancePresets
        {
            get => _savedAppearancePresets;
            set => SetValue(ref _savedAppearancePresets, value ?? new List<OverlayAppearancePreset>());
        }

        private string _achievementStyle = "Console";
        public string AchievementStyle
        {
            get => _achievementStyle;
            set => SetValue(ref _achievementStyle,
                string.Equals(value, "Desktop", StringComparison.OrdinalIgnoreCase) ? "Desktop" : "Console");
        }

        private string _accentColor = "#66C0F4";
        private string _panelBackgroundColor = "#1A1D23";
        private double _panelOpacity = 1.0;
        private double _panelCornerRadius = 10;
        private bool _panelGlassBorder = true;   // frosted panels are on by default
        private bool _settingsMenuGlass;
        private double _panelGlassStrength = 0.5;   // 0 = faint, 1 = strong; 0.5 = the standard glass look
        private double _backdropDim = 0.15;
        /// <summary>When true, accent follows the live Windows accent color (Windows 11 preset).</summary>
        private bool _useWindowsAccent = false;
        /// <summary>When true, accent follows the current game's cover image.</summary>
        private bool _useCoverAccent = true;
        // Last Custom values so switching away from Custom and back restores them
        private string _customAccentColor = "#66C0F4";
        private string _customPanelBackgroundColor = "#1A1D23";
        private double _customPanelOpacity = 1.0;
        private double _customPanelCornerRadius = 10;
        private bool _customPanelGlassBorder = true;
        private double _customPanelGlassStrength = 0.5;
        private double _customBackdropDim = 0.15;
        private bool _customUseCoverAccent;
        private AspectRatio _aspectRatio = AspectRatio.Portrait;

        // Toggle properties for showing/hiding buttons
        private bool _showPlayniteButton = true;
        private bool _showRecordGameplay = true;
        private bool _showRecordRecent = true;
        private bool _showStreaming = true;
        private bool _showPerformanceOverlay = true;
        private bool _showScreenshotGallery = true;
        private bool _showWebBrowser = true;
        private bool _showDiscord = false;
        private bool _showBattery = false;
        private bool _showFpsLimiterPanel = true;
        private bool _showGameVolume = true;
        private bool _hideCursorOnGamepad = true;
        private bool _showGamepadHints = true; // on-screen button prompts in the overlay (default on for new users)
        private bool _pauseGameWhileOverlayOpen = true; // freeze game while overlay open (on by default; see loader)

        // Dragged panel offsets (design-space TranslateTransform, persist across opens)
        // Default layout: FPS limiter + volume in a centre column (offsets from their XAML positions).
        public const double DefaultFpsPanelOffsetX = 301;
        public const double DefaultFpsPanelOffsetY = -76;
        public const double DefaultAudioPanelOffsetX = 633;
        public const double DefaultAudioPanelOffsetY = -15;

        private double _fpsPanelOffsetX = DefaultFpsPanelOffsetX;
        private double _fpsPanelOffsetY = DefaultFpsPanelOffsetY;
        private double _audioPanelOffsetX = DefaultAudioPanelOffsetX;
        private double _audioPanelOffsetY = DefaultAudioPanelOffsetY;
        private bool _panelLayoutInitialized;

        /// <summary>Set once the panel layout has been seen; lets a never-customised (0,0) layout adopt the new defaults.</summary>
        public bool PanelLayoutInitialized
        {
            get => _panelLayoutInitialized;
            set => SetValue(ref _panelLayoutInitialized, value);
        }

        public void ResetPanelLayout()
        {
            FpsPanelOffsetX = DefaultFpsPanelOffsetX;
            FpsPanelOffsetY = DefaultFpsPanelOffsetY;
            AudioPanelOffsetX = DefaultAudioPanelOffsetX;
            AudioPanelOffsetY = DefaultAudioPanelOffsetY;
        }
        private string _lastFocusedPanel = "playnite";

        // Shortcut and path properties
        private string _recordGameplayShortcut = "";
        private string _recordRecentShortcut = "";
        private string _streamingShortcut = "";
        private string _performanceOverlayShortcut = "";
        private string _screenshotGalleryPath = "";
        private string _webBrowserPath = "";

        // Integrated FPS Limiter (RTSS) — replaces needing a separate FPS Limiter plugin for overlay use
        private string _backdropImageMode = "Auto";
        /// <summary>
        /// Auto | Banner | Fill | Fit | Stretch. Auto uses Banner (sharp image at its natural shape over
        /// a blurred fill) when the artwork's shape differs a lot from the screen, e.g. landscape SteamGridDB heroes.
        /// </summary>
        public string BackdropImageMode
        {
            get => _backdropImageMode;
            set => SetValue(ref _backdropImageMode,
                (value == "Banner" || value == "Fill" || value == "Fit" || value == "Stretch") ? value : "Auto");
        }

        private double _backdropImageScale = 100;
        /// <summary>Zoom of the backdrop image in percent (50-200).</summary>
        public double BackdropImageScale
        {
            get => _backdropImageScale;
            set => SetValue(ref _backdropImageScale, Math.Max(50, Math.Min(200, value)));
        }

        private bool _previewBackdropResolved;
        private string _previewBackdropPath;
        /// <summary>Background art of the running / most recently played game, for the settings live preview.</summary>
        public string GetPreviewBackdropPath()
        {
            if (_previewBackdropResolved) return _previewBackdropPath;
            _previewBackdropResolved = true;
            try
            {
                var api = plugin?.PlayniteApi;
                var games = api?.Database?.Games;
                if (games == null) return null;
                var candidate = games
                    .Where(g => !string.IsNullOrEmpty(g.BackgroundImage) &&
                                !g.BackgroundImage.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(g => g.IsRunning)
                    .ThenByDescending(g => g.LastActivity ?? DateTime.MinValue)
                    .FirstOrDefault();
                if (candidate != null)
                {
                    var p = api.Database.GetFullFilePath(candidate.BackgroundImage);
                    if (System.IO.File.Exists(p)) _previewBackdropPath = p;
                }
            }
            catch { }
            return _previewBackdropPath;
        }

        private bool _useGameBackgroundBackdrop = true;
        /// <summary>Show the blurred game background behind the overlay when a fullscreen game steps aside.</summary>
        public bool UseGameBackgroundBackdrop
        {
            get => _useGameBackgroundBackdrop;
            set => SetValue(ref _useGameBackgroundBackdrop, value);
        }

        private double _backdropBlur = 30;
        /// <summary>Blur radius (0-80) of that background image.</summary>
        public double BackdropBlur
        {
            get => _backdropBlur;
            set => SetValue(ref _backdropBlur, Math.Max(0, Math.Min(80, value)));
        }

        private bool _enableBreadcrumbLog = false;
        /// <summary>Write breadcrumbs.log (one line per important event) to the extension data folder.</summary>
        public bool EnableBreadcrumbLog
        {
            get => _enableBreadcrumbLog;
            set
            {
                SetValue(ref _enableBreadcrumbLog, value);
                Breadcrumb.Enabled = value;
            }
        }

        private bool _fakeFullscreen = false;
        /// <summary>Let Windows run exclusive-fullscreen D3D10/11/12 games as borderless "fake fullscreen" while a game runs. Default OFF.</summary>
        public bool FakeFullscreen
        {
            get => _fakeFullscreen;
            set
            {
                SetValue(ref _fakeFullscreen, value);
                OnPropertyChanged(nameof(FullscreenGamesSupport));
            }
        }

        private bool _borderlessWindowedGames = true;
        /// <summary>Run windowed games as borderless fullscreen (strip frame, cover the monitor). Default OFF.</summary>
        public bool BorderlessWindowedGames
        {
            get => _borderlessWindowedGames;
            set => SetValue(ref _borderlessWindowedGames, value);
        }

        private bool _exclusiveFullscreenSupport = true;
        /// <summary>
        /// Open the overlay on the desktop (opaque backdrop) for games in true exclusive fullscreen,
        /// and restore the game when it closes. Default ON.
        /// </summary>
        public bool ExclusiveFullscreenSupport
        {
            get => _exclusiveFullscreenSupport;
            set
            {
                SetValue(ref _exclusiveFullscreenSupport, value);
                OnPropertyChanged(nameof(FullscreenGamesSupport));
            }
        }

        /// <summary>Combined UI setting for exclusive fullscreen overlay support and fullscreen optimizations.</summary>
        [JsonIgnore]
        public bool FullscreenGamesSupport
        {
            get => ExclusiveFullscreenSupport || FakeFullscreen;
            set
            {
                bool exclusiveChanged = _exclusiveFullscreenSupport != value;
                bool fakeChanged = _fakeFullscreen != value;
                if (!exclusiveChanged && !fakeChanged) return;

                _exclusiveFullscreenSupport = value;
                _fakeFullscreen = value;
                if (exclusiveChanged) OnPropertyChanged(nameof(ExclusiveFullscreenSupport));
                if (fakeChanged) OnPropertyChanged(nameof(FakeFullscreen));
                OnPropertyChanged(nameof(FullscreenGamesSupport));
            }
        }

        private bool _enableWindowsGameModeRegistration = true;
        public bool EnableWindowsGameModeRegistration
        {
            get => _enableWindowsGameModeRegistration;
            set => SetValue(ref _enableWindowsGameModeRegistration, value);
        }

        private bool _fpsAutoApplyOnGameStart = true;
        private double _fpsLimit = 0;
        private int _fpsSyncMode = 0; // OverlayFpsSyncMode as int for serialization
        private bool _fpsReflexMarkers = true;
        private int _fpsScanlineSyncTarget = -1;
        private string _rtssPath = "";

        public bool FpsAutoApplyOnGameStart
        {
            get => _fpsAutoApplyOnGameStart;
            set => SetValue(ref _fpsAutoApplyOnGameStart, value);
        }

        public double FpsLimit
        {
            get => _fpsLimit;
            set => SetValue(ref _fpsLimit, value);
        }

        public int FpsSyncMode
        {
            get => _fpsSyncMode;
            set => SetValue(ref _fpsSyncMode, value);
        }

        public bool FpsReflexMarkers
        {
            get => _fpsReflexMarkers;
            set => SetValue(ref _fpsReflexMarkers, value);
        }

        public int FpsScanlineSyncTarget
        {
            get => _fpsScanlineSyncTarget;
            set => SetValue(ref _fpsScanlineSyncTarget, Math.Max(-500, Math.Min(500, value)));
        }

        public string RtssPath
        {
            get => _rtssPath;
            set => SetValue(ref _rtssPath, value);
        }

        public ControllerShortcut ControllerShortcut
        {
            get => _controllerShortcut;
            set => SetValue(ref _controllerShortcut, value);
        }

        public CloseBehavior CloseBehavior
        {
            get => _closeBehavior;
            set => SetValue(ref _closeBehavior, value);
        }

        /// <summary>
        /// When enabled, Alt+F4 instantly force-kills the running game (SuperF4-style)
        /// and consumes the key so the game never gets a soft close dialog.
        /// When disabled, Alt+F4 is left alone (normal Windows close behavior).
        /// </summary>
        public bool EnableAltF4InstantClose
        {
            get => _enableAltF4InstantClose;
            set => SetValue(ref _enableAltF4InstantClose, value);
        }

        /// <summary>
        /// SKIF-style Instant Play for Steam games: skip the regular platform launch
        /// process (including cloud save synchronization) and start the game .exe directly.
        /// Steam overlay / Steam playtime may be reduced; Playnite session tracking for the
        /// cancelled Steam path is skipped (overlay still tracks the process we start).
        /// </summary>
        private bool _autoPcPlatformForNewGames = true;
        /// <summary>Games added by hand with no platform get PC (Windows).</summary>
        public bool AutoPcPlatformForNewGames
        {
            get => _autoPcPlatformForNewGames;
            set => SetValue(ref _autoPcPlatformForNewGames, value);
        }

        private bool _autoDownloadMetadataForCustomGames = true;
        /// <summary>Download metadata automatically when a custom (non-library) game is added.</summary>
        public bool AutoDownloadMetadataForCustomGames
        {
            get => _autoDownloadMetadataForCustomGames;
            set => SetValue(ref _autoDownloadMetadataForCustomGames, value);
        }

        private bool _showMetadataDownloadProgress = true;
        /// <summary>Show Playnite's progress dialog while downloading game metadata.</summary>
        public bool ShowMetadataDownloadProgress
        {
            get => _showMetadataDownloadProgress;
            set => SetValue(ref _showMetadataDownloadProgress, value);
        }

        private bool _localStoreEnabled;
        /// <summary>Local Games Store: scan a folder for games that do not come from an online storefront.</summary>
        public bool LocalStoreEnabled
        {
            get => _localStoreEnabled;
            set => SetValue(ref _localStoreEnabled, value);
        }

        private string _localGamesFolder = "";
        public string LocalGamesFolder
        {
            get => _localGamesFolder;
            set => SetValue(ref _localGamesFolder, value?.Trim() ?? "");
        }

        private bool _localStoreScanOnStartup;
        /// <summary>Scan the local games folder when Playnite starts.</summary>
        public bool LocalStoreScanOnStartup
        {
            get => _localStoreScanOnStartup;
            set => SetValue(ref _localStoreScanOnStartup, value);
        }

        private bool _localStoreScanOnLibraryUpdate;
        /// <summary>Scan the local games folder when Playnite refreshes libraries (F5).</summary>
        public bool LocalStoreScanOnLibraryUpdate
        {
            get => _localStoreScanOnLibraryUpdate;
            set => SetValue(ref _localStoreScanOnLibraryUpdate, value);
        }

        private bool _localStoreDownloadMetadata = true;
        /// <summary>Download metadata for games the Local Games Store adds.</summary>
        public bool LocalStoreDownloadMetadata
        {
            get => _localStoreDownloadMetadata;
            set => SetValue(ref _localStoreDownloadMetadata, value);
        }

        private bool _localStoreDownloadAchievements = true;
        /// <summary>
        /// Ask the Playnite Achievements extension to fetch achievements for new custom PC games: found by the
        /// Local Games Store scan or added by hand. (Name kept for saved settings.)
        /// </summary>
        public bool LocalStoreDownloadAchievements
        {
            get => _localStoreDownloadAchievements;
            set => SetValue(ref _localStoreDownloadAchievements, value);
        }

        private string _hotkeyScanLocalGames = "";
        /// <summary>Hotkey that scans the local games folder now (empty = none). Works while Playnite is in front.</summary>
        public string HotkeyScanLocalGames
        {
            get => _hotkeyScanLocalGames;
            set => SetValue(ref _hotkeyScanLocalGames, value?.Trim() ?? "");
        }

        private bool _autoAddSaveLinks = false;
        /// <summary>Add "Save location" and "Config location" folder links to PC games.</summary>
        public bool AutoAddSaveLinks
        {
            get => _autoAddSaveLinks;
            set => SetValue(ref _autoAddSaveLinks, value);
        }

        private bool _autoAddVortexLink = false;
        /// <summary>Add a "Vortex" link (opens Vortex) to PC games that Nexus Mods lists. Needs Vortex installed.</summary>
        public bool AutoAddVortexLink
        {
            get => _autoAddVortexLink;
            set => SetValue(ref _autoAddVortexLink, value);
        }

        private bool _autoAddGameBananaLink = false;
        /// <summary>Add GameBanana mod pages only for confidently matched games.</summary>
        public bool AutoAddGameBananaLink
        {
            get => _autoAddGameBananaLink;
            set => SetValue(ref _autoAddGameBananaLink, value);
        }

        private bool _autoAddScsKillerLink = false;
        /// <summary>Add the official SCSKiller project link to PC games (known stutter and Unreal Engine games are labelled "(recommended)").</summary>
        public bool AutoAddScsKillerLink
        {
            get => _autoAddScsKillerLink;
            set => SetValue(ref _autoAddScsKillerLink, value);
        }

        private bool _autoAddGameLinks = false;
        /// <summary>Add PCGamingWiki and Nexus Mods to each game's Links in Playnite.</summary>
        public bool AutoAddGameLinks
        {
            get => _autoAddGameLinks;
            set => SetValue(ref _autoAddGameLinks, value);
        }

        private bool _showSteamFriendsButton = true;
        /// <summary>Show a button in the overlay that returns to the running Playnite shell.</summary>
        public bool ShowPlayniteButton
        {
            get => _showPlayniteButton;
            set => SetValue(ref _showPlayniteButton, value);
        }

        /// <summary>Show a "Steam Friends" button in the overlay (only when Steam is installed).</summary>
        public bool ShowSteamFriendsButton
        {
            get => _showSteamFriendsButton;
            set => SetValue(ref _showSteamFriendsButton, value);
        }

        private bool _enableGogInstantPlay;
        private bool _enableEpicInstantPlay;
        private bool _enableUbisoftInstantPlay;
        private bool _enableXboxInstantPlay;

        /// <summary>Instant Play for Xbox / Game Pass PC (GDK) games installed outside WindowsApps. Default OFF.</summary>
        public bool EnableXboxInstantPlay
        {
            get => _enableXboxInstantPlay;
            set => SetValue(ref _enableXboxInstantPlay, value);
        }

        /// <summary>Instant Play for GOG games: start the game's program directly (skips GOG Galaxy). Default OFF.</summary>
        public bool EnableGogInstantPlay
        {
            get => _enableGogInstantPlay;
            set => SetValue(ref _enableGogInstantPlay, value);
        }

        /// <summary>Instant Play for Epic games (some need the launcher for login and will not start). Default OFF.</summary>
        public bool EnableEpicInstantPlay
        {
            get => _enableEpicInstantPlay;
            set => SetValue(ref _enableEpicInstantPlay, value);
        }

        /// <summary>Instant Play for Ubisoft Connect games. Default OFF.</summary>
        public bool EnableUbisoftInstantPlay
        {
            get => _enableUbisoftInstantPlay;
            set => SetValue(ref _enableUbisoftInstantPlay, value);
        }

        public bool EnableSteamInstantPlay
        {
            get => _enableSteamInstantPlay;
            set => SetValue(ref _enableSteamInstantPlay, value);
        }

        // Persisted internal migration marker; intentionally not exposed in UI.
        public bool InstantPlayRestoreApplied
        {
            get => _instantPlayRestoreApplied;
            set => SetValue(ref _instantPlayRestoreApplied, value);
        }

        /// <summary>
        /// Steam games that opt out of Instant Play while the global toggle is on.
        /// They keep the normal Steam library launch action.
        /// </summary>
        public List<Guid> InstantPlayDisabledGames
        {
            get => _instantPlayDisabledGames ?? (_instantPlayDisabledGames = new List<Guid>());
            set => SetValue(ref _instantPlayDisabledGames, value ?? new List<Guid>());
        }

        public bool IsInstantPlayDisabledFor(Guid gameId)
        {
            return InstantPlayDisabledGames != null && InstantPlayDisabledGames.Contains(gameId);
        }

        public void SetInstantPlayDisabledFor(Guid gameId, bool disabled)
        {
            if (gameId == Guid.Empty) return;
            var list = InstantPlayDisabledGames;
            if (disabled)
            {
                if (!list.Contains(gameId))
                    list.Add(gameId);
            }
            else
            {
                list.Remove(gameId);
            }
            OnPropertyChanged(nameof(InstantPlayDisabledGames));
        }

        /// <summary>
        /// Custom Play actions temporarily demoted while Instant Play is the sole
        /// primary Play action. They are restored if Instant Play is disabled.
        /// </summary>
        public List<SuppressedInstantPlayAction> SuppressedInstantPlayActions
        {
            get => _suppressedInstantPlayActions ??
                   (_suppressedInstantPlayActions = new List<SuppressedInstantPlayAction>());
            set => SetValue(ref _suppressedInstantPlayActions,
                value ?? new List<SuppressedInstantPlayAction>());
        }

        public bool HasSuppressedInstantPlayAction(Guid gameId, string actionKey)
        {
            return SuppressedInstantPlayActions.Any(x =>
                x != null &&
                x.GameId == gameId &&
                string.Equals(x.ActionKey, actionKey, StringComparison.Ordinal));
        }

        /// <summary>
        /// Alt+Shift+S toggles suspend + minimize (park) for the running game.
        /// Resume with the same hotkey or Return to game from the overlay.
        /// </summary>
        public bool EnableSuspendMinimize
        {
            get => _enableSuspendMinimize;
            set => SetValue(ref _enableSuspendMinimize, value);
        }

        /// <summary>
        /// Minimal Playnite Desktop navigation: D-pad/stick → arrows, A → Enter, B → Escape
        /// when Playnite.DesktopApp is focused (overlay closed).
        /// </summary>
        public bool EnableDesktopControllerNav
        {
            get => _enableDesktopControllerNav;
            set => SetValue(ref _enableDesktopControllerNav, value);
        }

        /// <summary>PlayState: subtract suspended (parked + overlay pause) and unfocused-window seconds from playtime on stop.</summary>
        public bool SubtractSuspendedFromPlaytime
        {
            get => _subtractSuspendedFromPlaytime;
            set => SetValue(ref _subtractSuspendedFromPlaytime, value);
        }

        /// <summary>Only apply playtime subtract for games not managed by a library plugin.</summary>
        public bool SubtractSuspendedOnlyNonLibrary
        {
            get => _subtractSuspendedOnlyNonLibrary;
            set => SetValue(ref _subtractSuspendedOnlyNonLibrary, value);
        }

        /// <summary>If session length is under MinSessionMinutes, exclude that session from playtime.</summary>
        public bool ExcludeShortSessions
        {
            get => _excludeShortSessions;
            set => SetValue(ref _excludeShortSessions, value);
        }

        public int MinSessionMinutes
        {
            get => _minSessionMinutes;
            set => SetValue(ref _minSessionMinutes, value < 0 ? 0 : value);
        }

        public string HotkeyOverlayToggle
        {
            get => _hotkeyOverlayToggle;
            set => SetValue(ref _hotkeyOverlayToggle, string.IsNullOrWhiteSpace(value) ? "Alt+Oem3" : value.Trim());
        }

        public string HotkeyForceClose
        {
            get => _hotkeyForceClose;
            set => SetValue(ref _hotkeyForceClose, string.IsNullOrWhiteSpace(value) ? "Alt+F4" : value.Trim());
        }

        public string HotkeySuspend
        {
            get => _hotkeySuspend;
            set => SetValue(ref _hotkeySuspend, string.IsNullOrWhiteSpace(value) ? "Pause" : value.Trim());
        }

        /// <summary>Global hotkey to raise the selected game-volume target while a game is running (empty = disabled).</summary>
        public string HotkeyVolumeUp
        {
            get => _hotkeyVolumeUp;
            set => SetValue(ref _hotkeyVolumeUp, value?.Trim() ?? "");
        }

        /// <summary>Global hotkey to lower the selected game-volume target while a game is running (empty = disabled).</summary>
        public string HotkeyVolumeDown
        {
            get => _hotkeyVolumeDown;
            set => SetValue(ref _hotkeyVolumeDown, value?.Trim() ?? "");
        }

        /// <summary>Global hotkey to toggle game mute (empty = disabled).</summary>
        public string HotkeyVolumeMute
        {
            get => _hotkeyVolumeMute;
            set => SetValue(ref _hotkeyVolumeMute, value?.Trim() ?? "");
        }

        /// <summary>While a game has focus, PrintScreen is replaced by PrintScreenRemapCombo (e.g. for AMD/NVIDIA screenshot hotkeys that can't bind PrintScreen).</summary>
        public bool EnablePrintScreenRemap
        {
            get => _enablePrintScreenRemap;
            set => SetValue(ref _enablePrintScreenRemap, value);
        }

        /// <summary>Controller combos like "Back+A" or "LB+RB+Y" (empty = none). Work while a game is focused.</summary>
        public string ControllerHotkeyOverlay
        {
            get => _controllerHotkeyOverlay;
            set => SetValue(ref _controllerHotkeyOverlay, value?.Trim() ?? "");
        }
        /// <summary>Sends the PrintScreen-remap combo (the key combo set for AMD/NVIDIA screenshots).</summary>
        public string ControllerHotkeyScreenshot
        {
            get => _controllerHotkeyScreenshot;
            set => SetValue(ref _controllerHotkeyScreenshot, value?.Trim() ?? "");
        }
        public string ControllerHotkeySuspend
        {
            get => _controllerHotkeySuspend;
            set => SetValue(ref _controllerHotkeySuspend, value?.Trim() ?? "");
        }
        public string ControllerHotkeyMute
        {
            get => _controllerHotkeyMute;
            set => SetValue(ref _controllerHotkeyMute, value?.Trim() ?? "");
        }

        /// <summary>SendKeys-style combo, e.g. ^+{F12} = Ctrl+Shift+F12 (^ Ctrl, + Shift, % Alt).</summary>
        public string PrintScreenRemapCombo
        {
            get => _printScreenRemapCombo;
            set => SetValue(ref _printScreenRemapCombo, string.IsNullOrWhiteSpace(value) ? "^+{F12}" : value.Trim());
        }

        /// <summary>Hotkey that opens the per-game settings window while Playnite is focused (empty = disabled).</summary>
        public string HotkeyGameSettings
        {
            get => _hotkeyGameSettings;
            set => SetValue(ref _hotkeyGameSettings, value?.Trim() ?? "");
        }

        /// <summary>Hotkey that opens this extension's global settings while Playnite is focused (empty = disabled).</summary>
        public string HotkeyGlobalSettings
        {
            get => _hotkeyGlobalSettings;
            set => SetValue(ref _hotkeyGlobalSettings, value?.Trim() ?? "");
        }

        /// <summary>Show the per-game settings icon in Playnite's top bar.</summary>
        public bool ShowTopBarGameSettings
        {
            get => _showTopBarGameSettings;
            set => SetValue(ref _showTopBarGameSettings, value);
        }

        /// <summary>Percent step for volume up/down hotkeys (default 10).</summary>
        public int VolumeHotkeyStep
        {
            get => _volumeHotkeyStep;
            set => SetValue(ref _volumeHotkeyStep, value < 1 ? 1 : (value > 100 ? 100 : value));
        }

        /// <summary>Volume target: system master endpoint or the current game's audio session.</summary>
        public string VolumeControlMode
        {
            get => _volumeControlMode;
            set => SetValue(ref _volumeControlMode,
                string.Equals(value, "PerGame", StringComparison.OrdinalIgnoreCase) ? "PerGame" : "Master");
        }

        public bool UsesPerGameVolume =>
            string.Equals(VolumeControlMode, "PerGame", StringComparison.OrdinalIgnoreCase);

        /// <summary>Accent color (focus rings, slider fill, highlights) as #RRGGBB.</summary>
        public string AccentColor
        {
            get => _accentColor;
            set => SetHexColor(ref _accentColor, value, nameof(AccentColor));
        }

        /// <summary>Panel background color as #RRGGBB.</summary>
        public string PanelBackgroundColor
        {
            get => _panelBackgroundColor;
            set => SetHexColor(ref _panelBackgroundColor, value, nameof(PanelBackgroundColor));
        }

        /// <summary>Panel opacity, 0.3 - 1.0.</summary>
        public double PanelOpacity
        {
            get => _panelOpacity;
            set => SetValue(ref _panelOpacity, Math.Max(0.3, Math.Min(1.0, value)));
        }

        /// <summary>Panel corner radius in pixels, 0 - 28.</summary>
        public double PanelCornerRadius
        {
            get => _panelCornerRadius;
            set => SetValue(ref _panelCornerRadius, Math.Max(0, Math.Min(28, Math.Round(value))));
        }

        /// <summary>Glass effect: hairline light border on panels and translucent control surfaces.</summary>
        public bool PanelGlassBorder
        {
            get => _panelGlassBorder;
            set => SetValue(ref _panelGlassBorder, value);
        }

        public bool CustomPanelGlassBorder
        {
            get => _customPanelGlassBorder;
            set => SetValue(ref _customPanelGlassBorder, value);
        }

        public double CustomPanelGlassStrength
        {
            get => _customPanelGlassStrength;
            set => SetValue(ref _customPanelGlassStrength, Math.Max(0, Math.Min(1, Math.Round(value * 100) / 100)));
        }

        /// <summary>How strong the panel glass effect is (tint, sheen, rim, translucent buttons), 0 - 1. Default 0.5.</summary>
        public double PanelGlassStrength
        {
            get => _panelGlassStrength;
            set => SetValue(ref _panelGlassStrength, Math.Max(0, Math.Min(1, Math.Round(value * 100) / 100)));
        }

        /// <summary>Glass effect for the global and per-game settings menus (next to "Settings theme").</summary>
        public bool SettingsMenuGlass
        {
            get => _settingsMenuGlass;
            set => SetValue(ref _settingsMenuGlass, value);
        }

        /// <summary>How much the game is dimmed behind the overlay, 0 - 0.85.</summary>
        public double BackdropDim
        {
            get => _backdropDim;
            set => SetValue(ref _backdropDim, Math.Max(0, Math.Min(0.85, value)));
        }

        /// <summary>When true, overlay accent tracks the current Windows accent color.</summary>
        public bool UseWindowsAccent
        {
            get => _useWindowsAccent;
            set => SetValue(ref _useWindowsAccent, value);
        }

        /// <summary>When true, overlay accent is sampled from the current game's cover.</summary>
        public bool UseCoverAccent
        {
            get => _useCoverAccent;
            set => SetValue(ref _useCoverAccent, value);
        }

        /// <summary>Saved Custom preset accent so switching presets does not lose user colors.</summary>
        public string CustomAccentColor
        {
            get => _customAccentColor;
            set => SetHexColor(ref _customAccentColor, value, nameof(CustomAccentColor));
        }

        public string CustomPanelBackgroundColor
        {
            get => _customPanelBackgroundColor;
            set => SetHexColor(ref _customPanelBackgroundColor, value, nameof(CustomPanelBackgroundColor));
        }

        public double CustomPanelOpacity
        {
            get => _customPanelOpacity;
            set => SetValue(ref _customPanelOpacity, Math.Max(0.3, Math.Min(1.0, value)));
        }

        public double CustomPanelCornerRadius
        {
            get => _customPanelCornerRadius;
            set => SetValue(ref _customPanelCornerRadius, Math.Max(0, Math.Min(28, Math.Round(value))));
        }

        public double CustomBackdropDim
        {
            get => _customBackdropDim;
            set => SetValue(ref _customBackdropDim, Math.Max(0, Math.Min(0.85, value)));
        }

        public bool CustomUseCoverAccent
        {
            get => _customUseCoverAccent;
            set => SetValue(ref _customUseCoverAccent, value);
        }

        /// <summary>Copy current live appearance into the Custom snapshot.</summary>
        public void CaptureCustomAppearance()
        {
            _customAccentColor = _accentColor;
            _customPanelBackgroundColor = _panelBackgroundColor;
            _customPanelOpacity = _panelOpacity;
            _customPanelCornerRadius = _panelCornerRadius;
            _customPanelGlassBorder = _panelGlassBorder;
            _customPanelGlassStrength = _panelGlassStrength;
            _customBackdropDim = _backdropDim;
            _customUseCoverAccent = _useCoverAccent;
        }

        /// <summary>Apply the saved Custom snapshot to the live appearance fields.</summary>
        public void RestoreCustomAppearance()
        {
            UseWindowsAccent = false;
            UseCoverAccent = _customUseCoverAccent;
            AccentColor = string.IsNullOrWhiteSpace(_customAccentColor) ? "#66C0F4" : _customAccentColor;
            PanelBackgroundColor = string.IsNullOrWhiteSpace(_customPanelBackgroundColor) ? "#1A1D23" : _customPanelBackgroundColor;
            PanelOpacity = _customPanelOpacity;
            PanelCornerRadius = _customPanelCornerRadius;
            PanelGlassBorder = _customPanelGlassBorder;
            PanelGlassStrength = _customPanelGlassStrength;
            BackdropDim = _customBackdropDim;
        }

        private void SetHexColor(ref string field, string value, string propertyName)
        {
            string normalized = NormalizeHexColor(value);
            if (normalized == null)
            {
                // Not a valid #RRGGBB: keep the old value and make the bound text box snap back.
                OnPropertyChanged(propertyName);
                return;
            }
            SetValue(ref field, normalized, propertyName);
        }

        internal static string NormalizeHexColor(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string v = value.Trim().TrimStart('#');
            if (v.Length != 6) return null;
            foreach (char ch in v)
                if (!Uri.IsHexDigit(ch)) return null;
            return "#" + v.ToUpperInvariant();
        }

        public bool DebugMode
        {
            get => _debugMode;
            set => SetValue(ref _debugMode, value);
        }

        public AspectRatio AspectRatio
        {
            get => _aspectRatio;
            set => SetValue(ref _aspectRatio, value);
        }

        // Properties for toggle options
        public bool ShowRecordGameplay
        {
            get => _showRecordGameplay;
            set => SetValue(ref _showRecordGameplay, value);
        }

        public bool ShowRecordRecent
        {
            get => _showRecordRecent;
            set => SetValue(ref _showRecordRecent, value);
        }

        public bool ShowStreaming
        {
            get => _showStreaming;
            set => SetValue(ref _showStreaming, value);
        }

        public bool ShowPerformanceOverlay
        {
            get => _showPerformanceOverlay;
            set => SetValue(ref _showPerformanceOverlay, value);
        }

        public bool ShowScreenshotGallery
        {
            get => _showScreenshotGallery;
            set => SetValue(ref _showScreenshotGallery, value);
        }

        public bool ShowWebBrowser
        {
            get => _showWebBrowser;
            set => SetValue(ref _showWebBrowser, value);
        }

        public bool ShowDiscord
        {
            get => _showDiscord;
            set => SetValue(ref _showDiscord, value);
        }

        public bool ShowBattery
        {
            get => _showBattery;
            set => SetValue(ref _showBattery, value);
        }

        /// <summary>True when the FPS Limiter extension is installed. Used to grey out the panel option.</summary>
        [JsonIgnore]
        public bool FpsLimiterInstalled
        {
            get
            {
                try { return plugin?.PlayniteApi == null || FpsLimiterSettingsSync.IsFpsLimiterInstalled(plugin.PlayniteApi); }
                catch { return true; }
            }
        }

        public bool ShowFpsLimiterPanel
        {
            get => _showFpsLimiterPanel;
            set => SetValue(ref _showFpsLimiterPanel, value);
        }

        public bool ShowGameVolume
        {
            get => _showGameVolume;
            set => SetValue(ref _showGameVolume, value);
        }

        /// <summary>Show the gamepad button prompt bar (D-Pad, LB/RB, LT/RT, A, B) in the overlay.</summary>
        public bool ShowGamepadHints
        {
            get => _showGamepadHints;
            set => SetValue(ref _showGamepadHints, value);
        }

        /// <summary>Glyph set for the button prompts and shortcut labels: Auto, Xbox or DualShock.</summary>
        public ButtonPromptStyle ButtonPromptStyle
        {
            get => _buttonPromptStyle;
            set => SetValue(ref _buttonPromptStyle, value);
        }

        /// <summary>Hide mouse cursor when gamepad is used; show again on mouse move.</summary>
        public bool HideCursorOnGamepad
        {
            get => _hideCursorOnGamepad;
            set => SetValue(ref _hideCursorOnGamepad, value);
        }


        /// <summary>Suspend (freeze) the game process while the overlay is open. On by default.</summary>
        public bool PauseGameWhileOverlayOpen
        {
            get => _pauseGameWhileOverlayOpen;
            set => SetValue(ref _pauseGameWhileOverlayOpen, value);
        }

        public double FpsPanelOffsetX
        {
            get => _fpsPanelOffsetX;
            set => SetValue(ref _fpsPanelOffsetX, value);
        }

        public double FpsPanelOffsetY
        {
            get => _fpsPanelOffsetY;
            set => SetValue(ref _fpsPanelOffsetY, value);
        }

        public double AudioPanelOffsetX
        {
            get => _audioPanelOffsetX;
            set => SetValue(ref _audioPanelOffsetX, value);
        }

        public double AudioPanelOffsetY
        {
            get => _audioPanelOffsetY;
            set => SetValue(ref _audioPanelOffsetY, value);
        }

        /// <summary>Last focused panel key: playnite | achievements | volume | fps</summary>
        public string LastFocusedPanel
        {
            get => _lastFocusedPanel;
            set => SetValue(ref _lastFocusedPanel, value ?? "playnite");
        }

        // Properties for shortcuts and paths
        public string RecordGameplayShortcut
        {
            get => _recordGameplayShortcut;
            set => SetValue(ref _recordGameplayShortcut, value);
        }

        public string RecordRecentShortcut
        {
            get => _recordRecentShortcut;
            set => SetValue(ref _recordRecentShortcut, value);
        }

        public string StreamingShortcut
        {
            get => _streamingShortcut;
            set => SetValue(ref _streamingShortcut, value);
        }

        public string PerformanceOverlayShortcut
        {
            get => _performanceOverlayShortcut;
            set => SetValue(ref _performanceOverlayShortcut, value);
        }

        public string ScreenshotGalleryPath
        {
            get => _screenshotGalleryPath;
            set => SetValue(ref _screenshotGalleryPath, value);
        }

        public string WebBrowserPath
        {
            get => _webBrowserPath;
            set => SetValue(ref _webBrowserPath, value);
        }

        // Backup values for cancel operation
        private ControllerShortcut _controllerShortcutBackup;
        private CloseBehavior _closeBehaviorBackup;
        private bool _enableAltF4InstantCloseBackup;
        private string _accentColorBackup;
        private string _panelBackgroundColorBackup;
        private double _panelOpacityBackup;
        private double _panelCornerRadiusBackup;
        private bool _panelGlassBorderBackup;
        private bool _customPanelGlassBorderBackup;
        private bool _settingsMenuGlassBackup;
        private double _panelGlassStrengthBackup;
        private double _customPanelGlassStrengthBackup;
        private double _backdropDimBackup;
        private bool _useWindowsAccentBackup;
        private bool _useCoverAccentBackup;
        private bool _customUseCoverAccentBackup;
        private string _customAccentColorBackup;
        private string _customPanelBackgroundColorBackup;
        private double _customPanelOpacityBackup;
        private double _customPanelCornerRadiusBackup;
        private double _customBackdropDimBackup;
        private List<OverlayAppearancePreset> _savedAppearancePresetsBackup;
        private string _achievementStyleBackup;
        private bool _enableSteamInstantPlayBackup;
        private bool _enableGogInstantPlayBackup;
        private bool _showPlayniteButtonBackup;
        private bool _showSteamFriendsButtonBackup;
        private bool _autoAddGameLinksBackup;
        private bool _autoAddScsKillerLinkBackup;
        private bool _autoAddGameBananaLinkBackup;
        private bool _autoAddVortexLinkBackup;
        private bool _autoAddSaveLinksBackup;
        private bool _autoPcPlatformForNewGamesBackup;
        private bool _autoDownloadMetadataForCustomGamesBackup;
        private bool _showMetadataDownloadProgressBackup;
        private bool _localStoreEnabledBackup;
        private string _localGamesFolderBackup;
        private bool _localStoreScanOnStartupBackup;
        private bool _localStoreScanOnLibraryUpdateBackup;
        private bool _localStoreDownloadMetadataBackup;
        private bool _localStoreDownloadAchievementsBackup;
        private string _hotkeyScanLocalGamesBackup;
        private bool _enableEpicInstantPlayBackup;
        private bool _enableUbisoftInstantPlayBackup;
        private bool _enableXboxInstantPlayBackup;
        private List<Guid> _instantPlayDisabledGamesBackup;
        private bool _enableSuspendMinimizeBackup;
        private bool _enableDesktopControllerNavBackup;
        private bool _exclusiveFullscreenSupportBackup;
        private bool _borderlessWindowedGamesBackup;
        private bool _fakeFullscreenBackup;
        private bool _enableBreadcrumbLogBackup;
        private bool _useGameBackgroundBackdropBackup;
        private double _backdropBlurBackup;
        private string _backdropImageModeBackup;
        private double _backdropImageScaleBackup;
        private bool _subtractSuspendedFromPlaytimeBackup;
        private bool _subtractSuspendedOnlyNonLibraryBackup;
        private bool _excludeShortSessionsBackup;
        private int _minSessionMinutesBackup;
        private string _hotkeyOverlayToggleBackup;
        private string _hotkeyForceCloseBackup;
        private string _hotkeySuspendBackup;
        private string _hotkeyVolumeUpBackup;
        private string _hotkeyVolumeDownBackup;
        private string _hotkeyVolumeMuteBackup;
        private bool _enablePrintScreenRemapBackup;
        private string _controllerHotkeyOverlayBackup, _controllerHotkeyScreenshotBackup,
                       _controllerHotkeySuspendBackup, _controllerHotkeyMuteBackup;
        private string _printScreenRemapComboBackup;
        private string _volumeControlModeBackup;
        private string _hotkeyGameSettingsBackup;
        private string _hotkeyGlobalSettingsBackup;
        private bool _showTopBarGameSettingsBackup;
        private int _volumeHotkeyStepBackup;
        private bool _debugModeBackup;
        private bool _showRecordGameplayBackup;
        private bool _showRecordRecentBackup;
        private bool _showStreamingBackup;
        private bool _showPerformanceOverlayBackup;
        private bool _showScreenshotGalleryBackup;
        private bool _showWebBrowserBackup;
        private bool _showDiscordBackup;
        private bool _showBatteryBackup;
        private bool _showFpsLimiterPanelBackup;
        private bool _showGameVolumeBackup;
        private bool _showGamepadHintsBackup;
        private ButtonPromptStyle _buttonPromptStyleBackup;
        private string _recordGameplayShortcutBackup;
        private string _recordRecentShortcutBackup;
        private string _streamingShortcutBackup;
        private string _performanceOverlayShortcutBackup;
        private string _screenshotGalleryPathBackup;
        private string _webBrowserPathBackup;

        // Parameterless constructor needed for serialization
        public OverlaySettings()
        {
        }

        // Main constructor that loads saved settings
        public OverlaySettings(PlayniteGameOverlay plugin)
        {
            this.plugin = plugin;

            // Load saved settings
            var savedSettings = plugin.LoadPluginSettings<OverlaySettings>();
            if (savedSettings != null)
            {
                if (savedSettings.ControllerShortcut != null)
                    ControllerShortcut = savedSettings.ControllerShortcut;
                if (savedSettings.CloseBehavior != null)
                    CloseBehavior = savedSettings.CloseBehavior;
                EnableAltF4InstantClose = savedSettings.EnableAltF4InstantClose;
                EnableSteamInstantPlay = savedSettings.EnableSteamInstantPlay;
                EnableGogInstantPlay = savedSettings.EnableGogInstantPlay;
                ShowPlayniteButton = savedSettings.ShowPlayniteButton;
                ShowSteamFriendsButton = savedSettings.ShowSteamFriendsButton;
                AutoAddGameLinks = savedSettings.AutoAddGameLinks;
                AutoAddScsKillerLink = savedSettings.AutoAddScsKillerLink;
                AutoAddGameBananaLink = savedSettings.AutoAddGameBananaLink;
                AutoAddVortexLink = savedSettings.AutoAddVortexLink;
                AutoAddSaveLinks = savedSettings.AutoAddSaveLinks;
                AutoPcPlatformForNewGames = savedSettings.AutoPcPlatformForNewGames;
                AutoDownloadMetadataForCustomGames = savedSettings.AutoDownloadMetadataForCustomGames;
                ShowMetadataDownloadProgress = savedSettings.ShowMetadataDownloadProgress;
                LocalStoreEnabled = savedSettings.LocalStoreEnabled;
                LocalGamesFolder = savedSettings.LocalGamesFolder;
                LocalStoreScanOnStartup = savedSettings.LocalStoreScanOnStartup;
                LocalStoreScanOnLibraryUpdate = savedSettings.LocalStoreScanOnLibraryUpdate;
                LocalStoreDownloadMetadata = savedSettings.LocalStoreDownloadMetadata;
                LocalStoreDownloadAchievements = savedSettings.LocalStoreDownloadAchievements;
                HotkeyScanLocalGames = savedSettings.HotkeyScanLocalGames;
                EnableEpicInstantPlay = savedSettings.EnableEpicInstantPlay;
                EnableUbisoftInstantPlay = savedSettings.EnableUbisoftInstantPlay;
                EnableXboxInstantPlay = savedSettings.EnableXboxInstantPlay;
                InstantPlayRestoreApplied = savedSettings.InstantPlayRestoreApplied;
                if (savedSettings.InstantPlayDisabledGames != null)
                    InstantPlayDisabledGames = new List<Guid>(savedSettings.InstantPlayDisabledGames);
                if (savedSettings.SuppressedInstantPlayActions != null)
                    SuppressedInstantPlayActions = new List<SuppressedInstantPlayAction>(savedSettings.SuppressedInstantPlayActions);
                // New setting: default ON when missing from older config files
                try
                {
                    var cfgPath = System.IO.Path.Combine(plugin.GetPluginUserDataPath(), "config.json");
                    if (System.IO.File.Exists(cfgPath))
                    {
                        var json = System.IO.File.ReadAllText(cfgPath);
                        if (json.IndexOf("EnableSuspendMinimize", System.StringComparison.OrdinalIgnoreCase) >= 0)
                            EnableSuspendMinimize = savedSettings.EnableSuspendMinimize;
                        else
                            EnableSuspendMinimize = true;
                    }
                    else
                        EnableSuspendMinimize = true;
                }
                catch { EnableSuspendMinimize = true; }
                try
                {
                    // Default ON when missing from older configs
                    var cfgPath2 = System.IO.Path.Combine(plugin.GetPluginUserDataPath(), "config.json");
                    if (System.IO.File.Exists(cfgPath2))
                    {
                        var json2 = System.IO.File.ReadAllText(cfgPath2);
                        if (json2.IndexOf("EnableDesktopControllerNav", System.StringComparison.OrdinalIgnoreCase) >= 0)
                            EnableDesktopControllerNav = savedSettings.EnableDesktopControllerNav;
                        else
                            EnableDesktopControllerNav = true;
                    }
                    else
                        EnableDesktopControllerNav = true;
                }
                catch { EnableDesktopControllerNav = true; }
                // Exclusive-fullscreen overlay support is always enabled; ignore any old saved opt-out.
                ExclusiveFullscreenSupport = true;
                BorderlessWindowedGames = savedSettings.BorderlessWindowedGames;
                FakeFullscreen = savedSettings.FakeFullscreen;
                try
                {
                    // Default ON / 30 when missing from older configs
                    var cfgPath4 = System.IO.Path.Combine(plugin.GetPluginUserDataPath(), "config.json");
                    string json4 = System.IO.File.Exists(cfgPath4) ? System.IO.File.ReadAllText(cfgPath4) : "";
                    UseGameBackgroundBackdrop = json4.IndexOf("UseGameBackgroundBackdrop", System.StringComparison.OrdinalIgnoreCase) >= 0
                        ? savedSettings.UseGameBackgroundBackdrop : true;
                    BackdropBlur = json4.IndexOf("BackdropBlur", System.StringComparison.OrdinalIgnoreCase) >= 0
                        ? savedSettings.BackdropBlur : 30;
                    EnableBreadcrumbLog = json4.IndexOf("EnableBreadcrumbLog", System.StringComparison.OrdinalIgnoreCase) >= 0
                        ? savedSettings.EnableBreadcrumbLog : true;
                    BackdropImageMode = json4.IndexOf("BackdropImageMode", System.StringComparison.OrdinalIgnoreCase) >= 0
                        ? savedSettings.BackdropImageMode : "Auto";
                    BackdropImageScale = json4.IndexOf("BackdropImageScale", System.StringComparison.OrdinalIgnoreCase) >= 0
                        ? savedSettings.BackdropImageScale : 100;
                }
                catch { EnableBreadcrumbLog = true; UseGameBackgroundBackdrop = true; BackdropBlur = 30; BackdropImageMode = "Auto"; BackdropImageScale = 100; }
                SubtractSuspendedFromPlaytime = savedSettings.SubtractSuspendedFromPlaytime;
                SubtractSuspendedOnlyNonLibrary = savedSettings.SubtractSuspendedOnlyNonLibrary;
                ExcludeShortSessions = savedSettings.ExcludeShortSessions;
                if (savedSettings.MinSessionMinutes > 0)
                    MinSessionMinutes = savedSettings.MinSessionMinutes;
                if (!string.IsNullOrWhiteSpace(savedSettings.HotkeyOverlayToggle))
                    HotkeyOverlayToggle = savedSettings.HotkeyOverlayToggle;
                if (!string.IsNullOrWhiteSpace(savedSettings.HotkeyForceClose))
                    HotkeyForceClose = savedSettings.HotkeyForceClose;
                if (!string.IsNullOrWhiteSpace(savedSettings.HotkeySuspend))
                    HotkeySuspend = savedSettings.HotkeySuspend;
                if (savedSettings.HotkeyVolumeUp != null)
                    HotkeyVolumeUp = savedSettings.HotkeyVolumeUp ?? "";
                if (savedSettings.HotkeyVolumeDown != null)
                    HotkeyVolumeDown = savedSettings.HotkeyVolumeDown ?? "";
                if (savedSettings.HotkeyVolumeMute != null)
                    HotkeyVolumeMute = savedSettings.HotkeyVolumeMute ?? "";
                EnablePrintScreenRemap = savedSettings.EnablePrintScreenRemap;
                PrintScreenRemapCombo = savedSettings.PrintScreenRemapCombo;
                ControllerHotkeyOverlay = savedSettings.ControllerHotkeyOverlay;
                ControllerHotkeyScreenshot = savedSettings.ControllerHotkeyScreenshot;
                ControllerHotkeySuspend = savedSettings.ControllerHotkeySuspend;
                ControllerHotkeyMute = savedSettings.ControllerHotkeyMute;
                HotkeysLayout_Load(savedSettings); // hotkeys-layout
                UpdateChecks_Load(savedSettings); // update-checks
                if (savedSettings.HotkeyGameSettings != null)
                    HotkeyGameSettings = savedSettings.HotkeyGameSettings;
                if (savedSettings.HotkeyGlobalSettings != null)
                    HotkeyGlobalSettings = savedSettings.HotkeyGlobalSettings;
                ShowTopBarGameSettings = savedSettings.ShowTopBarGameSettings;
                if (savedSettings.VolumeHotkeyStep > 0)
                    VolumeHotkeyStep = savedSettings.VolumeHotkeyStep;
                if (savedSettings.VolumeControlMode != null)
                    VolumeControlMode = savedSettings.VolumeControlMode;
                if (savedSettings.DebugMode != null)
                    DebugMode = savedSettings.DebugMode;
                // Appearance (missing keys keep their defaults)
                SettingsThemePreset = savedSettings.SettingsThemePreset;
                AccentColor = savedSettings.AccentColor;
                PanelBackgroundColor = savedSettings.PanelBackgroundColor;
                PanelOpacity = savedSettings.PanelOpacity;
                PanelCornerRadius = savedSettings.PanelCornerRadius;
                PanelGlassBorder = savedSettings.PanelGlassBorder;
                SettingsMenuGlass = savedSettings.SettingsMenuGlass;
                PanelGlassStrength = savedSettings.PanelGlassStrength;
                BackdropDim = savedSettings.BackdropDim;
                UseWindowsAccent = savedSettings.UseWindowsAccent;
                UseCoverAccent = savedSettings.UseCoverAccent;
                if (!string.IsNullOrWhiteSpace(savedSettings.CustomAccentColor))
                    CustomAccentColor = savedSettings.CustomAccentColor;
                if (!string.IsNullOrWhiteSpace(savedSettings.CustomPanelBackgroundColor))
                    CustomPanelBackgroundColor = savedSettings.CustomPanelBackgroundColor;
                if (savedSettings.CustomPanelOpacity > 0)
                    CustomPanelOpacity = savedSettings.CustomPanelOpacity;
                CustomPanelCornerRadius = savedSettings.CustomPanelCornerRadius;
                CustomPanelGlassBorder = savedSettings.CustomPanelGlassBorder;
                CustomPanelGlassStrength = savedSettings.CustomPanelGlassStrength;
                CustomBackdropDim = savedSettings.CustomBackdropDim;
                CustomUseCoverAccent = savedSettings.CustomUseCoverAccent;
                if (savedSettings.SavedAppearancePresets != null)
                    SavedAppearancePresets = savedSettings.SavedAppearancePresets
                        .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Name))
                        .Select(p => p.Copy()).ToList();
                AchievementStyle = savedSettings.AchievementStyle;
                if (savedSettings.AspectRatio != null)
                    AspectRatio = savedSettings.AspectRatio;

                // Load toggle settings
                if (savedSettings.ShowRecordGameplay != null && savedSettings.RecordGameplayShortcut != null)
                {
                    ShowRecordGameplay = savedSettings.ShowRecordGameplay;
                    RecordGameplayShortcut = savedSettings.RecordGameplayShortcut;
                }
                else
                {
                    ShowRecordGameplay = false;
                }
                if (savedSettings.ShowRecordRecent != null && savedSettings.RecordRecentShortcut != null)
                {
                    ShowRecordRecent = savedSettings.ShowRecordRecent;
                    RecordRecentShortcut = savedSettings.RecordRecentShortcut;
                }
                else
                {
                    ShowRecordRecent = false;
                }
                if (savedSettings.ShowStreaming != null && savedSettings.StreamingShortcut != null)
                {
                    ShowStreaming = savedSettings.ShowStreaming;
                    StreamingShortcut = savedSettings.StreamingShortcut;
                }
                else
                {
                    ShowStreaming = false;
                }
                if (savedSettings.ShowPerformanceOverlay != null && savedSettings.PerformanceOverlayShortcut != null)
                {
                    ShowPerformanceOverlay = savedSettings.ShowPerformanceOverlay;
                    PerformanceOverlayShortcut = savedSettings.PerformanceOverlayShortcut;
                }
                else
                {
                    ShowPerformanceOverlay = false;
                }
                if (savedSettings.ShowScreenshotGallery != null && savedSettings.ScreenshotGalleryPath != null)
                {
                    ShowScreenshotGallery = savedSettings.ShowScreenshotGallery;
                    ScreenshotGalleryPath = savedSettings.ScreenshotGalleryPath;
                }
                else
                {
                    ShowScreenshotGallery = false;
                }
                if (savedSettings.ShowWebBrowser != null && savedSettings.WebBrowserPath != null)
                {
                    ShowWebBrowser = savedSettings.ShowWebBrowser;
                    WebBrowserPath = savedSettings.WebBrowserPath;

                // Integrated FPS Limiter settings
                FpsAutoApplyOnGameStart = savedSettings.FpsAutoApplyOnGameStart;
                FpsLimit = savedSettings.FpsLimit;
                FpsSyncMode = savedSettings.FpsSyncMode;
                FpsReflexMarkers = savedSettings.FpsReflexMarkers;
                FpsScanlineSyncTarget = savedSettings.FpsScanlineSyncTarget;
                if (!string.IsNullOrEmpty(savedSettings.RtssPath))
                    RtssPath = savedSettings.RtssPath;
                }
                else
                {
                    ShowWebBrowser = false;
                }
                if (savedSettings.ShowBattery != null) ShowBattery = savedSettings.ShowBattery;
                if (savedSettings.ShowFpsLimiterPanel != null) ShowFpsLimiterPanel = savedSettings.ShowFpsLimiterPanel;
                if (savedSettings.ShowGameVolume != null) ShowGameVolume = savedSettings.ShowGameVolume;
                // Missing in older configs -> keeps the default (on).
                ShowGamepadHints = savedSettings.ShowGamepadHints;
                ButtonPromptStyle = savedSettings.ButtonPromptStyle;   // missing in older configs -> Auto
                if (savedSettings.ShowDiscord != null) ShowDiscord = savedSettings.ShowDiscord;

                // New setting defaults ON. JSON bool missing deserializes as false — detect explicit off only.
                HideCursorOnGamepad = true;
                try
                {
                    var dataPath = plugin?.PlayniteApi?.Paths?.ExtensionsDataPath;
                    if (!string.IsNullOrEmpty(dataPath) && plugin?.Id != null)
                    {
                        var cfg = System.IO.Path.Combine(dataPath, plugin.Id.ToString(), "config.json");
                        if (System.IO.File.Exists(cfg))
                        {
                            var json = System.IO.File.ReadAllText(cfg);
                            if (json.IndexOf("\"HideCursorOnGamepad\": false", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                json.IndexOf("\"HideCursorOnGamepad\":false", StringComparison.OrdinalIgnoreCase) >= 0)
                                HideCursorOnGamepad = false;
                        }
                    }
                }
                catch { HideCursorOnGamepad = true; }

                // Default ON; only an explicitly saved "false" turns it off.
                PauseGameWhileOverlayOpen = true;
                try
                {
                    var dataPath = plugin?.PlayniteApi?.Paths?.ExtensionsDataPath;
                    if (!string.IsNullOrEmpty(dataPath) && plugin?.Id != null)
                    {
                        var cfg = System.IO.Path.Combine(dataPath, plugin.Id.ToString(), "config.json");
                        if (System.IO.File.Exists(cfg))
                        {
                            var json = System.IO.File.ReadAllText(cfg);
                            if (json.IndexOf("\"PauseGameWhileOverlayOpen\": false", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                json.IndexOf("\"PauseGameWhileOverlayOpen\":false", StringComparison.OrdinalIgnoreCase) >= 0)
                                PauseGameWhileOverlayOpen = false;
                        }
                    }
                }
                catch { PauseGameWhileOverlayOpen = true; }

                FpsPanelOffsetX = savedSettings.FpsPanelOffsetX;
                FpsPanelOffsetY = savedSettings.FpsPanelOffsetY;
                AudioPanelOffsetX = savedSettings.AudioPanelOffsetX;
                AudioPanelOffsetY = savedSettings.AudioPanelOffsetY;
                // Keep installations on the previous untouched defaults aligned
                // after moving the volume panel 5 design pixels left.
                if (FpsPanelOffsetX == DefaultFpsPanelOffsetX &&
                    FpsPanelOffsetY == DefaultFpsPanelOffsetY &&
                    AudioPanelOffsetX == 638 &&
                    AudioPanelOffsetY == DefaultAudioPanelOffsetY)
                    AudioPanelOffsetX = DefaultAudioPanelOffsetX;
                // Older configs saved (0,0) = "never moved": give those the new default layout once.
                PanelLayoutInitialized = savedSettings.PanelLayoutInitialized;
                if (!PanelLayoutInitialized)
                {
                    if (FpsPanelOffsetX == 0 && FpsPanelOffsetY == 0 && AudioPanelOffsetX == 0 && AudioPanelOffsetY == 0)
                        ResetPanelLayout();
                    PanelLayoutInitialized = true;
                }
                if (!string.IsNullOrEmpty(savedSettings.LastFocusedPanel))
                    LastFocusedPanel = savedSettings.LastFocusedPanel;
            }
        }

        public void BeginEdit()
        {
            // Backup current values in case user cancels
            _controllerShortcutBackup = ControllerShortcut;
            _debugModeBackup = DebugMode;
            _settingsThemePresetBackup = SettingsThemePreset;
            _accentColorBackup = AccentColor;
            _panelBackgroundColorBackup = PanelBackgroundColor;
            _panelOpacityBackup = PanelOpacity;
            _panelCornerRadiusBackup = PanelCornerRadius;
            _panelGlassBorderBackup = PanelGlassBorder;
            _settingsMenuGlassBackup = SettingsMenuGlass;
            _panelGlassStrengthBackup = PanelGlassStrength;
            _backdropDimBackup = BackdropDim;
            _useWindowsAccentBackup = UseWindowsAccent;
            _useCoverAccentBackup = UseCoverAccent;
            _customUseCoverAccentBackup = CustomUseCoverAccent;
            _customAccentColorBackup = CustomAccentColor;
            _customPanelBackgroundColorBackup = CustomPanelBackgroundColor;
            _customPanelOpacityBackup = CustomPanelOpacity;
            _customPanelCornerRadiusBackup = CustomPanelCornerRadius;
            _customPanelGlassBorderBackup = CustomPanelGlassBorder;
            _customPanelGlassStrengthBackup = CustomPanelGlassStrength;
            _customBackdropDimBackup = CustomBackdropDim;
            _savedAppearancePresetsBackup = SavedAppearancePresets?.Select(p => p?.Copy()).Where(p => p != null).ToList();
            _achievementStyleBackup = AchievementStyle;
            _closeBehaviorBackup = CloseBehavior;
            _enableAltF4InstantCloseBackup = EnableAltF4InstantClose;
            _enableSteamInstantPlayBackup = EnableSteamInstantPlay;
            _enableGogInstantPlayBackup = EnableGogInstantPlay;
            _showPlayniteButtonBackup = ShowPlayniteButton;
            _showSteamFriendsButtonBackup = ShowSteamFriendsButton;
            _autoAddGameLinksBackup = AutoAddGameLinks;
            _autoAddScsKillerLinkBackup = AutoAddScsKillerLink;
            _autoAddGameBananaLinkBackup = AutoAddGameBananaLink;
            _autoAddVortexLinkBackup = AutoAddVortexLink;
            _autoAddSaveLinksBackup = AutoAddSaveLinks;
            _autoPcPlatformForNewGamesBackup = AutoPcPlatformForNewGames;
            _autoDownloadMetadataForCustomGamesBackup = AutoDownloadMetadataForCustomGames;
            _showMetadataDownloadProgressBackup = ShowMetadataDownloadProgress;
            _localStoreEnabledBackup = LocalStoreEnabled;
            _localGamesFolderBackup = LocalGamesFolder;
            _localStoreScanOnStartupBackup = LocalStoreScanOnStartup;
            _localStoreScanOnLibraryUpdateBackup = LocalStoreScanOnLibraryUpdate;
            _localStoreDownloadMetadataBackup = LocalStoreDownloadMetadata;
            _localStoreDownloadAchievementsBackup = LocalStoreDownloadAchievements;
            _hotkeyScanLocalGamesBackup = HotkeyScanLocalGames;
            _enableEpicInstantPlayBackup = EnableEpicInstantPlay;
            _enableUbisoftInstantPlayBackup = EnableUbisoftInstantPlay;
            _enableXboxInstantPlayBackup = EnableXboxInstantPlay;
            _instantPlayDisabledGamesBackup = InstantPlayDisabledGames != null ? new List<Guid>(InstantPlayDisabledGames) : new List<Guid>();
            _enableSuspendMinimizeBackup = EnableSuspendMinimize;
            _enableDesktopControllerNavBackup = EnableDesktopControllerNav;
            _exclusiveFullscreenSupportBackup = ExclusiveFullscreenSupport;
            _borderlessWindowedGamesBackup = BorderlessWindowedGames;
            _fakeFullscreenBackup = FakeFullscreen;
            _enableBreadcrumbLogBackup = EnableBreadcrumbLog;
            _useGameBackgroundBackdropBackup = UseGameBackgroundBackdrop;
            _backdropBlurBackup = BackdropBlur;
            _backdropImageModeBackup = BackdropImageMode;
            _backdropImageScaleBackup = BackdropImageScale;
            _subtractSuspendedFromPlaytimeBackup = SubtractSuspendedFromPlaytime;
            _subtractSuspendedOnlyNonLibraryBackup = SubtractSuspendedOnlyNonLibrary;
            _excludeShortSessionsBackup = ExcludeShortSessions;
            _minSessionMinutesBackup = MinSessionMinutes;
            _hotkeyOverlayToggleBackup = HotkeyOverlayToggle;
            _hotkeyForceCloseBackup = HotkeyForceClose;
            _hotkeySuspendBackup = HotkeySuspend;
            _hotkeyVolumeUpBackup = HotkeyVolumeUp;
            _hotkeyVolumeDownBackup = HotkeyVolumeDown;
            _hotkeyVolumeMuteBackup = HotkeyVolumeMute;
            _enablePrintScreenRemapBackup = EnablePrintScreenRemap;
            _printScreenRemapComboBackup = PrintScreenRemapCombo;
            _controllerHotkeyOverlayBackup = ControllerHotkeyOverlay;
            _controllerHotkeyScreenshotBackup = ControllerHotkeyScreenshot;
            _controllerHotkeySuspendBackup = ControllerHotkeySuspend;
            _controllerHotkeyMuteBackup = ControllerHotkeyMute;
            HotkeysLayout_Backup(); // hotkeys-layout
            UpdateChecks_Backup(); // update-checks
            _volumeControlModeBackup = VolumeControlMode;
            _hotkeyGameSettingsBackup = HotkeyGameSettings;
            _hotkeyGlobalSettingsBackup = HotkeyGlobalSettings;
            _showTopBarGameSettingsBackup = ShowTopBarGameSettings;
            _volumeHotkeyStepBackup = VolumeHotkeyStep;

            // Backup toggle values
            _showRecordGameplayBackup = ShowRecordGameplay;
            _showRecordRecentBackup = ShowRecordRecent;
            _showStreamingBackup = ShowStreaming;
            _showPerformanceOverlayBackup = ShowPerformanceOverlay;
            _showScreenshotGalleryBackup = ShowScreenshotGallery;
            _showWebBrowserBackup = ShowWebBrowser;
            _showDiscordBackup = ShowDiscord;
            _showBatteryBackup = ShowBattery;
            _showFpsLimiterPanelBackup = ShowFpsLimiterPanel;
            _showGameVolumeBackup = ShowGameVolume;
            _showGamepadHintsBackup = ShowGamepadHints;
            _buttonPromptStyleBackup = ButtonPromptStyle;

            // Backup shortcut and path values
            _recordGameplayShortcutBackup = RecordGameplayShortcut;
            _recordRecentShortcutBackup = RecordRecentShortcut;
            _streamingShortcutBackup = StreamingShortcut;
            _performanceOverlayShortcutBackup = PerformanceOverlayShortcut;
            _screenshotGalleryPathBackup = ScreenshotGalleryPath;
            _webBrowserPathBackup = WebBrowserPath;
        }

        public void CancelEdit()
        {
            // Restore from backup
            ControllerShortcut = _controllerShortcutBackup;
            DebugMode = _debugModeBackup;
            SettingsThemePreset = _settingsThemePresetBackup;
            AccentColor = _accentColorBackup;
            PanelBackgroundColor = _panelBackgroundColorBackup;
            PanelOpacity = _panelOpacityBackup;
            PanelCornerRadius = _panelCornerRadiusBackup;
            PanelGlassBorder = _panelGlassBorderBackup;
            SettingsMenuGlass = _settingsMenuGlassBackup;
            PanelGlassStrength = _panelGlassStrengthBackup;
            BackdropDim = _backdropDimBackup;
            UseWindowsAccent = _useWindowsAccentBackup;
            UseCoverAccent = _useCoverAccentBackup;
            CustomUseCoverAccent = _customUseCoverAccentBackup;
            CustomAccentColor = _customAccentColorBackup;
            CustomPanelBackgroundColor = _customPanelBackgroundColorBackup;
            CustomPanelOpacity = _customPanelOpacityBackup;
            CustomPanelCornerRadius = _customPanelCornerRadiusBackup;
            CustomPanelGlassBorder = _customPanelGlassBorderBackup;
            CustomPanelGlassStrength = _customPanelGlassStrengthBackup;
            CustomBackdropDim = _customBackdropDimBackup;
            SavedAppearancePresets = _savedAppearancePresetsBackup?.Select(p => p?.Copy()).Where(p => p != null).ToList()
                ?? new List<OverlayAppearancePreset>();
            AchievementStyle = _achievementStyleBackup;
            CloseBehavior = _closeBehaviorBackup;
            EnableAltF4InstantClose = _enableAltF4InstantCloseBackup;
            EnableSteamInstantPlay = _enableSteamInstantPlayBackup;
            EnableGogInstantPlay = _enableGogInstantPlayBackup;
            ShowPlayniteButton = _showPlayniteButtonBackup;
            ShowSteamFriendsButton = _showSteamFriendsButtonBackup;
            AutoAddGameLinks = _autoAddGameLinksBackup;
            AutoAddScsKillerLink = _autoAddScsKillerLinkBackup;
            AutoAddGameBananaLink = _autoAddGameBananaLinkBackup;
            AutoAddVortexLink = _autoAddVortexLinkBackup;
            AutoAddSaveLinks = _autoAddSaveLinksBackup;
            AutoPcPlatformForNewGames = _autoPcPlatformForNewGamesBackup;
            AutoDownloadMetadataForCustomGames = _autoDownloadMetadataForCustomGamesBackup;
            ShowMetadataDownloadProgress = _showMetadataDownloadProgressBackup;
            LocalStoreEnabled = _localStoreEnabledBackup;
            LocalGamesFolder = _localGamesFolderBackup;
            LocalStoreScanOnStartup = _localStoreScanOnStartupBackup;
            LocalStoreScanOnLibraryUpdate = _localStoreScanOnLibraryUpdateBackup;
            LocalStoreDownloadMetadata = _localStoreDownloadMetadataBackup;
            LocalStoreDownloadAchievements = _localStoreDownloadAchievementsBackup;
            HotkeyScanLocalGames = _hotkeyScanLocalGamesBackup;
            EnableEpicInstantPlay = _enableEpicInstantPlayBackup;
            EnableUbisoftInstantPlay = _enableUbisoftInstantPlayBackup;
            EnableXboxInstantPlay = _enableXboxInstantPlayBackup;
            InstantPlayDisabledGames = _instantPlayDisabledGamesBackup != null ? new List<Guid>(_instantPlayDisabledGamesBackup) : new List<Guid>();
            EnableSuspendMinimize = _enableSuspendMinimizeBackup;
            EnableDesktopControllerNav = _enableDesktopControllerNavBackup;
            ExclusiveFullscreenSupport = _exclusiveFullscreenSupportBackup;
            BorderlessWindowedGames = _borderlessWindowedGamesBackup;
            FakeFullscreen = _fakeFullscreenBackup;
            EnableBreadcrumbLog = _enableBreadcrumbLogBackup;
            UseGameBackgroundBackdrop = _useGameBackgroundBackdropBackup;
            BackdropBlur = _backdropBlurBackup;
            BackdropImageMode = _backdropImageModeBackup;
            BackdropImageScale = _backdropImageScaleBackup;
            SubtractSuspendedFromPlaytime = _subtractSuspendedFromPlaytimeBackup;
            SubtractSuspendedOnlyNonLibrary = _subtractSuspendedOnlyNonLibraryBackup;
            ExcludeShortSessions = _excludeShortSessionsBackup;
            MinSessionMinutes = _minSessionMinutesBackup;
            HotkeyOverlayToggle = _hotkeyOverlayToggleBackup;
            HotkeyForceClose = _hotkeyForceCloseBackup;
            HotkeySuspend = _hotkeySuspendBackup;
            HotkeyVolumeUp = _hotkeyVolumeUpBackup;
            HotkeyVolumeDown = _hotkeyVolumeDownBackup;
            HotkeyVolumeMute = _hotkeyVolumeMuteBackup;
            EnablePrintScreenRemap = _enablePrintScreenRemapBackup;
            PrintScreenRemapCombo = _printScreenRemapComboBackup;
            ControllerHotkeyOverlay = _controllerHotkeyOverlayBackup;
            ControllerHotkeyScreenshot = _controllerHotkeyScreenshotBackup;
            ControllerHotkeySuspend = _controllerHotkeySuspendBackup;
            ControllerHotkeyMute = _controllerHotkeyMuteBackup;
            HotkeysLayout_Restore(); // hotkeys-layout
            UpdateChecks_Restore(); // update-checks
            VolumeControlMode = _volumeControlModeBackup;
            HotkeyGameSettings = _hotkeyGameSettingsBackup;
            HotkeyGlobalSettings = _hotkeyGlobalSettingsBackup;
            ShowTopBarGameSettings = _showTopBarGameSettingsBackup;
            VolumeHotkeyStep = _volumeHotkeyStepBackup;

            // Restore toggle backups
            ShowRecordGameplay = _showRecordGameplayBackup;
            ShowRecordRecent = _showRecordRecentBackup;
            ShowStreaming = _showStreamingBackup;
            ShowPerformanceOverlay = _showPerformanceOverlayBackup;
            ShowScreenshotGallery = _showScreenshotGalleryBackup;
            ShowWebBrowser = _showWebBrowserBackup;
            ShowDiscord = _showDiscordBackup;
            ShowBattery = _showBatteryBackup;
            ShowFpsLimiterPanel = _showFpsLimiterPanelBackup;
            ShowGameVolume = _showGameVolumeBackup;
            ShowGamepadHints = _showGamepadHintsBackup;
            ButtonPromptStyle = _buttonPromptStyleBackup;

            // Restore shortcut and path backups
            RecordGameplayShortcut = _recordGameplayShortcutBackup;
            RecordRecentShortcut = _recordRecentShortcutBackup;
            StreamingShortcut = _streamingShortcutBackup;
            PerformanceOverlayShortcut = _performanceOverlayShortcutBackup;
            ScreenshotGalleryPath = _screenshotGalleryPathBackup;
            WebBrowserPath = _webBrowserPathBackup;
        }

        public void EndEdit()
        {
            // Save settings to persistent storage
            plugin.SaveSettingsSafely(this);
            plugin.ReloadOverlay(this);
            // Exclusive Instant Play → no Steam action picker when the option is checked
            plugin.ApplySteamInstantPlayPolicy();
            plugin.ApplyTopBarVisibility();
            plugin.ApplyAutoGameLinks();
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            // Add any validation logic here if needed
            return true;
        }
    }

    /// <summary>Which controller glyphs the overlay prompts and the shortcut labels use.</summary>
    public enum ButtonPromptStyle
    {
        [Description("Auto (match the connected controller)")]
        Auto,

        [Description("Xbox")]
        Xbox,

        [Description("DualShock / DualSense")]
        DualShock
    }

    /// <summary>
    /// Shared prompt state. SonyDetected is true while a DualShock 4 / DualSense is the pad in use
    /// (set by XInputPoller). Auto uses it to choose between Xbox and PlayStation labels.
    /// </summary>
    public static class PadPrompts
    {
        public static volatile bool SonyDetected;

        public static bool IsSony(ButtonPromptStyle style)
        {
            return style == ButtonPromptStyle.DualShock || (style == ButtonPromptStyle.Auto && SonyDetected);
        }
    }

    public enum ControllerShortcut
    {
        [Description("View + Menu (Back + Start)")]
        StartBack,

        [Description("Xbox Button (Guide Button)")]
        Guide,

        [Description("None (Disabled)")]
        None
    }


    public enum CloseBehavior
    {
        [Description("Close Window (asking)")]
        CloseWindow,

        [Description("End Task (telling)")]
        EndTask,

        [Description("Close then End Task")]
        CloseAndEnd
    }

    public enum AspectRatio
    {
        Portrait,
        Landscape,
        Square
    }

    public class ControllerState
    {
        public bool ButtonA { get; set; }
        public bool ButtonB { get; set; }
        public bool ButtonX { get; set; }
        public bool ButtonY { get; set; }
        public bool ButtonStart { get; set; }
        public bool ButtonBack { get; set; }
        public bool ButtonLeftBumper { get; set; }
        public bool ButtonRightBumper { get; set; }
        public Direction LeftStick { get; set; }
        public Direction RightStick { get; set; }
        public bool ButtonDPadUp { get; set; }
        public bool ButtonDPadDown { get; set; }
        public bool ButtonDPadLeft { get; set; }
        public bool ButtonDPadRight { get; set; }
        public bool ButtonGuide { get; set; } // Xbox Guide button
        public bool ButtonLeftTrigger { get; set; }
        public bool ButtonRightTrigger { get; set; }

        public ControllerInput? Update(OnControllerButtonStateChangedArgs args)
        {
            if (args == null)
            {
                return null;
            }
            var pressed = args.State == ControllerInputState.Pressed;
            switch (args.Button)
            {
                case ControllerInput.A:
                    ButtonA = pressed;
                    break;
                case ControllerInput.B:
                    ButtonB = pressed;
                    break;
                case ControllerInput.X:
                    ButtonX = pressed;
                    break;
                case ControllerInput.Y:
                    ButtonY = pressed;
                    break;
                case ControllerInput.Start:
                    ButtonStart = pressed;
                    break;
                case ControllerInput.Back:
                    ButtonBack = pressed;
                    break;
                case ControllerInput.LeftShoulder:
                    ButtonLeftBumper = pressed;
                    break;
                case ControllerInput.RightShoulder:
                    ButtonRightBumper = pressed;
                    break;
                case ControllerInput.TriggerLeft:
                    ButtonLeftTrigger = pressed;
                    break;
                case ControllerInput.TriggerRight:
                    ButtonRightTrigger = pressed;
                    break;
                case ControllerInput.DPadUp:
                    ButtonDPadUp = pressed;
                    break;
                case ControllerInput.DPadDown:
                    ButtonDPadDown = pressed;
                    break;
                case ControllerInput.DPadLeft:
                    ButtonDPadLeft = pressed;
                    break;
                case ControllerInput.DPadRight:
                    ButtonDPadRight = pressed;
                    break;
                case ControllerInput.Guide:
                    ButtonGuide = pressed; // Xbox Guide button
                    break;
                case ControllerInput.LeftStickRight:
                    LeftStick = pressed ? Direction.Right : Direction.None;
                    break;
                case ControllerInput.LeftStickLeft:
                    LeftStick = pressed ? Direction.Left : Direction.None;
                    break;
                case ControllerInput.LeftStickUp:
                    LeftStick = pressed ? Direction.Up : Direction.None;
                    break;
                case ControllerInput.LeftStickDown:
                    LeftStick = pressed ? Direction.Down : Direction.None;
                    break;
                case ControllerInput.RightStickRight:
                    RightStick = pressed ? Direction.Right : Direction.None;
                    break;
                case ControllerInput.RightStickLeft:
                    RightStick = pressed ? Direction.Left : Direction.None;
                    break;
                case ControllerInput.RightStickUp:
                    RightStick = pressed ? Direction.Up : Direction.None;
                    break;
                case ControllerInput.RightStickDown:
                    RightStick = pressed ? Direction.Down : Direction.None;
                    break;
            }

            if (pressed)
            {
                return args.Button;
            }
            else
            {
                // Return null if the button was released
                return null;
            }
        }
    }

    public enum Direction
    {
        Up,
        Down,
        Left,
        Right,
        None
    }

    /// <summary>
    /// The list of games Nexus Mods supports (public file used by Nexus's own apps), matched by name.
    /// </summary>
    internal static class GameBananaLookup
    {
        public sealed class CachedMatch
        {
            public string Name { get; set; }
            public int Id { get; set; }
            public DateTime CheckedUtc { get; set; }
        }
        private static readonly Dictionary<string, CachedMatch> Cache = new Dictionary<string, CachedMatch>(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded;
        private static DateTime _lastRequestUtc = DateTime.MinValue;
        // The metadata worker and the GameBanana link worker both call into this class.
        private static readonly object CacheGate = new object();
        private static readonly object RequestGate = new object();

        public static void Load(string path)
        {
            lock (CacheGate)
            {
                if (_loaded) return;
                _loaded = true;
                try
                {
                    if (!File.Exists(path)) return;
                    var list = Playnite.SDK.Data.Serialization.FromJson<List<CachedMatch>>(File.ReadAllText(path));
                    foreach (var item in list ?? new List<CachedMatch>())
                        if (item != null && !string.IsNullOrWhiteSpace(item.Name)) Cache[item.Name] = item;
                }
                catch { }
            }
        }

        public static void Save(string path)
        {
            lock (CacheGate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp, Playnite.SDK.Data.Serialization.ToJson(Cache.Values.ToList()));
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                }
                catch { }
            }
        }

        private static string Norm(string text)
        {
            return new string((text ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        }

        public static int? Find(string name, string installDirectory)
        {
            var query = name?.Trim();
            if (string.IsNullOrWhiteSpace(query) || query.Length < 2) return 0;
            // Hookshot is the Switch 2 recompilation, not Ship of Harkinian (N64 port).
            // Match this specific port only when its executable is actually present.
            bool exactOnly = false;   // these two ports must never fall back to a looser spelling
            try
            {
                var normalized = Norm(query);
                if ((normalized == "thelegendofzeldaocarinaoftime" || normalized == "hookshot") &&
                    !string.IsNullOrWhiteSpace(installDirectory) &&
                    (File.Exists(Path.Combine(installDirectory, "bin", "hookshot.exe")) || File.Exists(Path.Combine(installDirectory, "hookshot.exe"))))
                { query = "The Legend of Zelda: Ocarina of Time (Switch 2)"; exactOnly = true; }
            }
            catch { }
            if (Norm(query) == "shipofharkinian") { query = "Ship of Harkinian (Ocarina of Time PC Port)"; exactOnly = true; }

            // Console, emulated and PC titles are named differently from GameBanana's list ("The" prefix,
            // edition words, region suffixes). Try a few safe spellings, each needing one exact, unique
            // title match. Subtitles are never cut off: "Zelda: Twilight Princess" is not "The Legend of Zelda".
            var allCandidates = exactOnly ? new List<string> { query } : Candidates(query);
            if (!exactOnly)
            {
                // Fan ports / remasters ("God Hand Rearmed") are listed under the original title.
                foreach (var alt in LocalGamesStore.BuildFallbackSearchNames(query))
                    if (Norm(alt).Length >= 3 && !allCandidates.Any(x => Norm(x) == Norm(alt))) allCandidates.Add(alt);
            }
            foreach (var candidate in allCandidates)
            {
                var found = FindExact(candidate);
                if (found == null) return null;      // network/response failure: retry on a later scan
                if (found.Value > 0) return found;
            }
            return 0;
        }

        private static List<string> Candidates(string query)
        {
            var list = new List<string> { query };
            Action<string> add = c =>
            {
                c = (c ?? "").Trim(' ', '-', ':', '.');
                if (Norm(c).Length >= 3 && !list.Any(x => Norm(x) == Norm(c))) list.Add(c);
            };
            var noMarks = System.Text.RegularExpressions.Regex.Replace(query, "[\u2122\u00AE\u00A9]", "");
            add(noMarks);
            var noParens = System.Text.RegularExpressions.Regex.Replace(noMarks, "\\([^)]*\\)|\\[[^\\]]*\\]", " ");
            noParens = System.Text.RegularExpressions.Regex.Replace(noParens, "\\s+", " ").Trim();
            add(noParens);
            var noEdition = System.Text.RegularExpressions.Regex.Replace(noParens,
                "\\b(game of the year|goty|definitive|complete|enhanced|ultimate|deluxe|special|anniversary|remastered|director'?s cut)\\b( edition)?",
                "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            noEdition = System.Text.RegularExpressions.Regex.Replace(noEdition, "\\s+", " ").Trim();
            add(noEdition);
            if (noParens.StartsWith("the ", StringComparison.OrdinalIgnoreCase)) add(noParens.Substring(4));
            else add("The " + noParens);
            return list;
        }

        // null = network/response failure, 0 = no unique exact match, >0 = GameBanana game id
        private static int? FindExact(string query)
        {
            lock (CacheGate)
            {
                CachedMatch cached;
                if (Cache.TryGetValue(query, out cached) && cached.CheckedUtc <= DateTime.UtcNow &&
                    (DateTime.UtcNow - cached.CheckedUtc).TotalDays < (cached.Id > 0 ? 30 : 1)) return cached.Id;
            }
            try
            {
                string body;
                lock (RequestGate)   // one request per second across every worker
                {
                    var delay = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - _lastRequestUtc);
                    if (delay > TimeSpan.Zero) System.Threading.Thread.Sleep(delay);
                    _lastRequestUtc = DateTime.UtcNow;
                    var url = "https://gamebanana.com/apiv11/Util/Search/Results?_sModelName=Game&_sSearchString=" + Uri.EscapeDataString(query);
                    var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                    request.Timeout = 12000;
                    request.ReadWriteTimeout = 12000;
                    request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) PlayniteGameOverlay/1.0";
                    request.Accept = "application/json";
                    using (var response = (System.Net.HttpWebResponse)request.GetResponse())
                    using (var stream = response.GetResponseStream())
                    using (var reader = new StreamReader(stream))
                        body = reader.ReadToEnd();
                }

                using (var document = JsonDocument.Parse(body))
                {
                    JsonElement records;
                    if (!document.RootElement.TryGetProperty("_aRecords", out records) || records.ValueKind != JsonValueKind.Array) return null;
                    var wanted = Norm(query);
                    var ids = new HashSet<int>();
                    foreach (var record in records.EnumerateArray())
                    {
                        JsonElement model, title, id;
                        if (!record.TryGetProperty("_sModelName", out model) || model.GetString() != "Game" ||
                            !record.TryGetProperty("_sName", out title) || title.ValueKind != JsonValueKind.String ||
                            Norm(title.GetString()) != wanted ||
                            !record.TryGetProperty("_idRow", out id)) continue;
                        int number;
                        if (id.TryGetInt32(out number) && number > 0) ids.Add(number);
                    }
                    // Ambiguous names and no match get no link; never construct a search URL.
                    var match = ids.Count == 1 ? ids.First() : 0;
                    lock (CacheGate)
                        Cache[query] = new CachedMatch { Name = query, Id = match, CheckedUtc = DateTime.UtcNow };
                    return match;
                }
            }
            catch { return null; }
        }
    }

    internal static class NexusGames
    {
        private sealed class Entry
        {
            public string name { get; set; }
            public string domain_name { get; set; }
            public string nexusmods_url { get; set; }
        }

        private static readonly object Gate = new object();
        private static Dictionary<string, string> _index;
        private static Dictionary<string, string> _vortexGameIds;

        public static void EnsureLoaded(string cachePath, Action<string> log)
        {
            lock (Gate)
            {
                if (_index != null) return;
                string json = null;
                try
                {
                    if (!string.IsNullOrEmpty(cachePath))
                    {
                        var fi = new FileInfo(cachePath);
                        if (fi.Exists && (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalDays < 7)
                            json = File.ReadAllText(cachePath);
                    }
                }
                catch { }

                if (json == null)
                {
                    try
                    {
                        json = Download();
                        if (!string.IsNullOrEmpty(cachePath))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                            File.WriteAllText(cachePath, json);
                        }
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke("Could not download the Nexus Mods game list: " + ex.Message);
                        try { if (!string.IsNullOrEmpty(cachePath) && File.Exists(cachePath)) json = File.ReadAllText(cachePath); } catch { }
                    }
                }
                if (json == null) return;

                try
                {
                    var list = Playnite.SDK.Data.Serialization.FromJson<List<Entry>>(json);
                    var map = new Dictionary<string, string>();
                    var vortexIds = new Dictionary<string, string>();
                    foreach (var e in list ?? new List<Entry>())
                    {
                        if (e == null || string.IsNullOrWhiteSpace(e.name)) continue;
                        var url = !string.IsNullOrWhiteSpace(e.nexusmods_url) ? e.nexusmods_url
                                : (!string.IsNullOrWhiteSpace(e.domain_name) ? "https://www.nexusmods.com/" + e.domain_name : null);
                        var key = Norm(e.name);
                        if (url != null && key.Length > 0 && !map.ContainsKey(key)) map[key] = url;
                        var vortexId = ToVortexGameId(e.domain_name);
                        if (!string.IsNullOrWhiteSpace(vortexId) && key.Length > 0 && !vortexIds.ContainsKey(key))
                            vortexIds[key] = vortexId;
                    }
                    _vortexGameIds = vortexIds;
                    _index = map;
                }
                catch (Exception ex) { log?.Invoke("Could not read the Nexus Mods game list: " + ex.Message); }
            }
        }

        private static string Download()
        {
            try { System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072; } catch { }
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("https://data.nexusmods.com/file/nexus-data/games.json");
            req.Timeout = 20000;
            req.UserAgent = "PlayniteGameOverlay";
            using (var resp = req.GetResponse())
            using (var rd = new StreamReader(resp.GetResponseStream()))
                return rd.ReadToEnd();
        }

        private static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.ToLowerInvariant().Replace("&", "and");
            s = System.Text.RegularExpressions.Regex.Replace(s, "[\\u2122\\u00AE\\u00A9]", "");
            s = System.Text.RegularExpressions.Regex.Replace(s, "[^a-z0-9]", "");
            return s;
        }

        private static string ToVortexGameId(string domainName)
        {
            if (string.IsNullOrWhiteSpace(domainName)) return null;
            switch (domainName.Trim().ToLowerInvariant())
            {
                // Vortex's internal game IDs differ from the Nexus URL domains for these titles.
                case "skyrimspecialedition": return "skyrimse";
                case "newvegas": return "falloutnv";
                case "neverwinter": return "nwn";
                default: return domainName.Trim().ToLowerInvariant();
            }
        }

        private static List<string> NameCandidates(string gameName)
        {
            var candidates = new List<string> { gameName };
            var noParens = System.Text.RegularExpressions.Regex.Replace(gameName, "\\([^)]*\\)", "").Trim();
            if (noParens != gameName) candidates.Add(noParens);
            foreach (var sep in new[] { " - ", " \u2013 " })
            {
                int i = gameName.IndexOf(sep, StringComparison.Ordinal);
                if (i > 0) candidates.Add(gameName.Substring(0, i));
            }
            var edition = System.Text.RegularExpressions.Regex.Replace(noParens,
                "\\b(game of the year|goty|definitive|complete|enhanced|ultimate|deluxe|special|anniversary|remastered|director'?s cut)\\b( edition)?",
                "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim(' ', '-', ':');
            if (edition.Length > 0) candidates.Add(edition);
            if (noParens.StartsWith("the ", StringComparison.OrdinalIgnoreCase)) candidates.Add(noParens.Substring(4));
            return candidates;
        }

        /// <summary>True once the Nexus Mods game list has been read, so "not supported" can be trusted.</summary>
        public static bool IsLoaded { get { return _vortexGameIds != null; } }

        // Games Vortex can actually manage: its built-in game extensions plus the popular official ones.
        // Being listed on Nexus Mods is NOT enough (Nexus lists thousands of games, Vortex supports ~100),
        // so a game must have its Vortex game ID here before it gets a Vortex link.
        private static readonly HashSet<string> VortexSupportedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "7daystodie", "ahatintime", "baldursgate3", "battletech", "bladeandsorcery", "bloodstainedritualofthenight",
            "breakingwheel", "codevein", "conanexiles", "cyberpunk2077", "daggerfallunity", "darkestdungeon",
            "darksouls", "darksouls2", "dawnofman", "devilmaycry5", "divinityoriginalsin2",
            "divinityoriginalsin2definitiveedition", "dragonage", "dragonage2", "dragonsdogma", "elex", "enderal",
            "factorio", "fallout3", "fallout4", "fallout4vr", "falloutnv", "galacticcivilizations3", "gardenpaws",
            "greedfall", "grimdawn", "grimrock", "kenshi", "kerbalspaceprogram", "kingdomcomedeliverance",
            "masterchiefcollection", "microsoftflightsimulator", "monsterhunterworld", "morrowind", "mountandblade",
            "mountandblade2bannerlord", "nehrim", "neverwinter2", "nomanssky", "nwn", "nwnee", "oblivion", "oni",
            "oxygennotincluded", "palworld", "pathfinderkingmaker", "pathfinderwrathoftherighteous",
            "pillarsofeternity2", "prisonarchitect", "residentevil22019", "residentevil32020", "rimworld", "sekiro",
            "shadowrunreturns", "skyrim", "skyrimse", "skyrimvr", "spyroreignitedtrilogy", "starbound",
            "stardewvalley", "starfield", "stateofdecay", "subnautica", "subnauticabelowzero", "survivingmars",
            "swkotor", "teamfortress2", "teso", "thesims3", "thesims4", "torchlight2", "totalwarthreekingdoms",
            "untitledgoose", "vampirebloodlines", "warthunder", "witcher", "witcher2", "witcher3",
            "wolcenlordsofmayhem", "worldoftanks", "x4foundations", "xcom2", "xrebirth"
        };

        /// <summary>
        /// The Vortex --game identifier for a game, or null when Vortex does not support the game
        /// (even if Nexus Mods has a page for it).
        /// </summary>
        public static string GetVortexGameId(string gameName, string steamAppId = null)
        {
            // A Steam app id is exact; a title match is a guess. Prefer the exact answer.
            string bySteam;
            if (!string.IsNullOrEmpty(steamAppId) && SteamAppToVortexId.TryGetValue(steamAppId, out bySteam))
                return bySteam;

            var map = _vortexGameIds;
            if (map == null || string.IsNullOrWhiteSpace(gameName)) return null;
            foreach (var candidate in NameCandidates(gameName))
            {
                string gameId;
                if (map.TryGetValue(Norm(candidate), out gameId))
                {
                    // Every game Nexus lists gets its Nexus id: that is what "Vortex --game <id>" selects.
                    return RefineVortexId(gameName, gameId);
                }
            }
            return null;
        }

        // Steam app id -> Vortex game id, for the titles where picking the wrong sibling is easy
        // (Skyrim LE / SE / VR, Fallout 3 / 4 / 4 VR ...) plus the most common supported games.
        private static readonly Dictionary<string, string> SteamAppToVortexId = new Dictionary<string, string>
        {
            { "72850", "skyrim" }, { "489830", "skyrimse" }, { "611670", "skyrimvr" },
            { "377160", "fallout4" }, { "611660", "fallout4vr" }, { "22380", "falloutnv" },
            { "22300", "fallout3" }, { "22370", "fallout3" }, { "22330", "oblivion" }, { "22320", "morrowind" },
            { "1091500", "cyberpunk2077" }, { "292030", "witcher3" }, { "20920", "witcher2" }, { "20900", "witcher" },
            { "1086940", "baldursgate3" }, { "1716740", "starfield" }, { "413150", "stardewvalley" },
            { "1623730", "palworld" }, { "294100", "rimworld" }, { "264710", "subnautica" },
            { "848450", "subnauticabelowzero" }, { "275850", "nomanssky" }, { "233860", "kenshi" },
            { "261550", "mountandblade2bannerlord" }, { "427520", "factorio" }, { "211820", "starbound" },
            { "219990", "grimdawn" }, { "379430", "kingdomcomedeliverance" }
        };

        /// <summary>
        /// Name matching strips words like "Special Edition" / "Anniversary", which can land on the older sibling
        /// (Skyrim Anniversary Edition -> Skyrim, not Skyrim SE). Put the edition and VR variants back.
        /// </summary>
        private static string RefineVortexId(string gameName, string id)
        {
            var n = (gameName ?? "").ToLowerInvariant();
            bool vr = System.Text.RegularExpressions.Regex.IsMatch(n, @"\bvr\b");
            if (id == "skyrim" && (n.Contains("special edition") || n.Contains("anniversary"))) id = "skyrimse";
            if ((id == "skyrim" || id == "skyrimse") && vr) id = "skyrimvr";
            if (id == "fallout4" && vr) id = "fallout4vr";
            return id;
        }

        /// <summary>The game's Nexus Mods page, or null when Nexus does not list it (or the list is not loaded).</summary>
        public static string Find(string gameName)
        {
            var map = _index;
            if (map == null || string.IsNullOrWhiteSpace(gameName)) return null;
            foreach (var candidate in NameCandidates(gameName))
            {
                string url;
                if (map.TryGetValue(Norm(candidate), out url)) return url;
            }
            return null;
        }

    }

    /// <summary>
    /// Checks that a game really has its own PCGamingWiki page (via the wiki's Cargo database) before a link
    /// is added. Results are cached on disk, so only new games cost a request.
    /// </summary>
    internal static class PcGamingWikiPages
    {
        public enum PageState { Unknown, NoPage, Page }

        public sealed class GameKey
        {
            public Guid Id;
            public string Name;      // cleaned game name
            public string Field;     // "Steam_AppID" / "GOGcom_ID" / null
            public string StoreId;   // digits only
            public string ExtraName; // page name from a PCGamingWiki link the game already has
        }

        private sealed class Entry
        {
            public string Key { get; set; }
            public string Url { get; set; }          // null = the wiki has no page for this game
            public DateTime CheckedUtc { get; set; }
        }

        private sealed class CacheFile
        {
            public Dictionary<string, Entry> Items { get; set; } = new Dictionary<string, Entry>();
        }

        private sealed class TitlePage { public string title { get; set; } public string missing { get; set; } public string invalid { get; set; } }
        private sealed class TitleQuery { public Dictionary<string, TitlePage> pages { get; set; } }
        private sealed class TitleResponse { public TitleQuery query { get; set; } }
        private sealed class CargoRow { public Dictionary<string, string> title { get; set; } }
        private sealed class CargoResponse { public List<CargoRow> cargoquery { get; set; } }

        private static readonly object Gate = new object();
        private static string _path;
        private static CacheFile _cache;

        public static void Configure(string cachePath)
        {
            lock (Gate) { _path = cachePath; }
        }

        private static string KeyOf(GameKey k)
        {
            return (k.Name ?? "") + "|" + (k.Field ?? "") + "|" + (k.StoreId ?? "");
        }

        private static void EnsureCacheLocked()
        {
            if (_cache != null) return;
            try
            {
                if (!string.IsNullOrEmpty(_path) && File.Exists(_path))
                    _cache = Playnite.SDK.Data.Serialization.FromJson<CacheFile>(File.ReadAllText(_path));
            }
            catch { }
            if (_cache == null || _cache.Items == null) _cache = new CacheFile();
        }

        /// <summary>What the cache says. Never touches the network, so it is safe on any thread.</summary>
        public static PageState Get(GameKey k, out string url)
        {
            url = null;
            if (k == null) return PageState.Unknown;
            lock (Gate)
            {
                EnsureCacheLocked();
                Entry e;
                if (!_cache.Items.TryGetValue(k.Id.ToString(), out e) || e == null || e.Key != KeyOf(k))
                    return PageState.Unknown;
                // Pages get created over time: look again sooner for games that had none.
                double maxDays = e.Url != null ? 60 : 14;
                if ((DateTime.UtcNow - e.CheckedUtc).TotalDays > maxDays) return PageState.Unknown;
                url = e.Url;
                return e.Url != null ? PageState.Page : PageState.NoPage;
            }
        }

        private static void Save()
        {
            try
            {
                lock (Gate)
                {
                    if (_cache == null || string.IsNullOrEmpty(_path)) return;
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    File.WriteAllText(_path, Playnite.SDK.Data.Serialization.ToJson(_cache));
                }
            }
            catch { }
        }

        /// <summary>Looks up every game that has no fresh answer yet. Call from a background thread.</summary>
        public static void Prepare(List<GameKey> games, Action<string> log, System.Threading.CancellationToken ct)
        {
            if (games == null) return;
            int failures = 0, sinceSave = 0, withPage = 0, noPage = 0, failed = 0;
            foreach (var k in games)
            {
                if (ct.IsCancellationRequested) break;
                string cached;
                if (Get(k, out cached) != PageState.Unknown) continue;

                string pageUrl;
                if (!Query(k, out pageUrl, log))
                {
                    // Offline or the wiki is down: keep what we have and try again on the next pass.
                    failed++;
                    if (++failures >= 3) { log?.Invoke("PCGamingWiki could not be reached; page check postponed."); break; }
                    continue;
                }
                failures = 0;
                if (pageUrl != null) withPage++; else noPage++;
                lock (Gate)
                {
                    EnsureCacheLocked();
                    _cache.Items[k.Id.ToString()] = new Entry { Key = KeyOf(k), Url = pageUrl, CheckedUtc = DateTime.UtcNow };
                }
                if (++sinceSave >= 25) { Save(); sinceSave = 0; }
                System.Threading.Thread.Sleep(120);   // be polite to a community wiki
            }
            Save();
            if (withPage + noPage + failed > 0)
                log?.Invoke("PCGamingWiki page check: " + withPage + " with a page, " + noPage + " without, " + failed + " failed.");
        }

        private static List<string> NameCandidates(string name)
        {
            var list = new List<string>();
            Action<string> add = n =>
            {
                n = (n ?? "").Trim();
                if (n.Length > 0 && n.IndexOf('"') < 0 && n.IndexOf('\\') < 0 &&
                    !list.Contains(n, StringComparer.OrdinalIgnoreCase)) list.Add(n);
            };
            add(name);
            var noParens = System.Text.RegularExpressions.Regex.Replace(name ?? "", "\\([^)]*\\)", "").Trim();
            add(noParens);
            add(noParens.Replace(" - ", ": "));
            var edition = System.Text.RegularExpressions.Regex.Replace(noParens,
                "\\b(game of the year|goty|definitive|complete|enhanced|ultimate|deluxe|special|anniversary|remastered|director'?s cut)\\b( edition)?",
                "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim(' ', '-', ':');
            add(edition);
            return list;
        }

        // PCGamingWiki sits behind Cloudflare, which answers some clients with 403. The first header set that gets
        // through is remembered for later requests.
        private static readonly string[][] PcgwHeaderSets =
        {
            new[] { "PlayniteGameOverlay/1.0 (Playnite extension; https://playnite.link)", "application/json" },
            new[] { "Playnite/10 PlayniteGameOverlay/1.0", "application/json, text/plain, */*" },
            new[] { "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36", "application/json, text/plain, */*" },
        };
        private static int _pcgwHeaderSet;

        private static string HttpGet(string url)
        {
            try { System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072; } catch { }
            System.Net.WebException last = null;
            for (int attempt = 0; attempt < PcgwHeaderSets.Length; attempt++)
            {
                int set = (_pcgwHeaderSet + attempt) % PcgwHeaderSets.Length;
                try
                {
                    var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                    req.Timeout = 15000;
                    req.AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate;
                    req.UserAgent = PcgwHeaderSets[set][0];
                    req.Accept = PcgwHeaderSets[set][1];
                    string body;
                    using (var resp = req.GetResponse())
                    using (var rd = new StreamReader(resp.GetResponseStream()))
                        body = rd.ReadToEnd();
                    if (set != _pcgwHeaderSet)
                    {
                        _pcgwHeaderSet = set;
                        try { Breadcrumb.Write("PCGW: request accepted with header set " + set); } catch { }
                    }
                    return body;
                }
                catch (System.Net.WebException ex)
                {
                    last = ex;
                    var status = (ex.Response as System.Net.HttpWebResponse)?.StatusCode;
                    try { Breadcrumb.Write("PCGW: header set " + set + " -> " + (status.HasValue ? ((int)status.Value).ToString() : ex.Status.ToString())); } catch { }
                    // Only a block (403 / 429 / 503) is worth another header set; anything else would fail again.
                    if (status != System.Net.HttpStatusCode.Forbidden && (int?)status != 429 &&
                        status != System.Net.HttpStatusCode.ServiceUnavailable)
                        throw;
                }
            }
            throw last ?? new System.Net.WebException("PCGamingWiki request failed");
        }

        private static string PageUrlFor(string page)
        {
            return "https://www.pcgamingwiki.com/wiki/" + Uri.EscapeDataString(page.Replace(' ', '_')).Replace("%3A", ":");
        }

        /// <summary>True when the wiki answered (pageUrl is null if it has no page for the game).</summary>
        private static bool Query(GameKey k, out string pageUrl, Action<string> log)
        {
            pageUrl = null;
            var names = NameCandidates(k.Name);
            // A page the game already links to is checked too, so a correct existing link survives.
            if (!string.IsNullOrWhiteSpace(k.ExtraName) && k.ExtraName.IndexOf('"') < 0 && k.ExtraName.IndexOf('\\') < 0 &&
                !names.Contains(k.ExtraName, StringComparer.OrdinalIgnoreCase))
                names.Add(k.ExtraName);
            bool hasStoreId = !string.IsNullOrEmpty(k.Field) && !string.IsNullOrEmpty(k.StoreId) && k.StoreId.All(char.IsDigit);
            if (!hasStoreId && names.Count == 0) return true;

            string cargoError = null;
            try
            {
                var where = new List<string>();
                if (hasStoreId) where.Add(k.Field + " HOLDS \"" + k.StoreId + "\"");
                if (names.Count > 0)
                    where.Add("_pageName IN (" + string.Join(",", names.Select(n => "\"" + n + "\"")) + ")");
                var url = "https://www.pcgamingwiki.com/w/api.php?action=cargoquery&tables=Infobox_game" +
                          "&fields=" + Uri.EscapeDataString("_pageName=Page") +
                          "&where=" + Uri.EscapeDataString(string.Join(" OR ", where)) +
                          "&limit=10&format=json";
                var json = HttpGet(url);
                var parsed = json.IndexOf("\"cargoquery\"", StringComparison.Ordinal) >= 0
                    ? Playnite.SDK.Data.Serialization.FromJson<CargoResponse>(json) : null;
                if (parsed != null && parsed.cargoquery != null)
                {
                    var pages = parsed.cargoquery
                        .Where(r => r != null && r.title != null && r.title.ContainsKey("Page") && !string.IsNullOrWhiteSpace(r.title["Page"]))
                        .Select(r => r.title["Page"]).ToList();
                    if (pages.Count == 0) return true;
                    var page = pages[0];
                    var byName = pages.FirstOrDefault(p => names.Contains(p, StringComparer.OrdinalIgnoreCase));
                    if (byName != null && !hasStoreId) page = byName;
                    pageUrl = PageUrlFor(page);
                    return true;
                }
                cargoError = "unexpected reply";
            }
            catch (Exception ex) { cargoError = ex.Message; }

            // Cargo failed (blocked, changed, or down): fall back to a plain "does this page exist" check.
            // That can only judge by name, so a game with a store id and no name match stays unknown.
            try
            {
                if (names.Count == 0) return false;
                var url = "https://www.pcgamingwiki.com/w/api.php?action=query&redirects=1&format=json&titles=" +
                          Uri.EscapeDataString(string.Join("|", names));
                var parsed = Playnite.SDK.Data.Serialization.FromJson<TitleResponse>(HttpGet(url));
                if (parsed == null || parsed.query == null || parsed.query.pages == null) { log?.Invoke("PCGamingWiki check failed for '" + k.Name + "': " + cargoError); return false; }
                var found = parsed.query.pages.Values.FirstOrDefault(p => p != null && p.missing == null && p.invalid == null &&
                                                                         !string.IsNullOrWhiteSpace(p.title));
                if (found != null) { pageUrl = PageUrlFor(found.title); return true; }
                if (hasStoreId) return false;   // the store-id lookup is what failed: do not guess "no page"
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke("PCGamingWiki check failed for '" + k.Name + "': " + cargoError + " / " + ex.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// Where PC games keep their saves and settings, from the community Ludusavi manifest
    /// (downloaded once, cached, and read only for the games in your library).
    /// </summary>
    internal static class SaveLocations
    {
        public sealed class Templates
        {
            public List<string> Save { get; set; } = new List<string>();
            public List<string> Config { get; set; } = new List<string>();
        }

        public sealed class IndexFile
        {
            public DateTime BuiltUtc { get; set; }
            public Dictionary<string, Templates> Games { get; set; } = new Dictionary<string, Templates>();
        }

        public sealed class GameKey
        {
            public Guid Id;
            public string Name;
            public string SteamId;
        }

        private const string ManifestUrl = "https://raw.githubusercontent.com/mtkennerly/ludusavi-manifest/master/data/manifest.yaml";
        private static readonly object Gate = new object();
        private static IndexFile _index;

        public static Templates Get(Guid id)
        {
            var idx = _index;
            Templates t;
            return idx != null && idx.Games.TryGetValue(id.ToString(), out t) ? t : null;
        }

        private static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.ToLowerInvariant().Replace("&", "and");
            s = System.Text.RegularExpressions.Regex.Replace(s, "[\\u2122\\u00AE\\u00A9]", "");
            return System.Text.RegularExpressions.Regex.Replace(s, "[^a-z0-9]", "");
        }

        /// <summary>Loads the saved index and fills in games that are not in it yet.</summary>
        public static void Prepare(string dataDir, List<GameKey> games, Action<string> log)
        {
            lock (Gate)
            {
                string indexPath = Path.Combine(dataDir, "save_locations.json");
                if (_index == null)
                {
                    try
                    {
                        if (File.Exists(indexPath))
                            _index = Playnite.SDK.Data.Serialization.FromJson<IndexFile>(File.ReadAllText(indexPath));
                    }
                    catch { }
                    if (_index == null || _index.Games == null) _index = new IndexFile();
                }
                // Re-read everything now and then: the manifest keeps growing.
                if (_index.BuiltUtc != default(DateTime) && (DateTime.UtcNow - _index.BuiltUtc).TotalDays > 30)
                    _index = new IndexFile();

                var missing = games.Where(g => !_index.Games.ContainsKey(g.Id.ToString())).ToList();
                if (missing.Count == 0) return;

                string manifest = Path.Combine(dataDir, "ludusavi_manifest.yaml");
                try
                {
                    var fi = new FileInfo(manifest);
                    if (!fi.Exists || (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalDays > 30)
                        Download(manifest);
                }
                catch (Exception ex)
                {
                    log?.Invoke("Could not download the save-location list: " + ex.Message);
                    if (!File.Exists(manifest)) return;
                }

                var byName = new Dictionary<string, List<GameKey>>();
                var bySteam = new Dictionary<string, List<GameKey>>();
                foreach (var g in missing)
                {
                    var n = Norm(g.Name);
                    if (n.Length > 0) { if (!byName.ContainsKey(n)) byName[n] = new List<GameKey>(); byName[n].Add(g); }
                    if (!string.IsNullOrEmpty(g.SteamId)) { if (!bySteam.ContainsKey(g.SteamId)) bySteam[g.SteamId] = new List<GameKey>(); bySteam[g.SteamId].Add(g); }
                }

                var found = new Dictionary<string, Templates>();
                try { Scan(manifest, byName, bySteam, found); }
                catch (Exception ex) { log?.Invoke("Could not read the save-location list: " + ex.Message); return; }

                foreach (var g in missing)   // games the list does not know get an empty entry, so they are not scanned again
                {
                    Templates t;
                    _index.Games[g.Id.ToString()] = found.TryGetValue(g.Id.ToString(), out t) ? t : new Templates();
                }
                if (_index.BuiltUtc == default(DateTime)) _index.BuiltUtc = DateTime.UtcNow;
                try { File.WriteAllText(indexPath, Playnite.SDK.Data.Serialization.ToJson(_index)); } catch { }
            }
        }

        private static void Download(string target)
        {
            try { System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072; } catch { }
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(ManifestUrl);
            req.Timeout = 60000;
            req.ReadWriteTimeout = 60000;
            req.UserAgent = "PlayniteGameOverlay";
            var tmp = target + ".tmp";
            using (var resp = req.GetResponse())
            using (var input = resp.GetResponseStream())
            using (var output = File.Create(tmp))
                input.CopyTo(output);
            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);
        }

        private static string Unquote(string t)
        {
            t = t.Trim();
            if (t.EndsWith(": {}")) t = t.Substring(0, t.Length - 4);
            else if (t.EndsWith(":")) t = t.Substring(0, t.Length - 1);
            t = t.Trim();
            if (t.Length >= 2 && ((t[0] == '"' && t[t.Length - 1] == '"') || (t[0] == '\'' && t[t.Length - 1] == '\'')))
                t = t.Substring(1, t.Length - 2);
            return t;
        }

        /// <summary>Reads the manifest line by line (it is large) and keeps only the games in the library.</summary>
        private static void Scan(string path, Dictionary<string, List<GameKey>> byName,
            Dictionary<string, List<GameKey>> bySteam, Dictionary<string, Templates> result)
        {
            string game = null, section = null, curPath = null, sub = null, steamId = null;
            var files = new List<Tuple<string, List<string>, List<string>>>();
            List<string> tags = null, oses = null;

            Action flushPath = () =>
            {
                if (curPath != null) files.Add(Tuple.Create(curPath, tags ?? new List<string>(), oses ?? new List<string>()));
                curPath = null; tags = null; oses = null; sub = null;
            };
            Action flushGame = () =>
            {
                flushPath();
                if (game != null && files.Count > 0)
                {
                    var matched = new List<GameKey>();
                    List<GameKey> list;
                    if (steamId != null && bySteam.TryGetValue(steamId, out list)) matched.AddRange(list);
                    if (byName.TryGetValue(Norm(game), out list)) matched.AddRange(list);
                    foreach (var key in matched.GroupBy(k => k.Id).Select(x => x.First()))
                    {
                        Templates t;
                        if (!result.TryGetValue(key.Id.ToString(), out t)) { t = new Templates(); result[key.Id.ToString()] = t; }
                        foreach (var f in files)
                        {
                            // Entries for other systems only (no "windows") do not apply here.
                            if (f.Item3.Count > 0 && !f.Item3.Any(o => o.Equals("windows", StringComparison.OrdinalIgnoreCase))) continue;
                            if (f.Item2.Contains("save") && !t.Save.Contains(f.Item1)) t.Save.Add(f.Item1);
                            if (f.Item2.Contains("config") && !t.Config.Contains(f.Item1)) t.Config.Add(f.Item1);
                        }
                    }
                }
                files = new List<Tuple<string, List<string>, List<string>>>();
                steamId = null; section = null;
            };

            using (var sr = new StreamReader(path))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    string t = line.Trim();
                    if (t.Length == 0 || t[0] == '#') continue;
                    int indent = line.Length - line.TrimStart(' ').Length;

                    if (indent == 0)
                    {
                        flushGame();
                        game = Unquote(t);
                        continue;
                    }
                    if (game == null) continue;
                    if (indent == 2)
                    {
                        flushPath();
                        section = t.TrimEnd(':');
                        continue;
                    }
                    if (section == "files")
                    {
                        if (indent == 4) { flushPath(); curPath = Unquote(t); tags = new List<string>(); oses = new List<string>(); }
                        else if (indent == 6) sub = t.TrimEnd(':');
                        else if (indent >= 8 && t.StartsWith("- ") && curPath != null)
                        {
                            var item = t.Substring(2).Trim();
                            if (sub == "tags") tags.Add(item);
                            else if (sub == "when" && item.StartsWith("os:")) oses.Add(item.Substring(3).Trim());
                        }
                    }
                    else if (section == "steam" && indent == 4 && t.StartsWith("id:"))
                        steamId = t.Substring(3).Trim();
                }
                flushGame();
            }
        }

        // ---- turning a manifest path into a real folder on this PC ----

        private static string Expand(string template, string installDir, string gameId)
        {
            string s = template;
            bool needsInstall = s.IndexOf("<base>", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                s.IndexOf("<root>", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                s.IndexOf("<game>", StringComparison.OrdinalIgnoreCase) >= 0;
            if (needsInstall && (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))) return null;

            string root = null, folder = null;
            if (!string.IsNullOrWhiteSpace(installDir))
            {
                var norm = installDir.TrimEnd('\\', '/');
                folder = Path.GetFileName(norm);
                int i = norm.IndexOf("\\steamapps\\", StringComparison.OrdinalIgnoreCase);
                root = i > 0 ? norm.Substring(0, i) : Path.GetDirectoryName(norm);
            }
            Func<Environment.SpecialFolder, string> sf = f => Environment.GetFolderPath(f);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "<winAppData>", sf(Environment.SpecialFolder.ApplicationData) },
                { "<winLocalAppData>", sf(Environment.SpecialFolder.LocalApplicationData) },
                { "<winLocalAppDataLow>", Path.Combine(sf(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow") },
                { "<winDocuments>", sf(Environment.SpecialFolder.MyDocuments) },
                { "<winPublic>", Path.Combine(Path.GetDirectoryName(sf(Environment.SpecialFolder.UserProfile)) ?? "C:\\Users", "Public") },
                { "<winProgramData>", sf(Environment.SpecialFolder.CommonApplicationData) },
                { "<winDir>", sf(Environment.SpecialFolder.Windows) },
                { "<home>", sf(Environment.SpecialFolder.UserProfile) },
                { "<osUserName>", Environment.UserName },
                { "<storeUserId>", "*" },
                { "<storeGameId>", gameId ?? "*" },
                { "<base>", installDir ?? "" },
                { "<root>", root ?? "" },
                { "<game>", folder ?? "" }
            };
            foreach (var kv in map)
                s = System.Text.RegularExpressions.Regex.Replace(s, System.Text.RegularExpressions.Regex.Escape(kv.Key),
                    kv.Value.Replace("$", "$$"), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (s.IndexOf('<') >= 0) return null;   // a placeholder we cannot fill in
            return s.Replace('/', '\\');
        }

        private static string FirstExisting(string path)
        {
            try
            {
                var segs = path.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (segs.Length < 2) return null;
                string start = segs[0].EndsWith(":") ? segs[0] + "\\" : segs[0];
                return Walk(start, segs, 1, 0);
            }
            catch { return null; }
        }

        private static string Walk(string prefix, string[] segs, int i, int depth)
        {
            if (depth > 40) return null;
            if (i == segs.Length)
            {
                if (Directory.Exists(prefix)) return prefix;
                if (File.Exists(prefix)) return Path.GetDirectoryName(prefix);
                return null;
            }
            string seg = segs[i];
            if (seg.IndexOf('*') >= 0 || seg.IndexOf('?') >= 0)
            {
                if (!Directory.Exists(prefix)) return null;
                int tried = 0;
                foreach (var entry in Directory.EnumerateFileSystemEntries(prefix, seg))
                {
                    if (++tried > 25) break;
                    var r = Walk(entry, segs, i + 1, depth + 1);
                    if (r != null) return r;
                }
                return null;
            }
            var next = Path.Combine(prefix, seg);
            if (i < segs.Length - 1 && !Directory.Exists(next)) return null;
            return Walk(next, segs, i + 1, depth + 1);
        }

        /// <summary>The first save / config folder that exists on this PC, or null.</summary>
        public static string ResolveFolder(IEnumerable<string> templates, string installDir, string gameId)
        {
            if (templates == null) return null;
            foreach (var t in templates)
            {
                var p = Expand(t, installDir, gameId);
                if (p == null) continue;
                var folder = FirstExisting(p);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) return folder;
            }
            return null;
        }
    }
}