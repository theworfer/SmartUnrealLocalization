using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using SmartUnrealLocalization.Controls;
using SmartUnrealLocalization.Models;
using Xunit;
using static SmartUnrealLocalization.Tests.Ui.UiSession;

namespace SmartUnrealLocalization.Tests.Ui;

/// <summary>
/// Part of a Key / Source / Target cell can be selected with the mouse and copied
/// with Ctrl+C. Before, only Target could be copied, and only after a double-click
/// opened its editor. A plain click must still just focus the row, and a
/// double-click must still open the editor.
/// </summary>
[Collection("AppState")]
public class CellTextSelectionUiTests
{
    private static T CellOf<T>(Views.MainWindow window, TranslationRow row, Func<T, bool> which) where T : Control
        => window.MainDataGrid.GetVisualDescendants().OfType<T>().First(c => ReferenceEquals(c.DataContext, row) && which(c));

    private static Point At(Views.MainWindow window, Control c, double x)
        => c.TranslatePoint(new Point(x, c.Bounds.Height / 2), window)!.Value;

    private static async Task Drag(Views.MainWindow window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        await Settle();
        window.MouseMove(new Point((from.X + to.X) / 2, from.Y), RawInputModifiers.LeftMouseButton);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        await Settle();
        window.MouseUp(to, MouseButton.Left);
        await Settle();
    }

    [Fact]
    public Task Dragging_over_a_cell_selects_part_of_it_and_Ctrl_C_copies_that_part()
        => WithWindowAsync(async (window, vm) =>
        {
            await vm.OpenFileAsync(MakeLocres("sel.locres", 3, i => $"Open the <b>heavy</b> door {i}"));
            await Settle();
            var row = vm.AllRows[0];
            row.Target = "Відчини двері";
            await Settle();

            string? copied = null;
            vm.ClipboardWriteOverride = t => { copied = t; return Task.FromResult(true); };
            try
            {
                // Source: from the start to past the end — the whole text, tag included.
                var source = CellOf<TaggedTextBlock>(window, row, t => t.SourceText == row.Source);
                await Drag(window, At(window, source, 1), At(window, source, source.Bounds.Width - 1));
                Assert.Same(source, CellTextSelection.Owner);
                Assert.Equal(row.Source, CellTextSelection.SelectedText);

                window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
                await Settle();
                Assert.Equal(row.Source, copied);

                // Part of it: dragged leftwards, from past the end to inside the first word.
                await Drag(window, At(window, source, source.Bounds.Width - 1), At(window, source, 14));
                string part = CellTextSelection.SelectedText;
                Assert.False(string.IsNullOrEmpty(part));
                Assert.EndsWith(part, row.Source);
                Assert.True(part.Length < row.Source!.Length && part.Length > 20, $"part=[{part}]");

                // Key and Target too.
                var key = CellOf<SelectableCellText>(window, row, _ => true);
                await Drag(window, At(window, key, 1), At(window, key, key.Bounds.Width - 1));
                Assert.Equal(row.Key, CellTextSelection.SelectedText);

                var target = CellOf<TaggedTextBlock>(window, row, t => t.SourceText == row.Target);
                await Drag(window, At(window, target, 1), At(window, target, target.Bounds.Width - 1));
                Assert.Equal(row.Target, CellTextSelection.SelectedText);
                Assert.False(row.IsEditing);                           // a drag never opens the editor

                // A plain click elsewhere clears it; a click is not a selection.
                window.MouseDown(At(window, source, 5), MouseButton.Left);
                window.MouseUp(At(window, source, 5), MouseButton.Left);
                await Settle();
                Assert.Null(CellTextSelection.Owner);
                Assert.Same(row, vm.FocusedRow);                       // …and still focuses the row

                // Nothing selected: Ctrl+C does not copy a stale selection.
                copied = null;
                window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
                await Settle();
                Assert.Null(copied);
            }
            finally
            {
                vm.ClipboardWriteOverride = null;
                CellTextSelection.Clear();
            }
        });

    [Fact]
    public Task A_double_click_still_opens_the_Target_editor()
        => WithWindowAsync(async (window, vm) =>
        {
            await vm.OpenFileAsync(MakeLocres("dbl.locres", 2, i => $"Line {i}"));
            await Settle();
            var row = vm.AllRows[0];
            row.Target = "Рядок";
            await Settle();

            var target = CellOf<TaggedTextBlock>(window, row, t => t.SourceText == row.Target);
            var p = At(window, target, 10);
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            window.MouseDown(p, MouseButton.Left);
            window.MouseUp(p, MouseButton.Left);
            await Settle();
            Assert.True(row.IsEditing);
            Assert.Null(CellTextSelection.Owner);
        });

    [Fact]
    public Task A_selection_goes_away_when_its_text_changes()
        => WithWindowAsync(async (window, vm) =>
        {
            await vm.OpenFileAsync(MakeLocres("chg.locres", 2, i => $"Line {i}"));
            await Settle();
            var row = vm.AllRows[0];
            row.Target = "Перший";
            await Settle();
            var target = CellOf<TaggedTextBlock>(window, row, t => t.SourceText == row.Target);
            CellTextSelection.Select(target, 0, 3);
            Assert.Equal("Пер", CellTextSelection.SelectedText);

            row.Target = "Інший";
            await Settle();
            Assert.Null(CellTextSelection.Owner);
            Assert.Equal("", CellTextSelection.SelectedText);
        });

    private static async Task DoubleClick(Views.MainWindow window, Point p)
    {
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        await Settle();
    }

    [Fact]
    public Task A_double_click_selects_the_word_in_Source_and_the_whole_Key()
        => WithWindowAsync(async (window, vm) =>
        {
            await vm.OpenFileAsync(MakeLocres("word.locres", 2, i => $"Open the <b>heavy</b> door {i}"));
            await Settle();
            var row = vm.AllRows[0];
            try
            {
                // Inside "Open": the word, not the cell.
                var source = CellOf<TaggedTextBlock>(window, row, t => t.SourceText == row.Source);
                await DoubleClick(window, At(window, source, 12));
                Assert.Same(source, CellTextSelection.Owner);
                Assert.Equal("Open", CellTextSelection.SelectedText);
                Assert.False(row.IsEditing);

                // On the tag chip: the whole tag.
                var chip = source.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("inlineTagChip"));
                var chipCenter = chip.TranslatePoint(new Point(chip.Bounds.Width / 2, chip.Bounds.Height / 2), window)!.Value;
                await DoubleClick(window, chipCenter);
                Assert.Equal("<b>", CellTextSelection.SelectedText);

                // Key: all of it, wherever the double-click lands.
                var key = CellOf<SelectableCellText>(window, row, _ => true);
                await DoubleClick(window, At(window, key, 6));
                Assert.Equal(row.Key, CellTextSelection.SelectedText);
            }
            finally { CellTextSelection.Clear(); }
        });

    [Theory]
    [InlineData("Open the door", 2, "Open")]
    [InlineData("Open the door", 4, "Open")]          // just after the last letter
    [InlineData("Open the door", 6, "the")]
    [InlineData("don't stop", 2, "don't")]            // an apostrophe inside a word
    [InlineData("'quoted' text", 3, "quoted")]        // quotes around it are not part of it
    [InlineData("Двері, вікно", 2, "Двері")]
    [InlineData("a , b", 2, ",")]                     // punctuation: the character itself
    [InlineData("snake_case_id", 7, "snake_case_id")]
    public void WordAt_finds_the_word_around_a_position(string text, int index, string expected)
    {
        var (s, e) = CellTextSelection.WordAt(text, index);
        Assert.Equal(expected, text.Substring(s, e - s));
    }
}
