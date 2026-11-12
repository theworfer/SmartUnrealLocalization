using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services.Validators;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// The duplicate note. It used to elect a "canonical" row and hand every other
/// row a one-click Apply that overwrote its Target — a button that could erase a
/// deliberate per-context translation in bulk. It is now a pointer, not an
/// action, and these tests pin both halves of that: no fix, and a detail that
/// actually names where the other translations are.
/// </summary>
[Collection("AppState")]
public class DuplicateSourceValidatorTests
{
    // The texts are asserted in English; load it rather than rely on test order.
    public DuplicateSourceValidatorTests() => Localization.Loc.Instance.Load("en");

    private static TranslationRow Row(string key, string source, string? target)
        => new() { Namespace = "NS", Key = key, Source = source, Target = target ?? "" };

    private static List<TranslationRow> Validate(params TranslationRow[] rows)
    {
        new DuplicateSourceValidator().ValidateAll(rows);
        return rows.ToList();
    }

    private static Problem? Note(TranslationRow row)
        => row.Problems.FirstOrDefault(p => p.Kind == "DuplicateSource");

    [Fact]
    public void A_duplicate_note_never_offers_a_fix()
    {
        var rows = Validate(Row("A", "Cancel", "Скасувати"), Row("B", "Cancel", "Відміна"));
        Assert.All(rows, r => Assert.Null(Note(r)!.Fix));
    }

    [Fact]
    public void Every_disagreeing_row_is_noted_not_just_the_later_ones()
    {
        // No row is canonical any more — they simply disagree, and privileging
        // the first one was only ever there to aim the (now removed) fix.
        var rows = Validate(Row("A", "Cancel", "Скасувати"), Row("B", "Cancel", "Відміна"));
        Assert.All(rows, r => Assert.NotNull(Note(r)));
    }

    [Fact]
    public void The_note_names_the_other_translation_and_its_key()
    {
        var rows = Validate(Row("A", "Cancel", "Скасувати"), Row("B", "Cancel", "Відміна"));

        Assert.Contains("Відміна", Note(rows[0])!.Detail);
        Assert.Contains("key B", Note(rows[0])!.Detail);
        Assert.Contains("Скасувати", Note(rows[1])!.Detail);
        Assert.Contains("key A", Note(rows[1])!.Detail);
    }

    [Fact]
    public void It_stays_an_advisory_note()
        => Assert.Equal(ProblemSeverity.Info,
                        Note(Validate(Row("A", "Cancel", "Скасувати"), Row("B", "Cancel", "Відміна"))[0])!.Severity);

    [Fact]
    public void Rows_that_agree_are_not_noted()
    {
        var rows = Validate(Row("A", "Cancel", "Скасувати"), Row("B", "Cancel", "Скасувати"));
        Assert.All(rows, r => Assert.Null(Note(r)));
    }

    [Fact]
    public void Untranslated_rows_neither_trigger_nor_receive_a_note()
    {
        // Empty targets are the Untranslated counter's job. Counting "" as a
        // third variant would flag every partially-translated file end to end.
        var rows = Validate(Row("A", "Cancel", "Скасувати"),
                            Row("B", "Cancel", "Скасувати"),
                            Row("C", "Cancel", ""));
        Assert.All(rows, r => Assert.Null(Note(r)));
    }

    [Fact]
    public void Three_variants_are_all_named_from_each_row()
    {
        var rows = Validate(Row("A", "Cancel", "Скасувати"),
                            Row("B", "Cancel", "Відміна"),
                            Row("C", "Cancel", "Назад"));

        var detail = Note(rows[0])!.Detail;
        Assert.Contains("Відміна", detail);
        Assert.Contains("Назад", detail);
        Assert.Contains("different translations: 3", Note(rows[0])!.Title);
    }

    [Fact]
    public void Rows_sharing_a_translation_are_named_once_between_them()
    {
        // Two rows translate it the same way; the odd one out should hear about
        // that translation once, not twice.
        var rows = Validate(Row("A", "Cancel", "Скасувати"),
                            Row("B", "Cancel", "Скасувати"),
                            Row("C", "Cancel", "Відміна"));

        var detail = Note(rows[2])!.Detail;
        int occurrences = detail.Split("Скасувати").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void A_source_used_once_is_never_noted()
        => Assert.Null(Note(Validate(Row("A", "Cancel", "Скасувати"))[0]));
}
