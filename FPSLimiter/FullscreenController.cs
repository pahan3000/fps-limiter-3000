using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace FPSLimiter
{
    internal enum ControllerAction
    {
        None,
        Confirm,
        Cancel
    }

    /// <summary>
    /// Playnite Fullscreen only turns the D-pad / sticks into real arrow key presses. The confirm (A) and
    /// cancel (B) buttons are NOT sent as Enter / Escape: they arrive as Playnite's own
    /// <c>GameControllerInputEventArgs</c> (a KeyEventArgs with Key.None and a Button property). Playnite's own
    /// controls check for that type, but plain WPF controls ignore it, which is why navigation worked while
    /// selecting and going back did nothing. That type lives in Playnite.exe, not in the SDK, so it is read by
    /// reflection here.
    /// </summary>
    /// <summary>
    /// Inherited flag: true only in Playnite Fullscreen while the pad / keyboard (not the mouse) is driving
    /// the UI. Focus rings that exist for controller users are shown only then, never on Desktop.
    /// </summary>
    public static class FpsFocus
    {
        public static readonly DependencyProperty GamepadActiveProperty = DependencyProperty.RegisterAttached(
            "GamepadActive", typeof(bool), typeof(FpsFocus),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

        public static bool GetGamepadActive(DependencyObject d) { return (bool)d.GetValue(GamepadActiveProperty); }
        public static void SetGamepadActive(DependencyObject d, bool value) { d.SetValue(GamepadActiveProperty, value); }
    }

    internal static class FullscreenController
    {
        private static bool resolved;
        private static PropertyInfo buttonProperty;
        private static PropertyInfo stateProperty;
        private static PropertyInfo confirmBinding;
        private static PropertyInfo cancelBinding;
        private static int lastConfirmTick;
        private static bool confirmSeen;

        public static bool IsControllerEvent(KeyEventArgs e)
        {
            return e != null && e.GetType().Name == "GameControllerInputEventArgs";
        }

        private static void Resolve(Type eventType)
        {
            if (resolved) return;
            resolved = true;
            try
            {
                buttonProperty = eventType.GetProperty("Button", BindingFlags.Public | BindingFlags.Instance);
                stateProperty = eventType.GetProperty("ButtonState", BindingFlags.Public | BindingFlags.Instance);
                var gesture = eventType.Assembly.GetType("Playnite.Input.GameControllerGesture");
                if (gesture != null)
                {
                    confirmBinding = gesture.GetProperty("ConfirmationBinding", BindingFlags.Public | BindingFlags.Static);
                    cancelBinding = gesture.GetProperty("CancellationBinding", BindingFlags.Public | BindingFlags.Static);
                }
            }
            catch { /* fall back to A = confirm, B = cancel */ }
        }

        /// <summary>Confirm / Cancel for a controller button press; None for anything else (including releases).</summary>
        public static ControllerAction Classify(KeyEventArgs e)
        {
            if (!IsControllerEvent(e)) return ControllerAction.None;
            try
            {
                Resolve(e.GetType());
                if (stateProperty != null)
                {
                    var state = stateProperty.GetValue(e, null);
                    if (state != null && !string.Equals(state.ToString(), "Pressed", StringComparison.Ordinal))
                        return ControllerAction.None;
                }

                var button = buttonProperty?.GetValue(e, null)?.ToString();
                if (string.IsNullOrEmpty(button)) return ControllerAction.None;
                // Honours Playnite's "swap confirm / cancel buttons" option.
                var confirm = confirmBinding?.GetValue(null, null)?.ToString() ?? "A";
                var cancel = cancelBinding?.GetValue(null, null)?.ToString() ?? "B";
                if (string.Equals(button, confirm, StringComparison.Ordinal)) return ControllerAction.Confirm;
                if (string.Equals(button, cancel, StringComparison.Ordinal)) return ControllerAction.Cancel;
            }
            catch { /* never let input handling crash Playnite */ }
            return ControllerAction.None;
        }

        /// <summary>
        /// Some Playnite configurations also simulate a real Enter key for the confirm button. Ignore that Enter
        /// right after a controller confirm so a toggle is not flipped twice.
        /// </summary>
        public static bool IsDuplicateEnter()
        {
            if (!confirmSeen) return false;
            var elapsed = unchecked(Environment.TickCount - lastConfirmTick);
            return elapsed >= 0 && elapsed < 250;
        }

        public static ComboBox OpenComboOf(DependencyObject focused)
        {
            ComboBox combo = focused as ComboBox;
            if (combo == null && focused is ComboBoxItem item)
                combo = ItemsControl.ItemsControlFromItemContainer(item) as ComboBox;
            return combo != null && combo.IsDropDownOpen ? combo : null;
        }

        /// <summary>
        /// Handles a controller event for a window or view: B closes an open drop-down first, otherwise calls
        /// <paramref name="cancel"/>; A activates the focused control. Returns true when the event was consumed.
        /// </summary>
        public static bool HandleControllerKey(KeyEventArgs e, Action cancel)
        {
            if (e == null || e.Handled) return false;
            var action = Classify(e);
            if (action == ControllerAction.None) return false;

            try
            {
                var focused = Keyboard.FocusedElement as DependencyObject;
                if (action == ControllerAction.Cancel)
                {
                    var combo = OpenComboOf(focused);
                    e.Handled = true;
                    if (combo != null)
                    {
                        combo.IsDropDownOpen = false;
                        combo.Focus();
                    }
                    else
                    {
                        cancel?.Invoke();
                    }
                    return true;
                }

                lastConfirmTick = Environment.TickCount;
                confirmSeen = true;
                if (ActivateFocused(focused))
                {
                    e.Handled = true;
                    return true;
                }
            }
            catch { /* never let input handling crash Playnite */ }
            return false;
        }

        private static FocusNavigationDirection? ArrowDirection(Key key)
        {
            switch (key)
            {
                case Key.Up: return FocusNavigationDirection.Up;
                case Key.Down: return FocusNavigationDirection.Down;
                case Key.Left: return FocusNavigationDirection.Left;
                case Key.Right: return FocusNavigationDirection.Right;
                default: return null;
            }
        }

        /// <summary>
        /// D-pad arrows on a closed combo box would change its selection, and on a slider they would eat the
        /// key, so the pad could never move on. Turn them into focus moves (sliders keep Left / Right so their
        /// value can still be adjusted). Returns true when the key was consumed.
        /// </summary>
        public static bool TryMoveFocusFromArrow(KeyEventArgs e)
        {
            if (e == null || e.Handled || IsControllerEvent(e)) return false;
            try
            {
                var focused = Keyboard.FocusedElement as DependencyObject;
                if (focused == null || focused is ComboBoxItem || OpenComboOf(focused) != null) return false;

                var dir = ArrowDirection(e.Key);
                if (!dir.HasValue) return false;

                var isCombo = focused is ComboBox;
                var isSlider = focused is Slider;
                if (!isCombo && !isSlider) return false;
                if (isSlider && (dir == FocusNavigationDirection.Left || dir == FocusNavigationDirection.Right)) return false;

                e.Handled = true;
                (focused as UIElement)?.MoveFocus(new TraversalRequest(dir.Value));
                return true;
            }
            catch { return false; }
        }

        /// <summary>Does what Enter would do for the focused control. Returns false when nothing was done.</summary>
        public static bool ActivateFocused(DependencyObject focused)
        {
            if (focused == null) return false;

            if (focused is ComboBoxItem comboItem)
            {
                var owner = ItemsControl.ItemsControlFromItemContainer(comboItem) as ComboBox;
                comboItem.IsSelected = true;
                if (owner != null)
                {
                    owner.IsDropDownOpen = false;
                    owner.Focus();
                }
                return true;
            }

            if (focused is ComboBox combo)
            {
                // Arrow keys change the selection while the list is open, so A just commits (closes) it.
                combo.IsDropDownOpen = !combo.IsDropDownOpen;
                return true;
            }

            if (focused is TextBox || focused is Slider) return false;

            if (focused is RadioButton radio)
            {
                radio.IsChecked = true;
                return true;
            }

            if (focused is ToggleButton toggle)
            {
                // Covers CheckBox, Expander headers and switch-style toggles. Setting IsChecked drives any
                // TwoWay binding (e.g. Expander.IsExpanded) exactly like a click.
                toggle.IsChecked = !(toggle.IsChecked == true);
                return true;
            }

            if (focused is Expander expander)
            {
                expander.IsExpanded = !expander.IsExpanded;
                return true;
            }

            if (focused is ButtonBase button)
            {
                Click(button);
                return true;
            }

            if (focused is ListBoxItem listItem)
            {
                listItem.IsSelected = true;
                return true;
            }

            if (focused is TabItem tab)
            {
                tab.IsSelected = true;
                return true;
            }

            return false;
        }

        private static readonly MethodInfo buttonOnClick =
            typeof(ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);

        private static void Click(ButtonBase button)
        {
            try
            {
                // ButtonBase.OnClick raises Click and runs the Command, exactly like a mouse click
                // (Playnite's own ButtonEx calls it the same way).
                if (buttonOnClick != null)
                {
                    buttonOnClick.Invoke(button, null);
                    return;
                }
            }
            catch (TargetInvocationException ex)
            {
                // The click handler itself threw: surface that instead of silently swallowing it.
                throw ex.InnerException ?? ex;
            }
            catch { /* fall through to a plain Click event */ }
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
        }

        /// <summary>
        /// Gives a Fullscreen dialog a starting focus. Fullscreen opens child windows with nothing focused, so
        /// the D-pad has no starting point until something is focused.
        /// </summary>
        public static void FocusFirst(FrameworkElement root, Window window)
        {
            if (root == null) return;
            root.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    window?.Activate();
                    root.UpdateLayout();
                    root.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                }
                catch { /* cosmetic */ }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
    }
}
