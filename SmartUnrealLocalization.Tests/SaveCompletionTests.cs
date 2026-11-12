using System.Text.RegularExpressions;
using SmartUnrealLocalization.ViewModels;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// A save that finishes late must not mark newer work as saved (audit 2026-09 §5).
/// A .locres save writes on a background thread; before 2026-10-19 its completion
/// cleared whatever flag was live by then — a newer edit of the same tab, or
/// a different tab entirely — and closing that tab stopped asking.
/// </summary>
public class SaveCompletionTests
{
    private static readonly object TabA = new(), TabB = new();

    [Fact]
    public void Nothing_changed_since_the_snapshot_the_tab_is_clean()
        => Assert.Equal(SaveCompletion.Clear.LiveFlag, SaveCompletion.Decide(TabA, TabA, 7, 7, -1));

    [Fact]
    public void An_edit_made_while_the_file_was_written_stays_unsaved()
        => Assert.Equal(SaveCompletion.Clear.Nothing, SaveCompletion.Decide(TabA, TabA, 7, 8, -1));

    [Fact]
    public void Switching_tabs_during_the_write_does_not_clean_the_new_tab()
    {
        // Saved A at 7; B is active now. B's flag is never touched — A's is
        // cleared only if A was not edited between the snapshot and the switch.
        Assert.Equal(SaveCompletion.Clear.SessionFlag, SaveCompletion.Decide(TabA, TabB, 7, 99, 7));
        Assert.Equal(SaveCompletion.Clear.Nothing, SaveCompletion.Decide(TabA, TabB, 7, 99, 8));
    }

    [Fact]
    public void A_save_from_no_tab_never_cleans_a_tab()
        => Assert.Equal(SaveCompletion.Clear.Nothing, SaveCompletion.Decide(null, TabB, 7, 7, 7));

    [Fact]
    public void The_background_save_reports_through_the_revision_check()
    {
        string root = TestPaths.RepoRoot();
        string fileOps = File.ReadAllText(Path.Combine(root, "ViewModels", "MainWindowViewModel.FileOps.cs"));
        var save = Regex.Match(fileOps, @"public bool SaveEditedData\(.*?\n        \}", RegexOptions.Singleline);
        Assert.True(save.Success, "SaveEditedData not found");
        Assert.Contains("MarkSavedIfUnchanged(snap.Session, snap.Revision)", save.Value);
        Assert.DoesNotContain("HasUnsavedChanges = false", save.Value);
        Assert.Contains("ActiveSession, _lastEditRevision", save.Value);   // captured WITH the rows

        string vm = File.ReadAllText(Path.Combine(root, "ViewModels", "MainWindowViewModel.cs"));
        Assert.Matches(@"TranslationRow\.Target\)\) return;\s*(//[^\n]*\n\s*)*_lastEditRevision = \+\+_editRevisionCounter;", vm);
        string tabs = File.ReadAllText(Path.Combine(root, "ViewModels", "MainWindowViewModel.Tabs.cs"));
        Assert.Contains("ActiveSession.LastEditRevision = _lastEditRevision;", tabs);
        Assert.Contains("_lastEditRevision = session.LastEditRevision;", tabs);
    }
}
