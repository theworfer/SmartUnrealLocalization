using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>
    /// Visual for a single <see cref="ToastNotification"/>. Hosted inside
    /// <see cref="ToastStack"/>'s ItemsControl; one instance per active toast.
    /// Click on Undo runs the toast's <see cref="ToastNotification.UndoAction"/>
    /// (if any) then dismisses; click on × just dismisses.
    /// </summary>
    public partial class Toast : UserControl
    {
        public Toast() => InitializeComponent();

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        /// <summary>
        /// Try once on DataContext change AND again on visual-tree attach.
        /// In an ItemsControl, the order is: instantiate → set DataContext →
        /// attach to tree. <see cref="StyledElement.TryFindResource"/> walks
        /// ancestor resources, which only succeed AFTER attach. Without the
        /// attach hook the stripe + glyph were rendering with
        /// <see cref="Brushes.Transparent"/> on first paint (invisible).
        /// </summary>
        protected override void OnDataContextChanged(System.EventArgs e)
        {
            base.OnDataContextChanged(e);
            ResolveStripeBrush();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            // Re-resolve once we're in the tree — fixes the first-paint
            // Transparent-stripe race described above. Cheap to re-run.
            ResolveStripeBrush();
        }

        private void ResolveStripeBrush()
        {
            if (DataContext is not ToastNotification toast) return;

            // Resource lookup may fail if we're not attached yet — leave the
            // current brush in place rather than overwriting with Transparent
            // (otherwise the AttachedToVisualTree pass below has to re-win).
            if (!this.TryFindResource(toast.StripeBrushKey, out var res) || res is not IBrush b)
                return;

            if (this.FindControl<Border>("StripeAccent") is { } stripe)
                stripe.Background = b;
            if (this.FindControl<TextBlock>("GlyphText") is { } glyph)
                glyph.Foreground = b;
        }

        private void OnUndoClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not ToastNotification toast) return;
            // Fire the registered Undo first, then dismiss — order matters so
            // the user perceives the undo happening BEFORE the toast vanishes.
            toast.UndoAction?.Invoke();
            ToastService.Instance.Dismiss(toast);
        }

        private void OnDismissClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is ToastNotification toast)
                ToastService.Instance.Dismiss(toast);
        }
    }
}
