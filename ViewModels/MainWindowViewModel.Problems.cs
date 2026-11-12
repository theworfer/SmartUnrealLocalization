using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartUnrealLocalization.Localization;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  MainWindowViewModel — Problems tab (Etap 10)
//
//  The bottom panel hosts two primary tabs: PROBLEMS and LOGS. This partial
//  owns the Problems tab: the aggregated per-problem list pulled from every
//  row's Problems collection, the severity segmented filter, the live counts
//  that label the toolbar chips, the "Fix all" bulk auto-fix command, and the
//  RevealRow plumbing the [Go] button uses to scroll the grid to a row.
//
//  Live updates: RefreshProblemsView() is called from the debounced
//  ScheduleStatusCountsRefresh (catches per-edit validator output) and from
//  ApplyFilter (catches F8 / Clear / Fix all / file open-close). The expensive
//  list rebuild only runs while the Problems tab is actually on screen.
// ─────────────────────────────────────────────────────────────────────────────

namespace SmartUnrealLocalization.ViewModels
{
    /// <summary>
    /// Bottom panel primary tab. QC is a peer of Problems, not a filter inside
    /// it: a review produces its own kind of entry (a proposal with a verdict
    /// and an undo path), and on a real file it outnumbers validator findings
    /// fifty to one — mixing the two hid every actual error.
    /// </summary>
    public enum BottomTab { Problems, Qc, Logs }

    /// <summary>Severity segmented filter inside the Problems tab.</summary>
    public enum ProblemFilter { All, Errors, Warnings, Info }

    public partial class MainWindowViewModel
    {
        // ── Bottom panel primary tab ─────────────────────────────────────────
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsProblemsTabActive))]
        [NotifyPropertyChangedFor(nameof(IsQcTabActive))]
        [NotifyPropertyChangedFor(nameof(IsLogsTabActive))]
        private BottomTab _activeBottomTab = BottomTab.Logs;

        public bool IsProblemsTabActive => ActiveBottomTab == BottomTab.Problems;
        public bool IsQcTabActive => ActiveBottomTab == BottomTab.Qc;
        public bool IsLogsTabActive => ActiveBottomTab == BottomTab.Logs;

        partial void OnActiveBottomTabChanged(BottomTab value)
        {
            if (value == BottomTab.Problems) RebuildProblemItems();
            else if (value == BottomTab.Qc) RebuildQcGroups();
        }

        [RelayCommand] private void ShowProblemsTab() => ActiveBottomTab = BottomTab.Problems;
        [RelayCommand] private void ShowQcTab() => ActiveBottomTab = BottomTab.Qc;
        [RelayCommand] private void ShowLogsTab() => ActiveBottomTab = BottomTab.Logs;

        // ── Severity filter inside the Problems tab ──────────────────────────
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsProblemFilterAll))]
        [NotifyPropertyChangedFor(nameof(IsProblemFilterErrors))]
        [NotifyPropertyChangedFor(nameof(IsProblemFilterWarnings))]
        [NotifyPropertyChangedFor(nameof(IsProblemFilterInfo))]
        private ProblemFilter _problemsSeverityFilter = ProblemFilter.All;
        partial void OnProblemsSeverityFilterChanged(ProblemFilter value) => RebuildProblemItems();

        public bool IsProblemFilterAll => ProblemsSeverityFilter == ProblemFilter.All;
        public bool IsProblemFilterErrors => ProblemsSeverityFilter == ProblemFilter.Errors;
        public bool IsProblemFilterWarnings => ProblemsSeverityFilter == ProblemFilter.Warnings;
        public bool IsProblemFilterInfo => ProblemsSeverityFilter == ProblemFilter.Info;

        /// <summary>The QC tab only appears once a review has produced something.</summary>
        public bool HasQcSuggestions => QcPendingCount > 0;

        /// <summary>Aggregated problem rows bound to the Problems-tab ListBox.</summary>
        public ObservableCollection<ProblemListItem> ProblemItems { get; } = new();

        /// <summary>Drives the "No problems" empty-state placeholder.</summary>
        [ObservableProperty] private bool _problemsListEmpty = true;

        // ── Per-PROBLEM counts (not per-row) — label the toolbar chips. ──────
        [ObservableProperty] private int _problemErrorCount;
        [ObservableProperty] private int _problemWarningCount;
        [ObservableProperty] private int _problemInfoCount;
        [ObservableProperty] private int _problemTotalCount;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(FixAllProblemsCommand))]
        private int _totalFixableProblemCount;

        // Hard cap so a pathological file (tens of thousands of problems) can't
        // stall the ListBox materialisation. The counts above stay exact.
        private const int MaxProblemItems = 5000;

        /// <summary>
        /// A validation switch changed (Settings or MCP): re-check every open tab,
        /// so a note switched off disappears everywhere and one switched on appears.
        /// </summary>
        public void RevalidateAllOpenFiles()
        {
            var v = Services.ValidationService.Instance;
            v.RevalidateAll(_allRows);
            foreach (var s in OpenSessions)
                if (!ReferenceEquals(s, ActiveSession) && s.IsLoaded && s.Rows.Count > 0)
                    v.RevalidateAll(s.Rows);
            ApplyFilter();
        }

        /// <summary>
        /// Recompute the per-problem severity counts (always — cheap) and, when
        /// the Problems tab is visible, rebuild the item list. Single O(problem)
        /// pass over <c>_allRows</c>.
        /// </summary>
        public void RefreshProblemsView()
        {
            int err = 0, warn = 0, info = 0, fixable = 0;
            foreach (var row in _allRows)
            {
                var problems = row.Problems;
                for (int i = 0; i < problems.Count; i++)
                {
                    switch (problems[i].Severity)
                    {
                        case ProblemSeverity.Error: err++; break;
                        case ProblemSeverity.Warning: warn++; break;
                        default: info++; break;
                    }
                    if (problems[i].Fix is not null) fixable++;
                }
            }
            ProblemErrorCount = err;
            ProblemWarningCount = warn;
            ProblemInfoCount = info;
            ProblemTotalCount = err + warn + info;
            TotalFixableProblemCount = fixable;

            if (ShowLogs && IsProblemsTabActive)
                RebuildProblemItems();
        }

        /// <summary>
        /// Rebuild <see cref="ProblemItems"/> from <c>_allRows</c>, filtered by
        /// <see cref="ProblemsSeverityFilter"/> and ordered Error → Warning →
        /// Info, stable within a bucket (row order preserved). Capped at
        /// <see cref="MaxProblemItems"/>.
        /// </summary>
        public void RebuildProblemItems()
        {
            ProblemItems.Clear();

            var matches = new List<ProblemListItem>();

            // Validator findings only. QC suggestions live in their own tab —
            // they outnumber these by orders of magnitude and used to bury them.
            foreach (var row in _allRows)
                foreach (var p in row.Problems)
                    if (MatchesProblemFilter(p.Severity))
                        matches.Add(new ProblemListItem(row, p));

            int added = 0;
            // OrderByDescending is a stable sort, so equal-severity entries keep
            // their _allRows append order — a deterministic top-to-bottom read.
            foreach (var item in matches.OrderByDescending(m => (int)m.Severity))
            {
                if (added++ >= MaxProblemItems) break;
                ProblemItems.Add(item);
            }
            ProblemsListEmpty = ProblemItems.Count == 0;
        }

        private bool MatchesProblemFilter(ProblemSeverity sev) => ProblemsSeverityFilter switch
        {
            ProblemFilter.Errors => sev == ProblemSeverity.Error,
            ProblemFilter.Warnings => sev == ProblemSeverity.Warning,
            ProblemFilter.Info => sev == ProblemSeverity.Info,
            _ => true,
        };

        // ── Severity filter chip commands (segmented toggle) ─────────────────
        [RelayCommand] private void SetProblemFilterAll() => ProblemsSeverityFilter = ProblemFilter.All;
        [RelayCommand] private void SetProblemFilterErrors() => ProblemsSeverityFilter = ProblemFilter.Errors;
        [RelayCommand] private void SetProblemFilterWarnings() => ProblemsSeverityFilter = ProblemFilter.Warnings;
        [RelayCommand] private void SetProblemFilterInfo() => ProblemsSeverityFilter = ProblemFilter.Info;

        // ── Fix all ──────────────────────────────────────────────────────────
        private bool CanFixAll => TotalFixableProblemCount > 0;

        /// <summary>
        /// Asks the view to show planned fixes and returns the ones the user kept;
        /// null = cancelled. Without a subscriber nothing is applied: a fix the
        /// user has not seen is never written (2026-10-21).
        /// </summary>
        public event Func<IReadOnlyList<PlannedFix>, int, Task<List<PlannedFix>?>>? FixReviewRequested;

        /// <summary>
        /// Plan the fixes for <paramref name="rows"/> on scratch copies, show every
        /// change before → after, apply only what the user kept — as ONE undo step.
        /// Fix all used to write straight into the file and show nothing.
        /// </summary>
        public async Task ReviewAndApplyFixesAsync(IEnumerable<TranslationRow> rows,
                                                   Func<TranslationRow, IEnumerable<Problem>>? only = null)
        {
            var plan = FixPlanner.Plan(rows, only);
            if (plan.Fixes.Count == 0)
            {
                StatusText = plan.Refused > 0
                    ? string.Format(Loc.T("fixReview.noneSafe"), plan.Refused)
                    : Loc.T("fixReview.nothing");
                return;
            }
            if (FixReviewRequested is null) return;
            var chosen = await FixReviewRequested(plan.Fixes, plan.Refused);
            if (chosen is { Count: > 0 }) ApplyPlannedFixes(chosen);
        }

        [RelayCommand(CanExecute = nameof(CanFixAll))]
        private Task FixAllProblems() => ReviewAndApplyFixesAsync(_allRows.ToList());

        /// <summary>One problem's fix (Problems tab [Fix], the cell's lightbulb) — through
        /// the same review, so it too is seen before it is written.</summary>
        public Task ReviewProblemFix(TranslationRow row, Problem problem)
            => ReviewAndApplyFixesAsync(new[] { row }, r => new[] { problem });

        /// <summary>
        /// Write reviewed fixes. A row whose Target changed since the review is
        /// skipped — the user approved a change to text that is no longer there.
        /// Returns how many rows were written.
        /// </summary>
        public int ApplyPlannedFixes(IReadOnlyList<PlannedFix> fixes)
        {
            var edits = new List<CellEditAction>();
            var rows = new List<TranslationRow>();
            int stale = 0;
            SuppressUndoCapture = true;
            try
            {
                foreach (var f in fixes)
                {
                    if ((f.Row.Target ?? "") != f.Before) { stale++; continue; }
                    f.Row.Target = f.After;
                    edits.Add(new CellEditAction(f.Row, f.Before, f.After));
                    rows.Add(f.Row);
                    ValidationService.Instance.RunForRow(f.Row);
                }
            }
            finally { SuppressUndoCapture = false; }

            if (stale > 0) AddLog(string.Format(Loc.T("fixReview.skippedStale"), stale), LogLevel.Warning);
            if (edits.Count == 0) return 0;

            // Fixes change Targets, which changes duplicate grouping (heavy).
            ValidationService.Instance.RunHeavy(_allRows);
            var batch = new BatchEditAction(edits);
            UndoManager.AddAction(batch);
            HasUnsavedChanges = true;
            ApplyFilter();
            AddLog(string.Format(Loc.Plural("log.problems.autoFixed", edits.Count), edits.Count));

            Action undo = () =>
            {
                SuppressUndoCapture = true;
                try { batch.Undo(); }
                finally { SuppressUndoCapture = false; }
                foreach (var r in rows) ValidationService.Instance.RunForRow(r);
                ValidationService.Instance.RunHeavy(_allRows);
                ApplyFilter();
            };
            ToastService.Instance.ShowSuccess(string.Format(Loc.Plural("log.problems.autoFixed", edits.Count), edits.Count), undo: undo);
            return edits.Count;
        }

        // ── Reveal a row from the Problems list ([Go] / double-click) ────────
        /// <summary>
        /// Raised when a row should be shown: the View focuses it and scrolls it
        /// into the grid. Only raised for a row the grid currently lists.
        /// </summary>
        public event Action<TranslationRow>? RevealRowRequested;

        /// <summary>Asks a Yes / No question (title, message); true = Yes.</summary>
        public event Func<string, string, Task<bool>>? YesNoRequested;

        public void RevealRow(TranslationRow row) => _ = RevealRowAsync(row);

        /// <summary>
        /// Jump to a row. When a filter hides it, the jump can only happen by
        /// changing what the grid shows — so it asks first ("the row is not shown;
        /// show all rows?"). Yes clears every filter and goes to the row; No leaves
        /// the grid exactly as it is. It used to clear the filters without asking.
        /// Returns true when the row was revealed.
        /// </summary>
        public async Task<bool> RevealRowAsync(TranslationRow row)
        {
            if (!_allRows.Contains(row)) return false;
            if (!Rows.Contains(row))
            {
                if (YesNoRequested is null) return false;
                if (!await YesNoRequested(Loc.T("reveal.hidden.title"), Loc.T("reveal.hidden.body"))) return false;
                if (!_allRows.Contains(row)) return false;          // the file changed while the question was open
                ShowAllRows();
                // Still hidden: it is a locres entry with an empty value, which the
                // grid leaves out unless "Show empty keys" is on.
                if (!Rows.Contains(row)) { ShowEmptyKeys = true; ApplyFilter(); }
                if (!Rows.Contains(row)) return false;
                StatusText = Loc.T("status.msg.filtersCleared");
            }
            RevealRowRequested?.Invoke(row);
            return true;
        }

        // ── The same text translated in different ways (DuplicateSource note) ──

        /// <summary>Every translated row with the same Source as <paramref name="row"/>, in file order.</summary>
        public IReadOnlyList<TranslationRow> SameSourceRows(TranslationRow row)
        {
            string source = row.Source ?? "";
            if (source.Length == 0) return Array.Empty<TranslationRow>();
            return _allRows.Where(r => !r.IsUntranslated && string.Equals(r.Source ?? "", source, StringComparison.Ordinal)).ToList();
        }

        /// <summary>
        /// Walk the translations of one text: go to the next (or previous) row with
        /// the same Source, wrapping round. Returns the row it went to, or null when
        /// there is nowhere to go or the user declined to show a hidden row.
        /// </summary>
        public async Task<TranslationRow?> GoToSameSourceRowAsync(TranslationRow row, bool forward)
        {
            var group = SameSourceRows(row);
            if (group.Count < 2) return null;
            int at = -1;
            for (int i = 0; i < group.Count; i++) if (ReferenceEquals(group[i], row)) { at = i; break; }
            int next = at < 0 ? 0 : (at + (forward ? 1 : group.Count - 1)) % group.Count;
            var target = group[next];
            if (!await RevealRowAsync(target)) return null;
            StatusText = string.Format(Loc.T("val.dup.position"), next + 1, group.Count);
            return target;
        }

        /// <summary>
        /// "Use this translation for the same text": give every other row with the
        /// same Source this row's Target. Like every fix it is shown before → after
        /// first and only the kept rows are written, as one undo step — each row has
        /// its own key and context, so the user decides row by row.
        /// Returns how many rows were written.
        /// </summary>
        public async Task<int> UseTranslationForSameSourceAsync(TranslationRow row)
        {
            string source = row.Source ?? "", value = row.Target ?? "";
            if (source.Length == 0 || value.Length == 0) return 0;

            string label = Loc.T("val.dup.use");
            var plan = _allRows
                .Where(r => !ReferenceEquals(r, row)
                            && string.Equals(r.Source ?? "", source, StringComparison.Ordinal)
                            && !string.Equals(r.Target ?? "", value, StringComparison.Ordinal))
                .Select(r => new PlannedFix(r, r.Target ?? "", value, new[] { label }))
                .ToList();
            if (plan.Count == 0) { StatusText = Loc.T("fixReview.nothing"); return 0; }
            if (FixReviewRequested is null) return 0;

            var chosen = await FixReviewRequested(plan, 0);
            return chosen is { Count: > 0 } ? ApplyPlannedFixes(chosen) : 0;
        }

        /// <summary>Clear every filter and rebuild the grid now (the text filter's own reset is debounced).</summary>
        private void ShowAllRows()
        {
            ResetAllFilters();
            ApplyFilter();
        }

    }
}
