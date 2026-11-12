using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.VisualTree;
using SmartUnrealLocalization.Controls;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using Xunit;
using static SmartUnrealLocalization.Tests.Ui.UiSession;

namespace SmartUnrealLocalization.Tests.Ui;

/// <summary>
/// Every validator fix is SEEN before it is written (validator stage 2,
/// 2026-10-21). Fix all used to change the file at once and show nothing; the
/// cell's lightbulb applied on click. Now the fixes are planned on copies,
/// shown before → after in a review, and only the ticked ones are written, as
/// one undo step.
/// </summary>
[Collection("AppState")]
public class FixReviewTests
{
    private static string BrokenFile()
        => MakeLocres("fixes.locres", 30, i => i % 3 == 0 ? $"Take the coins, line {i}." : $"Plain line {i}");

    [Fact]
    public Task Planning_writes_nothing_and_applying_writes_only_what_was_kept()
        => WithWindowAsync(async (window, vm) =>
        {
            await vm.OpenFileAsync(BrokenFile());
            await Settle();
            // Translations that dropped the final full stop — the punctuation check has a fix.
            foreach (var r in vm.AllRows.Where(r => r.Source!.EndsWith(".")))
                r.Target = "Візьми монети " + r.Key;
            foreach (var r in vm.AllRows) ValidationService.Instance.RunForRow(r);
            await Settle();
            var before = vm.AllRows.ToDictionary(r => r, r => r.Target);

            var plan = FixPlanner.Plan(vm.AllRows);
            Assert.NotEmpty(plan.Fixes);
            Assert.All(vm.AllRows, r => Assert.Equal(before[r], r.Target));   // planning wrote nothing
            Assert.All(plan.Fixes, f => Assert.EndsWith(".", f.After));

            var kept = plan.Fixes.Take(2).ToList();
            Assert.Equal(2, vm.ApplyPlannedFixes(kept));
            Assert.All(kept, f => Assert.Equal(f.After, f.Row.Target));
            Assert.All(plan.Fixes.Skip(2), f => Assert.Equal(f.Before, f.Row.Target));

            vm.UndoCommand.Execute(null);                                      // one step undoes both
            await Settle();
            Assert.All(kept, f => Assert.Equal(f.Before, f.Row.Target));
        });

    [Fact]
    public Task A_row_edited_after_the_review_is_skipped_not_overwritten()
        => WithWindowAsync(async (window, vm) =>
        {
            await vm.OpenFileAsync(BrokenFile());
            await Settle();
            var row = vm.AllRows.First(r => r.Source!.EndsWith("."));
            row.Target = "Візьми монети";
            ValidationService.Instance.RunForRow(row);
            var plan = FixPlanner.Plan(new[] { row });
            Assert.Single(plan.Fixes);

            row.Target = "the user typed this meanwhile";
            Assert.Equal(0, vm.ApplyPlannedFixes(plan.Fixes));
            Assert.Equal("the user typed this meanwhile", row.Target);
        });

    [Fact]
    public Task A_fix_that_would_break_something_else_is_not_offered()
        => WithWindowAsync(async (window, vm) =>
        {
            await Settle();
            var row = new TranslationRow { Namespace = "UI", Key = "K", Source = "Plain text.", Target = "Звичайний текст." };
            ValidationService.Instance.RunForRow(row);
            var harmful = new Problem
            {
                Kind = "Test", Severity = ProblemSeverity.Info, Title = "test",
                Fix = new Fix { Label = "harmful", OldValue = row.Target!, NewValue = "Звичайний <b>текст." },
            };
            row.Problems.Add(harmful);

            var plan = FixPlanner.Plan(new[] { row });
            Assert.Empty(plan.Fixes);
            Assert.Equal(1, plan.Refused);
        });

    [Fact]
    public Task The_review_shows_both_sides_with_the_change_highlighted_and_spaces_visible()
        => WithWindowAsync(async (window, vm) =>
        {
            var row = new TranslationRow { Namespace = "UI", Key = "K", Source = "Play ", Target = "Грати" };
            var fix = new PlannedFix(row, "Грати", "Грати ", new[] { "Match source edges" });
            var dialog = new FixReviewDialog(new[] { fix }, refused: 1);
            dialog.Show(window);
            await Settle();
            try
            {
                var diffs = dialog.GetVisualDescendants().OfType<DiffText>().ToList();
                Assert.Equal(2, diffs.Count);
                var after = diffs.Single(d => d.ShowAfter);
                var changed = after.Inlines!.OfType<Run>().Where(r => r.FontWeight == FontWeight.Bold).ToList();
                Assert.Contains(changed, r => r.Text == "·");                 // the added space, made visible
                Assert.True(dialog.FindControl<TextBlock>("RefusedText")!.IsVisible);
            }
            finally { dialog.Close(); }
        });

    /// <summary>QC's bulk fixes (filter bar, QC tab) go through the same review:
    /// each one reaches ApplyQcProposals only via ReviewAndApplyQcAsync.</summary>
    [Fact]
    public void Qc_bulk_fixes_are_reviewed_before_they_are_written()
    {
        string root = TestPaths.RepoRoot();
        string code = File.ReadAllText(Path.Combine(root, "ViewModels", "MainWindowViewModel.Qc.cs"))
                    + File.ReadAllText(Path.Combine(root, "ViewModels", "MainWindowViewModel.QcTab.cs"));
        foreach (var name in new[] { "QcFixAll", "QcFixSelected", "FixQcGroup", "QcFixAllMechanical" })
        {
            var m = Regex.Match(code, $@"\b{name}\([^)]*\)\s*\{{(?<body>.*?)\n        \}}", RegexOptions.Singleline);
            Assert.True(m.Success, $"{name} not found");
            string body = m.Groups["body"].Value;
            Assert.Contains("await ReviewAndApplyQcAsync(", body);
            Assert.DoesNotContain("ApplyQcProposals(", body);
        }
    }

    /// <summary>Nothing applies a validator fix without showing it first: the
    /// lightbulb and the Problems tab go through the review, Fix all too. The
    /// popover may apply directly — it shows the before/after itself.</summary>
    [Fact]
    public void No_fix_is_applied_unseen()
    {
        string root = TestPaths.RepoRoot();
        foreach (var dir in new[] { "Views", "Controls", "ViewModels", "Services" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            {
                if (file.EndsWith("ProblemPopover.axaml.cs", StringComparison.Ordinal)) continue;
                string code = Regex.Replace(File.ReadAllText(file), @"//[^\n]*", "");
                Assert.False(Regex.IsMatch(code, @"\b[Ff]ix\??\.Apply\("), $"{Path.GetFileName(file)} applies a Fix directly");
            }
    }
}
