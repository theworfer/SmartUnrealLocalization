using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using SmartUnrealLocalization.Services.Qc;
using SmartUnrealLocalization.Utils;
using SmartUnrealLocalization.Localization;

// ─────────────────────────────────────────────────────────────────────────────
//  MainWindowViewModel — QC tab (bottom panel, next to Problems and Logs)
//
//  QC used to share the Problems list. On a real file that meant a thousand-plus
//  entries mixed in with the validator's own findings, and the twenty rows that
//  actually needed a human were invisible. The QC tab presents the SAME data as
//  a handful of collapsed CATEGORY lines, each with a bulk action:
//
//      ▸ ⚠ PUNCTUATION-MISMATCH   412   [Fix 412]
//      ▸ ⓧ TAG-MISMATCH            18   needs review
//
//  The tab holds the AI review's suggestions. Every bulk Fix opens the same
//  before → after review as the validator's fixes; nothing is written unseen.

// ─────────────────────────────────────────────────────────────────────────────

namespace SmartUnrealLocalization.ViewModels
{
    public partial class MainWindowViewModel
    {
        /// <summary>The categories themselves — the model behind <see cref="QcRows"/>.</summary>
        public ObservableCollection<QcGroupItem> QcGroups { get; } = new();

        /// <summary>
        /// What the QC tab's ListBox shows: category headers interleaved with the
        /// items of the expanded ones. Flat on purpose — a single ListBox gives
        /// selection, multi-select copy, keyboard navigation and a place to
        /// reveal a row from the grid, none of which nested ItemsControls do.
        /// </summary>
        public ObservableCollection<object> QcRows { get; } = new();

        [ObservableProperty] private bool _qcGroupsEmpty = true;

        /// <summary>Suggestions carrying a replacement — the "Fix all" button's count.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasQcFixable))]
        private int _qcFixableCount;

        public bool HasQcFixable => QcFixableCount > 0;

        /// <summary>Flag-only findings: no machine fix exists, a human must look.</summary>
        [ObservableProperty] private int _qcReviewCount;

        // Same reasoning as MaxProblemItems: the counts stay exact, only the
        // materialised rows are capped so a pathological file can't stall the UI.
        private const int MaxQcItemsPerGroup = 500;

        /// <summary>
        /// Rebuild the category list from the pending proposals. Cheap enough to
        /// run on every verdict (one pass over the rows), and only ever called
        /// while the tab is actually on screen.
        /// </summary>
        public void RebuildQcGroups()
        {
            // Remember which categories the user had open so a rebuild after a
            // single Fix doesn't collapse the group they are working through.
            var expanded = QcGroups.Where(g => g.IsExpanded).Select(g => g.Kind).ToHashSet(StringComparer.Ordinal);

            var built = QcGrouping.Build(_allRows, MaxQcItemsPerGroup, expanded);

            QcFixableCount = built.Fixable;
            QcReviewCount = built.Review;

            QcGroups.Clear();
            foreach (var g in built.Groups) QcGroups.Add(g);

            QcGroupsEmpty = QcGroups.Count == 0;
            FlattenQcRows();
        }

        /// <summary>Project the categories into the flat list the ListBox binds to.</summary>
        private void FlattenQcRows()
        {
            QcRows.Clear();
            foreach (var row in QcGrouping.Flatten(QcGroups)) QcRows.Add(row);
        }

        /// <summary>Header click — collapse/expand one category.</summary>
        public void ToggleQcGroup(QcGroupItem group) => SetQcGroupExpanded(group, !group.IsExpanded);

        private void SetQcGroupExpanded(QcGroupItem group, bool expanded)
            => QcGrouping.SetExpanded(QcRows, group, expanded);

        /// <summary>
        /// Find the QC entry for a grid row, expanding its category if needed, so
        /// the panel can select it. Returns null when the row has no suggestion.
        /// </summary>
        public ProblemListItem? RevealQcEntry(TranslationRow row)
        {
            foreach (var g in QcGroups)
            {
                var hit = g.Items.FirstOrDefault(i => ReferenceEquals(i.Row, row));
                if (hit is null) continue;
                SetQcGroupExpanded(g, true);
                return hit;
            }
            return null;
        }

        /// <summary>
        /// Review, then apply, every applicable suggestion in ONE category — one
        /// undo step. Reads the rows, not the (capped) displayed list, so the
        /// review offers exactly the number written on the button.
        /// </summary>
        public async Task FixQcGroup(QcGroupItem group)
        {
            var items = _allRows
                .Where(r => r.QcProposal is { } p && QcGrouping.IsApplicable(r, p)
                            && QcGrouping.FirstKind(p) == group.Kind)
                .Select(r => (r, r.QcProposal!))
                .ToList();
            if (items.Count == 0) { StatusText = Loc.T("status.msg.qcNothingInCategory"); return; }

            int applied = await ReviewAndApplyQcAsync(items);
            if (applied > 0)
                AddLog(string.Format(Loc.Plural("log.qc.appliedInGroup", applied), applied, group.Label), LogLevel.Info);
        }

        /// <summary>
        /// Review, then apply, every pending suggestion that HAS a replacement,
        /// across all categories. Flag-only findings are untouched — they are the
        /// ones that need a human, and closing them here would defeat the review.
        /// </summary>
        [RelayCommand]
        private async Task QcFixAllMechanical()
        {
            var items = _allRows
                .Where(r => r.QcProposal is { } p && QcGrouping.IsApplicable(r, p))
                .Select(r => (r, r.QcProposal!))
                .ToList();
            if (items.Count == 0) { StatusText = Loc.T("status.msg.qcNoAutoFixes"); return; }
            await ReviewAndApplyQcAsync(items);
        }

        /// <summary>
        /// Every bulk QC fix goes through the same before → after review as the
        /// validator's fixes (2026-10-21): nothing is written that the user has
        /// not seen. Suggestions without a replacement, or on a row edited since
        /// the review, are not offered. Returns how many rows were written.
        /// </summary>
        public async Task<int> ReviewAndApplyQcAsync(IReadOnlyList<(TranslationRow Row, QcProposal Proposal)> items)
        {
            var byRow = new Dictionary<TranslationRow, QcProposal>(ReferenceEqualityComparer.Instance);
            var plan = new List<PlannedFix>();
            int stale = 0;
            foreach (var (row, p) in items)
            {
                if (string.Equals(p.Before, p.After, StringComparison.Ordinal)) continue;
                if (p.IsStaleFor(row)) { stale++; continue; }
                if (!byRow.TryAdd(row, p)) continue;
                // Before = the text that was REVIEWED. For a single-language file
                // that is Source; the Target cell the fix lands in is still empty,
                // and showing "" → fix hid what actually changed.
                plan.Add(new PlannedFix(row, p.Before, p.After,
                    p.Kinds.Select(TextUtils.KindToLabel).ToList()));
            }
            if (stale > 0)
                AddLog(string.Format(Loc.Plural("log.qc.skippedStale", stale), stale), LogLevel.Warning);
            if (plan.Count == 0) { StatusText = Loc.T("status.msg.qcNoAutoFixes"); return 0; }
            if (FixReviewRequested is null) return 0;

            var chosen = await FixReviewRequested(plan, 0);
            if (chosen is not { Count: > 0 }) return 0;
            return ApplyQcProposals(chosen.Where(f => byRow.ContainsKey(f.Row))
                                          .Select(f => (f.Row, byRow[f.Row])).ToList());
        }
    }
}