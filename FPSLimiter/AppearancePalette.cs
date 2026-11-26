using System;
using System.Windows;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Windows.Threading;
using System.Windows.Media;

namespace FPSLimiter
{
    /// <summary>Local extension colors. Never changes Playnite's application resources or fonts.</summary>
    internal static class AppearancePalette
    {
        private static readonly ConditionalWeakTable<FrameworkElement, AccentWatcher> watchers =
            new ConditionalWeakTable<FrameworkElement, AccentWatcher>();

        internal static void Apply(FrameworkElement target, string preset, bool glass = false)
        {
            bool windows = string.Equals(preset, "Windows 11", StringComparison.OrdinalIgnoreCase);
            bool steamDark = string.Equals(preset, "Steam Dark", StringComparison.OrdinalIgnoreCase);
            target.Resources["Windows11Only"] = windows ? Visibility.Visible : Visibility.Collapsed;
            target.Resources["SteamOnly"] = windows ? Visibility.Collapsed : Visibility.Visible;
            target.Resources["UiEmphasisFontWeight"] = FontWeights.Normal;
            target.Resources["GameContentMaxWidth"] = windows ? double.PositiveInfinity : 560d;
            Set(target, "ReShadeBg", windows ? "#FF202020" : "#FF171D25");
            Set(target, "ReShadePanelBg", windows ? "#FF2B2B2B" : "#FF23262E");
            Set(target, "ReShadeBorder", windows ? "#FF3F3F3F" : "#FF3D4450");
            Set(target, "ReShadeBorderHover", windows ? "#FF484848" : "#FF434953");
            Set(target, "ReShadeText", windows ? "#FFF1F1F1" : "#FFDCDEDF");
            Set(target, "ReShadeTextMuted", windows ? "#FFC6C6C6" : "#FFB8BCBF");
            Set(target, "ReShadeAccent", windows ? "#FF4CC2FF" : "#FF4A9EFF");
            Set(target, "ReShadeAccentHover", windows ? "#FF80D3FF" : "#FF6BB0FF");
            Set(target, "ReShadeAccentPress", windows ? "#FF32ACEA" : "#FF2E7AD1");
            Set(target, "ReShadeAccentSoft", windows ? "#FF303030" : "#FF1A2A40");
            Set(target, "ReShadeControlBg", windows ? "#FF373737" : "#FF2A2D34");
            Set(target, "ReShadeControlHover", windows ? "#FF414141" : "#FF3D4450");
            Set(target, "UiCard", windows ? "#FF2B2B2B" : "#FF23262E");
            Set(target, "UiCardHover", windows ? "#FF333333" : "#FF3D4450");
            Set(target, "UiHeaderHover", windows ? "#FF333333" : "#FF303641");
            Set(target, "UiExpandedHeader", windows ? "#FF303030" : "#FF344252");
            Set(target, "ComboBorderHover", windows ? "#FF5A5A5A" : "#FF535B68");
            Set(target, "ComboSelectedBg", windows ? "#FF414141" : "#FF314B68");
            Set(target, "UiBorder", windows ? "#FF303030" : "#FF23262E");
            Set(target, "UiBorderHover", windows ? "#FF454545" : "#FF434953");
            Set(target, "UiDivider", windows ? "#FF383838" : "#FF3D4450");
            Set(target, "UiAccent", windows ? "#FF4CC2FF" : "#FF4A9EFF");
            Set(target, "UiButton", windows ? "#FF373737" : "#FF2F333B");
            target.Resources["UiButtonCornerRadius"] = new CornerRadius(windows ? 4 : 2);
            target.Resources["UiGameButtonCornerRadius"] = new CornerRadius(windows ? 4 : 7);
            target.Resources["UiSectionCornerRadius"] = new CornerRadius(windows ? 4 : 7);
            target.Resources["UiSectionHeaderCornerRadius"] = new CornerRadius(windows ? 4 : 7, windows ? 4 : 7, 0, 0);
            target.Resources["UiSectionContentCornerRadius"] = new CornerRadius(0, 0, windows ? 4 : 7, windows ? 4 : 7);
            target.Resources["SwitchThumbSize"] = windows ? 12d : 16d;
            target.Resources["SwitchThumbLeftMargin"] = windows ? new Thickness(4, 0, 0, 0) : new Thickness(2, 0, 0, 0);
            target.Resources["SwitchThumbRightMargin"] = windows ? new Thickness(0, 0, 4, 0) : new Thickness(0, 0, 2, 0);
            target.Resources["GameSwitchThumbLeftMargin"] = windows ? new Thickness(4, 0, 0, 0) : new Thickness(3, 0, 0, 0);
            target.Resources["GameSwitchThumbRightMargin"] = windows ? new Thickness(0, 0, 4, 0) : new Thickness(0, 0, 3, 0);
            Set(target, "UiPrimary", windows ? "#FF4CC2FF" : "#FF2F65A0");
            Set(target, "UiPrimaryText", windows ? "#FF101010" : "#FFF1F4FA");
            Set(target, "SwitchTrackOffBrush", windows ? "#FF202020" : "#FF3D4149");
            Set(target, "SwitchTrackBorderBrush", windows ? "#FF8C8C8C" : "#003D4149");
            Set(target, "UiStatusBg", windows ? "#FF2B2B2B" : "#FF1E2A3A");
            Set(target, "SwitchTrackOnBrush", windows ? "#FF4CC2FF" : "#FF1A9FFF");
            Set(target, "SwitchThumbBrush", windows ? "#FFCFCFCF" : "#FFF5F6F7");
            Set(target, "SwitchThumbOnBrush", windows ? "#FF000000" : "#FFF5F6F7");
            Set(target, "GameSwitchTrackOff", windows ? "#FF202020" : "#FF303037");
            Set(target, "GameSwitchTrackBorder", windows ? "#FF8C8C8C" : "#FF3A3A42");
            Set(target, "GameSwitchThumb", windows ? "#FFCFCFCF" : "#FFE9E9EC");
            Set(target, "GameSwitchThumbOn", windows ? "#FF000000" : "#FFE9E9EC");

            // Steam Dark preserves the Steam layout, blue accents and typography. The
            // supplied swatch is the window/content background; surfaces remain distinct.
            if (steamDark)
            {
                Set(target, "ReShadeBg", "#FF16181D");
                Set(target, "ReShadePanelBg", "#FF202329");
                Set(target, "ReShadeBorder", "#FF393D46");
                Set(target, "ReShadeBorderHover", "#FF4A505A");
                Set(target, "ReShadeControlBg", "#FF292C33");
                Set(target, "ReShadeControlHover", "#FF373C45");
                Set(target, "UiCard", "#FF202329");
                Set(target, "UiCardHover", "#FF363C45");
                Set(target, "UiHeaderHover", "#FF2D323A");
                Set(target, "UiExpandedHeader", "#FF303E4D");
                Set(target, "UiBorder", "#FF202329");
                Set(target, "UiBorderHover", "#FF424953");
                Set(target, "UiDivider", "#FF3A4049");
                Set(target, "UiButton", "#FF2D3139");
                Set(target, "UiStatusBg", "#FF1C2633");
            }

            // Keep dropdown menus opaque, even when the surrounding surfaces use glass.
            Set(target, "UiPopupBg", windows ? "#FF2B2B2B" : steamDark ? "#FF202329" : "#FF23262E");

            if (glass)
            {
                Color bg = windows ? Color.FromRgb(0x20, 0x20, 0x20)
                         : steamDark ? Color.FromRgb(0x16, 0x18, 0x1D)
                         : Color.FromRgb(0x17, 0x1D, 0x25);
                Color glow = windows ? GetWindowsAccent() : Color.FromRgb(0x4A, 0x9E, 0xFF);
                Func<byte, Color> white = alpha => Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF);

                SetBrush(target, "ReShadeBg", Diagonal(
                    Stop(Mix(bg, glow, 0.14), 0.0),
                    Stop(Mix(bg, glow, 0.05), 0.5),
                    Stop(Mix(bg, Colors.Black, 0.10), 1.0)));

                var card = Diagonal(
                    Stop(white(0x24), 0.0),
                    Stop(white(0x14), 0.5),
                    Stop(white(0x0E), 1.0));
                SetBrush(target, "ReShadePanelBg", card);
                SetBrush(target, "UiCard", card);
                SetBrush(target, "UiStatusBg", Diagonal(Stop(white(0x1C), 0.0), Stop(white(0x10), 1.0)));
                SetBrush(target, "UiCardHover", Diagonal(Stop(white(0x30), 0.0), Stop(white(0x1E), 1.0)));
                SetBrush(target, "UiHeaderHover", Diagonal(Stop(white(0x2C), 0.0), Stop(white(0x1A), 1.0)));
                SetBrush(target, "UiExpandedHeader", Diagonal(Stop(white(0x34), 0.0), Stop(white(0x22), 1.0)));

                var control = Vertical(Stop(white(0x28), 0.0), Stop(white(0x16), 1.0));
                var controlHover = Vertical(Stop(white(0x38), 0.0), Stop(white(0x2A), 1.0));
                SetBrush(target, "ReShadeControlBg", control);
                SetBrush(target, "UiButton", control);
                SetBrush(target, "ReShadeControlHover", controlHover);

                var rim = Diagonal(
                    Stop(white(0x58), 0.0),
                    Stop(white(0x20), 0.35),
                    Stop(white(0x16), 0.7),
                    Stop(white(0x3A), 1.0));
                var rimHover = Diagonal(
                    Stop(white(0x88), 0.0),
                    Stop(white(0x3C), 0.4),
                    Stop(white(0x30), 0.7),
                    Stop(white(0x60), 1.0));
                SetBrush(target, "ReShadeBorder", rim);
                SetBrush(target, "UiBorder", rim);
                SetBrush(target, "ReShadeBorderHover", rimHover);
                SetBrush(target, "UiBorderHover", rimHover);
                SetBrush(target, "ComboBorderHover", rimHover);
                Set(target, "UiDivider", white(0x1E));

                Color popup = windows ? Color.FromRgb(0x2B, 0x2B, 0x2B)
                            : steamDark ? Color.FromRgb(0x20, 0x23, 0x29)
                            : Color.FromRgb(0x23, 0x26, 0x2E);
                Set(target, "ComboSelectedBg", Mix(popup, glow, 0.24));
            }

            // Existing FPS Limiter templates use Fps-prefixed brush names.
            target.Resources["FpsBg"] = target.Resources["ReShadeBg"];
            target.Resources["FpsPanelBg"] = target.Resources["UiCard"];
            target.Resources["FpsBorder"] = target.Resources["ReShadeBorder"];
            target.Resources["FpsText"] = target.Resources["ReShadeText"];
            target.Resources["FpsMuted"] = target.Resources["ReShadeTextMuted"];
            target.Resources["FpsAccent"] = target.Resources["ReShadeAccent"];

            // The watcher is scoped to the individual view/window; it never modifies
            // Playnite's resources and does not run while the element is unloaded.
            var watcher = watchers.GetValue(target, element => new AccentWatcher(element));
            watcher.SetEnabled(windows);
        }

        /// <summary>
        /// Per-game menu only. Its sections use the unified card, whose header is painted over the
        /// card's own glass, so the global "open header" brush stacks into a bright flat gray.
        /// Use a dim accent-tinted glass instead. Call right after <see cref="Apply"/>.
        /// </summary>
        internal static void ApplyUnifiedSectionGlass(FrameworkElement target, string preset, bool glass)
        {
            // Flat button surfaces for the FPS preset / action buttons (per-game menu).
            if (glass)
            {
                Func<byte, Color> w = a => Color.FromArgb(a, 0xFF, 0xFF, 0xFF);
                // Subtle glass: nearly even fill (tiny top-to-bottom fade) and a thin, evenly lit rim.
                SetBrush(target, "FlatButtonBg", Vertical(Stop(w(0x18), 0.0), Stop(w(0x12), 1.0)));
                SetBrush(target, "FlatButtonHoverBg", Vertical(Stop(w(0x26), 0.0), Stop(w(0x20), 1.0)));
                SetBrush(target, "FlatButtonBorder", Vertical(Stop(w(0x26), 0.0), Stop(w(0x1C), 1.0)));
                SetBrush(target, "FlatButtonBorderHover", Vertical(Stop(w(0x50), 0.0), Stop(w(0x44), 1.0)));
            }
            else
            {
                Brush Pick(string key, string fallback) =>
                    (target.TryFindResource(key) as Brush) ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
                target.Resources["FlatButtonBg"] = Pick("ReShadeControlBg", "#FF2A2D34");
                target.Resources["FlatButtonHoverBg"] = Pick("ReShadeControlHover", "#FF3D4450");
                target.Resources["FlatButtonBorder"] = Pick("ReShadeBorder", "#FF3D4450");
                target.Resources["FlatButtonBorderHover"] = Pick("ReShadeBorderHover", "#FF434953");
            }
            if (!glass) return;
            bool windows = string.Equals(preset, "Windows 11", StringComparison.OrdinalIgnoreCase);
            Color glow = windows ? GetWindowsAccent() : Color.FromRgb(0x4A, 0x9E, 0xFF);
            Func<byte, Color> tint = alpha => Color.FromArgb(alpha, glow.R, glow.G, glow.B);
            SetBrush(target, "UiExpandedHeader", Diagonal(Stop(tint(0x2A), 0.0), Stop(tint(0x10), 1.0)));
        }

        private sealed class AccentWatcher
        {
            private readonly FrameworkElement target;
            private readonly DispatcherTimer timer;
            private bool enabled;
            private Color? lastAccent;

            internal AccentWatcher(FrameworkElement target)
            {
                this.target = target;
                timer = new DispatcherTimer(DispatcherPriority.Background, target.Dispatcher)
                {
                    Interval = TimeSpan.FromSeconds(2)
                };
                timer.Tick += (_, __) => Refresh();
                target.Loaded += (_, __) => { if (enabled) { Refresh(); timer.Start(); } };
                target.Unloaded += (_, __) => timer.Stop();
            }

            internal void SetEnabled(bool value)
            {
                enabled = value;
                lastAccent = null; // Apply() may have just reset these resources.
                if (!enabled) { timer.Stop(); return; }
                Refresh();
                if (target.IsLoaded) timer.Start();
            }

            private void Refresh()
            {
                if (!enabled) return;
                var accent = GetWindowsAccent();
                if (lastAccent.HasValue && lastAccent.Value == accent) return;
                lastAccent = accent;
                var text = ContrastText(accent);
                Set(target, "ReShadeAccent", accent);
                Set(target, "ReShadeAccentHover", Mix(accent, Colors.White, 0.15));
                Set(target, "ReShadeAccentPress", Mix(accent, Colors.Black, 0.15));
                Set(target, "UiAccent", accent);
                Set(target, "FpsAccent", accent);
                Set(target, "UiPrimary", accent);
                Set(target, "UiPrimaryText", text);
                Set(target, "SwitchTrackOnBrush", accent);
                Set(target, "SwitchThumbOnBrush", text);
                Set(target, "GameSwitchThumbOn", text);
            }
        }

        // UISettings returns the actual Windows accent (including automatic wallpaper
        // accents). Reflection keeps the .NET Framework Playnite add-on free of WinRT
        // compile-time dependencies; the WPF system color is a safe fallback.
        private static object uiSettings;
        private static MethodInfo getColorValue;
        private static object accentKind;
        private static bool attemptedUiSettings;

        private static Color GetWindowsAccent()
        {
            try
            {
                if (!attemptedUiSettings)
                {
                    attemptedUiSettings = true;
                    var settingsType = Type.GetType("Windows.UI.ViewManagement.UISettings, Windows, ContentType=WindowsRuntime");
                    var colorType = Type.GetType("Windows.UI.ViewManagement.UIColorType, Windows, ContentType=WindowsRuntime");
                    if (settingsType != null && colorType != null)
                    {
                        uiSettings = Activator.CreateInstance(settingsType);
                        getColorValue = settingsType.GetMethod("GetColorValue", new[] { colorType });
                        accentKind = Enum.Parse(colorType, "Accent");
                    }
                }
                if (uiSettings != null && getColorValue != null && accentKind != null)
                {
                    var value = getColorValue.Invoke(uiSettings, new[] { accentKind });
                    if (value != null)
                        return Color.FromRgb(ColorChannel(value, "R"), ColorChannel(value, "G"), ColorChannel(value, "B"));
                }
            }
            catch { /* use WPF's system color below */ }
            try
            {
                var color = SystemParameters.WindowGlassColor;
                return Color.FromRgb(color.R, color.G, color.B);
            }
            catch { return Color.FromRgb(0x4C, 0xC2, 0xFF); }
        }

        private static byte ColorChannel(object value, string name)
        {
            var type = value.GetType();
            var property = type.GetProperty(name);
            if (property != null) return Convert.ToByte(property.GetValue(value, null));
            var field = type.GetField(name);
            if (field == null) throw new InvalidOperationException("Windows accent color is unavailable.");
            return Convert.ToByte(field.GetValue(value));
        }

        private static Color Mix(Color color, Color other, double amount)
        {
            byte Blend(byte a, byte b) => (byte)Math.Round(a * (1 - amount) + b * amount);
            return Color.FromRgb(Blend(color.R, other.R), Blend(color.G, other.G), Blend(color.B, other.B));
        }

        private static GradientStop Stop(Color color, double offset)
        {
            return new GradientStop(color, offset);
        }

        private static Brush Diagonal(params GradientStop[] stops)
        {
            return new LinearGradientBrush(new GradientStopCollection(stops), new Point(0, 0), new Point(1, 1));
        }

        private static Brush Vertical(params GradientStop[] stops)
        {
            return new LinearGradientBrush(new GradientStopCollection(stops), new Point(0, 0), new Point(0, 1));
        }

        private static Color ContrastText(Color color)
        {
            double Linear(byte c)
            {
                var v = c / 255.0;
                return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }
            var luminance = 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
            return luminance > 0.179 ? Colors.Black : Colors.White;
        }

        private static void Set(FrameworkElement target, string key, Color value)
        {
            var brush = new SolidColorBrush(value);
            brush.Freeze();
            target.Resources[key] = brush;
        }

        private static void Set(FrameworkElement target, string key, string value)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Freeze();
            target.Resources[key] = brush;
        }

        private static void SetBrush(FrameworkElement target, string key, Brush brush)
        {
            target.Resources[key] = brush;
        }
    }
}
