using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using SmartUnrealLocalization.Services.Validators;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// The live validation pass — what F8 and file-open report. Until this audit it
/// had no tests at all, which is how a missing printf check and a whitespace rule
/// that only looked in one direction survived: every defect below was found by
/// reading the code against the QC engine, and every one is now pinned here.
///
/// <para>These checks matter more than most: they run on every keystroke and are
/// the only thing standing between a translator and a string that crashes the
/// game's format call.</para>
/// </summary>
public class ValidatorTests
{
    private static TranslationRow Row(string source, string target)
        => new() { Namespace = "NS", Key = "K", Source = source, Target = target };

    private static List<Problem> Run(IValidator v, string source, string target)
    {
        var output = new List<Problem>();
        v.Validate(Row(source, target), output);
        return output;
    }

    private static Problem? One(IValidator v, string source, string target)
        => Run(v, source, target).FirstOrDefault();

    // ── printf placeholders: the check that did not exist ─────────────────

    private static readonly PlaceholderMismatchValidator Placeholders = new();

    [Theory]
    [InlineData("Give %d gold", "Daj złota")]                 // dropped
    [InlineData("Hello %s", "Witaj")]                         // dropped
    [InlineData("Hi %1$s and %2$s", "Cześć %2$s")]            // positional dropped
    [InlineData("Deal %.2f damage", "Zadaj obrażenia")]       // precision dropped
    [InlineData("Wait %d s", "Czekaj %d s %d")]               // invented an extra
    public void A_broken_printf_placeholder_is_an_error(string src, string tgt)
    {
        var p = One(Placeholders, src, tgt);
        Assert.NotNull(p);
        Assert.Equal(ProblemSeverity.Error, p!.Severity);
    }

    [Theory]
    [InlineData("Give %d gold", "Daj %d złota")]
    [InlineData("Hi %1$s and %2$s", "Cześć %1$s i %2$s")]
    [InlineData("Deal %.2f damage", "Zadaj %.2f obrażeń")]
    [InlineData("Pad %02d", "Wypełnij %02d")]
    [InlineData("Long %ld", "Długi %ld")]
    public void A_correct_translation_is_silent(string src, string tgt)
        => Assert.Empty(Run(Placeholders, src, tgt));

    [Fact]
    public void Ordinary_prose_with_a_percent_sign_is_not_a_placeholder()
    {
        // "% d" is a legal printf format (space flag), but game text is full of
        // "100% done" — accepting the space flag would invent a placeholder in
        // every one of them and report the translation for "dropping" it.
        Assert.Empty(Run(Placeholders, "100% done", "100% zrobione"));
        Assert.Empty(Run(Placeholders, "50% off", "50% zniżki"));
    }

    [Fact]
    public void A_doubled_percent_is_a_literal_not_a_placeholder()
        => Assert.Empty(Run(Placeholders, "Progress: 100%%", "Postęp: 100%%"));

    [Fact]
    public void A_typo_placeholder_can_be_replaced_in_place()
    {
        var p = One(Placeholders, "Give %d gold", "Daj %s złota");
        Assert.NotNull(p!.Fix);
        Assert.Equal("Daj %d złota", p.Fix!.NewValue);
    }

    [Fact]
    public void A_missing_placeholder_with_nothing_to_swap_offers_no_fix()
    {
        // Appending a bare "%d" to the end of a sentence puts the number in the
        // wrong place. Better to report it and let a human place it.
        Assert.Null(One(Placeholders, "Give %d gold", "Daj złota")!.Fix);
    }

    // ── whitespace: it used to look in one direction only ─────────────────

    private static readonly WhitespaceValidator Whitespace = new();

    [Fact]
    public void Whitespace_the_translation_LOST_is_reported()
    {
        // The damaging direction: strings are concatenated at runtime, so a
        // dropped leading space glues two words together. This was invisible.
        var p = One(Whitespace, " Loading", "Ładowanie");
        Assert.NotNull(p);
        Assert.Equal(" Ładowanie", p!.Fix!.NewValue);
    }

    [Fact]
    public void A_lost_trailing_line_break_is_reported_and_restored()
    {
        var p = One(Whitespace, "Page one\n", "Strona jeden");
        Assert.NotNull(p);
        Assert.Equal("Strona jeden\n", p!.Fix!.NewValue);
    }

    [Fact]
    public void Whitespace_the_translation_ADDED_is_still_reported()
    {
        var p = One(Whitespace, "Loading", " Ładowanie ");
        Assert.NotNull(p);
        Assert.Equal("Ładowanie", p!.Fix!.NewValue);
    }

    [Fact]
    public void Edge_whitespace_that_mirrors_the_source_is_left_alone()
    {
        // A " / " separator entry keeps its spaces on purpose.
        Assert.Empty(Run(Whitespace, " / ", " / "));
        Assert.Empty(Run(Whitespace, " Loading ", " Ładowanie "));
    }

    [Fact]
    public void The_whitespace_fix_never_touches_inner_text()
    {
        var p = One(Whitespace, "A\nB\n", "X\nY");
        Assert.Equal("X\nY\n", p!.Fix!.NewValue);
    }

    [Fact]
    public void Doubled_inner_spaces_are_collapsed_only_when_the_source_has_none()
    {
        Assert.NotNull(One(Whitespace, "a b", "x  y"));
        Assert.Empty(Run(Whitespace, "a  b", "x  y"));
    }

    // ── markup tags ───────────────────────────────────────────────────────

    private static readonly TagMismatchValidator Tags = new();

    [Fact]
    public void A_dropped_angle_tag_is_an_error()
        => Assert.Equal(ProblemSeverity.Error, One(Tags, "<b>Bold</b>", "Pogrubiony")!.Severity);

    [Fact]
    public void A_dropped_bracket_tag_is_an_error()
    {
        // [b] / [/i] markup was checked by QC only — the live pass never saw it.
        Assert.NotNull(One(Tags, "[b]Bold[/b]", "Pogrubiony"));
    }

    [Fact]
    public void Bracketed_prose_is_not_a_tag()
    {
        // Narrative asides are text the translator is meant to translate.
        Assert.Empty(Run(Tags,
            "[The rest of the page has been torn off]",
            "[Reszta strony została wyrwana]"));
    }

    [Theory]
    // Reported from real data: a red Error on a perfectly good translation.
    [InlineData("Mysterious Orb [Charged]", "Tajemnicza sfera [Naładowana]")]
    [InlineData("Mysterious Orb [Uncharged]", "Tajemnicza sfera [Nienaładowana]")]
    [InlineData("Chest [Locked]", "Skrzynia [Zamknięta]")]
    [InlineData("Sword [NEW]", "Miecz [NOWY]")]
    public void A_capitalised_word_in_brackets_is_text_not_markup(string src, string tgt)
    {
        // Markup is written in lower case by every convention that uses square
        // brackets. A capitalised word inside them is a word, and a translator
        // is right to translate it — flagging that put an Error on good rows.
        Assert.Empty(Run(Tags, src, tgt));
    }

    [Theory]
    [InlineData("[b]Bold[/b]", "Pogrubiony")]              // lower-case code
    [InlineData("[color=red]Red[/color]", "Czerwony")]     // parameterised
    [InlineData("Hello [PLAYER_NAME]", "Witaj")]           // variable name
    public void Code_shaped_brackets_are_still_markup(string src, string tgt)
        => Assert.NotNull(One(Tags, src, tgt));

    [Fact]
    public void A_variable_name_in_brackets_must_not_be_translated()
    {
        // The counter-case to [Charged]: [PLAYER_NAME] carries an underscore, so
        // it reads as a substitution and translating it breaks the game.
        Assert.NotNull(One(Tags, "Hello [PLAYER_NAME]", "Witaj [IMIĘ_GRACZA]"));
    }

    [Fact]
    public void A_tag_the_translation_invented_is_reported()
    {
        // Extras with nothing missing used to return silently — a stray pasted
        // <b> broke rendering and nothing said so.
        var p = One(Tags, "Plain text", "<b>Zwykły tekst</b>");
        Assert.NotNull(p);
        Assert.Null(p!.Fix);          // deleting it may not be the right correction
    }

    [Fact]
    public void A_typo_tag_is_replaced_in_place_not_appended()
    {
        var p = One(Tags, "<b>Bold</b>", "<b>Pogrubiony</bb>");
        Assert.Contains("</b>", p!.Fix!.NewValue);
        Assert.DoesNotContain("</bb>", p.Fix.NewValue);
    }

    [Fact]
    public void Restoring_a_tag_does_not_delete_a_trailing_line_break()
    {
        // The append path used TrimEnd(), which ate the edge run wholesale — a
        // "fix the tag" click silently changed how the string wrapped.
        var p = One(Tags, "<b>Title</b>\n", "Tytuł\n");
        Assert.EndsWith("\n", p!.Fix!.NewValue);
    }

    [Fact]
    public void A_correctly_translated_tagged_string_is_silent()
        => Assert.Empty(Run(Tags, "<b>Bold</b> and <i>italic</i>", "<b>Pogrubiony</b> i <i>kursywa</i>"));

    // ── {token} substitution ──────────────────────────────────────────────

    private static readonly TokenMismatchValidator Tokens = new();

    [Fact]
    public void A_translated_token_name_is_an_error()
        => Assert.Equal(ProblemSeverity.Error, One(Tokens, "You have {gold} gold", "Masz {złoto} złota")!.Severity);

    [Fact]
    public void A_dropped_duplicate_token_is_counted()
    {
        // Multiset, not set: "{0} of {0}" → "{0}" is one MISSING token.
        Assert.NotNull(One(Tokens, "{0} of {0}", "{0} z"));
    }

    [Fact]
    public void A_duplicated_token_is_reported()
        => Assert.NotNull(One(Tokens, "Fire {0} arrows", "Wystrzel {0} strzał {0}"));

    [Fact]
    public void Matching_tokens_are_silent()
        => Assert.Empty(Run(Tokens, "You have {gold} gold", "Masz {gold} złota"));

    // ── ending punctuation ────────────────────────────────────────────────

    private static readonly PunctuationValidator Punctuation = new();

    [Fact]
    public void A_dropped_question_mark_is_reported()
        => Assert.NotNull(One(Punctuation, "Are you sure?", "Czy na pewno"));

    [Fact]
    public void A_dropped_colon_is_reported()
    {
        // A label the UI concatenates a value onto — "Health:" → "Zdrowie" runs
        // the number straight into the word. Shared with QC's punctuation set.
        var p = One(Punctuation, "Health:", "Zdrowie");
        Assert.NotNull(p);
        Assert.Equal("Zdrowie:", p!.Fix!.NewValue);
    }

    [Fact]
    public void Punctuation_the_translation_added_on_its_own_is_left_alone()
        => Assert.Empty(Run(Punctuation, "Cancel", "Anuluj."));

    [Fact]
    public void The_punctuation_fix_keeps_a_trailing_line_break_in_place()
    {
        // The mark must land BEFORE the break, not after it.
        var p = One(Punctuation, "Wait!", "Czekaj\n");
        Assert.Equal("Czekaj!\n", p!.Fix!.NewValue);
    }

    [Fact]
    public void Matching_punctuation_is_silent()
        => Assert.Empty(Run(Punctuation, "Are you sure?", "Czy na pewno?"));

    // ── plural directives ─────────────────────────────────────────────────

    private static readonly PluralIncompleteValidator Plurals = new();

    [Fact]
    public void A_dropped_plural_directive_is_an_error()
        => Assert.Equal(ProblemSeverity.Error,
                        One(Plurals, "{n}|plural(one=\"item\", other=\"items\")", "{n} przedmiotów")!.Severity);

    [Fact]
    public void Fewer_forms_than_the_source_is_an_error()
        => Assert.NotNull(One(Plurals,
            "{n}|plural(one=\"item\", other=\"items\")",
            "{n}|plural(other=\"przedmiotów\")"));

    [Fact]
    public void More_forms_than_the_source_is_fine()
        // Ukrainian and Polish need more forms than English — that is correct work.
        => Assert.Empty(Run(Plurals,
            "{n}|plural(one=\"item\", other=\"items\")",
            "{n}|plural(one=\"przedmiot\", few=\"przedmioty\", many=\"przedmiotów\", other=\"przedmiotu\")"));

    [Fact]
    public void A_parenthesis_inside_a_form_does_not_invent_an_error()
    {
        // The block parser used to stop at the first ')', hiding every form
        // keyword after it. With the parenthesis in the TRANSLATION only, the
        // target looked short of forms and the row was reported — falsely.
        Assert.Empty(Run(Plurals,
            "{n}|plural(one=\"item\", other=\"items\")",
            "{n}|plural(one=\"przedmiot (jeden)\", other=\"przedmiotów\")"));
    }

    // ── content mismatch ──────────────────────────────────────────────────

    private static readonly ContentMismatchValidator Content = new();

    [Fact]
    public void A_phrase_translated_into_digits_is_a_warning()
        => Assert.Equal(ProblemSeverity.Warning, One(Content, "Open", "123")!.Severity);

    [Fact]
    public void A_symbol_only_source_should_come_through_unchanged()
    {
        var p = One(Content, "/", "|");
        Assert.Equal("/", p!.Fix!.NewValue);
    }

    [Fact]
    public void A_normal_translation_is_silent()
        => Assert.Empty(Run(Content, "Open", "Otwórz"));

    // ── mixed scripts ─────────────────────────────────────────────────────

    private static readonly MixedScriptsValidator Scripts = new();

    [Fact]
    public void A_latin_letter_hiding_in_a_cyrillic_word_is_a_warning()
    {
        // "Кiлька" with a Latin "i" — identical glyph, broken string.
        var p = One(Scripts, "Several", "Кiлька");
        Assert.Equal(ProblemSeverity.Warning, p!.Severity);
    }

    [Fact]
    public void Separate_latin_and_cyrillic_words_are_fine()
        => Assert.Empty(Run(Scripts, "Open Steam", "Відкрити Steam"));

    // ── the registry ──────────────────────────────────────────────────────

    [Fact]
    public void Every_validator_in_this_file_actually_runs_in_the_app()
    {
        // A validator that exists but is not registered protects nobody, and a
        // unit test that constructs it directly will still pass. This is the only
        // check that would have caught the printf gap: the SHAPE was known to QC
        // all along, the live pass simply never ran a check for it.
        var live = ValidationService.Instance;
        var kinds = live.LightValidators.Select(v => v.Kind)
            .Concat(live.HeavyValidators.Select(v => v.Kind))
            .ToHashSet();

        Assert.Contains("PlaceholderMismatch", kinds);
        Assert.Contains("TagMismatch", kinds);
        Assert.Contains("TokenMismatch", kinds);
        Assert.Contains("Whitespace", kinds);
        Assert.Contains("Punctuation", kinds);
        Assert.Contains("MixedScripts", kinds);
        Assert.Contains("ContentMismatch", kinds);
        Assert.Contains("PluralIncomplete", kinds);
        Assert.Contains("LengthExcessive", kinds);
        Assert.Contains("DuplicateSource", kinds);
    }

    [Fact]
    public void No_two_validators_claim_the_same_kind()
    {
        // Kinds drive which problems get replaced on a re-run. Two validators
        // sharing one would wipe each other's findings on every keystroke.
        var live = ValidationService.Instance;
        var kinds = live.LightValidators.Select(v => v.Kind)
            .Concat(live.HeavyValidators.Select(v => v.Kind))
            .ToList();

        Assert.Equal(kinds.Count, kinds.Distinct().Count());
    }

    // ── every validator ignores untranslated rows ─────────────────────────

    public static IEnumerable<object[]> AllValidators() => new List<object[]>
    {
        new object[] { new TagMismatchValidator() },
        new object[] { new TokenMismatchValidator() },
        new object[] { new PlaceholderMismatchValidator() },
        new object[] { new WhitespaceValidator() },
        new object[] { new PunctuationValidator() },
        new object[] { new MixedScriptsValidator() },
        new object[] { new ContentMismatchValidator() },
        new object[] { new PluralIncompleteValidator() },
        new object[] { new LengthExcessiveValidator() },
    };

    [Theory]
    [MemberData(nameof(AllValidators))]
    public void An_untranslated_row_is_never_a_problem(IValidator validator)
    {
        // Empty targets belong to the Untranslated counter and filter. A validator
        // that fires here lights up an unfinished file end to end.
        Assert.Empty(Run(validator, "<b>Give %d {gold}</b>", ""));
        Assert.Empty(Run(validator, "<b>Give %d {gold}</b>", "   "));
    }

    [Theory]
    [MemberData(nameof(AllValidators))]
    public void No_validator_throws_on_hostile_input(IValidator validator)
    {
        string[] nasty =
        {
            "", " ", "\n", "{", "}", "<", ">", "[", "]", "%", "%%", "%z",
            "<unclosed", "{unclosed", "[unclosed", "|plural(", "|plural(one=",
            "😀", "​", "…", new string('x', 500),
        };

        foreach (var a in nasty)
            foreach (var b in nasty)
                Run(validator, a, b);     // must not throw
    }

    /// <summary>
    /// Every fix a validator offers must be applicable without corrupting the row:
    /// it must actually change the text, must not blank it, and must not destroy
    /// the placeholders the row still has.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllValidators))]
    public void Every_offered_fix_is_safe(IValidator validator)
    {
        (string Src, string Tgt)[] cases =
        {
            (" Loading", "Ładowanie"), ("Loading", " Ładowanie "), ("Page\n", "Strona"),
            ("<b>Bold</b>", "Pogrubiony"), ("<b>T</b>\n", "T\n"), ("<b>B</b>", "<b>B</bb>"),
            ("You have {gold}", "Masz {złoto}"), ("Give %d gold", "Daj %s złota"),
            ("Are you sure?", "Czy na pewno"), ("Health:", "Zdrowie"), ("Wait!", "Czekaj\n"),
            ("/", "|"), ("a b", "x  y"),
        };

        foreach (var (src, tgt) in cases)
            foreach (var p in Run(validator, src, tgt))
            {
                if (p.Fix is not { } fix) continue;

                Assert.NotEqual(fix.OldValue, fix.NewValue);
                Assert.False(string.IsNullOrWhiteSpace(fix.NewValue),
                             $"{validator.Kind} would blank [{tgt}]");

                // Placeholders present in the ORIGINAL must survive the fix.
                int before = LocalizationRules.Placeholder.Matches(src).Count;
                int after = LocalizationRules.Placeholder.Matches(fix.NewValue).Count;
                if (before > 0)
                    Assert.True(after <= before,
                                $"{validator.Kind} multiplied placeholders: [{fix.NewValue}]");
            }
    }
}
