using System.Collections.Generic;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;

namespace SmartUnrealLocalization.ViewModels
{
    /// <summary>
    /// One open file = one tab (Etap 13). Holds the per-tab state the
    /// MainWindowViewModel swaps in/out when the active tab changes: the live
    /// row instances (with their edits / Review / Problems), the undo-history
    /// snapshot, the unsaved flag, and the file's path / format / metadata.
    ///
    /// <para>
    /// Bindable bits (IsActive / IsPinned / HasUnsavedChanges / DisplayName)
    /// drive the <c>FileTabsBar</c>. The heavy bits (Rows / UndoState) are plain
    /// fields — they're captured on switch-away and restored on switch-to.
    /// </para>
    /// </summary>
    public partial class OpenFileSession : ObservableObject
    {
        /// <summary>User-selected path (re-openable).</summary>
        public string Path { get; init; } = "";

        // File-identity / metadata captured at open; stable for the tab's lifetime.
        public FileFormat Format { get; set; }
        public string? CurrentLocresPath { get; set; }
        public string? OriginalLocresName { get; set; }
        public string CsvFile { get; set; } = "";
        public string? DetectedLanguageCode { get; set; }
        public string? DetectedLocresVersion { get; set; }

        /// <summary>Raw PO header msgstr (Plural-Forms etc.) — re-emitted on export.
        /// Null for locres/csv tabs. Immutable after open, so it's set at commit
        /// time only (no capture on switch-away needed).</summary>
        public string? PoHeader { get; set; }

        /// <summary>
        /// Parsed .locres skeleton for this tab (version, entry order, per-entry
        /// SourceStringHash). Every save writes through it via SaveWith, so the
        /// tab can ALWAYS save back to .locres — no temp copy of the original
        /// file, no external tool. Null for .po/.csv tabs.
        /// </summary>
        public Locres.IO.LocresDocument? Document { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayName))]
        private string _fileName = "";

        [ObservableProperty] private bool _isActive;
        [ObservableProperty] private bool _isPinned;
        [ObservableProperty] private bool _hasUnsavedChanges;

        /// <summary>
        /// False for a tab restored from last session that hasn't been opened
        /// yet (lazy): only its header shows; the file loads + validates on first
        /// activation. True once <c>Rows</c>/metadata are populated.
        /// </summary>
        [ObservableProperty] private bool _isLoaded;

        /// <summary>Live rows for this tab (the model instances, edits and all).</summary>
        public List<TranslationRow> Rows { get; set; } = new();

        /// <summary>The tab's edit revision when it was last made inactive — what a
        /// save finishing after a tab switch is compared against (SaveCompletion).</summary>
        public long LastEditRevision { get; set; }

        /// <summary>Undo/redo history for this tab, stashed while it's inactive.</summary>
        public UndoRedoManager.Snapshot? UndoState { get; set; }

        public string DisplayName =>
            string.IsNullOrEmpty(FileName)
                ? (string.IsNullOrEmpty(Path) ? "untitled" : System.IO.Path.GetFileName(Path))
                : FileName;
    }
}
