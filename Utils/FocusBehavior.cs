using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace SmartUnrealLocalization.Utils
{
    /// <summary>
    /// Attached behaviour: when the target control's <c>IsVisible</c> flips to true
    /// (i.e. the model-owned inline editor is shown), give it keyboard focus and,
    /// if it's a <see cref="TextBox"/>, select all its text.
    ///
    /// Used by the Target column's CellTemplate, where a <c>TextBox</c> sits next
    /// to the <c>TaggedTextBlock</c> with their visibility toggled by
    /// <c>TranslationRow.IsEditing</c>. Focus follows visibility automatically;
    /// callers never have to walk the visual tree.
    ///
    /// Usage:
    /// <code>
    ///   xmlns:u="using:SmartUnrealLocalization.Utils"
    ///   ...
    ///   &lt;TextBox IsVisible="{Binding IsEditing}"
    ///            u:FocusBehavior.FocusOnVisible="True" .../&gt;
    /// </code>
    /// </summary>
    public static class FocusBehavior
    {
        public static readonly AttachedProperty<bool> FocusOnVisibleProperty =
            AvaloniaProperty.RegisterAttached<Control, bool>(
                "FocusOnVisible", typeof(FocusBehavior));

        public static void SetFocusOnVisible(Control c, bool v) => c.SetValue(FocusOnVisibleProperty, v);
        public static bool GetFocusOnVisible(Control c) => c.GetValue(FocusOnVisibleProperty);

        static FocusBehavior()
        {
            FocusOnVisibleProperty.Changed.AddClassHandler<Control>((c, _) =>
            {
                // Detach any prior subscription, then (re)attach.
                c.PropertyChanged -= OnControlPropertyChanged;
                if (GetFocusOnVisible(c))
                    c.PropertyChanged += OnControlPropertyChanged;
            });
        }

        private static void OnControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != Visual.IsVisibleProperty) return;
            if (sender is not Control c) return;
            if (e.NewValue is not bool now || !now) return;

            // Defer to the next dispatcher cycle: layout must place the control
            // before focus can land on it (especially when it just became visible
            // inside a virtualised DataGrid cell).
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    c.Focus();
                    if (c is TextBox tb) tb.SelectAll();
                }
                catch { /* swallow; focus loss is non-fatal */ }
            }, DispatcherPriority.Input);
        }
    }
}
