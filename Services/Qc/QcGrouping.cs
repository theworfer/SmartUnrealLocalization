using System;
using System.Collections.Generic;
using System.Linq;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services.Qc
{
    /// <summary>
    /// Turns the pending proposals of a file into the QC tab's category rows.
    ///
    /// <para>Pure and static on purpose: this is the part with the arithmetic the
    /// user actually reads ("Fix 412"), and a number that disagrees with what the
    /// button does is worse than no button. Keeping it out of the view model is
    /// what makes it testable at all.</para>
    /// </summary>
    internal static class QcGrouping
    {
        /// <summary>The category a proposal belongs to — its first firing check.</summary>
        public static string FirstKind(QcProposal p) => p.Kinds.Count > 0 ? p.Kinds[0] : "Qc";

        /// <summary>Is this proposal one the bulk buttons can actually apply?</summary>
        public static bool IsApplicable(TranslationRow row, QcProposal p)
            => p.Verdict == QcVerdict.Pending
               && !string.Equals(p.Before, p.After, StringComparison.Ordinal)
               && !p.IsStaleFor(row);

        public sealed class Result
        {
            public List<QcGroupItem> Groups { get; } = new();

            /// <summary>Suggestions carrying a replacement, across all categories.</summary>
            public int Fixable { get; set; }

            /// <summary>Findings with no machine fix — the ones that need a human.</summary>
            public int Review { get; set; }
        }

        /// <summary>
        /// Build the category rows, ordered worst-severity first then largest.
        /// Only the DISPLAYED items are capped by <paramref name="maxItemsPerGroup"/>;
        /// every count covers the whole category, because the counts are what the
        /// bulk buttons act on.
        /// </summary>
        public static Result Build(IEnumerable<TranslationRow> rows, int maxItemsPerGroup,
                                   ISet<string>? keepExpanded = null)
        {
            var result = new Result();
            var groups = new Dictionary<string, QcGroupItem>(StringComparer.Ordinal);

            foreach (var row in rows)
            {
                if (row.QcProposal is not { Verdict: QcVerdict.Pending } proposal) continue;

                var item = new ProblemListItem(row, proposal);
                if (item.HasFix) result.Fixable++; else result.Review++;

                string kind = FirstKind(proposal);
                if (!groups.TryGetValue(kind, out var group))
                    groups[kind] = group = new QcGroupItem(kind)
                    {
                        IsExpanded = keepExpanded?.Contains(kind) == true,
                    };

                if (group.Items.Count < maxItemsPerGroup) group.Items.Add(item);
                group.Count++;
                if (item.HasFix) group.FixableCount++;
                if (item.Severity > group.Severity) group.Severity = item.Severity;
            }

            result.Groups.AddRange(groups.Values
                .OrderByDescending(g => (int)g.Severity)
                .ThenByDescending(g => g.Count));
            return result;
        }

        /// <summary>
        /// Project categories into the flat row list the QC tab binds to: each
        /// header, followed by its items when it is expanded.
        /// </summary>
        public static List<object> Flatten(IEnumerable<QcGroupItem> groups)
        {
            var rows = new List<object>();
            foreach (var g in groups)
            {
                rows.Add(g);
                if (g.IsExpanded) rows.AddRange(g.Items);
            }
            return rows;
        }

        /// <summary>
        /// Splice one category's children in or out of an already-built flat list,
        /// in place.
        ///
        /// <para>Deliberately not a rebuild: expansion is driven from a
        /// PointerPressed handler on the header element itself, and clearing the
        /// bound collection would tear down the control mid-event. It also keeps
        /// the rest of the list's selection and scroll position intact.</para>
        /// </summary>
        public static void SetExpanded(IList<object> rows, QcGroupItem group, bool expanded)
        {
            if (group.IsExpanded == expanded) return;

            int index = rows.IndexOf(group);
            if (index < 0) { group.IsExpanded = expanded; return; }

            group.IsExpanded = expanded;
            if (expanded)
                for (int i = 0; i < group.Items.Count; i++)
                    rows.Insert(index + 1 + i, group.Items[i]);
            else
                for (int i = 0; i < group.Items.Count; i++)
                    rows.RemoveAt(index + 1);   // children are contiguous after the header
        }
    }
}
