using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// Undo/redo. This is the user's safety net for every bulk operation in the app
/// (AI translate, Fix all, Replace all), so "one action = one Ctrl+Z" and a
/// correct redo stack matter more here than almost anywhere else.
/// </summary>
public class UndoRedoManagerTests
{
    private static TranslationRow Row(string target = "before")
        => new() { Namespace = "UI", Key = "K", Source = "Play", Target = target };

    // ── single edits ─────────────────────────────────────────────────────

    [Fact]
    public void Undo_restores_the_previous_value_and_redo_reapplies_it()
    {
        var mgr = new UndoRedoManager();
        var row = Row("before");
        row.Target = "after";
        mgr.AddAction(new CellEditAction(row, "before", "after"));

        mgr.Undo();
        Assert.Equal("before", row.Target);

        mgr.Redo();
        Assert.Equal("after", row.Target);
    }

    [Fact]
    public void An_empty_stack_reports_nothing_to_do_and_ignores_the_request()
    {
        var mgr = new UndoRedoManager();

        Assert.False(mgr.CanUndo);
        Assert.False(mgr.CanRedo);

        mgr.Undo();   // must not throw
        mgr.Redo();
    }

    [Fact]
    public void CanUndo_and_CanRedo_track_the_stacks()
    {
        var mgr = new UndoRedoManager();
        var row = Row();
        mgr.AddAction(new CellEditAction(row, "before", "after"));

        Assert.True(mgr.CanUndo);
        Assert.False(mgr.CanRedo);

        mgr.Undo();
        Assert.False(mgr.CanUndo);
        Assert.True(mgr.CanRedo);

        mgr.Redo();
        Assert.True(mgr.CanUndo);
        Assert.False(mgr.CanRedo);
    }

    [Fact]
    public void A_new_edit_after_an_undo_discards_the_redo_branch()
    {
        // Standard editor behaviour: once you type over an undone change, the
        // redone future is gone — keeping it would let Redo resurrect text the
        // user has already replaced.
        var mgr = new UndoRedoManager();
        var row = Row();
        mgr.AddAction(new CellEditAction(row, "before", "after"));
        mgr.Undo();

        mgr.AddAction(new CellEditAction(row, "before", "something else"));

        Assert.False(mgr.CanRedo);
    }

    [Fact]
    public void Undo_and_redo_walk_the_history_in_order()
    {
        var mgr = new UndoRedoManager();
        var row = Row("v0");
        foreach (var (from, to) in new[] { ("v0", "v1"), ("v1", "v2"), ("v2", "v3") })
        {
            row.Target = to;
            mgr.AddAction(new CellEditAction(row, from, to));
        }

        mgr.Undo(); Assert.Equal("v2", row.Target);
        mgr.Undo(); Assert.Equal("v1", row.Target);
        mgr.Undo(); Assert.Equal("v0", row.Target);
        mgr.Redo(); Assert.Equal("v1", row.Target);
        mgr.Redo(); Assert.Equal("v2", row.Target);
    }

    // ── batches ──────────────────────────────────────────────────────────

    [Fact]
    public void A_bulk_operation_is_one_undo_step()
    {
        // "Fix all" / an AI batch must not cost the user 500 presses of Ctrl+Z.
        var mgr = new UndoRedoManager();
        var rows = Enumerable.Range(0, 5).Select(i => Row("old" + i)).ToList();
        var edits = rows.Select((r, i) => new CellEditAction(r, "old" + i, "new" + i)).ToList();
        for (int i = 0; i < rows.Count; i++) rows[i].Target = "new" + i;

        mgr.AddAction(new BatchEditAction(edits));
        mgr.Undo();

        Assert.All(rows.Select((r, i) => (r, i)), t => Assert.Equal("old" + t.i, t.r.Target));
        Assert.False(mgr.CanUndo);          // one step, fully consumed

        mgr.Redo();
        Assert.All(rows.Select((r, i) => (r, i)), t => Assert.Equal("new" + t.i, t.r.Target));
    }

    [Fact]
    public void An_empty_batch_is_harmless()
    {
        var mgr = new UndoRedoManager();
        mgr.AddAction(new BatchEditAction(new List<CellEditAction>()));

        mgr.Undo();
        mgr.Redo();
        Assert.True(mgr.CanUndo);
    }

    // ── bounded history ──────────────────────────────────────────────────

    [Fact]
    public void The_history_is_capped_and_drops_the_oldest_entries_first()
    {
        // A per-batch AI translate on a big file pushes hundreds of entries; the
        // cap keeps the stack — and the rows it pins in memory — bounded.
        var mgr = new UndoRedoManager();
        var row = Row("v0");
        for (int i = 0; i < 150; i++)
            mgr.AddAction(new CellEditAction(row, "v" + i, "v" + (i + 1)));

        int undone = 0;
        while (mgr.CanUndo) { mgr.Undo(); undone++; }

        Assert.Equal(100, undone);
        Assert.Equal("v50", row.Target);   // the newest 100 survived, the oldest 50 were evicted
    }

    // ── replay guard ─────────────────────────────────────────────────────

    [Fact]
    public void IsReplaying_is_set_only_while_an_action_runs()
    {
        // Row listeners use this to skip capturing the replay as a fresh edit —
        // without it undo would push inverse actions and never empty the stack.
        var mgr = new UndoRedoManager();
        bool seenDuringUndo = false;
        var probe = new ProbeAction(() => seenDuringUndo = mgr.IsReplaying);
        mgr.AddAction(probe);

        Assert.False(mgr.IsReplaying);
        mgr.Undo();

        Assert.True(seenDuringUndo);
        Assert.False(mgr.IsReplaying);
    }

    [Fact]
    public void IsReplaying_is_cleared_even_when_an_action_throws()
    {
        var mgr = new UndoRedoManager();
        mgr.AddAction(new ProbeAction(() => throw new InvalidOperationException("boom")));

        Assert.Throws<InvalidOperationException>(() => mgr.Undo());
        Assert.False(mgr.IsReplaying);   // a stuck flag would silently disable edit capture
    }

    // ── notifications ────────────────────────────────────────────────────

    [Fact]
    public void State_changes_are_announced_so_the_toolbar_can_refresh()
    {
        var mgr = new UndoRedoManager();
        int notifications = 0;
        mgr.StateChanged += () => notifications++;

        mgr.AddAction(new CellEditAction(Row(), "a", "b"));
        mgr.Undo();
        mgr.Redo();

        Assert.Equal(3, notifications);
    }

    [Fact]
    public void A_no_op_undo_does_not_announce_a_change()
    {
        var mgr = new UndoRedoManager();
        int notifications = 0;
        mgr.StateChanged += () => notifications++;

        mgr.Undo();
        mgr.Redo();

        Assert.Equal(0, notifications);
    }

    // ── clear-problems action idempotency ────────────────────────────────

    [Fact]
    public void Clearing_problems_can_be_undone_once_however_many_times_it_is_called()
    {
        // This action lives in TWO undo paths at once (the toast's Undo button and
        // Ctrl+Z). Without the guard the second call re-adds the same Problem
        // objects, duplicating the status icon and the wavy underline.
        var row = Row();
        var problem = new Problem { Kind = "TagMismatch", Severity = ProblemSeverity.Error, Title = "test" };
        var action = new ClearProblemsAction(
            new List<(TranslationRow, List<Problem>)> { (row, new List<Problem> { problem }) });

        action.Undo();
        action.Undo();
        Assert.Single(row.Problems);

        action.Redo();
        action.Redo();
        Assert.Empty(row.Problems);
    }

    private sealed class ProbeAction : IUndoableAction
    {
        private readonly Action _onUndo;
        public ProbeAction(Action onUndo) => _onUndo = onUndo;
        public void Undo() => _onUndo();
        public void Redo() { }
    }
}
