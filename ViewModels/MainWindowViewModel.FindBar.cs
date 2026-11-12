using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartUnrealLocalization.Localization;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using SmartUnrealLocalization.Utils;

namespace SmartUnrealLocalization.ViewModels
{
    /// <summary>
    /// Etap 9 — VS-style inline FindBar state + commands. Replaces the
    /// previous modal <c>FindReplacePanel</c>; see <c>Controls/FindBar.axaml</c>
    /// for the view.
    ///
    /// <para>
    /// The scan model is a single flat list of <see cref="FindMatch"/>
    /// entries (row + character range) computed by <see cref="RecomputeFindMatches"/>
    /// whenever a query / option changes. Per-row visual highlights live on
    /// <see cref="TranslationRow.FindHits"/> (ObservableCollection so
    /// <c>TaggedTextBlock.FindHits</c> picks them up live) and
    /// <see cref="TranslationRow.CurrentFindHitIndex"/> marks the active match
    /// inside that row.
    /// </para>
    ///
    /// <para>
    /// Search column: when set to a column other than Target, visual highlight
    /// can't reach Key (rendered as plain TextBlock) but the counter still
    /// includes those matches and Find Next scrolls the row into view. Source
    /// is also rendered via TaggedTextBlock so it could be highlighted; for
    /// Etap 9 we ONLY highlight Target to keep one row state path and avoid
    /// per-cell FindHits forks. Future polish.
    /// </para>
    /// </summary>
    public partial class MainWindowViewModel
    {
        // ════════════════════════════════════════════════════════════════════
        //  Open / mode
        // ════════════════════════════════════════════════════════════════════

        /// <summary>True while the FindBar overlay is visible.</summary>
        [ObservableProperty]
        private bool _isFindBarOpen;

        /// <summary>
        /// True when the Replace row of the FindBar is expanded (Ctrl+H). The
        /// FindBar chevron toggles this. Replace inputs / buttons are visible
        /// only in this mode.
        /// </summary>
        [ObservableProperty]
        private bool _findBarReplaceMode;

        // ════════════════════════════════════════════════════════════════════
        //  Query inputs
        // ════════════════════════════════════════════════════════════════════

        [ObservableProperty] private string _findQuery = "";
        [ObservableProperty] private string _replaceQuery = "";

        [ObservableProperty] private bool _findMatchCase;
        [ObservableProperty] private bool _findWholeWord;
        [ObservableProperty] private bool _findUseRegex;

        /// <summary>Which column to scan. Drives both counter and Replace eligibility.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsReplaceEnabled))]
        [NotifyPropertyChangedFor(nameof(ReplaceDisabledTooltip))]
        private FindBarColumn _findColumn = FindBarColumn.All;

        // ════════════════════════════════════════════════════════════════════
        //  Computed view-bindings
        // ════════════════════════════════════════════════════════════════════

        /// <summary>True iff replacement makes sense for the active column.</summary>
        public bool IsReplaceEnabled => FindColumn == FindBarColumn.Target || FindColumn == FindBarColumn.All;

        /// <summary>
        /// Tooltip text for the (visually disabled) Replace inputs when the
        /// column is Key or Source. Empty when replace is enabled.
        /// </summary>
        public string ReplaceDisabledTooltip =>
            IsReplaceEnabled ? "" : Loc.T("find.replaceDisabledForKeySource");

        // Counter — "3 / 27" or "No results" or "" (no query yet).
        [ObservableProperty] private string _findCounterLabel = "";

        // Internal flat match list — exposed via FindMatchCount only.
        private readonly List<FindMatch> _findMatches = new();
        private int _currentMatchIdx = -1;

        // Rows that have at least one match for the current query — drives the
        // "Found rows" filter (MainWindowViewModel.Filters / ApplyFilter).
        private readonly HashSet<TranslationRow> _rowsWithFindMatch = new();

        /// <summary>True when the "Found rows" filter should actually restrict — i.e. the
        /// checkbox is on AND there's a live query to match against.</summary>
        public bool IsFoundFilterActive =>
            ShowOnlyFoundRows && IsFindBarOpen && !string.IsNullOrEmpty(FindQuery);

        public bool PassesFoundFilter(TranslationRow r) =>
            !IsFoundFilterActive || _rowsWithFindMatch.Contains(r);

        /// <summary>When the "Found rows" filter is engaged, refresh the grid. Called at
        /// every exit of <see cref="RecomputeFindMatches(bool)"/>.</summary>
        /// <remarks>The set is filled by the scan itself, row by row, and is NOT derived
        /// from <c>_findMatches</c>: that list stops at <c>MaxNavigableMatches</c>, so a
        /// query matching 5 001 rows used to filter down to 5 000 — the cap on how far
        /// Find Next can step silently redefined which rows match (audit 2026-09 §4).</remarks>
        private void RefreshFoundFilter()
        {
            if (ShowOnlyFoundRows) ApplyFilter();
        }

        // ════════════════════════════════════════════════════════════════════
        //  Property changed reactions
        // ════════════════════════════════════════════════════════════════════

        // Typing in the find box must NOT run a full O(N) scan of every row per
        // keystroke — on a 6k-row file that froze the UI for seconds (Windows offered
        // to kill the app). Coalesce to one scan ~250 ms after the last keystroke.
        // Option/column toggles are rare clicks, so they stay immediate.
        // The request carries "reset the cursor?". A reset (the query changed)
        // outranks a keep (a row was edited underneath): if both land in the same
        // window, the old cursor points into a result set built from a different
        // query and is meaningless — hence OR. See Services/Debouncer for why this
        // is not a hand-rolled timer any more.
        private Services.Debouncer<bool>? _findDebounce;

        private void ScheduleFindRecompute(bool resetCursor = true)
        {
            _findDebounce ??= new Services.Debouncer<bool>(
                TimeSpan.FromMilliseconds(250),
                merge: (pending, incoming) => pending || incoming,
                run: RecomputeFindMatches);
            _findDebounce.Request(resetCursor);
        }

        /// <summary>
        /// A row's Target changed while the find bar is open — from anywhere:
        /// a single cell edit, an AI batch, undo/redo, a pasted column, MCP.
        ///
        /// <para>Every one of those invalidates the recorded match offsets, and
        /// until this existed the bar kept showing a count and highlights for text
        /// that was no longer on screen. Debounced on purpose: a 10 000-row batch
        /// raises this ten thousand times, and a synchronous rescan per row would
        /// be O(N²) over the whole file — the exact freeze the debounce was
        /// introduced to stop. The cursor is KEPT, because the user did not move
        /// it; something else moved the text under it.</para>
        /// </summary>
        internal void NoteTargetEdited()
        {
            if (!IsFindBarOpen || string.IsNullOrEmpty(FindQuery)) return;
            // Bulk paths may write rows off the UI thread; DispatcherTimer is not
            // safe there.
            if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(NoteTargetEdited);
                return;
            }
            ScheduleFindRecompute(resetCursor: false);
        }

        partial void OnFindQueryChanged(string value) => ScheduleFindRecompute();
        partial void OnFindMatchCaseChanged(bool value) => RecomputeFindMatches();
        partial void OnFindWholeWordChanged(bool value) => RecomputeFindMatches();
        partial void OnFindUseRegexChanged(bool value) => RecomputeFindMatches();
        partial void OnFindColumnChanged(FindBarColumn value) => RecomputeFindMatches();

        partial void OnIsFindBarOpenChanged(bool value)
        {
            if (!value)
            {
                // Closing is also what a tab switch and a file close do; a rescan
                // still pending from before would otherwise run against rows it
                // was never asked about.
                _findDebounce?.Cancel();
                ClearFindVisuals();
            }
            else RecomputeFindMatches();
        }

        // ════════════════════════════════════════════════════════════════════
        //  Public surface — kicked by the View
        // ════════════════════════════════════════════════════════════════════

        /// <summary>Show FindBar, optionally pre-expanding Replace (Ctrl+H).</summary>
        public void OpenFindBar(bool replaceMode)
        {
            FindBarReplaceMode = replaceMode;
            IsFindBarOpen = true;
        }

        /// <summary>Close FindBar and wipe per-row highlights.</summary>
        [RelayCommand]
        public void CloseFindBar()
        {
            IsFindBarOpen = false;
        }

        [RelayCommand(CanExecute = nameof(HasMatches))]
        public void FindNext()
        {
            if (_findMatches.Count == 0) return;
            SetCurrentMatch((_currentMatchIdx + 1) % _findMatches.Count);
        }

        [RelayCommand(CanExecute = nameof(HasMatches))]
        public void FindPrev()
        {
            if (_findMatches.Count == 0) return;
            SetCurrentMatch((_currentMatchIdx - 1 + _findMatches.Count) % _findMatches.Count);
        }

        [RelayCommand(CanExecute = nameof(CanReplace))]
        public void ReplaceNext()
        {
            if (!IsReplaceEnabled || _findMatches.Count == 0) return;
            if (_currentMatchIdx < 0) { FindNext(); return; }

            var match = _findMatches[_currentMatchIdx];

            // Replace only ever rewrites Target. In All-column mode the current
            // match can be a Source/Key hit whose Start/Length index a DIFFERENT
            // string — splicing Target at those offsets would silently corrupt
            // it. Skip to the next match instead of replacing.
            if (match.Column != ColumnHit.Target) { FindNext(); return; }

            var row = match.Row;
            var oldT = row.Target ?? "";

            // The offsets come from the last full scan. An edit that landed after
            // that scan — an AI batch, an undo, a paste, an MCP write — can leave
            // them in bounds while pointing at different characters, and the
            // recompute those edits schedule is debounced, so there is a window
            // where this button is faster than the rescan. Confirm the hit is
            // still where it was before rewriting anything.
            var verify = BuildMatcher(FindQuery, FindMatchCase, FindWholeWord, FindUseRegex);
            bool intact;
            try
            {
                intact = verify is not null
                      && FindHighlighter.HitStillPresent(oldT, verify, match.Start, match.Length, MaxHitsPerRow);
            }
            catch (RegexMatchTimeoutException)
            {
                intact = false;
            }
            if (!intact)
            {
                // Stale offsets (Target changed under us) — rescan and stop.
                RecomputeFindMatches();
                return;
            }
            var newT = oldT.Substring(0, match.Start) + ReplaceQuery + oldT.Substring(match.Start + match.Length);
            if (oldT == newT) { FindNext(); return; }

            row.Target = newT; // standard setter → CellEditAction on undo stack
            HasUnsavedChanges = true;

            // Re-scan so the counter + highlights reflect the new text. Keep the
            // current logical match (resetCursor: false → no scroll), then step
            // the cursor PAST the inserted replacement. The "+ ReplaceQuery.Length"
            // is essential: without it, a replacement that still contains the
            // query (find "cat" → replace "cats") would re-select itself and the
            // next Replace click would replace it again ("cats" → "catss" …).
            RecomputeFindMatches(resetCursor: false);
            if (_findMatches.Count == 0) return;

            int caret = match.Start + ReplaceQuery.Length;
            int idx = _findMatches.FindIndex(m =>
                m.Column == ColumnHit.Target && ReferenceEquals(m.Row, row) && m.Start >= caret);
            if (idx < 0)
            {
                // No further hit in this row → first match in a later row
                // (matches are stored in _allRows order so a row's hits are
                // contiguous), else wrap to the top.
                int lastInRow = _findMatches.FindLastIndex(m => ReferenceEquals(m.Row, row));
                idx = (lastInRow >= 0 && lastInRow + 1 < _findMatches.Count) ? lastInRow + 1 : 0;
            }
            SetCurrentMatch(idx);
        }

        [RelayCommand(CanExecute = nameof(CanReplaceAll))]
        public void ReplaceAll()
        {
            if (!IsReplaceEnabled || _findMatches.Count == 0) return;

            // Plan against every Target as it is NOW — not against the offsets the
            // last scan recorded, which an edit since then can leave in bounds but
            // pointing at other characters, and which were capped for display
            // anyway. Planning writes nothing, so a regex that times out aborts the
            // whole Replace All before any row changes. See Utils/FindReplace.
            // Replace only ever rewrites Target; Source/Key hits are never touched.
            var matcher = BuildMatcher(FindQuery, FindMatchCase, FindWholeWord, FindUseRegex);
            if (matcher is null) { RecomputeFindMatches(); return; }

            List<FindReplace.TargetEdit> plan;
            try
            {
                plan = FindReplace.PlanReplaceAll(_allRows, matcher, ReplaceQuery);
            }
            catch (RegexMatchTimeoutException)
            {
                RecomputeFindMatches();   // surfaces the invalid-regex hint; nothing was written
                return;
            }

            var actions = new List<CellEditAction>(plan.Count);
            SuppressUndoCapture = true;
            try
            {
                foreach (var edit in plan)
                {
                    actions.Add(new CellEditAction(edit.Row, edit.OldText, edit.NewText));
                    edit.Row.Target = edit.NewText;
                    MarkTargetSnapshot(edit.Row, edit.NewText);
                }
            }
            finally { SuppressUndoCapture = false; }

            if (actions.Count > 0)
            {
                UndoManager.AddAction(new BatchEditAction(actions));
                HasUnsavedChanges = true;
                ToastService.Instance.ShowSuccess(
                    string.Format(Loc.Plural("find.toast.replacedAll", actions.Count), actions.Count));
                AddLog(string.Format(Loc.Plural("log.find.replaced", actions.Count), actions.Count), LogLevel.Info);
            }
            RecomputeFindMatches();
        }

        // RelayCommand CanExecute backers — referenced via [RelayCommand(CanExecute=...)].
        private bool HasMatches() => _findMatches.Count > 0;
        private bool CanReplace() => IsReplaceEnabled && _findMatches.Count > 0;
        private bool CanReplaceAll() => IsReplaceEnabled && _findMatches.Count > 0;

        // ════════════════════════════════════════════════════════════════════
        //  Match computation
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Rescan _allRows, populate per-row <see cref="TranslationRow.FindHits"/>
        /// for the active column, refresh the counter, and move the current
        /// match to the first hit. Called whenever any query/option/column
        /// changes, after every Replace, and on FindBar open.
        /// </summary>
        /// <summary>
        /// Ceilings that keep a broad query from turning the grid into a slideshow.
        /// A search for a common word on a 13 000-row file with both columns filled
        /// matches hundreds of thousands of times; navigating that many is not a
        /// thing anyone does, but building it froze the app.
        ///
        /// <para>Both are generous enough that a real search never notices them, and
        /// the counter says "N+" when one is hit rather than quietly lying.</para>
        /// </summary>
        private const int MaxNavigableMatches = 5000;

        /// <summary>Highlights per cell. Past this the whole cell is coloured anyway.</summary>
        private const int MaxHitsPerRow = 60;

        /// <summary>Every hit found, including ones past <see cref="MaxNavigableMatches"/>.</summary>
        private long _findTotalHits;
        private bool _findCapped;

        public void RecomputeFindMatches() => RecomputeFindMatches(resetCursor: true);

        /// <summary>
        /// Core rescan. When <paramref name="resetCursor"/> is true the cursor
        /// snaps to the first match and the row is scrolled into view (query /
        /// option changes, FindBar open). When false we try to keep the cursor on
        /// the same logical match and do NOT scroll — used after an external
        /// Target edit while the bar is open, so a single inline edit doesn't
        /// yank the grid to match #1.
        /// </summary>
        private void RecomputeFindMatches(bool resetCursor)
        {
            // A scan now satisfies any scan still pending: run once, not twice —
            // but keep a pending cursor reset rather than dropping it.
            if (_findDebounce is { } pending && pending.TryTakePending(out bool pendingReset))
                resetCursor |= pendingReset;

            // Remember the current match by identity so we can restore it when
            // the caller asked us not to reset the cursor.
            FindMatch? keep = (!resetCursor && _currentMatchIdx >= 0 && _currentMatchIdx < _findMatches.Count)
                ? _findMatches[_currentMatchIdx]
                : null;

            _findMatches.Clear();
            _rowsWithFindMatch.Clear();
            _currentMatchIdx = -1;

            if (!IsFindBarOpen || string.IsNullOrEmpty(FindQuery))
            {
                ClearFindVisuals();
                FindCounterLabel = "";
                RefreshFoundFilter();
                NotifyFindCommandsCanExecuteChanged();
                return;
            }

            // Build matcher once.
            var matcher = BuildMatcher(FindQuery, FindMatchCase, FindWholeWord, FindUseRegex);
            if (matcher is null)
            {
                // Invalid regex → no matches, hint via counter.
                ClearFindVisuals();
                FindCounterLabel = Loc.T("find.invalidRegex");
                RefreshFoundFilter();
                NotifyFindCommandsCanExecuteChanged();
                return;
            }

            bool wantTarget = FindColumn == FindBarColumn.Target || FindColumn == FindBarColumn.All;
            bool wantSource = FindColumn == FindBarColumn.Source || FindColumn == FindBarColumn.All;
            bool wantKey = FindColumn == FindBarColumn.Key || FindColumn == FindBarColumn.All;

            // Total hits, including the ones past MaxNavigableMatches — so the
            // counter tells the truth about how much the query actually matches.
            long totalHits = 0;
            bool capped = false;

            try
            {
                foreach (var row in _allRows)
                {
                    row.CurrentFindHitIndex = -1;
                    row.SourceCurrentFindHitIndex = -1;

                    // Collect into a plain list and assign ONCE per row. Adding to
                    // the row's collection item by item made the cell rebuild its
                    // whole visual tree per hit — see TranslationRow.FindHits.
                    var targetHits = wantTarget
                        ? FindHighlighter.Collect(row.Target, matcher, MaxHitsPerRow, ref totalHits)
                        : FindHighlighter.None;
                    var sourceHits = wantSource
                        ? FindHighlighter.Collect(row.Source, matcher, MaxHitsPerRow, ref totalHits)
                        : FindHighlighter.None;
                    // Key matches scroll but never highlight — collected only so
                    // Find Next can step onto them.
                    var keyHits = wantKey
                        ? FindHighlighter.Collect(row.Key, matcher, MaxHitsPerRow, ref totalHits)
                        : FindHighlighter.None;

                    // Assigning the shared empty list when there is nothing to show
                    // keeps the property change a no-op for the vast majority of
                    // rows: same reference in, same reference out, no rebuild.
                    row.FindHits = targetHits.Count > 0 ? targetHits : TranslationRow.NoHits;
                    row.SourceFindHits = sourceHits.Count > 0 ? sourceHits : TranslationRow.NoHits;
                    if (targetHits.Count > 0 || sourceHits.Count > 0 || keyHits.Count > 0)
                        _rowsWithFindMatch.Add(row);   // uncapped — see RefreshFoundFilter

                    Navigate(targetHits, ColumnHit.Target);
                    Navigate(sourceHits, ColumnHit.Source);
                    Navigate(keyHits, ColumnHit.Key);

                    void Navigate(IReadOnlyList<TextRange> hits, ColumnHit column)
                    {
                        for (int i = 0; i < hits.Count; i++)
                        {
                            if (_findMatches.Count >= MaxNavigableMatches) { capped = true; return; }
                            _findMatches.Add(new FindMatch(row, hits[i].Start, hits[i].Length, column));
                        }
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Catastrophic-backtracking pattern blew the per-op budget mid
                // scan. Abandon the partial result and surface the same hint as
                // an unparseable regex.
                ClearFindVisuals();
                _findMatches.Clear();
                _rowsWithFindMatch.Clear();
                _currentMatchIdx = -1;
                FindCounterLabel = Loc.T("find.invalidRegex");
                RefreshFoundFilter();
                NotifyFindCommandsCanExecuteChanged();
                return;
            }

            if (_findMatches.Count == 0)
            {
                FindCounterLabel = Loc.T("find.noMatches");
                RefreshFoundFilter();
                NotifyFindCommandsCanExecuteChanged();
                return;
            }

            _findTotalHits = totalHits;
            _findCapped = capped;

            if (resetCursor || keep is null)
            {
                SetCurrentMatch(0);
            }
            else
            {
                // Restore the same logical match if it still exists; otherwise
                // clamp to the first. Never scroll (bringIntoView: false).
                int idx = _findMatches.FindIndex(m =>
                    ReferenceEquals(m.Row, keep.Row) && m.Start == keep.Start &&
                    m.Length == keep.Length && m.Column == keep.Column);
                if (idx < 0) idx = 0;
                SetCurrentMatch(idx, bringIntoView: false);
            }
            RefreshFoundFilter();
            NotifyFindCommandsCanExecuteChanged();
        }

        private void SetCurrentMatch(int idx, bool bringIntoView = true)
        {
            // Clear previous row's current-hit markers (both columns).
            if (_currentMatchIdx >= 0 && _currentMatchIdx < _findMatches.Count)
            {
                var prev = _findMatches[_currentMatchIdx].Row;
                prev.CurrentFindHitIndex = -1;
                prev.SourceCurrentFindHitIndex = -1;
            }

            _currentMatchIdx = idx;
            if (idx < 0 || idx >= _findMatches.Count) return;

            var match = _findMatches[idx];
            var row = match.Row;

            // Mark the current hit in the matched column's own highlight collection so
            // it gets the stronger "current match" colour. Source and Target keep
            // separate collections; Key has none (scroll only, no highlight).
            row.CurrentFindHitIndex = -1;
            row.SourceCurrentFindHitIndex = -1;
            var hits = match.Column == ColumnHit.Source ? row.SourceFindHits : row.FindHits;
            int local = -1;
            for (int i = 0; i < hits.Count; i++)
            {
                if (hits[i].Start == match.Start && hits[i].Length == match.Length)
                {
                    local = i;
                    break;
                }
            }
            if (match.Column == ColumnHit.Source) row.SourceCurrentFindHitIndex = local;
            else row.CurrentFindHitIndex = local;

            // "3 / 5000+ (of 128 431)" when the query is too broad to navigate —
            // the user must be able to tell a capped result from a complete one.
            FindCounterLabel = _findCapped
                ? string.Format("{0} / {1}+ (of {2:N0})", idx + 1, _findMatches.Count, _findTotalHits)
                : string.Format("{0} / {1}", idx + 1, _findMatches.Count);
            NotifyFindCommandsCanExecuteChanged();

            // Ask the View to scroll the row into view + select it. View-side
            // wiring listens to FindBringIntoViewRequested. Suppressed when the
            // move was a side-effect of an external edit (bringIntoView: false)
            // so we don't hijack the grid scroll position on every keystroke.
            if (bringIntoView)
                FindBringIntoViewRequested?.Invoke(row);
        }

        /// <summary>Raised when Find Next/Prev moves to a row that should be visible.</summary>
        public event Action<TranslationRow>? FindBringIntoViewRequested;

        private void ClearFindVisuals()
        {
            // The `!= NoHits` guards matter: assigning the SAME reference is a
            // no-op for the binding, so closing the bar on a 13 000-row file only
            // notifies the handful of rows that actually had a highlight.
            foreach (var row in _allRows)
            {
                if (!ReferenceEquals(row.FindHits, TranslationRow.NoHits)) row.FindHits = TranslationRow.NoHits;
                if (row.CurrentFindHitIndex != -1) row.CurrentFindHitIndex = -1;
                if (!ReferenceEquals(row.SourceFindHits, TranslationRow.NoHits)) row.SourceFindHits = TranslationRow.NoHits;
                if (row.SourceCurrentFindHitIndex != -1) row.SourceCurrentFindHitIndex = -1;
            }
            _findTotalHits = 0;
            _findCapped = false;
        }

        private void NotifyFindCommandsCanExecuteChanged()
        {
            FindNextCommand.NotifyCanExecuteChanged();
            FindPrevCommand.NotifyCanExecuteChanged();
            ReplaceNextCommand.NotifyCanExecuteChanged();
            ReplaceAllCommand.NotifyCanExecuteChanged();
        }

        /// <summary>
        /// Build a <see cref="Regex"/> matcher for the query + options. Returns
        /// null if the user enabled regex mode and typed something unparseable
        /// (caller surfaces a "find.invalidRegex" hint).
        /// </summary>
        private static Regex? BuildMatcher(string query, bool matchCase, bool wholeWord, bool useRegex)
        {
            var opts = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
            opts |= RegexOptions.CultureInvariant;
            // Per-operation timeout: a user-supplied regex in .* mode can trigger
            // catastrophic backtracking (e.g. "(a+)+$") that would otherwise hang
            // the UI thread forever while we scan every row. On timeout the match
            // loop throws RegexMatchTimeoutException, which RecomputeFindMatches
            // catches and surfaces as the "invalid regex" hint.
            var timeout = TimeSpan.FromMilliseconds(500);
            try
            {
                if (useRegex)
                {
                    var pat = query;
                    if (wholeWord) pat = $@"(?<!\p{{L}})(?:{pat})(?!\p{{L}})";
                    return new Regex(pat, opts, timeout);
                }
                else
                {
                    var escaped = Regex.Escape(query);
                    if (wholeWord) escaped = $@"(?<!\p{{L}}){escaped}(?!\p{{L}})";
                    return new Regex(escaped, opts, timeout);
                }
            }
            catch (ArgumentException)
            {
                return null; // invalid regex
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  Types
        // ════════════════════════════════════════════════════════════════════

        private enum ColumnHit { Target, Source, Key }
        private sealed record FindMatch(TranslationRow Row, int Start, int Length, ColumnHit Column = ColumnHit.Target);
    }

    /// <summary>
    /// Which column the FindBar query targets. Drives both per-row visual
    /// highlights and Replace-button eligibility.
    /// </summary>
    public enum FindBarColumn
    {
        All,
        Key,
        Source,
        Target,
    }
}
