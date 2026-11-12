using System.Collections.Generic;
using System.Text.RegularExpressions;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Utils
{
    /// <summary>
    /// Replace All, planned against the text as it is NOW.
    ///
    /// <para>Replace All used to splice at the offsets recorded by the last find
    /// scan. Anything that rewrote a Target after that scan — an AI batch, an
    /// undo, a pasted column, an MCP write — could leave those offsets in bounds
    /// while pointing at different characters: search "the", let a row become
    /// "xxx cat", press Replace All with "DOG", and the row became "DOG cat". The
    /// scan's lists were also capped for DISPLAY (60 hits a row, 5 000 in all),
    /// so a row with 100 occurrences had only 60 replaced. Found by the
    /// 2026-09-09 reliability audit.</para>
    ///
    /// <para>Planning is separate from applying on purpose: a pattern that blows
    /// its regex timeout throws here, before a single row has been written, so a
    /// Replace All either happens completely or not at all.</para>
    /// </summary>
    public static class FindReplace
    {
        /// <summary>One row's change: its Target before and after, and how many
        /// occurrences were replaced in it.</summary>
        public readonly record struct TargetEdit(TranslationRow Row, string OldText, string NewText, int Replacements);

        /// <summary>
        /// Every Target in <paramref name="rows"/> with each match of
        /// <paramref name="matcher"/> replaced by <paramref name="replacement"/>.
        /// Rows that would not change are left out.
        ///
        /// <para>Semantics match what the find bar has always done:
        /// <list type="bullet">
        /// <item>the replacement is LITERAL — in regex mode "$1" is inserted as
        /// the two characters "$1", not as a group reference;</item>
        /// <item>zero-width matches are skipped, as the scan skips them — a
        /// pattern like <c>x*</c> would otherwise insert the replacement between
        /// every pair of characters;</item>
        /// <item>there is no cap: the display caps bound what the bar draws, not
        /// what Replace All means.</item>
        /// </list></para>
        ///
        /// <para>Throws <see cref="RegexMatchTimeoutException"/> if the pattern
        /// exceeds its timeout on any row; nothing has been written when it does.</para>
        /// </summary>
        public static List<TargetEdit> PlanReplaceAll(IEnumerable<TranslationRow> rows, Regex matcher, string replacement)
        {
            var plan = new List<TargetEdit>();
            foreach (var row in rows)
            {
                string old = row.Target ?? "";
                if (old.Length == 0) continue;

                int count = 0;
                string updated = matcher.Replace(old, m =>
                {
                    if (m.Length == 0) return m.Value;
                    count++;
                    return replacement;
                });

                if (count > 0 && updated != old)
                    plan.Add(new TargetEdit(row, old, updated, count));
            }
            return plan;
        }
    }
}
