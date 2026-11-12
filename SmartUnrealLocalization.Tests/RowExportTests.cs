using System.Text;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// The one-language ("flattened") export. It exists so a QC review of a
/// single-language file can be handed back as one corrected column: the
/// translation is reviewed in Source, accepted fixes land in Target, and this
/// collapses the pair using the same rule .locres saving applies natively.
/// </summary>
public class RowExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sul-flat-tests", Guid.NewGuid().ToString("N"));

    public RowExportTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static TranslationRow Row(string source, string target, string ns = "UI", string key = "K")
        => new() { Namespace = ns, Key = key, Source = source, Target = target };

    // ── the collapse rule ────────────────────────────────────────────────

    [Fact]
    public void A_translated_row_takes_its_target()
        => Assert.Equal("Чекай...", RowExport.Flatten(new[] { Row("Чекай", "Чекай...") })[0].Source);

    [Fact]
    public void An_untranslated_row_keeps_its_source()
    {
        // Exactly the .locres SaveWith contract: an empty target keeps the base
        // value, so untouched rows survive the round trip unchanged.
        Assert.Equal("Чекай", RowExport.Flatten(new[] { Row("Чекай", "") })[0].Source);
    }

    [Fact]
    public void The_target_column_is_emptied_so_the_result_reads_as_one_language()
        => Assert.Equal("", RowExport.Flatten(new[] { Row("Чекай", "Чекай...") })[0].Target);

    [Fact]
    public void Identity_and_comments_are_carried_through()
    {
        var row = Row("Чекай", "Чекай...", ns: "Weapon", key: "Charge");
        row.Comment = "reviewer note";

        var flat = RowExport.Flatten(new[] { row })[0];

        Assert.Equal("Weapon", flat.Namespace);
        Assert.Equal("Charge", flat.Key);
        Assert.Equal("reviewer note", flat.Comment);
    }

    [Fact]
    public void The_input_rows_are_left_untouched()
    {
        // Flatten runs on the live grid rows just before writing; mutating them
        // would silently rewrite the user's open file.
        var row = Row("Чекай", "Чекай...");
        RowExport.Flatten(new[] { row });

        Assert.Equal("Чекай", row.Source);
        Assert.Equal("Чекай...", row.Target);
    }

    [Fact]
    public void An_empty_input_produces_an_empty_result()
        => Assert.Empty(RowExport.Flatten(Array.Empty<TranslationRow>()));

    // ── end to end: the QC loop actually closes ──────────────────────────

    [Fact]
    public void A_reviewed_single_language_file_exports_as_one_corrected_column()
    {
        // The whole point of stage 3. Open a Ukrainian file (text in Source,
        // Target empty), accept a QC fix on one row (fix lands in Target), then
        // export: the corrected row must carry the fix, the untouched row its
        // original text, and neither may leak a second column.
        var rows = new List<TranslationRow>
        {
            Row("Чекай", "Чекай...", key: "K1"),   // QC fix accepted
            Row("Відкрити", "", key: "K2"),        // untouched
        };

        string path = Path.Combine(_dir, "one-language.csv");
        CsvFileService.Export(path, RowExport.Flatten(rows), includeNamespace: true);
        var back = CsvFileService.Import(path);

        Assert.Equal(2, back.Count);
        Assert.Equal("Чекай...", back[0].Source);
        Assert.Equal("Відкрити", back[1].Source);
        Assert.All(back, r => Assert.True(string.IsNullOrEmpty(r.Target)));
    }

    [Fact]
    public void The_flattened_file_round_trips_through_PO_as_well()
    {
        var rows = new List<TranslationRow> { Row("Чекай", "Чекай...", key: "K1") };

        string path = Path.Combine(_dir, "one-language.po");
        PoFileService.Export(path, RowExport.Flatten(rows), "uk");
        var back = PoFileService.Import(path);

        Assert.Equal("Чекай...", back[0].Source);
    }

    [Fact]
    public void Awkward_text_survives_the_flattened_export()
    {
        var rows = new List<TranslationRow>
        {
            Row("x", "Say \"hi\" {0}\nsecond line ", key: "K1"),
            Row("emoji 😀 中文", "", key: "K2"),
        };

        string path = Path.Combine(_dir, "awkward.csv");
        CsvFileService.Export(path, RowExport.Flatten(rows), includeNamespace: true);
        var back = CsvFileService.Import(path);

        Assert.Equal("Say \"hi\" {0}\nsecond line ", back[0].Source);
        Assert.Equal("emoji 😀 中文", back[1].Source);
    }
}
