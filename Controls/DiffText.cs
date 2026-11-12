using System;
using Avalonia;
using Avalonia.Controls;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>
    /// One side of a before/after change, with the changed words highlighted
    /// and invisible changes (spaces, line breaks) made visible — see
    /// <see cref="Utils.DiffInlines"/>. Builds its own inlines from plain
    /// strings, so it can sit in a virtualized list: a recycled container just
    /// receives new strings.
    /// </summary>
    public class DiffText : SelectableTextBlock
    {
        public static readonly StyledProperty<string?> OldTextProperty =
            AvaloniaProperty.Register<DiffText, string?>(nameof(OldText));
        public static readonly StyledProperty<string?> NewTextProperty =
            AvaloniaProperty.Register<DiffText, string?>(nameof(NewText));
        public static readonly StyledProperty<bool> ShowAfterProperty =
            AvaloniaProperty.Register<DiffText, bool>(nameof(ShowAfter));

        public string? OldText { get => GetValue(OldTextProperty); set => SetValue(OldTextProperty, value); }
        public string? NewText { get => GetValue(NewTextProperty); set => SetValue(NewTextProperty, value); }
        /// <summary>False = draw the "before" side; true = the "after" side.</summary>
        public bool ShowAfter { get => GetValue(ShowAfterProperty); set => SetValue(ShowAfterProperty, value); }

        protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == OldTextProperty || change.Property == NewTextProperty || change.Property == ShowAfterProperty)
            {
                var (before, after) = Utils.DiffInlines.Build(OldText ?? "", NewText ?? "");
                Inlines = ShowAfter ? after : before;
            }
        }
    }
}
