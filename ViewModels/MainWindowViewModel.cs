using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CancellationTokenSource = System.Threading.CancellationTokenSource;
using System.Timers;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartUnrealLocalization.Localization;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using SmartUnrealLocalization.Utils;

// ─────────────────────────────────────────────────────────────────────────────
//  MainWindowViewModel is split across several partial-class files by concern:
//
//    MainWindowViewModel.cs           — state, properties, lifecycle, filter,
//                                       selection, undo/redo, autosave, logging
//                                       (this file — the "control plane")
//    MainWindowViewModel.FileOps.cs   — Open / Import / Export / Save / Merge
//    MainWindowViewModel.Clipboard.cs — copy/paste/cut/clear over selected rows
//    MainWindowViewModel.Helpers.cs   — UnrealLocres.exe bootstrap + temp dirs
//
//  When adding new top-level features, prefer creating a new partial file over
//  growing this one. Cross-cutting plumbing (constructor, observable props,
//  event hooks) stays here.
// ─────────────────────────────────────────────────────────────────────────────

namespace SmartUnrealLocalization.ViewModels
{
    /// <summary>Severity filter inside the Logs tab — drives FilteredLogs (Etap 10).</summary>
    public enum LogLevelFilter { All, Info, Warning, Error }

    public partial class MainWindowViewModel : ViewModelBase
    {
        // ════════════════════════════════════════════════════════════════════
        //  OBSERVABLE PROPERTIES — bound to UI
        // ════════════════════════════════════════════════════════════════════
        [ObservableProperty] private string _statusText = "";
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsFilterHidingRows))]
        private int _rowCount;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FilterKilledAll))]
        [NotifyPropertyChangedFor(nameof(IsFilterHidingRows))]
        private int _filteredRowCount;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FilterKilledAll))]
        [NotifyPropertyChangedFor(nameof(IsFilterHidingRows))]
        private bool _hasData;

        /// <summary>True when an active filter hides part of the file. The status
        /// bar shows "Showing X of Y" only then — with nothing filtered the two
        /// numbers are equal and the total is already in "Untranslated: N / Y".</summary>
        public bool IsFilterHidingRows => HasData && FilteredRowCount < RowCount;

        /// <summary>True when a file is open but the active filter hides every row.
        /// Drives the inline "No rows match — Reset filter" state (spec §15).</summary>
        public bool FilterKilledAll => HasData && FilteredRowCount == 0;

        /// <summary>Clear all active filters in one call — wired to the empty-state Reset button.</summary>
        public void ResetAllFilters()
        {
            ShowOnlyUntranslated = false;
            ShowOnlyTranslated = false;
            ShowOnlyCellRecovered = false;
            ShowOnlyTagReplaced = false;
            ShowOnlyInfo = false;
            ShowOnlySelected = false;
            ShowOnlyUnselected = false;
            // Etap 12 restrict-filters
            FilterIncludeApproved = true;
            FilterIncludeRejected = true;
            FilterIncludeUnreviewed = true;
            ShowOnlyRecentlyEdited = false;
            RecentEditLimit = 0;
            ShowOnlyCommented = false;
            ShowOnlyNewKeys = false;
            ShowOnlyFoundRows = false;
            FilterText = "";
        }
        [ObservableProperty] private string _currentFileName = "";
        [ObservableProperty] private bool _hasUnsavedChanges;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CurrentFormatBadge))]
        [NotifyPropertyChangedFor(nameof(HasCurrentFormat))]
        private FileFormat _currentFormat = FileFormat.None;

        public string CurrentFormatBadge => CurrentFormat switch
        {
            FileFormat.Locres => "LOCRES",
            FileFormat.Csv => "CSV",
            FileFormat.Po => "PO",
            _ => ""
        };
        public bool HasCurrentFormat => CurrentFormat != FileFormat.None;

        [ObservableProperty] private bool _showOnlyUntranslated;
        [ObservableProperty] private bool _showOnlyTranslated;
        [ObservableProperty] private bool _showOnlyCellRecovered;
        [ObservableProperty] private bool _showOnlyTagReplaced;
        [ObservableProperty] private bool _showOnlyInfo;
        [ObservableProperty] private bool _showOnlySelected;
        [ObservableProperty] private bool _showOnlyUnselected;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasAnyStatusIcons))]
        private int _cellRecoveredCount;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasAnyStatusIcons))]
        private int _tagReplacedCount;
        /// <summary>
        /// Rows whose top severity is Info (no higher Error/Warning on the
        /// same row). Drives the third status-bar chip (blue ⓘ dot) that
        /// covers Whitespace / Punctuation / DuplicateSource /
        /// LengthExcessive. The chip stays visible at "0" so it sits in the
        /// same row as Errors / Warnings without jumping in/out as the user
        /// fixes the last Info issue.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasAnyStatusIcons))]
        private int _infoCount;
        /// <summary>
        /// Rows with ANY <see cref="TranslationRow.Problems"/> (covers all
        /// non-tag/token validators too: Whitespace, Punctuation,
        /// MixedScripts, PluralIncomplete, DuplicateSource, LengthExcessive).
        /// Drives <see cref="HasAnyStatusIcons"/> so the status-icon column
        /// shows the moment any validator fires, not only the legacy
        /// tag/token pair.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasAnyStatusIcons))]
        private int _anyProblemRowsCount;
        /// <summary>
        /// Rows carrying a review mark (Approved / Rejected). Counted so the
        /// status-icon column shows immediately on opening a file whose review
        /// sidecar has marks — previously only problem counts drove visibility,
        /// so the ✓/✗ column stayed hidden until the first edit.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasAnyStatusIcons))]
        private int _reviewMarkRowsCount;
        [ObservableProperty] private string _windowTitle = "Smart Unreal Localization";

        // ── SELECTION — one source of truth, see the SelectedRows block below ──
        /// <summary>
        /// Checked rows in the WHOLE file. This is what every operation acts on,
        /// so it is what the status bar reports.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSelection))]
        [NotifyPropertyChangedFor(nameof(HasSelectionOutsideFilter))]
        private int _selectedCount;
        /// <summary>
        /// Checked rows among the CURRENTLY VISIBLE ones. Drives only the
        /// tri-state Select-All header checkbox, which is filter-aware by design.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSelectionOutsideFilter))]
        private int _selectedVisibleCount;
        /// <summary>
        /// Tri-state Select-All flag.
        ///  • <c>true</c> — every visible row is checked
        ///  • <c>false</c> — none of the visible rows are checked
        ///  • <c>null</c> — partial: some checked, some not (indeterminate visual)
        /// </summary>
        [ObservableProperty] private bool? _isAllSelected = false;

        public bool HasAnyStatusIcons
            => CellRecoveredCount > 0 || TagReplacedCount > 0 || AnyProblemRowsCount > 0
               || ReviewMarkRowsCount > 0;

        [ObservableProperty] private bool _showLogs = false;

        /// <summary>Active severity filter in the Logs tab — drives the FilteredLogs view.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLogFilterAll))]
        [NotifyPropertyChangedFor(nameof(IsLogFilterInfo))]
        [NotifyPropertyChangedFor(nameof(IsLogFilterWarning))]
        [NotifyPropertyChangedFor(nameof(IsLogFilterError))]
        private LogLevelFilter _logFilter = LogLevelFilter.All;

        public bool IsLogFilterAll => LogFilter == LogLevelFilter.All;
        public bool IsLogFilterInfo => LogFilter == LogLevelFilter.Info;
        public bool IsLogFilterWarning => LogFilter == LogLevelFilter.Warning;
        public bool IsLogFilterError => LogFilter == LogLevelFilter.Error;

        /// <summary>
        /// Filtered view over <see cref="Logs"/> by <see cref="LogFilter"/>.
        /// "All" → full list; otherwise the matching single <see cref="LogLevel"/>.
        /// </summary>
        public ObservableCollection<LogMessage> FilteredLogs { get; } = new();

        // Side-panel state — mutually exclusive panels (AI Translate, Status, Settings,
        // Multi-translate, QC, Prompt history) share one docked host. Find & Replace is
        // an overlay and coexists with any of them.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSidePanelVisible))]
        private bool _showAiPanel;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSidePanelVisible))]
        private bool _showStatusPanel;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSidePanelVisible))]
        private bool _showSettingsPanel;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSidePanelVisible))]
        private bool _showMultiTranslatePanel;
        partial void OnShowMultiTranslatePanelChanged(bool value)
        {
            if (value) CloseOtherLeftPanels(nameof(ShowMultiTranslatePanel));
        }
        // ShowFindReplacePanel deleted in Etap 9. The inline FindBar uses
        // IsFindBarOpen (defined in the FindBar partial). It is no longer a
        // mutually-exclusive left panel — the bar overlays the grid top-right
        // and coexists with any side panel.
        /// <summary>Prompt history side panel (the prompts every AI run sent).</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSidePanelVisible))]
        private bool _showPromptHistoryPanel;

        /// <summary>
        /// When true, Source and Target cells render with <c>TextWrapping=Wrap</c>
        /// and the DataGrid row grows vertically to fit the text. Toggled from
        /// the filter-bar "Expand rows" chip. Persisted into
        /// <see cref="AppConfig.AutoExpandRows"/>.
        /// </summary>
        [ObservableProperty] private bool _autoExpandRows;

        partial void OnAutoExpandRowsChanged(bool value)
        {
            _cfg.AutoExpandRows = value;
            _cfg.Save();
        }

        public bool IsSidePanelVisible => ShowAiPanel || ShowStatusPanel || ShowSettingsPanel || ShowMultiTranslatePanel || ShowQcPanel || ShowPromptHistoryPanel;

        /// <summary>
        /// Single-panel rule: only ONE of the left-bar panels can be visible at a
        /// time. Opening any of {Prompt history, AI Translate, Status, Settings,
        /// Multi-translate, QC} closes the others: they share one host, and two open
        /// at once would leave the user unsure which panel is "active". Setting a property to false from inside this helper
        /// re-enters its own OnChanged with value=false, which is a no-op for all
        /// of them, so there is no infinite-loop risk.
        /// </summary>
        private void CloseOtherLeftPanels(string keep)
        {
            if (keep != nameof(ShowPromptHistoryPanel)) ShowPromptHistoryPanel = false;
            if (keep != nameof(ShowAiPanel)) ShowAiPanel = false;
            if (keep != nameof(ShowStatusPanel)) ShowStatusPanel = false;
            if (keep != nameof(ShowSettingsPanel)) ShowSettingsPanel = false;
            if (keep != nameof(ShowMultiTranslatePanel)) ShowMultiTranslatePanel = false;
            if (keep != nameof(ShowQcPanel)) ShowQcPanel = false;
            // ShowFindReplacePanel removed in Etap 9 (see field comment).
        }

        partial void OnShowAiPanelChanged(bool value)
        {
            if (value) CloseOtherLeftPanels(nameof(ShowAiPanel));
        }
        // OnShowStatusPanelChanged is defined further down (mutual exclusion + tick timer).
        partial void OnShowSettingsPanelChanged(bool value)
        {
            if (value) CloseOtherLeftPanels(nameof(ShowSettingsPanel));
        }
        partial void OnShowPromptHistoryPanelChanged(bool value)
        {
            if (value) CloseOtherLeftPanels(nameof(ShowPromptHistoryPanel));
        }
        // OnShowFindReplacePanelChanged removed in Etap 9 — FindBar overlay
        // is not mutually exclusive with the left side panels anymore.

        /// <summary>Percentage of translated rows (non-whitespace Target). 0 when no file open.</summary>
        public int TranslatedPercentage
        {
            get
            {
                if (_allRows.Count == 0) return 0;
                // Same "translated" definition as IsUntranslated / the Untranslated
                // filter + status counter — a whitespace-only Target is untranslated.
                int translated = _allRows.Count(r => !r.IsUntranslated);
                return (int)Math.Round(translated * 100.0 / _allRows.Count);
            }
        }

        /// <summary>HSL color string for the progress arc: red → orange → yellow → green by %.</summary>
        public IBrush TranslatedProgressBrush
        {
            get
            {
                int p = TranslatedPercentage;
                // hue 0 (red) → 120 (green) across [0, 100]
                double hue = p * 1.2;
                var color = HslToRgb(hue, 0.70, 0.45);
                return new SolidColorBrush(color);
            }
        }

        private static Color HslToRgb(double h, double s, double l)
        {
            double c = (1 - Math.Abs(2 * l - 1)) * s;
            double hp = h / 60.0;
            double x = c * (1 - Math.Abs(hp % 2 - 1));
            double r1 = 0, g1 = 0, b1 = 0;
            if (hp < 1) { r1 = c; g1 = x; }
            else if (hp < 2) { r1 = x; g1 = c; }
            else if (hp < 3) { g1 = c; b1 = x; }
            else if (hp < 4) { g1 = x; b1 = c; }
            else if (hp < 5) { r1 = x; b1 = c; }
            else { r1 = c; b1 = x; }
            double m = l - c / 2;
            return Color.FromRgb(
                (byte)Math.Round((r1 + m) * 255),
                (byte)Math.Round((g1 + m) * 255),
                (byte)Math.Round((b1 + m) * 255));
        }

        // Detected attributes shown in the project pill / status bar.
        // Populated in OpenFileAsync (Commit B); harmless null defaults for Commit A.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ProjectPillText))]
        private string? _detectedLanguageCode;

        // The leftover-language check needs the file's language by name; an
        // unknown or unparseable code leaves it empty, and that check stays silent.
        partial void OnDetectedLanguageCodeChanged(string? value)
        {
            string name = "";
            if (!string.IsNullOrWhiteSpace(value))
                try { name = System.Globalization.CultureInfo.GetCultureInfo(value).EnglishName.Split(' ')[0]; } catch { }
            Services.ValidationService.Instance.TargetLanguage = name;
        }
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ProjectPillText))]
        private string? _detectedLocresVersion;

        /// <summary>
        /// Composed text for the title-bar project pill. Hides segments whose backing
        /// value is null/empty. Returns "" when no file is loaded.
        /// </summary>
        public string ProjectPillText
        {
            get
            {
                if (string.IsNullOrEmpty(CurrentFileName)) return "";
                var sb = new System.Text.StringBuilder(CurrentFileName);
                if (!string.IsNullOrEmpty(DetectedLanguageCode))
                    sb.Append(" · ").Append(DetectedLanguageCode);
                if (!string.IsNullOrEmpty(DetectedLocresVersion))
                    sb.Append(" · ").Append(DetectedLocresVersion);
                return sb.ToString();
            }
        }
        [ObservableProperty] private GridLength _logPanelHeight = new(0);
        [ObservableProperty] private double _logPanelMinHeight = 0;

        private GridLength _savedLogPanelHeight = new(180);

        partial void OnShowLogsChanged(bool value)
        {
            if (value)
            {
                LogPanelHeight = _savedLogPanelHeight;
                LogPanelMinHeight = 60;
                // Panel just opened — populate the Problems tab now (the list
                // rebuild is otherwise skipped while the panel is hidden).
                RefreshProblemsView();
            }
            else
            {
                if (LogPanelHeight.Value > 60)
                {
                    _savedLogPanelHeight = LogPanelHeight;
                }
                LogPanelHeight = new GridLength(0);
                LogPanelMinHeight = 0;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  COLLECTIONS
        // ════════════════════════════════════════════════════════════════════
        /// <summary>Master list — all rows in memory.</summary>
        private readonly List<TranslationRow> _allRows = new();
        /// <summary>True when <see cref="Rows"/> mirrors <see cref="_allRows"/> in
        /// ORDER (no active filter/sort). The ApplyFilter fast path skips the
        /// rebuild only when this holds — otherwise switching a sort OFF would
        /// leave Rows in the previous sorted order (the "Default sort does nothing"
        /// bug).</summary>
        private bool _rowsArePristine = true;
        /// <summary>
        /// Filtered view bound to the DataGrid. Bulk-capable: a rebuild raises ONE
        /// Reset instead of 13 000 Adds, each of which the grid used to answer with
        /// a layout pass.
        /// </summary>
        public BulkObservableCollection<TranslationRow> Rows { get; } = new();
        /// <summary>Log messages for the output panel.</summary>
        public ObservableCollection<LogMessage> Logs { get; } = new();
        /// <summary>Recent file entries shown on the Welcome view (refreshed from AppConfig).</summary>
        public ObservableCollection<RecentFileEntry> RecentFiles { get; } = new();
        /// <summary>Full unfiltered row list — use this when operating on all data regardless of active filters.</summary>
        public IReadOnlyList<TranslationRow> AllRows => _allRows;

        // ════════════════════════════════════════════════════════════════════
        //  INTERNAL STATE
        // ════════════════════════════════════════════════════════════════════
        /// <summary>The exe's folder. AppContext, not Assembly.Location: the release is
        /// a single-file exe, where an assembly has no location of its own.</summary>
        private static readonly string ExeDir = AppContext.BaseDirectory;

        public string? CurrentLocresPath { get; set; }

        /// <summary>
        /// Parsed .locres skeleton of the active tab (LocresIO). Every locres
        /// save goes through <c>Document.SaveWith</c> — the document is
        /// immutable and never re-read from disk, so "cannot save back to
        /// locres" can no longer happen while the tab is open. Null for
        /// .po/.csv sessions.
        /// </summary>
        public Locres.IO.LocresDocument? CurrentLocresDocument { get; set; }

        public string? OriginalLocresName { get; set; }
        public string CsvFile { get; set; } = "";

        /// <summary>
        /// Path the user originally selected when opening the current file.
        /// This is what we push to <see cref="AppConfig.RecentFiles"/> —
        /// opening a recent should always re-resolve through this path.
        /// </summary>
        // Notifying: the QC panel (and anything else showing "which file is
        // active") refreshes off this when tabs switch or files open/close.
        private string? _originalFilePathValue;
        public string? OriginalFilePath
        {
            get => _originalFilePathValue;
            set
            {
                if (_originalFilePathValue == value) return;
                _originalFilePathValue = value;
                OnPropertyChanged(nameof(OriginalFilePath));
            }
        }

        /// <summary>
        /// Live handle to the in-flight AI translation, if any.
        /// Set by AITranslateWindow when a run starts; cleared when the run finishes.
        /// Any AITranslateWindow instance (including ones opened after the original was closed)
        /// can stop the translation through this handle.
        /// </summary>
        public AiTranslationHandle? ActiveAiTranslation { get; set; }

        public class AiTranslationHandle
        {
            public CancellationTokenSource Cts { get; }
            public Action HaltAction { get; }
            /// <summary>
            /// True for the single-file AI panel run, which reads/writes the ACTIVE
            /// tab's rows, undo stack and dirty flag — switching or closing the tab
            /// mid-run must cancel it (see Tabs). Multi-translate leaves this false:
            /// it writes standalone output files and survives tab changes.
            /// </summary>
            public bool IsTabBound { get; init; }

            /// <summary>
            /// Live progress snapshot for status surfaces (MCP get_status): rows
            /// done, errors, total, and an ETA in seconds (null while the rate is
            /// still unknown). Null when the producing flow has no row-level
            /// progress (e.g. multi-translate).
            /// </summary>
            public Func<(int Done, int Errors, int Total, double? EtaSeconds)>? ProgressProbe { get; init; }
            public AiTranslationHandle(CancellationTokenSource cts, Action haltAction)
            {
                Cts = cts;
                HaltAction = haltAction;
            }
            public void Stop()
            {
                HaltAction();
                try { Cts.Cancel(); } catch { }
            }
        }

        /// <summary>
        /// Composite (namespace, key) identities flagged as NEW by the last
        /// "Merge to new version" run — see <see cref="NewKeyId"/>.
        /// </summary>
        private HashSet<string> _newKeySet = new();

        /// <summary>
        /// Composite identities whose SOURCE TEXT changed in the last merge
        /// (SourceStringHash mismatch) — carried translations need a re-check.
        /// </summary>
        private HashSet<string> _sourceChangedKeySet = new();

        /// <summary>
        /// The file the two sets above describe. Without it they were applied to
        /// EVERY locres opened afterwards: the sets are keyed by (namespace, key)
        /// only, and a game's locres files share nearly all their keys, so opening
        /// any other file after a merge painted hundreds of its rows green
        /// ("new key") and violet ("source changed") when nothing of the sort was
        /// true — and violet reads as "re-check this translation".
        /// </summary>
        private string? _mergeFlagsForPath;

        /// <summary>
        /// Clear every piece of per-file identity metadata back to its "no file"
        /// baseline.
        ///
        /// <para><b>Call this FIRST from any path that builds a session.</b> The
        /// hazard it exists for is silent: a path that forgets to assign one of
        /// these keeps the PREVIOUS file’s value, which looks plausible — the
        /// project pill shows the last file’s language, a PO header from the tab
        /// you just left rides into a locres session and gets used by a later Save
        /// As. Nothing throws. "Merge to new version" hit exactly this, four
        /// fields at once, because it assembles a session by hand instead of going
        /// through the normal load.</para>
        ///
        /// <para>With the reset in place, forgetting a field yields a NEUTRAL value
        /// instead of the wrong one. <c>FileIdentityResetTests</c> fails if
        /// <see cref="OpenFileSession"/> grows an identity field this does not
        /// clear.</para>
        ///
        /// <para>Deliberately NOT here: CurrentFileName, CurrentFormat and
        /// HasUnsavedChanges. Every path assigns those explicitly, and blanking
        /// HasUnsavedChanges mid-load would drop the dirty flag of the tab being
        /// captured.</para>
        /// </summary>
        private void ResetFileIdentity()
        {
            OriginalFilePath = null;
            CurrentLocresPath = null;
            CurrentLocresDocument = null;
            OriginalLocresName = null;
            CurrentPoHeader = null;
            CsvFile = "";
            DetectedLanguageCode = null;
            DetectedLocresVersion = null;

            // Merge tints describe ONE file; they are identity too.
            _newKeySet.Clear();
            _sourceChangedKeySet.Clear();
            _mergeFlagsForPath = null;
        }

        /// <summary>Stable composite id for (namespace, key) row identity sets.
        /// U+001F (unit separator) cannot occur in real namespace/key text.</summary>
        private static string NewKeyId(string? ns, string? key)
            => string.IsNullOrEmpty(ns) ? key ?? "" : ns + '\u001F' + key;

        private Timer? _autoSaveTimer;
        private Timer? _statusClearTimer;
        private AppConfig _cfg;

        // Serializes SaveEditedData calls so an auto-save tick can't run on top of a manual
        // export or another auto-save tick that is still finishing.
        private readonly object _saveLock = new();

        // Whose line the bar shows — see Services/StatusArbiter. Code that sets
        // StatusText is "explicit"; AddLog's copy goes through MirrorToStatus,
        // which raises this flag so the change is not mistaken for explicit.
        private readonly Services.StatusArbiter _statusArbiter = new(
            then => Avalonia.Threading.Dispatcher.UIThread.Post(then, Avalonia.Threading.DispatcherPriority.Background));
        private bool _mirroringLogToStatus;

        partial void OnStatusTextChanged(string value)
        {
            if (!_mirroringLogToStatus)
            {
                if (string.IsNullOrEmpty(value)) _statusArbiter.Cleared();
                else _statusArbiter.Explicit();
            }

            _statusClearTimer?.Stop();
            if (string.IsNullOrEmpty(value)) return;

            _statusClearTimer ??= new Timer { AutoReset = false };
            _statusClearTimer.Interval = 15000;
            _statusClearTimer.Elapsed -= OnStatusClearElapsed;
            _statusClearTimer.Elapsed += OnStatusClearElapsed;
            _statusClearTimer.Start();
        }

        private void OnStatusClearElapsed(object? sender, ElapsedEventArgs e)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusText = "");
        }

        // ════════════════════════════════════════════════════════════════════
        //  UNDO / REDO
        // ════════════════════════════════════════════════════════════════════
        public UndoRedoManager UndoManager { get; } = new();

        public bool CanUndo => UndoManager.CanUndo;
        public bool CanRedo => UndoManager.CanRedo;

        [RelayCommand(CanExecute = nameof(CanUndo))]
        private void Undo() => UndoManager.Undo();

        [RelayCommand(CanExecute = nameof(CanRedo))]
        private void Redo() => UndoManager.Redo();

        // ════════════════════════════════════════════════════════════════════
        //  EVENTS — view interaction (file dialogs, folder reveal)
        // ════════════════════════════════════════════════════════════════════
        public event Func<string, string, Task<string?>>? OpenFileRequested;
        public event Func<string, string, Task<string?>>? SaveFileRequested;
        public event Action<string>? OpenFolderRequested;

        /// <summary>
        /// Ask the View for a plain yes/no confirmation (title, message, confirm
        /// label). Used by the merge flow, which has to explain what it is about to
        /// do before the user commits to it.
        /// </summary>
        public event Func<string, string, string, Task<bool>>? ConfirmRequested;

        /// <summary>
        /// Show the merge match report and return the user's decision. Carries the
        /// result plus the two file names so the dialog can name them.
        /// </summary>
        public event Func<Services.MergeMatchResult, string, string,
                          Task<Controls.MergeReportChoice>>? MergeReportRequested;

        /// <summary>
        /// Raised after a file has been loaded + the review sidecar has been
        /// hydrated. The View opens a modal <c>ValidationProgressDialog</c> and
        /// runs <c>ValidationService.RunInitialAsync</c> against the supplied
        /// rows; the returned task resolves to <c>true</c> when the user
        /// cancelled. The VM clears all row Problems on cancel.
        /// </summary>
        public event Func<IReadOnlyList<TranslationRow>, Task<bool>>? InitialValidationRequested;

        /// <summary>VM-side trampoline so the partial FileOps file can raise the event.</summary>
        internal Task<bool> RaiseInitialValidationAsync(IReadOnlyList<TranslationRow> rows)
            => InitialValidationRequested?.Invoke(rows) ?? Task.FromResult(false);

        // ════════════════════════════════════════════════════════════════════
        //  CONSTRUCTOR
        // ════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Process-wide reference to the live VM. Set in the constructor so
        /// non-VM callers (ToastService Undo closures fired from controls
        /// like ProblemPopover / LightbulbButton that don't directly have
        /// the VM) can reach UndoManager / other VM state without DI plumbing.
        /// Only one MainWindow exists at a time so a static is safe.
        /// </summary>
        public static MainWindowViewModel? Current { get; private set; }

        public MainWindowViewModel()
        {
            _cfg = AppConfig.Instance;
            Current = this;

            // The status line holds a sentence already built in the old language;
            // after a live switch it would stay in that language for up to 15 s.
            Loc.Instance.LanguageChanged += OnUiLanguageChanged;

            // Hydrate persisted UI toggles before any view binding evaluates them.
            _autoExpandRows = _cfg.AutoExpandRows;

            if (!string.IsNullOrEmpty(_cfg.AutoSavePath))
            {
                try { Directory.CreateDirectory(_cfg.AutoSavePath); }
                catch (Exception ex) { Console.WriteLine($"[WARN] Could not create AutoSavePath '{_cfg.AutoSavePath}': {ex.Message}"); }
            }

            InitAutoSave();

            // A QC sidecar that cannot be written must never fail silently — the
            // user would keep reviewing and lose the work on exit.
            Services.Qc.QcService.SaveFailed = msg => AddLog(
                string.Format(Loc.T("log.qc.sidecarSaveFailed"), msg), LogLevel.Error);
            // One-release upgrade hygiene: sweep .temp-SUL-* dirs left behind by
            // pre-LocresIO versions (the app no longer creates them).
            CleanupStaleTempDirs();

            // App-usage counter: session count is incremented once per app launch.
            _cfg.TotalSessions++;

            UndoManager.StateChanged += () =>
            {
                UndoCommand.NotifyCanExecuteChanged();
                RedoCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanUndo));
                OnPropertyChanged(nameof(CanRedo));
            };

            // Keep FilteredLogs in sync with Logs + active tab.
            Logs.CollectionChanged += (_, _) => RebuildFilteredLogs();

            // Hydrate RecentFiles from persisted config.
            RefreshRecentFiles();

            AddLog(Loc.T("log.app.started"));
        }

        /// <summary>Recompute FilteredLogs from Logs + LogFilter. Cheap; O(n) on each rebuild.</summary>
        private void RebuildFilteredLogs()
        {
            FilteredLogs.Clear();
            foreach (var log in Logs)
            {
                if (ShouldShowInActiveTab(log)) FilteredLogs.Add(log);
            }
        }

        private bool ShouldShowInActiveTab(LogMessage m) => LogFilter switch
        {
            LogLevelFilter.Info => m.Level == LogLevel.Info,
            LogLevelFilter.Warning => m.Level == LogLevel.Warning,
            LogLevelFilter.Error => m.Level == LogLevel.Error,
            _ => true,
        };

        partial void OnLogFilterChanged(LogLevelFilter value) => RebuildFilteredLogs();

        // Logs-tab severity segmented filter (Etap 10).
        [RelayCommand] private void SetLogFilterAll() => LogFilter = LogLevelFilter.All;
        [RelayCommand] private void SetLogFilterInfo() => LogFilter = LogLevelFilter.Info;
        [RelayCommand] private void SetLogFilterWarning() => LogFilter = LogLevelFilter.Warning;
        [RelayCommand] private void SetLogFilterError() => LogFilter = LogLevelFilter.Error;

        /// <summary>Refresh the bound RecentFiles collection from AppConfig.</summary>
        public void RefreshRecentFiles()
        {
            RecentFiles.Clear();
            foreach (var path in _cfg.RecentFiles)
            {
                RecentFiles.Add(new RecentFileEntry { Path = path });
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  LOGGING
        // ════════════════════════════════════════════════════════════════════
        public void AddLog(string message, LogLevel level = LogLevel.Info)
        {
            long ticket = _statusArbiter.Issue();   // its place in line is NOW, not when the post runs
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Logs.Add(new LogMessage { Message = message, Level = level });
                if (Logs.Count > 500)
                {
                    int excess = Logs.Count - 450;
                    for (int i = 0; i < excess; i++) Logs.RemoveAt(0);
                }
                MirrorToStatus(message, ticket, level);
            });
        }

        /// <summary>
        /// Copy a log line into the status bar — unless it is older than what is
        /// showing, or an info detail of a line the code set this turn. See StatusArbiter.
        /// </summary>
        private void MirrorToStatus(string message, long ticket, LogLevel level)
        {
            if (!_statusArbiter.TryMirror(ticket, level)) return;
            _mirroringLogToStatus = true;
            try { StatusText = message; }
            finally { _mirroringLogToStatus = false; }
        }

        [RelayCommand]
        private void ToggleLogs() => ShowLogs = !ShowLogs;

        [RelayCommand]
        public void ClearLogs()
        {
            Logs.Clear();
            StatusText = Loc.T("status.msg.logsCleared");
        }

        // ════════════════════════════════════════════════════════════════════
        //  FILTER + SELECTION
        // ════════════════════════════════════════════════════════════════════
        // ─── SORTING (custom cycle: none → asc → desc → none) ──────────────
        /// <summary>Active sort column name ("Key" / "Source" / "Target") — null = natural file order.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(KeySortGlyph), nameof(SourceSortGlyph), nameof(TargetSortGlyph))]
        private string? _sortColumn;

        /// <summary>true = ascending, false = descending. Only meaningful when SortColumn != null.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(KeySortGlyph), nameof(SourceSortGlyph), nameof(TargetSortGlyph))]
        private bool _sortAscending = true;

        public string KeySortGlyph => SortGlyphFor("Key");
        public string SourceSortGlyph => SortGlyphFor("Source");
        public string TargetSortGlyph => SortGlyphFor("Target");

        private string SortGlyphFor(string column) =>
            SortColumn == column ? (SortAscending ? "▲" : "▼") : "";

        /// <summary>
        /// Click cycle for column header: same column → asc / desc / off; different
        /// column → start at asc. Order-of-rows is rebuilt via ApplyFilter so the
        /// filtered + sorted view in <see cref="Rows"/> stays consistent.
        /// </summary>
        public void CycleSort(string column)
        {
            // Column-header sort is suppressed while an EditedAt Sort-chip mode is
            // active (Etap 12). Clear the chip back to Default to use column sort.
            if (SortMode != RowSortMode.Default) return;

            if (SortColumn != column)
            {
                SortColumn = column;
                SortAscending = true;
            }
            else if (SortAscending)
            {
                SortAscending = false;
            }
            else
            {
                SortColumn = null;
            }
            ApplyFilter();
        }

        partial void OnShowOnlyUntranslatedChanged(bool value)
        {
            // Mutually exclusive with ShowOnlyTranslated — they're opposites,
            // toggling both ON is incoherent (would just select all rows via
            // OR semantics). Turning ON Untranslated turns OFF Translated.
            if (value && ShowOnlyTranslated) ShowOnlyTranslated = false;
            ApplyFilter();
        }
        partial void OnShowOnlyTranslatedChanged(bool value)
        {
            if (value && ShowOnlyUntranslated) ShowOnlyUntranslated = false;
            ApplyFilter();
        }
        partial void OnShowOnlyCellRecoveredChanged(bool value) => ApplyFilter();
        partial void OnShowOnlyTagReplacedChanged(bool value) => ApplyFilter();
        partial void OnShowOnlyInfoChanged(bool value) => ApplyFilter();

        // Selected / Unselected are mutually exclusive (same rationale as
        // Translated / Untranslated — toggling both ON would OR to "all rows"
        // and the filter would do nothing useful).
        partial void OnShowOnlySelectedChanged(bool value)
        {
            if (value && ShowOnlyUnselected) ShowOnlyUnselected = false;
            ApplyFilter();
        }
        partial void OnShowOnlyUnselectedChanged(bool value)
        {
            if (value && ShowOnlySelected) ShowOnlySelected = false;
            ApplyFilter();
        }

        public void ApplyFilter()
        {
            // Status-bar dot counters are severity-based: a row counts in its
            // HIGHEST severity bucket. CellRecoveredCount = Error chip (any
            // Error-severity Problem from any validator), TagReplacedCount =
            // Warning chip, InfoCount = third blue chip (shown, even at zero,
            // whenever a file is open — like Errors / Warnings). Info covers Whitespace /
            // Punctuation / DuplicateSource / LengthExcessive.
            CellRecoveredCount = _allRows.Count(r => r.Severity == ProblemSeverity.Error);
            TagReplacedCount = _allRows.Count(r => r.Severity == ProblemSeverity.Warning);
            InfoCount = _allRows.Count(r => r.Severity == ProblemSeverity.Info);
            AnyProblemRowsCount = _allRows.Count(r => r.Problems.Count > 0);
            ReviewMarkRowsCount = _allRows.Count(r => r.Review != ReviewStatus.None);

            // Etap 10: keep the Problems-tab counts + list in sync with the
            // current problem set (covers F8, Clear, Fix all, file open/close).
            RefreshProblemsView();

            // STATUS group (OR semantics): a row matches if ANY active status
            // filter matches (severity chips + translated/untranslated + selected).
            bool statusFilter = ShowOnlyCellRecovered || ShowOnlyTagReplaced || ShowOnlyInfo
                                || ShowOnlyUntranslated || ShowOnlyTranslated
                                || ShowOnlySelected || ShowOnlyUnselected;

            // RESTRICT groups (AND semantics, Etap 12): review include-set,
            // recently-edited-this-session, text-contains. Each narrows the result.
            bool recentFilter = ShowOnlyRecentlyEdited;
            bool commentedFilter = ShowOnlyCommented;
            bool foundFilter = IsFoundFilterActive;
            bool textFilter = !string.IsNullOrEmpty(FilterText);
            // Locres files legitimately carry entries with empty values — the
            // document keeps them for integrity, but the grid hides them unless
            // the user opts in via View → Show empty keys (persisted preference).
            bool hideEmptyFilter = !ShowEmptyKeys && CurrentFormat == FileFormat.Locres;
            bool mergeFilter = ShowOnlyNewKeys;
            bool anyFilter = statusFilter || IsReviewFilterActive || recentFilter || commentedFilter
                             || foundFilter || textFilter || hideEmptyFilter || mergeFilter;

            bool sortByEdited = SortMode != RowSortMode.Default;
            bool anySort = sortByEdited || SortColumn != null;

            // Fast path: nothing active AND Rows is already pristine (same order as
            // _allRows). _rowsArePristine guards against skipping the rebuild right
            // after a sort was turned off — Rows would still be in the sorted order.
            // The ReferenceEquals guard catches a wholesale _allRows swap (tab switch
            // / new file): equal COUNTS don't mean same rows — skipping the rebuild
            // then leaves the grid showing the previous file's row instances.
            if (!anyFilter && !anySort && _rowsArePristine && Rows.Count == _allRows.Count
                && (Rows.Count == 0 || ReferenceEquals(Rows[0], _allRows[0])))
            {
                FilteredRowCount = Rows.Count;
                UpdateSelectionCount();
                ApplyAltRowFlags();
                return;
            }

            IEnumerable<TranslationRow> source = _allRows;
            if (anyFilter)
            {
                source = source.Where(r =>
                    (!hideEmptyFilter || !string.IsNullOrEmpty(r.Source) || !string.IsNullOrEmpty(r.Target)) &&
                    (!statusFilter || StatusMatches(r)) &&
                    PassesReviewFilter(r) &&
                    PassesTextFilter(r) &&
                    (!recentFilter || r.IsEditedThisSession) &&
                    (!commentedFilter || r.HasComment) &&
                    (!mergeFilter || r.IsNewKey) &&
                    PassesFoundFilter(r));
            }

            // "Last N edits" cap — keep the N most-recently-edited of the
            // recently-edited set (only meaningful when that filter is on).
            if (recentFilter && RecentEditLimit > 0)
            {
                source = source
                    .OrderByDescending(r => r.EditedAt ?? DateTime.MinValue)
                    .Take(RecentEditLimit);
            }

            // Sort: the EditedAt Sort chip wins over the column-header sort.
            if (sortByEdited)
            {
                source = SortMode == RowSortMode.RecentlyEditedNewer
                    ? source.OrderByDescending(r => r.EditedAt ?? DateTime.MinValue)
                    : source.OrderBy(r => r.EditedAt ?? DateTime.MinValue);
            }
            else if (SortColumn != null)
            {
                source = SortColumn switch
                {
                    "Key" => SortAscending ? source.OrderBy(r => r.Key, System.StringComparer.CurrentCultureIgnoreCase)
                                              : source.OrderByDescending(r => r.Key, System.StringComparer.CurrentCultureIgnoreCase),
                    "Source" => SortAscending ? source.OrderBy(r => r.Source, System.StringComparer.CurrentCultureIgnoreCase)
                                              : source.OrderByDescending(r => r.Source, System.StringComparer.CurrentCultureIgnoreCase),
                    "Target" => SortAscending ? source.OrderBy(r => r.Target ?? "", System.StringComparer.CurrentCultureIgnoreCase)
                                              : source.OrderByDescending(r => r.Target ?? "", System.StringComparer.CurrentCultureIgnoreCase),
                    _ => source,
                };
            }

            Rows.ReplaceAll(source);
            // Rows now mirrors _allRows in order ONLY when nothing filtered/sorted it.
            _rowsArePristine = !anyFilter && !anySort;
            FilteredRowCount = Rows.Count;
            UpdateSelectionCount();
            ApplyAltRowFlags();
        }

        /// <summary>OR-match for the status filter group (severity chips + translated/selected).</summary>
        private bool StatusMatches(TranslationRow r) =>
            (ShowOnlyCellRecovered && r.Severity == ProblemSeverity.Error) ||
            (ShowOnlyTagReplaced && r.Severity == ProblemSeverity.Warning) ||
            (ShowOnlyInfo && r.Severity == ProblemSeverity.Info) ||
            (ShowOnlyUntranslated && r.IsUntranslated) ||
            (ShowOnlyTranslated && !r.IsUntranslated) ||
            (ShowOnlySelected && r.IsSelected) ||
            (ShowOnlyUnselected && !r.IsSelected);

        // ════════════════════════════════════════════════════════════════════
        //  SELECTION — ONE SOURCE OF TRUTH
        //
        //  A filter changes what is VISIBLE, never what is SELECTED: a row
        //  hidden by a filter keeps its checkbox. So "what is selected" has
        //  exactly one answer — every checked row in the file — and every
        //  consumer must ask for it here instead of re-deriving it from
        //  whatever collection happens to be in scope.
        //
        //  Before this, three different answers existed side by side: the
        //  status bar counted VISIBLE rows, most operations used the whole
        //  file, and the AI panel used a SNAPSHOT taken when it opened. The
        //  status bar could say "Selected: 0" while QC acted on 200 rows, and
        //  the AI panel's "Selected" scope silently translated the ENTIRE file
        //  when its snapshot was empty. Do not reintroduce a second answer.
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// The rows the user has checked, across the whole file — the scope of
        /// every "selected" operation. Materialised so callers can mutate rows
        /// while iterating.
        /// </summary>
        public List<TranslationRow> SelectedRows => _allRows.Where(r => r.IsSelected).ToList();

        /// <summary>True when at least one row anywhere in the file is checked.</summary>
        public bool HasSelection => SelectedCount > 0;

        /// <summary>
        /// True when part of the selection is hidden by the active filter. The
        /// status bar says so out loud — otherwise a user who selected 200 rows
        /// and then filtered sees a number that looks like the selection shrank.
        /// </summary>
        public bool HasSelectionOutsideFilter => SelectedCount > SelectedVisibleCount;

        public void UpdateSelectionCount()
        {
            SelectedCount = _allRows.Count(r => r.IsSelected);
            SelectedVisibleCount = Rows.Count(r => r.IsSelected);
            // Tri-state Select-All is deliberately filter-aware: it checks and
            // unchecks what is on screen, so it must reflect what is on screen.
            if (Rows.Count == 0)
                IsAllSelected = false;
            else if (SelectedVisibleCount == Rows.Count)
                IsAllSelected = true;
            else if (SelectedVisibleCount == 0)
                IsAllSelected = false;
            else
                IsAllSelected = null; // partial — show indeterminate
        }

        /// <summary>
        /// Replace the selection with exactly these rows. Used where a command
        /// carries its own scope (right-click → AI Translate): instead of handing
        /// that scope to a panel as a hidden snapshot, we make it the real
        /// selection, so the grid checkboxes, the status bar and the panel all
        /// show the same set before anything runs.
        /// </summary>
        public void SetSelection(IEnumerable<TranslationRow> rows)
        {
            var wanted = new HashSet<TranslationRow>(rows);
            foreach (var r in _allRows) r.IsSelected = wanted.Contains(r);
            UpdateSelectionCount();
        }

        /// <summary>Check or uncheck every CURRENTLY VISIBLE row (filter-aware).</summary>
        public void SelectAll(bool select)
        {
            foreach (var r in Rows)
                r.IsSelected = select;
            UpdateSelectionCount();
        }

        // Anchor row for Shift+Click range-select. NOT observable — internal state.
        private TranslationRow? _anchorRow;

        /// <summary>
        /// Returns the rows a context-menu action should apply to (spec §5.2):
        ///   if the clicked row is itself checked AND there are 2+ checked rows
        ///     → return all checked rows (multi-row scope)
        ///   else
        ///     → return just the clicked row (single-row scope).
        /// </summary>
        public List<TranslationRow> GetContextScope(TranslationRow clicked)
        {
            var checkedRows = SelectedRows;
            if (clicked.IsSelected && checkedRows.Count > 1)
                return checkedRows;
            return new List<TranslationRow> { clicked };
        }

        /// <summary>
        /// Modifier-aware row click — spec §3.1.
        ///   • no modifier            → set anchor only; do NOT change IsChecked.
        ///   • Ctrl (or Cmd)          → toggle IsChecked of this row; set anchor.
        ///   • Shift                  → range-check from anchor to clicked (inclusive),
        ///                              additive (does not clear existing checks);
        ///                              anchor unchanged.
        ///   • Ctrl+Shift             → same as Shift (already additive).
        /// </summary>
        public void HandleRowClick(TranslationRow row, bool ctrl, bool shift)
        {
            if (row == null) return;

            if (shift && _anchorRow != null)
            {
                int i1 = Rows.IndexOf(_anchorRow);
                int i2 = Rows.IndexOf(row);
                if (i1 >= 0 && i2 >= 0)
                {
                    int lo = Math.Min(i1, i2), hi = Math.Max(i1, i2);
                    for (int i = lo; i <= hi; i++) Rows[i].IsSelected = true;
                }
                // anchor not updated on shift
            }
            else if (shift)
            {
                row.IsSelected = true;
                _anchorRow = row;
            }
            else if (ctrl)
            {
                row.IsSelected = !row.IsSelected;
                _anchorRow = row;
            }
            else
            {
                _anchorRow = row;
                // plain click: do not change IsChecked — only focus (handled by DataGrid native).
            }

            UpdateSelectionCount();
        }

        private TranslationRow? _focusedRow;
        public TranslationRow? FocusedRow => _focusedRow;

        public void SetFocusedRow(TranslationRow? focusedRow)
        {
            _focusedRow = focusedRow;
            foreach (var r in _allRows)
                r.IsFocusedRow = ReferenceEquals(r, focusedRow);
            OnPropertyChanged(nameof(FocusedRow));
        }

        public void SetHoveredRow(TranslationRow? hoveredRow)
        {
            foreach (var r in _allRows)
                r.IsHoveredRow = ReferenceEquals(r, hoveredRow);
        }

        public void RefreshRowVisualStates()
        {
            foreach (var r in _allRows)
                r.RefreshVisualState();
        }

        /// <summary>
        /// Repaint zebra-striping on the visible <c>Rows</c> collection. Called
        /// after every filter/sort rebuild and after row removal so the alt-row
        /// background matches actual on-screen position. Matches the prototype's
        /// CSS <c>.grid-row:nth-child(even)</c>.
        /// </summary>
        public void ApplyAltRowFlags()
        {
            for (int i = 0; i < Rows.Count; i++)
                Rows[i].IsAltRow = (i % 2) == 1;
        }

        /// <summary>
        /// Move the focused row by <paramref name="delta"/> positions inside
        /// the visible <c>Rows</c> collection. Mirrors the prototype's
        /// ArrowUp/Down/PageUp/PageDown handlers.
        /// When <paramref name="extendSelection"/> is true the newly-focused row
        /// is also checked (Shift+Arrow range-extend). Anchor only updates on a
        /// plain move (matches prototype's <c>if (!e.shiftKey) setAnchorId(next)</c>).
        /// </summary>
        public void MoveFocus(int delta, bool extendSelection)
        {
            if (Rows.Count == 0) return;
            int currentIdx = _focusedRow != null ? Rows.IndexOf(_focusedRow) : -1;
            int newIdx = currentIdx < 0
                ? 0
                : Math.Clamp(currentIdx + delta, 0, Rows.Count - 1);
            FocusRowAt(newIdx, extendSelection);
        }

        /// <summary>
        /// Move focus to an absolute index inside <c>Rows</c>. Used by Home/End
        /// (index 0 / Rows.Count - 1) and any future "jump-to" actions.
        /// </summary>
        public void MoveFocusToIndex(int absoluteIdx, bool extendSelection = false)
        {
            if (Rows.Count == 0) return;
            int clamped = Math.Clamp(absoluteIdx, 0, Rows.Count - 1);
            FocusRowAt(clamped, extendSelection);
        }

        private void FocusRowAt(int idx, bool extendSelection)
        {
            var next = Rows[idx];
            if (extendSelection)
            {
                next.IsSelected = true;
                UpdateSelectionCount();
            }
            else
            {
                _anchorRow = next;
            }
            SetFocusedRow(next);
        }

        /// <summary>Click handler logic for either Select-All checkbox: cycles
        /// false/null/indeterminate → true → false. (Same intent as the prototype's
        /// `next = !allChecked` pattern.)</summary>
        public void ToggleSelectAll()
        {
            SelectAll(IsAllSelected != true);
        }

        /// <summary>
        /// Snapshot of each row's last-committed Target. Used by the row PropertyChanged
        /// handler to compute (oldValue, newValue) pairs for the undo stack. Subscribed on
        /// SetRows; flushed alongside it.
        /// </summary>
        private readonly Dictionary<TranslationRow, string> _targetSnapshot = new();

        /// <summary>Replace the master row list; resets filters and undo history.</summary>
        private void SetRows(IEnumerable<TranslationRow> rows)
        {
            // Drop any pending row-validation timers from the previous file so
            // they don't fire against rows we are about to release.
            Services.ValidationService.Instance.ClearDebounce();

            // Detach old subscriptions before we drop the references.
            foreach (var r in _allRows)
                r.PropertyChanged -= OnRowPropertyChanged;

            _allRows.Clear();
            _allRows.AddRange(rows);
            _rowsGeneration++;
            _rowsArePristine = false; // Rows still shows the OLD file — force a rebuild
            _targetSnapshot.Clear();

            foreach (var r in _allRows)
            {
                r.PropertyChanged += OnRowPropertyChanged;
                _targetSnapshot[r] = r.Target ?? "";
            }

            RowCount = _allRows.Count;
            HasData = _allRows.Count > 0;
            // The merge chips exist only while the open file carries merge tints.
            RefreshMergeFlagState();
            ApplyFilter();
            UndoManager.Clear();
            RaiseProgressNotifications();
        }

        public void CloseCurrentFile()
        {
            if (!HasData && string.IsNullOrEmpty(CurrentFileName)) return;

            ShowAiPanel = false;
            ShowStatusPanel = false;
            ShowSettingsPanel = false;
            IsFindBarOpen = false; // close inline FindBar on file close (Etap 9)

            ShowOnlyUntranslated = false;
            ShowOnlyTranslated = false;
            ShowOnlyCellRecovered = false;
            ShowOnlyTagReplaced = false;
            ShowOnlyInfo = false;
            ShowOnlySelected = false;
            ShowOnlyUnselected = false;
            SortColumn = null;
            // Etap 12 restrict-filters + Sort chip
            FilterIncludeApproved = true;
            FilterIncludeRejected = true;
            FilterIncludeUnreviewed = true;
            ShowOnlyRecentlyEdited = false;
            RecentEditLimit = 0;
            ShowOnlyCommented = false;
            ShowOnlyNewKeys = false;
            ShowOnlyFoundRows = false;
            FilterText = "";
            SortMode = RowSortMode.Default;

            SetRows(Array.Empty<TranslationRow>());

            // Drop any toasts whose captured Undo closures point at rows /
            // UndoManager state from the file we're closing. Without this,
            // a still-visible toast's "Undo" would fire against the
            // freshly-cleared undo stack (no-op) or — worse — against the
            // next file the user opens (silent cross-file edit).
            Services.ToastService.Instance.Clear();
            // Same hygiene for the popover: if it was anchored to a row from
            // the closing file, the DataGrid will recycle that row and the
            // PlacementTarget becomes a ghost. HideImmediate also nulls the
            // service's _currentRow/_currentProblem so a late hover-grace
            // tick after close can't reopen against stale state.
            Services.ProblemPopoverService.Instance.HideImmediate();

            ResetFileIdentity();
            CurrentFileName = "";
            CurrentFormat = FileFormat.None;
            HasUnsavedChanges = false;

            // Etap 13: closing to the Welcome screen drops all tabs.
            ActiveSession = null;
            if (OpenSessions.Count > 0) OpenSessions.Clear();
            OnPropertyChanged(nameof(HasOpenTabs));
            PersistOpenTabs();

            StatusText = Loc.T("project.fileClosed");
            AddLog(Loc.T("project.fileClosed"), LogLevel.Info);

            // A closed file drops thousands of rows + their cell controls. Reclaim now so
            // RAM returns to the user instead of lingering until the next gen-2 GC.
            ReclaimMemory();
        }

        /// <summary>Force a full reclaim after a large operation (file close, multi-translate).
        /// Used sparingly — only after we KNOW a lot just became garbage — so the working set
        /// drops promptly rather than drifting up across a long session.</summary>
        public static void ReclaimMemory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        /// <summary>
        /// Records every Target change as an undo action. Replaces the older DataGrid
        /// BeginningEdit/CellEditEnding handlers (which were unreliable on the first row's
        /// first edit — sometimes EditingElement came through as null and the action was
        /// silently dropped). Works for keyboard edits, paste-column, AI translation, and
        /// CopySourceToTarget alike because all of them ultimately set <see cref="TranslationRow.Target"/>.
        /// </summary>
        private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not TranslationRow row) return;

            // Validator output flipped a problem-related view on a row. Refresh
            // the aggregated counts that drive the status-column visibility +
            // the StatusPanel tiles. Listening to Severity catches every
            // Problems collection change (Severity recomputes from Problems.Max),
            // including non-tag/non-token problems like Whitespace / Plural /
            // Duplicate / Length — without it, status icons would stay hidden
            // on a file that only had those problem kinds. Debounced via
            // _statusCountsScheduled so an AI bulk-translate (which validates
            // every touched row) doesn't hammer .Count() over _allRows. Allowed
            // during replay too, so undoing a Clear-status restores its counts.
            if (e.PropertyName == nameof(TranslationRow.HasTokenError) ||
                e.PropertyName == nameof(TranslationRow.HasTokenWarning) ||
                e.PropertyName == nameof(TranslationRow.Severity))
            {
                ScheduleStatusCountsRefresh();
                return;
            }

            if (e.PropertyName != nameof(TranslationRow.Target)) return;

            // Every Target change — edit, bulk, undo — is a new revision of this tab.
            // A save that finishes later compares against it (SaveCompletion).
            _lastEditRevision = ++_editRevisionCounter;

            // Every path below this point changes Target — an undo replay, a bulk
            // operation, or a single edit — and each of them invalidates the find
            // bar's recorded offsets. Told once here rather than in the three
            // branches, because the two that return early (replay, bulk) are
            // exactly the ones that move the most text.
            NoteTargetEdited();

            string newVal = row.Target ?? "";

            // Undo/Redo replay is restoring a Target value. We must NOT record a
            // fresh CellEditAction (that would corrupt the stack), but we MUST keep
            // the snapshot current, refresh progress, and RE-VALIDATE the row — else
            // a reverted-to-broken Target keeps a stale, EMPTY Problems set: no
            // lightbulb, the popover says "clean", and Fix all sees nothing to fix.
            // (This was the "Ctrl+Z then Fix all does nothing, lightbulb says clean
            // but the text is broken" bug.)
            if (UndoManager.IsReplaying)
            {
                _targetSnapshot[row] = newVal;
                ScheduleProgressRefresh();
                Services.ValidationService.Instance.ScheduleRowValidation(row);
                return;
            }

            if (!_targetSnapshot.TryGetValue(row, out var oldVal)) oldVal = "";
            if (oldVal == newVal) return;

            // Bulk operations (AI translation, paste-column, CopySourceToTarget) build their
            // own BatchEditAction and push it in one shot. We skip per-row recording for those
            // paths via the _suppressUndoCapture flag.
            if (_suppressUndoCapture)
            {
                _targetSnapshot[row] = newVal;
                TouchEditMetadata(row);
                return;
            }

            UndoManager.AddAction(new CellEditAction(row, oldVal, newVal));
            _targetSnapshot[row] = newVal;
            HasUnsavedChanges = true;
            // Coalesced: the counters behind it are O(rows) and bound several times
            // over — ~22 ms per edit on a 35 744-row file. Invisible for one edit,
            // but quadratic for any loop of single edits; the bulk paths avoid it
            // only because they suppress capture.
            ScheduleProgressRefresh();
            TouchEditMetadata(row);
        }

        private bool _statusCountsScheduled;

        // Coalesced progress refresh (UntranslatedCount / TranslatedPercentage /
        // TranslatedCount). Used by the undo/redo replay path: a batch undo of N
        // rows fires this once per dispatcher cycle instead of calling
        // RaiseProgressNotifications N times — each progress getter is O(N), so the
        // naive per-row call would be O(N²) on a large Fix-all undo.
        private bool _progressRefreshScheduled;
        private void ScheduleProgressRefresh()
        {
            if (_progressRefreshScheduled) return;
            _progressRefreshScheduled = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _progressRefreshScheduled = false;
                RaiseProgressNotifications();
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// Coalesce status-count recomputes so a burst of per-row PropertyChanged
        /// events (one per row × multiple validator-driven property flips) ends
        /// up doing one O(N) pass per dispatcher cycle, not O(N²).
        /// </summary>
        private void ScheduleStatusCountsRefresh()
        {
            if (_statusCountsScheduled) return;
            _statusCountsScheduled = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Severity-based — see ApplyFilter for the rationale.
                CellRecoveredCount = _allRows.Count(r => r.Severity == ProblemSeverity.Error);
                TagReplacedCount = _allRows.Count(r => r.Severity == ProblemSeverity.Warning);
                InfoCount = _allRows.Count(r => r.Severity == ProblemSeverity.Info);
                AnyProblemRowsCount = _allRows.Count(r => r.Problems.Count > 0);
                ReviewMarkRowsCount = _allRows.Count(r => r.Review != ReviewStatus.None);
                _statusCountsScheduled = false;
                // Etap 10: refresh the Problems-tab counts + (if visible) list
                // so validator output appears live as the user edits.
                RefreshProblemsView();
            }, Avalonia.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// Stamp the per-row edit metadata after a Target mutation: UTC timestamp
        /// for the persistent <see cref="TranslationRow.EditedAt"/> (drives the
        /// Sort-by-recently-edited chip in Etap 12) and the session-only
        /// <see cref="TranslationRow.IsEditedThisSession"/> bool (drives the
        /// Recently-edited-this-session filter in Etap 12). The metadata is
        /// pushed through <see cref="Services.ReviewService"/> which debounces
        /// the sidecar write so a bulk edit flushes once, not N times.
        ///
        /// Also schedules a debounced validation pass on the row so the 6 light
        /// validators (Etap 2) refresh row.Problems 300 ms after the last edit.
        /// </summary>
        private static void TouchEditMetadata(TranslationRow row)
        {
            row.EditedAt = DateTime.UtcNow;
            row.IsEditedThisSession = true;
            Services.ReviewService.Instance.UpdateRow(row);
            Services.ValidationService.Instance.ScheduleRowValidation(row);
        }

        /// <summary>
        /// Refresh derived progress numbers shown in the Status panel + sidebar ring.
        /// Called after any operation that changes the translated row count.
        /// </summary>
        public void RaiseProgressNotifications()
        {
            OnPropertyChanged(nameof(TranslatedPercentage));
            OnPropertyChanged(nameof(TranslatedProgressBrush));
            OnPropertyChanged(nameof(TranslatedCount));
            OnPropertyChanged(nameof(UntranslatedCount));
            OnPropertyChanged(nameof(DuplicateSourceCount));
        }

        /// <summary>Count of translated rows (non-whitespace Target). Matches IsUntranslated.</summary>
        public int TranslatedCount => _allRows.Count(r => !r.IsUntranslated);
        /// <summary>Count of untranslated rows (empty or whitespace-only Target).</summary>
        public int UntranslatedCount => _allRows.Count - TranslatedCount;
        /// <summary>Count of rows whose Source value duplicates at least one other row.</summary>
        /// <remarks>Cached per row set: Source is fixed once a file is loaded, so a
        /// Target edit cannot change this number — yet it was regrouped on every
        /// edit, the most expensive of the progress counters (2026-10-19).</remarks>
        public int DuplicateSourceCount
        {
            get
            {
                if (_allRows.Count == 0) return 0;
                if (_duplicateSourceGeneration != _rowsGeneration)
                {
                    var perSource = new Dictionary<string, int>(_allRows.Count, StringComparer.Ordinal);
                    foreach (var r in _allRows)
                    {
                        string s = r.Source ?? "";
                        perSource[s] = perSource.TryGetValue(s, out int n) ? n + 1 : 1;
                    }
                    int dup = 0;
                    foreach (int n in perSource.Values) if (n > 1) dup += n;
                    _duplicateSourceCount = dup;
                    _duplicateSourceGeneration = _rowsGeneration;
                }
                return _duplicateSourceCount;
            }
        }

        /// <summary>Bumped whenever the CONTENT of <c>_allRows</c> is replaced (open,
        /// tab switch) — the key for caches that depend only on the row set.</summary>
        private int _rowsGeneration;
        private int _duplicateSourceGeneration = -1;
        private int _duplicateSourceCount;

        // ─── App-usage counters (proxy AppConfig — for binding) ────────────
        public int TotalFilesOpened => _cfg.TotalFilesOpened;
        public int TotalRowsTranslated => _cfg.TotalRowsTranslated;
        public int TotalAiRequests => _cfg.TotalAiRequests;
        public int TotalTmSaves => _cfg.TotalTmSaves;
        public int TotalSessions => _cfg.TotalSessions;

        // Live session-second tracker — flushed to AppConfig on Cleanup.
        private readonly DateTime _sessionStartUtc = DateTime.UtcNow;

        /// <summary>
        /// Total hours the app has been open across all sessions, INCLUDING the live
        /// current session. Recomputed on demand — refresh fires whenever the Status
        /// panel becomes visible (see <see cref="OnShowStatusPanelChanged"/>) and every
        /// minute while it stays open (via <see cref="_statusPanelTickTimer"/>).
        /// </summary>
        public int TotalHoursUsed
        {
            get
            {
                long sessionSeconds = (long)Math.Max(0, (DateTime.UtcNow - _sessionStartUtc).TotalSeconds);
                return (int)Math.Round((_cfg.TotalSecondsUsed + sessionSeconds) / 3600.0);
            }
        }

        private Timer? _statusPanelTickTimer;

        // Live-tick the Hours value while the Status panel is visible (every 60s).
        // Without this the Hours read stays at the boot snapshot until Cleanup.
        partial void OnShowStatusPanelChanged(bool value)
        {
            // Single-panel rule shared with the rest of the left-bar buttons.
            if (value) CloseOtherLeftPanels(nameof(ShowStatusPanel));

            if (value)
            {
                // Status panel opened: refresh derived values immediately, then keep ticking.
                OnPropertyChanged(nameof(TotalHoursUsed));
                RaiseProgressNotifications();
                _statusPanelTickTimer ??= new Timer(60_000) { AutoReset = true };
                _statusPanelTickTimer.Elapsed -= OnStatusTickElapsed;
                _statusPanelTickTimer.Elapsed += OnStatusTickElapsed;
                _statusPanelTickTimer.Start();
            }
            else
            {
                _statusPanelTickTimer?.Stop();
            }
        }

        private void OnStatusTickElapsed(object? sender, ElapsedEventArgs e)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(TotalHoursUsed)));
        }

        /// <summary>Record one AI-translation API request (one batch send).</summary>
        public void RecordAiRequest()
        {
            _cfg.TotalAiRequests++;
            OnPropertyChanged(nameof(TotalAiRequests));
        }

        /// <summary>Record N rows whose Target was set by AI translation.</summary>
        public void RecordRowsTranslatedByAi(int count)
        {
            if (count <= 0) return;
            _cfg.TotalRowsTranslated += count;
            OnPropertyChanged(nameof(TotalRowsTranslated));
        }

        /// <summary>Record one TM persistent-save.</summary>
        public void RecordTmSave()
        {
            _cfg.TotalTmSaves++;
            OnPropertyChanged(nameof(TotalTmSaves));
        }

        /// <summary>Record a successful file-open.</summary>
        public void RecordFileOpened()
        {
            _cfg.TotalFilesOpened++;
            OnPropertyChanged(nameof(TotalFilesOpened));
        }

        /// <summary>Session-only count of rows whose Target was filled from Translation Memory.</summary>
        [ObservableProperty] private int _tmHitsThisSession;

        /// <summary>Increment TM-hits counter by <paramref name="n"/>.</summary>
        public void RecordTmHits(int n)
        {
            if (n <= 0) return;
            TmHitsThisSession += n;
        }

        /// <summary>
        /// Bulk-operation guard. Operations that compose their own BatchEditAction
        /// (AI translation, paste column, CopySourceToTarget, ClearSelectedTarget, etc.)
        /// set this to true while mutating rows, then add their single composite action.
        ///
        /// Transitioning true → false also fires <see cref="RaiseProgressNotifications"/>
        /// so the Status panel + sidebar progress ring catch up at the end of every
        /// bulk write — individual row PropertyChanged handlers are skipped while the
        /// flag is true (avoids O(N×N) churn during AI translation).
        /// </summary>
        private bool _suppressUndoCaptureField;
        public bool SuppressUndoCapture
        {
            get => _suppressUndoCaptureField;
            set
            {
                if (_suppressUndoCaptureField == value) return;
                _suppressUndoCaptureField = value;
                if (!value) RaiseProgressNotifications();
            }
        }
        internal bool _suppressUndoCapture
        {
            get => SuppressUndoCapture;
            set => SuppressUndoCapture = value;
        }

        /// <summary>
        /// Keep the per-row Target snapshot in sync after a bulk mutation that wrote to
        /// <see cref="TranslationRow.Target"/> while <see cref="SuppressUndoCapture"/> was set.
        /// Without this, the next user edit would compute its (oldValue → newValue) pair
        /// against a stale snapshot and produce an unwound state.
        /// </summary>
        public void MarkTargetSnapshot(TranslationRow row, string value)
        {
            _targetSnapshot[row] = value ?? "";
        }

        // ════════════════════════════════════════════════════════════════════
        //  TAG VALIDATION
        // ════════════════════════════════════════════════════════════════════
        [RelayCommand]
        public void CheckTags()
        {
            if (_allRows.Count == 0) return;

            // Etap 7: full force-revalidate. Light validators run debounced on
            // every Target change + on file open; heavy validators run ONLY on
            // file open + this command (F8 / Project → Run Validation), because
            // PluralIncomplete/DuplicateSource/LengthExcessive are too expensive
            // to re-scan per keystroke. So this command:
            //   1. Re-runs every light validator on every row (synchronous;
            //      ValidationService.RunForRow only replaces light-kind problems).
            //   2. Re-runs every heavy validator over the whole set
            //      (ValidationService.RunHeavy clears + repopulates heavy-kind
            //      problems on every row).
            // No progress dialog here — for files large enough to lag the user
            // would already have waited through the modal at open time, and an
            // ad-hoc F8 should feel snappy. If profiling later shows this is
            // too slow on 50k+ rows we can move to the dialog path.
            foreach (var row in _allRows)
                Services.ValidationService.Instance.RunForRow(row);
            Services.ValidationService.Instance.RunHeavy(_allRows);

            ApplyFilter();

            int rowsWithProblems = _allRows.Count(r => r.Problems.Count > 0);
            int totalProblems = _allRows.Sum(r => r.Problems.Count);
            AddLog(string.Format(Loc.T("log.validation.done"), totalProblems, rowsWithProblems));
        }

        [RelayCommand]
        public void ClearCellRecoveredFromSelected() => ClearProblemsOfSeverity(ProblemSeverity.Error, "log.severity.error");

        [RelayCommand]
        public void ClearTagReplacedFromSelected() => ClearProblemsOfSeverity(ProblemSeverity.Warning, "log.severity.warning");

        [RelayCommand]
        public void ClearInfoFromSelected() => ClearProblemsOfSeverity(ProblemSeverity.Info, "log.severity.info");

        /// <summary>
        /// Shared body of the three Clear-status chip commands. Snapshots every
        /// removed <see cref="Problem"/> per row so the resulting
        /// <see cref="ClearProblemsAction"/> can both Undo (re-add the exact
        /// instances) and Redo (strip them again). Toast Undo button is wired
        /// to <see cref="UndoRedoManager.Undo"/> on this freshly-pushed action,
        /// so clicking it within the 4 s toast window restores the rows.
        ///
        /// <para>
        /// Was three near-duplicate methods that ALL bypassed the undo stack
        /// (Bug 4 in the post-Etap-8 audit) — a mis-click on the chip menu
        /// silently destroyed validator output with no recovery short of F8
        /// or another Target edit.
        /// </para>
        /// </summary>
        private void ClearProblemsOfSeverity(ProblemSeverity severity, string severityLabel)
        {
            // Match the prior semantics: only operate on rows whose TOP
            // severity equals the chip's severity for Error/Warning chips
            // (so an Error row isn't cleared by the Warning chip), but for
            // Info we allow rows where any Problem has Info severity even
            // if a higher Error/Warning is also present — otherwise clicking
            // the Info chip on a mixed-severity row would do nothing.
            var selected = severity == ProblemSeverity.Info
                ? SelectedRows.Where(r => r.Problems.Any(p => p.Severity == ProblemSeverity.Info)).ToList()
                : SelectedRows.Where(r => r.Severity == severity).ToList();

            if (selected.Count == 0) return;

            var snapshot = new List<(TranslationRow Row, List<Problem> Removed)>(selected.Count);
            foreach (var r in selected)
            {
                var removed = new List<Problem>();
                for (int i = r.Problems.Count - 1; i >= 0; i--)
                {
                    if (r.Problems[i].Severity == severity)
                    {
                        removed.Add(r.Problems[i]);
                        r.Problems.RemoveAt(i);
                    }
                }
                if (removed.Count > 0)
                    snapshot.Add((r, removed));
            }

            if (snapshot.Count == 0) return;

            var action = new ClearProblemsAction(snapshot);
            UndoManager.AddAction(action);

            ApplyFilter();
            int totalRemoved = snapshot.Sum(s => s.Removed.Count);
            AddLog(string.Format(Loc.T("log.status.cleared"), Loc.T(severityLabel), snapshot.Count, totalRemoved));

            // Targeted Undo: pop THIS specific action via direct Undo() call
            // on it, not UndoManager.Undo() — same Bug 2 reasoning as Apply
            // Fix toasts. If the user does another edit before clicking the
            // toast's Undo, UndoManager.Undo() would revert that unrelated
            // edit instead of restoring the problems.
            Action undo = () =>
            {
                action.Undo();
                ApplyFilter();
            };
            ToastService.Instance.ShowInfo(
                string.Format(Loc.Plural("toast.status.cleared", snapshot.Count), Loc.T(severityLabel), snapshot.Count),
                undo: undo);
        }

        // ════════════════════════════════════════════════════════════════════
        //  WINDOW TITLE
        // ════════════════════════════════════════════════════════════════════
        private void UpdateTitle()
        {
            var dirty = HasUnsavedChanges ? " •" : "";
            WindowTitle = string.IsNullOrEmpty(CurrentFileName)
                ? "Smart Unreal Localization"
                : $"Smart Unreal Localization — {CurrentFileName}{dirty}";
        }

        partial void OnCurrentFileNameChanged(string value)
        {
            UpdateTitle();
            OnPropertyChanged(nameof(ProjectPillText));
        }
        /// <summary>Monotonic across tabs; <see cref="_lastEditRevision"/> is the active tab's latest.</summary>
        private long _editRevisionCounter;
        private long _lastEditRevision;

        /// <summary>A save of <paramref name="session"/> taken at <paramref name="revision"/> finished.</summary>
        private void MarkSavedIfUnchanged(OpenFileSession? session, long revision)
        {
            switch (SaveCompletion.Decide(session, ActiveSession, revision, _lastEditRevision, session?.LastEditRevision ?? -1))
            {
                case SaveCompletion.Clear.LiveFlag: HasUnsavedChanges = false; break;
                case SaveCompletion.Clear.SessionFlag: session!.HasUnsavedChanges = false; break;
            }
        }

        partial void OnHasUnsavedChangesChanged(bool value)
        {
            UpdateTitle();
            // Etap 1.1 fix: keep the ACTIVE tab's unsaved flag (drives the tab's
            // unsaved dot + the close-confirm) in sync with the live editor state.
            // Session swaps null ActiveSession while a tab loads/restores, so this
            // never clobbers an outgoing tab's already-captured flag.
            if (ActiveSession is not null) ActiveSession.HasUnsavedChanges = value;
        }
        partial void OnCurrentFormatChanged(FileFormat value) => UpdateTitle();

        // ════════════════════════════════════════════════════════════════════
        //  AUTOSAVE
        // ════════════════════════════════════════════════════════════════════
        /// <summary>(Re)start the auto-save timer from current AppConfig values.
        /// Called at startup and again when Settings are saved, so interval/enabled
        /// changes apply immediately instead of after a restart.</summary>
        public void RestartAutoSave() => InitAutoSave();

        // The tab and edit revision the last successful auto-save wrote.
        private OpenFileSession? _autoSavedSession;
        private long _autoSavedRevision = -1;

        /// <summary>An auto-save is due only when there are unsaved edits that are
        /// not already in the last backup of this same tab.</summary>
        internal static bool AutoSaveDue(bool hasUnsavedChanges, object? session, long revision,
                                         object? savedSession, long savedRevision)
            => hasUnsavedChanges && !(ReferenceEquals(session, savedSession) && revision == savedRevision);

        private void InitAutoSave()
        {
            if (_autoSaveTimer != null)
            {
                _autoSaveTimer.Stop();
                _autoSaveTimer.Dispose();
                _autoSaveTimer = null;
            }
            if (!_cfg.AutoSaveEnabled) return;
            // Defensive re-clamp (config also clamps on load): interval <= 0 throws in
            // the Timer ctor, and int-math over ~35791 minutes would overflow — use
            // double milliseconds.
            double intervalMs = Math.Max(1, _cfg.AutoSaveIntervalMinutes) * 60_000d;
            _autoSaveTimer = new Timer(intervalMs);
            _autoSaveTimer.Elapsed += (_, _) =>
            {
                // One backup per state: auto-save never clears the unsaved flag (it is
                // a backup, not the user's file), so without this every tick wrote a
                // NEW timestamped copy of the same unchanged text — a day of unsaved
                // work left ~100 identical AutoSaved_* folders.
                var session = ActiveSession;
                long revision = _lastEditRevision;
                if (!AutoSaveDue(HasUnsavedChanges, session, revision, _autoSavedSession, _autoSavedRevision)) return;

                try
                {
                    // Auto-save in the file's OWN format: a .csv autosaves to .csv and a
                    // .po to .po (never to .locres) — only a .locres session packs back to
                    // .locres. SaveEditedData/AutoSaveSameFormat clear HasUnsavedChanges
                    // ONLY on full success (UnrealLocres can fail with a non-zero exit code
                    // without throwing, which historically produced silent data loss).
                    bool ok;
                    if (CurrentFormat == FileFormat.Locres)
                    {
                        if (CurrentLocresDocument == null) return;
                        ok = SaveEditedData(false);
                    }
                    else
                    {
                        ok = AutoSaveSameFormat();
                    }
                    if (!ok)
                    {
                        AddLog(Loc.T("log.autosave.incomplete"), LogLevel.Warning);
                    }
                    else
                    {
                        _autoSavedSession = session;
                        _autoSavedRevision = revision;
                    }
                }
                catch (Exception ex)
                {
                    AddLog(string.Format(Loc.T("log.autosave.crashed"), ex.Message), LogLevel.Error);
                }
            };
            _autoSaveTimer.Start();
        }

        // ════════════════════════════════════════════════════════════════════
        //  LIFECYCLE
        // ════════════════════════════════════════════════════════════════════
        private void OnUiLanguageChanged()
        {
            StatusText = "";
            // Problem titles and explanations are written in the language that was
            // current when the row was validated; re-run so the Problems tab and the
            // popovers follow the switch. Posted: the switch itself repaints first.
            if (OpenSessions.Any(s => s.IsLoaded) || _allRows.Count > 0)
                Avalonia.Threading.Dispatcher.UIThread.Post(RevalidateAllOpenFiles, Avalonia.Threading.DispatcherPriority.Background);
        }

        public void Cleanup()
        {
            Loc.Instance.LanguageChanged -= OnUiLanguageChanged;
            _autoSaveTimer?.Stop();
            _autoSaveTimer?.Dispose();

            // Force-flush any pending review-sidecar writes. Without this, a user
            // who edits a row and closes the app within the 500 ms debounce
            // window would lose the EditedAt / Review change.
            try { Services.ReviewService.Instance.SaveNow(); }
            catch (Exception ex) { Console.WriteLine($"[WARN] ReviewService flush: {ex.Message}"); }

            // Same for reviewer comments (own sidecar, own debounce).
            try { Services.CommentService.Instance.SaveNow(); }
            catch (Exception ex) { Console.WriteLine($"[WARN] CommentService flush: {ex.Message}"); }

            // Flush session duration to the persistent total before exit.
            try
            {
                _cfg.TotalSecondsUsed += (long)Math.Max(0, (DateTime.UtcNow - _sessionStartUtc).TotalSeconds);
                _cfg.Save();
            }
            catch (Exception ex) { Console.WriteLine($"[WARN] Could not flush usage stats: {ex.Message}"); }
        }

        public void MarkDirty()
        {
            HasUnsavedChanges = true;
        }
    }
}
