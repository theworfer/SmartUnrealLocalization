using Locres.IO;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// Which translations a merge may carry into a new game version, and which it
/// must refuse.
///
/// <para>This is the one operation in the app that can ruin a whole file without
/// throwing: carry a translation onto an entry whose original text has changed
/// since, and the game ships a line that says the wrong thing. Every rule below
/// exists to make that impossible by accident.</para>
/// </summary>
public class MergeMatcherTests
{
    // ── helpers ───────────────────────────────────────────────────────────

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
            if (s.Length == 0) { w.Write(0); return; }
            w.Write(-(s.Length + 1));
            foreach (char c in s) w.Write((ushort)c);
            w.Write((ushort)0);
        }
    }

    private static LocresDocument Doc(params (string Ns, (string Key, uint Hash, string Value)[] Entries)[] ns)
        => LocresDocument.Parse(Legacy(ns));

    private static TranslationRow Row(string? ns, string key, string source, string target)
        => new() { Namespace = ns ?? "", Key = key, Source = source, Target = target };

    private static string? Carried(MergeMatchResult r, string ns, string key)
        => r.Carried.TryGetValue((ns, key), out var v) ? v : null;

    // ── locres: verification by SourceStringHash ──────────────────────────

    [Fact]
    public void An_unchanged_original_carries_its_translation()
    {
        var old = MergeMatcher.FromDocument(Doc(("Game", new[] { ("K1", 111u, "Переклад") })));
        var newer = Doc(("Game", new[] { ("K1", 111u, "Source") }));

        var r = MergeMatcher.Match(newer, old, MergeVerification.Hash);

        Assert.Equal("Переклад", Carried(r, "Game", "K1"));
        Assert.Equal(0, r.RejectedRows);
    }

    [Fact]
    public void A_changed_original_leaves_the_row_empty()
    {
        // The whole point: a different hash means the English changed, so the old
        // translation was written for wording that no longer exists.
        var old = MergeMatcher.FromDocument(Doc(("Game", new[] { ("K1", 111u, "Переклад") })));
        var newer = Doc(("Game", new[] { ("K1", 222u, "Reworded source") }));

        var r = MergeMatcher.Match(newer, old, MergeVerification.Hash);

        Assert.Null(Carried(r, "Game", "K1"));
        Assert.Contains(("Game", "K1"), r.Rejected);
        Assert.Equal(1, r.RejectedRows);
    }

    [Fact]
    public void Key_only_carries_the_same_row_the_hash_check_refused()
    {
        var old = MergeMatcher.FromDocument(Doc(("Game", new[] { ("K1", 111u, "Переклад") })));
        var newer = Doc(("Game", new[] { ("K1", 222u, "Reworded") }));

        var r = MergeMatcher.Match(newer, old, MergeVerification.KeyOnly);

        Assert.Equal("Переклад", Carried(r, "Game", "K1"));
        Assert.Equal(0, r.RejectedRows);
    }

    [Fact]
    public void A_locres_offers_hash_verification()
        => Assert.Equal(MergeVerification.Hash,
            MergeMatcher.StrongestVerification(
                MergeMatcher.FromDocument(Doc(("N", new[] { ("K", 1u, "V") })))));

    [Fact]
    public void Every_hash_differing_is_reported_rather_than_silently_wiping_the_file()
    {
        // A file whose hashes were rebuilt by some other tool looks 100% changed.
        // Nothing is carried — which is why the caller must show the counts and
        // offer key-only instead of writing an empty file.
        var old = MergeMatcher.FromDocument(Doc(("Game", new[]
        {
            ("K1", 0u, "Один"), ("K2", 0u, "Два"), ("K3", 0u, "Три"),
        })));
        var newer = Doc(("Game", new[] { ("K1", 11u, "A"), ("K2", 22u, "B"), ("K3", 33u, "C") }));

        var r = MergeMatcher.Match(newer, old, MergeVerification.Hash);

        Assert.Equal(0, r.CarriedRows);
        Assert.Equal(3, r.RejectedRows);
        Assert.Equal(0.0, r.VerifiedShare);
    }

    // ── CSV / PO: verification by source text ─────────────────────────────

    [Fact]
    public void A_matching_source_column_carries_the_target()
    {
        var old = MergeMatcher.FromRows(new[] { Row("Game", "K1", "Open door", "Відчинити двері") }, out bool hasTarget);
        var newer = Doc(("Game", new[] { ("K1", 0u, "Open door") }));

        Assert.True(hasTarget);
        Assert.Equal(MergeVerification.SourceText, MergeMatcher.StrongestVerification(old));
        Assert.Equal("Відчинити двері",
                     Carried(MergeMatcher.Match(newer, old, MergeVerification.SourceText), "Game", "K1"));
    }

    [Fact]
    public void A_source_column_that_no_longer_matches_is_refused()
    {
        var old = MergeMatcher.FromRows(new[] { Row("Game", "K1", "Open door", "Відчинити двері") }, out _);
        var newer = Doc(("Game", new[] { ("K1", 0u, "Open the heavy door") }));

        var r = MergeMatcher.Match(newer, old, MergeVerification.SourceText);

        Assert.Null(Carried(r, "Game", "K1"));
        Assert.Contains(("Game", "K1"), r.Rejected);
    }

    [Fact]
    public void A_csv_that_went_through_a_spreadsheet_still_matches()
    {
        // Excel and Google Sheets rewrite embedded LF as CRLF. Comparing literally
        // would fail on every multi-line row — precisely the long translations.
        var old = MergeMatcher.FromRows(
            new[] { Row("Game", "K1", "First line\r\nSecond line", "Перший рядок\r\nДругий рядок") }, out _);
        var newer = Doc(("Game", new[] { ("K1", 0u, "First line\nSecond line") }));

        var r = MergeMatcher.Match(newer, old, MergeVerification.SourceText);

        // Matched despite the line endings…
        Assert.NotNull(Carried(r, "Game", "K1"));
        // …and the translation is carried EXACTLY as it was, CRLF included.
        Assert.Equal("Перший рядок\r\nДругий рядок", Carried(r, "Game", "K1"));
    }

    [Fact]
    public void Edge_whitespace_is_never_normalised_away()
    {
        // A leading space is load-bearing here (strings are concatenated at
        // runtime), so a difference in it is a real difference, not noise.
        var old = MergeMatcher.FromRows(new[] { Row("Game", "K1", " Loading", "Ładowanie") }, out _);
        var newer = Doc(("Game", new[] { ("K1", 0u, "Loading") }));

        Assert.Empty(MergeMatcher.Match(newer, old, MergeVerification.SourceText).Carried);
    }

    [Fact]
    public void A_single_language_csv_has_nothing_to_verify_against()
    {
        // 2-column CSV: the source column IS the translation, like a locres.
        var old = MergeMatcher.FromRows(new[] { Row("Game", "K1", "Переклад", "") }, out bool hasTarget);

        Assert.False(hasTarget);
        Assert.Equal(MergeVerification.KeyOnly, MergeMatcher.StrongestVerification(old));
        Assert.Equal("Переклад", old[0].Translation);
        Assert.Null(old[0].SourceText);
    }

    [Fact]
    public void A_file_with_some_targets_filled_treats_target_as_the_translation()
    {
        // One filled Target is enough to say "this is a two-language file"; rows
        // with an empty Target simply have nothing to carry.
        var old = MergeMatcher.FromRows(new[]
        {
            Row("Game", "K1", "Open", "Відчинити"),
            Row("Game", "K2", "Close", ""),
        }, out bool hasTarget);
        var newer = Doc(("Game", new[] { ("K1", 0u, "Open"), ("K2", 0u, "Close") }));

        Assert.True(hasTarget);
        var r = MergeMatcher.Match(newer, old, MergeVerification.SourceText);

        Assert.Equal("Відчинити", Carried(r, "Game", "K1"));
        Assert.Null(Carried(r, "Game", "K2"));
        Assert.Equal(1, r.EmptyRows);       // not a rejection — there was nothing to carry
        Assert.Equal(0, r.RejectedRows);
    }

    // ── namespaces ────────────────────────────────────────────────────────

    [Fact]
    public void Namespaces_are_used_when_the_old_file_has_them()
    {
        // Same key in two namespaces must not cross over.
        var old = MergeMatcher.FromRows(new[]
        {
            Row("Game", "Cancel", "Cancel", "Скасувати"),
            Row("DLC", "Cancel", "Cancel", "Відмінити"),
        }, out _);
        var newer = Doc(("Game", new[] { ("Cancel", 0u, "Cancel") }),
                        ("DLC", new[] { ("Cancel", 0u, "Cancel") }));

        var r = MergeMatcher.Match(newer, old, MergeVerification.SourceText);

        Assert.True(r.UsedNamespace);
        Assert.Equal("Скасувати", Carried(r, "Game", "Cancel"));
        Assert.Equal("Відмінити", Carried(r, "DLC", "Cancel"));
        Assert.Equal(0, r.AmbiguousKeys);
    }

    [Fact]
    public void A_namespaceless_csv_matches_bare_keys_and_says_where_that_is_risky()
    {
        // 3-column CSV has no namespace. One old row then lands on every namespace
        // that owns the key — reported so the user is not left guessing.
        var old = MergeMatcher.FromRows(new[] { Row(null, "Cancel", "Cancel", "Скасувати") }, out _);
        var newer = Doc(("Game", new[] { ("Cancel", 0u, "Cancel") }),
                        ("DLC", new[] { ("Cancel", 0u, "Cancel") }));

        var r = MergeMatcher.Match(newer, old, MergeVerification.SourceText);

        Assert.False(r.UsedNamespace);
        Assert.Equal("Скасувати", Carried(r, "Game", "Cancel"));
        Assert.Equal("Скасувати", Carried(r, "DLC", "Cancel"));
        Assert.Equal(2, r.AmbiguousKeys);
    }

    [Fact]
    public void A_namespaceless_key_that_lives_in_one_namespace_is_not_ambiguous()
    {
        var old = MergeMatcher.FromRows(new[] { Row(null, "K1", "Open", "Відчинити") }, out _);
        var newer = Doc(("Game", new[] { ("K1", 0u, "Open") }), ("DLC", new[] { ("D1", 0u, "Other") }));

        Assert.Equal(0, MergeMatcher.Match(newer, old, MergeVerification.SourceText).AmbiguousKeys);
    }

    // ── counting ──────────────────────────────────────────────────────────

    [Fact]
    public void The_report_accounts_for_every_row_in_the_new_file()
    {
        // carried + rejected + emptyInOld + newKeys must equal the row count, or a
        // number on the confirmation dialog is lying to the user.
        var old = MergeMatcher.FromDocument(Doc(("Game", new[]
        {
            ("Same", 1u, "Той самий"),
            ("Changed", 1u, "Змінений"),
            ("Empty", 1u, ""),
            ("Gone", 1u, "Видалений"),
        })));
        var newer = Doc(("Game", new[]
        {
            ("Same", 1u, "A"), ("Changed", 99u, "B"), ("Empty", 1u, "C"), ("Fresh", 1u, "D"),
        }));

        var r = MergeMatcher.Match(newer, old, MergeVerification.Hash);

        Assert.Equal(4, r.TotalRows);
        Assert.Equal(1, r.CarriedRows);      // Same
        Assert.Equal(1, r.RejectedRows);     // Changed
        Assert.Equal(1, r.EmptyRows);        // Empty
        Assert.Equal(1, r.NewRows);           // Fresh
        Assert.Equal(1, r.RemovedKeys);       // Gone
        Assert.Equal(r.TotalRows, r.CarriedRows + r.RejectedRows + r.EmptyRows + r.NewRows);
    }

    [Fact]
    public void Duplicate_keys_in_the_new_file_are_counted_once_but_all_receive_the_value()
    {
        // Duplicates are legal in .locres. Every copy must get the translation, or
        // the game shows a mix of translated and untranslated for one string.
        var old = MergeMatcher.FromDocument(Doc(("NS", new[] { ("Dup", 1u, "Переклад") })));
        var newer = Doc(("NS", new[] { ("Dup", 1u, "A"), ("Dup", 1u, "A") }));

        var r = MergeMatcher.Match(newer, old, MergeVerification.Hash);

        Assert.Equal(2, r.TotalRows);
        // BOTH rows get the value, and both are counted: the report speaks in
        // grid rows, and a duplicated key really is two lines in the game.
        Assert.Equal(2, r.CarriedRows);
        Assert.Equal("Переклад", Carried(r, "NS", "Dup"));
    }

    [Fact]
    public void Duplicate_keys_in_the_old_file_take_the_first_occurrence()
    {
        var old = MergeMatcher.FromRows(new[]
        {
            Row("NS", "Dup", "Src", "Перший"),
            Row("NS", "Dup", "Src", "Другий"),
        }, out _);
        var newer = Doc(("NS", new[] { ("Dup", 0u, "Src") }));

        Assert.Equal("Перший", Carried(MergeMatcher.Match(newer, old, MergeVerification.SourceText), "NS", "Dup"));
    }

    [Fact]
    public void The_verified_share_reflects_how_much_of_the_match_survived()
    {
        var old = MergeMatcher.FromDocument(Doc(("N", new[] { ("A", 1u, "a"), ("B", 1u, "b") })));
        var newer = Doc(("N", new[] { ("A", 1u, "x"), ("B", 2u, "y") }));

        Assert.Equal(0.5, MergeMatcher.Match(newer, old, MergeVerification.Hash).VerifiedShare);
    }

    [Fact]
    public void An_empty_old_file_carries_nothing_and_reports_everything_as_new()
    {
        var newer = Doc(("N", new[] { ("A", 1u, "x"), ("B", 2u, "y") }));

        var r = MergeMatcher.Match(newer, Array.Empty<MergeSource>(), MergeVerification.Hash);

        Assert.Empty(r.Carried);
        Assert.Equal(2, r.NewRows);
        Assert.Equal(0, r.RemovedKeys);
        // Nothing was verified, so nothing FAILED verification — a full share is
        // the honest answer, and it keeps the low-match alarm quiet.
        Assert.Equal(1.0, r.VerifiedShare);
    }


    [Fact]
    public void Rows_with_nothing_to_carry_do_not_drag_the_verified_share_down()
    {
        // Half the old file is untranslated but every hash matches: verification
        // rejected NOTHING. Counting the empty ones made this read as "half the
        // matches failed" and fired the rebuilt-hashes alarm on a healthy file.
        var old = MergeMatcher.FromDocument(Doc(("N", new[] { ("A", 1u, "a"), ("B", 1u, "") })));
        var newer = Doc(("N", new[] { ("A", 1u, "x"), ("B", 1u, "y") }));

        var r = MergeMatcher.Match(newer, old, MergeVerification.Hash);

        Assert.Equal(1, r.CarriedRows);
        Assert.Equal(1, r.EmptyRows);
        Assert.Equal(0, r.RejectedRows);
        Assert.Equal(1.0, r.VerifiedShare);
    }

    [Fact]
    public void Every_row_of_a_file_with_duplicate_keys_is_still_accounted_for()
    {
        // A .locres may repeat a key. Counting outcomes by DISTINCT KEY while
        // reporting against the ROW count made the report`s numbers not add up.
        var old = MergeMatcher.FromDocument(Doc(("N", new[] { ("Same", 1u, "T"), ("Gone", 1u, "") })));
        var newer = Doc(("N", new[]
        {
            ("Same", 1u, "a"), ("Same", 1u, "a"),      // duplicate, carried
            ("Gone", 1u, "b"), ("Gone", 1u, "b"),      // duplicate, empty in old
            ("Fresh", 1u, "c"),
        }));

        var r = MergeMatcher.Match(newer, old, MergeVerification.Hash);

        Assert.Equal(5, r.TotalRows);
        Assert.Equal(2, r.CarriedRows);
        Assert.Equal(2, r.EmptyRows);
        Assert.Equal(1, r.NewRows);
        Assert.Equal(r.TotalRows, r.CarriedRows + r.RejectedRows + r.EmptyRows + r.NewRows);
    }

    [Fact]
    public void Matching_is_case_sensitive_on_both_namespace_and_key()
    {
        // Unreal treats them as exact identifiers; loose matching would carry a
        // translation onto the wrong entry, which is worse than not carrying it.
        var old = MergeMatcher.FromRows(new[] { Row("Game", "Key", "Src", "Переклад") }, out _);
        var newer = Doc(("game", new[] { ("key", 0u, "Src") }));

        var r = MergeMatcher.Match(newer, old, MergeVerification.SourceText);

        Assert.Empty(r.Carried);
        Assert.Equal(1, r.NewRows);
    }

    [Fact]
    public void Nothing_throws_on_hostile_input()
    {
        var newer = Doc(("", new[] { ("", 0u, ""), ("K", 0u, "V") }));
        var old = MergeMatcher.FromRows(new[]
        {
            Row("", "", "", ""), Row(null, "K", "V", "T"), Row("NS", "K", "", "\r\n"),
        }, out _);

        foreach (var mode in new[] { MergeVerification.KeyOnly, MergeVerification.Hash, MergeVerification.SourceText })
            MergeMatcher.Match(newer, old, mode);      // must not throw
    }
}
