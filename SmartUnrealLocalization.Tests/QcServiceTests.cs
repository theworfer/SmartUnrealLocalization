using Newtonsoft.Json;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services.Qc;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// The proposal store and its sidecar: which results are accepted, what survives
/// a re-run, and what a restart must still remember. QcService is a singleton,
/// so these run in one collection to keep them off each other's file.
/// </summary>
[Collection("AppState")]
public class QcServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _fileA;
    private readonly string _fileB;
    private readonly QcService _svc = QcService.Instance;

    public QcServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sul-qc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _fileA = Path.Combine(_dir, "a.locres");
        _fileB = Path.Combine(_dir, "b.locres");
        _svc.LoadForFile(_fileA);
    }

    public void Dispose()
    {
        _svc.LoadForFile("");                       // detach before deleting
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static QcProposal P(string key, string after = "Y", string kind = "PunctuationMismatch",
                                string source = "model", string before = "X") => new()
    {
        Namespace = "NS", Key = key, Before = before, After = after, Reason = "test",
        Kinds = new List<string> { kind }, Severity = QcSeverity.Low, Source = source,
    };

    // ── file identity ────────────────────────────────────────────────────

    [Fact]
    public void Results_produced_for_another_file_are_refused()
    {
        // A background run whose tab was switched must never write into the file
        // that is open now.
        // It must also SAY so: the caller's follow-up effects (auto-fixes, the
        // bucket, the tab switch) would otherwise land on that file (audit §7).
        Assert.False(_svc.SetRunResults(new[] { P("K1") }, 1, null, _fileB));
        Assert.Equal(0, _svc.PendingCount);

        Assert.True(_svc.SetRunResults(new[] { P("K1") }, 1, null, _fileA));
        Assert.Equal(1, _svc.PendingCount);
    }

    [Fact]
    public void Merges_from_another_file_are_refused()
    {
        _svc.MergeProposals(new[] { P("KX", source: "model") }, _fileB);
        Assert.DoesNotContain(_svc.Pending, p => p.Key == "KX");
    }

    // ── run scope ────────────────────────────────────────────────────────

    [Fact]
    public void A_selected_rows_run_keeps_findings_outside_its_scope()
    {
        _svc.SetRunResults(new[] { P("K1"), P("K2") }, 2, null, _fileA);
        _svc.SetRunResults(new[] { P("K2", after: "Z") }, 1, new[] { ("NS", "K2") }, _fileA);

        Assert.Contains(_svc.Pending, p => p.Key == "K1");                 // untouched
        Assert.Equal("Z", _svc.Pending.Single(p => p.Key == "K2").After);  // replaced
        Assert.Equal(2, _svc.PendingCount);
    }

    [Fact]
    public void A_full_run_replaces_everything_it_re_derives()
    {
        _svc.SetRunResults(new[] { P("K1"), P("K2") }, 2, null, _fileA);
        _svc.SetRunResults(new[] { P("K3") }, 1, null, _fileA);
        Assert.Equal(new[] { "K3" }, _svc.Pending.Select(p => p.Key).ToArray());
    }

    // ── AI results are paid for and must not be lost ─────────────────────

    [Fact]
    public void Ai_proposals_survive_a_later_local_run_while_the_row_is_unchanged()
    {
        var row = new TranslationRow { Namespace = "NS", Key = "A1", Source = "s", Target = "X" };
        _svc.MergeProposals(new[] { P("A1", after: "краще", kind: "AiQc", source: "model") }, _fileA);

        _svc.SetRunResults(new[] { P("A1", after: "X2") }, 1, null, _fileA,
                           QcService.IndexRows(new[] { row }));

        var kept = _svc.Pending.Single(p => p.Key == "A1");
        Assert.Equal("model", kept.Source);                    // the paid result won
        Assert.Contains("PunctuationMismatch", kept.Kinds);    // local label absorbed
    }

    [Fact]
    public void A_stale_ai_proposal_is_dropped_when_the_row_was_edited()
    {
        var row = new TranslationRow { Namespace = "NS", Key = "A2", Source = "s", Target = "edited" };
        _svc.MergeProposals(new[] { P("A2", after: "краще", kind: "AiQc", source: "model") }, _fileA);

        _svc.SetRunResults(Array.Empty<QcProposal>(), 1, null, _fileA, QcService.IndexRows(new[] { row }));
        Assert.DoesNotContain(_svc.Pending, p => p.Key == "A2");
    }

    [Fact]
    public void An_ai_verdict_supersedes_the_deterministic_one_but_keeps_its_labels()
    {
        _svc.SetRunResults(new[] { P("K3", kind: "LanguageLeak") }, 1, null, _fileA);
        _svc.MergeProposals(new[] { P("K3", after: "W", kind: "AiQc", source: "model") }, _fileA);

        var p = _svc.Pending.Single(x => x.Key == "K3");
        Assert.Equal("model", p.Source);
        Assert.Equal("W", p.After);
        Assert.Contains("AiQc", p.Kinds);
        Assert.Contains("LanguageLeak", p.Kinds);
    }

    // ── the user's decisions stick ───────────────────────────────────────

    [Fact]
    public void A_dismissed_row_is_not_re_proposed_by_the_ai_layer()
    {
        _svc.SetRunResults(new[] { P("K4") }, 1, null, _fileA);
        _svc.SetVerdict(_svc.Pending.Single(p => p.Key == "K4"), QcVerdict.Kept);

        _svc.MergeProposals(new[] { P("K4", after: "W2", kind: "AiQc", source: "model") }, _fileA);
        Assert.DoesNotContain(_svc.Pending, p => p.Key == "K4");
    }

    [Fact]
    public void An_identical_suggestion_on_an_unchanged_row_respects_the_earlier_keep()
    {
        _svc.SetRunResults(new[] { P("K5") }, 1, null, _fileA);
        _svc.SetVerdict(_svc.Pending.Single(p => p.Key == "K5"), QcVerdict.Kept);

        _svc.SetRunResults(new[] { P("K5") }, 1, null, _fileA);   // same run again
        Assert.DoesNotContain(_svc.Pending, p => p.Key == "K5");
    }

    [Fact]
    public void A_keep_survives_ANY_number_of_re_runs_not_just_the_first()
    {
        // The Kept record must be carried into each run's fresh proposal set, or
        // the second re-run finds no memory of the dismissal and resurrects the
        // suggestion (caught by the mutation audit: dropping the carry passed the
        // single re-run test above).
        _svc.SetRunResults(new[] { P("K8") }, 1, null, _fileA);
        _svc.SetVerdict(_svc.Pending.Single(p => p.Key == "K8"), QcVerdict.Kept);

        _svc.SetRunResults(new[] { P("K8") }, 1, null, _fileA);
        _svc.SetRunResults(new[] { P("K8") }, 1, null, _fileA);
        _svc.SetRunResults(new[] { P("K8") }, 1, null, _fileA);

        Assert.DoesNotContain(_svc.Pending, p => p.Key == "K8");
    }

    // ── residual findings after a partial fix ────────────────────────────

    [Fact]
    public void Applying_a_partial_fix_leaves_the_row_flagged_with_what_is_left()
    {
        var p = P("K6", after: "fixed text");
        p.UnresolvedKinds = new List<string> { "TagMismatch" };
        p.UnresolvedReason = "<tag> count differs from the source";
        p.UnresolvedSeverity = QcSeverity.Critical;
        _svc.SetRunResults(new[] { p }, 1, null, _fileA);

        Assert.True(_svc.DemoteToResidual(p, "fixed text"));
        Assert.Equal(QcVerdict.Pending, p.Verdict);          // NOT closed
        Assert.Equal("fixed text", p.Before);                // measured against the new text
        Assert.Equal(p.Before, p.After);                     // flag only now
        Assert.Equal(QcSeverity.Critical, p.Severity);
        Assert.Equal(new[] { "TagMismatch" }, p.Kinds.ToArray());
    }

    [Fact]
    public void A_complete_fix_closes_the_row()
        => Assert.False(_svc.DemoteToResidual(P("K7"), "whatever"));

    // ── row binding ──────────────────────────────────────────────────────

    [Fact]
    public void Duplicate_rows_never_share_one_proposal()
    {
        // Duplicate (namespace, key) pairs are legal in .locres. Sharing the
        // object left a ghost badge on the twin after fixing one of them.
        _svc.SetRunResults(new[] { P("DUP") }, 1, null, _fileA);
        var rows = new List<TranslationRow>
        {
            new() { Namespace = "NS", Key = "DUP", Source = "s", Target = "X" },
            new() { Namespace = "NS", Key = "DUP", Source = "s", Target = "X" },
        };
        _svc.Hydrate(rows);

        Assert.Single(rows, r => r.QcProposal != null);
    }

    [Fact]
    public void IndexRows_survives_duplicate_keys_with_first_occurrence_winning()
    {
        var rows = new List<TranslationRow>
        {
            new() { Namespace = "UI", Key = "SAME", Target = "first" },
            new() { Namespace = "UI", Key = "SAME", Target = "second" },
            new() { Namespace = "UI", Key = "OTHER", Target = "x" },
        };
        var index = QcService.IndexRows(rows);
        Assert.Equal(2, index.Count);
        Assert.Equal("first", index[("UI", "SAME")].Target);
    }

    // ── durability ───────────────────────────────────────────────────────

    [Fact]
    public void Verdicts_are_stored_by_name_so_reordering_an_enum_cannot_corrupt_them()
    {
        _svc.SetRunResults(new[] { P("D1"), P("D2") }, 2, null, _fileA);
        _svc.SetVerdict(_svc.Pending.Single(p => p.Key == "D2"), QcVerdict.Kept);

        string raw = File.ReadAllText(_fileA + ".qc.json");
        Assert.Contains("\"Kept\"", raw);
        Assert.Contains("\"Pending\"", raw);
        Assert.Contains("\"Low\"", raw);
    }

    [Fact]
    public void A_legacy_sidecar_with_numeric_enums_still_loads()
    {
        var legacy = new
        {
            version = 1,
            proposals = new[]
            {
                new { ns = "NS", key = "L1", before = "X", after = "Y", reason = "r",
                      kinds = new[] { "AiQc" }, severity = 3, verdict = 2, source = "gpt" },
                new { ns = "NS", key = "L2", before = "X", after = "Y", reason = "r",
                      kinds = new[] { "AiQc" }, severity = 1, verdict = 0, source = "gpt" },
            },
        };
        _svc.LoadForFile("");
        File.WriteAllText(_fileB + ".qc.json", JsonConvert.SerializeObject(legacy));
        _svc.LoadForFile(_fileB);

        Assert.Equal(1, _svc.PendingCount);                       // L1 was Kept (2)
        Assert.Equal("L2", _svc.Pending[0].Key);
        Assert.Equal(QcSeverity.Medium, _svc.Pending[0].Severity); // 1 == Medium
    }

    [Fact]
    public void Pending_suggestions_from_the_removed_local_pass_are_dropped_on_load()
    {
        // QC's local pass is gone and the live validator reports those findings;
        // its old pending suggestions would show every problem twice. Decided
        // ones are history and stay.
        var legacy = new
        {
            version = 1,
            proposals = new[]
            {
                new { ns = "NS", key = "D1", before = "X", after = "Y", reason = "r",
                      kinds = new[] { "PunctuationMismatch" }, severity = 1, verdict = 0, source = "deterministic" },
                new { ns = "NS", key = "D2", before = "X", after = "Y", reason = "r",
                      kinds = new[] { "PunctuationMismatch" }, severity = 1, verdict = 2, source = "deterministic" },
                new { ns = "NS", key = "A1", before = "X", after = "Y", reason = "r",
                      kinds = new[] { "AiQc" }, severity = 1, verdict = 0, source = "gpt" },
            },
        };
        _svc.LoadForFile("");
        File.WriteAllText(_fileB + ".qc.json", JsonConvert.SerializeObject(legacy));
        _svc.LoadForFile(_fileB);

        Assert.Equal("A1", Assert.Single(_svc.Pending).Key);
    }
    [Fact]
    public void A_review_survives_switching_away_and_back()
    {
        _svc.SetRunResults(new[] { P("R1"), P("R2") }, 2, null, _fileA);
        _svc.LoadForFile(_fileB);
        _svc.LoadForFile(_fileA);

        Assert.Equal(2, _svc.PendingCount);
        Assert.Contains(_svc.Pending, p => p.Key == "R1");
    }

    [Fact]
    public void A_failed_sidecar_write_is_reported_instead_of_swallowed()
    {
        // Silence here would let the user keep reviewing and lose everything on
        // exit — a read-only folder or a sync client holding the file.
        string? reported = null;
        QcService.SaveFailed = m => reported = m;
        try
        {
            _svc.LoadForFile("");
            string locked = Path.Combine(_dir, "locked.locres");
            using (File.Open(locked + ".qc.json", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                _svc.LoadForFile(locked);
                _svc.SetRunResults(new[] { P("Z1") }, 1, null, locked);
            }
            Assert.NotNull(reported);
        }
        finally { QcService.SaveFailed = null; }
    }

    [Fact]
    public void Concurrent_merges_neither_throw_nor_lose_entries()
    {
        Parallel.For(0, 100, i =>
        {
            _svc.MergeProposals(Enumerable.Range(0, 5)
                .Select(j => P($"C{i}_{j}", kind: "AiQc", source: "model")).ToList(), _fileA);
            _ = _svc.Pending;
            _ = _svc.PendingCount;
        });
        Assert.Equal(500, _svc.PendingCount);
    }
}
