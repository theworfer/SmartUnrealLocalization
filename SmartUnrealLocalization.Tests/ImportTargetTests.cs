using Locres.IO;
using SmartUnrealLocalization.Models;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// "Import Target locres" — load a second .locres as the translation for the
/// open file, matched by key.
///
/// <para>The rule it got wrong for a long time: a translation that happens to be
/// IDENTICAL to the source was silently dropped, leaving the row with an empty
/// Target. Plenty of real translations are identical — tag-only strings the app
/// carries rather than translates, numbers, proper nouns, and short UI words
/// that are the same in both languages. Those rows then read as
/// <b>untranslated</b>: the counter lied, and AI Translate scoped to
/// "untranslated" would have paid to translate "OK" into "OK".</para>
///
/// <para>The import loop lives in the view model, which needs a window. These
/// tests exercise the same matching and decision rules against the same inputs,
/// so the rule itself is pinned even though the loop is not.</para>
/// </summary>
public class ImportTargetTests
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
            if (s.Length == 0) { w.Write(0); return; }
            w.Write(-(s.Length + 1));
            foreach (char c in s) w.Write((ushort)c);
            w.Write((ushort)0);
        }
    }

    private sealed record Outcome(int Matched, int Imported, int SameAsSource, int AlreadyEqual, int Unmatched);

    /// <summary>The import loop's rules, applied to rows the way the view model does.</summary>
    private static Outcome Import(List<TranslationRow> rows, LocresDocument target)
    {
        var exact = new Dictionary<(string Ns, string Key), string>();
        var flattened = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ns in target.Namespaces)
            foreach (var e in ns.Entries)
            {
                if (e.Value.Length == 0) continue;      // nothing to import
                exact[(ns.Name, e.Key)] = e.Value;
                flattened[ns.Name + "/" + e.Key] = e.Value;
            }

        int matched = 0, imported = 0, sameAsSource = 0, alreadyEqual = 0;
        foreach (var row in rows)
        {
            if (!exact.TryGetValue((row.Namespace ?? "", row.Key ?? ""), out var translated)
                && !(string.IsNullOrEmpty(row.Namespace) && row.Key != null
                     && flattened.TryGetValue(row.Key, out translated)))
                continue;

            matched++;
            if (translated == row.Source) sameAsSource++;
            if (translated == row.Target) { alreadyEqual++; continue; }

            row.Target = translated;
            imported++;
        }
        return new Outcome(matched, imported, sameAsSource, alreadyEqual, rows.Count - matched);
    }

    private static TranslationRow Row(string ns, string key, string source, string target = "")
        => new() { Namespace = ns, Key = key, Source = source, Target = target };

    // ── the reported bug ──────────────────────────────────────────────────

    [Fact]
    public void A_translation_identical_to_the_source_is_imported_like_any_other()
    {
        // The reported symptom: "some rows stayed empty even with the same key".
        var rows = new List<TranslationRow>
        {
            Row("Game", "Ok", "OK"),          // same word in both languages
            Row("Game", "Count", "100"),      // a number
            Row("Game", "Break", "<br/>"),    // pure markup, carried not translated
            Row("Game", "Door", "Door"),      // genuinely translated
        };
        var target = LocresDocument.Parse(Legacy(("Game", new[]
        {
            ("Ok", 0u, "OK"), ("Count", 0u, "100"), ("Break", 0u, "<br/>"), ("Door", 0u, "Двері"),
        })));

        var r = Import(rows, target);

        Assert.Equal(4, r.Imported);
        Assert.Equal(3, r.SameAsSource);
        Assert.All(rows, row => Assert.False(row.IsUntranslated));
    }

    [Fact]
    public void Those_rows_no_longer_count_as_untranslated()
    {
        // The consequence that made the old behaviour expensive: "Untranslated"
        // is an empty Target, and it is what AI Translate scopes to.
        var rows = new List<TranslationRow> { Row("Game", "Ok", "OK") };
        Import(rows, LocresDocument.Parse(Legacy(("Game", new[] { ("Ok", 0u, "OK") }))));

        Assert.Equal("OK", rows[0].Target);
        Assert.False(rows[0].IsUntranslated);
    }

    // ── the rules that were already right ─────────────────────────────────

    [Fact]
    public void An_empty_entry_in_the_imported_file_is_not_written()
    {
        // The target file has the key but no translation for it — there is
        // nothing to import, and blanking a Target we already have would be worse.
        var rows = new List<TranslationRow> { Row("Game", "K", "Source", "Наявний переклад") };
        var r = Import(rows, LocresDocument.Parse(Legacy(("Game", new[] { ("K", 0u, "") }))));

        Assert.Equal(0, r.Matched);
        Assert.Equal("Наявний переклад", rows[0].Target);
    }

    [Fact]
    public void A_row_that_already_holds_that_exact_translation_is_a_no_op()
    {
        var rows = new List<TranslationRow> { Row("Game", "K", "Door", "Двері") };
        var r = Import(rows, LocresDocument.Parse(Legacy(("Game", new[] { ("K", 0u, "Двері") }))));

        Assert.Equal(1, r.Matched);
        Assert.Equal(0, r.Imported);
        Assert.Equal(1, r.AlreadyEqual);
    }

    [Fact]
    public void An_existing_translation_is_overwritten_by_the_imported_one()
    {
        var rows = new List<TranslationRow> { Row("Game", "K", "Door", "Старий") };
        Import(rows, LocresDocument.Parse(Legacy(("Game", new[] { ("K", 0u, "Новий") }))));

        Assert.Equal("Новий", rows[0].Target);
    }

    [Fact]
    public void Namespaces_keep_two_identical_keys_apart()
    {
        var rows = new List<TranslationRow> { Row("Game", "Cancel", "Cancel"), Row("DLC", "Cancel", "Cancel") };
        Import(rows, LocresDocument.Parse(
            Legacy(("Game", new[] { ("Cancel", 0u, "Скасувати") }),
                   ("DLC", new[] { ("Cancel", 0u, "Відмінити") }))));

        Assert.Equal("Скасувати", rows[0].Target);
        Assert.Equal("Відмінити", rows[1].Target);
    }

    [Fact]
    public void A_legacy_flattened_key_still_matches()
    {
        // Pre-LocresIO 3-column CSV tabs carry "ns/key" in Key with no namespace.
        var rows = new List<TranslationRow> { Row("", "Game/K", "Door") };
        Import(rows, LocresDocument.Parse(Legacy(("Game", new[] { ("K", 0u, "Двері") }))));

        Assert.Equal("Двері", rows[0].Target);
    }

    [Fact]
    public void Rows_with_no_key_in_the_imported_file_are_left_alone()
    {
        var rows = new List<TranslationRow> { Row("Game", "Here", "A", "Мій переклад"), Row("Game", "Absent", "B") };
        var r = Import(rows, LocresDocument.Parse(Legacy(("Game", new[] { ("Here", 0u, "А") }))));

        Assert.Equal(1, r.Unmatched);
        Assert.Equal("", rows[1].Target);          // untouched, not blanked
    }

    [Fact]
    public void A_file_from_another_game_matches_nothing_and_changes_nothing()
    {
        var rows = new List<TranslationRow> { Row("Game", "K1", "A", "Моє"), Row("Game", "K2", "B") };
        var r = Import(rows, LocresDocument.Parse(Legacy(("Other", new[] { ("X", 0u, "Щось") }))));

        Assert.Equal(0, r.Matched);
        Assert.Equal("Моє", rows[0].Target);
        Assert.Equal("", rows[1].Target);
    }

    [Fact]
    public void Edge_whitespace_in_an_imported_translation_survives()
    {
        // Load-bearing here: strings are concatenated at runtime.
        var rows = new List<TranslationRow> { Row("Game", "Sep", "/") };
        Import(rows, LocresDocument.Parse(Legacy(("Game", new[] { ("Sep", 0u, " / ") }))));

        Assert.Equal(" / ", rows[0].Target);
    }
}
