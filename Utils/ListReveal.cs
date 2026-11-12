using System.Collections;
using Avalonia.Controls;

namespace SmartUnrealLocalization.Utils
{
    /// <summary>
    /// The one way to select and scroll to an item in a virtualized ListBox.
    ///
    /// <para>On Avalonia 12.0.2, inserting or removing items BEFORE the realized
    /// range marks the panel's start position unknown (NaN) until its next
    /// measure. <c>ScrollIntoView</c> then forces a layout pass in which the
    /// panel deliberately skips that measure ("waiting for a viewport update")
    /// and arranges with NaN: <c>InvalidOperationException: Invalid Arrange
    /// rectangle</c>, uncatchable in practice because the half-finished scroll
    /// leaves the panel in that state for every later pass too. That is the
    /// 2026-09-01 crash: a grid row selected (Find Next, a click, Enter after an
    /// edit) whose QC suggestion sat in a collapsed category above the visible
    /// part of the QC tab — revealing it expanded the category, then scrolled.
    /// Reproduced in the render harness (stage <c>crashqc</c>).</para>
    ///
    /// <para>The guard: an ordinary layout pass first (<see cref="Layoutable.UpdateLayout"/>),
    /// which measures, so the panel rebuilds its realized range at a known
    /// position; only then select and scroll. Callers must also not do this
    /// from inside another control's event in the same turn — post it.</para>
    /// </summary>
    public static class ListReveal
    {
        /// <summary>Select <paramref name="item"/> and bring it into view.</summary>
        public static void SelectAndReveal(ListBox list, object item)
        {
            if (!CanReveal(list, item)) return;
            list.UpdateLayout();
            list.SelectedItem = item;
            list.ScrollIntoView(item);
        }

        /// <summary>Bring <paramref name="item"/> into view without selecting it.</summary>
        public static void Reveal(ListBox list, object item)
        {
            if (!CanReveal(list, item)) return;
            list.UpdateLayout();
            list.ScrollIntoView(item);
        }

        private static bool CanReveal(ListBox list, object item)
            => list.IsEffectivelyVisible
               && list.ItemsSource is IList items
               && items.Contains(item);
    }
}
