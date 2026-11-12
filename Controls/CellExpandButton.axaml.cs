using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>
    /// Tiny 18×18 chevron pinned to the right edge of the Target cell.
    /// Visible when <c>TranslationRow.ShowExpandChevron</c> (i.e. text is
    /// currently clipped OR the row is already expanded). Click toggles
    /// <c>TranslationRow.IsExpanded</c>, which feeds into the TaggedTextBlock
    /// Wrap binding (MultiBinding Or of IsEditing / IsExpanded / global
    /// AutoExpandRows) so just THIS row flips to wrap mode and grows
    /// vertically. Per-row expand is the documented escape hatch for "I
    /// want to see this one long string without enabling global wrap and
    /// losing scroll precision on the whole grid".
    /// </summary>
    public partial class CellExpandButton : UserControl
    {
        public CellExpandButton() => InitializeComponent();

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        private void OnClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not TranslationRow row) return;
            row.IsExpanded = !row.IsExpanded;
        }
    }
}
