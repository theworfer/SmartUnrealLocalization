using System.Collections.Generic;
using System.Text.RegularExpressions;
using SmartUnrealLocalization.Models;

using SmartUnrealLocalization.Localization;

namespace SmartUnrealLocalization.Services.Validators
{
    /// <summary>
    /// Light validator (per-row, debounced after Target edits + on file open):
    /// flags rows whose Source uses Unreal's plural-form directive but Target
    /// either dropped it OR has fewer CLDR form definitions inside it.
    ///
    /// <para>Two-stage check:</para>
    /// <list type="number">
    ///   <item>Directive count — Target dropped one or more <c>|plural(...)</c>
    ///         blocks entirely. The runtime falls back to the literal
    ///         placeholder string.</item>
    ///   <item>Form count — Target has the right number of blocks but one or
    ///         more is empty / contains a placeholder (literal "...") /
    ///         missing form keywords. Detected by counting CLDR plural-form
    ///         keys (<c>zero</c>, <c>one</c>, <c>two</c>, <c>few</c>,
    ///         <c>many</c>, <c>other</c>) inside the captured contents.</item>
    /// </list>
    ///
    /// <para>
    /// Severity = <c>Error</c>: a real runtime bug, not a style nitpick.
    /// No auto-Fix — the translator has to actually write the plural forms
    /// for their language (Ukrainian needs 3-4 forms vs English's 2), which
    /// is way out of regex scope.
    /// </para>
    ///
    /// <para>
    /// Originally a heavy validator (Etap 7). Moved to light in this audit
    /// pass because the check is per-row and cheap (two regex scans); having
    /// it light means it auto-updates 300 ms after the user finishes typing
    /// the plural fix, instead of waiting for the next F8.
    /// </para>
    /// </summary>
    public sealed class PluralIncompleteValidator : IValidator
    {
        public string Kind => "PluralIncomplete";

        // Whole `|plural(<contents>)` block, with the inside captured in
        // group 1 so we can count CLDR-form keywords inside it. Covers both
        // UE syntaxes — standard `{count}|plural(one="x", other="y")` and
        // alternative `{count|plural(one="x", other="y")}`.
        private static readonly Regex PluralBlockRe = new(
            @"\|\s*plural\s*\(",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // CLDR plural-form keywords followed by `=`. Word boundary `\b` keeps
        // `oneone=` from double-counting; IgnoreCase covers `One=` capitalizations.
        private static readonly Regex PluralFormKeyRe = new(
            @"\b(zero|one|two|few|many|other)\s*=",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>One <c>|plural(…)</c> directive: where it is, and what is inside it.</summary>
        private readonly record struct Block(int Index, int Length, string Inner);

        /// <summary>
        /// Find every <c>|plural(…)</c> directive, matching parentheses by COUNTING
        /// them rather than stopping at the first <c>)</c>.
        ///
        /// <para>The old <c>\(([^)]*)\)</c> ended the block at the first close
        /// paren, so a form containing one — <c>other="{n} предметів (усього)"</c> —
        /// truncated the captured contents and hid the form keywords after it. When
        /// the parenthesis was in the TRANSLATION only, the target appeared to have
        /// fewer forms than the source and the row was reported as a plural Error
        /// that did not exist.</para>
        /// </summary>
        private static List<Block> FindBlocks(string s)
        {
            var blocks = new List<Block>();
            foreach (Match open in PluralBlockRe.Matches(s))
            {
                int start = open.Index + open.Length;      // just past the '('
                int depth = 1;
                int i = start;
                while (i < s.Length && depth > 0)
                {
                    if (s[i] == '(') depth++;
                    else if (s[i] == ')') depth--;
                    i++;
                }
                // An unclosed directive is itself malformed; take the rest of the
                // string so the form count still reflects what the author wrote.
                int innerEnd = depth == 0 ? i - 1 : s.Length;
                blocks.Add(new Block(open.Index, i - open.Index, s[start..innerEnd]));
            }
            return blocks;
        }

        public void Validate(TranslationRow row, List<Problem> output)
        {
            var src = row.Source ?? "";
            if (src.Length == 0) return;

            var srcBlocks = FindBlocks(src);
            if (srcBlocks.Count == 0) return;

            var tgt = row.Target ?? "";
            // Untranslated rows are intentionally NOT flagged as problems (they're
            // tracked by the Untranslated counter + filter). If we fired here the
            // row would light up as an Error just for being "not translated yet".
            // Skip; the moment they type anything we'll re-check.
            if (string.IsNullOrWhiteSpace(tgt)) return;

            var tgtBlocks = FindBlocks(tgt);

            // Check 1 — directive count: Target dropped one or more
            // `|plural(...)` blocks entirely.
            if (tgtBlocks.Count < srcBlocks.Count)
            {
                output.Add(new Problem
                {
                    Kind = Kind,
                    Severity = ProblemSeverity.Error,
                    Title = string.Format(Loc.T("val.plural.directive.title"), tgtBlocks.Count, srcBlocks.Count),
                    Detail = string.Format(Loc.T("val.plural.directive.detail"), srcBlocks.Count, tgtBlocks.Count),
                    Range = FirstBlockRange(tgtBlocks),
                });
                return;
            }

            // Check 2 — form count INSIDE each directive: Target has the
            // right number of `|plural(...)` but one or more is empty /
            // contains placeholder dots / missing form keywords.
            int srcForms = SumForms(srcBlocks);
            int tgtForms = SumForms(tgtBlocks);
            if (tgtForms < srcForms)
            {
                output.Add(new Problem
                {
                    Kind = Kind,
                    Severity = ProblemSeverity.Error,
                    Title = string.Format(Loc.T("val.plural.forms.title"), tgtForms, srcForms),
                    Detail = string.Format(Loc.T("val.plural.forms.detail"), srcForms, tgtForms),
                    Range = FirstBlockRange(tgtBlocks),
                });
            }
        }

        private static int SumForms(List<Block> blocks)
        {
            int total = 0;
            foreach (var b in blocks)
                total += PluralFormKeyRe.Matches(b.Inner).Count;
            return total;
        }

        private static TextRange? FirstBlockRange(List<Block> blocks)
            => blocks.Count == 0 ? null : new TextRange(blocks[0].Index, blocks[0].Length);
    }
}
