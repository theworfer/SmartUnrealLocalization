using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using Xunit;
using static SmartUnrealLocalization.Tests.Ui.UiSession;

namespace SmartUnrealLocalization.Tests.Ui;

/// <summary>
/// Going to a row a filter hides, and walking / unifying the translations of one
/// text. A jump to a hidden row used to change the grid's filters without asking
/// (and from the user's side often looked like nothing happened); the duplicate
/// note named the other translations but gave no way to reach or reuse them.
/// </summary>
[Collection("AppState")]
public class RevealAndDuplicatesUiTests
{
    [Fact]
    public Task Going_to_a_hidden_row_asks_and_No_changes_nothing()
        => WithWindowAsync(async (window, vm) =>
        {
            await vm.OpenFileAsync(MakeLocres("reveal.locres", 6, i => $"Line {i}"));
            await Settle();
            foreach (var r in vm.AllRows) r.Target = "t " + r.Key;
            var hidden = vm.AllRows[4];
            vm.AllRows[1].IsSelected = true;
            vm.ShowOnlySelected = true;                           // only the checked row is shown
            await Settle();
            Assert.DoesNotContain(hidden, vm.Rows);

            var asked = new List<string>();
            bool answer = false;
            TranslationRow? revealed = null;
            vm.YesNoRequested += (title, message) => { asked.Add(message); return Task.FromResult(answer); };
            vm.RevealRowRequested += r => revealed = r;

            // No: the question was asked, nothing else happened.
            Assert.False(await vm.RevealRowAsync(hidden));
            Assert.Single(asked);
            Assert.True(vm.ShowOnlySelected);
            Assert.Single(vm.Rows);
            Assert.Null(revealed);

            // Yes: every row is shown and the grid goes to the row.
            answer = true;
            Assert.True(await vm.RevealRowAsync(hidden));
            await Settle();
            Assert.Equal(2, asked.Count);
            Assert.False(vm.ShowOnlySelected);
            Assert.Equal(vm.AllRows.Count, vm.Rows.Count);
            Assert.Same(hidden, revealed);
            Assert.Same(hidden, vm.FocusedRow);
            Assert.Same(hidden, window.MainDataGrid.SelectedItem);

            // A row that is already shown: no question.
            Assert.True(await vm.RevealRowAsync(vm.AllRows[0]));
            Assert.Equal(2, asked.Count);
        });

    [Fact]
    public Task The_translations_of_one_text_can_be_walked_and_unified()
        => WithWindowAsync(async (window, vm) =>
        {
            // Rows 0, 2, 4 share one source; 1, 3, 5 another.
            await vm.OpenFileAsync(MakeLocres("dup.locres", 6, i => i % 2 == 0 ? "Open the door" : "Close the door"));
            await Settle();
            var rows = vm.AllRows;
            rows[0].Target = "Відчини двері";
            rows[2].Target = "Відкрий двері";
            rows[4].Target = "Відчини двері";
            rows[1].Target = rows[3].Target = rows[5].Target = "Зачини двері";

            Assert.Equal(new[] { rows[0], rows[2], rows[4] }, vm.SameSourceRows(rows[2]));

            // Walk: next from row 0 is row 2, then 4, then round to 0; previous goes back.
            Assert.Same(rows[2], await vm.GoToSameSourceRowAsync(rows[0], forward: true));
            Assert.Same(rows[4], await vm.GoToSameSourceRowAsync(rows[2], forward: true));
            Assert.Same(rows[0], await vm.GoToSameSourceRowAsync(rows[4], forward: true));
            Assert.Same(rows[4], await vm.GoToSameSourceRowAsync(rows[0], forward: false));
            Assert.Same(rows[4], vm.FocusedRow);

            // Use row 2's translation for the same text: the change is REVIEWED first…
            IReadOnlyList<PlannedFix>? shown = null;
            vm.FixReviewRequested += (plan, refused) => { shown = plan; return Task.FromResult<List<PlannedFix>?>(null); };
            Assert.Equal(0, await vm.UseTranslationForSameSourceAsync(rows[2]));
            Assert.NotNull(shown);
            Assert.Equal(new[] { rows[0], rows[4] }, shown!.Select(f => f.Row));     // only the rows that differ
            Assert.All(shown!, f => Assert.Equal("Відкрий двері", f.After));
            Assert.Equal("Відчини двері", rows[0].Target);                             // cancelled: nothing written
        });

    [Fact]
    public Task Using_a_translation_for_the_same_text_writes_the_kept_rows_as_one_undo()
        => WithWindowAsync(async (window, vm) =>
        {
            await vm.OpenFileAsync(MakeLocres("dup2.locres", 5, i => i < 4 ? "Open the door" : "Other"));
            await Settle();
            var rows = vm.AllRows;
            rows[0].Target = "A"; rows[1].Target = "B"; rows[2].Target = "C"; rows[3].Target = "A"; rows[4].Target = "Z";

            // The user keeps two of the three proposed rows.
            vm.FixReviewRequested += (plan, refused) => Task.FromResult<List<PlannedFix>?>(plan.Take(2).ToList());
            Assert.Equal(2, await vm.UseTranslationForSameSourceAsync(rows[0]));
            Assert.Equal(new[] { "A", "A", "A", "A", "Z" }, rows.Select(r => r.Target));

            // Same translation everywhere already: nothing to do, no dialog needed.
            Assert.Equal(0, await vm.UseTranslationForSameSourceAsync(rows[0]));

            vm.UndoCommand.Execute(null);
            Assert.Equal(new[] { "A", "B", "C", "A", "Z" }, rows.Select(r => r.Target));
        });
}
