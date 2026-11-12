using System.Collections.Generic;
using System.Text.RegularExpressions;
using SmartUnrealLocalization.Models;

using SmartUnrealLocalization.Localization;

namespace SmartUnrealLocalization.Services.Validators
{
    /// <summary>
    /// Detects whitespace anomalies in Target that usually indicate a
    /// copy-paste accident or a stray space from machine translation:
    /// leading space, trailing space, multiple spaces between words.
    ///
    /// <para>
    /// Severity = <c>Info</c>: rendering still works, just looks sloppy.
    /// Fix trims edges and collapses internal multi-spaces to single.
    /// </para>
    /// </summary>
    public sealed class WhitespaceValidator : IValidator
    {
        public string Kind => "Whitespace";

        // Two-or-more spaces in a row anywhere in the string.
        private static readonly Regex MultiSpace = new(@" {2,}", RegexOptions.Compiled);

        public void Validate(TranslationRow row, List<Problem> output)
        {
            var tgt = row.Target ?? "";
            // Empty OR whitespace-only Target is "untranslated" (IsUntranslated) —
            // not flagged as a problem; the Untranslated counter/filter own it.
            if (string.IsNullOrWhiteSpace(tgt)) return;

            // Edge whitespace must MATCH the Source exactly, in both directions.
            //
            // The old rule only fired when the Target had whitespace of its own
            // (`tgtLead.Length > 0`), so whitespace the translation LOST was
            // invisible here — and that is the damaging direction: strings are
            // concatenated at runtime, so a dropped leading space glues two words
            // together and a dropped trailing line break collapses a page. QC
            // caught it, the live pass did not.
            //
            // Mirroring the Source is still never flagged: a " / " separator entry
            // keeps its spaces, and the fix normalises toward the Source, so it can
            // never ping-pong with ContentMismatch's "Match Source".
            var src = row.Source ?? "";
            string srcLead = LeadingWs(src), srcTrail = TrailingWs(src);
            string tgtLead = LeadingWs(tgt), tgtTrail = TrailingWs(tgt);

            bool leading = tgtLead != srcLead;
            bool trailing = tgtTrail != srcTrail;
            bool internalMulti = MultiSpace.IsMatch(tgt) && !MultiSpace.IsMatch(src);

            if (!leading && !trailing && !internalMulti) return;

            // The fix normalizes toward the SOURCE's edges, so applying it can
            // never re-introduce a difference another validator objects to.
            var core = internalMulti ? MultiSpace.Replace(tgt.Trim(), " ") : tgt.Trim();
            var fixedValue = srcLead + core + srcTrail;

            var issues = new List<string>();
            if (leading) issues.Add(Loc.T(tgtLead.Length == 0 ? "val.ws.lostLeading" : "val.ws.leading"));
            if (trailing) issues.Add(Loc.T(tgtTrail.Length == 0 ? "val.ws.lostTrailing" : "val.ws.trailing"));
            if (internalMulti) issues.Add(Loc.T("val.ws.multi"));

            // Range = the FIRST problematic run, in priority leading → trailing →
            // internal. Etap 4 renders a wavy underline under it. Only one run
            // gets highlighted per row even if multiple issues coexist; the
            // tooltip + Fix still address all of them.
            TextRange? range = ComputeFirstRange(tgt, leading, trailing, internalMulti);

            output.Add(new Problem
            {
                Kind = Kind,
                Severity = ProblemSeverity.Info,
                Title = Loc.T("val.ws.title"),
                Detail = string.Format(Loc.T("val.ws.detail"), string.Join(", ", issues)),
                Range = range,
                Fix = new Fix
                {
                    Label = Loc.T("val.ws.fix"),
                    OldValue = tgt,
                    NewValue = fixedValue,
                },
            });
        }

        private static string LeadingWs(string s)
        {
            int i = 0;
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            return s.Substring(0, i);
        }

        private static string TrailingWs(string s)
        {
            int i = s.Length;
            while (i > 0 && char.IsWhiteSpace(s[i - 1])) i--;
            return s.Substring(i);
        }

        private static TextRange? ComputeFirstRange(string tgt, bool leading, bool trailing, bool internalMulti)
        {
            // Only whitespace that is actually PRESENT can be underlined. When the
            // finding is "the Source's space is missing" there is nothing to mark,
            // and a zero-length range would draw a stray artefact at the edge.
            if (leading)
            {
                int i = 0;
                while (i < tgt.Length && char.IsWhiteSpace(tgt[i])) i++;
                if (i > 0) return new TextRange(0, i);
            }
            if (trailing)
            {
                int i = tgt.Length - 1;
                while (i > 0 && char.IsWhiteSpace(tgt[i])) i--;
                if (tgt.Length - i - 1 > 0) return new TextRange(i + 1, tgt.Length - i - 1);
            }
            if (internalMulti)
            {
                var m = MultiSpace.Match(tgt);
                if (m.Success) return new TextRange(m.Index, m.Length);
            }
            return null;
        }
    }
}
