using System.Text;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// CSV import/export. The importer has to survive files written by other tools,
/// so the layout is detected per row and anomalies are reported rather than
/// silently mis-parsed.
/// </summary>
public class CsvFileServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sul-csv-tests", Guid.NewGuid().ToString("N"));

    public CsvFileServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string content, string name = "in.csv")
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    // ── layouts ──────────────────────────────────────────────────────────

    [Fact]
    public void Four_column_files_carry_the_namespace()
    {
        var rows = CsvFileService.Import(Write("UI,Play,Play,Грати\nMenu,Quit,Quit,Вийти\n"));

        Assert.Equal(2, rows.Count);
        Assert.Equal("UI", rows[0].Namespace);
        Assert.Equal("Play", rows[0].Key);
        Assert.Equal("Play", rows[0].Source);
        Assert.Equal("Грати", rows[0].Target);
    }

    [Fact]
    public void Three_column_files_are_key_source_target()
    {
        var rows = CsvFileService.Import(Write("Play,Play,Грати\n"));

        Assert.Equal("", rows[0].Namespace ?? "");
        Assert.Equal("Play", rows[0].Key);
        Assert.Equal("Грати", rows[0].Target);
    }

    [Fact]
    public void Two_column_files_leave_the_target_empty_rather_than_guessing()
    {
        var rows = CsvFileService.Import(Write("Play,Play\n"));
        Assert.Equal("Play", rows[0].Source);
        Assert.Equal("", rows[0].Target);
    }

    [Fact]
    public void Rows_without_a_key_are_dropped()
        => Assert.Single(CsvFileService.Import(Write("Play,Play,Грати\n,,,\n")));

    // ── header detection ─────────────────────────────────────────────────

    [Theory]
    [InlineData("key,source,target\nPlay,Play,Грати\n")]
    [InlineData("namespace,key,source,target\nUI,Play,Play,Грати\n")]
    [InlineData("key,text\nPlay,Play\n")]
    public void A_real_header_row_is_skipped(string content)
        => Assert.Single(CsvFileService.Import(Write(content)));

    [Fact]
    public void A_data_row_whose_key_is_the_word_key_is_not_mistaken_for_a_header()
    {
        // Header detection needs TWO matching fields, or this legitimate row
        // (a game string whose key is literally "key") would vanish.
        var rows = CsvFileService.Import(Write("key,Клавіша,Клавіша\n"));
        Assert.Single(rows);
        Assert.Equal("key", rows[0].Key);
    }

    // ── anomalies the user must be told about ────────────────────────────

    [Fact]
    public void A_mixed_column_layout_is_reported()
    {
        CsvFileService.Import(Write("A,B,C\nD,E\nF,G,H,I\n"), out string? warning);

        Assert.NotNull(warning);
        Assert.Contains("mixed column layout", warning);
    }

    [Fact]
    public void Exactly_two_layouts_already_trigger_the_warning()
    {
        // The common real case is one stray row, i.e. exactly two shapes — a
        // threshold accidentally raised to "more than two" would silence it
        // (caught by the mutation audit).
        CsvFileService.Import(Write("A,B,C\nD,E,F\nG,H\n"), out string? warning);

        Assert.NotNull(warning);
        Assert.Contains("mixed column layout", warning);
    }

    [Fact]
    public void An_unterminated_quote_is_reported_instead_of_silently_merging_rows()
    {
        // CsvHelper swallows the rest of the file into one field without throwing;
        // with a single resulting shape the mixed-layout warning stays quiet, so
        // this file would import as one row with no explanation.
        CsvFileService.Import(Write("Play,\"Play,Грати\nQuit,Quit,Вийти\n"), out string? warning);

        Assert.NotNull(warning);
        Assert.Contains("quote", warning);
    }

    [Fact]
    public void A_clean_file_produces_no_warning()
    {
        CsvFileService.Import(Write("UI,Play,Play,Грати\nUI,Quit,Quit,Вийти\n"), out string? warning);
        Assert.Null(warning);
    }

    // ── encoding ─────────────────────────────────────────────────────────

    [Fact]
    public void A_utf8_bom_is_consumed_and_not_treated_as_data()
    {
        string path = Path.Combine(_dir, "bom.csv");
        File.WriteAllText(path, "UI,Play,Play,Грати\n", new UTF8Encoding(true));

        var rows = CsvFileService.Import(path, out string? warning);
        Assert.Equal("UI", rows[0].Namespace);      // not "﻿UI"
        Assert.Equal("Грати", rows[0].Target);
        Assert.Null(warning);
    }

    [Fact]
    public void A_utf16_file_is_read_through_its_bom()
    {
        string path = Path.Combine(_dir, "utf16.csv");
        File.WriteAllText(path, "UI,Play,Play,Грати\n", new UnicodeEncoding(false, true));

        var rows = CsvFileService.Import(path, out string? warning);
        Assert.Equal("Грати", rows[0].Target);
        Assert.Null(warning);
    }

    [Fact]
    public void A_non_utf8_file_is_read_byte_preserving_and_reported()
    {
        // Previously decoded as UTF-8 with U+FFFD substitutions: the original
        // bytes were destroyed and nothing was said. Latin-1 keeps every byte, so
        // the user can re-save the file correctly.
        string path = Path.Combine(_dir, "cp1251.csv");
        byte[] cyrillicWin1251 = { 0xC3, 0xF0, 0xE0, 0xF2, 0xE8 };   // "Грати" in Windows-1251
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes("UI,Play,Play,"));
        bytes.AddRange(cyrillicWin1251);
        bytes.Add((byte)'\n');
        File.WriteAllBytes(path, bytes.ToArray());

        var rows = CsvFileService.Import(path, out string? warning);

        Assert.NotNull(warning);
        Assert.Contains("not UTF-8", warning);
        Assert.DoesNotContain('�', rows[0].Target!);            // nothing was destroyed
        Assert.Equal(cyrillicWin1251, Encoding.Latin1.GetBytes(rows[0].Target!));
    }

    // ── round trip ───────────────────────────────────────────────────────

    [Fact]
    public void Export_then_import_preserves_awkward_text_exactly()
    {
        var rows = new List<TranslationRow>
        {
            new() { Namespace = "UI", Key = "K1", Source = "Say \"hello\"", Target = "Скажи «привіт»" },
            new() { Namespace = "UI", Key = "K2", Source = "Line A\nLine B", Target = "Рядок А\nРядок Б" },
            new() { Namespace = "",   Key = "K3", Source = "a,b,c", Target = "а,б,в" },
            new() { Namespace = "UI", Key = "K4", Source = "emoji 😀 中文", Target = "емодзі 😀 中文" },
            new() { Namespace = "UI", Key = "K5", Source = "Trailing space ", Target = "Кінцевий пробіл " },
        };

        string path = Path.Combine(_dir, "out.csv");
        CsvFileService.Export(path, rows, includeNamespace: true);
        var back = CsvFileService.Import(path);

        Assert.Equal(rows.Count, back.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(rows[i].Namespace, back[i].Namespace);
            Assert.Equal(rows[i].Key, back[i].Key);
            Assert.Equal(rows[i].Source, back[i].Source);
            Assert.Equal(rows[i].Target, back[i].Target);
        }
    }

    [Fact]
    public void Export_without_the_namespace_column_still_round_trips_key_source_target()
    {
        var rows = new List<TranslationRow>
        {
            new() { Namespace = "UI", Key = "K1", Source = "Play", Target = "Грати" },
        };

        string path = Path.Combine(_dir, "three.csv");
        CsvFileService.Export(path, rows, includeNamespace: false);
        var back = CsvFileService.Import(path);

        Assert.Single(back);
        Assert.Equal("K1", back[0].Key);
        Assert.Equal("Грати", back[0].Target);
    }

    [Fact]
    public void Export_writes_utf8_so_other_tools_read_cyrillic_correctly()
    {
        string path = Path.Combine(_dir, "enc.csv");
        CsvFileService.Export(path, new List<TranslationRow>
        {
            new() { Key = "K", Source = "Play", Target = "Грати" },
        });

        Assert.Contains("Грати", File.ReadAllText(path, Encoding.UTF8));
    }
}
