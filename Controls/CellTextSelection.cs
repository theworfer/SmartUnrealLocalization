using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>A grid cell's read-only text that a mouse drag can select part of.</summary>
    internal interface ISelectableCellText
    {
        /// <summary>The text the character indices refer to (what Ctrl+C copies from).</summary>
        string SelectionText { get; }

        /// <summary>Character index (0 … length) under <paramref name="point"/>, in the control's coordinates.</summary>
        int HitIndex(Point point);

        /// <summary>Rectangles, in the control's coordinates, covering [start, end).</summary>
        IEnumerable<Rect> RangeRects(int start, int end);

        /// <summary>What a double-click at <paramref name="point"/> selects, or null when a
        /// double-click means something else here (the Target cell opens its editor).</summary>
        (int Start, int End)? DoubleClickRange(Point point);
    }

    /// <summary>What a double-click on a cell's text selects.</summary>
    public enum CellDoubleClick
    {
        /// <summary>Nothing — the double-click belongs to someone else (Target: the editor).</summary>
        None,
        /// <summary>The word under the pointer (a tag chip counts as one word).</summary>
        Word,
        /// <summary>The whole text (an identifier such as a Key).</summary>
        All,
    }

    /// <summary>
    /// Mouse selection of part of a Key / Source / Target cell's text: press, drag,
    /// release, then Ctrl+C. One selection exists in the whole app; a press anywhere
    /// else clears it.
    ///
    /// <para>Not <see cref="SelectableTextBlock"/>: it takes keyboard focus (the row
    /// flickered through the grid's focus styles) and handles double-click itself
    /// (which swallowed the double-click that opens the Target editor) — and the
    /// Source / Target cells are not one text block anyway, tags are drawn as
    /// separate chips. Here a plain click still focuses the row and a double-click
    /// still opens the editor: the selection only starts once the pointer has moved
    /// a few pixels with the button held.</para>
    /// </summary>
    internal static class CellTextSelection
    {
        private const double DragThreshold = 4;

        private static Control? _pressedOwner;
        private static Point _pressPoint;
        private static int _pressIndex;
        private static bool _dragging;

        /// <summary>The control holding the selection, or null.</summary>
        internal static Control? Owner { get; private set; }
        internal static int Anchor { get; private set; }
        internal static int Caret { get; private set; }

        private static readonly HashSet<TopLevel> _hooked = new();

        /// <summary>Test seam / Ctrl+C: the selected text, or "" when nothing is selected.</summary>
        internal static string SelectedText
        {
            get
            {
                if (Owner is not ISelectableCellText cell) return "";
                string text = cell.SelectionText;
                int s = Math.Clamp(Math.Min(Anchor, Caret), 0, text.Length);
                int e = Math.Clamp(Math.Max(Anchor, Caret), 0, text.Length);
                return e > s ? text.Substring(s, e - s) : "";
            }
        }

        /// <summary>Make <paramref name="control"/> selectable. Called once from its constructor.</summary>
        internal static void Attach(Control control)
        {
            control.Cursor = new Cursor(StandardCursorType.Ibeam);
            control.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Bubble, handledEventsToo: true);
            control.AttachedToVisualTree += (_, _) => Hook(TopLevel.GetTopLevel(control));
            control.DetachedFromVisualTree += (_, _) => { if (ReferenceEquals(Owner, control)) Clear(); };
        }

        /// <summary>The text of <paramref name="control"/> changed (a recycled cell, an edit): drop its selection.</summary>
        internal static void TextChanged(Control control)
        {
            if (ReferenceEquals(Owner, control)) Clear();
            if (ReferenceEquals(_pressedOwner, control)) _pressedOwner = null;
        }

        /// <summary>
        /// The highlight: one translucent layer over the cell that holds the
        /// selection, in the window's adorner layer. (The cell controls cannot
        /// paint it themselves — TextBlock and Panel seal Render.)
        /// </summary>
        private sealed class Highlight : Control
        {
            public Highlight() => IsHitTestVisible = false;

            public override void Render(DrawingContext context)
            {
                if (Owner is not ISelectableCellText cell) return;
                int s = Math.Min(Anchor, Caret), e = Math.Max(Anchor, Caret);
                if (e <= s) return;
                var brush = Owner.TryFindResource("SelectionStrong", Owner.ActualThemeVariant, out var res) && res is IBrush b
                    ? b : new SolidColorBrush(Color.FromArgb(0x55, 0x2B, 0x8A, 0xE6));
                // Kept inside the cell: a one-line cell clips text that runs past its edge.
                var bounds = new Rect(Owner.Bounds.Size);
                foreach (var rect in cell.RangeRects(s, e))
                {
                    var r = rect.Intersect(bounds);
                    if (r.Width > 0 && r.Height > 0) context.FillRectangle(brush, r);
                }
            }
        }

        private static readonly Highlight _highlight = new();
        private static Control? _adorned;

        /// <summary>Show the highlight on the owner (moving it from the previous one).</summary>
        private static void Repaint()
        {
            if (!ReferenceEquals(_adorned, Owner))
            {
                if (_adorned is not null) Avalonia.Controls.Primitives.AdornerLayer.SetAdorner(_adorned, null);
                _adorned = Owner;
                if (_adorned is not null) Avalonia.Controls.Primitives.AdornerLayer.SetAdorner(_adorned, _highlight);
            }
            _highlight.InvalidateVisual();
        }

        internal static void Clear()
        {
            Owner = null;
            Anchor = Caret = 0;
            Repaint();
        }

        /// <summary>Select [start, end) of <paramref name="control"/> (tests; keyboard paths).</summary>
        internal static void Select(Control control, int start, int end)
        {
            if (control is not ISelectableCellText cell) return;
            if (!ReferenceEquals(Owner, control)) Clear();
            int len = cell.SelectionText.Length;
            Owner = control;
            Anchor = Math.Clamp(start, 0, len);
            Caret = Math.Clamp(end, 0, len);
            Repaint();
        }

        private static void Hook(TopLevel? top)
        {
            if (top is null || !_hooked.Add(top)) return;
            // Tunnel handlers on the window see every move and release, wherever the
            // pointer is — the drag may leave the cell — and every press, so a press
            // anywhere else clears the selection.
            top.AddHandler(InputElement.PointerPressedEvent, OnAnyPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            top.AddHandler(InputElement.PointerMovedEvent, OnAnyMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            top.AddHandler(InputElement.PointerReleasedEvent, OnAnyReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            top.AddHandler(InputElement.KeyDownEvent, OnAnyKeyDown, RoutingStrategies.Tunnel, handledEventsToo: false);
            top.Closed += (_, _) => _hooked.Remove(top);
        }

        private static void OnPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Control control || control is not ISelectableCellText cell) return;
            var props = e.GetCurrentPoint(control).Properties;
            if (!props.IsLeftButtonPressed) return;
            if (e.ClickCount == 2)
            {
                // Double-click: the word (Source) or the whole key — like any text
                // field. Where the cell gives it another meaning, nothing is selected.
                _pressedOwner = null;
                if (cell.DoubleClickRange(e.GetPosition(control)) is { } range && range.End > range.Start)
                    Select(control, range.Start, range.End);
                return;
            }
            if (e.ClickCount > 2) return;
            // Ctrl / Shift + click check rows; they never start a text selection.
            if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Meta)) != 0) return;
            _pressedOwner = control;
            _pressPoint = e.GetPosition(control);
            _pressIndex = cell.HitIndex(_pressPoint);
            _dragging = false;
        }

        private static void OnAnyPressed(object? sender, PointerPressedEventArgs e)
        {
            // Not the owner's own press (that one restarts the selection on drag):
            // anything else — another cell, a button, the header — clears it.
            if (Owner is null) return;
            if (e.Source is Visual v && ReferenceEquals(v.FindAncestorOfType<Control>(includeSelf: true) is { } c
                    ? FindOwner(c) : null, Owner)) return;
            Clear();
        }

        private static Control? FindOwner(Control c)
        {
            for (Visual? v = c; v is not null; v = v.GetVisualParent())
                if (v is ISelectableCellText && v is Control owner) return owner;
            return null;
        }

        private static void OnAnyMoved(object? sender, PointerEventArgs e)
        {
            var control = _pressedOwner;
            if (control is null || control is not ISelectableCellText cell) return;
            if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) { _pressedOwner = null; return; }

            var p = e.GetPosition(control);
            if (!_dragging)
            {
                if (Math.Abs(p.X - _pressPoint.X) < DragThreshold && Math.Abs(p.Y - _pressPoint.Y) < DragThreshold) return;
                _dragging = true;
                Clear();
                Owner = control;
                Anchor = _pressIndex;
            }
            Caret = cell.HitIndex(p);
            Repaint();
        }

        private static void OnAnyReleased(object? sender, PointerReleasedEventArgs e)
        {
            _pressedOwner = null;
            _dragging = false;
        }

        private static async void OnAnyKeyDown(object? sender, KeyEventArgs e)
        {
            try
            {
                if (Owner is null) return;
                if (e.Key == Key.Escape) { Clear(); return; }
                bool ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
                if (!ctrl || (e.Key != Key.C && e.Key != Key.Insert)) return;
                // A focused text field copies its own selection.
                if (TopLevel.GetTopLevel(Owner)?.FocusManager?.GetFocusedElement() is TextBox) return;
                string text = SelectedText;
                if (text.Length == 0) return;
                e.Handled = true;            // the grid's own Ctrl+C would copy whole rows
                var vm = ViewModels.MainWindowViewModel.Current;
                if (vm is not null) await vm.WriteClipboardAsync(text);
            }
            catch (Exception ex) { Services.HandlerGuard.Report(ex, nameof(OnAnyKeyDown)); }
        }

        // ── Hit testing helpers shared by the two cell controls ─────────────

        /// <summary>
        /// The word around <paramref name="index"/> in <paramref name="text"/>: a run of
        /// letters, digits, '_' and in-word apostrophes (don't, l'eau). On a space or
        /// punctuation the character itself, so a double-click always selects something.
        /// </summary>
        internal static (int Start, int End) WordAt(string text, int index)
        {
            if (text.Length == 0) return (0, 0);
            static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '\'' || c == '’';
            index = Math.Clamp(index, 0, text.Length);
            // The pointer sits between two characters: prefer the word on its right,
            // else the one on its left (a click on the last letter of a word).
            int at = index < text.Length && IsWord(text[index]) ? index
                   : index > 0 && IsWord(text[index - 1]) ? index - 1
                   : -1;
            if (at < 0)
            {
                int i = Math.Min(index, text.Length - 1);
                return (i, i + 1);
            }
            int s = at, e = at + 1;
            while (s > 0 && IsWord(text[s - 1])) s--;
            while (e < text.Length && IsWord(text[e])) e++;
            // An apostrophe is part of a word only between letters, not a quote around it.
            while (s < e && (text[s] == '\'' || text[s] == '’')) s++;
            while (e > s && (text[e - 1] == '\'' || text[e - 1] == '’')) e--;
            return e > s ? (s, e) : (at, at + 1);
        }

        /// <summary>Character index inside one text block, from a point in that block's coordinates.</summary>
        internal static int IndexIn(TextBlock block, Point local)
        {
            var hit = block.TextLayout.HitTestPoint(local);
            return hit.CharacterHit.FirstCharacterIndex + hit.CharacterHit.TrailingLength;
        }

        /// <summary>Rectangles of [start, end) inside one text block, in that block's coordinates.</summary>
        internal static IEnumerable<Rect> RectsIn(TextBlock block, int start, int end)
        {
            int len = block.Text?.Length ?? 0;
            start = Math.Clamp(start, 0, len);
            end = Math.Clamp(end, 0, len);
            if (end <= start) return Array.Empty<Rect>();
            return block.TextLayout.HitTestTextRange(start, end - start);
        }
    }
}
