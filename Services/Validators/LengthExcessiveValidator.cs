using System.Collections.Generic;
using SmartUnrealLocalization.Models;

using SmartUnrealLocalization.Localization;

namespace SmartUnrealLocalization.Services.Validators
{
    /// <summary>
    /// Light validator (per-row, debounced after Target edits + on file open):
    /// flags rows whose Target is more than ~2× longer than Source. Often a
    /// sign of over-translation (translator added explanatory text), but
    /// sometimes intentional (English "OK" → German "Einverstanden").
    ///
    /// <para>
    /// Severity = <c>Info</c>: cosmetic / suspicion-only, never blocks build.
    /// No Fix — only the translator knows whether the length is correct.
    /// </para>
    ///
    /// <para>
    /// Originally a heavy validator (Etap 7). Moved to light in the audit
    /// pass so that re-typing a too-long Target into something reasonable
    /// clears the Info badge within 300 ms instead of waiting for F8.
    /// </para>
    /// </summary>
    public sealed class LengthExcessiveValidator : IValidator
    {
        public string Kind => "LengthExcessive";

        // Skip very short sources — "OK" → "Einverstanden" is a normal ratio
        // explosion. Below this threshold length comparison is meaningless noise.
        private const int MinSourceLengthToCheck = 10;
        private const double RatioThreshold = 2.0;

        public void Validate(TranslationRow row, List<Problem> output)
        {
            var src = row.Source ?? "";
            var tgt = row.Target ?? "";
            if (src.Length < MinSourceLengthToCheck) return;
            if (string.IsNullOrWhiteSpace(tgt)) return; // untranslated — owned by the counter/filter

            double ratio = (double)tgt.Length / src.Length;
            if (ratio <= RatioThreshold) return;

            output.Add(new Problem
            {
                Kind = Kind,
                Severity = ProblemSeverity.Info,
                Title = string.Format(Loc.T("val.length.title"), ratio.ToString("0.#")),
                Detail = string.Format(Loc.T("val.length.detail"), src.Length, tgt.Length, ratio.ToString("0.#")),
            });
        }
    }
}
