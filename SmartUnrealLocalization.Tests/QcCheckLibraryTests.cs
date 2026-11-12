using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services.Qc;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// The checks QcEngine still provides — to the live validator (model chatter,
/// leftover language, protected terms, terminology, line structure) and to the
/// AI review's "skip what cannot be reviewed" rule. QC's separate local pass
/// was removed on 2026-10-21; these cases were kept from its test suite (the
/// RU→UK field report it was built from, and the later audit rounds) for
/// the logic that lives on.
/// </summary>
public class QcCheckLibraryTests
{
    // ── model chatter ───────────────────────────────────────────────────

    [Fact]
    public void Model_chatter_is_stripped()
        => Assert.Equal("Відчини двері", QcEngine.StripModelChatter("Open the door", "Translation: Відчини двері"));

    [Theory]
    [InlineData("Translation: %s", "Переклад: %s")]        // the SOURCE has this shape
    [InlineData("\"Do it now.\"", "\"Зроби це зараз.\"")]  // quoted dialogue is intentional
    public void Text_mirroring_the_source_is_not_chatter(string src, string tgt)
        => Assert.Null(QcEngine.StripModelChatter(src, tgt));

    [Fact]
    public void Unsourced_wrapping_quotes_are_removed()
        => Assert.Equal("Зроби це зараз.", QcEngine.StripModelChatter("Do it now.", "\"Зроби це зараз.\""));

    [Fact]
    public void A_target_that_is_only_chatter_never_becomes_empty()
    {
        Assert.Null(QcEngine.StripModelChatter("Open", "Translation:"));   // no empty "fix"
        Assert.True(QcEngine.IsOnlyModelChatter("Open", "Translation:"));  // flagged instead
    }

    // ── leftover language ───────────────────────────────────────────────

    [Theory]
    [InlineData("Що тепер, ыгры")]     // letters that cannot exist in Ukrainian
    [InlineData("Але что робити")]     // untranslated Russian function word
    public void Language_leaks_are_detected(string tgt)
        => Assert.NotNull(QcEngine.LanguageLeak(tgt, "Ukrainian", Array.Empty<string>()));

    [Fact]
    public void Leak_exceptions_are_case_insensitive()
        => Assert.Null(QcEngine.LanguageLeak("Але что робити", "Ukrainian", new[] { "Что" }));

    [Fact]
    public void An_unknown_language_reports_nothing()
        => Assert.Null(QcEngine.LanguageLeak("Але что робити", "", Array.Empty<string>()));

    // ── terminology and protected terms ─────────────────────────────────

    private static IReadOnlyDictionary<string, string> Terms(params string[] lines) => QcEngine.ParseTermFixes(lines);

    [Fact]
    public void Known_bad_terminology_is_replaced()
        => Assert.Equal("Виявлено пробоїна", QcEngine.ApplyTerminology("Виявлено пролом", Terms("пролом=пробоїна"))!.Value.Fixed);

    [Theory]
    [InlineData("пролом=пробоїна", "пролом", "пробоїна")]
    [InlineData("  пролом = пробоїна  ", "пролом", "пробоїна")]     // sides are trimmed
    [InlineData("a=b=c", "a", "b=c")]                                // only the FIRST '=' splits
    public void Term_fix_lines_are_parsed_as_wrong_equals_correct(string line, string bad, string good)
        => Assert.Equal(good, QcEngine.ParseTermFixes(new[] { line })[bad]);

    [Theory]
    [InlineData("no separator here")]
    [InlineData("=missing left side")]
    [InlineData("missing right side=")]
    [InlineData("   ")]
    public void Malformed_term_fix_lines_are_skipped_not_guessed_at(string line)
        => Assert.Empty(QcEngine.ParseTermFixes(new[] { line }));

    [Fact]
    public void Duplicate_term_fix_keys_keep_the_first_line()
        => Assert.Equal("пробоїна", QcEngine.ParseTermFixes(new[] { "пролом=пробоїна", "пролом=дірка" })["пролом"]);

    [Fact]
    public void A_terminology_fix_keeps_the_sentences_capital_letter()
        => Assert.Equal("Пробоїна широкий", QcEngine.ApplyTerminology("Пролом широкий", Terms("пролом=пробоїна"))!.Value.Fixed);

    [Fact]
    public void A_terminology_fix_matches_an_all_caps_word()
        => Assert.Equal("ПРОБОЇНА", QcEngine.ApplyTerminology("ПРОЛОМ", Terms("пролом=пробоїна"))!.Value.Fixed);

    [Fact]
    public void A_terminology_rule_that_is_capitalised_on_purpose_keeps_its_capital()
        => Assert.Contains("Київ", QcEngine.ApplyTerminology("Йти до kyiv", Terms("kyiv=Київ"))!.Value.Fixed);

    [Fact]
    public void A_protected_term_missing_from_the_translation_is_reported()
    {
        Assert.NotNull(QcEngine.MissingProtectedTerms("Directive 8020 active", "Директива активна", new[] { "Directive 8020" }));
        Assert.Null(QcEngine.MissingProtectedTerms("Directive 8020 active", "Directive 8020 активна", new[] { "Directive 8020" }));
    }

    // ── line structure ──────────────────────────────────────────────────

    [Fact]
    public void Losing_every_line_break_is_reported()
        => Assert.NotNull(QcEngine.LineBreakProblem("A\nB\nC", "А Б В"));

    [Fact]
    public void Inventing_line_breaks_is_reported()
        => Assert.NotNull(QcEngine.LineBreakProblem("One line", "Один\nрядок"));

    [Fact]
    public void Reflowing_prose_by_one_break_is_not_reported()
    {
        // 33 → 32 breaks in a book page is normal translator behaviour; reporting
        // it is what made a real file look alarming.
        var source = string.Join("\n", Enumerable.Range(0, 6).Select(i => "line " + i));
        var target = string.Join("\n", Enumerable.Range(0, 5).Select(i => "wiersz " + i));
        Assert.Null(QcEngine.LineBreakProblem(source, target));
    }

    [Fact]
    public void A_trailing_break_difference_is_not_a_line_structure_problem()
        => Assert.Null(QcEngine.LineBreakProblem("Page\n", "Strona"));

    // ── what the AI review skips ────────────────────────────────────────

    [Theory]
    [InlineData("", "щось")]              // no original to compare against
    [InlineData("Something", "")]          // nothing translated
    [InlineData("<br/>{0}", "<br/>{0}")]   // tag/number only (auto-carried)
    public void Rows_without_reviewable_content_are_skipped(string original, string result)
        => Assert.True(QcEngine.ShouldSkipTexts(original, result));

    [Fact]
    public void A_row_that_is_only_real_tags_is_skipped()
        => Assert.True(QcEngine.ShouldSkip(new TranslationRow { Source = "[b][/b]", Target = "[b][/b]" }));

    [Fact]
    public void A_real_pair_is_not_skipped()
        => Assert.False(QcEngine.ShouldSkipTexts("Wait...", "Чекай"));
}
