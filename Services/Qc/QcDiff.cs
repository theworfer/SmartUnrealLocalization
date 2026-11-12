using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SmartUnrealLocalization.Services.Qc
{
    /// <summary>
    /// Word-level diff for the QC "Before / After" popup: the changed words are
    /// what the user actually needs to see, so both sides are split into
    /// word+separator tokens and aligned with a standard LCS. Removed words are
    /// highlighted on the Before side, added words on the After side.
    /// </summary>
    public static class QcDiff
    {
        public readonly record struct Segment(string Text, bool Changed);

        private static readonly Regex Tokenizer = new(@"\s+|[^\s]+", RegexOptions.Compiled);

        /// <summary>Split both texts into segments flagged as changed / unchanged.</summary>
        public static (List<Segment> Before, List<Segment> After) Compute(string before, string after)
        {
            var a = Tokenize(before);
            var b = Tokenize(after);

            // The LCS table is O(words²) ints. Game strings are short, but a
            // merged/corrupt row can be enormous — fall back to a plain
            // "everything changed" view instead of allocating gigabytes.
            const int MaxTokens = 1500;
            if (a.Count > MaxTokens || b.Count > MaxTokens)
            {
                return (new List<Segment> { new(before ?? "", true) },
                        new List<Segment> { new(after ?? "", true) });
            }

            // LCS table (word counts here are small — a UI string, not a document).
            int[,] lcs = new int[a.Count + 1, b.Count + 1];
            for (int i = a.Count - 1; i >= 0; i--)
                for (int j = b.Count - 1; j >= 0; j--)
                    lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

            var beforeSegs = new List<Segment>();
            var afterSegs = new List<Segment>();
            int x = 0, y = 0;
            while (x < a.Count && y < b.Count)
            {
                if (a[x] == b[y]) { Push(beforeSegs, a[x], false); Push(afterSegs, b[y], false); x++; y++; }
                else if (lcs[x + 1, y] >= lcs[x, y + 1]) { Push(beforeSegs, a[x], true); x++; }
                else { Push(afterSegs, b[y], true); y++; }
            }
            while (x < a.Count) Push(beforeSegs, a[x++], true);
            while (y < b.Count) Push(afterSegs, b[y++], true);

            return (beforeSegs, afterSegs);
        }

        private static List<string> Tokenize(string s)
            => Tokenizer.Matches(s ?? "").Select(m => m.Value).ToList();

        /// <summary>Append, merging into the previous segment when the flag matches.</summary>
        private static void Push(List<Segment> list, string text, bool changed)
        {
            if (list.Count > 0 && list[^1].Changed == changed)
                list[^1] = new Segment(list[^1].Text + text, changed);
            else
                list.Add(new Segment(text, changed));
        }
    }
}
