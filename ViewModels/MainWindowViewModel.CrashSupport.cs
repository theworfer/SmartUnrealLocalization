using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SmartUnrealLocalization.Localization;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  MainWindowViewModel — what Services/CrashReporter may ask of the app
//
//  Three read-only views, all called on the UI thread: a one-screen description
//  of what was open (file names and counts — never row text), the last log
//  lines, and the rescue of unsaved edits when the process is about to die.
// ─────────────────────────────────────────────────────────────────────────────

namespace SmartUnrealLocalization.ViewModels
{
    public partial class MainWindowViewModel
    {
        public string DescribeForCrashReport()
        {
            var sb = new StringBuilder();
            int unsavedTabs = OpenSessions.Count(s => ReferenceEquals(s, ActiveSession) ? HasUnsavedChanges : s.HasUnsavedChanges);
            sb.AppendLine($"Open tabs: {OpenSessions.Count} (with unsaved edits: {unsavedTabs})");
            sb.AppendLine(HasData
                ? $"Active file: {CurrentFileName} ({CurrentFormat}{(string.IsNullOrEmpty(DetectedLocresVersion) ? "" : ", " + DetectedLocresVersion)}), {RowCount} rows, {FilteredRowCount} shown, unsaved: {HasUnsavedChanges}"
                : "Active file: none");
            sb.AppendLine($"Bottom panel: {(ShowLogs ? ActiveBottomTab.ToString() : "closed")}; find bar: {(IsFindBarOpen ? (FindBarReplaceMode ? "replace" : "find") : "closed")}");
            var panels = new List<string>();
            if (ShowAiPanel) panels.Add("AI Translate");
            if (ShowMultiTranslatePanel) panels.Add("Multi-translate");
            if (ShowQcPanel) panels.Add("QC");
            if (ShowSettingsPanel) panels.Add("Settings");
            if (ShowStatusPanel) panels.Add("Status");
            if (ShowPromptHistoryPanel) panels.Add("Prompt history");
            sb.AppendLine($"Side panel: {(panels.Count == 0 ? "none" : string.Join(", ", panels))}");
            sb.AppendLine($"Checked rows: {SelectedCount}; validator problems: {ProblemTotalCount}; QC suggestions pending: {QcPendingCount}");
            sb.AppendLine($"UI: {Loc.Instance.CurrentLanguage}, {(AppConfig.Instance.IsDarkTheme ? "dark" : "light")} theme");
            return sb.ToString();
        }

        public IReadOnlyList<string> RecentLogLinesForCrashReport()
            => Logs.TakeLast(25)
                   .Select(m => $"[{m.DisplayTimestamp}] {m.Level}: {(m.Message.Length > 200 ? m.Message[..200] + "…" : m.Message)}")
                   .ToList();

        /// <summary>
        /// Write every tab's unsaved edits to a fresh <c>Recovered_*</c> folder
        /// beside the auto-saves, in the tab's own format, through the ordinary
        /// save code. Returns the files written. Each tab is attempted on its own
        /// — one failing does not cost the others.
        /// </summary>
        public IReadOnlyList<string> RecoverUnsavedEdits()
        {
            var written = new List<string>();
            string baseDir = string.IsNullOrWhiteSpace(_cfg.AutoSavePath) ? Path.Combine(ExeDir, "AutoSaved") : _cfg.AutoSavePath;
            string dir = Path.Combine(baseDir, $"Recovered_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}");
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // The active tab lives in the VM's own fields; its session object is
            // only refreshed on a tab switch, so it must not be read for this one.
            if (HasUnsavedChanges && _allRows.Count > 0)
                TryRecover(_allRows, CurrentFormat, CurrentLocresDocument,
                           OriginalLocresName ?? CurrentFileName, DetectedLanguageCode, CurrentPoHeader);

            foreach (var s in OpenSessions)
            {
                if (ReferenceEquals(s, ActiveSession) || !s.HasUnsavedChanges || !s.IsLoaded || s.Rows.Count == 0) continue;
                TryRecover(s.Rows, s.Format, s.Document, s.OriginalLocresName ?? s.FileName, s.DetectedLanguageCode, s.PoHeader);
            }
            return written;

            void TryRecover(List<TranslationRow> rows, FileFormat format, Locres.IO.LocresDocument? doc,
                            string? name, string? lang, string? poHeader)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    string fileName = string.IsNullOrWhiteSpace(name) ? "recovered" : Path.GetFileName(name);
                    // A .locres without its skeleton cannot be rebuilt; keep the edits as CSV.
                    if (format == FileFormat.Locres && doc == null) fileName = Path.ChangeExtension(fileName, ".csv");
                    string unique = fileName;
                    for (int n = 2; !usedNames.Add(unique); n++)
                        unique = $"{Path.GetFileNameWithoutExtension(fileName)} ({n}){Path.GetExtension(fileName)}";
                    string dest = Path.Combine(dir, unique);

                    if (format == FileFormat.Locres && doc != null)
                        doc.SaveWith(LocresFileService.BuildOverrides(rows), dest);
                    else if (format == FileFormat.Po)
                        PoFileService.Export(dest, rows, lang, poHeader);
                    else
                        CsvFileService.Export(dest, rows, rows.Any(r => !string.IsNullOrEmpty(r.Namespace)));
                    written.Add(dest);
                }
                catch { /* the report says what was recovered; a failure here must not mask the crash */ }
            }
        }

        /// <summary>
        /// Tell the user, once, about crashes and freezes of earlier sessions:
        /// when, where the report is, and where unsaved edits were rescued to.
        /// </summary>
        public void AnnouncePreviousSessionProblems()
        {
            foreach (var r in CrashReporter.TakeUnseenReports())
            {
                string when = r.Time.ToString("yyyy-MM-dd HH:mm");
                AddLog(string.Format(Loc.T(r.Kind == "hang" ? "log.crash.previousHang" : "log.crash.previousCrash"), when, r.Path), LogLevel.Warning);
                foreach (var file in r.Recovered)
                    AddLog(string.Format(Loc.T("log.crash.recovered"), file), LogLevel.Warning);
            }
        }
    }
}
