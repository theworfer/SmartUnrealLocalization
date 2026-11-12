using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using SmartUnrealLocalization.Services.Qc;
using SmartUnrealLocalization.Services.Validators;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// The live validator and the guard on AI fixes (<see cref="QcAiRunner.RejectFix"/>)
/// must never DISAGREE about structure.
///
/// <para>This file used to align the validator with QC's local pass; that pass
/// was removed on 2026-10-21 and the validator is now the only rule set. What is
/// left to keep aligned is the guard: if it throws out a model's fix for a
/// broken placeholder or tag, the validator must call that text broken too —
/// otherwise the user is told a fix is unsafe that F8 would happily accept.
/// The guard only COUNTS structure (renamed placeholders are the validator's
/// job), so the agreement runs one way: a guard rejection implies a finding.</para>
/// </summary>
public class RuleAgreementTests
{
    private static readonly ValidationService Live = ValidationService.Instance;
    /// <summary>Rows the validator must flag, or must leave alone.</summary>
    public static IEnumerable<object[]> StructuralRows() => new List<object[]>
    {
        // ── genuinely broken ──
        new object[] { "Give %d gold", "Daj złota", true },
        new object[] { "Hello %s", "Witaj", true },
        new object[] { "Hi %1$s and %2$s", "Cześć %2$s", true },
        new object[] { "You have {gold} gold", "Masz {złoto} złota", true },
        new object[] { "Fire {0} arrows", "Wystrzel strzały", true },
        new object[] { "<b>Bold</b>", "Pogrubiony", true },
        new object[] { "[b]Bold[/b]", "Pogrubiony", true },
        new object[] { " Loading", "Ładowanie", true },
        new object[] { "Page one\n", "Strona jeden", true },
        new object[] { "Are you sure?", "Czy na pewno", true },
        new object[] { "Health:", "Zdrowie", true },

        // ── correct, and must stay quiet on BOTH sides ──
        new object[] { "Give %d gold", "Daj %d złota", false },
        new object[] { "You have {gold} gold", "Masz {gold} złota", false },
        new object[] { "<b>Bold</b> text", "<b>Pogrubiony</b> tekst", false },
        new object[] { "[b]Bold[/b] text", "[b]Pogrubiony[/b] tekst", false },
        new object[] { "100% done", "100% zrobione", false },
        new object[] { " Loading ", " Ładowanie ", false },
        new object[] { "Are you sure?", "Czy na pewno?", false },
        new object[] { "[The page is torn]", "[Strona jest podarta]", false },
        new object[] { "Mysterious Orb [Charged]", "Tajemnicza sfera [Naładowana]", false },
        new object[] { "Sword [NEW]", "Miecz [NOWY]", false },
        new object[] { "Hello [PLAYER_NAME]", "Witaj [IMIĘ_GRACZA]", true },
    };

    private static readonly string[] StructuralLiveKinds =
    {
        "PlaceholderMismatch", "TokenMismatch", "TagMismatch", "Whitespace", "Punctuation",
    };

    private static List<string> LiveKinds(string src, string tgt)
    {
        var row = new TranslationRow { Namespace = "NS", Key = "K", Source = src, Target = tgt };
        var found = new List<Problem>();
        foreach (var v in Live.LightValidators) v.Validate(row, found);
        return found.Select(p => p.Kind).ToList();
    }

    [Theory]
    [MemberData(nameof(StructuralRows))]
    public void The_validator_flags_exactly_the_structurally_broken_rows(
        string src, string tgt, bool expectBroken)
    {
        bool live = LiveKinds(src, tgt).Intersect(StructuralLiveKinds).Any();
        Assert.True(live == expectBroken,
            $"F8 says broken={live}, expected {expectBroken} for [{src}] → [{tgt}]");
    }

    [Theory]
    [MemberData(nameof(StructuralRows))]
    public void A_fix_the_AI_guard_rejects_for_structure_is_text_the_validator_flags(
        string src, string tgt, bool expectBroken)
    {
        _ = expectBroken;
        // Offer the row's target as a model's "fix" for some other text.
        string? why = QcAiRunner.RejectFix(src, "\u0001", tgt);
        bool guardRejects = why is not null && (why.Contains("placeholder") || why.Contains("tag"));
        if (!guardRejects) return;

        var kinds = LiveKinds(src, tgt);
        Assert.True(kinds.Intersect(new[] { "PlaceholderMismatch", "TokenMismatch", "TagMismatch" }).Any(),
            $"the guard rejected [{tgt}] ({why}) but the validator sees no structural problem");
    }

    [Fact]
    public void A_dropped_printf_placeholder_is_an_error()
        => Assert.Contains("PlaceholderMismatch", LiveKinds("Give %d gold", "Daj złota"));

    [Fact]
    public void A_bracket_tag_is_markup_and_bracketed_prose_is_not()
    {
        Assert.Contains("TagMismatch", LiveKinds("[b]Bold[/b]", "Pogrubiony"));
        Assert.DoesNotContain("TagMismatch", LiveKinds("[The page is torn]", "[Strona podarta]"));
    }

    [Fact]
    public void The_validator_and_the_guard_use_the_same_definition_of_a_placeholder()
    {
        // Not "they behave alike" but "they read from the same constant" — the
        // only version of this guarantee that cannot drift.
        Assert.Same(LocalizationRules.Placeholder, QcEngine.Placeholder);
        Assert.Same(LocalizationRules.AngleTag, QcEngine.AngleTag);
        Assert.Same(LocalizationRules.BracketTag, QcEngine.BracketTag);
    }

    [Fact]
    public void Placeholder_order_changes_are_not_a_live_error()
    {
        // Reordering placeholders is normal in a free-word-order language, and a
        // per-keystroke Error for it would fight the translator as they type.
        Assert.DoesNotContain("PlaceholderMismatch", LiveKinds("{a} then {b}", "{b} potem {a}"));
        Assert.DoesNotContain("TokenMismatch", LiveKinds("{a} then {b}", "{b} potem {a}"));
    }

    [Fact]
    public void Duplicates_are_checked_by_the_heavy_pass_not_per_keystroke()
    {
        Assert.DoesNotContain("DuplicateSource", LiveKinds("Cancel", "Anuluj"));
        Assert.Contains("DuplicateSource", Live.HeavyValidators.Select(v => v.Kind));
    }

    /// <summary>A validator fix must never multiply the placeholders of the row it repairs.</summary>
    [Theory]
    [MemberData(nameof(StructuralRows))]
    public void No_validator_fix_adds_a_placeholder(string src, string tgt, bool expectBroken)
    {
        _ = expectBroken;
        int inSource = LocalizationRules.Placeholder.Matches(src).Count;
        if (inSource == 0) return;

        var row = new TranslationRow { Namespace = "NS", Key = "K", Source = src, Target = tgt };
        var found = new List<Problem>();
        foreach (var v in Live.LightValidators) v.Validate(row, found);

        foreach (var p in found)
        {
            if (p.Fix is not { } fix) continue;
            Assert.True(LocalizationRules.Placeholder.Matches(fix.NewValue).Count <= inSource,
                        $"F8 {p.Kind} multiplied placeholders: [{fix.NewValue}]");
        }
    }
}