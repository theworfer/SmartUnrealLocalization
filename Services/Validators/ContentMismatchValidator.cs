using System.Collections.Generic;
using System.Text.RegularExpressions;
using SmartUnrealLocalization.Models;

using SmartUnrealLocalization.Localization;

namespace SmartUnrealLocalization.Services.Validators
{
    /// <summary>
    /// Catches translations that are obviously wrong without any language
    /// knowledge — pure "kind of content" mismatches between Source and Target:
    /// <list type="bullet">
    ///   <item>Source has letters (words) but Target has NONE — e.g.
    ///         <c>"Open"</c> → <c>"123"</c> or <c>"Open"</c> → <c>"???"</c>.
    ///         The Target is a stray value / number / placeholder, not a translation.</item>
    ///   <item>Source has NO letters (pure symbol / number like <c>"/"</c>,
    ///         <c>"100"</c>, <c>"%"</c>) yet Target differs from it — such entries
    ///         are non-translatable, so Target should equal Source.</item>
    /// </list>
    ///
    /// <para>
    /// Severity = <c>Warning</c>: a strong, objective signal that the row is
    /// wrong, but occasionally a string genuinely is just a number. The second
    /// case ships a Fix (set Target = Source); the first can't be auto-fixed
    /// (it needs a real translation). Empty/whitespace Targets are skipped
    /// (untranslated, owned by the counter/filter) like every other validator.
    /// </para>
    /// </summary>
    public sealed class ContentMismatchValidator : IValidator
    {
        public string Kind => "ContentMismatch";

        private static readonly Regex AnyLetter = new(@"\p{L}", RegexOptions.Compiled);

        public void Validate(TranslationRow row, List<Problem> output)
        {
            var src = row.Source ?? "";
            var tgt = row.Target ?? "";
            if (string.IsNullOrWhiteSpace(tgt)) return; // untranslated — owned by the counter/filter

            bool srcHasLetters = AnyLetter.IsMatch(src);
            bool tgtHasLetters = AnyLetter.IsMatch(tgt);

            // Case 1: Source is a phrase, Target has no letters at all (digits / symbols / "???").
            if (srcHasLetters && !tgtHasLetters)
            {
                output.Add(new Problem
                {
                    Kind = Kind,
                    Severity = ProblemSeverity.Warning,
                    Title = Loc.T("val.content.noText.title"),
                    Detail = string.Format(Loc.T("val.content.noText.detail"), Short(tgt)),
                });
                return;
            }

            // Case 2: Source has no letters (pure symbol / number), so it is normally
            // non-translatable — yet Target changed it. The Target should match Source.
            // Skip an empty/whitespace Source: that's not a "symbol that shouldn't
            // change", and offering to blank the Target would be wrong. Compare TRIMMED so
            // whitespace-only diffs ("/" → "/ ") aren't double-flagged — WhitespaceValidator
            // owns those.
            if (!srcHasLetters && !string.IsNullOrWhiteSpace(src) && tgt.Trim() != src.Trim())
            {
                output.Add(new Problem
                {
                    Kind = Kind,
                    Severity = ProblemSeverity.Warning,
                    Title = Loc.T("val.content.nonText.title"),
                    Detail = string.Format(Loc.T("val.content.nonText.detail"), Short(src), Short(tgt)),
                    Fix = new Fix
                    {
                        Label = Loc.T("val.content.fix"),
                        OldValue = tgt,
                        NewValue = src,
                    },
                });
            }
        }

        private static string Short(string s) => s.Length <= 40 ? s : s.Substring(0, 40) + "…";
    }
}
