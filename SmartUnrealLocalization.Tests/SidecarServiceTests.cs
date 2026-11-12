using Newtonsoft.Json;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// Review sidecars. They live in AppData keyed by a hash of the file's FULL
/// path, so two files that merely share a name never share review state. Both
/// services are singletons, hence the shared collection.
/// </summary>
[Collection("AppState")]
public class ReviewServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly ReviewService _svc = ReviewService.Instance;

    public ReviewServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sul-review-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        AppConfig.ConfigDirOverride = _dir;
    }

    public void Dispose()
    {
        _svc.LoadForFile("");
        AppConfig.ConfigDirOverride = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string File_(string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, "placeholder");   // the sidecar is keyed by path, not content
        return path;
    }

    private static TranslationRow Row(string ns, string key, string source = "Play")
        => new() { Namespace = ns, Key = key, Source = source, Target = "" };

    // ── round trip ───────────────────────────────────────────────────────

    [Fact]
    public void A_review_survives_closing_and_reopening_the_file()
    {
        string file = File_("a.locres");
        _svc.LoadForFile(file);

        var row = Row("UI", "Play");
        row.Review = ReviewStatus.Approved;
        _svc.UpdateRow(row);
        _svc.SaveNow();

        _svc.LoadForFile("");
        _svc.LoadForFile(file);
        var fresh = Row("UI", "Play");
        _svc.Hydrate(new[] { fresh });

        Assert.Equal(ReviewStatus.Approved, fresh.Review);
    }

    [Fact]
    public void The_edited_at_timestamp_is_persisted_with_the_review()
    {
        string file = File_("b.locres");
        _svc.LoadForFile(file);

        var stamp = new DateTime(2026, 5, 12, 10, 30, 0, DateTimeKind.Utc);
        var row = Row("UI", "Play");
        row.Review = ReviewStatus.Rejected;
        row.EditedAt = stamp;
        _svc.UpdateRow(row);
        _svc.SaveNow();

        _svc.LoadForFile("");
        _svc.LoadForFile(file);
        var fresh = Row("UI", "Play");
        _svc.Hydrate(new[] { fresh });

        Assert.Equal(ReviewStatus.Rejected, fresh.Review);
        Assert.Equal(stamp, fresh.EditedAt);
    }

    [Fact]
    public void Clearing_a_review_removes_its_entry_instead_of_storing_None()
    {
        string file = File_("c.locres");
        _svc.LoadForFile(file);

        var row = Row("UI", "Play");
        row.Review = ReviewStatus.Approved;
        _svc.UpdateRow(row);

        row.Review = ReviewStatus.None;
        row.EditedAt = null;
        _svc.UpdateRow(row);
        _svc.SaveNow();

        _svc.LoadForFile("");
        _svc.LoadForFile(file);
        var fresh = Row("UI", "Play");
        _svc.Hydrate(new[] { fresh });

        Assert.Equal(ReviewStatus.None, fresh.Review);
    }

    // ── per-file isolation ───────────────────────────────────────────────

    [Fact]
    public void Two_files_with_the_same_name_in_different_folders_never_share_reviews()
    {
        // The whole reason the sidecar name carries a hash of the full path: the
        // same Game.locres from two game versions must not cross-contaminate.
        string sub = Path.Combine(_dir, "v2");
        Directory.CreateDirectory(sub);
        string fileA = File_("Game.locres");
        string fileB = Path.Combine(sub, "Game.locres");
        File.WriteAllText(fileB, "placeholder");

        _svc.LoadForFile(fileA);
        var a = Row("UI", "Play");
        a.Review = ReviewStatus.Approved;
        _svc.UpdateRow(a);
        _svc.SaveNow();

        _svc.LoadForFile(fileB);
        var b = Row("UI", "Play");
        _svc.Hydrate(new[] { b });

        Assert.Equal(ReviewStatus.None, b.Review);
    }

    [Fact]
    public void A_row_absent_from_the_sidecar_keeps_its_default_state()
    {
        string file = File_("d.locres");
        _svc.LoadForFile(file);

        var known = Row("UI", "Play");
        known.Review = ReviewStatus.Approved;
        _svc.UpdateRow(known);
        _svc.SaveNow();

        var stranger = Row("UI", "Quit");
        _svc.Hydrate(new[] { stranger });

        Assert.Equal(ReviewStatus.None, stranger.Review);
        Assert.Null(stranger.EditedAt);
    }

    // ── legacy migration ─────────────────────────────────────────────────

    /// <summary>
    /// Plant sidecar content for a file and return its path. Detaching FIRST
    /// matters: <c>LoadForFile("")</c> flushes pending changes before letting go,
    /// so writing the file while it is still attached could be undone by that flush.
    /// </summary>
    private string PlantSidecar(string file, string namePrefix, ReviewSidecar content)
    {
        string sidecar = ReviewService.SidecarPathFor(file);
        Assert.StartsWith(namePrefix + "__", Path.GetFileName(sidecar));

        _svc.LoadForFile("");                             // detach (and flush) BEFORE writing
        File.WriteAllText(sidecar, JsonConvert.SerializeObject(content));
        return sidecar;
    }

    [Fact]
    public void Pre_locresio_flattened_keys_are_migrated_to_the_real_identity()
    {
        // Before LocresIO, locres rows were stored flattened: Ns="", Key="ns/key".
        // Losing this fallback would discard every review filed before the switch.
        string file = File_("legacy.locres");
        PlantSidecar(file, "legacy", new ReviewSidecar
        {
            Entries = new List<ReviewEntry>
            {
                new() { Ns = "", Key = "UI/Play", Status = ReviewStatus.Approved },
            },
        });

        _svc.LoadForFile(file);
        var row = Row("UI", "Play");
        _svc.Hydrate(new[] { row });

        Assert.Equal(ReviewStatus.Approved, row.Review);
    }

    [Fact]
    public void The_anonymous_namespaces_leading_slash_form_also_migrates()
    {
        string file = File_("legacy2.locres");
        PlantSidecar(file, "legacy2", new ReviewSidecar
        {
            Entries = new List<ReviewEntry>
            {
                new() { Ns = "", Key = "/Quit", Status = ReviewStatus.Rejected },
            },
        });

        _svc.LoadForFile(file);
        var row = Row("", "Quit");
        _svc.Hydrate(new[] { row });

        Assert.Equal(ReviewStatus.Rejected, row.Review);
    }

    [Fact]
    public void A_migrated_entry_is_rewritten_under_its_new_identity()
    {
        string file = File_("legacy3.locres");
        string sidecar = PlantSidecar(file, "legacy3", new ReviewSidecar
        {
            Entries = new List<ReviewEntry>
            {
                new() { Ns = "", Key = "UI/Play", Status = ReviewStatus.Approved },
            },
        });

        _svc.LoadForFile(file);
        _svc.Hydrate(new[] { Row("UI", "Play") });
        _svc.SaveNow();

        var stored = JsonConvert.DeserializeObject<ReviewSidecar>(File.ReadAllText(sidecar))!;
        var entry = Assert.Single(stored.Entries);
        Assert.Equal("UI", entry.Ns);
        Assert.Equal("Play", entry.Key);      // migration is persisted, not re-done every open
    }

    // ── resilience ───────────────────────────────────────────────────────

    [Fact]
    public void A_corrupt_sidecar_is_preserved_rather_than_overwritten_empty()
    {
        // Silently starting empty would let the next save wipe every review the
        // user had filed for this file.
        string file = File_("corrupt.locres");
        string sidecar = ReviewService.SidecarPathFor(file);

        _svc.LoadForFile("");                       // detach before corrupting
        File.WriteAllText(sidecar, "{ not json at all");

        _svc.LoadForFile(file);                     // must not throw

        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_dir, "reviews"), "*.corrupt-*"));
    }

    [Fact]
    public void Working_with_no_file_open_is_a_no_op_rather_than_a_crash()
    {
        _svc.LoadForFile("");
        var row = Row("UI", "Play");
        row.Review = ReviewStatus.Approved;

        _svc.UpdateRow(row);      // nothing to write to
        _svc.SaveNow();
        _svc.Hydrate(new[] { row });

        Assert.Equal(ReviewStatus.Approved, row.Review);   // untouched, not reset
    }
}

/// <summary>
/// Comment sidecars. These sit NEXT TO the file (translators pass them around
/// with the .po/.csv), and their key format changed when namespaces arrived —
/// so the v1 fallback has to keep working.
/// </summary>
[Collection("AppState")]
public class CommentServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly CommentService _svc = CommentService.Instance;

    public CommentServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sul-comment-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _svc.LoadForFile("");
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string File_(string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, "placeholder");
        return path;
    }

    private static TranslationRow Row(string ns, string key)
        => new() { Namespace = ns, Key = key, Source = "Play", Target = "" };

    // ── round trip ───────────────────────────────────────────────────────

    [Fact]
    public void A_comment_survives_closing_and_reopening_the_file()
    {
        string file = File_("a.po");
        _svc.LoadForFile(file);
        _svc.Set("UI", "Play", "Check the button width");
        _svc.SaveNow();

        _svc.LoadForFile("");
        _svc.LoadForFile(file);

        Assert.Equal("Check the button width", _svc.Get("UI", "Play"));
    }

    [Fact]
    public void The_sidecar_lives_next_to_the_file_so_it_travels_with_it()
    {
        string file = File_("b.po");
        _svc.LoadForFile(file);
        _svc.Set("UI", "Play", "note");
        _svc.SaveNow();

        Assert.True(File.Exists(file + ".comments.json"));
    }

    [Fact]
    public void The_same_key_in_two_namespaces_no_longer_collides()
    {
        // The v2 key format exists precisely for this: in v1 both rows shared one
        // comment, so a note about the store button appeared on the weapon.
        string file = File_("ns.locres");
        _svc.LoadForFile(file);
        _svc.Set("Weapon", "Charge", "verb — to charge up");
        _svc.Set("Store", "Charge", "noun — a fee");

        Assert.Equal("verb — to charge up", _svc.Get("Weapon", "Charge"));
        Assert.Equal("noun — a fee", _svc.Get("Store", "Charge"));
    }

    [Fact]
    public void A_file_without_namespaces_keeps_the_bare_key_shape()
    {
        // .po and .csv have no namespaces; their comments must stay addressable
        // by the plain key.
        string file = File_("plain.po");
        _svc.LoadForFile(file);
        _svc.Set("", "Play", "note");
        _svc.SaveNow();

        string json = File.ReadAllText(file + ".comments.json");
        Assert.Contains("\"Play\"", json);
    }

    [Fact]
    public void Removing_a_comment_deletes_it()
    {
        string file = File_("del.po");
        _svc.LoadForFile(file);
        _svc.Set("UI", "Play", "note");
        _svc.Remove("UI", "Play");

        Assert.Null(_svc.Get("UI", "Play"));
    }

    [Fact]
    public void Setting_an_empty_comment_clears_it()
    {
        string file = File_("empty.po");
        _svc.LoadForFile(file);
        _svc.Set("UI", "Play", "note");
        _svc.Set("UI", "Play", "");

        Assert.True(string.IsNullOrEmpty(_svc.Get("UI", "Play")));
    }

    // ── hydration + legacy migration ─────────────────────────────────────

    [Fact]
    public void Hydrate_puts_comments_back_onto_the_rows()
    {
        string file = File_("hyd.locres");
        _svc.LoadForFile(file);
        _svc.Set("UI", "Play", "note");
        _svc.SaveNow();

        _svc.LoadForFile("");
        _svc.LoadForFile(file);
        var row = Row("UI", "Play");
        _svc.Hydrate(new[] { row });

        Assert.Equal("note", row.Comment);
    }

    [Fact]
    public void Version_one_flattened_keys_are_migrated_on_hydrate()
    {
        // v1 stored the pre-LocresIO flattened key ("ns/key"). Without the
        // fallback every comment written before the switch would vanish.
        string file = File_("legacy.locres");
        File.WriteAllText(file + ".comments.json", JsonConvert.SerializeObject(new CommentSidecar
        {
            Version = 1,
            Comments = new Dictionary<string, CommentEntry>
            {
                ["UI/Play"] = new() { Text = "old note", UpdatedUtc = DateTime.UtcNow },
            },
        }));

        _svc.LoadForFile(file);
        var row = Row("UI", "Play");
        _svc.Hydrate(new[] { row });

        Assert.Equal("old note", row.Comment);
        Assert.Equal("old note", _svc.Get("UI", "Play"));   // reachable under the new key too
    }

    [Fact]
    public void The_anonymous_namespaces_legacy_form_also_migrates()
    {
        string file = File_("legacy2.locres");
        File.WriteAllText(file + ".comments.json", JsonConvert.SerializeObject(new CommentSidecar
        {
            Version = 1,
            Comments = new Dictionary<string, CommentEntry>
            {
                ["/Quit"] = new() { Text = "old note", UpdatedUtc = DateTime.UtcNow },
            },
        }));

        _svc.LoadForFile(file);
        var row = Row("", "Quit");
        _svc.Hydrate(new[] { row });

        Assert.Equal("old note", row.Comment);
    }

    [Fact]
    public void An_empty_stored_comment_is_not_hydrated_onto_a_row()
    {
        string file = File_("blank.locres");
        File.WriteAllText(file + ".comments.json", JsonConvert.SerializeObject(new CommentSidecar
        {
            Comments = new Dictionary<string, CommentEntry> { ["UIPlay"] = new() { Text = "" } },
        }));

        _svc.LoadForFile(file);
        var row = Row("UI", "Play");
        _svc.Hydrate(new[] { row });

        Assert.True(string.IsNullOrEmpty(row.Comment));
    }

    // ── mirroring + resilience ───────────────────────────────────────────

    [Fact]
    public void Comments_can_be_mirrored_onto_an_exported_file()
    {
        // Export writes a new file; the reviewer's notes must follow it or they
        // are stranded on the original.
        string source = File_("src.locres");
        string dest = File_("dest.po");

        _svc.LoadForFile(source);
        _svc.Set("UI", "Play", "note");
        _svc.SaveNow();
        _svc.MirrorTo(dest);

        _svc.LoadForFile(dest);
        Assert.Equal("note", _svc.Get("UI", "Play"));
    }

    [Fact]
    public void A_corrupt_sidecar_is_preserved_rather_than_overwritten_empty()
    {
        string file = File_("corrupt.locres");
        File.WriteAllText(file + ".comments.json", "{ not json at all");

        _svc.LoadForFile(file);   // must not throw

        Assert.NotEmpty(Directory.GetFiles(_dir, "*.corrupt-*"));
    }

    [Fact]
    public void Working_with_no_file_open_is_a_no_op_rather_than_a_crash()
    {
        _svc.LoadForFile("");
        _svc.Set("UI", "Play", "note");
        _svc.SaveNow();

        Assert.Null(_svc.Get("UI", "Play"));
    }
}
