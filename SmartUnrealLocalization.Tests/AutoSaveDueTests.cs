using SmartUnrealLocalization.ViewModels;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// One auto-save backup per state (audit 2026-11). Auto-save keeps the unsaved
/// flag on purpose, so it used to write a fresh timestamped copy of the same text
/// on every tick until the user saved by hand.
/// </summary>
public class AutoSaveDueTests
{
    private static readonly object TabA = new(), TabB = new();

    [Fact]
    public void Nothing_unsaved_means_no_auto_save()
        => Assert.False(MainWindowViewModel.AutoSaveDue(false, TabA, 5, null, -1));

    [Fact]
    public void New_edits_since_the_last_backup_are_saved()
    {
        Assert.True(MainWindowViewModel.AutoSaveDue(true, TabA, 5, null, -1));
        Assert.True(MainWindowViewModel.AutoSaveDue(true, TabA, 6, TabA, 5));
    }

    [Fact]
    public void The_same_tab_at_the_same_revision_is_not_saved_again()
        => Assert.False(MainWindowViewModel.AutoSaveDue(true, TabA, 5, TabA, 5));

    [Fact]
    public void Another_tab_with_the_same_revision_number_is_still_saved()
        => Assert.True(MainWindowViewModel.AutoSaveDue(true, TabB, 5, TabA, 5));

    [Fact]
    public void The_auto_save_timer_asks_AutoSaveDue_and_remembers_what_it_wrote()
    {
        string code = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "ViewModels", "MainWindowViewModel.cs"));
        Assert.Contains("if (!AutoSaveDue(HasUnsavedChanges, session, revision, _autoSavedSession, _autoSavedRevision)) return;", code);
        Assert.Contains("_autoSavedRevision = revision;", code);
    }
}
