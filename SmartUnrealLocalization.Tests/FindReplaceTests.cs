using System.Text.RegularExpressions;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Utils;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// Replace All must act on the text that is there, all of it, or none of it.
///
/// <para>It used to splice at offsets recorded by the last find scan. An edit
/// since that scan — AI batch, undo, pasted column, MCP — could leave them in
/// bounds while pointing at other characters: the 2026-09-09 audit searched
/// "the", edited the row to "xxx cat", ran Replace All with "DOG" and got
/// "DOG cat". The scan lists were also capped for display, so 100 occurrences
/// in one row got 60 replacements. <see cref="FindReplace.PlanReplaceAll"/>
/// plans from the current text instead, and writes nothing itself.</para>
/// </summary>
public class FindReplaceTests
{
    private static readonly Regex The = new("the", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static TranslationRow Row(string? target) => new() { Key = "K", Source = "s", Target = target ?? "" };

    // ── the audit's cases ───────────────────────────────────────────────

    [Fact]
    public void A_same_length_rewrite_since_the_scan_is_not_touched()
    {
        // Scanned as "the cat"; rewritten to "xxx cat" before Replace All.
        var row = Row("xxx cat");

        var plan = FindReplace.PlanReplaceAll(new[] { row }, The, "DOG");

        Assert.Empty(plan);          // the old code produced "DOG cat"
        Assert.Equal("xxx cat", row.Target);
    }

    [Fact]
    public void A_shifted_occurrence_is_replaced_where_it_is_now()
    {
        // Scanned as "the cat" (hit at 0); now "a the cat" (hit at 2).
        var plan = FindReplace.PlanReplaceAll(new[] { Row("a the cat") }, The, "DOG");

        Assert.Equal("a DOG cat", Assert.Single(plan).NewText);
    }

    [Fact]
    public void Every_occurrence_is_replaced_not_just_the_first_sixty()
    {
        string text = string.Concat(Enumerable.Repeat("the ", 100));

        var edit = Assert.Single(FindReplace.PlanReplaceAll(new[] { Row(text) }, The, "a"));

        Assert.Equal(100, edit.Replacements);
        Assert.DoesNotContain("the", edit.NewText);
    }

    [Fact]
    public void A_timeout_aborts_before_anything_is_planned()
    {
        // Catastrophic backtracking with a 1 ms budget. Planning throws, and
        // since planning never writes, no row has changed — all or nothing.
        var evil = new Regex("(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(1));
        var rows = new[] { Row("aaaa"), Row(new string('a', 40) + "!") };

        Assert.Throws<RegexMatchTimeoutException>(() => FindReplace.PlanReplaceAll(rows, evil, "x"));
        Assert.Equal("aaaa", rows[0].Target);
    }

    // ── semantics the find bar has always had ───────────────────────────

    [Fact]
    public void The_replacement_is_literal_even_in_regex_mode()
    {
        var grouped = new Regex("(t)he");

        var edit = Assert.Single(FindReplace.PlanReplaceAll(new[] { Row("the cat") }, grouped, "$1X"));

        Assert.Equal("$1X cat", edit.NewText);
    }

    [Fact]
    public void Zero_width_matches_insert_nothing()
    {
        // "x*" matches the empty string between every pair of characters.
        Assert.Empty(FindReplace.PlanReplaceAll(new[] { Row("abc") }, new Regex("x*"), "!"));
    }

    [Fact]
    public void Case_and_whole_word_come_from_the_matcher()
    {
        var wholeWord = new Regex(@"(?<!\p{L})the(?!\p{L})", RegexOptions.IgnoreCase);

        var edit = Assert.Single(FindReplace.PlanReplaceAll(new[] { Row("The theme") }, wholeWord, "DOG"));

        Assert.Equal("DOG theme", edit.NewText);
    }

    [Fact]
    public void Rows_that_would_not_change_are_left_out()
    {
        var rows = new[] { Row(""), Row(null), Row("no match here"), Row("the end") };

        var plan = FindReplace.PlanReplaceAll(rows, The, "an");

        Assert.Same(rows[3], Assert.Single(plan).Row);
    }

    [Fact]
    public void Planning_never_writes()
    {
        var row = Row("the cat");

        FindReplace.PlanReplaceAll(new[] { row }, The, "DOG");

        Assert.Equal("the cat", row.Target);   // applying is the caller's job, as one undo step
    }

    [Fact]
    public void The_plan_keeps_the_old_text_for_undo()
    {
        var edit = Assert.Single(FindReplace.PlanReplaceAll(new[] { Row("the cat") }, The, "DOG"));

        Assert.Equal("the cat", edit.OldText);
        Assert.Equal("DOG cat", edit.NewText);
        Assert.Equal(1, edit.Replacements);
    }
}
