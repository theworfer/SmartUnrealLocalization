using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using SmartUnrealLocalization.Utils;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// An export that fails halfway leaves the previous file intact (audit 2026-11).
/// CSV and PO used to open the target directly, which truncates it at once — a
/// full disk or a crash mid-write replaced a good file with a cut-off one.
/// </summary>
public class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sul-atomic-" + Guid.NewGuid().ToString("N"));
    public AtomicFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void A_write_that_throws_leaves_the_old_file_and_no_temp_behind()
    {
        string path = Path.Combine(_dir, "game.csv");
        File.WriteAllText(path, "the good file");

        Assert.Throws<IOException>(() => AtomicFile.Write(path, s =>
        {
            s.Write(new byte[] { 1, 2, 3 });
            throw new IOException("disk full");
        }));

        Assert.Equal("the good file", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void A_csv_export_that_fails_midway_keeps_the_previous_csv()
    {
        string path = Path.Combine(_dir, "game.csv");
        CsvFileService.Export(path, new[] { new TranslationRow { Key = "K", Source = "S", Target = "T" } });
        string before = File.ReadAllText(path);

        IEnumerable<TranslationRow> Failing()
        {
            yield return new TranslationRow { Key = "K2", Source = "S2", Target = "T2" };
            throw new IOException("disk full");
        }
        Assert.Throws<IOException>(() => CsvFileService.Export(path, Failing()));

        Assert.Equal(before, File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void A_po_export_writes_through_the_atomic_path()
    {
        string path = Path.Combine(_dir, "game.po");
        PoFileService.Export(path, new[] { new TranslationRow { Key = "K", Source = "S", Target = "T" } });
        Assert.Contains("msgid \"S\"", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir));
    }
}
