using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using SmartUnrealLocalization.Services.Qc;
using SmartUnrealLocalization.Localization;

// ─────────────────────────────────────────────────────────────────────────────
//  MainWindowViewModel — Quality Control (QC)
//
//  QC never edits rows on its own: a run produces PROPOSALS (QcService, stored
//  in a per-file sidecar so a review survives a restart), the grid shows a badge
//  on rows that have one, and the user decides per row (Fix / Keep) or in bulk
//  (Fix All / Fix Selected — filter-bar buttons that exist only while the QC
//  panel is open). Applying is one undo step, so a wrong bulk decision is a
//  single Ctrl+Z away.
// ─────────────────────────────────────────────────────────────────────────────

namespace SmartUnrealLocalization.ViewModels
{
    public partial class MainWindowViewModel
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSidePanelVisible))]
        private bool _showQcPanel;

        partial void OnShowQcPanelChanged(bool value)
        {
            if (value) CloseOtherLeftPanels(nameof(ShowQcPanel));
            OnPropertyChanged(nameof(ShowQcActions));
        }

        /// <summary>Pending-proposal count, shown in the panel and the filter bar.</summary>
        [ObservableProperty] private int _qcPendingCount;

        /// <summary>Fix All / Fix Selected live in the filter bar ONLY in QC mode.</summary>
        public bool ShowQcActions => ShowQcPanel && QcPendingCount > 0;

        partial void OnQcPendingCountChanged(int value)
        {
            OnPropertyChanged(nameof(ShowQcActions));
            // The QC tab's visibility and its category list both key off this count.
            OnPropertyChanged(nameof(HasQcSuggestions));
            if (ShowLogs && IsQcTabActive) RebuildQcGroups();
        }

        /// <summary>Refresh badges + counters after a run or a verdict.</summary>
        public void RefreshQcState()
        {
            QcService.Instance.Hydrate(_allRows);
            QcPendingCount = QcService.Instance.PendingCount;
        }

        // ── running AI review vs. tab switches ───────────────────────────
        // The QC panel registers its AI run here; switching or closing the tab
        // cancels it, exactly like a tab-bound translation run. Otherwise the
        // review would keep spending money on rows of a file that is no longer
        // the one under review (its results are also refused by the file guard
        // in QcService, but stopping early is cheaper than discarding).

        private System.Threading.CancellationTokenSource? _qcAiCts;

        public void RegisterQcAiRun(System.Threading.CancellationTokenSource? cts) => _qcAiCts = cts;

        public void CancelQcAiRun()
        {
            if (_qcAiCts is { IsCancellationRequested: false } cts)
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { /* run just finished */ }
                AddLog(Loc.T("log.qc.aiStoppedTabClosed"), LogLevel.Warning);
            }
        }

        /// <summary>Apply one proposal to its row (shared by the badge popup and bulk actions).</summary>
        public void ApplyQcProposal(TranslationRow row, QcProposal proposal)
        {
            ApplyQcProposals(new[] { (row, proposal) });
        }

        /// <summary>
        /// Apply a set of proposals as ONE undo step. Stale proposals (the row was
        /// edited after the suggestion was made) are skipped — applying them would
        /// silently revert that edit.
        /// </summary>
        public int ApplyQcProposals(IEnumerable<(TranslationRow Row, QcProposal Proposal)> items)
        {
            var actions = new List<CellEditAction>();
            int skippedStale = 0;
            int skippedNoFix = 0;
            int residual = 0;

            SuppressUndoCapture = true;
            try
            {
                foreach (var (row, proposal) in items)
                {
                    // Flagged-only finding (no mechanical replacement): keep it
                    // PENDING. Marking it "fixed" would silently dismiss exactly the
                    // rows that need a human — the opposite of what Fix All means.
                    if (string.Equals(proposal.Before, proposal.After, StringComparison.Ordinal))
                    {
                        skippedNoFix++;
                        continue;
                    }
                    if (proposal.IsStaleFor(row)) { skippedStale++; continue; }
                    if (string.Equals(row.Target, proposal.After, StringComparison.Ordinal))
                    {
                        QcService.Instance.SetVerdict(proposal, QcVerdict.Fixed);
                        row.QcProposal = null;
                        continue;
                    }
                    actions.Add(new CellEditAction(row, row.Target ?? "", proposal.After));
                    string applied = proposal.After;
                    row.Target = applied;
                    MarkTargetSnapshot(row, applied);

                    // The fix may only cover part of what QC found (e.g. it restored
                    // the ellipsis but the row is still missing a <tag>). In that
                    // case keep the row flagged against its NEW text instead of
                    // closing it — a Critical finding must never be buried by an
                    // unrelated mechanical fix.
                    if (QcService.Instance.DemoteToResidual(proposal, applied))
                    {
                        residual++;
                        continue; // badge stays, proposal stays Pending
                    }
                    QcService.Instance.SetVerdict(proposal, QcVerdict.Fixed);
                    row.QcProposal = null;
                }
            }
            finally { SuppressUndoCapture = false; }

            if (actions.Count > 0)
            {
                UndoManager.AddAction(new BatchEditAction(actions));
                HasUnsavedChanges = true;
                ApplyFilter();
                AddLog(string.Format(Loc.Plural("log.qc.applied", actions.Count), actions.Count), LogLevel.Info);
            }
            if (skippedStale > 0)
                AddLog(string.Format(Loc.Plural("log.qc.skippedStale", skippedStale), skippedStale), LogLevel.Warning);
            if (skippedNoFix > 0)
                AddLog(string.Format(Loc.Plural("log.qc.noAutoFix", skippedNoFix), skippedNoFix), LogLevel.Info);
            if (residual > 0)
                AddLog(string.Format(Loc.Plural("log.qc.residual", residual), residual), LogLevel.Warning);

            RefreshQcState();
            return actions.Count;
        }

        /// <summary>User dismissed a suggestion — it won't be proposed again.</summary>
        public void KeepQcProposal(TranslationRow row, QcProposal proposal)
        {
            QcService.Instance.SetVerdict(proposal, QcVerdict.Kept);
            row.QcProposal = null;
            RefreshQcState();
        }

        [RelayCommand]
        private async Task QcFixAll()
        {
            var items = _allRows
                .Where(r => r.QcProposal is { Verdict: QcVerdict.Pending })
                .Select(r => (r, r.QcProposal!))
                .ToList();
            if (items.Count == 0) { StatusText = Loc.T("status.msg.qcNothingToFix"); return; }
            await ReviewAndApplyQcAsync(items);
        }

        [RelayCommand]
        private async Task QcFixSelected()
        {
            var items = SelectedRows
                .Where(r => r.QcProposal is { Verdict: QcVerdict.Pending })
                .Select(r => (r, r.QcProposal!))
                .ToList();
            if (items.Count == 0) { StatusText = Loc.T("status.msg.qcNoCheckedRows"); return; }
            await ReviewAndApplyQcAsync(items);
        }

        /// <summary>
        /// CSV report of the current suggestions — ONLY rows with a proposal, as
        /// requested. Written next to the app's export folder and revealed.
        /// </summary>
        public string ExportQcReport()
        {
            var rowsByKey = QcService.IndexRows(_allRows);
            var proposals = QcService.Instance.Pending;

            var sb = new StringBuilder();
            sb.AppendLine("Namespace,Key,Severity,Kinds,Source,Before,After,Reason");
            foreach (var p in proposals.OrderByDescending(p => p.Severity))
            {
                rowsByKey.TryGetValue((p.Namespace, p.Key), out var row);
                sb.AppendLine(string.Join(",",
                    Csv(p.Namespace), Csv(p.Key), Csv(p.Severity.ToString()),
                    Csv(string.Join(" ", p.Kinds)), Csv(row?.Source ?? ""),
                    Csv(p.Before), Csv(p.After), Csv(p.Reason)));
            }

            var dir = string.IsNullOrWhiteSpace(_cfg.ExportPath)
                ? Path.Combine(AppContext.BaseDirectory, "Export")
                : _cfg.ExportPath;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"qc_report_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv");
            Utils.AtomicFile.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); // BOM: Excel opens UTF-8 correctly
            AddLog(string.Format(Loc.Plural("log.qc.reportSaved", proposals.Count), Path.GetFileName(path), proposals.Count), LogLevel.Info);
            return path;

            static string Csv(string v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
        }
    }
}
