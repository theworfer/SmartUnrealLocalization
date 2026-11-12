using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services.Qc;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// The QC tab's arithmetic. The numbers on the header line are not decoration —
/// "Fix 412" is a promise about what pressing it does, and the bulk action reads
/// the rows, not the (capped) displayed list. These tests pin that promise.
/// </summary>
public class QcGroupingTests
{
    private static TranslationRow Row(string key, string target, QcProposal? p)
    {
        var row = new TranslationRow { Namespace = "NS", Key = key, Source = "src", Target = target };
        if (p is not null) row.QcProposal = p;
        return row;
    }

    private static QcProposal Proposal(string kind, string before, string after,
                                       QcSeverity severity = QcSeverity.Medium,
                                       QcVerdict verdict = QcVerdict.Pending) => new()
    {
        Namespace = "NS",
        Kinds = { kind },
        Before = before,
        After = after,
        Severity = severity,
        Verdict = verdict,
        Reason = kind,
    };

    [Fact]
    public void Rows_are_grouped_by_their_first_firing_check()
    {
        var rows = new[]
        {
            Row("a", "x", Proposal("Whitespace", "x", "x ")),
            Row("b", "y", Proposal("Whitespace", "y", "y ")),
            Row("c", "z", Proposal("TagMismatch", "z", "z")),
        };

        var built = QcGrouping.Build(rows, 100);

        Assert.Equal(2, built.Groups.Count);
        Assert.Equal(2, built.Groups.Single(g => g.Kind == "Whitespace").Count);
        Assert.Equal(1, built.Groups.Single(g => g.Kind == "TagMismatch").Count);
    }

    [Fact]
    public void A_proposal_that_fires_several_checks_is_counted_once()
    {
        var p = Proposal("PunctuationMismatch", "a", "a.");
        p.Kinds.Add("LengthRatio");
        var built = QcGrouping.Build(new[] { Row("a", "a", p) }, 100);

        Assert.Single(built.Groups);
        Assert.Equal(1, built.Groups[0].Count);
    }

    [Fact]
    public void The_worst_severity_in_a_group_drives_its_header()
    {
        var rows = new[]
        {
            Row("a", "x", Proposal("TagOrder", "x", "x", QcSeverity.Low)),
            Row("b", "y", Proposal("TagOrder", "y", "y", QcSeverity.Critical)),
        };

        Assert.Equal(ProblemSeverity.Error, QcGrouping.Build(rows, 100).Groups[0].Severity);
    }

    [Fact]
    public void Groups_are_ordered_worst_first_then_largest()
    {
        var rows = new[]
        {
            Row("a", "x", Proposal("LengthRatio", "x", "x", QcSeverity.Low)),
            Row("b", "y", Proposal("LengthRatio", "y", "y", QcSeverity.Low)),
            Row("c", "z", Proposal("LengthRatio", "z", "z", QcSeverity.Low)),
            Row("d", "w", Proposal("TagMismatch", "w", "w", QcSeverity.Critical)),
        };

        var kinds = QcGrouping.Build(rows, 100).Groups.Select(g => g.Kind).ToList();
        Assert.Equal(new[] { "TagMismatch", "LengthRatio" }, kinds);
    }

    [Fact]
    public void Capping_the_displayed_rows_never_understates_the_counts()
    {
        // The cap protects the UI from a pathological file. It must NOT reach the
        // header numbers: a "Fix 3" on a category of 10 would silently leave 7.
        var rows = Enumerable.Range(0, 10)
            .Select(i => Row("k" + i, "t", Proposal("Whitespace", "t", "t ")))
            .ToArray();

        var group = QcGrouping.Build(rows, maxItemsPerGroup: 3).Groups[0];

        Assert.Equal(3, group.Items.Count);       // display capped
        Assert.Equal(10, group.Count);            // truth
        Assert.Equal(10, group.FixableCount);     // what the button promises
    }

    [Fact]
    public void Flag_only_findings_count_as_review_not_as_fixable()
    {
        var rows = new[]
        {
            Row("a", "x", Proposal("Whitespace", "x", "x ")),      // has a replacement
            Row("b", "y", Proposal("TagMismatch", "y", "y")),      // flagged only
        };

        var built = QcGrouping.Build(rows, 100);
        Assert.Equal(1, built.Fixable);
        Assert.Equal(1, built.Review);
        Assert.Equal(0, built.Groups.Single(g => g.Kind == "TagMismatch").FixableCount);
    }

    [Fact]
    public void A_stale_proposal_is_listed_but_never_offered_for_bulk_fixing()
    {
        // The row was edited after the review: applying would revert that edit.
        var row = Row("a", "edited since the run", Proposal("Whitespace", "x", "x "));

        var built = QcGrouping.Build(new[] { row }, 100);
        Assert.Equal(1, built.Groups[0].Count);       // still visible…
        Assert.Equal(0, built.Groups[0].FixableCount); // …but not bulk-applied
        Assert.False(QcGrouping.IsApplicable(row, row.QcProposal!));
    }

    [Fact]
    public void Decided_rows_leave_the_list()
    {
        var rows = new[]
        {
            Row("a", "x", Proposal("Whitespace", "x", "x ", verdict: QcVerdict.Fixed)),
            Row("b", "y", Proposal("Whitespace", "y", "y ", verdict: QcVerdict.Kept)),
            Row("c", null!, null),
        };

        var built = QcGrouping.Build(rows, 100);
        Assert.Empty(built.Groups);
        Assert.Equal(0, built.Fixable);
        Assert.Equal(0, built.Review);
    }

    // ── the flat row list the QC tab binds to ────────────────────────────

    private static List<object> TwoGroups(out QcGroupItem first, out QcGroupItem second)
    {
        var rows = new[]
        {
            Row("a", "x", Proposal("TagMismatch", "x", "x", QcSeverity.Critical)),
            Row("b", "y", Proposal("TagMismatch", "y", "y", QcSeverity.Critical)),
            Row("c", "z", Proposal("LengthRatio", "z", "z", QcSeverity.Low)),
        };
        var groups = QcGrouping.Build(rows, 100).Groups;
        first = groups[0];
        second = groups[1];
        return QcGrouping.Flatten(groups);
    }

    [Fact]
    public void A_collapsed_list_shows_only_the_headers()
    {
        var flat = TwoGroups(out _, out _);
        Assert.Equal(2, flat.Count);
        Assert.All(flat, r => Assert.IsType<QcGroupItem>(r));
    }

    [Fact]
    public void Expanding_splices_the_children_in_right_after_their_header()
    {
        var flat = TwoGroups(out var first, out var second);

        QcGrouping.SetExpanded(flat, first, true);

        Assert.Equal(4, flat.Count);
        Assert.Same(first, flat[0]);
        Assert.IsType<ProblemListItem>(flat[1]);
        Assert.IsType<ProblemListItem>(flat[2]);
        Assert.Same(second, flat[3]);   // the other header did not move away
    }

    [Fact]
    public void Collapsing_removes_exactly_its_own_children()
    {
        // The index arithmetic here is the kind that silently eats a neighbour's
        // row: children shift up as each one is removed.
        var flat = TwoGroups(out var first, out var second);
        QcGrouping.SetExpanded(flat, first, true);
        QcGrouping.SetExpanded(flat, second, true);
        Assert.Equal(5, flat.Count);

        QcGrouping.SetExpanded(flat, first, false);

        Assert.Equal(3, flat.Count);
        Assert.Same(first, flat[0]);
        Assert.Same(second, flat[1]);
        Assert.IsType<ProblemListItem>(flat[2]);   // second's child survived
    }

    [Fact]
    public void Expanding_twice_does_not_duplicate_the_children()
    {
        var flat = TwoGroups(out var first, out _);
        QcGrouping.SetExpanded(flat, first, true);
        QcGrouping.SetExpanded(flat, first, true);
        Assert.Equal(4, flat.Count);
    }

    [Fact]
    public void Expanded_categories_survive_a_rebuild()
    {
        // Applying one fix rebuilds the list; collapsing the group the user is
        // working through would lose their place every single time.
        var rows = new[] { Row("a", "x", Proposal("Whitespace", "x", "x ")) };
        var keep = new HashSet<string> { "Whitespace" };

        Assert.True(QcGrouping.Build(rows, 100, keep).Groups[0].IsExpanded);
        Assert.False(QcGrouping.Build(rows, 100).Groups[0].IsExpanded);
    }
}
