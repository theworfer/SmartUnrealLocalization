using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SmartUnrealLocalization.Models;

using SmartUnrealLocalization.Localization;

namespace SmartUnrealLocalization.Services.Validators
{
    /// <summary>
    /// Detects printf-style placeholders — <c>%s</c>, <c>%d</c>, <c>%02d</c>,
    /// <c>%1$s</c>, <c>%.2f</c> — that the translation dropped, duplicated or
    /// invented.
    ///
    /// <para><b>Why this exists.</b> Until now nothing in the live validation
    /// looked at printf placeholders at all: <see cref="TagMismatchValidator"/>
    /// knows <c>&lt;…&gt;</c>, <see cref="TokenMismatchValidator"/> knows
    /// <c>{…}</c>, and a dropped <c>%d</c> — which makes the game print the wrong
    /// value or crash on the format call — was reported only by an explicit QC
    /// run. A defect that severe must not depend on the user remembering to
    /// review the file.</para>
    ///
    /// <para>Severity = <c>Error</c>, matching the other substitution checks. The
    /// shape comes from <see cref="LocalizationRules.Printf"/> so QC and F8 can
    /// never disagree about what a placeholder is.</para>
    /// </summary>
    public sealed class PlaceholderMismatchValidator : IValidator
    {
        public string Kind => "PlaceholderMismatch";

        private static readonly Regex Re = LocalizationRules.Printf;

        public void Validate(TranslationRow row, List<Problem> output)
        {
            var src = row.Source ?? "";
            var tgt = row.Target ?? "";
            if (string.IsNullOrWhiteSpace(tgt)) return;   // untranslated — owned by the counter/filter

            var result = TagFixHelper.Compute(src, tgt, Re);
            bool anyMissing = result.MissingTags.Count > 0;
            bool anyExtra = result.ExtraMatches.Count > 0;
            if (!anyMissing && !anyExtra) return;

            var parts = new List<string>();
            if (anyMissing) parts.Add(string.Format(Loc.T("val.ph.missing"), string.Join(", ", result.MissingTags)));
            if (anyExtra) parts.Add(string.Format(Loc.T("val.ph.unexpected"), string.Join(", ", result.ExtraMatches.Select(m => m.Value).Distinct())));

            output.Add(new Problem
            {
                Kind = Kind,
                Severity = ProblemSeverity.Error,
                Title = string.Format(Loc.T("val.ph.title"), string.Join("; ", parts)),
                Detail = anyMissing
                    ? Loc.T("val.ph.detail.dropped")
                    : Loc.T("val.ph.detail.extra"),
                Range = result.FirstExtraRange,
                // A fix is only offered when there is something to swap it INTO.
                // Appending a bare "%d" to the end of a sentence is not a fix — it
                // puts the number in the wrong place and reads as repaired.
                Fix = result.DidReplace
                    ? new Fix
                    {
                        Label = Loc.T("val.ph.fix"),
                        OldValue = tgt,
                        NewValue = result.FixedTarget,
                    }
                    : null,
            });
        }
    }
}
