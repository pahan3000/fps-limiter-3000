using System.Windows;
using System.Windows.Controls;

namespace FPSLimiter
{
    /// <summary>
    /// Stops a dropdown from scrolling by itself while the mouse hovers its (tall) items. WPF asks to bring
    /// a hovered, partly visible item into view; ignoring that request while the mouse is over the item
    /// keeps the list still, and keyboard navigation still scrolls normally.
    /// </summary>
    public static class ComboBoxBehaviors
    {
        public static readonly DependencyProperty SuppressHoverScrollProperty =
            DependencyProperty.RegisterAttached("SuppressHoverScroll", typeof(bool), typeof(ComboBoxBehaviors),
                new PropertyMetadata(false, OnSuppressHoverScrollChanged));

        public static bool GetSuppressHoverScroll(DependencyObject d) { return (bool)d.GetValue(SuppressHoverScrollProperty); }
        public static void SetSuppressHoverScroll(DependencyObject d, bool value) { d.SetValue(SuppressHoverScrollProperty, value); }

        private static void OnSuppressHoverScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var item = d as FrameworkElement;
            if (item == null) return;
            item.RequestBringIntoView -= OnRequestBringIntoView;
            if ((bool)e.NewValue) item.RequestBringIntoView += OnRequestBringIntoView;
        }

        private static void OnRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            var item = sender as ComboBoxItem;
            if (item != null && item.IsMouseOver) e.Handled = true;
        }
    }
}
