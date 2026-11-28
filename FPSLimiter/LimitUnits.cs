using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;

namespace FPSLimiter
{
    /// <summary>How frame-rate caps are typed and shown. Caps are always stored and applied as FPS.</summary>
    public enum LimitUnit
    {
        Fps = 0,
        Frametime = 1
    }

    public class LimitUnitOption
    {
        public LimitUnit Value { get; set; }
        public string DisplayName { get; set; }
    }

    /// <summary>Observable labels for XAML so headings / suffixes follow the chosen limit unit.</summary>
    public sealed class LimitUnitState : ObservableObject
    {
        public bool IsFrametime => LimitUnits.IsFrametime;
        public string Suffix => LimitUnits.Suffix;
        public string LimitLabel => LimitUnits.IsFrametime ? "Frametime limit (ms)" : "FPS limit";
        public string PresetsLabel => LimitUnits.IsFrametime ? "Frametime presets (this monitor)" : "FPS presets (this monitor)";
        public string PresetExample => LimitUnits.IsFrametime ? "e.g. 16.67" : "e.g. 60";

        internal void Refresh()
        {
            OnPropertyChanged(nameof(IsFrametime));
            OnPropertyChanged(nameof(Suffix));
            OnPropertyChanged(nameof(LimitLabel));
            OnPropertyChanged(nameof(PresetsLabel));
            OnPropertyChanged(nameof(PresetExample));
        }
    }

    /// <summary>
    /// FPS &lt;-&gt; frametime conversion used by every cap input and label. Everything stored in the
    /// settings and handed to RTSS / Special K stays FPS (frametime ms = 1000 / FPS); only what the
    /// user types and reads changes when the limit unit is Frametime.
    /// </summary>
    public static class LimitUnits
    {
        public static readonly LimitUnitState State = new LimitUnitState();

        public static LimitUnit Current { get; private set; } = LimitUnit.Fps;
        public static bool IsFrametime => Current == LimitUnit.Frametime;
        public static string Suffix => IsFrametime ? "ms" : "FPS";
        public static string CapNoun => IsFrametime ? "frametime cap" : "FPS cap";
        public static string ExampleText => IsFrametime ? "16.67 (= 60 FPS)" : "60 or 59.9";
        public static string RangeText => IsFrametime ? "a frametime between 1 and 1000 ms" : "an FPS value between 1 and 1000";

        public static void SetUnit(LimitUnit unit)
        {
            if (Current == unit) return;
            Current = unit;
            State.Refresh();
        }

        /// <summary>The cap as shown to the user, number only ("60" or "16.6667"). Zero / negative = "0".</summary>
        public static string FormatNumber(double fps)
        {
            if (double.IsNaN(fps) || double.IsInfinity(fps) || fps <= 0) return "0";
            var value = IsFrametime ? 1000.0 / fps : fps;
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        /// <summary>The cap with its unit ("60 FPS" or "16.6667 ms").</summary>
        public static string Format(double fps)
        {
            return FormatNumber(fps) + " " + Suffix;
        }

        /// <summary>
        /// Parses what the user typed in the active unit into FPS. A trailing "ms" or "fps" overrides the
        /// active unit ("16.67ms" always means frametime). Frametimes that are the correctly rounded form of a whole
        /// FPS (16.67 / 16.6667 ms) snap back to it (60); other values are kept as typed.
        /// </summary>
        public static bool TryParseLimit(string text, bool allowZero, out double fps)
        {
            fps = 0;
            var s = (text ?? string.Empty).Trim().Replace(',', '.');
            var asFrametime = IsFrametime;
            if (s.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            {
                asFrametime = true;
                s = s.Substring(0, s.Length - 2).Trim();
            }
            else if (s.EndsWith("fps", StringComparison.OrdinalIgnoreCase))
            {
                asFrametime = false;
                s = s.Substring(0, s.Length - 3).Trim();
            }

            double value;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            {
                return false;
            }

            if (value == 0)
            {
                if (!allowZero) return false;
                fps = 0;
                return true;
            }

            if (asFrametime)
            {
                var raw = 1000.0 / value;
                var whole = Math.Round(raw);
                var dot = s.IndexOf('.');
                var decimals = dot < 0 ? 0 : Math.Min(4, s.Length - dot - 1);
                // Snap only when the typed ms is the correct rounding of that whole FPS
                // (16.67 / 16.6667 -> 60). Anything else (16.666, 16) is taken exactly as typed.
                var snap = decimals >= 2 && whole > 0 &&
                           Math.Abs(raw - whole) <= whole * 0.001 &&
                           Math.Abs(Math.Round(1000.0 / whole, decimals) - value) < 1e-9;
                fps = snap ? whole : Math.Round(raw, 4);
            }
            else
            {
                fps = value;
            }

            return fps > 0 && fps <= 1000;
        }
    }

    /// <summary>XAML: shows an FPS value (first binding) in the active unit; bind the unit state as the second value so it refreshes.</summary>
    public class LimitDisplayConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values != null && values.Length > 0 && values[0] is double)
            {
                return LimitUnits.Format((double)values[0]);
            }

            return string.Empty;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
