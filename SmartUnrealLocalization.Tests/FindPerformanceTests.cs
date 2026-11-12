using System.Collections.Specialized;
using System.Diagnostics;
using System.Text.RegularExpressions;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Utils;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// The search froze the app on a real 13 000-row CSV with both columns filled.
/// The cause was never the matching — it was building UI state proportional to
/// the number of hits, one notification at a time. These tests pin the two
/// mechanisms that now bound that work.
/// </summary>
public class FindPerformanceTests
{
    private static readonly Regex Common = new("the", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── the per-cell ceiling ──────────────────────────────────────────────

    [Fact]
    public void Hits_are_capped_but_the_count_stays_truthful()
    {
        // A pathological cell: the query matches 500 times in one string.
        string text = string.Concat(Enumerable.Repeat("the cat sat. ", 500));
        long total = 0;

        var hits = FindHighlighter.Collect(text, Common, cap: 60, ref total);

        Assert.Equal(60, hits.Count);     // what gets drawn
        Assert.Equal(500, total);         // what the counter reports
    }

    [Fact]
    public void A_zero_width_match_produces_no_hits()
    {
        // "a*" matches the empty string at every position. Without the guard
        // this yields one highlight per character and nothing visible to show.
        long total = 0;
        var hits = FindHighlighter.Collect("xxxxx", new Regex("a*"), cap: 60, ref total);

        Assert.Empty(hits);
        Assert.Equal(0, total);
    }

    [Fact]
    public void An_empty_cell_allocates_nothing()
    {
        long total = 0;
        Assert.Same(FindHighlighter.None, FindHighlighter.Collect("", Common, 60, ref total));
        Assert.Same(FindHighlighter.None, FindHighlighter.Collect(null, Common, 60, ref total));
    }

    [Fact]
    public void A_row_with_no_match_allocates_nothing()
    {
        long total = 0;
        Assert.Same(FindHighlighter.None, FindHighlighter.Collect("nothing here", Common, 60, ref total));
        Assert.Equal(0, total);
    }

    [Fact]
    public void Ranges_point_at_the_matched_characters()
    {
        long total = 0;
        var hits = FindHighlighter.Collect("the theme", Common, 60, ref total);

        Assert.Equal(2, hits.Count);
        Assert.Equal(0, hits[0].Start);
        Assert.Equal(3, hits[0].Length);
        Assert.Equal(4, hits[1].Start);
    }

    // ── the scan itself must not be the bottleneck ────────────────────────

    /// <summary>13 476 rows of prose in both columns — the reported file's shape.</summary>
    private static List<TranslationRow> BigFile()
    {
        var rng = new Random(42);
        string[] words = { "the", "quick", "brown", "fox", "jumps", "over", "lazy", "dog", "and", "then" };
        var rows = new List<TranslationRow>(13_476);
        for (int i = 0; i < 13_476; i++)
        {
            string Make() => string.Join(' ', Enumerable.Range(0, rng.Next(4, 25))
                                                        .Select(_ => words[rng.Next(words.Length)]));
            rows.Add(new TranslationRow { Namespace = "NS", Key = "Key." + i, Source = Make(), Target = Make() });
        }
        return rows;
    }

    [Fact]
    public void Scanning_a_13k_row_file_for_a_common_word_is_fast()
    {
        // Deliberately generous: this asserts the ORDER OF MAGNITUDE, not a
        // benchmark. If a future change makes the scan itself quadratic, this
        // catches it; normal machine variance will not.
        var rows = BigFile();
        long total = 0;
        var sw = Stopwatch.StartNew();

        foreach (var row in rows)
        {
            FindHighlighter.Collect(row.Target, Common, 60, ref total);
            FindHighlighter.Collect(row.Source, Common, 60, ref total);
            FindHighlighter.Collect(row.Key, Common, 60, ref total);
        }
        sw.Stop();

        Assert.True(total > 10_000, $"the corpus should match heavily ({total})");
        Assert.True(sw.ElapsedMilliseconds < 2_000,
                    $"scanning 13 476 rows took {sw.ElapsedMilliseconds} ms — the matching itself has regressed");
    }

    // ── the grid rebuild ──────────────────────────────────────────────────

    [Fact]
    public void Replacing_the_whole_list_raises_exactly_one_notification()
    {
        // This is the fix for the other half of the freeze: Clear() + 13 000
        // Add() calls made the DataGrid do 13 001 layout passes for one click.
        var list = new BulkObservableCollection<int>();
        int events = 0;
        list.CollectionChanged += (_, e) =>
        {
            events++;
            Assert.Equal(NotifyCollectionChangedAction.Reset, e.Action);
        };

        list.ReplaceAll(Enumerable.Range(0, 13_476));

        Assert.Equal(1, events);
        Assert.Equal(13_476, list.Count);
    }

    [Fact]
    public void Replacing_reports_the_new_contents_in_order()
    {
        var list = new BulkObservableCollection<string>();
        list.ReplaceAll(new[] { "a", "b", "c" });
        Assert.Equal(new[] { "a", "b", "c" }, list);

        list.ReplaceAll(new[] { "x" });
        Assert.Equal(new[] { "x" }, list);

        list.ReplaceAll(Array.Empty<string>());
        Assert.Empty(list);
    }

    [Fact]
    public void Replacing_notifies_Count_so_bound_readouts_do_not_go_stale()
    {
        var list = new BulkObservableCollection<int>();
        var names = new List<string>();
        ((System.ComponentModel.INotifyPropertyChanged)list).PropertyChanged += (_, e) => names.Add(e.PropertyName ?? "");

        list.ReplaceAll(new[] { 1, 2, 3 });

        Assert.Contains("Count", names);
        Assert.Contains("Item[]", names);
    }

    [Fact]
    public void A_row_starts_and_ends_on_the_shared_empty_hit_list()
    {
        // Assigning the SAME reference is a no-op for the binding, so closing the
        // find bar on a 13 000-row file only notifies rows that had a highlight.
        var row = new TranslationRow { Key = "K", Source = "s", Target = "t" };
        Assert.Empty(row.FindHits);
        Assert.Empty(row.SourceFindHits);
    }

    // ── offsets that outlived the text they indexed ───────────────────────
    //
    // Replace splices Target at coordinates the last scan recorded. Anything
    // that rewrites a cell afterwards — an AI batch, an undo, a paste, an MCP
    // write — can leave those coordinates in bounds while pointing at other
    // characters. The recompute those edits schedule is debounced by 250 ms, so
    // a fast Replace click can beat it; this check is what makes that window
    // safe, and its absence is why the bug survived from May.

    [Fact]
    public void A_hit_is_confirmed_where_the_scan_left_it()
    {
        Assert.True(FindHighlighter.HitStillPresent("the cat sat", Common, start: 0, length: 3, cap: 60));
    }

    [Fact]
    public void An_offset_that_now_points_at_other_text_is_refused()
    {
        // Scanned "the cat" and recorded a hit at 0..3. The row was then edited
        // to "xxx cat" — same length, so every bounds check still passes, and
        // splicing at 0..3 would eat characters the user never searched for.
        Assert.False(FindHighlighter.HitStillPresent("xxx cat", Common, start: 0, length: 3, cap: 60));
    }

    [Fact]
    public void An_offset_that_ran_off_the_end_is_refused()
    {
        Assert.False(FindHighlighter.HitStillPresent("the", Common, start: 8, length: 3, cap: 60));
        Assert.False(FindHighlighter.HitStillPresent("the", Common, start: 1, length: 9, cap: 60));
    }

    [Fact]
    public void A_hit_that_moved_within_the_same_row_is_refused_at_its_old_offset()
    {
        // "the cat" -> "a the cat": the word is still there, three characters to
        // the right. Replacing at the old offset would cut "a t".
        Assert.False(FindHighlighter.HitStillPresent("a the cat", Common, start: 0, length: 3, cap: 60));
        Assert.True(FindHighlighter.HitStillPresent("a the cat", Common, start: 2, length: 3, cap: 60));
    }

    [Fact]
    public void An_empty_or_zero_length_target_is_refused_rather_than_spliced()
    {
        Assert.False(FindHighlighter.HitStillPresent("", Common, start: 0, length: 3, cap: 60));
        Assert.False(FindHighlighter.HitStillPresent(null, Common, start: 0, length: 3, cap: 60));
        Assert.False(FindHighlighter.HitStillPresent("the", Common, start: 0, length: 0, cap: 60));
        Assert.False(FindHighlighter.HitStillPresent("the", Common, start: -1, length: 3, cap: 60));
    }

    [Fact]
    public void A_hit_past_the_per_row_ceiling_is_refused_like_every_other_unknown()
    {
        // The scan itself stops recording past the cap, so a match beyond it was
        // never navigable; refusing it keeps the two sides consistent.
        string text = string.Concat(Enumerable.Repeat("the ", 200));
        Assert.True(FindHighlighter.HitStillPresent(text, Common, start: 0, length: 3, cap: 60));
        Assert.False(FindHighlighter.HitStillPresent(text, Common, start: 4 * 100, length: 3, cap: 60));
    }
}
