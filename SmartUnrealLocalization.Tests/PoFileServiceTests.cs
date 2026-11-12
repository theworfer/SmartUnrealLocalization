using System.Text;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// gettext .po import/export. The format is shared with translators' tools
/// (Poedit, msgfmt), so escaping, contexts and plural forms must survive a round
/// trip untouched — a mangled .po is rejected by the whole toolchain.
/// </summary>
public class PoFileServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sul-po-tests", Guid.NewGuid().ToString("N"));

    public PoFileServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string content, string name = "in.po")
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private string RoundTrip(List<TranslationRow> rows, string? language = null, string? header = null)
    {
        string path = Path.Combine(_dir, "out.po");
        PoFileService.Export(path, rows, language, header);
        return path;
    }

    // ── basic import ─────────────────────────────────────────────────────

    [Fact]
    public void A_plain_entry_becomes_a_row()
    {
        var rows = PoFileService.Import(Write("msgid \"Play\"\nmsgstr \"Грати\"\n"));

        Assert.Single(rows);
        Assert.Equal("Play", rows[0].Source);
        Assert.Equal("Грати", rows[0].Target);
        Assert.Equal("Play", rows[0].Key);        // no context → the id is the key
    }

    [Fact]
    public void The_header_entry_is_metadata_not_a_row()
    {
        var rows = PoFileService.Import(
            Write("msgid \"\"\nmsgstr \"Language: uk\\n\"\n\nmsgid \"Play\"\nmsgstr \"Грати\"\n"),
            out _, out string? header);

        Assert.Single(rows);
        Assert.NotNull(header);
        Assert.Contains("Language: uk", header);
    }

    [Fact]
    public void Multi_line_continuations_are_joined()
    {
        var rows = PoFileService.Import(Write(
            "msgid \"\"\n\"Hello \"\n\"world\"\nmsgstr \"\"\n\"Привіт \"\n\"світ\"\n"));

        // msgid "" followed by continuations is a real entry, not the header.
        Assert.Single(rows);
        Assert.Equal("Hello world", rows[0].Source);
        Assert.Equal("Привіт світ", rows[0].Target);
    }

    [Fact]
    public void Obsolete_entries_are_ignored()
    {
        var rows = PoFileService.Import(Write(
            "#~ msgid \"Old\"\n#~ msgstr \"Старе\"\n\nmsgid \"Play\"\nmsgstr \"Грати\"\n"));

        Assert.Single(rows);
        Assert.Equal("Play", rows[0].Source);
    }

    [Fact]
    public void Duplicate_entries_keep_the_first_and_are_reported()
    {
        // msgfmt rejects a file with duplicate (context, id) pairs, so exporting
        // them again would produce a file translators cannot compile.
        var rows = PoFileService.Import(
            Write("msgid \"Play\"\nmsgstr \"Грати\"\n\nmsgid \"Play\"\nmsgstr \"Інше\"\n"),
            out string? warning);

        Assert.Single(rows);
        Assert.Equal("Грати", rows[0].Target);
        Assert.NotNull(warning);
    }

    // ── encoding ─────────────────────────────────────────────────────────

    [Fact]
    public void A_utf8_bom_does_not_break_the_first_keyword()
    {
        string path = Path.Combine(_dir, "bom.po");
        File.WriteAllText(path, "msgid \"Play\"\nmsgstr \"Грати\"\n", new UTF8Encoding(true));

        var rows = PoFileService.Import(path, out string? warning);
        Assert.Single(rows);                    // the BOM did not hide "msgid"
        Assert.Equal("Грати", rows[0].Target);
        Assert.Null(warning);
    }

    [Fact]
    public void Crlf_line_endings_are_handled()
    {
        var rows = PoFileService.Import(Write("msgid \"Play\"\r\nmsgstr \"Грати\"\r\n", "crlf.po"));
        Assert.Equal("Грати", rows[0].Target);
    }

    [Fact]
    public void A_non_utf8_file_is_reported_instead_of_being_silently_mangled()
    {
        string path = Path.Combine(_dir, "cp1251.po");
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes("msgid \"Play\"\nmsgstr \""));
        bytes.AddRange(new byte[] { 0xC3, 0xF0, 0xE0, 0xF2, 0xE8 });   // Windows-1251
        bytes.AddRange(Encoding.ASCII.GetBytes("\"\n"));
        File.WriteAllBytes(path, bytes.ToArray());

        PoFileService.Import(path, out string? warning);

        Assert.NotNull(warning);
        Assert.Contains("not UTF-8", warning);
    }

    // ── namespace-aware context ──────────────────────────────────────────

    [Fact]
    public void A_namespace_aware_context_is_split_into_namespace_and_key()
    {
        var rows = PoFileService.Import(Write(
            "msgid \"\"\nmsgstr \"X-Generator: SmartUnrealLocalization\\n\"\n\n" +
            "msgctxt \"UI,Play\"\nmsgid \"Play\"\nmsgstr \"Грати\"\n"));

        Assert.Equal("UI", rows[0].Namespace);
        Assert.Equal("Play", rows[0].Key);
    }

    [Fact]
    public void A_foreign_context_with_a_comma_is_left_whole()
    {
        // Without our generator marker the comma is part of someone else's
        // context string — splitting it would invent a namespace.
        var rows = PoFileService.Import(Write("msgctxt \"a,b\"\nmsgid \"Play\"\nmsgstr \"Грати\"\n"));

        Assert.Equal("", rows[0].Namespace ?? "");
        Assert.Equal("a,b", rows[0].Key);
    }

    [Fact]
    public void A_foreign_file_WITH_a_header_still_keeps_comma_contexts_whole()
    {
        // Real .po files always carry a header. The namespace-aware split must
        // stay off unless the header names OUR generator (or Unreal's) — the
        // headerless test alone let a "split everything" regression through
        // (caught by the mutation audit).
        var rows = PoFileService.Import(Write(
            "msgid \"\"\nmsgstr \"Project-Id-Version: SomeOtherTool\\n\"\n\n" +
            "msgctxt \"a,b\"\nmsgid \"Play\"\nmsgstr \"Грати\"\n"));

        Assert.Equal("", rows[0].Namespace ?? "");
        Assert.Equal("a,b", rows[0].Key);
    }

    // ── escaping ─────────────────────────────────────────────────────────

    [Fact]
    public void Escape_sequences_are_decoded_on_import()
    {
        var rows = PoFileService.Import(Write(
            "msgid \"A\\nB\"\nmsgstr \"Він сказав \\\"так\\\"\\nі пішов\"\n"));

        Assert.Equal("A\nB", rows[0].Source);
        Assert.Equal("Він сказав \"так\"\nі пішов", rows[0].Target);
    }

    [Fact]
    public void Awkward_text_survives_an_export_import_round_trip()
    {
        var rows = new List<TranslationRow>
        {
            new() { Namespace = "UI", Key = "K1", Source = "Say \"hi\"", Target = "Скажи \"привіт\"" },
            new() { Namespace = "UI", Key = "K2", Source = "A\nB", Target = "А\nБ" },
            new() { Namespace = "UI", Key = "K3", Source = "Tab\there", Target = "Таб\tтут" },
            new() { Namespace = "UI", Key = "K4", Source = "Back\\slash", Target = "Зворотний\\слеш" },
            new() { Namespace = "",   Key = "K5", Source = "emoji 😀 中文", Target = "емодзі 😀 中文" },
            new() { Namespace = "UI", Key = "K6", Source = "Trailing ", Target = "Кінцевий " },
        };

        var back = PoFileService.Import(RoundTrip(rows));

        Assert.Equal(rows.Count, back.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(rows[i].Namespace, back[i].Namespace);
            Assert.Equal(rows[i].Key, back[i].Key);
            Assert.Equal(rows[i].Source, back[i].Source);
            Assert.Equal(rows[i].Target, back[i].Target);
        }
    }

    // ── header and plural fidelity ───────────────────────────────────────

    [Fact]
    public void The_source_files_header_is_preserved_on_export()
    {
        // Poedit and msgfmt rely on Plural-Forms; regenerating a default header
        // would silently change the plural rules of the whole file.
        const string original = "Project-Id-Version: MyGame\\n"
                              + "Plural-Forms: nplurals=3; plural=(n%10==1 && n%100!=11 ? 0 : 1);\\n";
        string path = RoundTrip(
            new List<TranslationRow> { new() { Key = "K", Source = "Play", Target = "Грати" } },
            language: "uk", header: original);

        string text = File.ReadAllText(path, Encoding.UTF8);
        Assert.Contains("Plural-Forms: nplurals=3", text);
        Assert.Contains("Project-Id-Version: MyGame", text);
    }

    [Fact]
    public void Plural_entries_import_their_first_form_as_the_target()
    {
        var rows = PoFileService.Import(Write(
            "msgid \"%d item\"\nmsgid_plural \"%d items\"\n" +
            "msgstr[0] \"%d предмет\"\nmsgstr[1] \"%d предмети\"\nmsgstr[2] \"%d предметів\"\n"),
            out string? warning);

        Assert.Single(rows);
        Assert.Equal("%d предмет", rows[0].Target);
        Assert.NotNull(warning);                  // the user is told plurals are present
    }

    [Fact]
    public void Plural_forms_survive_an_export_import_round_trip()
    {
        string source = "msgid \"\"\nmsgstr \"X-Generator: SmartUnrealLocalization\\n\"\n\n" +
                        "msgid \"%d item\"\nmsgid_plural \"%d items\"\n" +
                        "msgstr[0] \"%d предмет\"\nmsgstr[1] \"%d предмети\"\n";
        var rows = PoFileService.Import(Write(source), out _, out string? header);

        string text = File.ReadAllText(RoundTrip(rows, "uk", header), Encoding.UTF8);
        Assert.Contains("msgid_plural \"%d items\"", text);
        Assert.Contains("msgstr[0] \"%d предмет\"", text);
        Assert.Contains("msgstr[1] \"%d предмети\"", text);
    }

    [Fact]
    public void Translator_comments_and_flags_are_carried_through()
    {
        var rows = PoFileService.Import(Write(
            "#. Shown on the main menu button\n#, fuzzy\nmsgid \"Play\"\nmsgstr \"Грати\"\n"),
            out string? warning);

        Assert.NotNull(warning);                  // fuzzy entries are reported
        string text = File.ReadAllText(RoundTrip(rows), Encoding.UTF8);
        Assert.Contains("Shown on the main menu button", text);
        Assert.Contains("fuzzy", text);
    }

    [Fact]
    public void An_empty_target_exports_as_an_empty_msgstr_not_a_missing_one()
    {
        string text = File.ReadAllText(RoundTrip(new List<TranslationRow>
        {
            new() { Key = "K", Source = "Play", Target = "" },
        }), Encoding.UTF8);

        Assert.Contains("msgid \"Play\"", text);
        Assert.Contains("msgstr \"\"", text);
    }
}
