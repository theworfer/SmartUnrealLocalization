using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using SmartUnrealLocalization.Services.Qc;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>Word-level diff shown in the badge popup, and which rows the AI review sends.</summary>
public class QcDiffTests
{
    [Fact]
    public void Only_the_changed_word_is_highlighted()
    {
        var (before, after) = QcDiff.Compute("Виявлено пролом у корпусі", "Виявлено пробоїну у корпусі");
        Assert.Equal("пролом", string.Concat(before.Where(s => s.Changed).Select(s => s.Text)).Trim());
        Assert.Equal("пробоїну", string.Concat(after.Where(s => s.Changed).Select(s => s.Text)).Trim());
    }

    [Theory]
    [InlineData("Привіт світ", "Привіт світ")]
    [InlineData("", "")]
    [InlineData("{0} шкоди", "{0} шкоди")]
    public void Identical_texts_show_nothing_as_changed(string a, string b)
    {
        var (before, after) = QcDiff.Compute(a, b);
        Assert.DoesNotContain(before, s => s.Changed);
        Assert.DoesNotContain(after, s => s.Changed);
    }

    [Theory]
    [InlineData("Привіт світ", "Прощавай світ")]
    [InlineData("A\nB\tC", "A\nX\tC")]
    [InlineData("", "щось нове")]
    [InlineData("щось старе", "")]
    public void Both_sides_reproduce_their_input_verbatim(string a, string b)
    {
        // The popup renders these segments — losing a character would show the
        // user text that is not in the file.
        var (before, after) = QcDiff.Compute(a, b);
        Assert.Equal(a, string.Concat(before.Select(s => s.Text)));
        Assert.Equal(b, string.Concat(after.Select(s => s.Text)));
    }

    [Fact]
    public void A_long_paragraph_still_isolates_the_single_changed_word()
    {
        // Word-level precision must hold for realistic dialogue lengths, not just
        // five-word samples — a fallback threshold accidentally dropped to a few
        // tokens would mark WHOLE paragraphs as changed and the popup would stop
        // showing what actually differs (caught by the mutation audit).
        string before = string.Join(" ", Enumerable.Range(0, 60).Select(i => "слово" + i));
        string after = before.Replace("слово30", "змінено");

        var (b, a) = QcDiff.Compute(before, after);

        Assert.Equal("слово30", string.Concat(b.Where(s => s.Changed).Select(s => s.Text)).Trim());
        Assert.Equal("змінено", string.Concat(a.Where(s => s.Changed).Select(s => s.Text)).Trim());
    }

    [Fact]
    public void A_huge_row_falls_back_instead_of_allocating_a_giant_table()
    {
        string huge = string.Join(" ", Enumerable.Range(0, 4000).Select(i => "word" + i));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (before, after) = QcDiff.Compute(huge, huge + " tail");
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds}ms");
        Assert.Equal(huge, string.Concat(before.Select(s => s.Text)));
        Assert.Equal(huge + " tail", string.Concat(after.Select(s => s.Text)));
    }
}

/// <summary>
/// What the AI review sends (2026-10-21): every row in scope, minus what the
/// user's switches keep out — and every row kept out is counted against the
/// switch that did it, so the panel can show the cost before anything is sent.
/// </summary>
[Collection("AppState")]
public class QcAiScopeTests
{
    private static TranslationRow Row(string key, string src, string tgt,
                                      ReviewStatus review = ReviewStatus.None)
        => new() { Namespace = "N", Key = key, Source = src, Target = tgt, Review = review };

    private static List<TranslationRow> Corpus() => new()
    {
        Row("OK", "Open the door", "Відчини двері"),
        Row("EMPTY", "Something", ""),
        Row("TAGS", "<br/>{0}", "{0}<br/>"),      // nothing to translate, reordered
        Row("SAME", "Kyiv", "Kyiv"),
        Row("APPROVED", "Close the door", "Зачини двері", ReviewStatus.Approved),
    };

    private static List<string> Sent(QcAiScope.Result r) => r.Inputs.Select(i => i.Row.Key!).ToList();

    [Fact]
    public void By_default_every_reviewable_row_is_sent_and_each_skip_is_counted()
    {
        var r = QcAiScope.Build(Corpus(), new QcAiScope.Options());
        Assert.Equal(new[] { "OK" }, Sent(r));
        Assert.Equal(1, r.SkippedEmpty);
        Assert.Equal(1, r.SkippedAutoCarried);
        Assert.Equal(1, r.SkippedIdentical);
        Assert.Equal(1, r.SkippedApproved);
        Assert.Equal(0, r.SkippedUnflagged);
    }

    [Fact]
    public void Every_switch_off_sends_the_whole_scope()
    {
        var r = QcAiScope.Build(Corpus(), new QcAiScope.Options
        {
            SkipEmpty = false, SkipAutoCarried = false, SkipIdenticalToOriginal = false, SkipApproved = false,
        });
        Assert.Equal(Corpus().Select(x => x.Key), Sent(r));
    }

    [Theory]
    [InlineData("approved", "APPROVED")]
    [InlineData("identical", "SAME")]
    [InlineData("carried", "TAGS")]
    public void Turning_one_switch_off_lets_exactly_its_rows_through(string which, string key)
    {
        var r = QcAiScope.Build(Corpus(), new QcAiScope.Options
        {
            SkipApproved = which != "approved",
            SkipIdenticalToOriginal = which != "identical",
            SkipAutoCarried = which != "carried",
        });
        Assert.Equal(new[] { "OK", key }.OrderBy(k => k), Sent(r).OrderBy(k => k));
    }

    [Fact]
    public void Only_validator_flagged_rows_go_when_asked()
    {
        var broken = Row("BROKEN", "Give %d gold", "Дай золота");
        var rows = new List<TranslationRow> { Row("OK", "Open the door", "Відчини двері"), broken };
        foreach (var x in rows) ValidationService.Instance.RunForRow(x);

        var r = QcAiScope.Build(rows, new QcAiScope.Options { OnlyFlaggedByValidator = true });
        Assert.Equal(new[] { "BROKEN" }, Sent(r));
        Assert.Equal(1, r.SkippedUnflagged);
    }

    [Fact]
    public void With_the_original_in_another_file_unmatched_keys_are_counted_not_sent()
    {
        // Single-language file: the translation sits in Source, the original
        // comes from a second file by key.
        var rows = new List<TranslationRow>
        {
            new() { Namespace = "N", Key = "A", Source = "Відчини двері" },
            new() { Namespace = "N", Key = "B", Source = "Зачини двері" },
        };
        var original = new Dictionary<(string, string), string> { [("N", "A")] = "Open the door" };
        var reference = new Dictionary<(string, string), string> { [("N", "A")] = "Otwórz drzwi" };

        var r = QcAiScope.Build(rows, new QcAiScope.Options(), resultInTarget: false, original, reference);
        var input = Assert.Single(r.Inputs);
        Assert.Equal("Open the door", input.Original);
        Assert.Equal("Відчини двері", input.Result);
        Assert.Equal("Otwórz drzwi", input.Reference);
        Assert.True(input.ResultInSource);
        Assert.Equal(1, r.NoOriginal);
    }

    [Fact]
    public void The_switches_default_to_skipping_and_only_flagged_is_off()
    {
        var o = QcAiScope.Options.FromConfig(new AppConfig());
        Assert.True(o.SkipEmpty && o.SkipAutoCarried && o.SkipIdenticalToOriginal && o.SkipApproved);
        Assert.False(o.OnlyFlaggedByValidator);
    }
}