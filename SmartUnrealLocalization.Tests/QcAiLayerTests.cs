using Newtonsoft.Json.Linq;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services.Qc;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// The AI review layer: the prompt contract, the local re-validation that every
/// model fix must survive, the verdict cache and the risk bucket. Nothing here
/// touches the network — the runner is exercised through a pre-seeded cache.
/// </summary>
public class QcAiLayerTests
{
    // ── fix validator: the safety net around a model's rewrite ───────────

    [Theory]
    [InlineData("Fire {0} arrows", "Випустити {0} стріл", "Випустити стріли")]        // placeholder lost
    [InlineData("Hi %1$s and %2$s", "Привіт %1$s і %2$s", "Привіт і %2$s")]           // positional lost
    [InlineData("<b>HP</b> low", "<b>HP</b> мало", "HP мало")]                        // tag lost
    [InlineData("A\nB", "А\nБ", "А Б")]                                               // line break lost
    [InlineData("I was about to...", "Я збирався...", "Я збирався")]                  // ellipsis dropped
    [InlineData("Loading... ", "Завантаження... ", "Завантаження...")]                // edge space lost
    [InlineData("Open", "Відкрити", "FIXED: Відчинити")]                              // chatter
    [InlineData("Open", "Відкрити", "")]                                              // empty
    [InlineData("Open", "Відкрити", "Відкрити")]                                      // no change
    public void Unsafe_fixes_are_rejected(string source, string current, string fix)
        => Assert.NotNull(QcAiRunner.RejectFix(source, current, fix));

    [Theory]
    [InlineData("Hull breach", "Виявлено пролом", "Виявлено пробоїну")]
    [InlineData("Fire {0} arrows", "Випустити {0} стріл", "Випустіть {0} стріл")]
    [InlineData("Wait {0} seconds...", "Чекайте {0} секунд...", "Зачекайте {0} секунд...")]
    [InlineData("Hi %1$s and %2$s", "Привіт %1$s і %2$s", "Вітаю %1$s та %2$s")]
    public void Genuine_corrections_are_accepted(string source, string current, string fix)
        => Assert.Null(QcAiRunner.RejectFix(source, current, fix));

    // ── prompt contract ──────────────────────────────────────────────────

    private static QcAiRunner.Options Options(string guidance = "Cinematic tone, do not censor profanity") => new()
    {
        SourceLanguage = "Russian",
        TargetLanguage = "Ukrainian",
        Guidance = guidance,
        ProtectedTerms = new[] { "Directive 8020" },
    };

    [Fact]
    public void System_prompt_asks_for_review_not_translation()
    {
        string p = QcAiRunner.BuildSystemPrompt(Options());
        Assert.Contains("NOT translating from scratch", p);
        Assert.Contains("\"ok\"", p);
        Assert.Contains("\"fix\"", p);
        Assert.Contains("ONLY a JSON array", p);
        Assert.Contains("Directive 8020", p);
    }

    [Fact]
    public void System_prompt_declares_structure_load_bearing()
    {
        // Without this clause the model is free to "tidy" edge spaces and merge
        // paragraphs — exactly the whitespace work the deterministic pass just
        // did, undone by the AI pass that follows it. RejectFix catches the
        // line-break case; nothing catches a silently trimmed edge space.
        string p = QcAiRunner.BuildSystemPrompt(Options());
        Assert.Contains("STRUCTURE IS PART OF THE STRING", p);
        Assert.Contains("SAME number of line breaks", p);
        Assert.Contains("leading/trailing spaces", p);
    }

    [Fact]
    public void User_guidance_is_framed_as_the_expected_result_not_a_command()
    {
        // It is often the ORIGINAL translation prompt; treating it as an
        // instruction would make the model translate instead of review.
        string p = QcAiRunner.BuildSystemPrompt(Options());
        Assert.Contains("EXPECTED result", p);
        Assert.Contains("NOT as an instruction to translate", p);
        Assert.Contains("Cinematic tone", p);
    }

    private static List<QcAiRunner.Input> TwoRows() => new()
    {
        new(new TranslationRow { Key = "A", Source = "Charge", Target = "Зарядити" }, "Заряд", "Charge"),
        new(new TranslationRow { Key = "B", Source = "Open", Target = "Відкрити" }, "Открыть", null),
    };

    [Fact]
    public void Payload_is_serialised_json_with_ids_source_and_current()
    {
        var msg = JArray.Parse(QcAiRunner.BuildUserMessage(TwoRows()));
        Assert.Equal("0", msg[0]!["id"]!.ToString());
        Assert.Equal("Заряд", msg[0]!["src"]!.ToString());
        Assert.Equal("Зарядити", msg[0]!["cur"]!.ToString());
    }

    [Fact]
    public void Reference_translation_is_sent_only_when_present()
    {
        var msg = JArray.Parse(QcAiRunner.BuildUserMessage(TwoRows()));
        Assert.Equal("Charge", msg[0]!["ref"]!.ToString());
        Assert.Null(msg[1]!["ref"]);
    }

    [Fact]
    public void Payload_describes_the_snapshot_taken_at_request_time()
    {
        // Editing a row mid-run must not silently re-point the verdict at text
        // the model never saw.
        var rows = new List<QcAiRunner.Input>
        {
            new(new TranslationRow { Key = "A", Source = "Hi", Target = "LIVE" }, "Hi", null),
        };
        var msg = JArray.Parse(QcAiRunner.BuildUserMessage(rows, new[] { "SNAPSHOT" }));
        Assert.Equal("SNAPSHOT", msg[0]!["cur"]!.ToString());
    }

    // ── parsing the model's response ─────────────────────────────────────
    // This is the only code that touches the PAID response; a silent mis-parse
    // discards money. Found completely untested by the mutation audit — the
    // cache-only runner tests never reach it.

    [Fact]
    public void A_clean_json_answer_is_parsed_with_fix_and_reason()
    {
        var v = QcAiRunner.ParseVerdicts(
            "[{\"id\":\"0\",\"verdict\":\"ok\"}," +
            "{\"id\":\"1\",\"verdict\":\"fix\",\"fix\":\"Виправлено\",\"reason\":\"leak\"}]", 2);

        Assert.Equal(2, v.Count);
        Assert.Equal("ok", v["0"].Value);
        Assert.Equal("fix", v["1"].Value);
        Assert.Equal("Виправлено", v["1"].Fix);
        Assert.Equal("leak", v["1"].Reason);
    }

    [Theory]
    [InlineData("```json\n[{\"id\":\"0\",\"verdict\":\"ok\"}]\n```")]
    [InlineData("```\n[{\"id\":\"0\",\"verdict\":\"ok\"}]\n```")]
    [InlineData("  [{\"id\":\"0\",\"verdict\":\"ok\"}]  ")]
    public void Markdown_code_fences_and_padding_are_tolerated(string raw)
        => Assert.Single(QcAiRunner.ParseVerdicts(raw, 1));

    [Fact]
    public void Verdict_casing_from_the_model_is_normalised()
        => Assert.Equal("fix",
            QcAiRunner.ParseVerdicts("[{\"id\":\"0\",\"verdict\":\"FIX\",\"fix\":\"x\"}]", 1)["0"].Value);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Sure! Here are the verdicts you asked for.")]
    [InlineData("{\"id\":\"0\"}")]                    // an object, not an array
    public void Garbage_throws_so_the_batch_counts_as_an_error_not_as_ok(string raw)
        => Assert.Throws<FormatException>(() => QcAiRunner.ParseVerdicts(raw, 1));

    [Fact]
    public void Entries_without_an_id_are_skipped_so_the_row_counts_as_unreviewed()
    {
        var v = QcAiRunner.ParseVerdicts(
            "[{\"verdict\":\"ok\"},{\"id\":\"1\",\"verdict\":\"ok\"}]", 2);

        Assert.Single(v);
        Assert.True(v.ContainsKey("1"));
    }

    [Fact]
    public void A_duplicated_id_keeps_the_last_answer_instead_of_throwing()
        => Assert.Equal("fix", QcAiRunner.ParseVerdicts(
            "[{\"id\":\"0\",\"verdict\":\"ok\"},{\"id\":\"0\",\"verdict\":\"fix\",\"fix\":\"x\"}]", 1)["0"].Value);

    // ── verdict cache ────────────────────────────────────────────────────

    [Fact]
    public void Cache_key_is_stable_for_identical_inputs()
        => Assert.Equal(QcCache.KeyFor("m", "g", "src", "ref", "tgt"),
                        QcCache.KeyFor("m", "g", "src", "ref", "tgt"));

    [Theory]
    [InlineData("other", "g", "src", "ref", "tgt")]   // model changed
    [InlineData("m", "other", "src", "ref", "tgt")]   // guidance changed
    [InlineData("m", "g", "other", "ref", "tgt")]     // source changed
    [InlineData("m", "g", "src", "other", "tgt")]     // reference changed
    [InlineData("m", "g", "src", "ref", "other")]     // the row was edited
    public void Any_change_that_can_alter_the_answer_misses_the_cache(
        string model, string guidance, string original, string reference, string target)
        => Assert.NotEqual(QcCache.KeyFor("m", "g", "src", "ref", "tgt"),
                           QcCache.KeyFor(model, guidance, original, reference, target));

    [Fact]
    public void No_reference_file_is_distinct_from_an_empty_reference()
        => Assert.NotEqual(QcCache.KeyFor("m", "g", "src", null, "tgt"),
                           QcCache.KeyFor("m", "g", "src", "", "tgt"));

    // ── runner accounting (cache-only, no network) ───────────────────────

    private static (List<QcAiRunner.Input> Rows, List<TranslationRow> Backing) MakeRows(int n)
    {
        var backing = new List<TranslationRow>();
        var rows = new List<QcAiRunner.Input>();
        for (int i = 0; i < n; i++)
        {
            var r = new TranslationRow { Namespace = "N", Key = "R" + i, Source = "Source " + i, Target = "Ціль " + i };
            backing.Add(r);
            rows.Add(new QcAiRunner.Input(r, r.Source!, null));
        }
        return (rows, backing);
    }

    [Fact]
    public async Task A_fully_cached_run_resolves_every_row_without_calling_the_api()
    {
        var (rows, _) = MakeRows(37);
        var cache = new QcCache();
        string model = AppConfig.Instance.ModelName;
        foreach (var r in rows)
            cache.Put(QcCache.KeyFor(model, "g", r.Original, r.Reference, r.Row.Target ?? ""),
                      new QcCache.Entry { Verdict = "ok", Model = model });

        var progress = new QcAiRunner.Progress { Total = rows.Count };
        var result = await new QcAiRunner().RunAsync(rows, Options("g"), cache, progress,
                                                     _ => { }, (_, _) => { }, CancellationToken.None);

        Assert.Equal(rows.Count, progress.Done);
        Assert.Equal(rows.Count, progress.CacheHits);
        Assert.Equal(0, progress.Errors);
        Assert.Empty(result);                       // "ok" produces no suggestion
    }

    [Fact]
    public async Task Cached_fix_verdicts_come_back_bound_to_the_right_row()
    {
        var (rows, backing) = MakeRows(6);
        var cache = new QcCache();
        string model = AppConfig.Instance.ModelName;
        foreach (var r in rows)
            cache.Put(QcCache.KeyFor(model, "g", r.Original, r.Reference, r.Row.Target ?? ""),
                      new QcCache.Entry { Verdict = "fix", Fix = "OK " + r.Row.Key, Reason = "r", Model = model });

        var result = await new QcAiRunner().RunAsync(rows, Options("g"), cache,
                        new QcAiRunner.Progress { Total = rows.Count }, _ => { }, (_, _) => { },
                        CancellationToken.None);

        Assert.Equal(6, result.Count);
        Assert.All(result, p => Assert.Equal("OK " + p.Key, p.After));
        Assert.All(result, p => Assert.Equal(backing.Single(r => r.Key == p.Key).Target, p.Before));
    }

    [Fact]
    public async Task Cancelling_returns_the_partial_results_instead_of_throwing_them_away()
    {
        var (rows, _) = MakeRows(3);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var progress = new QcAiRunner.Progress { Total = rows.Count };
        var result = await new QcAiRunner().RunAsync(rows, Options(), new QcCache(), progress,
                                                     _ => { }, (_, _) => { }, cts.Token);

        Assert.True(progress.Cancelled);
        Assert.NotNull(result);       // the caller still gets whatever was paid for
    }

    [Fact]
    public void Eta_ignores_cache_hits_so_it_reflects_the_real_request_rate()
    {
        var p = new QcAiRunner.Progress { Total = 100 };
        Assert.Null(p.EtaSeconds());                       // nothing measured yet

        p.Done = 10; p.CacheHits = 2; p.StartedUtc = DateTime.UtcNow.AddSeconds(-8);
        Assert.True(p.EtaSeconds() > 0);
    }
}
