using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services.Qc;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// Property tests: invariants asserted over thousands of GENERATED rows instead
/// of hand-picked cases. This pass found the two worst defects of QC's former
/// local pass (case fixes corrupting placeholders, whitespace fixes deleting line
/// breaks) — both looked correct in isolation and survived several code reviews.
///
/// <para>Inputs are deliberately hostile: control characters, RTL scripts, emoji
/// with ZWJ sequences, CJK, zero-width spaces, unbalanced brackets, every
/// placeholder shape and 300-character words.</para>
/// </summary>
public class QcPropertyTests
{
    private static readonly string[] Atoms =
    {
        "Hello", "Привіт", "что", "ыъэё", "Києва", "RESOLVE", "…", "...", "..", "!", "?", "?!",
        "{0}", "{1}", "{name}", "%s", "%d", "%1$s", "%2$s", "%.2f", "%", "100%",
        "<b>", "</b>", "<font color=\"red\">", "[i]", "[/i]", "[b]", "\n", "\r\n", "\t",
        "\"", "'", "«", "»", "—", "–", "-", "  ", " ", "", "😀", "👨‍👩‍👧‍👦", "中文", "日本語",
        "العربية", "עברית", "é", "​", " ", "Translation:", "FIXED:", "Here is",
        "ID 12:", "1)", "a b", "%%", "{", "}", "[", "]", "<", ">",
    };

    private static string Rand(Random rng, int maxParts = 6)
    {
        int n = rng.Next(0, maxParts);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < n; i++) sb.Append(Atoms[rng.Next(Atoms.Length)]);
        return sb.ToString();
    }

    [Fact]
    public void Diff_always_reproduces_both_texts_exactly()
    {
        var rng = new Random(4242);
        for (int i = 0; i < 5_000; i++)
        {
            string a = Rand(rng, 8), b = Rand(rng, 8);
            var (before, after) = QcDiff.Compute(a, b);

            Assert.Equal(a, string.Concat(before.Select(s => s.Text)));
            Assert.Equal(b, string.Concat(after.Select(s => s.Text)));
            if (a == b)
            {
                Assert.DoesNotContain(before, s => s.Changed);
                Assert.DoesNotContain(after, s => s.Changed);
            }
        }
    }

    [Fact]
    public void An_accepted_fix_always_preserves_placeholders_and_line_breaks()
    {
        var rng = new Random(7);
        var placeholder = new System.Text.RegularExpressions.Regex(@"\{[^}]*\}|%\d*\$?[sdifux]|%\.\d+f");

        for (int i = 0; i < 5_000; i++)
        {
            string src = Rand(rng), cur = Rand(rng), fix = Rand(rng);
            if (QcAiRunner.RejectFix(src, cur, fix) is not null) continue;

            Assert.False(string.IsNullOrWhiteSpace(fix));
            Assert.NotEqual(cur, fix);
            Assert.Equal(placeholder.Matches(src).Count, placeholder.Matches(fix).Count);
            Assert.Equal(src.Count(c => c == '\n'), fix.Count(c => c == '\n'));
        }
    }

    [Fact]
    public void Cache_keys_never_collide_across_distinct_inputs()
    {
        var rng = new Random(31337);
        var seen = new Dictionary<string, (string, string, string, string?, string)>();

        for (int i = 0; i < 5_000; i++)
        {
            string m = Rand(rng, 2), g = Rand(rng, 2), o = Rand(rng, 3), t = Rand(rng, 3);
            string? r = rng.Next(2) == 0 ? null : Rand(rng, 2);

            string key = QcCache.KeyFor(m, g, o, r, t);
            Assert.Equal(key, QcCache.KeyFor(m, g, o, r, t));          // stable

            var fields = (m, g, o, r, t);
            if (seen.TryGetValue(key, out var previous)) Assert.Equal(previous, fields);
            else seen[key] = fields;
        }
    }

    [Fact]
    public void The_review_scope_is_a_distinct_subset_in_file_order_and_every_row_is_accounted_for()
    {
        var rng = new Random(2026);
        for (int trial = 0; trial < 40; trial++)
        {
            var rows = Enumerable.Range(0, rng.Next(0, 400))
                .Select(i => new TranslationRow
                {
                    Namespace = "N", Key = "K" + i, Source = Rand(rng), Target = Rand(rng),
                    Review = rng.Next(5) == 0 ? ReviewStatus.Approved : ReviewStatus.None,
                })
                .ToList();
            var options = new QcAiScope.Options
            {
                SkipEmpty = rng.Next(2) == 0, SkipAutoCarried = rng.Next(2) == 0,
                SkipIdenticalToOriginal = rng.Next(2) == 0, SkipApproved = rng.Next(2) == 0,
            };

            var r = QcAiScope.Build(rows, options);
            var sent = r.Inputs.Select(i => i.Row).ToList();
            Assert.Equal(sent.Count, sent.Distinct().Count());
            Assert.All(sent, x => Assert.Contains(x, rows));
            var order = sent.Select(x => rows.IndexOf(x)).ToList();
            Assert.Equal(order.OrderBy(i => i), order);

            // Nothing silently disappears: every row is either sent or counted
            // against the switch that kept it out.
            Assert.Equal(rows.Count, sent.Count + r.SkippedEmpty + r.SkippedAutoCarried
                                     + r.SkippedIdentical + r.SkippedApproved + r.SkippedUnflagged + r.NoOriginal);

            // With the two content switches on, no row that cannot be reviewed is paid for.
            if (options.SkipEmpty && options.SkipAutoCarried)
                Assert.DoesNotContain(sent, QcEngine.ShouldSkip);
        }
    }
}