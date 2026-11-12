using System.Collections.Generic;
using System.Globalization;
using SmartUnrealLocalization.Models;

using SmartUnrealLocalization.Localization;

namespace SmartUnrealLocalization.Services.Validators
{
    /// <summary>
    /// Flags words inside Target that mix Latin and Cyrillic letters — a
    /// classic copy-paste leftover (e.g. translator pasted English then
    /// half-overtyped: "Кiлька" with a Latin "i" instead of "і").
    ///
    /// <para>
    /// Severity = <c>Warning</c>: usually a real bug; the glyphs look right
    /// but the string is wrong (search, sort, validation all break).
    /// No Fix because you can't tell which script was intended.
    /// </para>
    /// </summary>
    public sealed class MixedScriptsValidator : IValidator
    {
        public string Kind => "MixedScripts";

        public void Validate(TranslationRow row, List<Problem> output)
        {
            var tgt = row.Target ?? "";
            if (string.IsNullOrWhiteSpace(tgt)) return; // untranslated — owned by the counter/filter

            // Walk word by word, looking for any word with both Latin AND Cyrillic letters.
            // Captures both the word text (for the tooltip Title) and its start
            // index (for the Etap 4 wavy-underline Range — first offender wins).
            int wordStart = -1;
            bool sawLatin = false;
            bool sawCyrillic = false;
            var offenders = new List<string>();
            TextRange? firstRange = null;

            for (int i = 0; i <= tgt.Length; i++)
            {
                bool isLetter = i < tgt.Length && char.IsLetter(tgt[i]);
                if (isLetter)
                {
                    if (wordStart < 0) wordStart = i;
                    var script = ScriptOf(tgt[i]);
                    if (script == Script.Latin) sawLatin = true;
                    if (script == Script.Cyrillic) sawCyrillic = true;
                }
                else if (wordStart >= 0)
                {
                    if (sawLatin && sawCyrillic)
                    {
                        var word = tgt.Substring(wordStart, i - wordStart);
                        offenders.Add(word);
                        firstRange ??= new TextRange(wordStart, i - wordStart);
                    }
                    wordStart = -1;
                    sawLatin = false;
                    sawCyrillic = false;
                }
            }

            if (offenders.Count == 0) return;

            output.Add(new Problem
            {
                Kind = Kind,
                Severity = ProblemSeverity.Warning,
                Title = string.Format(Loc.T("val.mixed.title"), string.Join(", ", offenders)),
                Detail = Loc.T("val.mixed.detail"),
                Range = firstRange,
            });
        }

        private enum Script { Other, Latin, Cyrillic }

        private static Script ScriptOf(char c)
        {
            // Fast path for ASCII Latin.
            if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) return Script.Latin;
            // Cyrillic block U+0400..U+04FF + Cyrillic Supplement U+0500..U+052F.
            if (c >= 'Ѐ' && c <= 'ԯ') return Script.Cyrillic;
            // Extended Latin (Latin-1 Supplement, Latin Extended-A/B).
            if ((c >= 'À' && c <= 'ɏ')) return Script.Latin;
            return Script.Other;
        }
    }
}
