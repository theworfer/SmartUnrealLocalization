using Newtonsoft.Json;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// A file switch must never let a background save write one file's review or
/// comment data into another file, or delete it (audit 2026-09 §6).
///
/// <para>Both services save on a pool-thread timer. Switching files used to
/// change the path and the in-memory map in separate steps outside the save
/// lock, so a timer landing in between could:</para>
/// <list type="bullet">
/// <item>write A's reviews into B's sidecar (path already B, map still A);</item>
/// <item>write an empty review sidecar over B before B was read;</item>
/// <item>DELETE a comment sidecar — CommentService deletes the file when its
/// map is empty, and the map was emptied before the path moved.</item>
/// </list>
/// <para>Here a thread calls <c>SaveNow</c> in a loop — the timer at its worst
/// — while the UI side switches A ↔ B four hundred times, touching each file's
/// data on the way.</para>
/// </summary>
[Collection("AppState")]
public class SidecarRaceTests : IDisposable
{
    private readonly string _dir;

    public SidecarRaceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sul-race-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        AppConfig.ConfigDirOverride = _dir;
    }

    public void Dispose()
    {
        CommentService.Instance.LoadForFile("");
        ReviewService.Instance.LoadForFile("");
        AppConfig.ConfigDirOverride = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string NewFile(string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, "placeholder");
        return path;
    }

    private static void Storm(Action saveNow, Action<int> switchOnce)
    {
        using var stop = new CancellationTokenSource();
        var saver = Task.Run(() => { while (!stop.IsCancellationRequested) saveNow(); });
        try { for (int i = 0; i < 400; i++) switchOnce(i); }
        finally { stop.Cancel(); saver.Wait(); }
    }

    [Fact]
    public void Comments_never_cross_files_and_are_never_deleted_by_a_switch()
    {
        var svc = CommentService.Instance;
        string a = NewFile("a.locres"), b = NewFile("b.locres");
        svc.LoadForFile(a); svc.Set("UI", "Play", "comment of A"); svc.SaveNow();
        svc.LoadForFile(b); svc.Set("UI", "Quit", "comment of B"); svc.SaveNow();

        Storm(svc.SaveNow, i =>
        {
            if (i % 2 == 0) { svc.LoadForFile(a); svc.Set("UI", "Play", "comment of A"); }
            else { svc.LoadForFile(b); svc.Set("UI", "Quit", "comment of B"); }
        });
        svc.LoadForFile("");

        Assert.Equal(new[] { "comment of A" }, CommentsIn(a));
        Assert.Equal(new[] { "comment of B" }, CommentsIn(b));
    }

    [Fact]
    public void Reviews_never_cross_files_and_are_never_emptied_by_a_switch()
    {
        var svc = ReviewService.Instance;
        string a = NewFile("ra.locres"), b = NewFile("rb.locres");
        var rowA = new TranslationRow { Namespace = "UI", Key = "Play", Source = "Play", Review = ReviewStatus.Approved };
        var rowB = new TranslationRow { Namespace = "UI", Key = "Quit", Source = "Quit", Review = ReviewStatus.Rejected };
        svc.LoadForFile(a); svc.UpdateRow(rowA); svc.SaveNow();
        svc.LoadForFile(b); svc.UpdateRow(rowB); svc.SaveNow();

        Storm(svc.SaveNow, i =>
        {
            if (i % 2 == 0) { svc.LoadForFile(a); svc.UpdateRow(rowA); }
            else { svc.LoadForFile(b); svc.UpdateRow(rowB); }
        });
        svc.LoadForFile("");

        Assert.Equal(new[] { "Play" }, KeysReviewedIn(svc, a));
        Assert.Equal(new[] { "Quit" }, KeysReviewedIn(svc, b));
    }

    [Fact]
    public void An_unreadable_comment_sidecar_is_never_deleted_by_a_later_save()
    {
        // A sidecar that exists but cannot be read (locked by another program)
        // loads as "no comments"; the empty map must not then be "saved" as a
        // deletion of the real file.
        var svc = CommentService.Instance;
        string a = NewFile("locked.locres");
        svc.LoadForFile(a); svc.Set("UI", "Play", "keep me"); svc.SaveNow();
        svc.LoadForFile("");

        string sidecar = a + ".comments.json";
        using (new FileStream(sidecar, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            svc.LoadForFile(a);
            svc.SaveNow();
        }
        svc.LoadForFile("");
        Assert.Equal(new[] { "keep me" }, CommentsIn(a));
    }

    private static string[] CommentsIn(string file)
    {
        string sidecar = file + ".comments.json";
        Assert.True(File.Exists(sidecar), $"{Path.GetFileName(sidecar)} was deleted");
        var data = JsonConvert.DeserializeObject<CommentSidecar>(File.ReadAllText(sidecar))!;
        return data.Comments.Values.Select(c => c.Text).ToArray();
    }

    private static string[] KeysReviewedIn(ReviewService svc, string file)
    {
        svc.LoadForFile(file);
        var rows = new[] { new TranslationRow { Namespace = "UI", Key = "Play", Source = "Play" },
                           new TranslationRow { Namespace = "UI", Key = "Quit", Source = "Quit" } };
        svc.Hydrate(rows);
        svc.LoadForFile("");
        return rows.Where(r => r.Review != ReviewStatus.None).Select(r => r.Key!).ToArray();
    }
}
