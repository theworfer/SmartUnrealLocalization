using SmartUnrealLocalization.Utils;
using Xunit;
using static SmartUnrealLocalization.Tests.Ui.UiSession;

namespace SmartUnrealLocalization.Tests.Ui;

/// <summary>
/// The column header menu: Copy / Cut / Paste / Clear column. A copied column has
/// to paste back into the same rows whatever the cells hold — it used to be refused
/// ("N lines for M rows") whenever the column ended in empty cells, because the
/// trailing line breaks were read as a spreadsheet's end-of-copy and dropped.
/// </summary>
[Collection("AppState")]
public class ColumnMenuUiTests
{
    public static IEnumerable<object[]> Columns() => new[]
    {
        new object[] { new[] { "a", "b", "c" } },
        new object[] { new[] { "a", "", "" } },                    // ends in empty cells
        new object[] { new[] { "", "", "" } },                     // nothing but empty cells
        new object[] { new[] { "", "b", "" } },
        new object[] { new[] { "two\nlines", "tab\there", "say \"hi\"" } },
        new object[] { new[] { "5\" pipe", "\"quoted\"", "end\n" } },
        new object[] { new[] { "a\r\nb", "", "\"" } },
    };

    [Theory]
    [MemberData(nameof(Columns))]
    public void A_copied_column_parses_back_to_the_same_cells(string[] cells)
    {
        string text = string.Join(Environment.NewLine, cells.Select(TextUtils.EscapeForClipboard));
        Assert.Equal(cells, TextUtils.ParseClipboardLines(text, cells.Length));
    }

    [Fact]
    public void A_spreadsheet_copy_keeps_its_meaning()
    {
        // Excel ends a copy with a line break: two rows, not three.
        Assert.Equal(new[] { "a", "b" }, TextUtils.ParseClipboardLines("a\r\nb\r\n", 2));
        Assert.Equal(new[] { "a", "b" }, TextUtils.ParseClipboardLines("a\r\nb\r\n"));
        // …and a mismatch is still reported as what the user copied (2), not 3.
        Assert.Equal(2, TextUtils.ParseClipboardLines("a\r\nb\r\n", 5).Length);
        // A quote in the middle of a plain line is a character, not the start of a quoted run.
        Assert.Equal(new[] { "5\" pipe", "next" }, TextUtils.ParseClipboardLines("5\" pipe\nnext", 2));
        // Quotes that never close: fall back to plain lines instead of one giant cell.
        Assert.Equal(new[] { "\"open", "next" }, TextUtils.ParseClipboardLines("\"open\nnext", 2));
        Assert.Empty(TextUtils.ParseClipboardLines("", 3));
    }

    [Fact]
    public Task Copy_then_paste_restores_the_column_and_cut_respects_the_clipboard()
        => WithWindowAsync(async (window, vm) =>
        {
            await vm.OpenFileAsync(MakeLocres("col.locres", 5, i => $"Line {i}"));
            await Settle();
            var rows = vm.Rows.ToList();
            string[] original = { "one", "two\nlines", "say \"hi\"", "", "" };
            for (int i = 0; i < rows.Count; i++) rows[i].Target = original[i];

            string clip = "";
            try
            {
                vm.ClipboardWriteOverride = t => { clip = t; return Task.FromResult(true); };
                vm.ClipboardReadOverride = () => Task.FromResult<(bool, string?)>((true, clip));

                // Cut: on the clipboard AND cleared — as one undo step.
                await vm.CutTargetColumnAsync();
                Assert.All(rows, r => Assert.Equal("", r.Target));
                vm.UndoCommand.Execute(null);
                Assert.Equal(original, rows.Select(r => r.Target));

                // Clear, then paste what was copied: every cell back in its own row.
                vm.ClearTargetColumn();
                await vm.PasteTargetColumnAsync();
                Assert.Equal(original, rows.Select(r => r.Target));

                // A clipboard for another row count changes nothing.
                clip = "only\ntwo";
                await vm.PasteTargetColumnAsync();
                Assert.Equal(original, rows.Select(r => r.Target));

                // A busy clipboard: Cut must not erase what it could not copy.
                vm.ClipboardWriteOverride = _ => Task.FromResult(false);
                await vm.CutTargetColumnAsync();
                Assert.Equal(original, rows.Select(r => r.Target));

                // Copy Key / Source columns: one line per visible row.
                vm.ClipboardWriteOverride = t => { clip = t; return Task.FromResult(true); };
                await vm.CopyColumnAsync("Source");
                Assert.Equal(rows.Select(r => r.Source), TextUtils.ParseClipboardLines(clip, rows.Count));
                await vm.CopyColumnAsync("Key");
                Assert.Equal(rows.Select(r => r.Key), TextUtils.ParseClipboardLines(clip, rows.Count));
            }
            finally { vm.ClipboardWriteOverride = null; vm.ClipboardReadOverride = null; }
        });

    [Fact]
    public Task Column_actions_survive_a_filter_that_rows_leave_while_they_run()
        => WithWindowAsync(async (window, vm) =>
        {
            await vm.OpenFileAsync(MakeLocres("flt.locres", 6, i => $"Line {i}"));
            await Settle();
            foreach (var r in vm.AllRows) r.Target = "";
            vm.ShowOnlyUntranslated = true;                       // rows leave as they get a target
            await Settle();
            int shown = vm.Rows.Count;
            Assert.Equal(6, shown);
            string clip = string.Join("\n", Enumerable.Range(0, shown).Select(i => "t" + i));
            try
            {
                vm.ClipboardReadOverride = () => Task.FromResult<(bool, string?)>((true, clip));
                await vm.PasteTargetColumnAsync();
                await Settle();
                Assert.Equal(Enumerable.Range(0, 6).Select(i => "t" + i), vm.AllRows.Select(r => r.Target));
            }
            finally { vm.ClipboardReadOverride = null; vm.ShowOnlyUntranslated = false; }
        });
}
