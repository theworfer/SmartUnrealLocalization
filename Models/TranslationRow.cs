using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SmartUnrealLocalization.Models
{
    /// <summary>
    /// Universal data model for all localization formats: .locres, .po, .csv.
    /// Supports Namespace (required for .po / complex .locres), Source/Target split,
    /// and state tracking for UI filtering (new keys, untranslated, errors).
    /// </summary>
    public partial class TranslationRow : ObservableObject
    {
        public TranslationRow()
        {
            Problems = new ObservableCollection<Problem>();
            Problems.CollectionChanged += (_, _) =>
            {
                // Any change to the Problems set may flip Severity / HasFixableProblem /
                // LeftEdgeBrush. Notify them so the gutter, status icon, lightbulb,
                // and Problems-tab counts all stay live.
                OnPropertyChanged(nameof(Severity));
                OnPropertyChanged(nameof(HasFixableProblem));
                OnPropertyChanged(nameof(LeftEdgeBrush));
                // HasTokenError / HasTokenWarning / TokenErrorMessage /
                // TokenWarningMessage are computed views over Problems (post-Etap-2
                // migration). The status-icon column, RowBackground, and the
                // CellRecovered / TagReplaced count filters all read these names.
                OnPropertyChanged(nameof(HasTokenError));
                OnPropertyChanged(nameof(HasTokenWarning));
                OnPropertyChanged(nameof(TokenErrorMessage));
                OnPropertyChanged(nameof(TokenWarningMessage));
                OnPropertyChanged(nameof(RowBackground));
                // Status-icon column (Etap 3) — glyph/colour/tooltip all change
                // when severity changes.
                OnPropertyChanged(nameof(StatusIconGlyph));
                OnPropertyChanged(nameof(StatusIconBrush));
                OnPropertyChanged(nameof(StatusIconTooltip));
                RaiseStatusIconStateChanged();
            };
        }

        // Per-state flags driving the vector status-icon column (one icon shown
        // at a time; severity wins over review, matching StatusIconGlyph).
        public bool StatusShowError => Severity == ProblemSeverity.Error;
        public bool StatusShowWarning => Severity == ProblemSeverity.Warning;
        public bool StatusShowInfo => Severity == ProblemSeverity.Info;
        public bool StatusShowApproved => !StatusShowError && !StatusShowWarning && !StatusShowInfo && Review == ReviewStatus.Approved;
        public bool StatusShowRejected => !StatusShowError && !StatusShowWarning && !StatusShowInfo && Review == ReviewStatus.Rejected;

        private void RaiseStatusIconStateChanged()
        {
            OnPropertyChanged(nameof(StatusShowError));
            OnPropertyChanged(nameof(StatusShowWarning));
            OnPropertyChanged(nameof(StatusShowInfo));
            OnPropertyChanged(nameof(StatusShowApproved));
            OnPropertyChanged(nameof(StatusShowRejected));
        }

        /// <summary>Checkbox selection state.</summary>
        [ObservableProperty] private bool _isSelected;

        /// <summary>Native grid focus row: visual only, separate from checkbox selection.</summary>
        [ObservableProperty] private bool _isFocusedRow;

        /// <summary>Mouse hover row: visual only, tracked manually so it survives native grid focus changes.</summary>
        [ObservableProperty] private bool _isHoveredRow;

        /// <summary>
        /// True when this row sits at an odd index in the visible <c>Rows</c>
        /// collection (zebra striping). View-only flag — written by
        /// <c>MainWindowViewModel.ApplyAltRowFlags</c> after every filter/sort
        /// rebuild. Lowest priority colour in <see cref="RowBackground"/>.
        /// </summary>
        [ObservableProperty] private bool _isAltRow;

        /// <summary>Namespace from .locres or msgctxt from .po. Empty for flat CSV.</summary>
        [ObservableProperty] private string _namespace = "";

        /// <summary>Unique key identifying this string entry.</summary>
        [ObservableProperty] private string _key = "";

        /// <summary>Original (source language) text. Read-only in UI.</summary>
        [ObservableProperty] private string _source = "";

        /// <summary>Translated (target language) text. Editable in UI.</summary>
        [ObservableProperty] private string _target = "";

        /// <summary>
        /// True only while the Target cell is in inline-edit mode.
        /// Drives the TaggedTextBlock ↔ TextBox swap inside the Target CellTemplate.
        /// Set by MainWindow.StartEditingTarget (double-tap / F2), cleared on commit/cancel.
        /// </summary>
        [ObservableProperty] private bool _isEditing;

        /// <summary>
        /// Buffer the inline editor writes into while IsEditing == true.
        /// On commit we copy this back into Target as a single mutation
        /// (one undo entry per edit session). On cancel we discard it.
        /// </summary>
        [ObservableProperty] private string _editValue = "";

        /// <summary>
        /// Opaque per-entry payload a file format attaches at import so export can
        /// round-trip data the grid doesn't edit (PO: translator comments, flags
        /// incl. fuzzy, plural forms — see <c>PoFileService.PoEntryMeta</c>).
        /// Not observable, never shown in UI, never persisted outside the file.
        /// </summary>
        public object? FormatMeta { get; set; }

        /// <summary>Whether this key was introduced during a merge (new version key).</summary>
        [ObservableProperty] private bool _isNewKey;

        /// <summary>
        /// Set by "Merge to new version" when this key's SourceStringHash differs
        /// between the old and new game files — the source text changed, so the
        /// carried translation likely needs a re-check.
        /// </summary>
        [ObservableProperty] private bool _isSourceChanged;

        // HasTokenError / HasTokenWarning / TokenErrorMessage / TokenWarningMessage
        // used to be backing fields with setters that AI translation and CheckTags
        // mutated directly. As of Etap 2 they are computed views over the new
        // Problems collection — single-writer is ValidationService. Existing
        // bindings ("Status icon column" in MainWindow.axaml, the
        // CellRecoveredCount / TagReplacedCount counts in MainWindowViewModel)
        // keep working unchanged because the property names and types are
        // preserved; PropertyChanged for them is fired from the constructor's
        // Problems.CollectionChanged subscription.

        /// <summary>
        /// True when the chevron-expand button should appear at the right edge
        /// of the Target cell. Two triggers: either the text is clipped right
        /// now in no-wrap mode (the user needs a way to see the rest) or the
        /// row is already expanded (the user needs a way to collapse it back).
        /// In global-Expand mode <see cref="IsClipped"/> stays false because
        /// rows grow to fit, so the chevron hides unless this specific row was
        /// also expanded — which would be a no-op visually but the user can
        /// still toggle it off.
        /// </summary>
        public bool ShowExpandChevron => IsClipped || IsExpanded;

        /// <summary>True iff any TagMismatch Problem is currently attached.</summary>
        public bool HasTokenError => Problems.Any(p => p.Kind == "TagMismatch");

        /// <summary>Title of the first TagMismatch Problem (used as tooltip).</summary>
        public string TokenErrorMessage
            => Problems.FirstOrDefault(p => p.Kind == "TagMismatch")?.Title ?? "";

        /// <summary>True iff any TokenMismatch Problem is currently attached.</summary>
        public bool HasTokenWarning => Problems.Any(p => p.Kind == "TokenMismatch");

        /// <summary>Title of the first TokenMismatch Problem (used as tooltip).</summary>
        public string TokenWarningMessage
            => Problems.FirstOrDefault(p => p.Kind == "TokenMismatch")?.Title ?? "";

        // ─── Etap 1 additions ──────────────────────────────────────────────

        /// <summary>
        /// All validator findings attached to this row. Managed by
        /// <c>Services.ValidationService</c> from Etap 2 onward. Empty by
        /// default. Changes drive <see cref="Severity"/>, <see cref="HasFixableProblem"/>,
        /// and <see cref="LeftEdgeBrush"/> via the constructor's CollectionChanged
        /// subscription.
        /// </summary>
        public ObservableCollection<Problem> Problems { get; }

        /// <summary>
        /// Reviewer-set status. <c>None</c> by default; <c>Approved</c>/<c>Rejected</c>
        /// set via row context menu (Etap 11) and persisted in the per-file
        /// review sidecar by <c>Services.ReviewService</c>.
        /// </summary>
        [ObservableProperty] private ReviewStatus _review = ReviewStatus.None;

        /// <summary>
        /// Reviewer's free-text note for this row (context / question / instruction).
        /// null = no comment. Persisted in a per-file sidecar by
        /// <see cref="Services.CommentService"/>, keyed by <see cref="Key"/> — it is
        /// metadata, NEVER written into the exported .locres/.po/.csv. Distinct from
        /// <see cref="Review"/> (separate field, separate sidecar) and, unlike Review,
        /// editing the Target does NOT clear it.
        /// </summary>
        [ObservableProperty] private string? _comment;

        public bool HasComment => !string.IsNullOrWhiteSpace(Comment);

        /// <summary>Drives the comment indicator: always shown when a comment exists,
        /// faintly revealed on row hover otherwise, invisible at rest (so it never
        /// shifts layout or distracts). Hovering the icon itself bumps it to 1 via a style.</summary>
        public double CommentIconOpacity => HasComment ? 1.0 : (IsHoveredRow ? 0.7 : 0.0);

        public string CommentTooltip => HasComment
            ? Localization.Loc.T("comment.edit")
            : Localization.Loc.T("comment.add");

        partial void OnCommentChanged(string? value)
        {
            OnPropertyChanged(nameof(HasComment));
            OnPropertyChanged(nameof(CommentIconOpacity));
            OnPropertyChanged(nameof(CommentTooltip));
        }

        /// <summary>
        /// Pending QC suggestion for this row (null = nothing proposed). Set by
        /// <see cref="Services.Qc.QcService"/> after a QC run; drives the QC badge
        /// next to the comment icon. Like Review/Comment this is METADATA — the
        /// row's Target is only touched when the user presses Fix, so a QC review
        /// never silently rewrites the file.
        /// </summary>
        [ObservableProperty] private QcProposal? _qcProposal;

        public bool HasQcProposal => QcProposal != null;

        /// <summary>Badge visibility: only rows with a live suggestion show it.</summary>
        public double QcIconOpacity => HasQcProposal ? 1.0 : 0.0;

        partial void OnQcProposalChanged(QcProposal? value)
        {
            OnPropertyChanged(nameof(HasQcProposal));
            OnPropertyChanged(nameof(QcIconOpacity));
        }

        /// <summary>
        /// UTC timestamp of the last Target edit, persisted in the review sidecar.
        /// Drives the "Sort by recently edited" chip in Etap 12 (which needs the
        /// timestamp to survive restarts).
        /// </summary>
        [ObservableProperty] private DateTime? _editedAt;

        /// <summary>
        /// In-memory only. True iff Target has been edited in the current app
        /// session. Used by the "Recently edited (this session)" filter in
        /// Etap 12. Resets to false on every app launch — that's the point.
        /// </summary>
        [ObservableProperty] private bool _isEditedThisSession;

        /// <summary>
        /// True when the cell's text overflows its allocated width/height in
        /// no-wrap mode. Computed by <c>TaggedTextBlock</c> and propagated up
        /// in Etap 5 to drive the per-row chevron-expand affordance.
        /// </summary>
        [ObservableProperty] private bool _isClipped;

        /// <summary>
        /// User-toggled per-row override for "show me the full text on this row
        /// even though global Expand-rows is off". Drives row-specific wrap in
        /// Etap 5. Does NOT break virtualization because only the toggled row
        /// changes height — virtualization handles a single re-measured row.
        /// </summary>
        [ObservableProperty] private bool _isExpanded;

        /// <summary>Shared empty list — every row starts and ends here, allocation-free.</summary>
        internal static readonly IReadOnlyList<TextRange> NoHits = Array.Empty<TextRange>();

        /// <summary>
        /// Etap 9 — character ranges inside the Target text that the inline
        /// FindBar query matched. Bound to <c>TaggedTextBlock.FindHits</c> on
        /// the Target cell so each hit gets a translucent accent background.
        /// Empty when the FindBar is closed or the query has no match here.
        ///
        /// <para>Replaced WHOLESALE, never mutated in place. It used to be an
        /// ObservableCollection the search filled item by item, and every single
        /// Add raised a CollectionChanged that made the cell tear down and rebuild
        /// its entire visual tree. A row with 40 hits rebuilt 41 times; on a
        /// 13 000-row file with a common word that turned a search into a hard
        /// freeze. One assignment is now one rebuild.</para>
        /// </summary>
        [ObservableProperty] private IReadOnlyList<TextRange> _findHits = NoHits;

        /// <summary>
        /// Etap 9 — index into <see cref="FindHits"/> of the "current" match
        /// (the one Find Next would step on next, drawn with a stronger
        /// accent so the user spots it in a sea of matches). -1 means none
        /// of this row's hits are currently selected.
        /// </summary>
        [ObservableProperty] private int _currentFindHitIndex = -1;

        /// <summary>
        /// Search-match ranges inside the SOURCE text (separate from <see cref="FindHits"/>,
        /// which is Target-only) so a search on the Source / All column also highlights the
        /// matched characters in the Source cell. Bound to the Source cell's
        /// <c>TaggedTextBlock.FindHits</c>. Replaced wholesale, same as
        /// <see cref="FindHits"/> and for the same reason.
        /// </summary>
        [ObservableProperty] private IReadOnlyList<TextRange> _sourceFindHits = NoHits;

        /// <summary>Index of the current match inside <see cref="SourceFindHits"/>; -1 = none.</summary>
        [ObservableProperty] private int _sourceCurrentFindHitIndex = -1;

        /// <summary>
        /// Highest <see cref="ProblemSeverity"/> across <see cref="Problems"/>,
        /// or null when the collection is empty. Status-icon column (Etap 3)
        /// reads this to pick its glyph.
        /// </summary>
        public ProblemSeverity? Severity
            => Problems.Count == 0 ? null : Problems.Max(p => p.Severity);

        /// <summary>
        /// True when any attached Problem ships an auto-Fix. Drives the
        /// lightbulb 💡 visibility in Etap 5.
        /// </summary>
        public bool HasFixableProblem => Problems.Any(p => p.Fix is not null);

        /// <summary>Whether Target is empty — considered untranslated.</summary>
        public bool IsUntranslated => string.IsNullOrWhiteSpace(Target);

        /// <summary>
        /// True when the Target cell should render the italic "— untranslated —"
        /// placeholder (prototype's <c>.empty-cell</c>). Goes false the moment
        /// editing starts or any text is typed.
        /// </summary>
        public bool ShowEmptyTarget => !IsEditing && IsUntranslated;

        /// <summary>
        /// Unicode glyph for the status-icon column. Priority chain mirrors
        /// <see cref="LeftEdgeBrush"/> minus Editing/Focused (those are stripe-
        /// only) and minus Rejected being hidden by Approved (both review
        /// states have their own glyph):
        /// <list type="number">
        ///   <item>Error    → Ⓧ (U+24E7)</item>
        ///   <item>Warning  → ⚠ (U+26A0)</item>
        ///   <item>Info     → ⓘ (U+24D8)</item>
        ///   <item>Approved → ✓ (U+2713) [only when no severity]</item>
        ///   <item>Rejected → ✗ (U+2717) [only when no severity]</item>
        ///   <item>else     → empty string (no glyph rendered)</item>
        /// </list>
        /// </summary>
        public string StatusIconGlyph
        {
            get
            {
                var sev = Severity;
                if (sev == ProblemSeverity.Error) return Utils.Glyphs.ErrorRound; // Ⓧ
                if (sev == ProblemSeverity.Warning) return Utils.Glyphs.Warning; // ⚠
                if (sev == ProblemSeverity.Info) return Utils.Glyphs.InfoRound; // ⓘ
                if (Review == ReviewStatus.Approved) return Utils.Glyphs.Ok; // ✓
                if (Review == ReviewStatus.Rejected) return Utils.Glyphs.Rejected; // ✗
                return "";
            }
        }

        /// <summary>Colour of <see cref="StatusIconGlyph"/>. Theme-aware hex string.</summary>
        public string StatusIconBrush
        {
            get
            {
                bool dark = AppConfig.Instance.IsDarkTheme;
                var sev = Severity;
                if (sev == ProblemSeverity.Error) return dark ? "#F48771" : "#B44A2A";
                if (sev == ProblemSeverity.Warning) return dark ? "#DCDCAA" : "#B5891D";
                if (sev == ProblemSeverity.Info) return dark ? "#9CDCFE" : "#2B6CB0";
                if (Review == ReviewStatus.Approved) return dark ? "#4EC9B0" : "#4F7A3E";
                if (Review == ReviewStatus.Rejected) return dark ? "#F48771" : "#B44A2A";
                return "Transparent";
            }
        }

        /// <summary>
        /// Hover tooltip for the status icon. Uses the first <see cref="Problem.Title"/>
        /// when problems exist; falls back to a localized review-status string
        /// when only a review marker is present. Empty when no glyph renders.
        /// </summary>
        public string StatusIconTooltip
        {
            get
            {
                var first = Problems.FirstOrDefault();
                if (first is not null) return first.Title;
                if (Review == ReviewStatus.Approved) return Localization.Loc.T("review.tooltip.approved");
                if (Review == ReviewStatus.Rejected) return Localization.Loc.T("review.tooltip.rejected");
                return "";
            }
        }

        /// <summary>
        /// Colour of the 2 px left-edge stripe on the row. Computed via a
        /// strict priority chain (top wins) — the docs in Plan say so:
        /// <list type="number">
        ///   <item><c>IsEditing</c>            → accent (blue/cinnamon).</item>
        ///   <item><c>Severity == Error</c>    → red.</item>
        ///   <item><c>Severity == Warning</c>  → yellow.</item>
        ///   <item><c>Review == Rejected</c>   → red (shares colour with Error;
        ///         the status-icon column disambiguates).</item>
        ///   <item><c>Review == Approved</c>   → green.</item>
        ///   <item><c>IsFocusedRow</c>         → accent (focus cursor).</item>
        ///   <item><c>Severity == Info</c>     → blue (info hint).</item>
        ///   <item>otherwise                   → transparent (no stripe).</item>
        /// </list>
        /// Returned as a hex colour string for compatibility with Avalonia's
        /// string-to-Brush converter — same approach <see cref="RowBackground"/>
        /// uses. The DataGridRow template binds to this property in Etap 3;
        /// for Etap 1 the property exists but the template still uses the old
        /// IsFocusedRow-only binding, so no visual change ships in this commit.
        /// </summary>
        public string LeftEdgeBrush
        {
            get
            {
                bool dark = AppConfig.Instance.IsDarkTheme;
                // A clicked / checkbox-selected / editing row is "active": its
                // stripe shows at FULL strength. Inactive rows show the same hue
                // dimmed — so selecting a row never hides its severity/review
                // colour, it just brightens it (prototype: stripe 0.6 → 1.0 on
                // focus). A plain row gets an accent stripe only while active.
                bool active = IsSelected || IsFocusedRow || IsEditing;

                string? hue =
                    IsEditing ? Services.ThemeManager.Accent.Hex6 :
                    Severity == ProblemSeverity.Error ? (dark ? "F48771" : "B44A2A") :
                    Severity == ProblemSeverity.Warning ? (dark ? "DCDCAA" : "B5891D") :
                    Review == ReviewStatus.Rejected ? (dark ? "F48771" : "B44A2A") :
                    Review == ReviewStatus.Approved ? (dark ? "4EC9B0" : "4F7A3E") :
                    Severity == ProblemSeverity.Info ? (dark ? "9CDCFE" : "2B6CB0") :
                    active ? Services.ThemeManager.Accent.Hex6 :
                    null;

                if (hue is null) return "Transparent";
                return (active ? "#FF" : "#8C") + hue;
            }
        }

        /// <summary>
        /// Visual background color for the row, computed from the model's own state
        /// and rendered by the custom DataGridRow ControlTemplate in AppTheme.axaml.
        /// We deliberately do NOT use the DataGrid's native :selected / :focus /
        /// :pointerover pseudo-classes for visuals — the FluentTheme defaults for
        /// those pseudo-classes use Opacity overrides that caused the click-flicker.
        /// Priority (top wins) matches the HTML prototype's grid-row CSS cascade:
        ///   1. Editing         → accent-soft overlay (.grid-row.editing !important)
        ///   2. Error           → red overlay (deeper on hover)            [SUL extra]
        ///   3. Warning         → yellow overlay (deeper on hover)         [SUL extra]
        ///   4. Selected+Hover  → selection-strong (.grid-row.selected:hover)
        ///   5. Selected        → selection         (.grid-row.selected)
        ///   6. Hover           → hover overlay     (.grid-row:hover)
        ///   7. AltRow          → grid-row-alt      (.grid-row:nth-child(even))
        ///   8. Default         → transparent       (let grid background show through)
        /// Colours are pre-composited over --grid-bg so Avalonia does not
        /// double-blend them.
        /// </summary>
        public string RowBackground
        {
            get
            {
                bool dark = AppConfig.Instance.IsDarkTheme;
                if (IsEditing) return Services.ThemeManager.Accent.RowEditing;
                // Severity-based row tint — was HasTokenError / HasTokenWarning
                // (legacy tag/token-Kind-specific), now triggers on any Problem
                // whose Severity matches. Keeps the row colour consistent with
                // the LeftEdgeBrush stripe + StatusIconBrush mapping.
                // Severity tint deepens one more step when the row is selected,
                // so selection reads clearly even on an error/warning row (whose
                // tint would otherwise hide the selection colour).
                var sev = Severity;
                if (sev == ProblemSeverity.Error) return IsSelected ? "#59FF4444" : (IsHoveredRow ? "#44FF4444" : "#33FF4444");
                if (sev == ProblemSeverity.Warning) return IsSelected ? "#59FFCC00" : (IsHoveredRow ? "#44FFCC00" : "#33FFCC00");
                // Merge flags (same semi-transparent overlay technique so one
                // value works on both themes): violet = source text changed in
                // the new game version (re-check the carried translation),
                // green = brand-new key introduced by the merge.
                if (IsSourceChanged) return IsSelected ? "#598A5CFF" : (IsHoveredRow ? "#448A5CFF" : "#338A5CFF");
                if (IsNewKey) return IsSelected ? "#5944CC66" : (IsHoveredRow ? "#4444CC66" : "#3344CC66");
                if (IsSelected) return IsHoveredRow
                    ? Services.ThemeManager.Accent.RowSelectedHover
                    : Services.ThemeManager.Accent.RowSelected;
                if (IsHoveredRow) return dark ? "#292929" : "#F1E8DA";
                if (IsAltRow) return dark ? "#202021" : "#F6EEDC";
                return "Transparent";
            }
        }

        public void RefreshVisualState()
        {
            OnPropertyChanged(nameof(RowBackground));
            OnPropertyChanged(nameof(LeftEdgeBrush));
        }

        // Notify UI when these change
        partial void OnIsSelectedChanged(bool value) => RefreshVisualState();
        partial void OnIsHoveredRowChanged(bool value)
        {
            RefreshVisualState();
            OnPropertyChanged(nameof(CommentIconOpacity));
        }
        partial void OnIsAltRowChanged(bool value) => RefreshVisualState();
        partial void OnIsNewKeyChanged(bool value) => RefreshVisualState();
        partial void OnIsSourceChangedChanged(bool value) => RefreshVisualState();
        partial void OnIsFocusedRowChanged(bool value) => OnPropertyChanged(nameof(LeftEdgeBrush));
        partial void OnReviewChanged(ReviewStatus value)
        {
            OnPropertyChanged(nameof(LeftEdgeBrush));
            OnPropertyChanged(nameof(StatusIconGlyph));
            OnPropertyChanged(nameof(StatusIconBrush));
            OnPropertyChanged(nameof(StatusIconTooltip));
            RaiseStatusIconStateChanged();
        }
        partial void OnIsEditingChanged(bool value)
        {
            RefreshVisualState();
            OnPropertyChanged(nameof(ShowEmptyTarget));
        }
        // Etap 5 — chevron-expand visibility tracks both IsClipped (written by
        // TaggedTextBlock on every layout pass) and IsExpanded (user toggle).
        partial void OnIsClippedChanged(bool value) => OnPropertyChanged(nameof(ShowExpandChevron));
        partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ShowExpandChevron));

        // OnHasTokenErrorChanged / OnHasTokenWarningChanged are gone — the
        // backing fields became computed properties in Etap 2. PropertyChanged
        // for them is fired from the constructor's Problems.CollectionChanged
        // subscription, which already calls RefreshVisualState equivalents
        // (notifies RowBackground, LeftEdgeBrush, Severity, etc.).

        partial void OnTargetChanged(string value)
        {
            OnPropertyChanged(nameof(IsUntranslated));
            OnPropertyChanged(nameof(ShowEmptyTarget));
        }
    }
}
