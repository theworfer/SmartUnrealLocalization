using Locres.IO;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// "Merge to new version", end to end: two files in, saved bytes out — through
/// the exact path the app takes (MergeMatcher → grid rows → BuildOverrides →
/// SaveWith).
///
/// <para>`MergeMatcherTests` covers which translations may be carried.
/// This covers what actually reaches the file, because the grid holds the new
/// original in <b>Source</b> and the carried translation in <b>Target</b>, and
/// the saved result is the new version's skeleton with those Targets written
/// over it. If that path broke, a merge would write the new version's English
/// over every translation and the first sign would be a shipped file.</para>
/// </summary>
public class MergeRoundTripTests
{
    private static byte[] Legacy(params (string Ns, (string Key, uint Hash, string Value)[] Entries)[] namespaces)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, System.Text.Encoding.Unicode, leaveOpen: true);
        w.Write(namespaces.Length);
        foreach (var (ns, entries) in namespaces)
        {
            Str(w, ns);
            w.Write(entries.Length);
            foreach (var (key, hash, value) in entries) { Str(w, key); w.Write(hash); Str(w, value); }
        }
        return ms.ToArray();

        static void Str(BinaryWriter w, string s)
        {
            // Legacy locres strings: negative length = UTF-16, positive = ASCII.
            if (s.Length == 0) { w.Write(0); return; }
            w.Write(-(s.Length + 1));
            foreach (char c in s) w.Write((ushort)c);
            w.Write((ushort)0);
        }
    }

    private static LocresDocument Doc(params (string Ns, (string Key, uint Hash, string Value)[] Entries)[] ns)
        => LocresDocument.Parse(Legacy(ns));

    /// <summary>Exactly what MergeToNewVersionAsync builds from a match result.</summary>
    private static List<TranslationRow> RowsFrom(LocresDocument newVersion, MergeMatchResult match)
    {
        var rows = new List<TranslationRow>();
        foreach (var ns in newVersion.Namespaces)
            foreach (var e in ns.Entries)
                rows.Add(new TranslationRow
                {
                    Namespace = ns.Name,
                    Key = e.Key,
                    Source = e.Value,                                             // new version's original
                    Target = match.Carried.GetValueOrDefault((ns.Name, e.Key), ""), // carried translation
                    IsNewKey = match.NewKeySet.Contains((ns.Name, e.Key)),
                    IsSourceChanged = match.Rejected.Contains((ns.Name, e.Key)),
                });
        return rows;
    }

    private static LocresDocument SaveAndReload(LocresDocument doc, IReadOnlyList<TranslationRow> rows)
    {
        using var ms = new MemoryStream();
        doc.SaveWith(LocresFileService.BuildOverrides(rows), ms);
        return LocresDocument.Parse(ms.ToArray());
    }

    private static string Value(LocresDocument d, string ns, string key)
        => d.Namespaces.Single(n => n.Name == ns).Entries.Single(e => e.Key == key).Value;

    /// <summary>The whole app pipeline for an old .locres, in one call.</summary>
    private static (MergeMatchResult Match, LocresDocument Saved) Run(
        LocresDocument oldDoc, LocresDocument newDoc, MergeVerification mode)
    {
        var match = MergeMatcher.Match(newDoc, MergeMatcher.FromDocument(oldDoc), mode);
        return (match, SaveAndReload(newDoc, RowsFrom(newDoc, match)));
    }

    // ── what reaches the file ─────────────────────────────────────────────

    [Fact]
    public void A_verified_match_puts_the_translation_back_into_the_file()
    {
        var old = Doc(("Game", new[] { ("K1", 1u, "Переклад один"), ("K2", 2u, "Переклад два") }));
        var newer = Doc(("Game", new[] { ("K1", 1u, "Source one"), ("K2", 2u, "Source two"), ("K3", 3u, "Brand new") }));

        var (_, saved) = Run(old, newer, MergeVerification.Hash);

        Assert.Equal("Переклад один", Value(saved, "Game", "K1"));
        Assert.Equal("Переклад два", Value(saved, "Game", "K2"));
        Assert.Equal("Brand new", Value(saved, "Game", "K3"));   // new key keeps the original
    }

    [Fact]
    public void A_rejected_row_ships_the_NEW_original_not_the_stale_translation()
    {
        // The point of the whole redesign. The old translation was written for
        // wording that no longer exists, so the file must carry the new English
        // and the row must read as untranslated so it gets redone.
        var old = Doc(("Game", new[] { ("K1", 111u, "Стара фраза") }));
        var newer = Doc(("Game", new[] { ("K1", 222u, "Completely reworded line") }));

        var (match, saved) = Run(old, newer, MergeVerification.Hash);

        Assert.Equal("Completely reworded line", Value(saved, "Game", "K1"));
        Assert.Equal(1, match.RejectedRows);
        Assert.True(RowsFrom(newer, match).Single().IsUntranslated);
    }

    [Fact]
    public void Key_only_puts_the_same_row_back_when_the_user_asks_for_it()
    {
        var old = Doc(("Game", new[] { ("K1", 111u, "Стара фраза") }));
        var newer = Doc(("Game", new[] { ("K1", 222u, "Reworded") }));

        var (_, saved) = Run(old, newer, MergeVerification.KeyOnly);

        Assert.Equal("Стара фраза", Value(saved, "Game", "K1"));
    }

    [Fact]
    public void The_saved_file_carries_the_NEW_versions_hashes()
    {
        // The field the engine matches on. Writing the old hash would make the
        // game ignore the translation entirely — a silent, total failure.
        var old = Doc(("Game", new[] { ("K1", 0xAAAAu, "Переклад") }));
        var newer = Doc(("Game", new[] { ("K1", 0xAAAAu, "Same source") }));

        var (_, saved) = Run(old, newer, MergeVerification.Hash);

        Assert.Equal(0xAAAAu, saved.Namespaces[0].Entries[0].SourceStringHash);
        Assert.Equal("Переклад", saved.Namespaces[0].Entries[0].Value);
    }

    [Fact]
    public void A_key_the_new_game_dropped_is_not_written()
    {
        var old = Doc(("Game", new[] { ("Kept", 1u, "Лишився"), ("Gone", 9u, "Видалений") }));
        var newer = Doc(("Game", new[] { ("Kept", 1u, "Src") }));

        var (match, saved) = Run(old, newer, MergeVerification.Hash);

        Assert.Equal(1, saved.EntryCount);
        Assert.Equal("Лишився", Value(saved, "Game", "Kept"));
        Assert.Equal(1, match.RemovedKeys);
    }

    [Fact]
    public void A_namespace_added_by_the_update_survives_the_save()
    {
        var old = Doc(("Game", new[] { ("K1", 1u, "Переклад") }));
        var newer = Doc(("Game", new[] { ("K1", 1u, "Src") }),
                        ("DLC", new[] { ("D1", 5u, "Dlc line") }));

        var (_, saved) = Run(old, newer, MergeVerification.Hash);

        Assert.Equal(2, saved.Namespaces.Count);
        Assert.Equal("Dlc line", Value(saved, "DLC", "D1"));
        Assert.Equal("Переклад", Value(saved, "Game", "K1"));
    }

    [Fact]
    public void A_translation_that_is_only_whitespace_still_reaches_the_file()
    {
        // Edge whitespace is load-bearing here; a " / " separator is a deliberate
        // value, and BuildOverrides must not treat it as "no edit".
        var old = Doc(("Game", new[] { ("Sep", 1u, " / ") }));
        var newer = Doc(("Game", new[] { ("Sep", 1u, "-") }));

        var (_, saved) = Run(old, newer, MergeVerification.KeyOnly);

        Assert.Equal(" / ", Value(saved, "Game", "Sep"));
    }

    [Fact]
    public void An_edit_made_after_the_merge_wins_over_the_carried_value()
    {
        var old = Doc(("Game", new[] { ("K1", 1u, "Старий переклад") }));
        var newer = Doc(("Game", new[] { ("K1", 1u, "Src") }));

        var match = MergeMatcher.Match(newer, MergeMatcher.FromDocument(old), MergeVerification.Hash);
        var rows = RowsFrom(newer, match);
        rows[0].Target = "Виправлений переклад";

        Assert.Equal("Виправлений переклад", Value(SaveAndReload(newer, rows), "Game", "K1"));
    }

    // ── the counters the user reads ───────────────────────────────────────

    [Fact]
    public void Only_the_rows_with_nothing_carried_read_as_untranslated()
    {
        // "Untranslated" is an empty Target, and it is what AI Translate scopes
        // to. It must cover exactly the work the merge left: new keys plus the
        // rows whose original changed.
        var old = Doc(("Game", new[] { ("Same", 1u, "Той самий"), ("Changed", 1u, "Змінений") }));
        var newer = Doc(("Game", new[] { ("Same", 1u, "A"), ("Changed", 99u, "B"), ("Fresh", 5u, "C") }));

        var match = MergeMatcher.Match(newer, MergeMatcher.FromDocument(old), MergeVerification.Hash);
        var rows = RowsFrom(newer, match);

        Assert.Equal(1, rows.Count(r => !r.IsUntranslated));   // Same
        Assert.Equal(2, rows.Count(r => r.IsUntranslated));    // Changed + Fresh
    }

    [Fact]
    public void The_grid_tints_tell_the_two_kinds_of_empty_row_apart()
    {
        // Both are untranslated, but "new key" and "your translation was dropped
        // because the wording changed" are different jobs.
        var old = Doc(("Game", new[] { ("Changed", 1u, "Змінений") }));
        var newer = Doc(("Game", new[] { ("Changed", 99u, "B"), ("Fresh", 5u, "C") }));

        var match = MergeMatcher.Match(newer, MergeMatcher.FromDocument(old), MergeVerification.Hash);
        var rows = RowsFrom(newer, match);

        Assert.True(rows.Single(r => r.Key == "Changed").IsSourceChanged);
        Assert.False(rows.Single(r => r.Key == "Changed").IsNewKey);
        Assert.True(rows.Single(r => r.Key == "Fresh").IsNewKey);
        Assert.False(rows.Single(r => r.Key == "Fresh").IsSourceChanged);
    }

    // ── the modern table formats ──────────────────────────────────────────

    private static LocresDocument As(LocresDocument doc, LocresVersion version)
    {
        using var ms = new MemoryStream();
        doc.Save(ms, new LocresSaveOptions { TargetVersion = version });
        return LocresDocument.Parse(ms.ToArray());
    }

    [Theory]
    [InlineData(LocresVersion.Optimized)]
    [InlineData(LocresVersion.OptimizedCityHash64)]
    public void The_whole_operation_works_on_the_table_formats(LocresVersion version)
    {
        // v2/v3 store values in a shared string table with reference counts, a
        // completely different writer path — and v3 is what a current Unreal game
        // actually ships.
        var old = As(Doc(("Game", new[] { ("K1", 1u, "Переклад один"), ("K2", 2u, "Переклад два") })), version);
        var newer = As(Doc(("Game", new[] { ("K1", 1u, "Source one"), ("K2", 2u, "Source two"), ("K3", 3u, "New") })), version);

        var (match, saved) = Run(old, newer, MergeVerification.Hash);

        Assert.Equal(version, saved.Version);
        Assert.Equal(3, saved.EntryCount);
        Assert.Equal("Переклад один", Value(saved, "Game", "K1"));
        Assert.Equal("Переклад два", Value(saved, "Game", "K2"));
        Assert.Equal("New", Value(saved, "Game", "K3"));
        Assert.Equal(1, match.NewRows);
    }

    [Fact]
    public void Two_keys_sharing_one_translation_survive_the_table_writer()
    {
        // The table writer de-duplicates identical values and counts references.
        // Repeated strings are everywhere in a game catalogue ("Yes", "Close").
        var old = As(Doc(("Game", new[] { ("A", 1u, "Так"), ("B", 2u, "Так"), ("C", 3u, "Ні") })),
                     LocresVersion.OptimizedCityHash64);
        var newer = As(Doc(("Game", new[] { ("A", 1u, "Yes"), ("B", 2u, "Yes"), ("C", 3u, "No") })),
                       LocresVersion.OptimizedCityHash64);

        var (_, saved) = Run(old, newer, MergeVerification.Hash);

        Assert.Equal("Так", Value(saved, "Game", "A"));
        Assert.Equal("Так", Value(saved, "Game", "B"));
        Assert.Equal("Ні", Value(saved, "Game", "C"));
    }

    // ── an old CSV as the source of translations ──────────────────────────

    [Fact]
    public void A_csv_with_source_and_target_carries_only_the_rows_it_can_prove()
    {
        var oldRows = new[]
        {
            new TranslationRow { Namespace = "Game", Key = "Same", Source = "Open door", Target = "Відчинити двері" },
            new TranslationRow { Namespace = "Game", Key = "Changed", Source = "Old wording", Target = "Старе формулювання" },
        };
        var newer = Doc(("Game", new[] { ("Same", 0u, "Open door"), ("Changed", 0u, "New wording entirely") }));

        var old = MergeMatcher.FromRows(oldRows, out _);
        var match = MergeMatcher.Match(newer, old, MergeVerification.SourceText);
        var saved = SaveAndReload(newer, RowsFrom(newer, match));

        Assert.Equal("Відчинити двері", Value(saved, "Game", "Same"));
        Assert.Equal("New wording entirely", Value(saved, "Game", "Changed"));   // stale one refused
        Assert.Equal(1, match.RejectedRows);
    }
}
