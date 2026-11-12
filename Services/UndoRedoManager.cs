using System;
using System.Collections.Generic;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services
{
    public interface IUndoableAction
    {
        void Undo();
        void Redo();
    }

    public class CellEditAction : IUndoableAction
    {
        private readonly TranslationRow _row;
        private readonly string _oldValue;
        private readonly string _newValue;

        public CellEditAction(TranslationRow row, string oldValue, string newValue)
        {
            _row = row;
            _oldValue = oldValue;
            _newValue = newValue;
        }

        public void Undo() => _row.Target = _oldValue;
        public void Redo() => _row.Target = _newValue;
    }

    public class BatchEditAction : IUndoableAction
    {
        private readonly List<CellEditAction> _actions;

        public BatchEditAction(List<CellEditAction> actions)
        {
            _actions = actions;
        }

        public void Undo()
        {
            foreach (var action in _actions) action.Undo();
        }

        public void Redo()
        {
            foreach (var action in _actions) action.Redo();
        }
    }

    /// <summary>
    /// Undoable snapshot of a bulk "Clear status" operation (Etap 8 / Bug 4).
    /// The Clear chip commands strip Error / Warning / Info Problems off
    /// selected rows without touching <c>Target</c> — so they bypass the
    /// usual <c>CellEditAction</c> capture path on the Target setter, and
    /// without this action there is no way for the user to recover a mis-click
    /// short of waiting for the next light-validator pass (which won't
    /// regenerate heavy-only kinds like DuplicateSource / LengthExcessive
    /// until F8). Undo re-adds the exact Problem instances we removed;
    /// Redo strips them out again.
    ///
    /// <para>
    /// <b>Idempotency (Bug 8):</b> Undo and Redo are guarded by an internal
    /// flag because this action lives in TWO undo paths simultaneously — the
    /// toast's "Undo" button calls <see cref="Undo"/> directly, AND
    /// <see cref="UndoRedoManager.Undo"/> will pop the same action later on
    /// Ctrl+Z. Without the guard, the second Undo re-Add()s already-present
    /// <see cref="Problem"/> instances onto <c>row.Problems</c> (which is an
    /// ObservableCollection that does NOT dedupe), producing duplicates that
    /// then duplicate the status icon / wavy underline.
    /// </para>
    /// </summary>
    public class ClearProblemsAction : IUndoableAction
    {
        private readonly List<(TranslationRow Row, List<Problem> Removed)> _snapshot;
        // true  = Problems are currently REMOVED from rows (post-Clear state).
        // false = Problems are currently RESTORED to rows (post-Undo state).
        // Starts true because the Clear command does the removal before
        // pushing the action onto the undo stack.
        private bool _problemsCurrentlyRemoved = true;

        public ClearProblemsAction(List<(TranslationRow Row, List<Problem> Removed)> snapshot)
        {
            _snapshot = snapshot;
        }

        public void Undo()
        {
            if (!_problemsCurrentlyRemoved) return; // already undone — no-op.
            foreach (var (row, problems) in _snapshot)
                foreach (var p in problems) row.Problems.Add(p);
            _problemsCurrentlyRemoved = false;
        }

        public void Redo()
        {
            if (_problemsCurrentlyRemoved) return; // already redone — no-op.
            foreach (var (row, problems) in _snapshot)
                foreach (var p in problems) row.Problems.Remove(p);
            _problemsCurrentlyRemoved = true;
        }
    }

    /// <summary>
    /// Undoable bulk change of <see cref="TranslationRow.Review"/> status
    /// (Etap 11 — Mark approved / rejected / Clear review). Stores the old +
    /// new status per row so Undo/Redo flip the whole batch in one step. Each
    /// flip also re-persists through <see cref="ReviewService"/> (the Review
    /// setter itself doesn't write the sidecar). Re-applying the same status is
    /// a no-op at the setter level, so the toast-Undo + Ctrl+Z double-fire is
    /// harmless here (no idempotency flag needed, unlike ClearProblemsAction).
    /// </summary>
    public class ReviewChangeAction : IUndoableAction
    {
        private readonly List<(TranslationRow Row, ReviewStatus Old, ReviewStatus New)> _changes;

        public ReviewChangeAction(List<(TranslationRow Row, ReviewStatus Old, ReviewStatus New)> changes)
        {
            _changes = changes;
        }

        public void Undo()
        {
            foreach (var (row, old, _) in _changes)
            {
                row.Review = old;
                ReviewService.Instance.UpdateRow(row);
            }
        }

        public void Redo()
        {
            foreach (var (row, _, @new) in _changes)
            {
                row.Review = @new;
                ReviewService.Instance.UpdateRow(row);
            }
        }
    }

    /// <summary>
    /// Thread-safe undo/redo manager. The AI translation pipeline pushes BatchEditActions
    /// from background threads after Task.WhenAll, so stack mutations must be guarded.
    /// </summary>
    public class UndoRedoManager
    {
        /// <summary>Max retained undo entries. A bulk op (e.g. per-batch AI translate of a big
        /// file) can push hundreds of entries; capping keeps the stack — and the rows/strings it
        /// pins — bounded. Oldest entries are evicted first. Data safety is unaffected: the file
        /// is autosaved/backed up; this only limits how far back Ctrl+Z reaches.</summary>
        private const int MaxUndoEntries = 100;

        private readonly object _lock = new();
        private readonly Stack<IUndoableAction> _undoStack = new();
        private readonly Stack<IUndoableAction> _redoStack = new();

        public event Action? StateChanged;

        /// <summary>
        /// True while Undo()/Redo() is in flight. Listeners on row PropertyChanged use this
        /// to skip capturing the replay as a fresh user edit (otherwise undo would push
        /// inverse actions and the stack would never empty).
        /// </summary>
        public bool IsReplaying { get; private set; }

        public bool CanUndo
        {
            get { lock (_lock) return _undoStack.Count > 0; }
        }

        public bool CanRedo
        {
            get { lock (_lock) return _redoStack.Count > 0; }
        }

        public void AddAction(IUndoableAction action)
        {
            lock (_lock)
            {
                _undoStack.Push(action);
                if (_undoStack.Count > MaxUndoEntries)
                {
                    // Drop the OLDEST entries (bottom of the stack). ToArray() yields
                    // newest→oldest; keep the first MaxUndoEntries and rebuild.
                    var kept = _undoStack.ToArray();
                    _undoStack.Clear();
                    for (int i = MaxUndoEntries - 1; i >= 0; i--) _undoStack.Push(kept[i]);
                }
                _redoStack.Clear();
            }
            StateChanged?.Invoke();
        }

        public void Undo()
        {
            IUndoableAction? action = null;
            lock (_lock)
            {
                if (_undoStack.Count > 0)
                {
                    action = _undoStack.Pop();
                    _redoStack.Push(action);
                }
            }
            // Run the action outside the lock — Undo() may trigger UI / property changes
            // that re-enter the manager (e.g. CanUndo/CanRedo polls).
            if (action != null)
            {
                IsReplaying = true;
                try { action.Undo(); }
                finally { IsReplaying = false; }
                StateChanged?.Invoke();
            }
        }

        public void Redo()
        {
            IUndoableAction? action = null;
            lock (_lock)
            {
                if (_redoStack.Count > 0)
                {
                    action = _redoStack.Pop();
                    _undoStack.Push(action);
                }
            }
            if (action != null)
            {
                IsReplaying = true;
                try { action.Redo(); }
                finally { IsReplaying = false; }
                StateChanged?.Invoke();
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _undoStack.Clear();
                _redoStack.Clear();
            }
            StateChanged?.Invoke();
        }

        // ── Per-tab snapshot (Etap 13) ──────────────────────────────────────
        // Multi-file tabs each keep their own undo history. Rather than swap the
        // manager instance (which would break the single StateChanged wiring),
        // we export/restore the two stacks when the active tab changes.
        public sealed record Snapshot(IUndoableAction[] Undo, IUndoableAction[] Redo);

        /// <summary>Capture the current undo/redo stacks for stashing on a tab.</summary>
        public Snapshot ExportState()
        {
            lock (_lock)
                return new Snapshot(_undoStack.ToArray(), _redoStack.ToArray());
        }

        /// <summary>Replace the stacks with a previously exported snapshot (null = empty).</summary>
        public void LoadState(Snapshot? snapshot)
        {
            lock (_lock)
            {
                _undoStack.Clear();
                _redoStack.Clear();
                if (snapshot is not null)
                {
                    // ToArray() yields top→bottom; push bottom→top to rebuild order.
                    for (int i = snapshot.Undo.Length - 1; i >= 0; i--) _undoStack.Push(snapshot.Undo[i]);
                    for (int i = snapshot.Redo.Length - 1; i >= 0; i--) _redoStack.Push(snapshot.Redo[i]);
                }
            }
            StateChanged?.Invoke();
        }
    }
}
