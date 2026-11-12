using System.Collections.Generic;
using System.Linq;
using SmartUnrealLocalization.Models;

using SmartUnrealLocalization.Localization;

namespace SmartUnrealLocalization.Services.Validators
{
    /// <summary>
    /// Flags rows where Source's trailing punctuation differs from Target's —
    /// e.g. Source ends with <c>?</c>, Target ends with <c>.</c>. Often a
    /// translation typo or AI-rephrasing artefact.
    ///
    /// <para>
    /// Severity = <c>Info</c>: cosmetic for most strings, occasionally
    /// semantic (declarative vs interrogative). Fix swaps the trailing
    /// punctuation cluster on Target to match Source.
    /// </para>
    /// </summary>
    public sealed class PunctuationValidator : IValidator
    {
        public string Kind => "Punctuation";

        // Shared with QC so the two systems agree on what "ending punctuation"
        // means. `…` and the three-dot ellipsis are handled by including `.`.
        private static readonly char[] FinalPunct = LocalizationRules.FinalPunctuation;

        public void Validate(TranslationRow row, List<Problem> output)
        {
            var src = (row.Source ?? "").TrimEnd();
            var tgt = (row.Target ?? "").TrimEnd();
            if (src.Length == 0 || tgt.Length == 0) return;

            var srcTail = ExtractTail(src);
            var tgtTail = ExtractTail(tgt);

            if (srcTail == tgtTail) return;
            if (srcTail.Length == 0 && tgtTail.Length == 0) return;

            // Only the Source can say what the ending should be. Punctuation the
            // TRANSLATION added on its own (Source "Cancel" → Target "Скасувати.")
            // is left alone: it is a style choice, not a defect, and there is
            // nothing in the original to correct it against.
            //
            // (The comment here used to describe the opposite rule — it claimed
            // dropped punctuation was ignored. The code never did that, and
            // dropping a "?" really does turn a question into a statement.)
            if (srcTail.Length == 0) return;

            var fullTgt = row.Target ?? "";

            // Rebuild INSIDE the trailing whitespace so a string ending in a line
            // break keeps it, and the mark does not land after the break.
            var edge = LocalizationRules.TrailingWhitespace(fullTgt);
            var core = fullTgt[..(fullTgt.Length - edge.Length)];
            var fixedValue = StripTail(core) + srcTail + edge;

            // Range = the existing trailing punct cluster in row.Target (in the
            // original string — not the trimmed copy we used for comparison).
            // Zero-length cluster (target had no trailing punct) gets no range;
            // the status icon alone surfaces the issue.
            TextRange? range = null;
            if (tgtTail.Length > 0)
            {
                var trimmed = fullTgt.TrimEnd();
                int start = trimmed.Length - tgtTail.Length;
                if (start >= 0) range = new TextRange(start, tgtTail.Length);
            }

            output.Add(new Problem
            {
                Kind = Kind,
                Severity = ProblemSeverity.Info,
                Title = string.Format(Loc.T("val.punct.title"), srcTail, tgtTail),
                Detail = string.Format(Loc.T("val.punct.detail"), srcTail, tgtTail),
                Range = range,
                Fix = new Fix
                {
                    Label = Loc.T("val.punct.fix"),
                    OldValue = fullTgt,
                    NewValue = fixedValue,
                },
            });
        }

        private static string ExtractTail(string s)
        {
            int i = s.Length;
            while (i > 0 && FinalPunct.Contains(s[i - 1])) i--;
            return s.Substring(i);
        }

        private static string StripTail(string s)
        {
            var trimmed = s.TrimEnd();
            int i = trimmed.Length;
            while (i > 0 && FinalPunct.Contains(trimmed[i - 1])) i--;
            return trimmed.Substring(0, i);
        }
    }
}
