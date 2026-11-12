using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartUnrealLocalization.Localization;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  MainWindowViewModel — File tabs (Etap 13)
//
//  Multi-file editing without rewriting the single-file core: the VM still
//  operates on ONE active file (_allRows / UndoManager / Current* / etc.). Each
//  open file is an OpenFileSession that stashes its rows + undo snapshot +
//  unsaved flag + metadata while it's inactive. Switching tabs captures the
//  outgoing session and restores the incoming one.
//
//  Deferred (out of scope this etap, per plan): drag-drop reorder, per-tab find
//  / scroll restoration, memory eviction of cold sessions (all stay hot).
// ─────────────────────────────────────────────────────────────────────────────

namespace SmartUnrealLocalization.ViewModels
{
    public partial class MainWindowViewModel
    {
        /// <summary>Open file tabs, in display order (pinned first).</summary>
        public ObservableCollection<OpenFileSession> OpenSessions { get; } = new();

        [ObservableProperty] private OpenFileSession? _activeSession;

        /// <summary>Tab bar is shown only while at least one file is open.</summary>
        public bool HasOpenTabs => OpenSessions.Count > 0;

        /// <summary>
        /// Raised before closing a tab that has unsaved edits. The View shows a
        /// confirm and returns true to proceed, false to keep the tab (Etap 1.1).
        /// </summary>
        public event Func<OpenFileSession, Task<bool>>? CloseTabConfirmRequested;

        private static bool PathsEqual(string? a, string? b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        public OpenFileSession? FindSession(string path) =>
            OpenSessions.FirstOrDefault(s => PathsEqual(s.Path, path));

        // ── Capture / restore the live single-file state ─────────────────────

        /// <summary>Stash the active tab's current rows / undo / unsaved flag.</summary>
        private void CaptureActiveSession()
        {
            if (ActiveSession is null) return;
            ActiveSession.Rows = new List<TranslationRow>(_allRows);
            ActiveSession.UndoState = UndoManager.ExportState();
            ActiveSession.HasUnsavedChanges = HasUnsavedChanges;
            ActiveSession.LastEditRevision = _lastEditRevision;
            ActiveSession.CurrentLocresPath = CurrentLocresPath;
            ActiveSession.Document = CurrentLocresDocument;
        }

        /// <summary>
        /// Create the tab for the file that was JUST loaded into _allRows by
        /// OpenFileAsync, and make it active. The rows are already attached +
        /// validated; this only records the session and points ActiveSession at it.
        /// </summary>
        private void CommitLoadedSession(string path)
        {
            var session = new OpenFileSession
            {
                Path = OriginalFilePath ?? path,
                Format = CurrentFormat,
                CurrentLocresPath = CurrentLocresPath,
                Document = CurrentLocresDocument,
                OriginalLocresName = OriginalLocresName,
                CsvFile = CsvFile,
                FileName = CurrentFileName,
                DetectedLanguageCode = DetectedLanguageCode,
                DetectedLocresVersion = DetectedLocresVersion,
                PoHeader = CurrentPoHeader,
                Rows = new List<TranslationRow>(_allRows),
                UndoState = UndoManager.ExportState(),
                HasUnsavedChanges = HasUnsavedChanges,
                IsLoaded = true,
            };
            OpenSessions.Add(session);
            SetActive(session);
            OnPropertyChanged(nameof(HasOpenTabs));
            PersistOpenTabs();
        }

        /// <summary>Load a stashed tab's state back into the live single-file view.</summary>
        private void RestoreSession(OpenFileSession session)
        {
            // Detach the active tab up front so the metadata/unsaved restore below
            // never syncs onto the OUTGOING tab (see OnHasUnsavedChangesChanged).
            // SetActive(session) at the end re-points it to the incoming tab.
            ActiveSession = null;

            // Drop transient state that pointed at the outgoing tab.
            ValidationService.Instance.ClearDebounce();
            ToastService.Instance.Clear();
            ProblemPopoverService.Instance.HideImmediate();
            IsFindBarOpen = false;

            foreach (var r in _allRows)
                r.PropertyChanged -= OnRowPropertyChanged;

            _allRows.Clear();
            _allRows.AddRange(session.Rows);
            _rowsGeneration++;
            _rowsArePristine = false; // Rows still shows the OUTGOING tab — force a rebuild
            _targetSnapshot.Clear();
            foreach (var r in _allRows)
            {
                r.PropertyChanged += OnRowPropertyChanged;
                _targetSnapshot[r] = r.Target ?? "";
            }

            // Restore the file identity / metadata.
            OriginalFilePath = session.Path;
            CurrentLocresPath = session.CurrentLocresPath;
            CurrentLocresDocument = session.Document;
            OriginalLocresName = session.OriginalLocresName;
            CsvFile = session.CsvFile;
            CurrentFormat = session.Format;
            CurrentFileName = session.FileName;
            DetectedLanguageCode = session.DetectedLanguageCode;
            DetectedLocresVersion = session.DetectedLocresVersion;
            CurrentPoHeader = session.PoHeader;
            HasUnsavedChanges = session.HasUnsavedChanges;
            _lastEditRevision = session.LastEditRevision;

            RowCount = _allRows.Count;
            HasData = _allRows.Count > 0;

            UndoManager.LoadState(session.UndoState);
            // Point the Review sidecar at this file so review writes land correctly
            // (rows keep their in-memory Review state, so no re-hydrate needed).
            ReviewService.Instance.LoadForFile(session.Path);
            // Comments: re-point the sidecar (flushes the outgoing tab) and re-apply
            // to this tab's rows so the indicator is correct after a switch.
            CommentService.Instance.LoadForFile(session.Path);
            CommentService.Instance.Hydrate(session.Rows);
            // QC proposals are per-file as well — re-point and re-attach badges.
            Services.Qc.QcService.Instance.LoadForFile(session.Path);
            RefreshQcState();

            SetActive(session);
            ApplyFilter();
            RaiseProgressNotifications();
        }

        private void SetActive(OpenFileSession session)
        {
            foreach (var s in OpenSessions) s.IsActive = ReferenceEquals(s, session);
            ActiveSession = session;
        }

        // ── Commands the tab bar binds to ────────────────────────────────────

        /// <summary>
        /// The single-file AI run reads/writes the ACTIVE tab (rows, undo, dirty
        /// flag, same-source propagation over Rows) — letting it continue across a
        /// tab change corrupts the incoming tab. Cancel it; multi-translate
        /// (IsTabBound=false) is standalone and keeps running.
        /// </summary>
        private void CancelTabBoundAiRun()
        {
            if (ActiveAiTranslation is { IsTabBound: true } run)
            {
                run.Stop();
                AddLog(Loc.T("log.ai.stoppedTabClosed"), LogLevel.Warning);
            }
            CancelQcAiRun(); // the QC AI review is tab-bound the same way
        }

        [RelayCommand]
        public async Task ActivateTab(OpenFileSession? session)
        {
            if (session is null || ReferenceEquals(session, ActiveSession)) return;
            CancelTabBoundAiRun();
            // Lazy tab (restored header, not yet loaded) → load its file on demand.
            if (!session.IsLoaded) { await LoadIntoSessionAsync(session); return; }
            CaptureActiveSession();
            RestoreSession(session);
        }

        /// <summary>
        /// Load a not-yet-loaded (lazy) tab's file into the live view and commit
        /// the result into the EXISTING session. Mirrors CommitLoadedSession but
        /// targets `s` instead of creating a new tab.
        /// </summary>
        private async Task LoadIntoSessionAsync(OpenFileSession s)
        {
            var previous = ActiveSession;
            CaptureActiveSession();
            ActiveSession = null; // suppress the unsaved-sync clobber during the load
            if (!await LoadFileCoreAsync(s.Path))
            {
                // File missing / unsupported now — leave the tab unloaded, and put
                // the outgoing tab back so the VM isn't stuck detached with the
                // half-loaded file's leftovers (stale pill/format).
                ToastService.Instance.ShowInfo(string.Format(Loc.Plural("tabs.reopenFailed", 1), 1));
                // Back to the tab we came from, else to ANY loaded tab. Only when none
                // is left may the view close — CloseCurrentFile drops every tab, and
                // with no loaded tab left, only never-opened headers (no edits) go.
                // It used to be called whenever there was no previous tab, which on
                // the close path discarded other tabs' unsaved edits unasked.
                var fallback = previous != null && OpenSessions.Contains(previous)
                    ? previous
                    : OpenSessions.FirstOrDefault(x => x.IsLoaded);
                if (fallback != null) RestoreSession(fallback);
                else CloseCurrentFile();
                return;
            }
            s.Format = CurrentFormat;
            s.CurrentLocresPath = CurrentLocresPath;
            s.Document = CurrentLocresDocument;
            s.OriginalLocresName = OriginalLocresName;
            s.CsvFile = CsvFile;
            s.FileName = CurrentFileName;
            s.DetectedLanguageCode = DetectedLanguageCode;
            s.DetectedLocresVersion = DetectedLocresVersion;
            s.PoHeader = CurrentPoHeader;
            s.Rows = new List<TranslationRow>(_allRows);
            s.UndoState = UndoManager.ExportState();
            s.HasUnsavedChanges = HasUnsavedChanges;
            s.IsLoaded = true;
            SetActive(s);
            PersistOpenTabs();
        }

        [RelayCommand]
        public async Task CloseTab(OpenFileSession? session)
        {
            if (session is null) return;
            int idx = OpenSessions.IndexOf(session);
            if (idx < 0) return;

            // Guard against losing unsaved in-memory edits (Etap 1.1). The file on
            // disk is never touched by close, but the tab's edits would be dropped.
            if (session.HasUnsavedChanges && CloseTabConfirmRequested is not null)
            {
                bool proceed = await CloseTabConfirmRequested(session);
                if (!proceed) return;
            }

            bool wasActive = ReferenceEquals(session, ActiveSession);
            if (wasActive) CancelTabBoundAiRun();
            OpenSessions.Remove(session);
            session.IsActive = false;
            OnPropertyChanged(nameof(HasOpenTabs));

            if (wasActive)
            {
                // The closed tab's in-memory state is discarded (the file on disk
                // is untouched). Activate a neighbour, or fall back to Welcome.
                ActiveSession = null;
                if (OpenSessions.Count > 0)
                {
                    var next = OpenSessions[Math.Min(idx, OpenSessions.Count - 1)];
                    if (next.IsLoaded)
                        RestoreSession(next); // no capture — the outgoing tab is gone
                    else
                    {
                        // A tab restored from the last session but never opened has
                        // no rows yet. Restoring it directly showed an EMPTY file and
                        // marked it active, so clicking it did not load it either
                        // (audit 2026-09 §8). Load it the way ActivateTab does —
                        // after clearing the closed file's rows off the screen, so
                        // nothing can be edited into a tab that no longer exists.
                        // (Not CloseCurrentFile: that drops EVERY tab.)
                        SetRows(Array.Empty<TranslationRow>());
                        await LoadIntoSessionAsync(next);
                    }
                }
                else
                {
                    CloseCurrentFile();
                }
            }
            PersistOpenTabs();
        }

        /// <summary>Close the active tab (menu / Ctrl+W). No-op when nothing is open.</summary>
        public void CloseActiveTab()
        {
            if (ActiveSession is not null) _ = CloseTab(ActiveSession);
            else CloseCurrentFile();
        }

        [RelayCommand]
        public void TogglePin(OpenFileSession? session)
        {
            if (session is null) return;
            session.IsPinned = !session.IsPinned;
            ReorderPinnedFirst();
            PersistOpenTabs();
        }

        /// <summary>Stable-sort so pinned tabs sit first, preserving relative order.</summary>
        private void ReorderPinnedFirst()
        {
            var ordered = OpenSessions.OrderBy(s => s.IsPinned ? 0 : 1).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                int cur = OpenSessions.IndexOf(ordered[i]);
                if (cur != i) OpenSessions.Move(cur, i);
            }
        }

        // ── Drag-drop reorder (Etap 1.3) ─────────────────────────────────────

        /// <summary>Move <paramref name="dragged"/> into <paramref name="target"/>'s
        /// slot. Never crosses the pinned/unpinned boundary (keeps pinned-first).</summary>
        public void MoveTab(OpenFileSession dragged, OpenFileSession target)
        {
            if (ReferenceEquals(dragged, target)) return;
            if (dragged.IsPinned != target.IsPinned) return;
            int from = OpenSessions.IndexOf(dragged);
            int to = OpenSessions.IndexOf(target);
            if (from >= 0 && to >= 0 && from != to) OpenSessions.Move(from, to);
        }

        /// <summary>Called once when a tab drag finishes — re-enforce pinned-first + persist.</summary>
        public void CommitTabOrder()
        {
            ReorderPinnedFirst();
            PersistOpenTabs();
        }

        // ── Persistence ──────────────────────────────────────────────────────

        private void PersistOpenTabs()
        {
            _cfg.OpenFileTabs = OpenSessions
                .Select(s => new OpenTabEntry
                {
                    Path = s.Path,
                    IsPinned = s.IsPinned,
                    IsActive = ReferenceEquals(s, ActiveSession),
                })
                .ToList();
            _cfg.Save();
        }

        /// <summary>
        /// Reopen the tabs persisted from the last session (call once on launch).
        /// Missing files (deleted / moved) are skipped with a single toast.
        /// Each open runs the normal load path (incl. the validation modal).
        /// </summary>
        public async Task RestoreSessionsAsync()
        {
            var entries = _cfg.OpenFileTabs?.ToList() ?? new List<OpenTabEntry>();
            if (entries.Count == 0) return;

            int missing = 0;
            OpenFileSession? toActivate = null;
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Path) || !File.Exists(entry.Path)) { missing++; continue; }
                var s = new OpenFileSession
                {
                    Path = entry.Path,
                    FileName = Path.GetFileName(entry.Path),
                    IsPinned = entry.IsPinned,
                    IsLoaded = false,
                };
                OpenSessions.Add(s);
                if (entry.IsActive) toActivate = s;
            }
            OnPropertyChanged(nameof(HasOpenTabs));
            ReorderPinnedFirst();

            // Load ONLY the active tab now (lazy: the rest load on first click),
            // so a multi-tab restore is instant with no validation-modal spam.
            if (OpenSessions.Count > 0)
            {
                toActivate ??= OpenSessions[0];
                await LoadIntoSessionAsync(toActivate);
            }
            PersistOpenTabs();

            if (missing > 0)
                ToastService.Instance.ShowInfo(string.Format(Loc.Plural("tabs.reopenFailed", missing), missing));
        }
    }
}
