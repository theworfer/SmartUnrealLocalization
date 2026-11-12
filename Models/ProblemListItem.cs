using SmartUnrealLocalization.Utils;

namespace SmartUnrealLocalization.Models
{
    /// <summary>
    /// One aggregated entry in the Problems tab (Etap 10): a single
    /// <see cref="Problem"/> paired with the <see cref="TranslationRow"/> it
    /// belongs to. Immutable view-wrapper — the list is rebuilt fresh from
    /// <c>_allRows</c> whenever the problem set changes, so there is no
    /// INotifyPropertyChanged here. Display getters mirror the row's
    /// status-icon convention (same glyphs / severity names) so the panel and
    /// the grid gutter read identically.
    ///
    /// <para>An entry may instead wrap a <see cref="QcProposal"/>: QC findings
    /// appear in the same list so there is ONE place showing everything wrong
    /// with the file. They keep their own lifecycle though — applying a QC fix
    /// goes through the QC verdict flow (sidecar, Keep, staleness, residual
    /// findings), never through the validator's <see cref="Models.Fix"/> path.</para>
    /// </summary>
    public sealed class ProblemListItem
    {
        public TranslationRow Row { get; }
        public Problem? Problem { get; }
        public QcProposal? Qc { get; }

        public ProblemListItem(TranslationRow row, Problem problem)
        {
            Row = row;
            Problem = problem;
        }

        public ProblemListItem(TranslationRow row, QcProposal proposal)
        {
            Row = row;
            Qc = proposal;
        }

        /// <summary>True for a QC suggestion — decides which Fix path runs.</summary>
        public bool IsQc => Qc is not null;

        /// <summary>
        /// QC severities map onto the validator's three levels so both kinds sort
        /// and colour identically in a single list.
        /// </summary>
        public ProblemSeverity Severity => Qc is null
            ? Problem!.Severity
            : Qc.Severity switch
            {
                QcSeverity.Critical or QcSeverity.High => ProblemSeverity.Error,
                QcSeverity.Medium => ProblemSeverity.Warning,
                _ => ProblemSeverity.Info,
            };

        /// <summary>Glyph matching <see cref="TranslationRow.StatusIconGlyph"/>.</summary>
        public string Glyph => Severity switch
        {
            ProblemSeverity.Error => Utils.Glyphs.ErrorRound,
            ProblemSeverity.Warning => Utils.Glyphs.Warning,
            _ => Utils.Glyphs.InfoRound,
        };

        /// <summary>
        /// "Error" / "Warning" / "Info" — bound to the glyph TextBlock's Tag so
        /// the DataTemplate's attribute-selector styles can colour it from the
        /// theme tokens (DangerColor / WarningColor / InfoColor), staying
        /// theme-switch-safe without hardcoding hex on a non-observable item.
        /// </summary>
        public string SeverityName => Severity.ToString();

        /// <summary>SCREAMING-KEBAB kind, e.g. "TAG-MISMATCH". QC entries carry a
        /// prefix so the two sources are never confused at a glance.</summary>
        public string KindLabel => Qc is null
            ? TextUtils.KindToLabel(Problem!.Kind)
            : "QC " + TextUtils.KindToLabel(Qc.Kinds.Count > 0 ? Qc.Kinds[0] : "Qc");

        public string Title => Qc is null ? Problem!.Title : Qc.Reason;

        /// <summary>Row key shown on the right so the user can locate the entry.</summary>
        public string RowKey => Row.Key;

        /// <summary>
        /// Drives the per-row [Fix] button. A QC entry offers it only when there
        /// IS a replacement and the row has not changed since the review — the
        /// same two guards the badge popup applies.
        /// </summary>
        public bool HasFix => Qc is null
            ? Problem!.Fix is not null
            : Qc.Before != Qc.After && !Qc.IsStaleFor(Row);

        /// <summary>
        /// Hover text. For QC it shows the actual replacement — seeing what would
        /// change without opening every row's badge is the point of listing them
        /// here at all.
        /// </summary>
        public string Tooltip => Qc is null
            ? (string.IsNullOrWhiteSpace(Problem!.Detail) ? Problem.Title : Problem.Detail)
            : Qc.Before == Qc.After
                ? Qc.Reason + "\n\n— no automatic fix, this one needs a human"
                : $"{Qc.Reason}\n\n− {ShowEdges(Qc.Before)}\n+ {ShowEdges(Qc.After)}";

        /// <summary>
        /// Mark leading/trailing whitespace with a visible dot. The commonest QC
        /// finding is a lost edge space, and a plain preview shows two identical
        /// lines for it — the difference is real but literally invisible.
        /// </summary>
        private static string ShowEdges(string text)
        {
            if (text.Length == 0) return text;
            string trimmed = text.TrimStart();
            string lead = new('·', text.Length - trimmed.Length);
            string body = trimmed.TrimEnd();
            string trail = new('·', trimmed.Length - body.Length);
            return lead + body + trail;
        }
    }
}
