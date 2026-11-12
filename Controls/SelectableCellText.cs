using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>
    /// A plain grid cell text (the Key column) whose text a mouse drag can select —
    /// see <see cref="CellTextSelection"/>. It styles as a <see cref="TextBlock"/>,
    /// so <c>TextBlock.keyCell</c> keeps applying.
    /// </summary>
    public class SelectableCellText : TextBlock, ISelectableCellText
    {
        protected override Type StyleKeyOverride => typeof(TextBlock);

        public SelectableCellText() => CellTextSelection.Attach(this);

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == TextProperty) CellTextSelection.TextChanged(this);
        }

        public string SelectionText => Text ?? "";

        int ISelectableCellText.HitIndex(Point point)
            => Math.Clamp(CellTextSelection.IndexIn(this, point - new Point(Padding.Left, Padding.Top)), 0, SelectionText.Length);

        /// <summary>A key is one identifier: a double-click selects all of it.</summary>
        (int Start, int End)? ISelectableCellText.DoubleClickRange(Point point) => (0, SelectionText.Length);

        IEnumerable<Rect> ISelectableCellText.RangeRects(int start, int end)
        {
            foreach (var r in CellTextSelection.RectsIn(this, start, end))
                yield return r.Translate(new Vector(Padding.Left, Padding.Top));
        }

    }
}
