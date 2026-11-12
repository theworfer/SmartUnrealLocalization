using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.Input;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using SmartUnrealLocalization.Utils;
using SmartUnrealLocalization.Localization;

namespace SmartUnrealLocalization.ViewModels
{
    // Clipboard + bulk-selection operations: copy/paste/cut/clear of Source and Target
    // columns over the currently-checked rows, plus the single-shot CopySourceToTarget and
    // CopyAllTable commands. Operates strictly on the unfiltered AllRows so an active filter
    // can never silently drop data from a bulk action.
    public partial class MainWindowViewModel
    {
        public void CopySourceToTarget(IList<TranslationRow> selected)
        {
            var actions = new List<CellEditAction>();
            _suppressUndoCapture = true;
            try
            {
                foreach (var r in selected)
                {
                    if (r.Target != r.Source)
                    {
                        actions.Add(new CellEditAction(r, r.Target, r.Source));
                        r.Target = r.Source;
                        _targetSnapshot[r] = r.Source;
                    }
                }
            }
            finally { _suppressUndoCapture = false; }

            if (actions.Count > 0)
            {
                UndoManager.AddAction(new BatchEditAction(actions));
                HasUnsavedChanges = true;
                StatusText = string.Format(Loc.Plural("status.msg.copiedSourceToTarget", actions.Count), actions.Count);
            }
        }

        private IClipboard? GetClipboard()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                return desktop.MainWindow?.Clipboard;
            }
            return null;
        }

        /// <summary>Test seam: replaces the system clipboard's write (true = written).</summary>
        internal Func<string, Task<bool>>? ClipboardWriteOverride { get; set; }

        /// <summary>
        /// Write the clipboard through <see cref="ClipboardSafe"/>; on failure the user is
        /// told (another program holds the clipboard) and the caller gets false.
        /// </summary>
        internal async Task<bool> WriteClipboardAsync(string text)
        {
            bool ok = ClipboardWriteOverride != null
                ? await ClipboardWriteOverride(text)
                : await ClipboardSafe.TrySetTextAsync(GetClipboard(), text);
            if (!ok) AddLog(Loc.T("status.msg.clipboardBusy"), LogLevel.Warning);
            return ok;
        }

        /// <summary>Test seam: replaces the system clipboard's read.</summary>
        internal Func<Task<(bool Ok, string? Text)>>? ClipboardReadOverride { get; set; }

        private async Task<(bool Ok, string? Text)> ReadClipboardAsync()
        {
            var (ok, text) = ClipboardReadOverride != null
                ? await ClipboardReadOverride()
                : await ClipboardSafe.TryGetTextAsync(GetClipboard());
            if (!ok) AddLog(Loc.T("status.msg.clipboardBusy"), LogLevel.Warning);
            return (ok, text);
        }

        // ───────────── Column header menu (Copy / Cut / Paste / Clear column) ─────────────
        // These work on the rows the grid SHOWS, top to bottom — the column the user
        // right-clicked. Each takes one snapshot of that list first: writing a Target
        // can make a row leave an active filter, and walking the live collection while
        // it shrinks skipped rows (or threw) half-way through.

        /// <summary>Copy one column of the visible rows; true when it reached the clipboard.</summary>
        internal async Task<bool> CopyColumnAsync(string column)
        {
            var rows = Rows.ToList();
            var values = rows.Select(r => EscapeForClipboard(
                column == "Target" ? r.Target : column == "Source" ? r.Source : r.Key));
            return await WriteClipboardAsync(string.Join(Environment.NewLine, values));
        }

        /// <summary>Paste the clipboard over the visible rows' targets, one line per row.</summary>
        internal async Task PasteTargetColumnAsync()
        {
            var (ok, text) = await ReadClipboardAsync();
            if (!ok || string.IsNullOrEmpty(text)) return;

            var rows = Rows.ToList();
            string[] lines = TextUtils.ParseClipboardLines(text, rows.Count);
            if (lines.Length != rows.Count)
            {
                StatusText = string.Format(Loc.T("status.msg.pasteRowCountMismatch"), lines.Length, rows.Count);
                return;
            }
            SetTargets(rows, i => lines[i]);
        }

        /// <summary>Empty the visible rows' targets.</summary>
        internal void ClearTargetColumn() => SetTargets(Rows.ToList(), _ => "");

        /// <summary>Cut = copy, then clear — and only when the copy really happened.</summary>
        internal async Task CutTargetColumnAsync()
        {
            if (await CopyColumnAsync("Target")) ClearTargetColumn();
        }

        /// <summary>Write new targets as ONE undo step.</summary>
        private void SetTargets(IReadOnlyList<TranslationRow> rows, Func<int, string> valueFor)
        {
            var actions = new List<CellEditAction>();
            _suppressUndoCapture = true;
            try
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    string value = valueFor(i);
                    string old = r.Target ?? "";
                    if (old == value) continue;
                    actions.Add(new CellEditAction(r, old, value));
                    r.Target = value;
                    _targetSnapshot[r] = value;
                }
            }
            finally { _suppressUndoCapture = false; }

            if (actions.Count == 0) return;
            UndoManager.AddAction(new BatchEditAction(actions));
            MarkDirty();
        }

        // CSV-quote a cell for the clipboard. Shared with the code-behind copy
        // paths via TextUtils so a copied column round-trips to the same row count.
        private static string EscapeForClipboard(string? val) => TextUtils.EscapeForClipboard(val);

        [RelayCommand]
        private async Task CopySelectedSourceAsync()
        {
            var selected = SelectedRows.Select(r => EscapeForClipboard(r.Source)).ToList();
            if (selected.Count == 0) return;
            if (!await WriteClipboardAsync(string.Join(Environment.NewLine, selected))) return;
            AddLog(string.Format(Loc.Plural("log.clipboard.copiedSource", selected.Count), selected.Count));
        }

        [RelayCommand]
        private Task CopySelectedTargetAsync() => CopySelectedTargetCoreAsync();

        /// <summary>True when the checked rows' targets are on the clipboard.</summary>
        private async Task<bool> CopySelectedTargetCoreAsync()
        {
            var selected = SelectedRows.Select(r => EscapeForClipboard(r.Target)).ToList();
            if (selected.Count == 0) return false;
            if (!await WriteClipboardAsync(string.Join(Environment.NewLine, selected))) return false;
            AddLog(string.Format(Loc.Plural("log.clipboard.copiedTarget", selected.Count), selected.Count));
            return true;
        }

        [RelayCommand]
        private async Task PasteSelectedTargetAsync()
        {
            var (ok, text) = await ReadClipboardAsync();
            if (!ok || string.IsNullOrEmpty(text)) return;

            var selected = SelectedRows;
            string[] lines = TextUtils.ParseClipboardLines(text, selected.Count);

            if (lines.Length != selected.Count)
            {
                AddLog(string.Format(Loc.T("log.clipboard.pasteMismatch"), lines.Length, selected.Count), LogLevel.Error);
                return;
            }

            var actions = new List<CellEditAction>();
            _suppressUndoCapture = true;
            try
            {
                for (int i = 0; i < selected.Count; i++)
                {
                    if (selected[i].Target != lines[i])
                    {
                        actions.Add(new CellEditAction(selected[i], selected[i].Target, lines[i]));
                        selected[i].Target = lines[i];
                        _targetSnapshot[selected[i]] = lines[i];
                    }
                }
            }
            finally { _suppressUndoCapture = false; }

            if (actions.Count > 0)
            {
                UndoManager.AddAction(new BatchEditAction(actions));
                HasUnsavedChanges = true;
                AddLog(string.Format(Loc.Plural("log.clipboard.pasted", actions.Count), actions.Count));
            }
        }

        [RelayCommand]
        private async Task CutSelectedTargetAsync()
        {
            // Clear only what actually reached the clipboard: a busy clipboard used
            // to leave the targets erased with nothing to paste back.
            if (await CopySelectedTargetCoreAsync()) ClearSelectedTarget();
        }

        [RelayCommand]
        private void ClearSelectedTarget()
        {
            var selected = SelectedRows;
            var actions = new List<CellEditAction>();
            _suppressUndoCapture = true;
            try
            {
                foreach (var r in selected)
                {
                    if (!string.IsNullOrEmpty(r.Target))
                    {
                        actions.Add(new CellEditAction(r, r.Target, ""));
                        r.Target = "";
                        _targetSnapshot[r] = "";
                    }
                }
            }
            finally { _suppressUndoCapture = false; }

            if (actions.Count > 0)
            {
                UndoManager.AddAction(new BatchEditAction(actions));
                HasUnsavedChanges = true;
                AddLog(string.Format(Loc.Plural("log.clipboard.clearedTarget", actions.Count), actions.Count));
            }
        }

        [RelayCommand]
        private async Task CopyAllTableAsync()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Key\tSource\tTarget");
            foreach (var r in _allRows)
                sb.AppendLine($"{EscapeForClipboard(r.Key)}\t{EscapeForClipboard(r.Source)}\t{EscapeForClipboard(r.Target)}");
            if (!await WriteClipboardAsync(sb.ToString())) return;
            AddLog(Loc.T("log.clipboard.copiedTable"));
        }
    }
}
