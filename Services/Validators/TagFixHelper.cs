using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services.Validators
{
    /// <summary>
    /// Shared "missing / extra / fixed-value" computation for the two near-
    /// identical token-shape validators — <see cref="TagMismatchValidator"/>
    /// (XML-ish &lt;Tag/&gt;) and <see cref="TokenMismatchValidator"/>
    /// (curly {Token}).
    ///
    /// <para>
    /// The naïve fix that just appends missing tokens at the end ignores
    /// broken/typo'd tokens already in Target (e.g. Source <c>{count}</c>,
    /// Target <c>{cont}</c>) — the user ends up with <c>{cont} … {count}</c>
    /// instead of <c>{count}</c>. This helper detects "extras" (tokens
    /// present in Target but absent from Source) and PAIRS them with
    /// missing tokens positionally, replacing in place. Any leftover
    /// missing tokens are still appended at the end.
    /// </para>
    /// </summary>
    internal static class TagFixHelper
    {
        public sealed class FixComputation
        {
            public List<string> MissingTags { get; init; } = new();
            public List<Match> ExtraMatches { get; init; } = new();
            public string FixedTarget { get; init; } = "";
            public TextRange? FirstExtraRange { get; init; }

            /// <summary>True iff at least one Target token was replaced.</summary>
            public bool DidReplace => ExtraMatches.Count > 0 && MissingTags.Count > 0;
        }

        /// <summary>
        /// Compute the diff between Source and Target tokens captured by
        /// <paramref name="re"/> and build the proposed fixed Target.
        ///
        /// <paramref name="brokenRe"/> (optional) matches MALFORMED fragments in
        /// Target — e.g. an unclosed <c>&lt;Tag/</c> missing its <c>&gt;</c>.
        /// Those fragments aren't captured by <paramref name="re"/> (which needs
        /// a closing bracket), so without this the fix would APPEND the missing
        /// tag next to the broken fragment and the user had to click twice. We
        /// treat such fragments as extras so the fix REPLACES them in place.
        /// </summary>
        public static FixComputation Compute(string src, string tgt, Regex re, Regex? brokenRe = null)
        {
            var srcTags = re.Matches(src).Select(m => m.Value).ToList();
            var tgtMatches = re.Matches(tgt).Cast<Match>().ToList();
            var tgtTags = tgtMatches.Select(m => m.Value).ToList();

            // Multiset diff — counts matter: Source "{0} {0}" vs Target "{0}" is one
            // MISSING {0} (the old set-diff passed it silently, so a dropped
            // duplicate placeholder never produced a Problem).
            var tgtCounts = new Dictionary<string, int>();
            foreach (var t in tgtTags) tgtCounts[t] = tgtCounts.GetValueOrDefault(t) + 1;

            var missing = new List<string>();
            var seenSrc = new Dictionary<string, int>();
            foreach (var t in srcTags)
            {
                seenSrc[t] = seenSrc.GetValueOrDefault(t) + 1;
                if (seenSrc[t] > tgtCounts.GetValueOrDefault(t)) missing.Add(t);
            }

            // Extras = target occurrences beyond the source's count of that token.
            // Walk in order consuming the source's budget so the SURPLUS occurrence
            // (not the first) is the one flagged/replaced.
            var srcBudget = new Dictionary<string, int>();
            foreach (var t in srcTags) srcBudget[t] = srcBudget.GetValueOrDefault(t) + 1;
            var extras = new List<Match>();
            foreach (var m in tgtMatches)
            {
                if (srcBudget.TryGetValue(m.Value, out int left) && left > 0)
                    srcBudget[m.Value] = left - 1;
                else
                    extras.Add(m);
            }

            // Malformed/unclosed fragments are also "extras" to replace in place.
            // Skip any that overlap a well-formed match we already counted.
            if (brokenRe != null)
            {
                foreach (Match bm in brokenRe.Matches(tgt))
                {
                    bool overlaps = tgtMatches.Any(m =>
                        bm.Index < m.Index + m.Length && m.Index < bm.Index + bm.Length);
                    if (!overlaps) extras.Add(bm);
                }
                extras = extras.OrderBy(m => m.Index).ToList();
            }

            // Build the fixed target:
            // 1) Walk extras RIGHT-TO-LEFT replacing each with the
            //    corresponding missing token. Right-to-left keeps earlier
            //    positions valid as we mutate the string.
            // 2) If more missing than extras, append the leftover at the end
            //    (matches the prior behaviour but only for tokens that have
            //    no "broken" counterpart in Target).
            var sb = new StringBuilder(tgt);
            int pairs = System.Math.Min(extras.Count, missing.Count);
            for (int i = pairs - 1; i >= 0; i--)
            {
                sb.Remove(extras[i].Index, extras[i].Length);
                sb.Insert(extras[i].Index, missing[i]);
            }

            string fixedValue = sb.ToString();
            if (missing.Count > extras.Count)
            {
                var trailing = string.Join(" ", missing.Skip(pairs));

                // Append INSIDE the trailing whitespace. `TrimEnd()` used to eat
                // the edge run wholesale, so restoring a tag on a string ending in
                // a line break silently deleted that break — a structural change
                // the user never asked for, hidden inside a "fix the tag" click.
                string edge = LocalizationRules.TrailingWhitespace(fixedValue);
                string core = fixedValue[..(fixedValue.Length - edge.Length)];
                fixedValue = core.TrimEnd(' ', '\t') + " " + trailing + edge;
            }

            // Range for the wavy underline = first extra's position in the
            // ORIGINAL Target (so the user can see what we're about to fix).
            // If there are no extras (pure append case), the wavy underline
            // has nothing to anchor to — Range stays null.
            TextRange? firstExtraRange = extras.Count > 0
                ? new TextRange(extras[0].Index, extras[0].Length)
                : null;

            return new FixComputation
            {
                MissingTags = missing,
                ExtraMatches = extras,
                FixedTarget = fixedValue,
                FirstExtraRange = firstExtraRange,
            };
        }
    }
}
