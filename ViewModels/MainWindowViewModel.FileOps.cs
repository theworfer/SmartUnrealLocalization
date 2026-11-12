using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Locres.IO;
using SmartUnrealLocalization.Localization;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;

namespace SmartUnrealLocalization.ViewModels
{
    // All file-system entry points: opening (.locres / .po / .csv), dual-import (load a
    // translated target locres and merge into current source by key), export back to
    // .locres / .po / .csv, Save-As, and the "merge to new version" workflow that
    // diffs an old translated locres against a new game build to find new keys.
    public partial class MainWindowViewModel
    {
        // ============================ OPEN / IMPORT ============================

        /// <summary>Universal Open — opens the system file picker, then delegates.</summary>
        public async Task OpenFileAsync()
        {
            try
            {
                StatusText = Loc.T("status.msg.selectingFile");
                var path = await (OpenFileRequested?.Invoke(
                    Loc.T("pick.openFile"),
                    "All supported|*.locres;*.po;*.csv") ?? Task.FromResult<string?>(null));
                if (path == null) { AddLog(Loc.T("log.file.selectionCancelled")); return; }
                await OpenFileAsync(path);
            }
            catch (Exception ex)
            {
                StatusText = string.Format(Loc.T("status.msg.error"), ex.Message);
                Console.WriteLine($"[ERROR] OpenFileAsync: {ex}");
            }
        }

        /// <summary>
        /// Universal Open — explicit path entrypoint (used by Welcome, Recent files,
        /// and recent-files click). Records the ORIGINAL user-selected path, dispatches by
        /// extension, and on success pushes the path to <see cref="AppConfig.RecentFiles"/>
        /// and refreshes UI-bound state (project pill, language, …).
        /// </summary>
        public async Task OpenFileAsync(string path)
        {
            // Remembered so a FAILED load can put the outgoing tab back instead of
            // leaving the VM detached with the half-loaded file's leftovers (stale
            // pill/format/Welcome mix — the openers already touched Current* state).
            OpenFileSession? previous = ActiveSession;
            try
            {
                // Etap 13: if this file is already open, just switch to its tab.
                var existingTab = FindSession(path);
                if (existingTab != null) { await ActivateTab(existingTab); return; }

                // Stash the active tab's edits/undo before the load replaces _allRows.
                CaptureActiveSession();
                // Detach the active tab during the load so the load's
                // HasUnsavedChanges=false doesn't clobber the just-captured tab.
                ActiveSession = null;

                if (await LoadFileCoreAsync(path))
                {
                    _cfg.AddRecentFile(path);
                    RefreshRecentFiles();
                    RecordFileOpened();
                    // OnPropertyChanged for ProjectPillText is fired by CurrentFileName.

                    // Etap 13: record the freshly-loaded file as a new tab + activate.
                    CommitLoadedSession(path);
                }
                else if (previous != null)
                {
                    RestoreSession(previous); // failed open → back to the tab we left
                }
                else
                {
                    CloseCurrentFile(); // no previous tab → clean Welcome, no leftovers
                }
            }
            catch (Exception ex)
            {
                StatusText = string.Format(Loc.T("status.msg.error"), ex.Message);
                Console.WriteLine($"[ERROR] OpenFileAsync(path): {ex}");
                if (ActiveSession is null && previous != null && OpenSessions.Contains(previous))
                    RestoreSession(previous);
            }
        }

        /// <summary>
        /// "Build Locres from CSV" — pack the currently-open CSV (INCLUDING unsaved in-app
        /// edits) into a .locres. Uses the active CSV grid; if the active file isn't a CSV
        /// it prompts to open one first. Then prompts for a SAMPLE/BASE .locres whose binary
        /// FORMAT the output must match; the grid's TARGET column is overlaid onto the base
        /// document in-process (LocresIO SaveWith) — the base supplies the format + every key
        /// whose target is empty/absent. Opens the result so it can be verified.
        /// </summary>
        public async Task OpenSpreadsheetAsync()
        {
            // Work from the active CSV (with its in-app edits). If the active file is not a
            // CSV, let the user pick one to open first.
            if (CurrentFormat != FileFormat.Csv)
            {
                var csvPath = await (OpenFileRequested?.Invoke(
                    Loc.T("pick.csvForBuild"), "CSV|*.csv") ?? Task.FromResult<string?>(null));
                if (string.IsNullOrEmpty(csvPath)) { AddLog(Loc.T("log.build.cancelled")); return; }
                await OpenFileAsync(csvPath);
                if (CurrentFormat != FileFormat.Csv) return; // open failed / not a CSV
            }

            if (_allRows.Count == 0) { StatusText = Loc.T("status.msg.noRowsToBuild"); return; }

            // Base/sample .locres — supplies the binary format (UE version) to pack into.
            var basePath = await (OpenFileRequested?.Invoke(
                Loc.T("pick.baseFormat"),
                "Locres|*.locres") ?? Task.FromResult<string?>(null));
            if (string.IsNullOrEmpty(basePath))
            {
                StatusText = Loc.T("status.msg.buildCancelledNoBase");
                AddLog(Loc.T("log.build.needsBase"), LogLevel.Warning);
                return;
            }

            // Snapshot the CURRENT grid (Target included, even unsaved) so edits are packed.
            var rows = _allRows.ToList();
            var commentSource = OriginalFilePath; // captured on the UI thread with the rows
            StatusText = Loc.T("status.msg.buildingLocres");
            var dest = await Task.Run(() => PackGridIntoLocres(rows, basePath!, commentSource));
            if (dest != null)
                await OpenFileAsync(dest); // open the built .locres to verify it
        }

        /// <summary>
        /// Load a file's rows into <c>_allRows</c>, hydrate Review from the sidecar,
        /// and run the initial validation modal. Returns true on success. Shared by
        /// a fresh open (OpenFileAsync → new tab) and a lazy tab activation
        /// (LoadIntoSessionAsync → existing tab). Does NOT touch recent-files /
        /// telemetry / the tab list — the caller owns that.
        /// </summary>
        internal async Task<bool> LoadFileCoreAsync(string path)
        {
            // Neutral baseline first, so anything this path does not assign cannot
            // arrive from the tab we are leaving. See ResetFileIdentity.
            ResetFileIdentity();
            OriginalFilePath = path;
            DetectedLanguageCode = DetectLanguageCode(path);

            var ext = Path.GetExtension(path).ToLowerInvariant();
            // Success = the file PARSED, not "has rows" — a valid empty csv/po/locres
            // must open as a real (empty) tab instead of being treated as a failure.
            bool ok = ext switch
            {
                ".locres" => await OpenLocresAsync(path),
                ".po" => OpenPoFile(path),
                ".csv" => OpenCsvFile(path),
                _ => false,
            };
            if (ext is not (".locres" or ".po" or ".csv"))
                StatusText = string.Format(Loc.T("status.msg.unsupportedFormat"), ext);
            if (!ok) return false;

            // Hydrate per-row Review / EditedAt from the AppData sidecar for THIS
            // file (after SetRows has populated _allRows). Then run the 6 light
            // validators in the modal; Cancel clears partial Problems.
            Services.ReviewService.Instance.LoadForFile(path);
            Services.ReviewService.Instance.Hydrate(_allRows);
            // Reviewer comments — separate per-file sidecar, keyed by row Key.
            Services.CommentService.Instance.LoadForFile(path);
            Services.CommentService.Instance.Hydrate(_allRows);
            // QC suggestions — per-file sidecar too, so an unfinished review
            // survives closing the app or rebooting.
            Services.Qc.QcService.Instance.LoadForFile(path);
            RefreshQcState();
            // SetRows ran ApplyFilter BEFORE the hydrate, so the review-mark
            // count (status-column visibility) must be refreshed now.
            ScheduleStatusCountsRefresh();
            bool cancelled = await RaiseInitialValidationAsync(_allRows);
            if (cancelled)
                foreach (var r in _allRows) r.Problems.Clear();
            return true;
        }

        /// <summary>
        /// Extract a likely UE locale code from a file path. Looks for the deepest
        /// path segment shaped like "en", "uk", "en-US", "pt-BR" — UE convention is
        /// `…/Localization/{namespace}/{locale}/{file}.locres`. Returns null if no
        /// match is found (project pill then hides the language segment).
        /// </summary>
        private static string? DetectLanguageCode(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var segs = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var rx = new System.Text.RegularExpressions.Regex(@"^[a-z]{2}(?:-[A-Z]{2})?$");
            // Walk from rightmost (deepest) towards root.
            for (int i = segs.Length - 2; i >= 0; i--)
            {
                if (rx.IsMatch(segs[i])) return segs[i];
            }
            return null;
        }

        /// <summary>
        /// Open .locres in-process via LocresIO. The parsed document skeleton is
        /// retained for the tab's lifetime (it is what saves write through), so
        /// there is no temp copy, no CSV intermediary and no external tool.
        /// Rows carry the REAL Namespace — pre-LocresIO opens flattened it into
        /// the key as "ns/key".
        /// </summary>
        private async Task<bool> OpenLocresAsync(string path)
        {
            StatusText = string.Format(Loc.T("status.msg.opening"), Path.GetFileName(path));
            OriginalLocresName = Path.GetFileName(path);
            CurrentLocresPath = path;

            try
            {
                var (doc, rows) = await Task.Run(() => LocresFileService.Open(path));

                // Merge flags belong to ONE file. Opening anything else drops them
                // rather than repainting an unrelated file's rows — see
                // _mergeFlagsForPath.
                if (!string.Equals(_mergeFlagsForPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _newKeySet.Clear();
                    _sourceChangedKeySet.Clear();
                    _mergeFlagsForPath = null;
                }
                else
                {
                    foreach (var r in rows)
                    {
                        var id = NewKeyId(r.Namespace, r.Key);
                        r.IsNewKey = _newKeySet.Contains(id);
                        r.IsSourceChanged = _sourceChangedKeySet.Contains(id);
                    }
                }

                CurrentLocresDocument = doc;
                DetectedLocresVersion = LocresFileService.VersionLabel(doc.Version);
                CurrentPoHeader = null;

                SetRows(rows);
                CurrentFileName = Path.GetFileName(path);
                CurrentFormat = FileFormat.Locres;
                HasUnsavedChanges = false;
                // Version is shown persistently in the project pill — keep it OUT of
                // this log line (AddLog mirrors to the 15 s status bar, and the user
                // doesn't want the locres version flashing there on every open).
                AddLog(string.Format(Loc.T("log.file.loaded"), RowCount, CurrentFileName), LogLevel.Info);
                return true; // parse success — a valid EMPTY file still opens
            }
            catch (Exception ex)
            {
                // LocresFormatException carries offset/section context in Message.
                StatusText = string.Format(Loc.T("status.msg.cannotOpen"), Path.GetFileName(path), ex.Message);
                AddLog(string.Format(Loc.T("log.file.openFailed"), ex.Message), LogLevel.Error);
                return false;
            }
        }

        /// <summary>
        /// Raw header msgstr of the active PO tab (Plural-Forms, Project-Id-Version, …),
        /// re-emitted on export so plural rules survive the round-trip. Null for
        /// locres/csv tabs. Per-tab: stashed on OpenFileSession across switches.
        /// </summary>
        public string? CurrentPoHeader { get; set; }

        /// <summary>Open .po file directly. Returns parse success (empty file = success).</summary>
        private bool OpenPoFile(string path)
        {
            StatusText = string.Format(Loc.T("status.msg.parsing"), Path.GetFileName(path));
            try
            {
                var rows = PoFileService.Import(path, out var poWarning, out var poHeader);
                CurrentPoHeader = poHeader;
                SetRows(rows);
                if (poWarning != null)
                    AddLog(string.Format(Loc.T("log.file.poWarning"), poWarning), LogLevel.Warning);
                CurrentLocresPath = null;
                CurrentLocresDocument = null;
                DetectedLocresVersion = null; // don't leak the previous tab's locres version into the pill
                CurrentFileName = Path.GetFileName(path);
                CurrentFormat = FileFormat.Po;
                HasUnsavedChanges = false;
                // Read in the same dispatcher turn that showed "Reading…": a log line
                // would only be its detail and the bar would keep saying "Reading…".
                StatusText = string.Format(Loc.T("log.file.loadedEntries"), RowCount, CurrentFileName);
                AddLog(StatusText, LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                StatusText = string.Format(Loc.T("status.msg.cannotOpen"), Path.GetFileName(path), ex.Message);
                AddLog(string.Format(Loc.T("log.file.openFailed"), ex.Message), LogLevel.Error);
                return false;
            }
        }

        /// <summary>Open .csv file directly (no locres needed). Returns parse success (empty file = success).</summary>
        private bool OpenCsvFile(string path)
        {
            StatusText = string.Format(Loc.T("status.msg.parsing"), Path.GetFileName(path));
            try
            {
                var rows = CsvFileService.Import(path, out var csvWarning);
                SetRows(rows);
                if (csvWarning != null)
                    AddLog(string.Format(Loc.T("log.file.csvWarning"), csvWarning), LogLevel.Warning);
                CurrentLocresPath = null;
                CurrentLocresDocument = null;
                DetectedLocresVersion = null; // don't leak the previous tab's locres version into the pill
                CurrentPoHeader = null;
                CurrentFileName = Path.GetFileName(path);
                CurrentFormat = FileFormat.Csv;
                HasUnsavedChanges = false;
                // Same turn as "Reading…" — see OpenPoFile.
                StatusText = string.Format(Loc.T("log.file.loaded"), RowCount, CurrentFileName);
                AddLog(StatusText, LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                StatusText = string.Format(Loc.T("status.msg.cannotOpen"), Path.GetFileName(path), ex.Message);
                AddLog(string.Format(Loc.T("log.file.openFailed"), ex.Message), LogLevel.Error);
                return false;
            }
        }

        // ============================ DUAL IMPORT ============================

        /// <summary>Load a second .locres as Target language, matching by Key.</summary>
        public async Task ImportTargetLocresAsync()
        {
            try
            {
                if (RowCount == 0) { StatusText = Loc.T("status.msg.openSourceFirst"); return; }

                StatusText = Loc.T("status.msg.selectTargetLocres");
                var path = await (OpenFileRequested?.Invoke(
                    Loc.T("pick.importTarget"),
                    "Locres files|*.locres") ?? Task.FromResult<string?>(null));
                if (path == null) { StatusText = Loc.T("status.msg.cancelled"); return; }

                StatusText = Loc.T("status.msg.readingTargetLocres");
                var targetDoc = await Task.Run(() => LocresDocument.Load(path));

                // Exact (ns, key) lookup, plus the legacy flattened "ns/key" form
                // so pre-LocresIO tabs (3-column CSVs whose Key still carries the
                // flattened string) keep matching.
                var exact = new Dictionary<(string Ns, string Key), string>();
                var flattened = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var ns in targetDoc.Namespaces)
                {
                    foreach (var e in ns.Entries)
                    {
                        if (e.Value.Length == 0) continue;
                        exact[(ns.Name, e.Key)] = e.Value;
                        flattened[ns.Name + "/" + e.Key] = e.Value;
                    }
                }

                int matched = 0, imported = 0, sameAsSource = 0, alreadyEqual = 0;
                // One undo step for the whole import (same pattern as the AI
                // batch pre-pass): capture manually under SuppressUndoCapture,
                // then push a single BatchEditAction.
                var importActions = new List<CellEditAction>();
                var touched = new List<TranslationRow>();
                SuppressUndoCapture = true;
                try
                {
                    foreach (var row in _allRows)
                    {
                        if (!exact.TryGetValue((row.Namespace ?? "", row.Key ?? ""), out var translated)
                            && !(string.IsNullOrEmpty(row.Namespace) && row.Key != null
                                 && flattened.TryGetValue(row.Key, out translated)))
                        {
                            continue;
                        }
                        matched++;

                        // A translation EQUAL to the source used to be skipped here,
                        // and that was wrong. Plenty of legitimate translations are
                        // identical to the original: tag-only strings the app carries
                        // rather than translates, numbers, proper nouns, and short UI
                        // words that are the same in both languages. Dropping them
                        // left the row with an empty Target, so it read as
                        // UNTRANSLATED — the counter lied, and AI Translate scoped to
                        // "untranslated" would have paid to translate "OK" into "OK"
                        // and to rewrite rows that are pure markup.
                        if (translated == row.Source) sameAsSource++;

                        if (translated == row.Target) { alreadyEqual++; continue; }   // no-op

                        importActions.Add(new CellEditAction(row, row.Target ?? "", translated));
                        row.Target = translated;
                        MarkTargetSnapshot(row, translated);
                        touched.Add(row);
                        imported++;
                    }
                }
                finally { SuppressUndoCapture = false; }

                if (importActions.Count > 0)
                {
                    UndoManager.AddAction(new BatchEditAction(importActions));
                    // Dirty only when something actually changed — a 0-match import
                    // used to flag unsaved changes with nothing to save.
                    HasUnsavedChanges = true;
                }

                // Validate what arrived. Bulk writes run under SuppressUndoCapture,
                // which skips the per-row debounced validation, so without this an
                // imported translation whose tags no longer match its source looked
                // clean until someone pressed F8.
                foreach (var row in touched)
                    Services.ValidationService.Instance.RunForRow(row);
                if (touched.Count > 0)
                    Services.ValidationService.Instance.RunHeavy(_allRows);

                ApplyFilter();
                ScheduleStatusCountsRefresh();

                // Say what happened in numbers the user can check, the way the merge
                // report does. Silence here is how "some rows stayed empty" went
                // unexplained for so long.
                int unmatchedHere = _allRows.Count - matched;
                StatusText = string.Format(Loc.T("import.status.done"), imported, Path.GetFileName(path));
                AddLog(string.Format(Loc.T("import.log.summary"),
                                     Path.GetFileName(path), matched, _allRows.Count, imported), LogLevel.Info);
                if (alreadyEqual > 0)
                    AddLog(string.Format(Loc.T("import.log.alreadyEqual"), alreadyEqual), LogLevel.Info);
                if (sameAsSource > 0)
                    AddLog(string.Format(Loc.T("import.log.sameAsSource"), sameAsSource), LogLevel.Info);
                if (unmatchedHere > 0)
                    AddLog(string.Format(Loc.T("import.log.unmatched"), unmatchedHere), LogLevel.Info);
                if (matched == 0)
                    AddLog(Loc.T("import.log.nothingMatched"), LogLevel.Warning);
            }
            catch (Exception ex)
            {
                StatusText = string.Format(Loc.T("status.msg.error"), ex.Message);
                Console.WriteLine($"[ERROR] ImportTargetLocresAsync: {ex}");
            }
        }

        // ============================ EXPORT / SAVE ============================

        /// <summary>
        /// Toolbar Save (floppy) — write the current data back in its ORIGINAL format
        /// (csv→csv, po→po, locres→locres) into a fresh timestamped Export_* folder.
        /// </summary>
        [RelayCommand]
        public async Task SaveCurrentFormatAsync()
        {
            if (_allRows.Count == 0) { StatusText = Loc.T("status.msg.noDataToSave"); return; }

            // .locres writes through the session's document skeleton (LocresIO).
            if (CurrentFormat == FileFormat.Locres)
            {
                if (CurrentLocresDocument == null)
                {
                    StatusText = Loc.T("status.msg.noBaseLocres");
                    return;
                }
                await Task.Run(() => SaveEditedData(true));
                return;
            }

            // csv / po → write directly into the Export folder (keeps the same format).
            try
            {
                var exportDir = CreateExportDir(true);
                var fileName = string.IsNullOrEmpty(CurrentFileName)
                    ? (CurrentFormat == FileFormat.Po ? "export.po" : "export.csv")
                    : CurrentFileName;
                var dest = Path.Combine(exportDir, fileName);

                if (CurrentFormat == FileFormat.Po)
                    PoFileService.Export(dest, _allRows, DetectedLanguageCode, CurrentPoHeader);
                else
                    CsvFileService.Export(dest, _allRows, _allRows.Any(r => !string.IsNullOrEmpty(r.Namespace)));

                // Carry reviewer comments alongside the exported copy so reopening
                // it keeps them (comments are keyed by file path).
                CommentService.Instance.MirrorTo(dest);

                HasUnsavedChanges = false;
                StatusText = string.Format(Loc.T("status.msg.savedTo"), Path.GetFileName(exportDir));
                AddLog(string.Format(Loc.T("log.save.done"), CurrentFormat, fileName, Path.GetFileName(exportDir)));
                OpenFolderRequested?.Invoke(exportDir);
            }
            catch (Exception ex)
            {
                StatusText = string.Format(Loc.T("status.msg.saveError"), ex.Message);
                AddLog(string.Format(Loc.T("log.save.error"), ex.Message), LogLevel.Error);
            }
        }

        /// <summary>
        /// Menu / Ctrl+E Export — produce a .locres. csv and locres → .locres (a csv with
        /// no base prompts for a sample .locres to pack the rows into); .po has no locres
        /// form in this workflow, so it is exported as .po.
        /// </summary>
        public async Task ExportLocresAsync()
        {
            if (_allRows.Count == 0) { StatusText = Loc.T("status.msg.nothingToExport"); return; }

            // PO stays PO.
            if (CurrentFormat == FileFormat.Po)
            {
                await SaveCurrentFormatAsync();
                return;
            }

            // A .locres source already carries its document skeleton — pack straight back.
            if (CurrentLocresDocument != null)
            {
                await Task.Run(() => SaveEditedData(true));
                return;
            }

            // CSV source → build a .locres from the CURRENT grid (key/source/target,
            // including unsaved edits) packed into a chosen base.
            var sample = await (OpenFileRequested?.Invoke(
                Loc.T("pick.baseBuild"),
                "Locres|*.locres") ?? Task.FromResult<string?>(null));
            if (string.IsNullOrEmpty(sample))
            {
                StatusText = Loc.T("status.msg.exportCancelledNoBase");
                return;
            }
            var rows = _allRows.ToList();
            var commentSource = OriginalFilePath;
            await Task.Run(() => PackGridIntoLocres(rows, sample!, commentSource));
        }

        /// <summary>
        /// Pack the given grid rows (key/source/TARGET) into the format of
        /// <paramref name="baseLocres"/> via LocresIO: the base document is loaded,
        /// the grid's TARGET column is overlaid onto it (empty targets keep the
        /// base value by contract), and the result is written atomically into a
        /// fresh Export_* folder. Rows match by (Namespace, Key) with a fallback
        /// to the legacy flattened "ns/key" Key form of older CSV exports.
        /// The base file itself is never modified. Runs on a background thread.
        /// Returns the written .locres path, or null on failure.
        /// </summary>
        private string? PackGridIntoLocres(IReadOnlyList<TranslationRow> rows, string baseLocres,
                                           string? commentSourcePath = null)
        {
            lock (_saveLock)
            {
                try
                {
                    var baseDoc = LocresDocument.Load(baseLocres);
                    var overrides = LocresFileService.BuildOverridesForBase(baseDoc, rows);

                    var exportDir = CreateExportDir(true);
                    var dest = Path.Combine(exportDir, Path.GetFileName(baseLocres));
                    var result = baseDoc.SaveWith(overrides, dest);

                    // Carry reviewer comments from the source CSV onto the built .locres —
                    // OpenSpreadsheetAsync reopens dest right after, so without this the
                    // comments would vanish on the CSV→locres build. Mirrored from the
                    // caller-captured path (this runs on a background thread — the live
                    // sidecar may already belong to another tab).
                    CommentService.Instance.MirrorFromTo(commentSourcePath, dest);

                    Ui(() =>
                    {
                        StatusText = string.Format(Loc.T("status.msg.built"), Path.GetFileName(dest), result.OverridesApplied, result.EntriesWritten);
                        OpenFolderRequested?.Invoke(exportDir);
                    });
                    AddLog(string.Format(Loc.T("log.build.done"), Path.GetFileName(dest), result.OverridesApplied, result.EntriesWritten), LogLevel.Info);
                    if (result.OverridesApplied == 0 && rows.Count > 0)
                        AddLog(Loc.T("log.build.noMatches"), LogLevel.Warning);
                    return dest;
                }
                catch (Exception ex)
                {
                    Ui(() => StatusText = string.Format(Loc.T("status.msg.buildError"), ex.Message));
                    AddLog(string.Format(Loc.T("log.build.error"), ex.Message), LogLevel.Error);
                    return null;
                }
            }
        }

        /// <summary>
        /// Create (and return) a fresh timestamped Export_* / AutoSaved_* output dir.
        /// Manual exports land under <see cref="AppConfig.ExportPath"/>, timer
        /// auto-saves under <see cref="AppConfig.AutoSavePath"/>;
        /// <paramref name="useAutosaveDir"/> forces the autosave folder for a
        /// manual save (the "Save As → Same as Autosave" behavior).
        /// </summary>
        private string CreateExportDir(bool isManualExport, bool useAutosaveDir = false)
        {
            string baseDir = isManualExport && !useAutosaveDir
                ? (string.IsNullOrWhiteSpace(_cfg.ExportPath) ? Path.Combine(ExeDir, "Export") : _cfg.ExportPath)
                : (string.IsNullOrWhiteSpace(_cfg.AutoSavePath) ? Path.Combine(ExeDir, "AutoSaved") : _cfg.AutoSavePath);
            var prefix = isManualExport ? "Export" : "AutoSaved";
            var exportDir = Path.Combine(baseDir, $"{prefix}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}");
            Directory.CreateDirectory(exportDir);
            return exportDir;
        }

        /// <summary>
        /// Auto-save a non-locres session in its OWN format (csv → csv, po → po) into a
        /// timestamped AutoSaved_* folder — never to .locres. Runs on the auto-save timer
        /// thread (same as SaveEditedData(false)). Returns true on success.
        /// </summary>
        private bool AutoSaveSameFormat()
        {
            // Runs on the auto-save TIMER thread. Snapshot rows + format + name in
            // ONE UI-thread hop (same contract as SaveEditedData): a tab switch
            // mutates them together, so reading them piecemeal from here could
            // export tab A's rows under tab B's name/format mid-switch.
            (List<TranslationRow> Rows, FileFormat Format, string? FileName, string? Lang, string? PoHeader) snap;
            try
            {
                (List<TranslationRow>, FileFormat, string?, string?, string?) Capture()
                    => (_allRows.ToList(), CurrentFormat, CurrentFileName, DetectedLanguageCode, CurrentPoHeader);
                snap = Avalonia.Threading.Dispatcher.UIThread.CheckAccess()
                    ? Capture()
                    : Avalonia.Threading.Dispatcher.UIThread.Invoke(Capture);
            }
            catch (Exception ex)
            {
                AddLog(string.Format(Loc.T("log.autosave.snapshotFailed"), ex.Message), LogLevel.Warning);
                return false;
            }
            if (snap.Rows.Count == 0 || snap.Format == FileFormat.None) return false;

            try
            {
                var exportDir = CreateExportDir(false);
                var fileName = string.IsNullOrEmpty(snap.FileName)
                    ? (snap.Format == FileFormat.Po ? "autosave.po" : "autosave.csv")
                    : snap.FileName;
                var dest = Path.Combine(exportDir, fileName);
                if (snap.Format == FileFormat.Po)
                    PoFileService.Export(dest, snap.Rows, snap.Lang, snap.PoHeader);
                else
                    CsvFileService.Export(dest, snap.Rows, snap.Rows.Any(r => !string.IsNullOrEmpty(r.Namespace)));
                // Deliberately do NOT clear HasUnsavedChanges: auto-save writes a
                // BACKUP copy — the user's original file is still stale, so the
                // unsaved dot and the close-confirm must keep protecting it.
                AddLog(string.Format(Loc.T("log.autosave.done"), snap.Format, Path.GetFileName(exportDir)));
                return true;
            }
            catch (Exception ex)
            {
                AddLog(string.Format(Loc.T("log.autosave.failed"), snap.Format, ex.Message), LogLevel.Warning);
                return false;
            }
        }

        /// <summary>
        /// Export CSV/PO with the two columns collapsed into ONE language:
        /// Target where translated, Source otherwise — the same rule .locres
        /// saving applies natively. Separate from <see cref="SaveAsAsync"/> on
        /// purpose: the ordinary two-column export stays untouched, and this one
        /// closes the QC loop for a single-language file (translation reviewed in
        /// Source, accepted fixes in Target, one corrected column out).
        /// </summary>
        public async Task SaveAsFlattenedAsync()
        {
            if (_allRows.Count == 0) { StatusText = Loc.T("status.msg.noData"); return; }

            var path = await (SaveFileRequested?.Invoke(
                Loc.T("pick.saveAsOneLanguage"), "CSV|*.csv|PO|*.po") ?? Task.FromResult<string?>(null));
            if (path == null) return;

            var flat = RowExport.Flatten(_allRows);
            int fromTarget = _allRows.Count(r => !string.IsNullOrEmpty(r.Target));

            try
            {
                switch (Path.GetExtension(path).ToLowerInvariant())
                {
                    case ".po":
                        PoFileService.Export(path, flat, DetectedLanguageCode, CurrentPoHeader);
                        break;
                    default:
                        CsvFileService.Export(path, flat, flat.Any(r => !string.IsNullOrEmpty(r.Namespace)));
                        break;
                }
                CommentService.Instance.MirrorTo(path);
                StatusText = string.Format(Loc.T("status.msg.savedFlattened"), Path.GetFileName(path));
                AddLog(string.Format(Loc.Plural("log.export.flattened", fromTarget), Path.GetFileName(path), fromTarget), LogLevel.Info);
            }
            catch (Exception ex)
            {
                StatusText = string.Format(Loc.T("status.msg.saveFailed"), ex.Message);
                AddLog(string.Format(Loc.T("log.export.flattenedFailed"), ex.Message), LogLevel.Error);
            }
        }

        /// <summary>Save As — user picks format (.csv, .po, .locres).
        /// File-type filters are ordered with AppConfig.DefaultExportFormat first.</summary>
        [RelayCommand]
        public async Task SaveAsAsync()
        {
            if (_allRows.Count == 0) { StatusText = Loc.T("status.msg.noData"); return; }

            // Silent behaviors: no dialog — save in the CURRENT format into the
            // configured folder (Settings → Save As behavior).
            if (_cfg.SaveAsBehavior != SaveAsBehavior.AlwaysAsk)
            {
                await SaveSilentAsync(_cfg.SaveAsBehavior == SaveAsBehavior.AutosaveFolder);
                return;
            }

            // Filter ordering driven by the user's default export format.
            // Format: "label|*.ext|label|*.ext|…" parsed by MainWindow.PickSaveFile.
            string csv = "CSV|*.csv";
            string po = "PO|*.po";
            string locres = "Locres|*.locres";
            string filter = _cfg.DefaultExportFormat switch
            {
                FileFormat.Csv => $"{csv}|{po}|{locres}",
                FileFormat.Po => $"{po}|{csv}|{locres}",
                _ => $"{locres}|{csv}|{po}",
            };

            var path = await (SaveFileRequested?.Invoke(
                Loc.T("action.saveAs"),
                filter) ?? Task.FromResult<string?>(null));
            if (path == null) return;

            var ext = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                switch (ext)
                {
                    case ".csv":
                        bool hasNamespace = _allRows.Any(r => !string.IsNullOrEmpty(r.Namespace));
                        CsvFileService.Export(path, _allRows, hasNamespace);
                        CommentService.Instance.MirrorTo(path);
                        HasUnsavedChanges = false; // manual save — same contract as the locres branch
                        StatusText = string.Format(Loc.T("status.msg.savedAs"), "CSV", Path.GetFileName(path));
                        break;

                    case ".po":
                        PoFileService.Export(path, _allRows, DetectedLanguageCode, CurrentPoHeader);
                        CommentService.Instance.MirrorTo(path);
                        HasUnsavedChanges = false;
                        StatusText = string.Format(Loc.T("status.msg.savedAs"), "PO", Path.GetFileName(path));
                        break;

                    case ".locres":
                        if (CurrentLocresDocument == null)
                        {
                            StatusText = Loc.T("status.msg.cannotSaveLocresNoBase");
                            return;
                        }
                        // The picked path is honored — the file is written exactly
                        // where the user chose (in-process, atomic). Background
                        // thread so a multi-MB write never sits on the dispatcher.
                        await Task.Run(() => SaveEditedData(true, path));
                        break;

                    default:
                        StatusText = string.Format(Loc.T("status.msg.unsupportedFormat"), ext);
                        break;
                }
            }
            catch (Exception ex)
            {
                StatusText = string.Format(Loc.T("status.msg.saveAsError"), ex.Message);
                AddLog(string.Format(Loc.T("log.saveAs.error"), ex.Message), LogLevel.Error);
            }
        }

        /// <summary>
        /// Silent Save As (behavior = Default Export Folder / Same as Autosave):
        /// save in the CURRENT format into the configured folder, no dialog.
        /// </summary>
        private async Task SaveSilentAsync(bool toAutosaveDir)
        {
            if (CurrentFormat == FileFormat.Locres)
            {
                if (CurrentLocresDocument == null)
                {
                    StatusText = Loc.T("status.msg.noBaseLocres");
                    return;
                }
                await Task.Run(() => SaveEditedData(true, null, toAutosaveDir));
                return;
            }

            try
            {
                var exportDir = CreateExportDir(true, toAutosaveDir);
                var fileName = string.IsNullOrEmpty(CurrentFileName)
                    ? (CurrentFormat == FileFormat.Po ? "export.po" : "export.csv")
                    : CurrentFileName;
                var dest = Path.Combine(exportDir, fileName);

                if (CurrentFormat == FileFormat.Po)
                    PoFileService.Export(dest, _allRows, DetectedLanguageCode, CurrentPoHeader);
                else
                    CsvFileService.Export(dest, _allRows, _allRows.Any(r => !string.IsNullOrEmpty(r.Namespace)));

                // Carry reviewer comments alongside the exported copy so reopening
                // it keeps them (comments are keyed by file path).
                CommentService.Instance.MirrorTo(dest);

                HasUnsavedChanges = false;
                StatusText = string.Format(Loc.T("status.msg.savedTo"), Path.GetFileName(exportDir));
                AddLog(string.Format(Loc.T("log.save.done"), CurrentFormat, fileName, Path.GetFileName(exportDir)));
                OpenFolderRequested?.Invoke(exportDir);
            }
            catch (Exception ex)
            {
                StatusText = string.Format(Loc.T("status.msg.saveError"), ex.Message);
                AddLog(string.Format(Loc.T("log.save.error"), ex.Message), LogLevel.Error);
            }
        }

        // SaveEditedData runs on a background thread (manual Save → Task.Run, and the
        // auto-save timer). Marshal UI-bound writes so we never poke bound properties off
        // the UI thread. (AddLog already posts to the UI thread itself.)
        private static void Ui(Action a) => Avalonia.Threading.Dispatcher.UIThread.Post(a);

        /// <summary>
        /// Save the active locres session: overlay the grid's translations onto the
        /// session's document skeleton (LocresIO SaveWith — atomic write, empty
        /// targets keep the base value) into a fresh Export_*/AutoSaved_* folder.
        /// Returns true only if the file was fully written; callers (auto-save)
        /// must use the return value before clearing the dirty flag. Holds
        /// <see cref="_saveLock"/> so concurrent auto-save and manual export calls
        /// serialise. Safe to call from any thread — the row snapshot is taken on
        /// the UI thread (rows are mutated there by the grid).
        /// </summary>
        /// <param name="isManualExport">Manual save clears the unsaved flag; timer auto-save keeps it.</param>
        /// <param name="explicitPath">When set (Save As → Always Ask), write the .locres to exactly this
        /// path instead of a timestamped export folder.</param>
        /// <param name="useAutosaveDir">Manual save routed into the autosave folder ("Same as Autosave").</param>
        public bool SaveEditedData(bool isManualExport, string? explicitPath = null, bool useAutosaveDir = false)
        {
            // Snapshot document + rows + file identity ATOMICALLY on the UI
            // thread — the grid mutates rows there, and a tab switch swaps all
            // three together, so capturing them in one hop can never mix tab A's
            // rows with tab B's document. The UI thread never blocks on a save,
            // so this synchronous hop cannot deadlock.
            // The tab and edit revision travel with the snapshot: the write finishes
            // later, on another thread, when the user may have edited again or
            // switched tabs — only THIS tab, and only if nothing changed since the
            // snapshot, may be marked clean (audit 2026-09 §5).
            (LocresDocument? Doc, Dictionary<(string Ns, string Key), string?> Overrides, string FileName, string? SourcePath, OpenFileSession? Session, long Revision) snap;
            try
            {
                (LocresDocument? Doc, Dictionary<(string Ns, string Key), string?> Overrides, string FileName, string? SourcePath, OpenFileSession? Session, long Revision) Capture()
                    => (CurrentLocresDocument, LocresFileService.BuildOverrides(_allRows), OriginalLocresName ?? "output.locres", OriginalFilePath, ActiveSession, _lastEditRevision);

                snap = Avalonia.Threading.Dispatcher.UIThread.CheckAccess()
                    ? Capture()
                    : Avalonia.Threading.Dispatcher.UIThread.Invoke(Capture);
            }
            catch (Exception ex)
            {
                AddLog(string.Format(Loc.T("log.save.snapshotFailed"), ex.Message), LogLevel.Error);
                return false;
            }
            if (snap.Doc == null) return false;

            lock (_saveLock)
            {
                try
                {
                    string dest, doneLabel, openDir;
                    if (explicitPath != null)
                    {
                        dest = explicitPath;
                        doneLabel = Path.GetFileName(explicitPath);
                        openDir = Path.GetDirectoryName(explicitPath) ?? "";
                    }
                    else
                    {
                        var exportDir = CreateExportDir(isManualExport, useAutosaveDir);
                        dest = Path.Combine(exportDir, snap.FileName);
                        doneLabel = Path.GetFileName(exportDir);
                        openDir = exportDir;
                    }

                    var result = snap.Doc.SaveWith(snap.Overrides, dest);

                    // Carry reviewer comments alongside the exported .locres too —
                    // from the SNAPSHOTTED source file, not the live sidecar: this
                    // runs on a background thread, and a tab switch mid-save would
                    // otherwise mirror the NEW tab's comments next to this export.
                    CommentService.Instance.MirrorFromTo(snap.SourcePath, dest);

                    if (isManualExport)
                    {
                        AddLog(string.Format(Loc.T("log.export.manual"), snap.FileName, doneLabel, result.OverridesApplied));
                        // Only a MANUAL save/export clears the unsaved flag — the timer
                        // auto-save writes a backup copy, so the dot + close-confirm keep
                        // protecting the in-memory edits.
                        Ui(() =>
                        {
                            MarkSavedIfUnchanged(snap.Session, snap.Revision);
                            if (openDir.Length > 0) OpenFolderRequested?.Invoke(openDir);
                        });
                    }
                    else
                    {
                        AddLog(string.Format(Loc.T("log.autosave.doneTo"), doneLabel, result.OverridesApplied));
                    }

                    Ui(() => StatusText = string.Format(Loc.T("status.msg.savedTo"), doneLabel));
                    return true;
                }
                catch (Exception ex)
                {
                    // Never let a save throw out from under the lock or vanish silently —
                    // surface it and report failure so the caller keeps the dirty flag.
                    Ui(() => StatusText = string.Format(Loc.T("status.msg.saveFailedNotSaved"), ex.Message));
                    AddLog(string.Format(Loc.T("log.save.failed"), ex.Message), LogLevel.Error);
                    return false;
                }
            } // _saveLock
        }

        // ============================ MERGE TO NEW VERSION ============================

        /// <summary>
        /// Carry translations into a new game version.
        ///
        /// <para>The result takes the NEW version's structure, format version and
        /// per-entry SourceStringHash — keys the update removed disappear, brand-new
        /// namespaces are included — and the grid shows the new original in Source
        /// with the carried translation in Target.</para>
        ///
        /// <para><b>A translation is carried only where it can be PROVEN to still
        /// belong.</b> A key match alone is not proof: the original English may have
        /// been reworded under the same key, and carrying the old line then ships
        /// text that says the wrong thing. So the key match is confirmed by the
        /// SourceStringHash (an old .locres) or by the source column (an old
        /// CSV/PO), and anything that fails is left untranslated — where the
        /// ordinary "Untranslated" scope picks it up. The looser key-only rule is
        /// available per run from the report, or permanently in Advanced settings.</para>
        /// </summary>
        public async Task MergeToNewVersionAsync()
        {
            try
            {
                // 1 — the old translated file. CSV/PO are accepted because they can
                // carry the ORIGINAL text alongside the translation, which a
                // localized .locres cannot.
                var oldPath = await (OpenFileRequested?.Invoke(
                    Loc.T("merge.pick.old"),
                    "Translated file|*.locres;*.csv;*.po") ?? Task.FromResult<string?>(null));
                if (oldPath == null) return;

                string ext = Path.GetExtension(oldPath).ToLowerInvariant();
                bool oldIsLocres = ext == ".locres";

                // 2 — say what will happen BEFORE the second file picker, in the
                // terms that apply to the file actually chosen.
                if (ConfirmRequested is not null)
                {
                    bool proceed = oldIsLocres
                        ? await ConfirmRequested(Loc.T("merge.info.locres.title"),
                                                 Loc.T("merge.info.locres.body"),
                                                 Loc.T("merge.continue"))
                        : await ConfirmRequested(Loc.T("merge.warn.text.title"),
                                                 Loc.T("merge.warn.text.body"),
                                                 Loc.T("merge.understand"));
                    if (!proceed) return;
                }

                // 3 — the new game version. Always a .locres: it is what ships.
                var newPath = await (OpenFileRequested?.Invoke(
                    Loc.T("merge.pick.new"), "Locres|*.locres") ?? Task.FromResult<string?>(null));
                if (newPath == null) return;

                // Picking one file twice is a slip, not a workflow: it "matches"
                // everything and produces the new version unchanged, which looks
                // like a successful merge and is not one.
                if (string.Equals(Path.GetFullPath(oldPath), Path.GetFullPath(newPath),
                                  StringComparison.OrdinalIgnoreCase))
                {
                    StatusText = Loc.T("merge.err.samefile");
                    AddLog(Loc.T("merge.err.samefile"), LogLevel.Warning);
                    return;
                }

                StatusText = Loc.T("merge.status.working");

                // 4 — read and match off the UI thread. Tab state is NOT touched
                // until this succeeds and the user has confirmed the report: a file
                // that fails to parse is the likeliest reason anyone runs this.
                var (newDoc, oldEntries, readWarning) = await Task.Run(() =>
                {
                    var nd = LocresDocument.Load(newPath);
                    string? warn = null;
                    List<MergeSource> src;
                    if (oldIsLocres)
                    {
                        src = MergeMatcher.FromDocument(LocresDocument.Load(oldPath));
                    }
                    else
                    {
                        var rows = ext == ".po"
                            ? PoFileService.Import(oldPath, out warn)
                            : CsvFileService.Import(oldPath, out warn);
                        src = MergeMatcher.FromRows(rows, out _);
                    }
                    return (nd, src, warn);
                });

                // A CSV that is neither BOM-marked nor valid UTF-8 is read as
                // Latin-1. Swallowing that here would be the worst place for it:
                // a mis-decoded source column matches nothing, and the merge would
                // quietly report that none of the translations could be carried.
                if (!string.IsNullOrEmpty(readWarning))
                    AddLog(string.Format(Loc.T("merge.log.readWarning"),
                                         Path.GetFileName(oldPath), readWarning), LogLevel.Warning);

                var verification = _cfg.MergeMatchByKeyOnly
                    ? MergeVerification.KeyOnly
                    : MergeMatcher.StrongestVerification(oldEntries);

                var match = await Task.Run(() => MergeMatcher.Match(newDoc, oldEntries, verification));

                // 5 — the counts, before anything is written. The strict rule can
                // legitimately match NOTHING (an old file whose hashes another tool
                // rebuilt looks 100% changed), and applying that silently would
                // replace a finished translation with an empty one.
                if (MergeReportRequested is not null)
                {
                    var choice = await MergeReportRequested(
                        match, Path.GetFileName(oldPath), Path.GetFileName(newPath));

                    if (choice == Controls.MergeReportChoice.Cancel)
                    {
                        StatusText = Loc.T("merge.status.cancelled");
                        AddLog(Loc.T("merge.log.cancelled"), LogLevel.Info);
                        return;
                    }
                    if (choice == Controls.MergeReportChoice.KeyOnly
                        && verification != MergeVerification.KeyOnly)
                    {
                        verification = MergeVerification.KeyOnly;
                        match = await Task.Run(() => MergeMatcher.Match(newDoc, oldEntries, verification));
                    }
                }

                // 6 — commit. Everything above this line is undone by doing nothing.
                CaptureActiveSession();
                ActiveSession = null;

                // Same neutral baseline a normal open starts from. This method
                // assembles a session by hand, so without it every field it does
                // not assign keeps the outgoing tab’s value.
                ResetFileIdentity();

                _newKeySet = new HashSet<string>(match.NewKeySet.Select(k => NewKeyId(k.Ns, k.Key)));
                _sourceChangedKeySet = new HashSet<string>(match.Rejected.Select(k => NewKeyId(k.Ns, k.Key)));
                _mergeFlagsForPath = newPath;   // these tints describe THIS file only

                // New version's original in Source, carried translation in Target —
                // the same two-column shape as every other file in the app, so the
                // counters, filters and QC all mean what they normally mean.
                var rows = new List<TranslationRow>(newDoc.EntryCount);
                foreach (var ns in newDoc.Namespaces)
                    foreach (var e in ns.Entries)
                        rows.Add(new TranslationRow
                        {
                            Namespace = ns.Name,
                            Key = e.Key,
                            Source = e.Value,
                            Target = match.Carried.GetValueOrDefault((ns.Name, e.Key), ""),
                            IsNewKey = match.NewKeySet.Contains((ns.Name, e.Key)),
                            IsSourceChanged = match.Rejected.Contains((ns.Name, e.Key)),
                        });

                // Skeleton = the NEW version's document. Saving writes the Targets
                // over it, so the result is the file the updated game ships.
                CurrentLocresDocument = newDoc;
                CurrentLocresPath = newPath;
                OriginalFilePath = newPath;
                OriginalLocresName = Path.GetFileName(newPath);
                DetectedLocresVersion = LocresFileService.VersionLabel(newDoc.Version);

                DetectedLanguageCode = DetectLanguageCode(newPath);

                SetRows(rows);

                // Hydrate review/comments from the NEW file's sidecars (rows are
                // fresh instances) so prior review work on this file is kept.
                ReviewService.Instance.LoadForFile(newPath);
                ReviewService.Instance.Hydrate(_allRows);
                CommentService.Instance.LoadForFile(newPath);
                CommentService.Instance.Hydrate(_allRows);

                // QC too — and this one was not cosmetic. QcService keeps a
                // CurrentFile that a QC run pins its results to; leaving it on the
                // previously-open file meant a review started after a merge wrote
                // its sidecar against the WRONG file.
                Services.Qc.QcService.Instance.LoadForFile(newPath);
                RefreshQcState();

                ScheduleStatusCountsRefresh();

                CurrentFileName = string.Format(Loc.T("merge.tabName"), match.CarriedRows);
                CurrentFormat = FileFormat.Locres;
                HasUnsavedChanges = true;

                int toTranslate = rows.Count - match.CarriedRows;
                StatusText = string.Format(Loc.T("merge.status.done"), match.CarriedRows, toTranslate);

                string modeName = verification switch
                {
                    MergeVerification.Hash => Loc.T("merge.log.mode.hash"),
                    MergeVerification.SourceText => Loc.T("merge.log.mode.source"),
                    _ => Loc.T("merge.log.mode.key"),
                };
                AddLog(string.Format(Loc.T("merge.log.summary"),
                                     Path.GetFileName(oldPath), Path.GetFileName(newPath),
                                     modeName, rows.Count, match.CarriedRows, toTranslate), LogLevel.Info);

                if (match.RejectedRows > 0)
                    AddLog(string.Format(Loc.T("merge.log.rejected"), match.RejectedRows), LogLevel.Warning);
                if (match.EmptyRows > 0)
                    AddLog(string.Format(Loc.T("merge.log.empty"), match.EmptyRows), LogLevel.Info);
                if (match.RemovedKeys > 0)
                    AddLog(string.Format(Loc.T("merge.log.removed"), match.RemovedKeys), LogLevel.Info);
                if (match.AmbiguousKeys > 0)
                    AddLog(string.Format(Loc.T("merge.log.ambiguous"), match.AmbiguousKeys), LogLevel.Warning);

                AddLog(Loc.T("merge.log.legend"), LogLevel.Info);

                // The merged tab takes over the NEW file's identity, so a tab already
                // open on that file is replaced — say so if it had unsaved edits,
                // rather than dropping them without a word.
                var existing = FindSession(newPath);
                if (existing != null)
                {
                    if (existing.HasUnsavedChanges)
                        AddLog(string.Format(Loc.T("merge.log.replacedTab"), Path.GetFileName(newPath)),
                               LogLevel.Warning);
                    OpenSessions.Remove(existing);
                }
                CommitLoadedSession(newPath);

                // Validate, exactly as opening a file does — and here it matters
                // more than usual: every carried translation is now paired with the
                // NEW original, so a tag or placeholder the update changed shows up
                // as a real problem instead of waiting for someone to press F8.
                if (await RaiseInitialValidationAsync(_allRows))
                    foreach (var r in _allRows) r.Problems.Clear();   // cancelled

                // The result exists only in memory until it is written: a restart
                // restores this tab by RE-READING the new version file, which has no
                // translations in it at all.
                AddLog(Loc.T("merge.log.saveFirst"), LogLevel.Warning);
            }
            catch (Exception ex)
            {
                // A 15-second status line is not a record. This runs once per game
                // update, on files that may be damaged — the reason it failed has to
                // survive in the log.
                StatusText = string.Format(Loc.T("status.msg.error"), ex.Message);
                AddLog(string.Format(Loc.T("merge.log.failed"), ex.Message), LogLevel.Error);
                Console.WriteLine($"[ERROR] MergeToNewVersionAsync: {ex}");
            }
        }
    }
}
