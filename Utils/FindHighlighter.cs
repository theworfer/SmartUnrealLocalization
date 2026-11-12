using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Utils
{
    /// <summary>
    /// Collects search hits inside one cell's text, with a ceiling.
    ///
    /// <para>Pulled out of the FindBar view model so the ceiling is testable. It
    /// exists because of a real freeze: searching a common word in a 13 000-row
    /// CSV with both columns filled produced hundreds of thousands of hits, and
    /// the app spent minutes building highlight state nobody could use. The
    /// counter still reports the TRUE total, so the ceiling never lies about how
    /// much matched — it only limits what gets built.</para>
    /// </summary>
    public static class FindHighlighter
    {
        /// <summary>Shared empty result — the answer for the overwhelming majority of rows.</summary>
        public static readonly IReadOnlyList<TextRange> None = Array.Empty<TextRange>();

        /// <summary>
        /// Match ranges in <paramref name="text"/>, at most <paramref name="cap"/>
        /// of them. <paramref name="total"/> is incremented by EVERY match found,
        /// including the ones past the cap.
        ///
        /// <para>Zero-width matches are skipped: a regex like <c>a*</c> matches the
        /// empty string at every position, which would produce one "highlight" per
        /// character and nothing visible to show for it.</para>
        /// </summary>
        public static IReadOnlyList<TextRange> Collect(string? text, Regex matcher, int cap, ref long total)
        {
            if (string.IsNullOrEmpty(text)) return None;

            List<TextRange>? hits = null;
            foreach (Match m in matcher.Matches(text))
            {
                if (m.Length == 0) continue;
                total++;
                if ((hits?.Count ?? 0) >= cap) continue;   // keep counting, stop building
                (hits ??= new List<TextRange>()).Add(new TextRange(m.Index, m.Length));
            }
            return hits ?? None;
        }

        /// <summary>
        /// Does <paramref name="text"/> still hold a match at exactly
        /// <paramref name="start"/> / <paramref name="length"/>?
        ///
        /// <para>Replace works from offsets recorded by the last full scan. If the
        /// cell changed after that scan — an AI batch, an undo, a paste, an MCP
        /// write — those offsets can still be IN BOUNDS while pointing at
        /// completely different characters, and splicing there rewrites text the
        /// user never searched for. A bounds check does not catch that; only
        /// re-finding the hit does.</para>
        /// </summary>
        public static bool HitStillPresent(string? text, Regex matcher, int start, int length, int cap)
        {
            if (length <= 0 || start < 0 || string.IsNullOrEmpty(text) || start + length > text.Length)
                return false;

            long ignored = 0;
            var hits = Collect(text, matcher, cap, ref ignored);
            for (int i = 0; i < hits.Count; i++)
                if (hits[i].Start == start && hits[i].Length == length) return true;
            return false;
        }
    }
}
