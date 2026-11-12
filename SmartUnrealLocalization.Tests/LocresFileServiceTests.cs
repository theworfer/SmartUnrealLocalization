using System.Text;
using Locres.IO;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// The glue between the LocresIO core and the grid rows. The core itself is
/// covered by LocresIO.Tests; what matters here is that the app hands it the
/// right overrides — a mistake means translations silently do not reach the file.
/// </summary>
public class LocresFileServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sul-locres-tests", Guid.NewGuid().ToString("N"));

    public LocresFileServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ── a real v0 file, built to the spec in LocresIO/FORMAT.md ──────────
    // Legacy has no header: int32 namespaceCount, then per namespace a wide
    // FString name, int32 keyCount, and per entry {FString key, uint32 hash,
    // FString value}. FString length INCLUDES the terminator and is negative
    // for UTF-16.

    private static void WriteWide(BinaryWriter w, string s)
    {
        w.Write(-(s.Length + 1));
        foreach (char c in s) w.Write((ushort)c);
        w.Write((ushort)0);
    }

    private string BuildLegacyLocres(params (string Ns, (string Key, string Value)[] Entries)[] namespaces)
    {
        string path = Path.Combine(_dir, $"test-{Guid.NewGuid():N}.locres");
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs, Encoding.Unicode);

        w.Write(namespaces.Length);
        foreach (var (ns, entries) in namespaces)
        {
            WriteWide(w, ns);
            w.Write(entries.Length);
            foreach (var (key, value) in entries)
            {
                WriteWide(w, key);
                w.Write(0u);            // SourceStringHash — pass-through, never recomputed
                WriteWide(w, value);
            }
        }
        return path;
    }

    // ── opening ──────────────────────────────────────────────────────────

    [Fact]
    public void Opening_yields_one_row_per_entry_with_the_real_namespace()
    {
        string path = BuildLegacyLocres(
            ("", new[] { ("KeyA", "Hello"), ("KeyB", "World") }),
            ("UI", new[] { ("Play", "Play") }));

        var (doc, rows) = LocresFileService.Open(path);

        Assert.Equal(3, doc.EntryCount);
        Assert.Equal(3, rows.Count);
        Assert.Equal("", rows[0].Namespace);
        Assert.Equal("KeyA", rows[0].Key);
        Assert.Equal("Hello", rows[0].Source);
        Assert.Equal("UI", rows[2].Namespace);
        Assert.Equal("Play", rows[2].Key);
    }

    [Fact]
    public void The_source_is_the_files_value_and_the_target_starts_empty()
    {
        // A .locres holds ONE language: it becomes the Source column, and the
        // user's translation goes into an empty Target.
        var (_, rows) = LocresFileService.Open(BuildLegacyLocres(("", new[] { ("K", "Hello") })));

        Assert.Equal("Hello", rows[0].Source);
        Assert.Equal("", rows[0].Target);
    }

    [Fact]
    public void Non_ascii_values_survive_the_read()
    {
        var (_, rows) = LocresFileService.Open(
            BuildLegacyLocres(("", new[] { ("K", "Привіт 😀 中文") })));

        Assert.Equal("Привіт 😀 中文", rows[0].Source);
    }

    // ── overrides for the document the rows came from ────────────────────

    [Fact]
    public void Only_translated_rows_become_overrides()
    {
        // Empty targets are omitted on purpose: SaveWith keeps the base value for
        // them by contract, and omitting keeps the written/kept counts meaningful.
        var overrides = LocresFileService.BuildOverrides(new List<TranslationRow>
        {
            new() { Namespace = "UI", Key = "K1", Source = "Play", Target = "Грати" },
            new() { Namespace = "UI", Key = "K2", Source = "Quit", Target = "" },
            new() { Namespace = "UI", Key = "K3", Source = "Load", Target = null! },
        });

        Assert.Single(overrides);
        Assert.Equal("Грати", overrides[("UI", "K1")]);
    }

    [Fact]
    public void Duplicate_keys_resolve_to_the_first_translated_row()
    {
        // Duplicate (namespace, key) pairs are legal in .locres and all of them
        // receive the same override. First-wins matches how row identity is
        // resolved everywhere else, so the row the user sees flagged by QC is the
        // row whose text actually reaches the file.
        var overrides = LocresFileService.BuildOverrides(new List<TranslationRow>
        {
            new() { Namespace = "UI", Key = "SAME", Source = "Charge", Target = "Зарядити" },
            new() { Namespace = "UI", Key = "SAME", Source = "Charge", Target = "Плата" },
        });

        Assert.Single(overrides);
        Assert.Equal("Зарядити", overrides[("UI", "SAME")]);
    }

    [Fact]
    public void An_untranslated_duplicate_does_not_shadow_its_translated_twin()
    {
        var overrides = LocresFileService.BuildOverrides(new List<TranslationRow>
        {
            new() { Namespace = "UI", Key = "SAME", Source = "Charge", Target = "" },
            new() { Namespace = "UI", Key = "SAME", Source = "Charge", Target = "Зарядити" },
        });

        Assert.Equal("Зарядити", overrides[("UI", "SAME")]);
    }

    [Fact]
    public void A_null_namespace_is_normalised_to_the_anonymous_one()
    {
        var overrides = LocresFileService.BuildOverrides(new List<TranslationRow>
        {
            new() { Namespace = null!, Key = "K", Source = "Play", Target = "Грати" },
        });

        Assert.True(overrides.ContainsKey(("", "K")));
    }

    [Fact]
    public void Saving_through_the_retained_document_applies_the_translations()
    {
        // End to end: open → translate → save → reopen. The document skeleton is
        // what every save writes through, never a re-read of the original file.
        string path = BuildLegacyLocres(("UI", new[] { ("Play", "Play"), ("Quit", "Quit") }));
        var (doc, rows) = LocresFileService.Open(path);
        rows[0].Target = "Грати";

        string outPath = Path.Combine(_dir, "saved.locres");
        doc.SaveWith(LocresFileService.BuildOverrides(rows), outPath);

        var (_, back) = LocresFileService.Open(outPath);
        Assert.Equal("Грати", back[0].Source);   // translated
        Assert.Equal("Quit", back[1].Source);    // untouched rows keep the base value
    }

    // ── overrides against a DIFFERENT base (Build Locres from CSV) ───────

    [Fact]
    public void Rows_are_matched_to_a_base_document_by_namespace_and_key()
    {
        var baseDoc = LocresDocument.Load(BuildLegacyLocres(
            ("UI", new[] { ("Play", "Play"), ("Quit", "Quit") })));

        var overrides = LocresFileService.BuildOverridesForBase(baseDoc, new List<TranslationRow>
        {
            new() { Namespace = "UI", Key = "Play", Target = "Грати" },
            new() { Namespace = "UI", Key = "Ghost", Target = "Привид" },   // not in the base
        });

        Assert.Single(overrides);
        Assert.Equal("Грати", overrides[("UI", "Play")]);
    }

    [Fact]
    public void Legacy_three_column_csv_keys_still_match_through_the_flattened_form()
    {
        // Pre-LocresIO exports put "ns/key" in the Key column with no namespace.
        // Dropping that fallback would silently translate nothing for those files.
        var baseDoc = LocresDocument.Load(BuildLegacyLocres(
            ("UI", new[] { ("Play", "Play") }),
            ("", new[] { ("Quit", "Quit") })));

        var overrides = LocresFileService.BuildOverridesForBase(baseDoc, new List<TranslationRow>
        {
            new() { Namespace = "", Key = "UI/Play", Target = "Грати" },
            new() { Namespace = "", Key = "/Quit", Target = "Вийти" },   // anonymous namespace
        });

        Assert.Equal("Грати", overrides[("UI", "Play")]);
        Assert.Equal("Вийти", overrides[("", "Quit")]);
    }

    [Fact]
    public void Untranslated_rows_are_skipped_when_packing_into_a_base()
    {
        var baseDoc = LocresDocument.Load(BuildLegacyLocres(("UI", new[] { ("Play", "Play") })));

        Assert.Empty(LocresFileService.BuildOverridesForBase(baseDoc, new List<TranslationRow>
        {
            new() { Namespace = "UI", Key = "Play", Target = "" },
        }));
    }

    // ── labels ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(LocresVersion.Legacy, "v0")]
    [InlineData(LocresVersion.Compact, "v1")]
    [InlineData(LocresVersion.Optimized, "v2")]
    [InlineData(LocresVersion.OptimizedCityHash64, "v3")]
    public void Every_format_version_has_a_readable_label(LocresVersion version, string expected)
        => Assert.Contains(expected, LocresFileService.VersionLabel(version));
}
