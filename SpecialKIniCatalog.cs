using System;
using System.Collections.Generic;
using System.Linq;

namespace FPSLimiter
{
    /// <summary>One documented SpecialK.ini setting, used by the editor to describe it and offer fixed values.</summary>
    internal sealed class SpecialKIniOption
    {
        public string Section { get; set; }
        public string Key { get; set; }

        /// <summary>Value Special K uses when the key is absent (shown as the starting value when added).</summary>
        public string DefaultValue { get; set; }

        public string Description { get; set; }

        /// <summary>Fixed values for a drop-down (empty = free input / plain switch).</summary>
        public IList<string> Choices { get; set; } = new List<string>();

        /// <summary>True for the keys FPS Limiter itself writes from its settings.</summary>
        public bool ManagedByExtension { get; set; }

        /// <summary>Position in the recommended list (0 = first); recommended options are listed above the rest.</summary>
        public int Rank { get; set; } = int.MaxValue;

        public bool IsRecommended => Rank != int.MaxValue;

        public string Id => SpecialKIniCatalog.IdOf(Section, Key);

        /// <summary>Text shown in the "Add known option" list.</summary>
        public string Label => Section + " / " + Key;
    }

    /// <summary>
    /// The SpecialK.ini settings FPS Limiter knows about. Special K writes many more keys than this into
    /// a profile; those are still shown in the editor (as plain rows without a description).
    /// </summary>
    internal static class SpecialKIniCatalog
    {
        private static readonly string[] TrueFalse = { "true", "false" };

        public static string IdOf(string section, string key)
        {
            return ((section ?? string.Empty).Trim() + "|" + (key ?? string.Empty).Trim()).ToLowerInvariant();
        }

        private static SpecialKIniOption O(string section, string key, string def, string description,
            bool managed = false, params string[] choices)
        {
            return new SpecialKIniOption
            {
                Section = section,
                Key = key,
                DefaultValue = def,
                Description = description,
                ManagedByExtension = managed,
                Choices = choices ?? new string[0]
            };
        }

        private const string ManagedNote =
            "\n\nFPS Limiter 3000 also writes this key (global setting, per-game override, or when it applies its " +
            "settings to existing profiles), so a manual change can be replaced before the next launch.";

        private static readonly List<SpecialKIniOption> all = Build();

        public static IList<SpecialKIniOption> All => all;

        private static List<SpecialKIniOption> Build()
        {
            var list = new List<SpecialKIniOption>
            {
                // --- Frame rate -------------------------------------------------------------------
                O("Render.FrameRate", "TargetFPS", "0.0",
                    "Frame rate cap in frames per second. 0 means unlimited. Fractional values such as 59.94 are accepted." +
                    "\n\nFPS Limiter 3000 rewrites this before launch when a cap is set for the game.", true),
                O("Render.FrameRate", "LimitEnforcementPolicy", "4",
                    "Where in the frame Special K enforces the cap. FPS Limiter 3000 uses 4 for Normal, 2 for Low-Latency " +
                    "(also used for NVIDIA Reflex) and -4 for Latent Sync." + ManagedNote, true, "4", "2", "-4"),
                O("Render.FrameRate", "PresentationInterval", "-1",
                    "Vertical sync interval. -1 lets the game decide, 0 turns V-Sync off (used by Latent Sync), " +
                    "1 and above wait for that many refreshes." + ManagedNote, true, "-1", "0", "1", "2"),
                O("Render.FrameRate", "BusyWaitPercent", "-1.0",
                    "How much of the wait before a frame is spent spinning instead of sleeping. -1 uses Special K's " +
                    "default. Higher is more accurate but uses more CPU (Power Saving 9, Max. Accuracy 100)." + ManagedNote, true),
                O("Render.FrameRate", "BusyWaitBias", "-1.0",
                    "Bias for the busy-wait calculation. -1 uses Special K's default (Power Saving 0.32, Max. Accuracy 10)." + ManagedNote, true),
                O("Render.FrameRate", "UseAMDMWAITX", "false",
                    "Use the AMD MWAITX instruction for waiting on supported Ryzen CPUs." + ManagedNote, true, TrueFalse),
                O("Render.FrameRate", "PreRenderLimit", "-1",
                    "Maximum number of frames the CPU may queue ahead of the GPU. -1 keeps the game's own value; lower is less latency."),
                O("Render.FrameRate", "BackBufferCount", "-1",
                    "Number of swap chain back buffers. -1 keeps the game's own value."),
                O("Render.FrameRate", "SleeplessRenderThread", "false",
                    "Stops the render thread from sleeping while limiting. Lower latency, more CPU use.", false, TrueFalse),
                O("Render.FrameRate", "SleeplessWindowThread", "false",
                    "Stops the window thread from sleeping while limiting. Lower input latency, more CPU use.", false, TrueFalse),

                // --- NVIDIA Reflex ----------------------------------------------------------------
                O("NVIDIA.Reflex", "Enable", "false",
                    "Turns NVIDIA Reflex on. Only works in games and on graphics cards that support it." + ManagedNote, true, TrueFalse),
                O("NVIDIA.Reflex", "LowLatency", "false",
                    "Reflex Low Latency mode." + ManagedNote, true, TrueFalse),
                O("NVIDIA.Reflex", "LowLatencyBoost", "false",
                    "Reflex Low Latency + Boost: keeps the GPU clocks high to cut latency further." + ManagedNote, true, TrueFalse),
                O("NVIDIA.Reflex", "UseFramerateLimiter", "false",
                    "Let Reflex enforce the frame rate cap (the NVIDIA Reflex limiter behavior)." + ManagedNote, true, TrueFalse),
                O("NVIDIA.Reflex", "EngagementPolicy", "0",
                    "When Reflex engages. FPS Limiter 3000 writes 0 for End of Frame (smoother frame pacing) and 1 for " +
                    "Start of Frame (lower latency)." + ManagedNote, true, "0", "1"),
                O("NVIDIA.Reflex", "OverrideNativeMode", "true",
                    "Lets Special K override the Reflex mode the game itself requests." + ManagedNote, true, TrueFalse),
                O("NVIDIA.Reflex", "DisableNative", "true",
                    "Disables the game's own native Reflex calls so Special K's Reflex settings apply." + ManagedNote, true, TrueFalse),
                O("NVIDIA.API", "EnableVulkanBridge", "0",
                    "Enables the Vulkan bridge so Special K features (like Reflex) can work in Vulkan games. 1 = on." + ManagedNote, true, "0", "1"),

                // --- Window / display -------------------------------------------------------------
                O("Window.System", "AlwaysOnTop", "-1",
                    "Window layering. -1 No Preference, 0 Prevent Always-On-Top, 1 Force Always-On-Top, " +
                    "2 Multitasking-On-Top." + ManagedNote, true, "-1", "0", "1", "2"),
                O("Window.System", "BackgroundRender", "false",
                    "Keep rendering while the window is in the background.", false, TrueFalse),
                O("Window.System", "Borderless", "false",
                    "Run the game in a borderless window.", false, TrueFalse),
                O("Window.System", "Fullscreen", "false",
                    "Stretch the borderless window over the whole display.", false, TrueFalse),
                O("Window.System", "Center", "false",
                    "Center the game window on the display.", false, TrueFalse),
                O("Render.DXGI", "UseFlipDiscard", "false",
                    "Force the flip-discard swap chain model on DXGI games (what Windows calls the flip model)." + ManagedNote, true, TrueFalse),

                // --- Compatibility ----------------------------------------------------------------
                O("Input.Gamepad", "DisableRumble", "false",
                    "Turns controller vibration off." + ManagedNote, true, TrueFalse),
                O("Steam.Log", "Silent", "false",
                    "Silent mode: hides Special K from the Steam API log and may suppress some Steam API behavior." + ManagedNote, true, TrueFalse),
                O("Steam.System", "PreLoadSteamOverlay", "false",
                    "Load the Steam overlay early, before the game starts rendering." + ManagedNote, true, TrueFalse),
                O("Steam.System", "NoDrawOverlay", "false",
                    "Stop the Steam overlay from drawing (disables the in-game Steam overlay)." + ManagedNote, true, TrueFalse),
            };

            for (var i = 0; i < list.Count; i++)
                list[i].Rank = i;
            return list;
        }

        public static IDictionary<string, SpecialKIniOption> ById()
        {
            return all.ToDictionary(o => o.Id, o => o, StringComparer.OrdinalIgnoreCase);
        }
    }
}
