using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartUnrealLocalization.Models;

// ─────────────────────────────────────────────────────────────────────────────
//  MainWindowViewModel — Filters & Sort (Etap 12)
//
//  Extends the existing status/selection filters (which live in the main file's
//  ApplyFilter) with three AND-restricting filter groups + an EditedAt sort mode:
//    • Review include filters (Approved / Rejected / Unreviewed tri-state).
//    • Recently-edited-this-session (+ optional "last N").
//    • Text-contains (Key / Source / Target).
//    • Sort chip: Default (column-header sort) / Recently edited newer / older.
//
//  All new state defaults to "show everything / no sort", so a fresh file behaves
//  exactly as before until the user opts in. ApplyFilter (main file) consumes the
//  predicates + sort helpers defined here.
// ─────────────────────────────────────────────────────────────────────────────

namespace SmartUnrealLocalization.ViewModels
{
    /// <summary>Sort mode chosen via the Sort chip. Non-Default suppresses column sort.</summary>
    public enum RowSortMode { Default, RecentlyEditedNewer, RecentlyEditedOlder }

    /// <summary>Which column the "Text contains" filter searches.</summary>
    public enum FilterTextColumn { Key, Source, Target }

    public partial class MainWindowViewModel
    {
        // ── Review include-filters (checked = include rows with that status) ──
        // Default all true → no restriction. Active when at least one is unchecked.
        [ObservableProperty] private bool _filterIncludeApproved = true;
        [ObservableProperty] private bool _filterIncludeRejected = true;
        [ObservableProperty] private bool _filterIncludeUnreviewed = true;
        partial void OnFilterIncludeApprovedChanged(bool value) => ApplyFilter();
        partial void OnFilterIncludeRejectedChanged(bool value) => ApplyFilter();
        partial void OnFilterIncludeUnreviewedChanged(bool value) => ApplyFilter();

        // ── Recently edited (this session) ──
        [ObservableProperty] private bool _showOnlyRecentlyEdited;
        [ObservableProperty] private int _recentEditLimit; // 0 = no "last N" cap
        partial void OnShowOnlyRecentlyEditedChanged(bool value) => ApplyFilter();
        partial void OnRecentEditLimitChanged(int value) => ApplyFilter();

        // ── Has reviewer comment ──
        // AND-restriction (like recently-edited): when on, only rows carrying a
        // sidecar comment survive. Default off → no restriction.
        [ObservableProperty] private bool _showOnlyCommented;
        partial void OnShowOnlyCommentedChanged(bool value) => ApplyFilter();

        // ── Merge results ──
        // After "Merge to new version" the grid tints new keys green and rows whose
        // ORIGINAL text changed violet. Only "new keys" is filterable: a merge now
        // REFUSES to carry a translation whose original changed, so those rows come
        // out with an empty Target and the ordinary Untranslated filter already
        // reaches them — a second chip for the same set was noise.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasMergeFlags))]
        private bool _showOnlyNewKeys;
        partial void OnShowOnlyNewKeysChanged(bool value) => ApplyFilter();

        /// <summary>True while the open file carries merge tints worth filtering by.</summary>
        public bool HasMergeFlags => _allRows.Any(r => r.IsNewKey || r.IsSourceChanged);

        /// <summary>Rows whose original text changed in the update — reported, not filtered.</summary>
        public int SourceChangedCount => _allRows.Count(r => r.IsSourceChanged);

        /// <summary>Refresh the merge chip after rows are swapped in.</summary>
        public void RefreshMergeFlagState()
        {
            OnPropertyChanged(nameof(HasMergeFlags));
            OnPropertyChanged(nameof(SourceChangedCount));
        }

        // ── Show empty keys (View menu, persisted preference) ──
        // Locres entries with an empty value are kept in the document for
        // integrity but hidden from the grid by default. Unlike the session
        // filters above, this is a persisted app preference: it survives file
        // close and restarts, and is NOT reset by ClearFilters/CloseCurrentFile.
        [ObservableProperty] private bool _showEmptyKeys = AppConfig.Instance.ShowEmptyKeys;
        partial void OnShowEmptyKeysChanged(bool value)
        {
            AppConfig.Instance.ShowEmptyKeys = value;
            AppConfig.Instance.Save();
            ApplyFilter();
        }

        // ── Found rows (search matches) ──
        // AND-restriction tied to the FindBar: when on AND a query is active, only
        // rows that match the current search survive. See PassesFoundFilter /
        // IsFoundFilterActive in the FindBar partial.
        [ObservableProperty] private bool _showOnlyFoundRows;
        partial void OnShowOnlyFoundRowsChanged(bool value) => ApplyFilter();

        // ── Text contains ──
        [ObservableProperty] private string _filterText = "";
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FilterTextColumnIndex))]
        private FilterTextColumn _filterTextColumn = FilterTextColumn.Target;
        partial void OnFilterTextChanged(string value) => ScheduleFilterTextApply();

        /// <summary>ComboBox SelectedIndex bridge for <see cref="FilterTextColumn"/>.</summary>
        public int FilterTextColumnIndex
        {
            get => (int)FilterTextColumn;
            set { if (value >= 0) FilterTextColumn = (FilterTextColumn)value; }
        }
        partial void OnFilterTextColumnChanged(FilterTextColumn value)
        {
            if (!string.IsNullOrEmpty(FilterText)) ApplyFilter();
        }

        // Debounce the text filter: a per-keystroke ApplyFilter is O(N) over every
        // row, which lags on large files. Coalesce to one pass ~250 ms after the
        // last keystroke. Checkbox filters stay instant (they call ApplyFilter
        // directly); only free-text typing is debounced.
        // One subscription for the life of the VM — see Services/Debouncer for
        // the hand-rolled version this replaced and why it ran one filter pass per
        // keystroke instead of one per pause.
        private Services.Debouncer? _filterTextDebounce;
        private void ScheduleFilterTextApply()
        {
            _filterTextDebounce ??= new Services.Debouncer(TimeSpan.FromMilliseconds(250), ApplyFilter);
            _filterTextDebounce.Request();
        }

        // ── Sort mode (Sort chip) ──
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSortByEdited))]
        [NotifyPropertyChangedFor(nameof(IsSortDefault))]
        [NotifyPropertyChangedFor(nameof(IsSortNewer))]
        [NotifyPropertyChangedFor(nameof(IsSortOlder))]
        private RowSortMode _sortMode = RowSortMode.Default;

        public bool IsSortByEdited => SortMode != RowSortMode.Default;
        public bool IsSortDefault => SortMode == RowSortMode.Default;
        public bool IsSortNewer => SortMode == RowSortMode.RecentlyEditedNewer;
        public bool IsSortOlder => SortMode == RowSortMode.RecentlyEditedOlder;

        partial void OnSortModeChanged(RowSortMode value)
        {
            // Selecting an EditedAt sort suppresses the column-header sort so the
            // two don't fight; clearing back to Default leaves column sort free
            // again (the column glyphs already reflect SortColumn being null).
            if (value != RowSortMode.Default) SortColumn = null;
            ApplyFilter();
        }

        [RelayCommand] private void SetSortDefault() => SortMode = RowSortMode.Default;
        [RelayCommand] private void SetSortNewer() => SortMode = RowSortMode.RecentlyEditedNewer;
        [RelayCommand] private void SetSortOlder() => SortMode = RowSortMode.RecentlyEditedOlder;

        // ── Predicates consumed by ApplyFilter ──

        /// <summary>True when at least one Review include-box is unchecked.</summary>
        public bool IsReviewFilterActive =>
            !(FilterIncludeApproved && FilterIncludeRejected && FilterIncludeUnreviewed);

        public bool PassesReviewFilter(TranslationRow r) => r.Review switch
        {
            ReviewStatus.Approved => FilterIncludeApproved,
            ReviewStatus.Rejected => FilterIncludeRejected,
            _ => FilterIncludeUnreviewed,
        };

        public bool PassesTextFilter(TranslationRow r)
        {
            if (string.IsNullOrEmpty(FilterText)) return true;
            string hay = FilterTextColumn switch
            {
                FilterTextColumn.Key => r.Key ?? "",
                FilterTextColumn.Source => r.Source ?? "",
                _ => r.Target ?? "",
            };
            return hay.Contains(FilterText, StringComparison.CurrentCultureIgnoreCase);
        }
    }
}
