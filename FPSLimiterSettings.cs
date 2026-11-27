using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace FPSLimiter
{
    // UseDefault leaves existing RTSS settings untouched globally and means Use global per game.
    public enum RtssFlipPreference { UseDefault = 0, Enabled = 1, Disabled = 2 }

    public enum FpsSyncMode
    {
        /// <summary>
        /// Per-game only: no sync mode of its own has been chosen, so the currently active
        /// Global sync mode (Desktop or Fullscreen, whichever applies) is used instead. This is
        /// the default for every new per-game profile, so a game's sync mode automatically
        /// tracks the Global sync mode until the user explicitly overrides it for that game.
        /// </summary>
        UseGlobal = -1,
        Async = 0,
        FrontEdgeSync = 1,
        BackEdgeSync = 2,
        ReflexSync = 3,
        /// <summary>RTSS Scanline Sync (SyncScanline0 + SyncFlush).</summary>
        ScanlineSync = 4,
        /// <summary>RTSS scanline synchronization once every two display refreshes.</summary>
        ScanlineSyncHalf = 5
    }

    public static class FpsSyncModeNames
    {
        public static string GetMethodName(FpsSyncMode mode)
        {
            switch (mode)
            {
                case FpsSyncMode.UseGlobal:
                    return "Use global";
                case FpsSyncMode.FrontEdgeSync:
                    return "Front Edge Sync";
                case FpsSyncMode.BackEdgeSync:
                    return "Back Edge Sync";
                case FpsSyncMode.ReflexSync:
                    return "NVIDIA Reflex";
                case FpsSyncMode.ScanlineSync:
                    return "Scanline Sync";
                case FpsSyncMode.ScanlineSyncHalf:
                    return "Scanline Sync x/2";
                default:
                    return "Async";
            }
        }

        public static string GetParentheticalDescription(FpsSyncMode mode)
        {
            if (mode == FpsSyncMode.UseGlobal) return string.Empty;

            var fullName = GetDisplayName(mode);
            var methodName = GetMethodName(mode);
            return fullName.StartsWith(methodName, StringComparison.Ordinal)
                ? fullName.Substring(methodName.Length).Trim()
                : string.Empty;
        }

        /// <summary>Short explanation shown under each option while the sync dropdown is open.</summary>
        public static string GetDropdownDescription(FpsSyncMode mode)
        {
            var text = GetDropdownDescriptionCore(mode);
            if (mode == FpsSyncMode.UseGlobal || string.IsNullOrEmpty(text)) return text;
            return text + "\nSpecial K equivalent: " + SpecialKFrameLimiterModeNames.GetDisplayName(SpecialKFrameLimiterModeNames.FromSyncMode(mode)) + ".";
        }

        private static string GetDropdownDescriptionCore(FpsSyncMode mode)
        {
            switch (mode)
            {
                case FpsSyncMode.FrontEdgeSync:
                    return "Smoothest frame pacing, slightly higher latency than Async and Back Edge Sync.";
                case FpsSyncMode.BackEdgeSync:
                    return "Similar to Async with lower latency than Front Edge Sync. Async is still preferable for VRR.";
                case FpsSyncMode.ReflexSync:
                    return "Lowest latency, great for frame generation. May look a bit juddery.";
                case FpsSyncMode.ScanlineSync:
                    return "Fixed refresh rate displays only. Locks each frame to a chosen scanline (see the target slider) for tear-free, low-latency output.";
                case FpsSyncMode.ScanlineSyncHalf:
                    return "Scanline Sync at half the refresh rate (e.g. 72 FPS on 144 Hz). Fixed refresh rate displays only.";
                case FpsSyncMode.UseGlobal:
                    return string.Empty;
                default:
                    return "Low latency, good frame pacing, great for VRR.";
            }
        }

        public static string GetDisplayName(FpsSyncMode mode)
        {
            switch (mode)
            {
                case FpsSyncMode.UseGlobal:
                    return "Use Global Sync Mode";
                case FpsSyncMode.FrontEdgeSync:
                    return "Front Edge Sync (smoother frame pacing, slightly higher latency, may fix VRR flicker)";
                case FpsSyncMode.BackEdgeSync:
                    return "Back Edge Sync (similar to async, though async is preferable for VRR)";
                case FpsSyncMode.ReflexSync:
                    return "NVIDIA Reflex (lowest latency, may look a bit juddery, great for frame generation)";
                case FpsSyncMode.ScanlineSync:
                    return "Scanline Sync (Front Edge) (useful only for fixed refresh rate monitors to reduce latency and get rid of tearing)";
                case FpsSyncMode.ScanlineSyncHalf:
                    return "Scanline Sync x/2 (useful only for fixed refresh rate monitors to reduce latency and get rid of tearing)";
                default:
                    return "Async (low latency, smooth frame pacing, great for VRR)";
            }
        }
    }

    /// <summary>
    /// Controls RTSS's "Inject NVIDIA Reflex latency markers" profile option, which RTSS enables
    /// by default on every profile. This is independent of <see cref="FpsSyncMode.ReflexSync"/>
    /// (the framerate limiter mode); it's the separate marker-injection option used for latency
    /// measurement/overlay purposes.
    /// </summary>
    public enum ReflexMarkerMode
    {
        /// <summary>
        /// Per-game only: no explicit choice has been made for this game, so the currently active
        /// Global Reflex marker setting (Desktop or Fullscreen, whichever applies) is used instead.
        /// </summary>
        UseGlobal = -1,
        Enabled = 0,
        Disabled = 1
    }

    /// <summary>What a global hotkey does when pressed.</summary>
    public enum HotkeyAction
    {
        /// <summary>Apply a specific FPS cap (see <see cref="HotkeyBinding.FrameLimit"/>).</summary>
        ApplyFpsLimit = 0,
        /// <summary>Disable the FPS cap for the target game(s).</summary>
        DisableCap = 1,
        /// <summary>Set the RTSS sync mode for the target game(s) (see <see cref="HotkeyBinding.SyncMode"/>).</summary>
        SetSyncMode = 2,
        /// <summary>Toggle RTSS's FCAT "Enable frame color indicator" (global, no game needed).</summary>
        ToggleFrameColorIndicator = 3
    }

    public static class ReflexMarkerModeNames
    {
        public static string GetDisplayName(ReflexMarkerMode mode)
        {
            switch (mode)
            {
                case ReflexMarkerMode.UseGlobal:
                    return "Use Global Setting";
                case ReflexMarkerMode.Disabled:
                    return "Disabled";
                default:
                    return "Enabled";
            }
        }
    }

    public enum PlayniteUiMode
    {
        Desktop = 0,
        Fullscreen = 1
    }

    /// <summary>
    /// Which FPS limiter backend to use. Auto follows Special K Helper selective features
    /// (and running SK services); RTSS / SpecialK force that backend.
    /// </summary>
    public enum LimiterBackendPreference
    {
        Auto = 0,
        Rtss = 1,
        SpecialK = 2
    }

    /// <summary>Per-game backend override; UseGlobal tracks the extension-wide default.</summary>
    public enum GameBackendPreference
    {
        UseGlobal = -1,
        Auto = 0,
        Rtss = 1,
        SpecialK = 2
    }

    /// <summary>Per-game SK injection. UseGlobal follows extension SpecialKInjectionMode.</summary>
    public enum GameSpecialKInjectionPreference
    {
        UseGlobal = 0,
        Local = 1,
        Selective = 2,
        Global = 3
    }

    /// <summary>
    /// Per-game VRR refresh-rate switching (display Hz), not AMD FreeSync on/off.
    /// </summary>
    public enum GameVrrPreference
    {
        UseGlobal = 0,
        Enabled = 1,
        Disabled = 2
    }

    public static class LimiterBackendPreferenceNames
    {
        public static string GetDisplayName(LimiterBackendPreference preference)
        {
            switch (preference)
            {
                case LimiterBackendPreference.Rtss:
                    return "RTSS";
                case LimiterBackendPreference.SpecialK:
                    return "Special K";
                default:
                    return "Auto (Special K when enabled)";
            }
        }

        public static string GetDisplayName(GameBackendPreference preference)
        {
            switch (preference)
            {
                case GameBackendPreference.UseGlobal:
                    return "Use Global";
                case GameBackendPreference.Rtss:
                    return "RTSS";
                case GameBackendPreference.SpecialK:
                    return "Special K";
                default:
                    return "Auto (Special K when enabled)";
            }
        }
    }

    /// <summary>
    /// Special K framerate limiter mode written as LimitEnforcementPolicy / related INI keys.
    /// Maps to the Advanced limiter modes in SK's control panel.
    /// </summary>
    public enum SpecialKFrameLimiterMode
    {
        /// <summary>Smooth pacing (SK "Normal").</summary>
        Normal = 0,
        /// <summary>Lower latency, good for VRR (SK "Low-Latency").</summary>
        LowLatency = 1,
        /// <summary>Tear-line style sync for fixed refresh (SK "Latent Sync").</summary>
        LatentSync = 2,
        /// <summary>NVIDIA Reflex-based limiter (SK "NVIDIA Reflex").</summary>
        NvidiaReflex = 3
    }

    public enum SpecialKReflexTrigger
    {
        StartOfFrame = 0,
        /// <summary>Default for Reflex limiter mode.</summary>
        EndOfFrame = 1
    }

    /// <summary>NVIDIA Reflex Mode in SK control panel.</summary>
    public enum SpecialKReflexMode
    {
        Off = 0,
        LowLatency = 1,
        LowLatencyBoost = 2
    }

    /// <summary>
    /// How Special K injection services are managed around game launch.
    /// Prefer SKIFsvc (no SKIF UI); fall back to SKIF.exe / rundll32.
    /// Independent of Special K Helper.
    /// </summary>
    public enum SpecialKInjectionMode
    {
        /// <summary>Start injection service only when this extension uses the Special K backend for the game.</summary>
        Selective = 0,
        /// <summary>Use the global injector for all eligible games; the service lifetime choice still applies.</summary>
        Global = 1,
        /// <summary>
        /// Local injection only — never start the global service. Use when games have SpecialK.dll / dxgi.dll
        /// in the game folder. Extension still writes profile TargetFPS / defaults.
        /// </summary>
        Local = 2
    }

    public enum SpecialKServiceStopMode
    {
        Never = 0,
        /// <summary>
        /// Stop after Special K is detected in the game; if detection does not happen,
        /// stop on game exit or after the safety timeout.
        /// </summary>
        AfterSuccessfulInjection = 1,
        /// <summary>Explicitly stop the injection service when the game stops.</summary>
        WhenGameStops = 2
    }

    public enum SpecialKUpdateInterval
    {
        OnLaunch = 0,
        Daily = 1,
        Weekly = 2,
        Monthly = 3,
        Never = 4,
        CheckAndInstallOnLaunch = 5
    }

    /// <summary>How often FPS Limiter 3000 checks GitHub for a newer release of itself.</summary>
    public enum ExtensionUpdateInterval
    {
        OnLaunch = 0,
        Weekly = 1,
        Monthly = 2,
        Never = 3
    }

    /// <summary>Which Special K build stream the updater follows.</summary>
    public enum SpecialKUpdateChannel
    {
        /// <summary>Newest CI build of SpecialKO/SpecialK main (via nightly.link).</summary>
        Nightly = 0,
        /// <summary>SKIF's "Discord (updates regularly)" branch from sk-data.special-k.info.</summary>
        Discord = 1
    }

    /// <summary>
    /// Special K [Window.System] AlwaysOnTop values (Window Layering).
    /// </summary>
    public enum SpecialKWindowLayering
    {
        /// <summary>No Preference (SK AlwaysOnTop=-1).</summary>
        NoPreference = -1,
        /// <summary>Prevent Always-On-Top (SK AlwaysOnTop=0).</summary>
        PreventAlwaysOnTop = 0,
        /// <summary>Force Always-On-Top (SK AlwaysOnTop=1).</summary>
        ForceAlwaysOnTop = 1,
        /// <summary>Multitasking-On-Top (SK AlwaysOnTop=2).</summary>
        MultitaskingOnTop = 2
    }

    /// <summary>
    /// Special K Framerate Limiter → Energy Efficiency presets.
    /// Maps to [Render.FrameRate] BusyWaitPercent / BusyWaitBias.
    /// </summary>
    public enum SpecialKEnergyEfficiency
    {
        Default = 0,
        PowerSaving = 1,
        MaxAccuracy = 2,
        Custom = 3
    }

    public static class SpecialKFrameLimiterModeNames
    {
        public static string GetDisplayName(SpecialKFrameLimiterMode mode)
        {
            switch (mode)
            {
                case SpecialKFrameLimiterMode.LowLatency:
                    return "Low-Latency";
                case SpecialKFrameLimiterMode.LatentSync:
                    return "Latent Sync";
                case SpecialKFrameLimiterMode.NvidiaReflex:
                    return "NVIDIA Reflex";
                default:
                    return "Normal";
            }
        }

        /// <summary>
        /// Values written to [Render.FrameRate] LimitEnforcementPolicy.
        /// Matched to Special K control panel presets (from reference SpecialK.ini exports):
        /// Normal = 4, Low-Latency = 2, Latent Sync = -4, NVIDIA Reflex = 2.
        /// </summary>
        /// <summary>
        /// Special K equivalent of an RTSS-style Sync mode: Async / Back Edge = Low-Latency,
        /// Front Edge = Normal, Scanline = Latent Sync, Reflex = NVIDIA Reflex.
        /// </summary>
        public static SpecialKFrameLimiterMode FromSyncMode(FpsSyncMode syncMode)
        {
            switch (syncMode)
            {
                case FpsSyncMode.Async:
                case FpsSyncMode.BackEdgeSync:
                    return SpecialKFrameLimiterMode.LowLatency;
                case FpsSyncMode.ScanlineSync:
                case FpsSyncMode.ScanlineSyncHalf:
                    return SpecialKFrameLimiterMode.LatentSync;
                case FpsSyncMode.ReflexSync:
                    return SpecialKFrameLimiterMode.NvidiaReflex;
                default:
                    return SpecialKFrameLimiterMode.Normal;
            }
        }

        public static int ToEnforcementPolicy(SpecialKFrameLimiterMode mode)
        {
            switch (mode)
            {
                case SpecialKFrameLimiterMode.LowLatency:
                    return 2;
                case SpecialKFrameLimiterMode.LatentSync:
                    return -4;
                case SpecialKFrameLimiterMode.NvidiaReflex:
                    return 2;
                default:
                    return 4; // Normal (Special K's default/end-of-frame policy)
            }
        }
    }

    public class ModeLimitSettings : ObservableObject
    {
        private bool enabled = false;
        private double frameLimit = 0;
        private string frameLimitText = FormatFrameLimit(0);
        private FpsSyncMode syncMode = FpsSyncMode.Async;
        private ReflexMarkerMode reflexMarkers = ReflexMarkerMode.Enabled;

        public bool Enabled
        {
            get => enabled;
            set => SetValue(ref enabled, value);
        }

        public double FrameLimit
        {
            get => frameLimit;
            set
            {
                if (frameLimit.Equals(value))
                {
                    return;
                }

                frameLimit = value;
                OnPropertyChanged(nameof(FrameLimit));

                var formatted = FormatFrameLimit(value);
                if (!string.Equals(frameLimitText, formatted, StringComparison.Ordinal))
                {
                    frameLimitText = formatted;
                    OnPropertyChanged(nameof(FrameLimitText));
                }
            }
        }

        /// <summary>
        /// Text-editable proxy for <see cref="FrameLimit"/>, same reasoning as
        /// <see cref="HotkeyBinding.FrameLimitText"/>: lets the settings page's Global FPS limit
        /// fields accept fractional values like "59.9" without WPF's default numeric converter
        /// fighting the user mid-keystroke.
        /// </summary>
        public string FrameLimitText
        {
            get => frameLimitText;
            set
            {
                if (string.Equals(frameLimitText, value, StringComparison.Ordinal))
                {
                    return;
                }

                frameLimitText = value;
                OnPropertyChanged(nameof(FrameLimitText));

                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                    parsed >= 0 && parsed <= 1000)
                {
                    frameLimit = parsed;
                    OnPropertyChanged(nameof(FrameLimit));
                }
            }
        }

        public FpsSyncMode SyncMode
        {
            get => syncMode;
            set => SetValue(ref syncMode, value);
        }

        /// <summary>
        /// Whether RTSS should inject NVIDIA Reflex latency markers for this profile. RTSS enables
        /// this by default; set to <see cref="ReflexMarkerMode.Disabled"/> to turn it off.
        /// </summary>
        public ReflexMarkerMode ReflexMarkers
        {
            get => reflexMarkers;
            set => SetValue(ref reflexMarkers, value);
        }

        private static string FormatFrameLimit(double value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Optional independent global FPS limits at a single display resolution.</summary>
    public class ResolutionGlobalSettings
    {
        public int Width { get; set; }
        public int Height { get; set; }
        /// <summary>Always on: every resolution entry overrides the monitor defaults (the toggle was removed). Old saved values are ignored.</summary>
        public bool UseOverride { get => true; set { } }
        public bool IsCurrent { get; set; }
        public string DisplayName => Width + " × " + Height + (IsCurrent ? " (current)" : "");
        public ModeLimitSettings Windows { get; set; } = new ModeLimitSettings();
        public ModeLimitSettings NonWindows { get; set; } = new ModeLimitSettings();

        public ModeLimitSettings GetLimit(bool windowsGames)
        {
            if (windowsGames) return Windows ?? (Windows = new ModeLimitSettings());
            return NonWindows ?? (NonWindows = new ModeLimitSettings());
        }
    }

    /// <summary>One optional precise FPS-sync value for a nominal Windows refresh-rate mode.</summary>
    public class RefreshRateSyncOverride
    {
        public int NominalHz { get; set; }
        public double ExactHz { get; set; }
    }

    /// <summary>Monitor defaults plus optional independent limits per pixel resolution.</summary>
    public class MonitorGlobalSettings
    {
        public string DeviceName { get; set; }
        public string DisplayName { get; set; }
        /// <summary>Optional user-defined label; blank uses the detected detailed display name.</summary>
        public string CustomName { get; set; } = string.Empty;
        /// <summary>
        /// Optional exact signal Hz for nominal 30 Hz Scanline / Latent Sync modes on this monitor.
        /// Zero means use automatic CCD detection; other refresh modes are unaffected.
        /// Saved independently of individual resolution FPS profiles.
        /// </summary>
        public double ExactSyncRefreshHz { get; set; } = 0; // Legacy 30 Hz override (retained for settings compatibility)
        public List<RefreshRateSyncOverride> ExactSyncRefreshOverrides { get; set; } = new List<RefreshRateSyncOverride>();
        public ModeLimitSettings Windows { get; set; } = new ModeLimitSettings();
        public ModeLimitSettings NonWindows { get; set; } = new ModeLimitSettings();
        public List<ResolutionGlobalSettings> Resolutions { get; set; } = new List<ResolutionGlobalSettings>();

        // ── Per-monitor profile: presets and display type are independent for every monitor ──
        /// <summary>Set once the profile below was seeded from the old global settings.</summary>
        public bool ProfileInitialized { get; set; }
        /// <summary>This monitor's FPS presets (comma separated).</summary>
        public string PresetsText { get; set; } = "";
        /// <summary>Display type: true = fixed refresh, false = VRR (FreeSync / G-Sync).</summary>
        public bool FixedRefreshDisplayMode { get; set; }
        /// <summary>When true the display type follows what the monitor reports (EDID) instead of a manual choice.</summary>
        public bool DisplayTypeAuto { get; set; } = true;
        /// <summary>Fixed refresh: build presets from every refresh mode and switch to the matching mode for a cap.</summary>
        public bool MatchPresetsAllModes { get; set; } = true;
        /// <summary>Fixed refresh: switch this monitor's Hz for low FPS caps.</summary>
        public bool VrrRefreshRateEnabled { get; set; }
        public int VrrTargetHz { get; set; }
        public double VrrFpsThreshold { get; set; }
        /// <summary>Generate presets from this monitor's refresh rate (Hz, Hz/2, Hz/3 ... down to 28 FPS).</summary>
        public bool MatchPresetsToRefresh { get; set; }
        /// <summary>Low-lag VSync: generated presets sit a hair below the exact divisor.</summary>
        public bool LowLagVsync { get; set; }

        // ── Forced display mode: when set, every game runs at this resolution + refresh rate ──
        public int ForcedWidth { get; set; }
        public int ForcedHeight { get; set; }
        public int ForcedRefreshHz { get; set; }

        public ModeLimitSettings GetLimit(bool windowsGames)
        {
            if (windowsGames) return Windows ?? (Windows = new ModeLimitSettings());
            return NonWindows ?? (NonWindows = new ModeLimitSettings());
        }

        public ModeLimitSettings GetLimit(bool windowsGames, int width, int height)
        {
            var choice = (Resolutions ?? new List<ResolutionGlobalSettings>()).FirstOrDefault(r =>
                r != null && r.UseOverride && r.Width == width && r.Height == height);
            if (choice == null) return GetLimit(windowsGames);
            var perResolution = choice.GetLimit(windowsGames);
            // A resolution profile only takes over when it has a cap of its own. An empty/disabled one
            // (e.g. created before the monitor default was set) must not mask the monitor's Global FPS.
            var monitorDefault = GetLimit(windowsGames);
            if ((perResolution == null || !perResolution.Enabled || perResolution.FrameLimit <= 0) &&
                monitorDefault != null && monitorDefault.Enabled && monitorDefault.FrameLimit > 0)
                return monitorDefault;
            return perResolution;
        }
    }

    /// <summary>
    /// UI-only choice for the compact per-monitor resolution picker. The settings continue
    /// to live in the original monitor/resolution models, so no stored profile is lost.
    /// </summary>
    public class MonitorResolutionProfile : ObservableObject
    {
        private readonly MonitorGlobalSettings monitor;
        private readonly ResolutionGlobalSettings resolution;

        public MonitorResolutionProfile(MonitorGlobalSettings monitor, ResolutionGlobalSettings resolution = null)
        {
            this.monitor = monitor;
            this.resolution = resolution;
        }

        public string Key => resolution == null ? "default" : resolution.Width + "x" + resolution.Height;
        public string Label => resolution == null ? "Default (all resolutions)" : resolution.DisplayName;
        public bool IsResolution => resolution != null;
        public int Width => resolution?.Width ?? 0;
        public int Height => resolution?.Height ?? 0;

        public ModeLimitSettings Windows => resolution == null ? monitor.GetLimit(true) : resolution.GetLimit(true);
        public ModeLimitSettings NonWindows => resolution == null ? monitor.GetLimit(false) : resolution.GetLimit(false);

        public bool UseOverride => true;

        public bool IsEditable => true;
    }

    /// <summary>Temporary selection state for one monitor, not part of serialized settings.</summary>
    public class MonitorResolutionEditor : ObservableObject
    {
        private readonly FPSLimiterSettings settings;
        private MonitorResolutionProfile selectedProfile;
        private string exactSyncRefreshHzText;
        private string newPresetText = string.Empty;
        private int selectedRefreshRateHz;
        private List<int> refreshRates;
        private bool initializing = true;

        public MonitorGlobalSettings Monitor { get; }
        public string DisplayName => string.IsNullOrWhiteSpace(Monitor.CustomName)
            ? Monitor.DisplayName
            : Monitor.CustomName.Trim();
        public string DeviceName => Monitor.DeviceName;
        public string CustomName
        {
            get => Monitor.CustomName ?? string.Empty;
            set
            {
                var next = value ?? string.Empty;
                if (string.Equals(Monitor.CustomName ?? string.Empty, next, StringComparison.Ordinal)) return;
                Monitor.CustomName = next;
                OnPropertyChanged(nameof(CustomName));
                OnPropertyChanged(nameof(DisplayName));
            }
        }

        public List<int> RefreshRates
        {
            get => refreshRates;
            private set { refreshRates = value; OnPropertyChanged(nameof(RefreshRates)); }
        }

        /// <summary>
        /// Refresh rate paired with the selected resolution. While a resolution is selected this is
        /// the refresh rate every game on this monitor runs at; with "Default" selected it only picks
        /// which nominal mode the Exact Hz box edits (the physical display is not changed).
        /// </summary>
        public int SelectedRefreshRateHz
        {
            get => selectedRefreshRateHz;
            set
            {
                if (selectedRefreshRateHz == value) return;
                if (value <= 0 && refreshRates != null && refreshRates.Count > 0) return;
                selectedRefreshRateHz = value;
                OnPropertyChanged(nameof(SelectedRefreshRateHz));
                LoadExactHzText();
                if (!initializing && Monitor.ForcedWidth > 0)
                {
                    Monitor.ForcedRefreshHz = value;
                    RebuildMatchedPresets();
                    OnPropertyChanged(nameof(ForcedModeLabel));
                }
            }
        }

        /// <summary>Blank means auto for the selected nominal mode only.</summary>
        public string ExactSyncRefreshHzText
        {
            get => exactSyncRefreshHzText;
            set
            {
                if (string.Equals(exactSyncRefreshHzText, value, StringComparison.Ordinal)) return;
                exactSyncRefreshHzText = value ?? string.Empty;
                OnPropertyChanged(nameof(ExactSyncRefreshHzText));
                if (string.IsNullOrWhiteSpace(exactSyncRefreshHzText))
                    SetOverride(0);
                else if (TryParseExactSyncHz(exactSyncRefreshHzText, out var hz))
                    SetOverride(hz);
            }
        }

        private void SetOverride(double hz)
        {
            if (selectedRefreshRateHz <= 0) return;
            if (Monitor.ExactSyncRefreshOverrides == null)
                Monitor.ExactSyncRefreshOverrides = new List<RefreshRateSyncOverride>();
            Monitor.ExactSyncRefreshOverrides.RemoveAll(x => x != null && x.NominalHz == selectedRefreshRateHz);
            if (hz > 0)
                Monitor.ExactSyncRefreshOverrides.Add(new RefreshRateSyncOverride { NominalHz = selectedRefreshRateHz, ExactHz = hz });
            // Once the legacy 30 Hz setting has been edited, don't allow it to override the new setting.
            if (selectedRefreshRateHz == 30)
                Monitor.ExactSyncRefreshHz = 0;
        }

        private void LoadExactHzText()
        {
            var entry = Monitor.ExactSyncRefreshOverrides?.FirstOrDefault(x => x != null && x.NominalHz == selectedRefreshRateHz);
            var hz = entry?.ExactHz ?? (selectedRefreshRateHz == 30 ? Monitor.ExactSyncRefreshHz : 0);
            exactSyncRefreshHzText = hz > 0 ? hz.ToString("0.####", CultureInfo.InvariantCulture) : string.Empty;
            OnPropertyChanged(nameof(ExactSyncRefreshHzText));
        }

        public bool HasInvalidExactSyncRefreshHz =>
            !string.IsNullOrWhiteSpace(exactSyncRefreshHzText) &&
            !TryParseExactSyncHz(exactSyncRefreshHzText, out _);

        private static bool TryParseExactSyncHz(string input, out double hz)
        {
            var normalized = (input ?? "").Trim().Replace(',', '.');
            return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out hz) &&
                !double.IsNaN(hz) && !double.IsInfinity(hz) && hz >= 10 && hz <= 1000;
        }

        public string DetectedSyncRefreshHzLabel
        {
            get
            {
                var hz = RefreshRateManager.GetCurrentPreciseRefreshRate(DeviceName);
                if (hz <= 0) return "Detected: unavailable";
                var nominal = MonitorManager.GetRefreshRate(DeviceName);
                var rounded = nominal > 0 && Math.Abs(hz - nominal) < 0.00005;
                return "Detected: " + hz.ToString("0.####", CultureInfo.InvariantCulture) +
                    " Hz" + (rounded ? " (Windows-reported)" : "");
            }
        }
        public List<MonitorResolutionProfile> Profiles { get; }

        /// <summary>
        /// Choosing a resolution here makes every game on this monitor run at that resolution
        /// (and the selected refresh rate). "Default (all resolutions)" turns forcing off.
        /// </summary>
        public MonitorResolutionProfile SelectedProfile
        {
            get => selectedProfile;
            set
            {
                if (value == null || ReferenceEquals(selectedProfile, value)) return;
                selectedProfile = value;
                OnPropertyChanged(nameof(SelectedProfile));
                if (!initializing) ApplyForcedModeFromSelection();
            }
        }

        private void ApplyForcedModeFromSelection()
        {
            var profile = selectedProfile;
            if (profile != null && profile.IsResolution && profile.Width > 0 && profile.Height > 0)
            {
                Monitor.ForcedWidth = profile.Width;
                Monitor.ForcedHeight = profile.Height;
                var rates = BuildRates(profile.Width, profile.Height);
                RefreshRates = rates;
                var hz = rates.Contains(selectedRefreshRateHz) ? selectedRefreshRateHz
                    : (rates.Count > 0 ? rates.Max() : 0);
                selectedRefreshRateHz = hz;
                Monitor.ForcedRefreshHz = hz;
            }
            else
            {
                Monitor.ForcedWidth = 0;
                Monitor.ForcedHeight = 0;
                Monitor.ForcedRefreshHz = 0;
                var rates = BuildRates(0, 0);
                RefreshRates = rates;
                var current = MonitorManager.GetRefreshRate(DeviceName);
                selectedRefreshRateHz = rates.Contains(selectedRefreshRateHz) ? selectedRefreshRateHz
                    : (rates.Contains(current) ? current : rates.FirstOrDefault());
            }
            OnPropertyChanged(nameof(SelectedRefreshRateHz));
            LoadExactHzText();
            RebuildMatchedPresets();
            OnPropertyChanged(nameof(ForcedModeLabel));
        }

        /// <summary>Refresh rates for the forced resolution (width/height > 0) or the current mode.</summary>
        private List<int> BuildRates(int width, int height)
        {
            var rates = width > 0 && height > 0
                ? RefreshRateManager.GetSupportedRefreshRatesAt(Monitor.DeviceName, width, height)
                : RefreshRateManager.GetSupportedRefreshRates(Monitor.DeviceName);
            rates = rates ?? new List<int>();
            if (width <= 0)
            {
                var currentRate = MonitorManager.GetRefreshRate(Monitor.DeviceName);
                if (currentRate > 1 && !rates.Contains(currentRate)) rates.Add(currentRate);
            }
            foreach (var entry in Monitor.ExactSyncRefreshOverrides ?? new List<RefreshRateSyncOverride>())
                if (entry != null && entry.NominalHz > 1 && !rates.Contains(entry.NominalHz)) rates.Add(entry.NominalHz);
            if (Monitor.ExactSyncRefreshHz > 0 && !rates.Contains(30)) rates.Add(30);
            if (width > 0 && Monitor.ForcedRefreshHz > 1 && !rates.Contains(Monitor.ForcedRefreshHz)) rates.Add(Monitor.ForcedRefreshHz);
            rates.Sort();
            return rates;
        }

        public string ForcedModeLabel => Monitor.ForcedWidth > 0 && Monitor.ForcedHeight > 0
            ? "All games on this monitor run at " + Monitor.ForcedWidth + " × " + Monitor.ForcedHeight +
              (Monitor.ForcedRefreshHz > 0 ? " @ " + Monitor.ForcedRefreshHz + " Hz" : "") +
              " and the previous mode is restored when they close."
            : "Not forced: games keep the current Windows display mode. Pick a resolution to force it for every game.";

        // ── Per-monitor presets ─────────────────────────────────────────────────────────────
        public string PresetsText
        {
            get => Monitor.PresetsText ?? "";
            set
            {
                if (string.Equals(Monitor.PresetsText ?? "", value ?? "", StringComparison.Ordinal)) return;
                Monitor.PresetsText = value ?? "";
                // Typing your own presets turns off automatic presets for this monitor.
                if (Monitor.MatchPresetsToRefresh)
                {
                    Monitor.MatchPresetsToRefresh = false;
                    OnPropertyChanged(nameof(MatchPresetsToRefresh));
                    OnPropertyChanged(nameof(MatchedPresetsPreview));
                }
                OnPropertyChanged(nameof(PresetsText));
                OnPropertyChanged(nameof(PresetEntries));
            }
        }

        /// <summary>Editable, individual preset values for the chip-based settings UI.</summary>
        public List<string> PresetEntries =>
            FPSLimiterSettings.ParsePresetValues(PresetsText).Select(FormatPreset).ToList();

        /// <summary>Temporary input for adding one FPS preset at a time.</summary>
        public string NewPresetText
        {
            get => newPresetText;
            set => SetValue(ref newPresetText, value ?? string.Empty);
        }

        public bool TryAddPreset()
        {
            var input = (NewPresetText ?? string.Empty).Trim().Replace(',', '.');
            if (!double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                double.IsNaN(value) || double.IsInfinity(value) || value <= 0 || value > 1000)
            {
                return false;
            }

            value = Math.Round(value, 4);
            var values = FPSLimiterSettings.ParsePresetValues(PresetsText);
            if (!values.Any(existing => Math.Abs(existing - value) < 0.001))
            {
                values.Add(value);
                PresetsText = string.Join(", ", values.OrderBy(v => v).Select(FormatPreset));
            }

            NewPresetText = string.Empty;
            return true;
        }

        public void RemovePreset(string presetText)
        {
            var input = (presetText ?? string.Empty).Trim().Replace(',', '.');
            if (!double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return;

            var values = FPSLimiterSettings.ParsePresetValues(PresetsText);
            if (values.RemoveAll(existing => Math.Abs(existing - value) < 0.001) > 0)
                PresetsText = string.Join(", ", values.OrderBy(v => v).Select(FormatPreset));
        }

        // Auto-generated presets (and their lock) only apply to fixed-refresh displays.
        public bool PresetsEditable => true;
        public bool PresetsLocked => !PresetsEditable;
        public bool VrrFieldsLocked => !Monitor.VrrRefreshRateEnabled;

        public bool MatchPresetsToRefresh
        {
            get => Monitor.MatchPresetsToRefresh;
            set
            {
                if (Monitor.MatchPresetsToRefresh == value) return;
                Monitor.MatchPresetsToRefresh = value;
                OnPropertyChanged(nameof(MatchPresetsToRefresh));
                OnPropertyChanged(nameof(PresetsEditable));
                OnPropertyChanged(nameof(PresetsLocked));
                RebuildMatchedPresets();
            }
        }

        public bool LowLagVsync
        {
            get => Monitor.LowLagVsync;
            set
            {
                if (Monitor.LowLagVsync == value) return;
                Monitor.LowLagVsync = value;
                OnPropertyChanged(nameof(LowLagVsync));
                RebuildMatchedPresets();
            }
        }

        /// <summary>Refresh rate the presets are derived from: the forced mode, else the active mode.</summary>
        private double EffectiveRefreshHz()
        {
            if (Monitor.ForcedWidth > 0 && Monitor.ForcedRefreshHz > 0)
                return RefreshRateManager.GetPreciseCapForNominalMode(Monitor.ForcedRefreshHz, DeviceName);
            return RefreshRateManager.GetCurrentPreciseRefreshRate(DeviceName);
        }

        /// <summary>True when presets are built from every refresh mode of the display.</summary>
        private bool UsesAllModes => Monitor.MatchPresetsAllModes && !(Monitor.ForcedWidth > 0 && Monitor.ForcedRefreshHz > 0);

        /// <summary>Supported nominal refresh rates (current resolution) with the exact rate used for the active one.</summary>
        private List<double> ModeRatesForPresets()
        {
            var modes = RefreshRateManager.GetRefreshModes(DeviceName, 0, 0);
            if (modes.Count == 0)
            {
                var now = RefreshRateManager.GetCurrentPreciseRefreshRate(DeviceName);
                return now > 0 ? new List<double> { now } : new List<double>();
            }
            return modes.Select(m => m.Hz).ToList();
        }

        /// <summary>The monitor's real refresh modes, e.g. "59.968 / 71.93 Hz".</summary>
        public string RefreshModesLabel
        {
            get
            {
                var rates = ModeRatesForPresets();
                return rates.Count == 0 ? "Refresh modes: not detected"
                    : "Refresh modes: " + string.Join(" / ", rates.Select(FormatPreset)) + " Hz";
            }
        }

        private List<double> BuildPresetValues()
        {
            if (!UsesAllModes)
            {
                var hz = EffectiveRefreshHz();
                return hz <= 0 ? new List<double>() : FPSLimiterSettings.BuildRefreshDivisorPresets(hz, Monitor.LowLagVsync);
            }
            var all = new List<double>();
            foreach (var hz in ModeRatesForPresets())
                all.AddRange(FPSLimiterSettings.BuildRefreshDivisorPresets(hz, Monitor.LowLagVsync));
            var merged = new List<double>();
            foreach (var v in all.OrderBy(v => v))
                if (merged.Count == 0 || v - merged[merged.Count - 1] > 0.05) merged.Add(v);
            return merged;
        }

        public string MatchedPresetsPreview
        {
            get
            {
                OnPropertyChanged(nameof(RefreshModesLabel));
                if (UsesAllModes)
                {
                    var rates = ModeRatesForPresets();
                    if (rates.Count == 0) return "Refresh rates could not be detected.";
                    var presets = BuildPresetValues();
                    return string.Join(" / ", rates.OrderByDescending(r => r).Select(FormatPreset)) + " Hz → " +
                        (presets.Count == 0 ? "no preset reaches 28 FPS"
                            : string.Join(" / ", presets.OrderByDescending(v => v).Select(FormatPreset)) + " FPS");
                }
                var hz = EffectiveRefreshHz();
                if (hz <= 0) return "Refresh rate could not be detected.";
                var values = BuildPresetValues();
                return hz.ToString("0.####", CultureInfo.InvariantCulture) + " Hz → " +
                    (values.Count == 0 ? "no preset reaches 28 FPS"
                        : string.Join(" / ", values.OrderByDescending(v => v).Select(FormatPreset)) + " FPS");
            }
        }

        private static string FormatPreset(double value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        /// <summary>Regenerates this monitor's presets when "match presets to refresh rate" is on.</summary>
        public void RebuildMatchedPresets()
        {
            OnPropertyChanged(nameof(MatchedPresetsPreview));
            if (!Monitor.FixedRefreshDisplayMode || !Monitor.MatchPresetsToRefresh) return;
            var values = BuildPresetValues();
            if (values.Count == 0) return;
            Monitor.PresetsText = string.Join(", ", values.Select(FormatPreset));
            OnPropertyChanged(nameof(PresetsText));
            OnPropertyChanged(nameof(PresetEntries));
        }

        // ── Per-monitor display type ────────────────────────────────────────────────────────
        /// <summary>Follow what the monitor reports instead of a manual VRR / fixed choice.</summary>
        public bool DisplayTypeAuto
        {
            get => Monitor.DisplayTypeAuto;
            set
            {
                if (Monitor.DisplayTypeAuto == value) return;
                Monitor.DisplayTypeAuto = value;
                if (value)
                {
                    var vrr = MonitorManager.DetectVrrSupport(DeviceName);
                    Monitor.FixedRefreshDisplayMode = vrr.Support != VrrSupport.Supported;
                    OnPropertyChanged(nameof(IsFixedRefresh));
                    OnPropertyChanged(nameof(IsVrr));
                    OnPropertyChanged(nameof(PresetsEditable));
                    OnPropertyChanged(nameof(PresetsLocked));
                    RebuildMatchedPresets();
                }
                OnPropertyChanged(nameof(DisplayTypeAuto));
                OnPropertyChanged(nameof(VrrDetectionLabel));
            }
        }

        public string VrrDetectionLabel
        {
            get
            {
                var vrr = MonitorManager.DetectVrrSupport(DeviceName);
                switch (vrr.Support)
                {
                    case VrrSupport.Supported: return "Detected: VRR (" + vrr.Evidence + ")";
                    case VrrSupport.NotSupported: return "Detected: fixed refresh (" + vrr.Evidence + ")";
                    default: return "Detected: unknown, using fixed refresh";
                }
            }
        }

        /// <summary>Presets from every refresh mode, and the display switches to the matching mode for a cap.</summary>
        public bool MatchPresetsAllModes
        {
            get => Monitor.MatchPresetsAllModes;
            set
            {
                if (Monitor.MatchPresetsAllModes == value) return;
                Monitor.MatchPresetsAllModes = value;
                OnPropertyChanged(nameof(MatchPresetsAllModes));
                RebuildMatchedPresets();
            }
        }

        public bool IsFixedRefresh
        {
            get => Monitor.FixedRefreshDisplayMode;
            set
            {
                if (Monitor.FixedRefreshDisplayMode == value) return;
                // A manual choice turns automatic detection off for this monitor.
                if (Monitor.DisplayTypeAuto)
                {
                    Monitor.DisplayTypeAuto = false;
                    OnPropertyChanged(nameof(DisplayTypeAuto));
                }
                Monitor.FixedRefreshDisplayMode = value;
                OnPropertyChanged(nameof(IsFixedRefresh));
                OnPropertyChanged(nameof(IsVrr));
                OnPropertyChanged(nameof(PresetsEditable));
                OnPropertyChanged(nameof(PresetsLocked));
                RebuildMatchedPresets();
            }
        }

        public bool IsVrr
        {
            get => !Monitor.FixedRefreshDisplayMode;
            set { if (value) IsFixedRefresh = false; }
        }

        public bool VrrRefreshRateEnabled
        {
            get => Monitor.VrrRefreshRateEnabled;
            set
            {
                if (Monitor.VrrRefreshRateEnabled == value) return;
                Monitor.VrrRefreshRateEnabled = value;
                OnPropertyChanged(nameof(VrrRefreshRateEnabled));
                OnPropertyChanged(nameof(VrrFieldsLocked));
            }
        }

        public string VrrTargetHzText
        {
            get => Monitor.VrrTargetHz > 0 ? Monitor.VrrTargetHz.ToString(CultureInfo.InvariantCulture) : "";
            set
            {
                int hz;
                if (int.TryParse((value ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out hz) && hz >= 0 && hz <= 1000)
                    Monitor.VrrTargetHz = hz;
                OnPropertyChanged(nameof(VrrTargetHzText));
            }
        }

        public string VrrFpsThresholdText
        {
            get => Monitor.VrrFpsThreshold > 0 ? Monitor.VrrFpsThreshold.ToString("0.####", CultureInfo.InvariantCulture) : "";
            set
            {
                double fps;
                if (double.TryParse((value ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out fps) && fps >= 0 && fps <= 1000)
                    Monitor.VrrFpsThreshold = fps;
                OnPropertyChanged(nameof(VrrFpsThresholdText));
            }
        }

        public string VrrSafeCapLabel
        {
            get
            {
                var hz = MonitorManager.GetPreciseRefreshRate(DeviceName);
                if (hz <= 0) return "VRR-safe cap: refresh rate not detected";
                var cap = Math.Round(RefreshRateManager.GetVrrSafeCap(hz), 2, MidpointRounding.AwayFromZero);
                return "VRR-safe cap: " + cap.ToString("0.##", CultureInfo.InvariantCulture) + " FPS (" +
                    hz.ToString("0.####", CultureInfo.InvariantCulture) + " Hz)";
            }
        }

        /// <summary>Applies the VRR-safe cap to this monitor's Windows-games global limit.</summary>
        public void ApplyVrrSafeCap()
        {
            var hz = MonitorManager.GetPreciseRefreshRate(DeviceName);
            if (hz <= 0 || settings == null) return;
            var cap = Math.Round(RefreshRateManager.GetVrrSafeCap(hz), 2, MidpointRounding.AwayFromZero);
            var limit = settings.GetGlobalForMonitor(DeviceName, true, PlayniteUiMode.Desktop);
            limit.FrameLimit = cap;
            limit.Enabled = true;
        }

        public MonitorResolutionEditor(MonitorGlobalSettings monitor, string preferredKey, FPSLimiterSettings settings = null)
        {
            Monitor = monitor;
            this.settings = settings;
            var forcedKey = monitor.ForcedWidth > 0 && monitor.ForcedHeight > 0
                ? monitor.ForcedWidth + "x" + monitor.ForcedHeight : null;
            refreshRates = BuildRates(forcedKey != null ? monitor.ForcedWidth : 0, forcedKey != null ? monitor.ForcedHeight : 0);
            var currentRate = MonitorManager.GetRefreshRate(monitor.DeviceName);
            selectedRefreshRateHz = forcedKey != null && monitor.ForcedRefreshHz > 0 && refreshRates.Contains(monitor.ForcedRefreshHz)
                ? monitor.ForcedRefreshHz
                : (refreshRates.Contains(currentRate) ? currentRate : refreshRates.FirstOrDefault());
            LoadExactHzText();
            Profiles = new List<MonitorResolutionProfile> { new MonitorResolutionProfile(monitor) };
            Profiles.AddRange((monitor.Resolutions ?? new List<ResolutionGlobalSettings>())
                .Where(r => r != null)
                .Select(r => new MonitorResolutionProfile(monitor, r)));
            selectedProfile = Profiles.FirstOrDefault(p => p.Key == (preferredKey ?? forcedKey)) ?? Profiles[0];
            initializing = false;
        }
    }

    public class FPSLimiterSettings : ObservableObject
    {
        private bool showTopBarIcon = true;
        public bool ShowTopBarIcon
        {
            get => showTopBarIcon;
            set => SetValue(ref showTopBarIcon, value);
        }

        private string themePreset = "Steam Dark";
        public string ThemePreset
        {
            get => themePreset;
            set => SetValue(ref themePreset,
                string.Equals(value, "Steam Dark", StringComparison.OrdinalIgnoreCase) ? "Steam Dark" :
                string.Equals(value, "Windows 11", StringComparison.OrdinalIgnoreCase) ? "Windows 11" : "Steam");
        }

        private bool glassEffect;
        public bool GlassEffect
        {
            get => glassEffect;
            set => SetValue(ref glassEffect, value);
        }

        public const int DefaultRtssScanlineSyncTarget = -1;
        public const int MinRtssScanlineSyncTarget = -500;
        public const int MaxRtssScanlineSyncTarget = 500;
        private string rtssPath = string.Empty;
        private string specialKPath = string.Empty;
        private static string DefaultSpecialKInstallPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "Special K");
        /// <summary>
        /// Windows device name (e.g. \\.\DISPLAY1) used as the default monitor for games
        /// before a window is detected. Empty = Windows primary.
        /// </summary>
        private string preferredGameMonitorDeviceName = string.Empty;
        private RtssFlipPreference rtssForceFlipModel = RtssFlipPreference.Enabled;
        public RtssFlipPreference RtssForceFlipModel
        {
            get => rtssForceFlipModel;
            set => SetValue(ref rtssForceFlipModel, value);
        }

        /// <summary>
        /// Forces RTSS to use the DXGI flip model. This is similar to Windows 11's
        /// windowed-games optimization, but applies to all games even when that
        /// Windows setting does not work.
        /// </summary>
        public bool ForceDxgiFlipModel
        {
            get => rtssForceFlipModel == RtssFlipPreference.Enabled;
            set => RtssForceFlipModel = value ? RtssFlipPreference.Enabled : RtssFlipPreference.Disabled;
        }

        private bool autoStartRtss = true;
        private bool autoCloseRtss = true;
        /// <summary>
        /// When true (default), prefer Special K for FPS limiting whenever SK injection is
        /// detected / available, and skip RTSS so the two injectors do not conflict.
        /// </summary>
        private bool preferSpecialKWhenAvailable = true;
        private LimiterBackendPreference defaultBackend = LimiterBackendPreference.Rtss;
        private bool neverInjectSpecialKForAntiCheatGames = true;
        private bool dontUseSpecialKForVulkanGames = false;
        private bool fallbackToRtssIfSpecialKFails = true;
        private bool specialKVulkanBridgeEnabled = true;
        private bool specialKUseFlipDiscard = true;
        private SpecialKFrameLimiterMode specialKFrameLimiterMode = SpecialKFrameLimiterMode.Normal;
        private bool specialKSteamLogSilent = false;
        private bool specialKReflexEnable = true;
        private bool specialKReflexLowLatency = true;
        private bool specialKReflexLowLatencyBoost = false;
        private int specialKReflexEngagementPolicy = 0; // End of frame default (SK: 0 = EOF, 1 = SOF)
        private bool specialKReflexOptimizeByMarkers = false;
        private bool specialKReflexOverrideNativeMode = true;
        private bool specialKReflexUseFramerateLimiter = false;
        private bool specialKReflexCombineFramerateLimiters = false;
        private bool specialKReflexDisableNative = true;
        private SpecialKReflexTrigger specialKReflexTrigger = SpecialKReflexTrigger.EndOfFrame;
        private SpecialKInjectionMode specialKInjectionMode = SpecialKInjectionMode.Global;
        private SpecialKServiceStopMode specialKServiceStopMode = SpecialKServiceStopMode.AfterSuccessfulInjection;
        private SpecialKUpdateInterval specialKUpdateFrequency = SpecialKUpdateInterval.Weekly;
        private bool specialKAutoUpdateOnLaunch = false;
        private SpecialKUpdateChannel specialKUpdateChannel = SpecialKUpdateChannel.Nightly;
        private bool specialKNotifyUpdateAvailable = true;
        private bool specialKNotifyUpdateInstalled = true;
        private DateTime specialKUpdateLastCheckUtc = DateTime.MinValue;
        private string specialKUpdateLastCheckedArtifact = string.Empty;
        private string specialKUpdateLastInstalledArtifact = string.Empty;
        // Existing per-game Special K profiles may contain their own choices. When enabled,
        // the FPS Limiter settings panel owns the keys it exposes and updates those keys before launch.
        private bool applySpecialKSettingsToExistingProfiles = false;
        private string presetsText = "30, 60, 120";
        // GlobalDesktop / GlobalFullscreen are the Windows-game globals (kept under these names
        // so existing user settings continue to deserialize correctly). Non-Windows games use the
        // separate *NonWindows properties below.
        private ModeLimitSettings globalDesktop = new ModeLimitSettings();
        private ModeLimitSettings globalFullscreen = new ModeLimitSettings();
        private ModeLimitSettings globalDesktopNonWindows = new ModeLimitSettings();
        private ModeLimitSettings globalFullscreenNonWindows = new ModeLimitSettings();
        private List<MonitorGlobalSettings> monitorGlobals = new List<MonitorGlobalSettings>();
        private bool rtssStartedByExtension = false;
        private List<GameLimitProfile> gameLimits = new List<GameLimitProfile>();
        private List<LimitSessionSnapshot> activeSessions = new List<LimitSessionSnapshot>();
        private ObservableCollection<HotkeyBinding> hotkeys = new ObservableCollection<HotkeyBinding>();
        private string playniteMenuHotkey = "None";
        private string globalSettingsHotkey = "None";
        private string rtssOverlayHotkey = "Oem5"; // backslash

        // VRR refresh-rate switching
        private bool fixedRefreshDisplayMode = false;
        private bool vrrRefreshRateEnabled = false;
        private int vrrTargetHz = 48;
        private double vrrFpsThreshold = 40;

        // Non-VRR refresh-rate matching (refresh rate set to a multiple of the FPS cap)
        private bool matchRefreshRateEnabled = false;
        private bool perGameRefreshRateEnabled = true;
        private int rtssScanlineSyncTarget = DefaultRtssScanlineSyncTarget;
        private int matchRefreshRateMaxMultiplier = 4;

        public string RtssPath
        {
            get => rtssPath;
            set => SetValue(ref rtssPath, value);
        }

        /// <summary>
        /// Preferred monitor for games when the process window has not been detected yet
        /// (multi-monitor setups). Empty string means Windows primary display.
        /// </summary>
        public string PreferredGameMonitorDeviceName
        {
            get => preferredGameMonitorDeviceName ?? string.Empty;
            set => SetValue(ref preferredGameMonitorDeviceName, value ?? string.Empty);
        }

        /// <summary>
        /// Path to the Special K install root (folder containing Profiles\) or to SKIF.exe.
        /// New installations default to %LOCALAPPDATA%\Programs\Special K.
        /// </summary>
        public string SpecialKPath
        {
            get => string.IsNullOrWhiteSpace(specialKPath) ? DefaultSpecialKInstallPath : specialKPath;
            set => SetValue(ref specialKPath, value ?? string.Empty);
        }

        /// <summary>
        /// When true, use the Special K backend (and skip RTSS) whenever Special K injection
        /// is detected or a Special K install is present. Prevents RTSS + Special K double-injection.
        /// </summary>
        public bool PreferSpecialKWhenAvailable
        {
            get => preferSpecialKWhenAvailable;
            set => SetValue(ref preferSpecialKWhenAvailable, value);
        }

        /// <summary>
        /// Extension-wide default backend. PreferSpecialKWhenAvailable is kept for older
        /// settings; when DefaultBackend is Auto it still gates on that flag.
        /// </summary>
        public LimiterBackendPreference DefaultBackend
        {
            // Migrate the older Auto value into one of the two explicit choices exposed by the UI.
            get => defaultBackend == LimiterBackendPreference.Auto
                ? (preferSpecialKWhenAvailable ? LimiterBackendPreference.SpecialK : LimiterBackendPreference.Rtss)
                : defaultBackend;
            set
            {
                SetValue(ref defaultBackend, value);
                OnPropertyChanged(nameof(UseSpecialKBackend));
                OnPropertyChanged(nameof(CurrentLimiterMethodLabel));
            }
        }

        /// <summary>
        /// Settings toggle: On = Special K, Off = RTSS.
        /// </summary>
        public bool UseSpecialKBackend
        {
            get => defaultBackend == LimiterBackendPreference.SpecialK ||
                   (defaultBackend == LimiterBackendPreference.Auto && preferSpecialKWhenAvailable);
            set
            {
                DefaultBackend = value
                    ? LimiterBackendPreference.SpecialK
                    : LimiterBackendPreference.Rtss;
                OnPropertyChanged(nameof(UseSpecialKBackend));
                OnPropertyChanged(nameof(CurrentLimiterMethodLabel));
            }
        }

        public string CurrentLimiterMethodLabel =>
            UseSpecialKBackend ? "Current method: Special K" : "Current method: RTSS";

        /// <summary>Skip Special K for games whose install folder contains a known anti-cheat component.</summary>
        public bool NeverInjectSpecialKForAntiCheatGames
        {
            get => neverInjectSpecialKForAntiCheatGames;
            set => SetValue(ref neverInjectSpecialKForAntiCheatGames, value);
        }

        /// <summary>Use RTSS instead of Special K for native Vulkan and DXVK games.</summary>
        public bool DontUseSpecialKForVulkanGames
        {
            get => dontUseSpecialKForVulkanGames;
            set => SetValue(ref dontUseSpecialKForVulkanGames, value);
        }

        /// <summary>Use RTSS on the next launch when a Special K launch fails to start.</summary>
        public bool FallbackToRtssIfSpecialKFails
        {
            get => fallbackToRtssIfSpecialKFails;
            set => SetValue(ref fallbackToRtssIfSpecialKFails, value);
        }

        /// <summary>Enables Special K's VulkanBridge for newly-created game profiles.</summary>
        public bool SpecialKVulkanBridgeEnabled
        {
            get => specialKVulkanBridgeEnabled;
            set => SetValue(ref specialKVulkanBridgeEnabled, value);
        }

        /// <summary>Use Special K's flip-discard swap effect (Render.DXGI / UseFlipDiscard).</summary>
        public bool SpecialKUseFlipDiscard
        {
            get => specialKUseFlipDiscard;
            set => SetValue(ref specialKUseFlipDiscard, value);
        }

        /// <summary>SK Advanced framerate limiter mode (Normal / Low-Latency / Latent Sync / NVIDIA Reflex).</summary>
        public SpecialKFrameLimiterMode SpecialKFrameLimiterMode
        {
            get => specialKFrameLimiterMode;
            set
            {
                SetValue(ref specialKFrameLimiterMode, value);
                OnPropertyChanged(nameof(SpecialKModeIsNormal));
                OnPropertyChanged(nameof(SpecialKModeIsLowLatency));
                OnPropertyChanged(nameof(SpecialKModeIsLatentSync));
                OnPropertyChanged(nameof(SpecialKModeIsReflex));
            }
        }

        public bool SpecialKModeIsNormal
        {
            get => specialKFrameLimiterMode == SpecialKFrameLimiterMode.Normal;
            set { if (value) SpecialKFrameLimiterMode = SpecialKFrameLimiterMode.Normal; }
        }

        public bool SpecialKModeIsLowLatency
        {
            get => specialKFrameLimiterMode == SpecialKFrameLimiterMode.LowLatency;
            set { if (value) SpecialKFrameLimiterMode = SpecialKFrameLimiterMode.LowLatency; }
        }

        public bool SpecialKModeIsLatentSync
        {
            get => specialKFrameLimiterMode == SpecialKFrameLimiterMode.LatentSync;
            set { if (value) SpecialKFrameLimiterMode = SpecialKFrameLimiterMode.LatentSync; }
        }

        public bool SpecialKModeIsReflex
        {
            get => specialKFrameLimiterMode == SpecialKFrameLimiterMode.NvidiaReflex;
            set { if (value) SpecialKFrameLimiterMode = SpecialKFrameLimiterMode.NvidiaReflex; }
        }

        public bool SpecialKSteamLogSilent
        {
            get => specialKSteamLogSilent;
            set => SetValue(ref specialKSteamLogSilent, value);
        }

        private bool specialKPreloadSteamOverlay = false;
        public bool SpecialKPreloadSteamOverlay
        {
            get => specialKPreloadSteamOverlay;
            set => SetValue(ref specialKPreloadSteamOverlay, value);
        }

        private bool specialKDisableSteamOverlay = false;
        public bool SpecialKDisableSteamOverlay
        {
            get => specialKDisableSteamOverlay;
            set => SetValue(ref specialKDisableSteamOverlay, value);
        }

        private bool specialKDisableRumble = false;
        public bool SpecialKDisableRumble
        {
            get => specialKDisableRumble;
            set => SetValue(ref specialKDisableRumble, value);
        }

        private bool specialKAlwaysOnTop = true;
        public bool SpecialKAlwaysOnTop
        {
            get => specialKAlwaysOnTop;
            set => SetValue(ref specialKAlwaysOnTop, value);
        }

        private SpecialKWindowLayering specialKWindowLayering = SpecialKWindowLayering.PreventAlwaysOnTop;
        private SpecialKEnergyEfficiency specialKEnergyEfficiency = SpecialKEnergyEfficiency.MaxAccuracy;
        private double specialKBusyWaitPercent = 100.0;
        private double specialKBusyWaitBias = 10.0;
        private bool specialKUseAmdMwaitx = true;
        /// <summary>
        /// Window layering value written to new profiles and, when enabled, existing profiles.
        /// </summary>
        public SpecialKWindowLayering SpecialKWindowLayering
        {
            get => specialKWindowLayering;
            set
            {
                SetValue(ref specialKWindowLayering, value);
                OnPropertyChanged(nameof(SpecialKLayerNoPreference));
                OnPropertyChanged(nameof(SpecialKLayerPrevent));
                OnPropertyChanged(nameof(SpecialKLayerForce));
                OnPropertyChanged(nameof(SpecialKLayerMultitasking));
            }
        }

        public bool SpecialKLayerNoPreference
        {
            get => specialKWindowLayering == SpecialKWindowLayering.NoPreference;
            set { if (value) SpecialKWindowLayering = SpecialKWindowLayering.NoPreference; }
        }

        public bool SpecialKLayerPrevent
        {
            get => specialKWindowLayering == SpecialKWindowLayering.PreventAlwaysOnTop;
            set { if (value) SpecialKWindowLayering = SpecialKWindowLayering.PreventAlwaysOnTop; }
        }

        public bool SpecialKLayerForce
        {
            get => specialKWindowLayering == SpecialKWindowLayering.ForceAlwaysOnTop;
            set { if (value) SpecialKWindowLayering = SpecialKWindowLayering.ForceAlwaysOnTop; }
        }

        public bool SpecialKLayerMultitasking
        {
            get => specialKWindowLayering == SpecialKWindowLayering.MultitaskingOnTop;
            set { if (value) SpecialKWindowLayering = SpecialKWindowLayering.MultitaskingOnTop; }
        }

        public SpecialKEnergyEfficiency SpecialKEnergyEfficiency
        {
            get => specialKEnergyEfficiency;
            set
            {
                SetValue(ref specialKEnergyEfficiency, value);
                // Apply preset numbers when not Custom
                if (value == SpecialKEnergyEfficiency.Default)
                {
                    // -1 means SK internal default
                }
                else if (value == SpecialKEnergyEfficiency.PowerSaving)
                {
                    specialKBusyWaitPercent = 9.0;  // (1-0.91)*100
                    specialKBusyWaitBias = 0.32;
                    OnPropertyChanged(nameof(SpecialKBusyWaitPercent));
                    OnPropertyChanged(nameof(SpecialKBusyWaitBias));
                }
                else if (value == SpecialKEnergyEfficiency.MaxAccuracy)
                {
                    specialKBusyWaitPercent = 100.0;
                    specialKBusyWaitBias = 10.0;
                    OnPropertyChanged(nameof(SpecialKBusyWaitPercent));
                    OnPropertyChanged(nameof(SpecialKBusyWaitBias));
                }
                OnPropertyChanged(nameof(SpecialKEnergyIsDefault));
                OnPropertyChanged(nameof(SpecialKEnergyIsPowerSaving));
                OnPropertyChanged(nameof(SpecialKEnergyIsMaxAccuracy));
                OnPropertyChanged(nameof(SpecialKEnergyIsCustom));
            }
        }

        public bool SpecialKEnergyIsDefault
        {
            get => specialKEnergyEfficiency == SpecialKEnergyEfficiency.Default;
            set { if (value) SpecialKEnergyEfficiency = SpecialKEnergyEfficiency.Default; }
        }

        public bool SpecialKEnergyIsPowerSaving
        {
            get => specialKEnergyEfficiency == SpecialKEnergyEfficiency.PowerSaving;
            set { if (value) SpecialKEnergyEfficiency = SpecialKEnergyEfficiency.PowerSaving; }
        }

        public bool SpecialKEnergyIsMaxAccuracy
        {
            get => specialKEnergyEfficiency == SpecialKEnergyEfficiency.MaxAccuracy;
            set { if (value) SpecialKEnergyEfficiency = SpecialKEnergyEfficiency.MaxAccuracy; }
        }

        public bool SpecialKEnergyIsCustom
        {
            get => specialKEnergyEfficiency == SpecialKEnergyEfficiency.Custom;
            set { if (value) SpecialKEnergyEfficiency = SpecialKEnergyEfficiency.Custom; }
        }

        public double SpecialKBusyWaitPercent
        {
            get => specialKBusyWaitPercent;
            set => SetValue(ref specialKBusyWaitPercent, value);
        }

        public double SpecialKBusyWaitBias
        {
            get => specialKBusyWaitBias;
            set => SetValue(ref specialKBusyWaitBias, value);
        }

        public bool SpecialKUseAmdMwaitx
        {
            get => specialKUseAmdMwaitx;
            set => SetValue(ref specialKUseAmdMwaitx, value);
        }

        /// <summary>Single switch: enable Special K NVIDIA Reflex limiter-related keys (does not touch TargetFPS).</summary>
        private bool specialKUseReflexMode = false;
        private SpecialKReflexMode specialKReflexMode = SpecialKReflexMode.Off;
        public bool SpecialKUseReflexMode
        {
            get => specialKUseReflexMode;
            set
            {
                SetValue(ref specialKUseReflexMode, value);
                if (value && specialKReflexMode == SpecialKReflexMode.Off)
                    SpecialKReflexMode = SpecialKReflexMode.LowLatency;
                OnPropertyChanged(nameof(SpecialKReflexIsOff));
                OnPropertyChanged(nameof(SpecialKReflexIsLowLatency));
                OnPropertyChanged(nameof(SpecialKReflexIsLowLatencyBoost));
            }
        }

        public SpecialKReflexMode SpecialKReflexMode
        {
            get => specialKReflexMode;
            set
            {
                SetValue(ref specialKReflexMode, value);
                specialKUseReflexMode = value != SpecialKReflexMode.Off;
                OnPropertyChanged(nameof(SpecialKUseReflexMode));
                OnPropertyChanged(nameof(SpecialKReflexIsOff));
                OnPropertyChanged(nameof(SpecialKReflexIsLowLatency));
                OnPropertyChanged(nameof(SpecialKReflexIsLowLatencyBoost));
            }
        }

        public bool SpecialKReflexIsOff
        {
            get => specialKReflexMode == SpecialKReflexMode.Off;
            set { if (value) SpecialKReflexMode = SpecialKReflexMode.Off; }
        }

        public bool SpecialKReflexIsLowLatency
        {
            get => specialKReflexMode == SpecialKReflexMode.LowLatency;
            set { if (value) SpecialKReflexMode = SpecialKReflexMode.LowLatency; }
        }

        public bool SpecialKReflexIsLowLatencyBoost
        {
            get => specialKReflexMode == SpecialKReflexMode.LowLatencyBoost;
            set { if (value) SpecialKReflexMode = SpecialKReflexMode.LowLatencyBoost; }
        }

        public bool SpecialKReflexEnable
        {
            get => specialKReflexEnable;
            set => SetValue(ref specialKReflexEnable, value);
        }

        public bool SpecialKReflexLowLatency
        {
            get => specialKReflexLowLatency;
            set => SetValue(ref specialKReflexLowLatency, value);
        }

        public bool SpecialKReflexLowLatencyBoost
        {
            get => specialKReflexLowLatencyBoost;
            set => SetValue(ref specialKReflexLowLatencyBoost, value);
        }

        /// <summary>NVIDIA.Reflex EngagementPolicy. Default 1 = End of Frame.</summary>
        public int SpecialKReflexEngagementPolicy
        {
            get => specialKReflexEngagementPolicy;
            set => SetValue(ref specialKReflexEngagementPolicy, value);
        }

        public SpecialKReflexTrigger SpecialKReflexTrigger
        {
            get => specialKReflexTrigger;
            set
            {
                SetValue(ref specialKReflexTrigger, value);
                // SK EngagementPolicy: 0 = End of Frame, 1 = Start of Frame (verified against control panel).
                specialKReflexEngagementPolicy = value == SpecialKReflexTrigger.EndOfFrame ? 0 : 1;
                OnPropertyChanged(nameof(SpecialKReflexEngagementPolicy));
                OnPropertyChanged(nameof(SpecialKReflexIsEndOfFrame));
                OnPropertyChanged(nameof(SpecialKReflexIsStartOfFrame));
            }
        }

        public bool SpecialKReflexOptimizeByMarkers
        {
            get => specialKReflexOptimizeByMarkers;
            set => SetValue(ref specialKReflexOptimizeByMarkers, value);
        }

        public bool SpecialKReflexOverrideNativeMode
        {
            get => specialKReflexOverrideNativeMode;
            set => SetValue(ref specialKReflexOverrideNativeMode, value);
        }

        public bool SpecialKReflexUseFramerateLimiter
        {
            get => specialKReflexUseFramerateLimiter;
            set => SetValue(ref specialKReflexUseFramerateLimiter, value);
        }

        public bool SpecialKReflexCombineFramerateLimiters
        {
            get => specialKReflexCombineFramerateLimiters;
            set => SetValue(ref specialKReflexCombineFramerateLimiters, value);
        }

        public bool SpecialKReflexDisableNative
        {
            get => specialKReflexDisableNative;
            set => SetValue(ref specialKReflexDisableNative, value);
        }

        /// <summary>Controls which games may use the global Special K injector; service lifetime is configured separately.</summary>
        public SpecialKInjectionMode SpecialKInjectionMode
        {
            // Treat the former Selective setting as the new service-based Global
            // method so existing configurations continue to select the same behavior.
            get => specialKInjectionMode == SpecialKInjectionMode.Selective
                ? SpecialKInjectionMode.Global
                : specialKInjectionMode;
            set
            {
                if (value == SpecialKInjectionMode.Selective)
                    value = SpecialKInjectionMode.Global;
                SetValue(ref specialKInjectionMode, value);
                OnPropertyChanged(nameof(SpecialKInjectionIsSelective));
                OnPropertyChanged(nameof(SpecialKInjectionIsGlobal));
                OnPropertyChanged(nameof(SpecialKInjectionIsLocal));
            }
        }

        public SpecialKServiceStopMode SpecialKServiceStopMode
        {
            get => specialKServiceStopMode;
            set
            {
                SetValue(ref specialKServiceStopMode, value);
                OnPropertyChanged(nameof(SpecialKStopNever));
                OnPropertyChanged(nameof(SpecialKStopAfterInject));
                OnPropertyChanged(nameof(SpecialKStopOnExit));
            }
        }

        public bool ApplySpecialKSettingsToExistingProfiles
        {
            get => applySpecialKSettingsToExistingProfiles;
            set => SetValue(ref applySpecialKSettingsToExistingProfiles, value);
        }

        public bool SpecialKInjectionIsSelective
        {
            get => SpecialKInjectionMode == SpecialKInjectionMode.Selective;
            set { if (value) SpecialKInjectionMode = SpecialKInjectionMode.Selective; }
        }

        public bool SpecialKInjectionIsGlobal
        {
            get => SpecialKInjectionMode == SpecialKInjectionMode.Global;
            set { if (value) SpecialKInjectionMode = SpecialKInjectionMode.Global; }
        }

        public bool SpecialKInjectionIsLocal
        {
            get => SpecialKInjectionMode == SpecialKInjectionMode.Local;
            set { if (value) SpecialKInjectionMode = SpecialKInjectionMode.Local; }
        }

        public bool SpecialKStopNever
        {
            get => specialKServiceStopMode == SpecialKServiceStopMode.Never;
            set { if (value) SpecialKServiceStopMode = SpecialKServiceStopMode.Never; }
        }

        public bool SpecialKStopAfterInject
        {
            get => specialKServiceStopMode == SpecialKServiceStopMode.AfterSuccessfulInjection;
            set { if (value) SpecialKServiceStopMode = SpecialKServiceStopMode.AfterSuccessfulInjection; }
        }

        public bool SpecialKStopOnExit
        {
            get => specialKServiceStopMode == SpecialKServiceStopMode.WhenGameStops;
            set { if (value) SpecialKServiceStopMode = SpecialKServiceStopMode.WhenGameStops; }
        }

        public SpecialKUpdateInterval SpecialKUpdateFrequency
        {
            get => specialKAutoUpdateOnLaunch
                ? SpecialKUpdateInterval.CheckAndInstallOnLaunch
                : specialKUpdateFrequency == SpecialKUpdateInterval.Daily
                    ? SpecialKUpdateInterval.Weekly
                    : specialKUpdateFrequency;
            set
            {
                bool installOnLaunch = value == SpecialKUpdateInterval.CheckAndInstallOnLaunch;
                var frequency = installOnLaunch ? SpecialKUpdateInterval.OnLaunch :
                    value == SpecialKUpdateInterval.Daily ? SpecialKUpdateInterval.Weekly : value;
                SetValue(ref specialKUpdateFrequency, frequency);
                SetValue(ref specialKAutoUpdateOnLaunch, installOnLaunch);
                OnPropertyChanged(nameof(SpecialKUpdateFrequency));
                OnPropertyChanged(nameof(SpecialKAutoUpdateOnLaunch));
                OnPropertyChanged(nameof(SpecialKUpdateOnLaunch));
                OnPropertyChanged(nameof(SpecialKUpdateDaily));
                OnPropertyChanged(nameof(SpecialKUpdateWeekly));
                OnPropertyChanged(nameof(SpecialKUpdateMonthly));
                OnPropertyChanged(nameof(SpecialKUpdateNever));
            }
        }

        public bool SpecialKUpdateOnLaunch
        {
            get => specialKUpdateFrequency == SpecialKUpdateInterval.OnLaunch;
            set { if (value) SpecialKUpdateFrequency = SpecialKUpdateInterval.OnLaunch; }
        }

        public bool SpecialKUpdateDaily
        {
            get => specialKUpdateFrequency == SpecialKUpdateInterval.Daily;
            set { if (value) SpecialKUpdateFrequency = SpecialKUpdateInterval.Daily; }
        }

        public bool SpecialKUpdateWeekly
        {
            get => specialKUpdateFrequency == SpecialKUpdateInterval.Weekly;
            set { if (value) SpecialKUpdateFrequency = SpecialKUpdateInterval.Weekly; }
        }

        public bool SpecialKUpdateMonthly
        {
            get => specialKUpdateFrequency == SpecialKUpdateInterval.Monthly;
            set { if (value) SpecialKUpdateFrequency = SpecialKUpdateInterval.Monthly; }
        }

        public bool SpecialKUpdateNever
        {
            get => specialKUpdateFrequency == SpecialKUpdateInterval.Never;
            set { if (value) SpecialKUpdateFrequency = SpecialKUpdateInterval.Never; }
        }

        public bool SpecialKAutoUpdateOnLaunch
        {
            get => specialKAutoUpdateOnLaunch;
            set
            {
                SetValue(ref specialKAutoUpdateOnLaunch, value);
                OnPropertyChanged(nameof(SpecialKUpdateFrequency));
            }
        }

        public SpecialKUpdateChannel SpecialKUpdateChannel
        {
            get => specialKUpdateChannel;
            set
            {
                if (specialKUpdateChannel == value) return;
                SetValue(ref specialKUpdateChannel, value);
                // Build names differ per channel; forget the other channel's "available" marker.
                SpecialKUpdateLastCheckedArtifact = string.Empty;
                SpecialKUpdateLastCheckUtc = DateTime.MinValue;
            }
        }

        public bool SpecialKNotifyUpdateAvailable
        {
            get => specialKNotifyUpdateAvailable;
            set => SetValue(ref specialKNotifyUpdateAvailable, value);
        }

        public bool SpecialKNotifyUpdateInstalled
        {
            get => specialKNotifyUpdateInstalled;
            set => SetValue(ref specialKNotifyUpdateInstalled, value);
        }

        public DateTime SpecialKUpdateLastCheckUtc
        {
            get => specialKUpdateLastCheckUtc;
            set => SetValue(ref specialKUpdateLastCheckUtc, value);
        }

        public string SpecialKUpdateLastCheckedArtifact
        {
            get => specialKUpdateLastCheckedArtifact;
            set
            {
                SetValue(ref specialKUpdateLastCheckedArtifact, value ?? string.Empty);
                OnPropertyChanged(nameof(HasAvailableSpecialKUpdate));
            }
        }

        public string SpecialKUpdateLastInstalledArtifact
        {
            get => specialKUpdateLastInstalledArtifact;
            set
            {
                SetValue(ref specialKUpdateLastInstalledArtifact, value ?? string.Empty);
                OnPropertyChanged(nameof(HasAvailableSpecialKUpdate));
            }
        }

        public bool HasAvailableSpecialKUpdate =>
            !string.IsNullOrWhiteSpace(SpecialKUpdateLastCheckedArtifact) &&
            !string.Equals(SpecialKUpdateLastCheckedArtifact, SpecialKUpdateLastInstalledArtifact,
                StringComparison.OrdinalIgnoreCase);

        // ---- FPS Limiter 3000 (this extension) self-update ----
        private ExtensionUpdateInterval extensionUpdateFrequency = ExtensionUpdateInterval.Weekly;
        private bool extensionNotifyUpdateAvailable = true;
        private DateTime extensionUpdateLastCheckUtc = DateTime.MinValue;
        private string extensionUpdateAvailableVersion = string.Empty;
        private string extensionUpdateLastNotifiedVersion = string.Empty;

        public ExtensionUpdateInterval ExtensionUpdateFrequency
        {
            get => extensionUpdateFrequency;
            set => SetValue(ref extensionUpdateFrequency, value);
        }

        public bool ExtensionNotifyUpdateAvailable
        {
            get => extensionNotifyUpdateAvailable;
            set => SetValue(ref extensionNotifyUpdateAvailable, value);
        }

        public DateTime ExtensionUpdateLastCheckUtc
        {
            get => extensionUpdateLastCheckUtc;
            set => SetValue(ref extensionUpdateLastCheckUtc, value);
        }

        /// <summary>Newest release found by the last check (empty when up to date).</summary>
        public string ExtensionUpdateAvailableVersion
        {
            get => extensionUpdateAvailableVersion;
            set
            {
                SetValue(ref extensionUpdateAvailableVersion, value ?? string.Empty);
                OnPropertyChanged(nameof(HasAvailableExtensionUpdate));
            }
        }

        public string ExtensionUpdateLastNotifiedVersion
        {
            get => extensionUpdateLastNotifiedVersion;
            set => SetValue(ref extensionUpdateLastNotifiedVersion, value ?? string.Empty);
        }

        [DontSerialize]
        public bool HasAvailableExtensionUpdate =>
            !string.IsNullOrWhiteSpace(ExtensionUpdateAvailableVersion) &&
            ExtensionUpdateService.IsNewerThanInstalled(ExtensionUpdateAvailableVersion);

        [DontSerialize]
        public string ExtensionInstalledVersionText =>
            "Installed version: " + ExtensionUpdateService.FormatVersion(ExtensionUpdateService.GetInstalledVersion());

        public bool SpecialKReflexIsEndOfFrame
        {
            get => SpecialKReflexTrigger == SpecialKReflexTrigger.EndOfFrame;
            set { if (value) SpecialKReflexTrigger = SpecialKReflexTrigger.EndOfFrame; }
        }

        public bool SpecialKReflexIsStartOfFrame
        {
            get => SpecialKReflexTrigger == SpecialKReflexTrigger.StartOfFrame;
            set { if (value) SpecialKReflexTrigger = SpecialKReflexTrigger.StartOfFrame; }
        }

        public bool AutoStartRtss
        {
            get => autoStartRtss;
            set => SetValue(ref autoStartRtss, value);
        }

        /// <summary>Whether RTSS should be closed after the last active limiter session ends.</summary>
        public bool AutoCloseRtss
        {
            get => autoCloseRtss;
            set => SetValue(ref autoCloseRtss, value);
        }


        public string PresetsText
        {
            get => presetsText;
            set => SetValue(ref presetsText, value);
        }

        /// <summary>Global FPS limit for Windows / PC games when Playnite is in Desktop mode.</summary>
        public ModeLimitSettings GlobalDesktop
        {
            get => globalDesktop ?? (globalDesktop = new ModeLimitSettings());
            set => SetValue(ref globalDesktop, value ?? new ModeLimitSettings());
        }

        /// <summary>Global FPS limit for Windows / PC games when Playnite is in Fullscreen mode.</summary>
        public ModeLimitSettings GlobalFullscreen
        {
            get => globalFullscreen ?? (globalFullscreen = new ModeLimitSettings());
            set => SetValue(ref globalFullscreen, value ?? new ModeLimitSettings());
        }

        /// <summary>Global FPS limit for non-Windows (console / emulated / other) games in Desktop mode.</summary>
        public ModeLimitSettings GlobalDesktopNonWindows
        {
            get => globalDesktopNonWindows ?? (globalDesktopNonWindows = new ModeLimitSettings());
            set => SetValue(ref globalDesktopNonWindows, value ?? new ModeLimitSettings());
        }

        /// <summary>Global FPS limit for non-Windows (console / emulated / other) games in Fullscreen mode.</summary>
        public ModeLimitSettings GlobalFullscreenNonWindows
        {
            get => globalFullscreenNonWindows ?? (globalFullscreenNonWindows = new ModeLimitSettings());
            set => SetValue(ref globalFullscreenNonWindows, value ?? new ModeLimitSettings());
        }

        /// <summary>Persisted monitor profiles; disconnected displays are retained for reconnection.</summary>
        public List<MonitorGlobalSettings> MonitorGlobals
        {
            get => monitorGlobals ?? (monitorGlobals = new List<MonitorGlobalSettings>());
            set => SetValue(ref monitorGlobals, value ?? new List<MonitorGlobalSettings>());
        }

        /// <summary>Exact sync Hz for the specified monitor and nominal rate (0 = automatic).</summary>
        public double GetExactSyncRefreshHzForMonitor(string deviceName, int nominalHz)
        {
            var key = string.IsNullOrWhiteSpace(deviceName) ? MonitorManager.PrimaryDeviceName : deviceName;
            var monitor = MonitorGlobals.FirstOrDefault(m => m != null &&
                string.Equals(m.DeviceName, key, StringComparison.OrdinalIgnoreCase));
            var matched = monitor?.ExactSyncRefreshOverrides?.FirstOrDefault(x => x != null && x.NominalHz == nominalHz);
            if (matched != null) return matched.ExactHz;
            return nominalHz == 30 ? (monitor?.ExactSyncRefreshHz ?? 0) : 0;
        }

        /// <summary>
        /// Synchronize active displays, inheriting the old UI-mode global limit on first discovery.
        /// DISPLAY device names persist across runs under a stable Windows display configuration.
        /// </summary>
        public List<MonitorGlobalSettings> EnsureDetectedMonitors(PlayniteUiMode legacyMode)
        {
            var active = new List<MonitorGlobalSettings>();
            foreach (var display in MonitorManager.GetDisplays())
            {
                var entry = MonitorGlobals.FirstOrDefault(m => m != null &&
                    string.Equals(m.DeviceName, display.DeviceName, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    entry = new MonitorGlobalSettings
                    {
                        DeviceName = display.DeviceName,
                        Windows = CopyGlobal(GetGlobal(legacyMode, true)),
                        NonWindows = CopyGlobal(GetGlobal(legacyMode, false))
                    };
                    MonitorGlobals.Add(entry);
                }
                InitializeMonitorProfile(entry);
                if (entry.DisplayTypeAuto)
                {
                    try
                    {
                        var vrr = MonitorManager.DetectVrrSupport(display.DeviceName);
                        // Unknown / not supported -> fixed refresh; only a monitor that reports VRR is treated as VRR.
                        entry.FixedRefreshDisplayMode = vrr.Support != VrrSupport.Supported;
                    }
                    catch { }
                }
                entry.DisplayName = display.Label;
                active.Add(entry);
            }
            return active;
        }

        /// <summary>
        /// Seeds a monitor's own presets / display type from the old global settings the first time,
        /// so nothing the user already configured is lost when these become per-monitor.
        /// </summary>
        private void InitializeMonitorProfile(MonitorGlobalSettings monitor)
        {
            if (monitor == null || monitor.ProfileInitialized) return;
            monitor.PresetsText = PresetsText ?? "";
            monitor.FixedRefreshDisplayMode = FixedRefreshDisplayMode;
            monitor.VrrRefreshRateEnabled = VrrRefreshRateEnabled;
            monitor.VrrTargetHz = VrrTargetHz;
            monitor.VrrFpsThreshold = VrrFpsThreshold;
            monitor.ProfileInitialized = true;
        }

        /// <summary>The saved profile of a display (null/empty = primary). Null if it was never detected.</summary>
        public MonitorGlobalSettings GetMonitorProfile(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName)) deviceName = MonitorManager.PrimaryDeviceName;
            var monitor = MonitorGlobals.FirstOrDefault(m => m != null &&
                string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
            InitializeMonitorProfile(monitor);
            return monitor;
        }

        /// <summary>Presets of one monitor; falls back to the legacy global list when it has none.</summary>
        public List<double> GetPresetValuesForMonitor(string deviceName)
        {
            var monitor = GetMonitorProfile(deviceName);
            return monitor == null ? GetPresetValues() : ParsePresetValues(monitor.PresetsText);
        }

        /// <summary>
        /// FPS presets that are whole fractions of a refresh rate: Hz, Hz/2, Hz/3 ... while the result
        /// stays at or above the 28 FPS floor (90 Hz → 90, 45, 30). With low-lag VSync each value is
        /// 0.01 FPS below the exact divisor, so the render queue stays empty with VSync on.
        /// </summary>
        public static List<double> BuildRefreshDivisorPresets(double refreshHz, bool lowLagVsync)
        {
            const double MinFps = 28.0;
            const double LowLagOffset = 0.01;
            var result = new List<double>();
            if (refreshHz < MinFps || double.IsNaN(refreshHz) || double.IsInfinity(refreshHz)) return result;
            for (int divisor = 1; divisor <= 16; divisor++)
            {
                var fps = refreshHz / divisor;
                if (fps < MinFps - 0.0001) break;
                result.Add(Math.Round(fps - (lowLagVsync ? LowLagOffset : 0), 4));
            }
            return result.Distinct().OrderBy(v => v).ToList();
        }

        /// <summary>Populates only supported resolutions; keeps profiles for disconnected modes.</summary>
        public void EnsureSupportedResolutions(MonitorGlobalSettings monitor)
        {
            if (monitor == null) return;
            if (monitor.Resolutions == null) monitor.Resolutions = new List<ResolutionGlobalSettings>();
            foreach (var existing in monitor.Resolutions.Where(r => r != null)) existing.IsCurrent = false;
            foreach (var resolution in MonitorManager.GetSupportedResolutions(monitor.DeviceName))
            {
                var item = monitor.Resolutions.FirstOrDefault(r => r != null &&
                    r.Width == resolution.Width && r.Height == resolution.Height);
                if (item == null)
                {
                    item = new ResolutionGlobalSettings
                    {
                        Width = resolution.Width, Height = resolution.Height,
                        Windows = CopyGlobal(monitor.GetLimit(true)),
                        NonWindows = CopyGlobal(monitor.GetLimit(false))
                    };
                    monitor.Resolutions.Add(item);
                }
                item.IsCurrent = resolution.IsCurrent;
            }
            monitor.Resolutions = monitor.Resolutions.Where(r => r != null)
                .OrderByDescending(r => r.IsCurrent)
                .ThenByDescending(r => (long)r.Width * r.Height)
                .ThenByDescending(r => r.Width).ToList();
        }

        private static ModeLimitSettings CopyGlobal(ModeLimitSettings source)
        {
            return new ModeLimitSettings
            {
                Enabled = source.Enabled,
                FrameLimit = source.FrameLimit,
                SyncMode = source.SyncMode,
                ReflexMarkers = source.ReflexMarkers
            };
        }

        /// <summary>Finds the global profile for a display, falling back to legacy settings.</summary>
        public ModeLimitSettings GetGlobalForMonitor(string deviceName, bool windowsGames, PlayniteUiMode fallbackMode)
        {
            var monitor = MonitorGlobals.FirstOrDefault(m => m != null &&
                string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
            if (monitor == null) return GetGlobal(fallbackMode, windowsGames);
            var resolution = MonitorManager.GetCurrentResolution(deviceName);
            return resolution == null ? monitor.GetLimit(windowsGames)
                : monitor.GetLimit(windowsGames, resolution.Width, resolution.Height);
        }

        /// <summary>
        /// Returns the global limit settings for the given Playnite UI mode and Windows/non-Windows
        /// game category. Prefer the overload that takes a <see cref="Playnite.SDK.Models.Game"/>
        /// when applying a limit so the correct category is chosen automatically.
        /// </summary>
        public ModeLimitSettings GetGlobal(PlayniteUiMode mode, bool windowsGames)
        {
            if (mode == PlayniteUiMode.Fullscreen)
            {
                return windowsGames ? GlobalFullscreen : GlobalFullscreenNonWindows;
            }

            return windowsGames ? GlobalDesktop : GlobalDesktopNonWindows;
        }

        /// <summary>Legacy helper: Windows-game global for the given UI mode.</summary>
        public ModeLimitSettings GetGlobal(PlayniteUiMode mode)
        {
            return GetGlobal(mode, windowsGames: true);
        }

        public ModeLimitSettings GetGlobalForGame(PlayniteUiMode mode, Playnite.SDK.Models.Game game)
        {
            return GetGlobal(mode, IsWindowsGame(game));
        }

        /// <summary>
        /// True when the game is treated as a Windows / PC title for global FPS fallback.
        /// Uses platform <c>SpecificationId == "pc_windows"</c> when available, otherwise name
        /// heuristics ("Windows", exact "PC"). Games with no platforms default to Windows so
        /// typical PC libraries keep working without tagging.
        /// </summary>
        public static bool IsWindowsGame(Playnite.SDK.Models.Game game)
        {
            if (game?.Platforms == null || game.Platforms.Count == 0)
            {
                return true;
            }

            foreach (var platform in game.Platforms)
            {
                if (platform == null)
                {
                    continue;
                }

                if (string.Equals(platform.SpecificationId, "pc_windows", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var name = platform.Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (name.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                if (string.Equals(name.Trim(), "PC", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public bool RtssStartedByExtension
        {
            get => rtssStartedByExtension;
            set => SetValue(ref rtssStartedByExtension, value);
        }

        public List<GameLimitProfile> GameLimits
        {
            get => gameLimits ?? (gameLimits = new List<GameLimitProfile>());
            set => SetValue(ref gameLimits, value ?? new List<GameLimitProfile>());
        }

        public List<LimitSessionSnapshot> ActiveSessions
        {
            get => activeSessions ?? (activeSessions = new List<LimitSessionSnapshot>());
            set => SetValue(ref activeSessions, value ?? new List<LimitSessionSnapshot>());
        }

        /// <summary>Global keyboard shortcuts that apply an FPS cap to the currently running (or selected) game.</summary>
        public ObservableCollection<HotkeyBinding> Hotkeys
        {
            get => hotkeys ?? (hotkeys = new ObservableCollection<HotkeyBinding>());
            set => SetValue(ref hotkeys, value ?? new ObservableCollection<HotkeyBinding>());
        }

        private ObservableCollection<SpecialKMacroHotkey> specialKMacroHotkeys;
        /// <summary>Special K macro hotkeys (preset caps only), written to Special K's Global\macros.ini at game launch.</summary>
        public ObservableCollection<SpecialKMacroHotkey> SpecialKMacroHotkeys
        {
            get => specialKMacroHotkeys ?? (specialKMacroHotkeys = new ObservableCollection<SpecialKMacroHotkey>());
            set => SetValue(ref specialKMacroHotkeys, value ?? new ObservableCollection<SpecialKMacroHotkey>());
        }

        /// <summary>Playnite library shortcut that opens per-game FPS Limiter settings for the selected game.</summary>
        public string PlayniteMenuHotkey
        {
            get => string.IsNullOrWhiteSpace(playniteMenuHotkey) ? "None" : playniteMenuHotkey;
            set => SetValue(ref playniteMenuHotkey, string.IsNullOrWhiteSpace(value) ? "None" : value);
        }

        /// <summary>Key (PlayniteHotkey format, e.g. "Oem5" or "Alt+End") that toggles the RTSS overlay. Written to RTSS's HotkeyHandler.cfg.</summary>
        public string RtssOverlayHotkey
        {
            get => string.IsNullOrWhiteSpace(rtssOverlayHotkey) ? "None" : rtssOverlayHotkey;
            set
            {
                SetValue(ref rtssOverlayHotkey, string.IsNullOrWhiteSpace(value) ? "None" : value);
                OnPropertyChanged(nameof(RtssOverlayHotkeyDisplay));
            }
        }

        [DontSerialize]
        public string RtssOverlayHotkeyDisplay => RtssHotkeyCodec.ToDisplay(RtssOverlayHotkey);

        /// <summary>Playnite library shortcut that opens the global FPS Limiter settings window.</summary>
        public string GlobalSettingsHotkey
        {
            get => string.IsNullOrWhiteSpace(globalSettingsHotkey) ? "None" : globalSettingsHotkey;
            set => SetValue(ref globalSettingsHotkey, string.IsNullOrWhiteSpace(value) ? "None" : value);
        }

        /// <summary>
        /// When true, the display refresh rate is switched to <see cref="VrrTargetHz"/> whenever
        /// an FPS cap at or below <see cref="VrrFpsThreshold"/> is applied, and restored on game stop.
        /// </summary>
        public bool FixedRefreshDisplayMode
        {
            get => fixedRefreshDisplayMode;
            set => SetValue(ref fixedRefreshDisplayMode, value);
        }

        public bool VrrRefreshRateEnabled
        {
            get => vrrRefreshRateEnabled;
            set => SetValue(ref vrrRefreshRateEnabled, value);
        }

        /// <summary>Refresh rate (Hz) to switch to when the VRR low-fps cap is active.</summary>
        public int VrrTargetHz
        {
            get => vrrTargetHz;
            set => SetValue(ref vrrTargetHz, value);
        }

        /// <summary>FPS caps at or below this value trigger the refresh-rate switch.</summary>
        public double VrrFpsThreshold
        {
            get => vrrFpsThreshold;
            set => SetValue(ref vrrFpsThreshold, value);
        }

        /// <summary>
        /// When true (and <see cref="VrrRefreshRateEnabled"/> is false), the display refresh rate
        /// is automatically switched to a whole multiple of the active FPS cap whenever a cap is
        /// applied (e.g. 30 FPS -> 60 Hz, 36 FPS -> 72 Hz), and restored on game stop. This keeps
        /// frame pacing even on displays without VRR.
        /// </summary>
        public bool MatchRefreshRateEnabled
        {
            get => matchRefreshRateEnabled;
            set => SetValue(ref matchRefreshRateEnabled, value);
        }

        /// <summary>
        /// Highest multiplier of the FPS cap to try when looking for a supported refresh rate
        /// (2x is tried first, then 3x, 4x, ... up to this value).
        /// </summary>
        public int MatchRefreshRateMaxMultiplier
        {
            get => matchRefreshRateMaxMultiplier;
            set => SetValue(ref matchRefreshRateMaxMultiplier, value);
        }

        /// <summary>
        /// Per-game settings can always force a fixed display refresh rate for the game session. This used to be
        /// a user toggle; it is now permanently on (a saved "false" from older versions is ignored).
        /// </summary>
        public bool PerGameRefreshRateEnabled
        {
            get => true;
            set => SetValue(ref perGameRefreshRateEnabled, true);
        }

        /// <summary>
        /// RTSS SyncScanline0 when sync mode is Scanline Sync. Typical calibrated values are negative (e.g. -140).
        /// -1 is a common starting point for half-rate hybrid setups.
        /// </summary>
        public int RtssScanlineSyncTarget
        {
            get => rtssScanlineSyncTarget;
            set
            {
                var clamped = Math.Max(MinRtssScanlineSyncTarget, Math.Min(MaxRtssScanlineSyncTarget, value));
                SetValue(ref rtssScanlineSyncTarget, clamped);
            }
        }

        public List<double> GetPresetValues()
        {
            return ParsePresetValues(PresetsText);
        }

        public GameLimitProfile GetOrCreateGameProfile(Guid gameId)
        {
            var profile = GameLimits.FirstOrDefault(a => a.GameId == gameId);
            if (profile == null)
            {
                profile = new GameLimitProfile { GameId = gameId };
                GameLimits.Add(profile);
            }

            return profile;
        }

        public GameLimitProfile GetGameProfile(Guid gameId)
        {
            return GameLimits.FirstOrDefault(a => a.GameId == gameId);
        }

        public void RemoveEmptyGameProfile(Guid gameId)
        {
            var profile = GetGameProfile(gameId);
            if (profile != null &&
                !profile.HasAnyLimit() &&
                profile.RtssForceFlipModel == RtssFlipPreference.UseDefault &&
                !profile.SkipLimiterInjection &&
                string.IsNullOrWhiteSpace(profile.LocalWrapperPath) &&
                string.IsNullOrWhiteSpace(profile.LocalWrapperIniPath) &&
                string.IsNullOrWhiteSpace(profile.ManualExecutablePath) &&
                string.IsNullOrWhiteSpace(profile.LastResolvedExecutable) &&
                !profile.HasSpecialKWindowLayeringOverride &&
                !profile.HasSpecialKDisableRumbleOverride &&
                !profile.HasSpecialKPreloadSteamOverlayOverride &&
                !profile.HasSpecialKDisableSteamOverlayOverride &&
                !profile.HasSpecialKUseFlipDiscardOverride &&
                !profile.HasSteamLogSilentOverride)
            {
                GameLimits.Remove(profile);
            }
        }

        public static List<double> ParsePresetValues(string text)
        {
            var result = new List<double>();

            if (string.IsNullOrWhiteSpace(text))
            {
                return result;
            }

            var parts = text.Split(new[] { ',', ';', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                double value;
                if (double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                    value > 0 &&
                    value <= 1000)
                {
                    result.Add(Math.Round(value, 4));
                }
            }

            return result.Distinct().OrderBy(a => a).ToList();
        }
    }

    public class GameLimitProfile
    {
        /// <summary>Disable AMD FreeSync for this game (when the optional ADLX helper is available).</summary>
        public bool DisableFreeSyncForGame { get; set; } = false;
        // Do not launch or configure an FPS Limiter backend for this game.
        public bool SkipLimiterInjection { get; set; } = false;
        /// <summary>Path of a local Special K wrapper DLL this extension copied into the game folder (empty = none).</summary>
        public string LocalWrapperPath { get; set; }
        /// <summary>True when the extension also created the SpecialK.central marker next to it.</summary>
        public bool LocalWrapperMarkerCreated { get; set; }
        /// <summary>The wrapper's own ini in the game folder (dxgi.ini ...). When set, every extension write for this game targets it instead of Profiles\<game>\SpecialK.ini.</summary>
        public string LocalWrapperIniPath { get; set; }
        // UseDefault inherits the global choice.
        public RtssFlipPreference RtssForceFlipModel { get; set; } = RtssFlipPreference.UseDefault;
        public bool HasSpecialKWindowLayeringOverride { get; set; }
        public SpecialKWindowLayering SpecialKWindowLayeringOverride { get; set; } = SpecialKWindowLayering.PreventAlwaysOnTop;
        public bool HasSpecialKDisableRumbleOverride { get; set; }
        public bool SpecialKDisableRumbleOverride { get; set; }
        public bool HasSpecialKPreloadSteamOverlayOverride { get; set; }
        public bool SpecialKPreloadSteamOverlayOverride { get; set; }
        public bool HasSpecialKDisableSteamOverlayOverride { get; set; }
        public bool SpecialKDisableSteamOverlayOverride { get; set; }
        public bool HasSpecialKUseFlipDiscardOverride { get; set; }
        public bool SpecialKUseFlipDiscardOverride { get; set; }
        public Guid GameId { get; set; }
        public ModeLimitSettings Desktop { get; set; } = new ModeLimitSettings { SyncMode = FpsSyncMode.UseGlobal, ReflexMarkers = ReflexMarkerMode.UseGlobal };
        public ModeLimitSettings Fullscreen { get; set; } = new ModeLimitSettings { SyncMode = FpsSyncMode.UseGlobal, ReflexMarkers = ReflexMarkerMode.UseGlobal };
        public string ManualExecutablePath { get; set; }
        public string LastResolvedExecutable { get; set; }
        /// <summary>Special K profile folder name learned from a real run (Profiles folder).</summary>
        public string SpecialKProfileFolder { get; set; }

        /// <summary>
        /// Optional monitor override for this game. Empty means use the global preferred
        /// monitor / detected monitor resolution.
        /// </summary>
        public string PreferredMonitorDeviceName { get; set; } = string.Empty;

        /// <summary>Per-game limiter backend. UseGlobal follows <see cref="FPSLimiterSettings.DefaultBackend"/>.</summary>
        public GameBackendPreference Backend { get; set; } = GameBackendPreference.UseGlobal;

        /// <summary>Per-game SK injection. UseGlobal follows extension SpecialKInjectionMode.</summary>
        public GameSpecialKInjectionPreference SpecialKInjection { get; set; } = GameSpecialKInjectionPreference.UseGlobal;

        /// <summary>Per-game VRR refresh-rate switching. UseGlobal follows extension VrrRefreshRateEnabled.</summary>
        public GameVrrPreference VrrPreference { get; set; } = GameVrrPreference.UseGlobal;

        /// <summary>When VrrPreference is Enabled, optional override target Hz (0 = use global VrrTargetHz).</summary>
        public int VrrTargetHzOverride { get; set; } = 0;

        /// <summary>When VrrPreference is Enabled, optional FPS threshold (0 = use global VrrFpsThreshold).</summary>
        public double VrrFpsThresholdOverride { get; set; } = 0;

        /// <summary>
        /// When &gt; 0 and extension PerGameRefreshRateEnabled is on, force this display Hz for the game session
        /// (restored on stop). Independent of VRR threshold switching.
        /// </summary>
        public int ForcedRefreshRateHz { get; set; } = 0;

        /// <summary>
        /// Optional per-game exact signal timing for Scanline/Latent Sync. This is an FPS
        /// target only; it does not change the display mode. 0 = autodetect via CCD.
        /// Older settings files deserialize safely with the default value 0.
        /// </summary>
        public double ExactSyncRefreshHz { get; set; } = 0;


        /// <summary>
        /// Null / unset = use global SpecialKFrameLimiterMode.
        /// Stored as nullable via sentinel: when not overridden, property is null at runtime
        /// by using HasSpecialKLimiterModeOverride.
        /// </summary>
        public SpecialKFrameLimiterMode SpecialKLimiterMode { get; set; } = SpecialKFrameLimiterMode.Normal;

        public bool HasSpecialKLimiterModeOverride { get; set; } = false;

        /// <summary>Per-game NVIDIA Reflex mode. When not overridden, uses global SpecialKReflexMode.</summary>
        public bool HasSpecialKReflexModeOverride { get; set; } = false;
        public SpecialKReflexMode SpecialKReflexModeOverride { get; set; } = SpecialKReflexMode.LowLatency;

        /// <summary>Per-game Reflex engagement point. When not overridden, uses global SpecialKReflexTrigger.</summary>
        public bool HasSpecialKReflexTriggerOverride { get; set; } = false;
        public SpecialKReflexTrigger SpecialKReflexTriggerOverride { get; set; } = SpecialKReflexTrigger.EndOfFrame;

        /// <summary>Per-game frame pacing / energy preset. When not overridden, uses global SpecialKEnergyEfficiency.</summary>
        public bool HasSpecialKEnergyOverride { get; set; } = false;
        public SpecialKEnergyEfficiency SpecialKEnergyOverride { get; set; } = SpecialKEnergyEfficiency.MaxAccuracy;
        public double SpecialKBusyWaitPercentOverride { get; set; } = 100.0;
        public double SpecialKBusyWaitBiasOverride { get; set; } = 10.0;

        /// <summary>When true, SpecialKSteamLogSilent on this profile is used instead of global.</summary>
        public bool HasSteamLogSilentOverride { get; set; } = false;

        public bool SpecialKSteamLogSilent { get; set; } = false;

        public ModeLimitSettings GetMode(PlayniteUiMode mode)
        {
            if (Desktop == null)
            {
                Desktop = new ModeLimitSettings { SyncMode = FpsSyncMode.UseGlobal, ReflexMarkers = ReflexMarkerMode.UseGlobal };
            }

            if (Fullscreen == null)
            {
                Fullscreen = new ModeLimitSettings { SyncMode = FpsSyncMode.UseGlobal, ReflexMarkers = ReflexMarkerMode.UseGlobal };
            }

            return mode == PlayniteUiMode.Fullscreen ? Fullscreen : Desktop;
        }

        public bool HasAnyLimit()
        {
            return (Desktop != null && Desktop.Enabled) || (Fullscreen != null && Fullscreen.Enabled);
        }
    }

    public class LimitSessionSnapshot
    {
        public Guid GameId { get; set; }
        public string GameName { get; set; }
        public string ExecutablePath { get; set; }
        public string ProfileName { get; set; }
        public double AppliedLimit { get; set; }
        public FpsSyncMode AppliedSyncMode { get; set; }
        // Nullable so sessions saved by older builds cause one fresh Scanline target apply.
        public int? AppliedScanlineTarget { get; set; }
        public bool AppliedReflexMarkersDisabled { get; set; }
        public bool? AppliedForceFlipModel { get; set; }
        public bool AppliedFlipOnly { get; set; }
        public bool UsesGlobalProfile { get; set; }
        public bool ProfileExisted { get; set; }
        public bool OriginalLimitAvailable { get; set; }
        public int OriginalLimit { get; set; }
        public bool OriginalAppDetectionLevelAvailable { get; set; }
        public int OriginalAppDetectionLevel { get; set; }
        public bool FileFallbackUsed { get; set; }
        public string ProfileFilePath { get; set; }
        public bool OriginalProfileFileExisted { get; set; }
        public string OriginalProfileFileContent { get; set; }
        public bool StartedRtssProcess { get; set; }
        public DateTime StartedAt { get; set; }

        // VRR refresh rate tracking
        public bool RefreshRateChanged { get; set; }
        public int OriginalRefreshRate { get; set; }
        /// <summary>Display whose refresh rate was switched (null = primary, for older sessions).</summary>
        public string RefreshDeviceName { get; set; }

        /// <summary>
        /// Which limiter backend owns this session. Defaults to RTSS for sessions created by
        /// older builds that did not store this field.
        /// </summary>
        public LimiterBackendKind Backend { get; set; } = LimiterBackendKind.Rtss;
    }

    /// <summary>
    /// A global keyboard shortcut that applies (or disables) an FPS cap on whichever game
    /// currently has an active RTSS session, falling back to the game selected in the library.
    /// </summary>
    public class HotkeyBinding : ObservableObject
    {
        private bool enabled = true;
        private HotkeyAction action = HotkeyAction.ApplyFpsLimit;
        private bool disableCap = false; // kept for backward-compatible settings deserialization
        private double frameLimit = 60;
        private string frameLimitText = FormatFrameLimit(60);
        private FpsSyncMode syncMode = FpsSyncMode.Async;
        private ModifierKeys modifiers = ModifierKeys.Control | ModifierKeys.Alt;
        private Key key = Key.None;

        public bool Enabled
        {
            get => enabled;
            set => SetValue(ref enabled, value);
        }

        /// <summary>What this hotkey does when pressed.</summary>
        public HotkeyAction Action
        {
            get => action;
            set
            {
                if (action == value)
                {
                    return;
                }

                action = value;
                OnPropertyChanged(nameof(Action));
                // Keep the legacy flag in sync so older code paths / serialization stay consistent.
                disableCap = value == HotkeyAction.DisableCap;
                OnPropertyChanged(nameof(DisableCap));
                OnPropertyChanged(nameof(IsFpsLimitAction));
                OnPropertyChanged(nameof(IsSyncModeAction));
                OnPropertyChanged(nameof(ActionSummary));
            }
        }

        /// <summary>
        /// Legacy property kept so existing saved settings that only stored <c>DisableCap</c>
        /// still load correctly. Prefer <see cref="Action"/> for new code.
        /// </summary>
        public bool DisableCap
        {
            get => disableCap || action == HotkeyAction.DisableCap;
            set
            {
                disableCap = value;
                if (value)
                {
                    Action = HotkeyAction.DisableCap;
                }
                else if (action == HotkeyAction.DisableCap)
                {
                    Action = HotkeyAction.ApplyFpsLimit;
                }
                OnPropertyChanged(nameof(DisableCap));
            }
        }

        public double FrameLimit
        {
            get => frameLimit;
            set
            {
                if (frameLimit.Equals(value))
                {
                    return;
                }

                frameLimit = value;
                OnPropertyChanged(nameof(FrameLimit));

                var formatted = FormatFrameLimit(value);
                if (!string.Equals(frameLimitText, formatted, StringComparison.Ordinal))
                {
                    frameLimitText = formatted;
                    OnPropertyChanged(nameof(FrameLimitText));
                }

                OnPropertyChanged(nameof(ActionSummary));
            }
        }

        /// <summary>
        /// Text-editable proxy for <see cref="FrameLimit"/>. The hotkey FPS textbox binds to this
        /// instead of FrameLimit directly, because WPF's default numeric value converter parses
        /// using the current culture and rejects intermediate keystrokes like "59." while typing a
        /// decimal such as "59.9", making it look like fractional caps can't be entered at all.
        /// Parsing here always uses invariant culture (so "." is the decimal separator regardless of
        /// Windows locale) and only pushes a value into FrameLimit once it's a complete, valid number,
        /// so partial input while typing is never rejected or reformatted out from under the user.
        /// </summary>
        public string FrameLimitText
        {
            get => frameLimitText;
            set
            {
                if (string.Equals(frameLimitText, value, StringComparison.Ordinal))
                {
                    return;
                }

                frameLimitText = value;
                OnPropertyChanged(nameof(FrameLimitText));

                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                    parsed > 0 && parsed <= 1000)
                {
                    frameLimit = parsed;
                    OnPropertyChanged(nameof(FrameLimit));
                    OnPropertyChanged(nameof(ActionSummary));
                }
            }
        }

        /// <summary>Sync mode applied when <see cref="Action"/> is <see cref="HotkeyAction.SetSyncMode"/>.</summary>
        public FpsSyncMode SyncMode
        {
            get => syncMode;
            set
            {
                if (syncMode == value)
                {
                    return;
                }

                // Never allow UseGlobal on a hotkey — the point of a hotkey is an explicit choice.
                if (value == FpsSyncMode.UseGlobal)
                {
                    value = FpsSyncMode.Async;
                }

                syncMode = value;
                OnPropertyChanged(nameof(SyncMode));
                OnPropertyChanged(nameof(ActionSummary));
            }
        }

        /// <summary>True when the hotkey applies an FPS number (UI shows the FPS textbox).</summary>
        public bool IsFpsLimitAction => Action == HotkeyAction.ApplyFpsLimit;

        /// <summary>True when the hotkey sets a sync mode (UI shows the sync-mode combo).</summary>
        public bool IsSyncModeAction => Action == HotkeyAction.SetSyncMode;

        /// <summary>Short human-readable description of what the hotkey does (e.g. "60 FPS", "Disable", "Front Edge Sync").</summary>
        public string ActionSummary
        {
            get
            {
                switch (Action)
                {
                    case HotkeyAction.DisableCap:
                        return "Disable cap";
                    case HotkeyAction.SetSyncMode:
                        return FpsSyncModeNames.GetDisplayName(SyncMode);
                    case HotkeyAction.ToggleFrameColorIndicator:
                        return "Toggle FCAT indicator";
                    default:
                        return FormatFrameLimit(FrameLimit) + " FPS";
                }
            }
        }

        private static string FormatFrameLimit(double value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        public ModifierKeys Modifiers
        {
            get => modifiers;
            set
            {
                SetValue(ref modifiers, value);
                OnPropertyChanged(nameof(DisplayText));
            }
        }

        public Key Key
        {
            get => key;
            set
            {
                SetValue(ref key, value);
                OnPropertyChanged(nameof(DisplayText));
            }
        }

        /// <summary>Human-readable combo, e.g. "Ctrl + Alt + F1". Not persisted; derived from Modifiers/Key.</summary>
        public string DisplayText
        {
            get
            {
                if (Key == Key.None)
                {
                    return "Click, then press a key combo...";
                }

                var parts = new List<string>();
                if (Modifiers.HasFlag(ModifierKeys.Control))
                {
                    parts.Add("Ctrl");
                }

                if (Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    parts.Add("Alt");
                }

                if (Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    parts.Add("Shift");
                }

                if (Modifiers.HasFlag(ModifierKeys.Windows))
                {
                    parts.Add("Win");
                }

                parts.Add(KeyToDisplayString(Key));
                return string.Join(" + ", parts);
            }
        }

        private static string KeyToDisplayString(Key key)
        {
            if (key >= Key.D0 && key <= Key.D9)
            {
                return ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture);
            }

            if (key >= Key.NumPad0 && key <= Key.NumPad9)
            {
                return "Num" + (int)(key - Key.NumPad0);
            }

            return key.ToString();
        }
    }

    /// <summary>
    /// A Special K macro hotkey: pressing the combo inside a Special K game runs "TargetFPS &lt;preset&gt;".
    /// Only FPS presets can be assigned. Written to Special K's Global\macros.ini when a game launches.
    /// </summary>
    public class SpecialKMacroHotkey : ObservableObject
    {
        private ModifierKeys modifiers = ModifierKeys.Control;
        private Key key = Key.None;
        private double frameLimit = 60;

        public ModifierKeys Modifiers
        {
            get => modifiers;
            set
            {
                // Special K macros have no Windows-key modifier.
                SetValue(ref modifiers, value & ~ModifierKeys.Windows);
                OnPropertyChanged(nameof(DisplayText));
            }
        }

        public Key Key
        {
            get => key;
            set
            {
                SetValue(ref key, value);
                OnPropertyChanged(nameof(DisplayText));
            }
        }

        /// <summary>The preset FPS this hotkey applies.</summary>
        public double FrameLimit
        {
            get => frameLimit;
            set => SetValue(ref frameLimit, value);
        }

        [DontSerialize]
        public bool IsAssigned => Key != Key.None && FrameLimit > 0;

        [DontSerialize]
        public string DisplayText => Key == Key.None ? "Click, then press a key combo..." : Combo(" + ");

        /// <summary>Combo in macros.ini syntax, e.g. "Ctrl+Alt+F5" (null when unassigned).</summary>
        [DontSerialize]
        public string MacroKeyText => Key == Key.None ? null : Combo("+");

        private string Combo(string separator)
        {
            var parts = new List<string>();
            if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            parts.Add(KeyName(Key));
            return string.Join(separator, parts);
        }

        private static string KeyName(Key key)
        {
            if (key >= Key.D0 && key <= Key.D9) return ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture);
            if (key == Key.Oem5 || key == Key.OemBackslash) return "\\";
            return key.ToString();
        }
    }

    /// <summary>Display-friendly wrapper for a FpsSyncMode value, used by settings-page combo boxes.</summary>
    public class SyncModeOption
    {
        public FpsSyncMode Value { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
    }

    /// <summary>Display-friendly wrapper for a ReflexMarkerMode value, used by settings-page combo boxes.</summary>
    public class ReflexMarkerOption
    {
        public ReflexMarkerMode Value { get; set; }
        public string DisplayName { get; set; }
    }

    /// <summary>Display-friendly wrapper for a HotkeyAction value, used by the hotkey settings combo box.</summary>
    public class HotkeyActionOption
    {
        public HotkeyAction Value { get; set; }
        public string DisplayName { get; set; }
    }

    public class FPSLimiterSettingsViewModel : ObservableObject, ISettings
    {
        private readonly FPSLimiter plugin;
        private readonly IPlayniteAPI playniteApi;
        private FPSLimiterSettings editingClone;
        private FPSLimiterSettings settings;

        public RelayCommand TestRtssProfilePermissionsCommand { get; private set; }
        public RelayCommand FixRtssProfilePermissionsCommand { get; private set; }
        public RelayCommand AddSpecialKMacroCommand { get; private set; }
        public RelayCommand<SpecialKMacroHotkey> RemoveSpecialKMacroCommand { get; private set; }

        /// <summary>
        /// Presets offered for Special K macro hotkeys: those of the display set to run games
        /// (primary display when none is set). Already-assigned values stay listed.
        /// </summary>
        public List<double> SpecialKPresetOptions
        {
            get
            {
                string device = null;
                var preferred = Settings?.PreferredGameMonitorDeviceName;
                if (!string.IsNullOrWhiteSpace(preferred) && MonitorManager.GetDisplays().Any(d =>
                        string.Equals(d.DeviceName, preferred, StringComparison.OrdinalIgnoreCase)))
                    device = preferred;
                var list = Settings.GetPresetValuesForMonitor(device);
                foreach (var hotkey in Settings.SpecialKMacroHotkeys)
                {
                    if (hotkey.FrameLimit > 0 && !list.Any(v => Math.Abs(v - hotkey.FrameLimit) < 0.001))
                        list.Add(hotkey.FrameLimit);
                }
                return list;
            }
        }

        public RelayCommand AddHotkeyCommand { get; private set; }
        public RelayCommand<HotkeyBinding> RemoveHotkeyCommand { get; private set; }
        public RelayCommand RefreshVrrSafeCapCommand { get; private set; }
        public RelayCommand AddVrrSafeCapToPresetsCommand { get; private set; }
        public RelayCommand<MonitorGlobalSettings> ApplyVrrSafeCapToMonitorCommand { get; private set; }
        public RelayCommand RefreshDetectedMonitorsCommand { get; private set; }
        public ObservableCollection<MonitorGlobalSettings> DetectedMonitors { get; } = new ObservableCollection<MonitorGlobalSettings>();
        public ObservableCollection<MonitorResolutionEditor> MonitorResolutionEditors { get; } = new ObservableCollection<MonitorResolutionEditor>();
        /// <summary>Combo options for Preferred monitor for games (includes Primary / Auto).</summary>
        public ObservableCollection<DetectedDisplay> PreferredMonitorOptions { get; } = new ObservableCollection<DetectedDisplay>();

        /// <summary>Shows the preferred-monitor selector only when more than one display is connected.</summary>
        public bool HasMultipleMonitors => DetectedMonitors.Count > 1;

        /// <summary>Options for the Global FPS limit sync-mode combo boxes (Async / Front Edge / Back Edge / Reflex).</summary>
        public List<SyncModeOption> SyncModeOptions { get; } = ((FpsSyncMode[])Enum.GetValues(typeof(FpsSyncMode)))
            .Where(mode => mode != FpsSyncMode.UseGlobal)
            .Select(mode => new SyncModeOption
            {
                Value = mode,
                DisplayName = FpsSyncModeNames.GetMethodName(mode),
                Description = FpsSyncModeNames.GetDropdownDescription(mode)
            })
            .ToList();

        /// <summary>Options for the Global Reflex marker injection combo boxes (Enabled / Disabled).</summary>
        public List<ReflexMarkerOption> ReflexMarkerOptions { get; } = ((ReflexMarkerMode[])Enum.GetValues(typeof(ReflexMarkerMode)))
            .Where(mode => mode != ReflexMarkerMode.UseGlobal)
            .Select(mode => new ReflexMarkerOption { Value = mode, DisplayName = ReflexMarkerModeNames.GetDisplayName(mode) })
            .ToList();

        /// <summary>Options for the hotkey action combo box (Apply FPS / Disable / Set sync mode).</summary>
        public List<HotkeyActionOption> HotkeyActionOptions { get; } = new List<HotkeyActionOption>
        {
            new HotkeyActionOption { Value = HotkeyAction.ApplyFpsLimit, DisplayName = "Apply FPS cap" },
            new HotkeyActionOption { Value = HotkeyAction.DisableCap, DisplayName = "Disable cap" },
            new HotkeyActionOption { Value = HotkeyAction.SetSyncMode, DisplayName = "Set sync mode" },
            new HotkeyActionOption { Value = HotkeyAction.ToggleFrameColorIndicator, DisplayName = "Toggle RTSS FCAT indicator" },
        };

        private double vrrSafeCapValue;
        private string vrrSafeCapPreview;

        /// <summary>The last-computed Blur Busters VRR-safe cap (0 if the refresh rate couldn't be detected).</summary>
        public double VrrSafeCapValue
        {
            get => vrrSafeCapValue;
            private set => SetValue(ref vrrSafeCapValue, value);
        }

        /// <summary>Human-readable summary of the detected refresh rate and its VRR-safe cap.</summary>
        public string VrrSafeCapPreview
        {
            get => vrrSafeCapPreview;
            private set => SetValue(ref vrrSafeCapPreview, value);
        }

        public FPSLimiterSettings Settings
        {
            get => settings;
            set
            {
                settings = value;
                OnPropertyChanged();
            }
        }

        public FPSLimiterSettingsViewModel(FPSLimiter plugin, IPlayniteAPI playniteApi)
        {
            this.plugin = plugin;
            this.playniteApi = playniteApi;

            var savedSettings = plugin.LoadPluginSettings<FPSLimiterSettings>();
            Settings = savedSettings ?? new FPSLimiterSettings();
            MigrateLegacyHotkeys(Settings);
            PopulateDetectedRtssPathIfEmpty();

            TestRtssProfilePermissionsCommand = new RelayCommand(TestRtssProfilePermissions);
            FixRtssProfilePermissionsCommand = new RelayCommand(FixRtssProfilePermissions);
            AddSpecialKMacroCommand = new RelayCommand(AddSpecialKMacro);
            RemoveSpecialKMacroCommand = new RelayCommand<SpecialKMacroHotkey>(RemoveSpecialKMacro);
            AddHotkeyCommand = new RelayCommand(AddHotkey);
            RemoveHotkeyCommand = new RelayCommand<HotkeyBinding>(RemoveHotkey);
            RefreshVrrSafeCapCommand = new RelayCommand(RecalculateVrrSafeCap);
            AddVrrSafeCapToPresetsCommand = new RelayCommand(AddVrrSafeCapToPresets);
            ApplyVrrSafeCapToMonitorCommand = new RelayCommand<MonitorGlobalSettings>(ApplyVrrSafeCapToMonitor);
            RefreshDetectedMonitorsCommand = new RelayCommand(RefreshDetectedMonitors);
            RefreshDetectedMonitors();

            RecalculateVrrSafeCap();
        }

        private void RefreshDetectedMonitors()
        {
            // Keep the chosen resolution when the user presses Refresh monitors.
            var selections = MonitorResolutionEditors
                .Where(e => e.SelectedProfile != null && !string.IsNullOrEmpty(e.DeviceName))
                .GroupBy(e => e.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().SelectedProfile.Key, StringComparer.OrdinalIgnoreCase);
            MonitorResolutionEditors.Clear();
            DetectedMonitors.Clear();
            PreferredMonitorOptions.Clear();
            PreferredMonitorOptions.Add(new DetectedDisplay
            {
                DeviceName = string.Empty,
                Label = "Windows primary display (default)"
            });
            var mode = playniteApi.ApplicationInfo.Mode == ApplicationMode.Fullscreen
                ? PlayniteUiMode.Fullscreen : PlayniteUiMode.Desktop;
            foreach (var monitor in Settings.EnsureDetectedMonitors(mode))
            {
                Settings.EnsureSupportedResolutions(monitor);
                DetectedMonitors.Add(monitor);
                string preferredKey;
                selections.TryGetValue(monitor.DeviceName ?? "", out preferredKey);
                var editor = new MonitorResolutionEditor(monitor, preferredKey, Settings);
                editor.PropertyChanged += (sender, args) =>
                {
                    if (args.PropertyName == nameof(MonitorResolutionEditor.IsFixedRefresh))
                        OnPropertyChanged(nameof(HasFixedRefreshMonitor));
                    if (args.PropertyName == nameof(MonitorResolutionEditor.CustomName))
                    {
                        var option = PreferredMonitorOptions.FirstOrDefault(o =>
                            string.Equals(o.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase));
                        if (option != null)
                        {
                            option.Label = !string.IsNullOrWhiteSpace(monitor.CustomName)
                                ? monitor.CustomName.Trim()
                                : monitor.DisplayName ?? (monitor.DeviceName ?? "Monitor");
                        }
                    }
                };
                MonitorResolutionEditors.Add(editor);

                var detected = MonitorManager.GetDisplays()
                    .FirstOrDefault(d => string.Equals(d.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase));
                PreferredMonitorOptions.Add(new DetectedDisplay
                {
                    DeviceName = monitor.DeviceName ?? string.Empty,
                    Label = !string.IsNullOrWhiteSpace(monitor.CustomName)
                        ? monitor.CustomName.Trim()
                        : detected?.Label ?? (monitor.DeviceName ?? "Monitor")
                });
            }

            // If the saved preference is no longer connected, fall back to primary.
            if (!string.IsNullOrWhiteSpace(Settings.PreferredGameMonitorDeviceName) &&
                !PreferredMonitorOptions.Any(o =>
                    !string.IsNullOrEmpty(o.DeviceName) &&
                    string.Equals(o.DeviceName, Settings.PreferredGameMonitorDeviceName, StringComparison.OrdinalIgnoreCase)))
            {
                Settings.PreferredGameMonitorDeviceName = string.Empty;
            }

            OnPropertyChanged(nameof(HasFixedRefreshMonitor));
            OnPropertyChanged(nameof(HasMultipleMonitors));
            OnPropertyChanged(nameof(PreferredMonitorOptions));
        }

        /// <summary>True when at least one detected monitor is set to the Fixed refresh display type.</summary>
        public bool HasFixedRefreshMonitor
        {
            get { return DetectedMonitors.Any(m => m != null && m.FixedRefreshDisplayMode); }
        }

        private void RecalculateVrrSafeCap()
        {
            var hz = RefreshRateManager.GetCurrentPreciseRefreshRate();
            if (hz <= 0)
            {
                VrrSafeCapValue = 0;
                VrrSafeCapPreview = "Could not detect the current display refresh rate.";
                return;
            }

            VrrSafeCapValue = Math.Round(RefreshRateManager.GetVrrSafeCap(hz), 2, MidpointRounding.AwayFromZero);
            VrrSafeCapPreview = $"Primary: {hz} Hz \u2192 {FormatFps(VrrSafeCapValue)} FPS. Monitor buttons use each screen's Hz.";
        }

        private void AddVrrSafeCapToPresets()
        {
            if (VrrSafeCapValue <= 0)
            {
                return;
            }

            var values = Settings.GetPresetValues();
            if (!values.Any(v => Math.Abs(v - VrrSafeCapValue) < 0.001))
            {
                values.Add(VrrSafeCapValue);
            }

            Settings.PresetsText = string.Join(", ", values.OrderBy(v => v).Select(FormatFps));
        }

        /// <summary>Applies the VRR-safe cap to the Windows games profile on one monitor.</summary>
        private void ApplyVrrSafeCapToMonitor(MonitorGlobalSettings monitor)
        {
            if (VrrSafeCapValue <= 0 || monitor == null)
                return;
            // Re-read the selected monitor's refresh rate instead of assuming primary Hz.
            var hz = MonitorManager.GetPreciseRefreshRate(monitor.DeviceName);
            if (hz <= 0) return;
            var cap = Math.Round(RefreshRateManager.GetVrrSafeCap(hz), 2, MidpointRounding.AwayFromZero);
            var mode = playniteApi.ApplicationInfo.Mode == ApplicationMode.Fullscreen
                ? PlayniteUiMode.Fullscreen : PlayniteUiMode.Desktop;
            var limit = Settings.GetGlobalForMonitor(monitor.DeviceName, true, mode);
            limit.FrameLimit = cap;
            limit.Enabled = true;
        }

        private void AddSpecialKMacro()
        {
            var first = SpecialKPresetOptions.FirstOrDefault();
            Settings.SpecialKMacroHotkeys.Add(new SpecialKMacroHotkey { FrameLimit = first > 0 ? first : 60 });
        }

        private void RemoveSpecialKMacro(SpecialKMacroHotkey macro)
        {
            if (macro != null) Settings.SpecialKMacroHotkeys.Remove(macro);
        }

        private void AddHotkey()
        {
            Settings.Hotkeys.Add(new HotkeyBinding { Key = Key.None });
        }

        private void RemoveHotkey(HotkeyBinding binding)
        {
            if (binding != null)
            {
                Settings.Hotkeys.Remove(binding);
            }
        }

        public void SaveSettings()
        {
            plugin.SaveSettingsSafely(Settings);
        }

        public System.Threading.Tasks.Task UpdateSpecialKNowAsync() => plugin.UpdateSpecialKNowAsync();

        public System.Threading.Tasks.Task CheckSpecialKUpdatesNowAsync()
        {
            return plugin.CheckSpecialKUpdatesNowAsync();
        }

        public System.Threading.Tasks.Task CheckExtensionUpdatesNowAsync() => plugin.CheckExtensionUpdatesNowAsync();

        public System.Threading.Tasks.Task UpdateExtensionNowAsync() => plugin.UpdateExtensionNowAsync();

        public void BeginEdit()
        {
            PopulateDetectedRtssPathIfEmpty();
            RefreshDetectedMonitors();
            editingClone = Serialization.GetClone(Settings);
        }

        public void CancelEdit()
        {
            Settings = editingClone;
            RefreshDetectedMonitors();
        }

        public void EndEdit()
        {
            NormalizeSettings();
            SaveSettings();
            plugin.RefreshHotkeys();
            plugin.ApplyEditedGlobalSettings();
            // Push toggles into Special K Global\default_SpecialK.ini
            try { plugin.SyncSpecialKGlobalDefaults(); } catch { }
        }

        private static void ValidateGlobalLimit(List<string> errors, ModeLimitSettings limit, string label)
        {
            if (limit != null &&
                limit.Enabled &&
                (limit.FrameLimit <= 0 || limit.FrameLimit > 1000))
            {
                errors.Add($"Global FPS limit ({label}): enter an FPS value between 1 and 1000, or uncheck Enabled.");
            }
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();

            if (!string.IsNullOrWhiteSpace(Settings.RtssPath) &&
                (!File.Exists(Settings.RtssPath) ||
                 !string.Equals(Path.GetFileName(Settings.RtssPath), "RTSS.exe", StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add("RTSS path must point to RTSS.exe, or be left blank for auto-detection.");
            }

            if (!Settings.GetPresetValues().Any())
            {
                errors.Add("Add at least one FPS preset between 1 and 1000.");
            }

            foreach (var editor in MonitorResolutionEditors)
            {
                if (editor.HasInvalidExactSyncRefreshHz)
                    errors.Add("Exact Sync (" + editor.DisplayName + ", " + editor.SelectedRefreshRateHz + " Hz): enter 10–1000 Hz (e.g. 29.9001), or leave blank for Auto.");
            }

            foreach (var monitor in DetectedMonitors)
            {
                ValidateGlobalLimit(errors, monitor.GetLimit(true), monitor.DisplayName + " · Windows games");
                ValidateGlobalLimit(errors, monitor.GetLimit(false), monitor.DisplayName + " · Non-Windows games");
                foreach (var resolution in (monitor.Resolutions ?? new List<ResolutionGlobalSettings>())
                    .Where(r => r != null && r.UseOverride))
                {
                    ValidateGlobalLimit(errors, resolution.GetLimit(true), monitor.DisplayName + " · " + resolution.DisplayName + " · Windows games");
                    ValidateGlobalLimit(errors, resolution.GetLimit(false), monitor.DisplayName + " · " + resolution.DisplayName + " · Non-Windows games");
                }
            }

            foreach (var profile in Settings.GameLimits)
            {
                if (!string.IsNullOrWhiteSpace(profile.ManualExecutablePath) &&
                    !File.Exists(profile.ManualExecutablePath))
                {
                    errors.Add($"Manual executable path no longer exists: {profile.ManualExecutablePath}");
                }
            }

            foreach (var hotkey in Settings.Hotkeys)
            {
                if (hotkey.Key != Key.None && !hotkey.DisableCap &&
                    (hotkey.FrameLimit <= 0 || hotkey.FrameLimit > 1000))
                {
                    errors.Add($"Hotkey {hotkey.DisplayText}: enter an FPS value between 1 and 1000, or check \"Disable cap\".");
                }
            }

            var duplicateCombo = Settings.Hotkeys
                .Where(a => a.Key != Key.None)
                .GroupBy(a => new { a.Modifiers, a.Key })
                .FirstOrDefault(g => g.Count() > 1);
            if (duplicateCombo != null)
            {
                errors.Add($"The hotkey {duplicateCombo.First().DisplayText} is assigned more than once.");
            }

            var macros = Settings.SpecialKMacroHotkeys.Where(a => a.Key != Key.None).ToList();
            foreach (var macro in macros)
            {
                var combo = macro.MacroKeyText ?? string.Empty;
                if (macro.FrameLimit <= 0 || macro.FrameLimit > 1000)
                    errors.Add($"Special K hotkey {macro.DisplayText}: pick an FPS preset.");
                if (combo.StartsWith("Ctrl+Shift+", StringComparison.OrdinalIgnoreCase) && combo.Length == "Ctrl+Shift+".Length + 1 &&
                    char.IsDigit(combo[combo.Length - 1]))
                    errors.Add($"Special K hotkey {macro.DisplayText} is reserved for live preset switching (Ctrl + Shift + 0-9).");
            }

            var duplicateMacro = macros.GroupBy(a => a.MacroKeyText ?? string.Empty).FirstOrDefault(g => g.Count() > 1);
            if (duplicateMacro != null)
            {
                errors.Add($"The Special K hotkey {duplicateMacro.First().DisplayText} is assigned more than once.");
            }

            return !errors.Any();
        }

        private void NormalizeSettings()
        {
            Settings.PresetsText = string.Join(", ", Settings.GetPresetValues().Select(FormatFps));
            Settings.RtssPath = Settings.RtssPath?.Trim() ?? string.Empty;
            Settings.SpecialKPath = Settings.SpecialKPath?.Trim() ?? string.Empty;
            Settings.GlobalDesktop.FrameLimitText = FormatFps(Settings.GlobalDesktop.FrameLimit);
            Settings.GlobalDesktopNonWindows.FrameLimitText = FormatFps(Settings.GlobalDesktopNonWindows.FrameLimit);
            Settings.GlobalFullscreen.FrameLimitText = FormatFps(Settings.GlobalFullscreen.FrameLimit);
            Settings.GlobalFullscreenNonWindows.FrameLimitText = FormatFps(Settings.GlobalFullscreenNonWindows.FrameLimit);
            foreach (var monitor in Settings.MonitorGlobals.Where(m => m != null))
            {
                monitor.GetLimit(true).FrameLimitText = FormatFps(monitor.GetLimit(true).FrameLimit);
                monitor.GetLimit(false).FrameLimitText = FormatFps(monitor.GetLimit(false).FrameLimit);
                foreach (var resolution in (monitor.Resolutions ?? new List<ResolutionGlobalSettings>())
                    .Where(r => r != null))
                {
                    resolution.GetLimit(true).FrameLimitText = FormatFps(resolution.GetLimit(true).FrameLimit);
                    resolution.GetLimit(false).FrameLimitText = FormatFps(resolution.GetLimit(false).FrameLimit);
                }
            }

            foreach (var hotkey in Settings.Hotkeys)
            {
                hotkey.FrameLimitText = FormatFps(hotkey.FrameLimit);
                if (hotkey.SyncMode == FpsSyncMode.UseGlobal)
                {
                    hotkey.SyncMode = FpsSyncMode.Async;
                }
            }
        }

        /// <summary>
        /// Older builds only stored a <c>DisableCap</c> boolean on hotkeys. Map that onto the
        /// newer <see cref="HotkeyAction"/> enum so existing user configs keep working.
        /// </summary>
        private static void MigrateLegacyHotkeys(FPSLimiterSettings settings)
        {
            if (settings?.Hotkeys == null)
            {
                return;
            }

            foreach (var hotkey in settings.Hotkeys)
            {
                // Hotkeys no longer have per-row enable switches; keep every saved shortcut active.
                hotkey.Enabled = true;

                // If the saved JSON never had an Action field, it defaults to ApplyFpsLimit.
                // Honour the old DisableCap flag in that case.
                if (hotkey.DisableCap && hotkey.Action == HotkeyAction.ApplyFpsLimit)
                {
                    hotkey.Action = HotkeyAction.DisableCap;
                }

                if (hotkey.SyncMode == FpsSyncMode.UseGlobal)
                {
                    hotkey.SyncMode = FpsSyncMode.Async;
                }
            }
        }

        private static string FormatFps(double value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private void PopulateDetectedRtssPathIfEmpty()
        {
            if (!string.IsNullOrWhiteSpace(Settings.RtssPath))
            {
                return;
            }

            try
            {
                var detectedPath = new RtssLimiterBackend(Settings).ResolveRtssPath();
                if (!string.IsNullOrWhiteSpace(detectedPath))
                {
                    Settings.RtssPath = detectedPath;
                }
            }
            catch
            {
            }
        }

        private void TestRtssProfilePermissions()
        {
            try
            {
                NormalizeSettings();
                var backend = new RtssLimiterBackend(Settings);
                var result = backend.TestProfileWriteAccess();

                if (result.Success)
                {
                    playniteApi.Dialogs.ShowMessage(
                        $"FPS Limiter 3000 can write to the RTSS Profiles folder.\n\n{result.ProfilesDirectory}",
                        "FPS Limiter 3000");
                }
                else
                {
                    playniteApi.Dialogs.ShowErrorMessage(
                        $"FPS Limiter 3000 cannot write to the RTSS Profiles folder yet.\n\n{result.Message}",
                        "FPS Limiter 3000");
                }
            }
            catch (Exception e)
            {
                playniteApi.Dialogs.ShowErrorMessage(
                    $"FPS Limiter 3000 could not test RTSS profile access.\n\n{e.Message}",
                    "FPS Limiter 3000");
            }
        }

        private void FixRtssProfilePermissions()
        {
            try
            {
                NormalizeSettings();
                var backend = new RtssLimiterBackend(Settings);
                var profilesDirectory = backend.ResolveProfilesDirectory();

                var response = playniteApi.Dialogs.ShowMessage(
                    $"FPS Limiter 3000 will request administrator approval once, then grant your current Windows user Modify access to this RTSS folder only:\n\n{profilesDirectory}\n\nContinue?",
                    "FPS Limiter 3000",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (response != MessageBoxResult.Yes)
                {
                    return;
                }

                backend.GrantProfilesModifyPermissionToCurrentUser();
                var result = backend.TestProfileWriteAccess();

                if (result.Success)
                {
                    playniteApi.Dialogs.ShowMessage(
                        $"RTSS profile permissions are ready.\n\n{result.ProfilesDirectory}",
                        "FPS Limiter 3000");
                }
                else
                {
                    playniteApi.Dialogs.ShowErrorMessage(
                        $"The permission command completed, but the write test still failed.\n\n{result.Message}",
                        "FPS Limiter 3000");
                }
            }
            catch (Exception e)
            {
                playniteApi.Dialogs.ShowErrorMessage(
                    $"FPS Limiter 3000 could not fix RTSS profile permissions.\n\n{e.Message}",
                    "FPS Limiter 3000");
            }
        }
    }
}
