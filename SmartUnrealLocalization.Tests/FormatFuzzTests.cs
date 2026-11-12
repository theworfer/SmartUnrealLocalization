using System.Diagnostics;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using Xunit;
using Xunit.Abstractions;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// CSV and PO under generated input (audit 2026-11):
/// what the app writes it must read back unchanged, and nothing it is given may
/// hang it. Fidelity is the dangerous one — a silent change survives into a game build.
/// </summary>
public class FormatFuzzTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sul-fuzz-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _out;
    public FormatFuzzTests(ITestOutputHelper output) { _out = output; Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly string[] Atoms =
    {
        "Hello", "Привіт", "日本語", "a,b", ";", "\"", "\"\"", "\n", "\r\n", "\t", " ", "  ", "=SUM(A1)", "+1", "-x", "@me",
        "{0}", "%s", "<b>", "</b>", "\\", "\\n", "#", "msgid", "\u00A0", "\u200B", "😀", "é", "", "'", "…",
    };

    private static string Rand(Random rng, int max = 5)
    {
        int n = rng.Next(0, max);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < n; i++) sb.Append(Atoms[rng.Next(Atoms.Length)]);
        return sb.ToString();
    }

    private static List<TranslationRow> RandomRows(Random rng, int count) =>
        Enumerable.Range(0, count).Select(i => new TranslationRow
        {
            Namespace = rng.Next(3) == 0 ? "" : "NS" + rng.Next(3),
            Key = "K" + i + Rand(rng, 2).Replace("\n", "").Replace("\r", ""),
            Source = "S" + Rand(rng),          // non-empty: an empty source has no meaning in either format
            Target = Rand(rng),
        }).ToList();

    [Fact]
    public void Csv_round_trips_whatever_it_writes()
    {
        var rng = new Random(11);
        var failures = new List<string>();
        for (int trial = 0; trial < 150; trial++)
        {
            var rows = RandomRows(rng, rng.Next(1, 25));
            string path = Path.Combine(_dir, $"t{trial}.csv");
            CsvFileService.Export(path, rows, includeNamespace: true);
            var back = CsvFileService.Import(path);
            if (back.Count != rows.Count) { failures.Add($"trial {trial}: {rows.Count} rows written, {back.Count} read"); continue; }
            for (int i = 0; i < rows.Count; i++)
            {
                if (back[i].Key != rows[i].Key || back[i].Source != rows[i].Source || (back[i].Target ?? "") != (rows[i].Target ?? "")
                    || (back[i].Namespace ?? "") != (rows[i].Namespace ?? ""))
                    failures.Add($"trial {trial} row {i}: wrote [{Show(rows[i])}] read [{Show(back[i])}]");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(15)));
    }

    [Fact]
    public void Po_round_trips_whatever_it_writes()
    {
        var rng = new Random(12);
        var failures = new List<string>();
        for (int trial = 0; trial < 150; trial++)
        {
            var rows = RandomRows(rng, rng.Next(1, 25));
            string path = Path.Combine(_dir, $"t{trial}.po");
            PoFileService.Export(path, rows);
            var back = PoFileService.Import(path);
            if (back.Count != rows.Count) { failures.Add($"trial {trial}: {rows.Count} rows written, {back.Count} read"); continue; }
            for (int i = 0; i < rows.Count; i++)
            {
                if (back[i].Key != rows[i].Key || back[i].Source != rows[i].Source || (back[i].Target ?? "") != (rows[i].Target ?? "")
                    || (back[i].Namespace ?? "") != (rows[i].Namespace ?? ""))
                    failures.Add($"trial {trial} row {i}: wrote [{Show(rows[i])}] read [{Show(back[i])}]");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(15)));
    }

    private static readonly string[] CsvJunk = { "\"", ",", "\n", "\r\n", "\"\"", "Key", "Source", "Target", "x", "\t", "\uFEFF", ";", "\r" };
    private static readonly string[] PoJunk = { "msgid \"", "msgstr \"", "msgctxt \"", "msgid_plural \"", "msgstr[0] \"", "msgstr[9] \"", "\"", "\\", "\\n", "\n", "#, fuzzy\n", "#~ ", "# c\n", "x", "\"\"\n", "\r\n" };

    [Theory]
    [InlineData(".csv")]
    [InlineData(".po")]
    public void Garbage_input_never_hangs(string ext)
    {
        var rng = new Random(ext.Length);
        var junk = ext == ".csv" ? CsvJunk : PoJunk;
        var kinds = new Dictionary<string, int>();
        for (int trial = 0; trial < 400; trial++)
        {
            var sb = new System.Text.StringBuilder();
            int n = rng.Next(0, 60);
            for (int i = 0; i < n; i++) sb.Append(junk[rng.Next(junk.Length)]);
            string path = Path.Combine(_dir, $"g{trial}{ext}");
            File.WriteAllText(path, sb.ToString());
            var sw = Stopwatch.StartNew();
            try
            {
                _ = ext == ".csv" ? CsvFileService.Import(path) : PoFileService.Import(path);
                kinds["ok"] = kinds.GetValueOrDefault("ok") + 1;
            }
            catch (Exception ex) { kinds[ex.GetType().Name] = kinds.GetValueOrDefault(ex.GetType().Name) + 1; }
            Assert.True(sw.ElapsedMilliseconds < 3000, $"trial {trial} took {sw.ElapsedMilliseconds} ms");
        }
        _out.WriteLine(string.Join(", ", kinds.Select(k => $"{k.Key}={k.Value}")));
    }

    private static string Show(TranslationRow r) => $"{r.Namespace}|{r.Key}|{r.Source}|{r.Target}".Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}
