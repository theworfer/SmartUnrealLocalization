using System;
using System.Collections.Generic;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using SmartUnrealLocalization.Localization;

// ─────────────────────────────────────────────────────────────────────────────
//  MainWindowViewModel — Review state (Etap 11)
//
//  Trinary review marker (Approved / Rejected / None) applied from the row
//  context menu over a multi-row scope (GetContextScope). Setting Review repaints
//  the left-edge stripe + status icon (TranslationRow.OnReviewChanged) and is
//  persisted to the AppData sidecar via ReviewService — independent of the
//  locres file's dirty state (review marks survive without "saving" the file).
// ─────────────────────────────────────────────────────────────────────────────

namespace SmartUnrealLocalization.ViewModels
{
    public partial class MainWindowViewModel
    {
        /// <summary>
        /// Set <paramref name="status"/> on every row in <paramref name="scope"/>
        /// that doesn't already have it, as one undoable batch + toast. Rows
        /// already at the target status are skipped, so the count reflects only
        /// real changes (and a no-op scope produces no toast). Review marks are
        /// persisted to the sidecar, NOT the locres file, so this does not touch
        /// HasUnsavedChanges.
        /// </summary>
        public void ApplyReview(IReadOnlyList<TranslationRow> scope, ReviewStatus status)
        {
            if (scope is null || scope.Count == 0) return;

            var changes = new List<(TranslationRow Row, ReviewStatus Old, ReviewStatus New)>();
            foreach (var r in scope)
            {
                if (r.Review == status) continue;
                changes.Add((r, r.Review, status));
                r.Review = status;
                ReviewService.Instance.UpdateRow(r);
            }
            if (changes.Count == 0) return;

            var action = new ReviewChangeAction(changes);
            UndoManager.AddAction(action);

            string msg = string.Format(Loc.Plural(status switch
            {
                ReviewStatus.Approved => "log.review.approved",
                ReviewStatus.Rejected => "log.review.rejected",
                _ => "log.review.cleared",
            }, changes.Count), changes.Count);
            AddLog(msg);

            // Targeted Undo (same rationale as ClearProblemsOfSeverity): revert
            // exactly THIS batch directly, not whatever is on top of the undo
            // stack, in case the user makes another edit before clicking Undo.
            Action undo = () => action.Undo();
            if (status == ReviewStatus.Approved)
                ToastService.Instance.ShowSuccess(msg, undo: undo);
            else
                ToastService.Instance.ShowInfo(msg, undo: undo);
        }
    }
}
