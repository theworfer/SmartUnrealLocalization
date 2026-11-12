using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Utils;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// The Problems tab shows validator findings and QC suggestions in one list, so
/// a single view answers "what is wrong with this file". The two keep separate
/// lifecycles: a QC entry must never look fixable when its suggestion cannot
/// safely be applied.
/// </summary>
public class ProblemListItemTests
{
    private static TranslationRow Row(string target = "Чекай", string source = "Wait...")
        => new() { Namespace = "UI", Key = "K1", Source = source, Target = target };

    private static QcProposal Proposal(string before = "Чекай", string after = "Чекай...",
                                       QcSeverity severity = QcSeverity.Medium,
                                       string kind = "PunctuationMismatch") => new()
    {
        Namespace = "UI", Key = "K1", Before = before, After = after,
        Reason = "Restored the source's ending punctuation", Kinds = new List<string> { kind },
        Severity = severity, Source = "deterministic",
    };

    // ── the two sources stay distinguishable ─────────────────────────────

    [Fact]
    public void A_validator_entry_is_not_marked_as_qc()
    {
        var item = new ProblemListItem(Row(), new Problem
        {
            Kind = "TagMismatch", Severity = ProblemSeverity.Error, Title = "tags differ",
        });

        Assert.False(item.IsQc);
        Assert.Null(item.Qc);
        Assert.Equal("TAG-MISMATCH", item.KindLabel);
    }

    [Fact]
    public void A_qc_entry_is_labelled_so_the_source_is_obvious()
    {
        var item = new ProblemListItem(Row(), Proposal());

        Assert.True(item.IsQc);
        Assert.StartsWith("QC ", item.KindLabel);
        Assert.Equal("Restored the source's ending punctuation", item.Title);
    }

    // ── severity mapping ─────────────────────────────────────────────────

    [Theory]
    [InlineData(QcSeverity.Critical, ProblemSeverity.Error)]
    [InlineData(QcSeverity.High, ProblemSeverity.Error)]
    [InlineData(QcSeverity.Medium, ProblemSeverity.Warning)]
    [InlineData(QcSeverity.Low, ProblemSeverity.Info)]
    public void Qc_severities_map_onto_the_validators_three_levels(QcSeverity qc, ProblemSeverity expected)
    {
        // Both kinds sort and colour together, so the mapping has to be total.
        var item = new ProblemListItem(Row(), Proposal(severity: qc));

        Assert.Equal(expected, item.Severity);
        Assert.Equal(expected.ToString(), item.SeverityName);
    }

    [Fact]
    public void The_glyph_follows_the_mapped_severity()
    {
        Assert.Equal(Glyphs.ErrorRound, new ProblemListItem(Row(), Proposal(severity: QcSeverity.Critical)).Glyph);
        Assert.Equal(Glyphs.Warning, new ProblemListItem(Row(), Proposal(severity: QcSeverity.Medium)).Glyph);
        Assert.Equal(Glyphs.InfoRound, new ProblemListItem(Row(), Proposal(severity: QcSeverity.Low)).Glyph);
    }

    // ── when Fix may be offered ──────────────────────────────────────────

    [Fact]
    public void A_qc_suggestion_with_a_replacement_is_fixable()
        => Assert.True(new ProblemListItem(Row(), Proposal()).HasFix);

    [Fact]
    public void A_flag_only_qc_finding_offers_no_fix()
    {
        // Before == After means "flagged, no automatic replacement" — the entry
        // must not show a Fix button that would do nothing.
        var item = new ProblemListItem(Row(), Proposal(before: "Чекай", after: "Чекай"));
        Assert.False(item.HasFix);
    }

    [Fact]
    public void A_stale_qc_suggestion_offers_no_fix()
    {
        // The row changed after the review; applying would revert that edit.
        var row = Row(target: "Чекай трохи");
        Assert.False(new ProblemListItem(row, Proposal(before: "Чекай")).HasFix);
    }

    [Fact]
    public void Staleness_of_a_source_reviewed_suggestion_reads_Source()
    {
        // Single-language file: the reviewed text is in Source, Target is empty
        // and reserved for the fix — measuring against Target would call every
        // such entry stale and hide its Fix button.
        var row = new TranslationRow { Namespace = "UI", Key = "K1", Source = "Чекай", Target = "" };
        var proposal = Proposal(before: "Чекай");
        proposal.ResultInSource = true;

        Assert.True(new ProblemListItem(row, proposal).HasFix);
    }

    // ── hover preview ────────────────────────────────────────────────────

    [Fact]
    public void The_tooltip_shows_the_actual_replacement()
    {
        // Seeing what would change without opening each row's badge is the whole
        // reason the suggestions are listed here.
        string tip = new ProblemListItem(Row(), Proposal()).Tooltip;

        Assert.Contains("− Чекай", tip);
        Assert.Contains("+ Чекай...", tip);
    }

    [Fact]
    public void A_lost_trailing_space_is_made_visible_in_the_preview()
    {
        // The single commonest QC finding is a dropped edge space. Previewing it
        // verbatim shows two identical-looking lines, so the difference is real
        // but invisible — the preview has to mark it.
        var item = new ProblemListItem(Row(), Proposal(before: "Level", after: "Level ",
                                                       kind: "Whitespace"));

        Assert.Contains("− Level\n", item.Tooltip);
        Assert.Contains("+ Level·", item.Tooltip);
    }

    [Fact]
    public void A_leading_space_is_marked_too()
    {
        var item = new ProblemListItem(Row(), Proposal(before: "Level", after: " Level",
                                                       kind: "Whitespace"));
        Assert.Contains("+ ·Level", item.Tooltip);
    }

    [Fact]
    public void Spaces_inside_the_text_are_left_alone()
    {
        // Only the EDGES are ambiguous; dotting every inner space would make
        // ordinary sentences unreadable.
        var item = new ProblemListItem(Row(), Proposal(before: "Play now", after: "Grati zaraz"));

        Assert.Contains("− Play now", item.Tooltip);
        Assert.Contains("+ Grati zaraz", item.Tooltip);
    }

    [Fact]
    public void A_flag_only_entry_says_so_instead_of_showing_an_empty_diff()
    {
        string tip = new ProblemListItem(Row(), Proposal(before: "Чекай", after: "Чекай")).Tooltip;

        Assert.DoesNotContain("+ ", tip);
        Assert.Contains("human", tip);
    }

    [Fact]
    public void A_validator_entry_falls_back_to_its_title_when_it_has_no_detail()
    {
        var withDetail = new ProblemListItem(Row(), new Problem
        {
            Kind = "TagMismatch", Severity = ProblemSeverity.Error,
            Title = "tags differ", Detail = "expected <b>, found none",
        });
        var bare = new ProblemListItem(Row(), new Problem
        {
            Kind = "TagMismatch", Severity = ProblemSeverity.Error, Title = "tags differ",
        });

        Assert.Equal("expected <b>, found none", withDetail.Tooltip);
        Assert.Equal("tags differ", bare.Tooltip);
    }

    [Fact]
    public void Every_entry_reports_the_row_key_so_it_can_be_located()
        => Assert.Equal("K1", new ProblemListItem(Row(), Proposal()).RowKey);
}
