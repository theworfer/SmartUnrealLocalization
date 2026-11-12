using System;
using System.Collections.Generic;
using System.Linq;
using SmartUnrealLocalization.Models;

using SmartUnrealLocalization.Localization;

namespace SmartUnrealLocalization.Services.Validators
{
    /// <summary>
    /// Heavy validator: groups rows by their Source text and NOTES any group
    /// whose Targets disagree, naming the other translations and the keys they
    /// live at so the difference can actually be looked at.
    ///
    /// <para>The note carries no one-click fix, but the row's popover lets the user
    /// walk the translations of that text and offer one of them to every row with
    /// the same Source — through the before → after review, row by row
    /// (<c>MainWindowViewModel.UseTranslationForSameSourceAsync</c>).</para>
    ///
    /// <para><b>A note, not a defect, and deliberately without a fix.</b> Earlier
    /// versions elected a "canonical" row and offered every other row a one-click
    /// Apply that overwrote its Target with the canonical value. That is the wrong
    /// default: each row has its own Key and therefore its own context, and a file
    /// assembled from several source languages diverges on purpose — so the button
    /// could erase a deliberate translation in one click, silently, in bulk. With
    /// the fix gone the canonical concept goes too: no row in the group is
    /// privileged, they simply disagree, and each one points at the others.</para>
    ///
    /// <para>Severity stays <c>Info</c>. The detail text is what the Problems tab
    /// shows on hover and what the row popover renders — the QC magnifier is for
    /// QC proposals only, so this is where the pointer has to live.</para>
    /// </summary>
    public sealed class DuplicateSourceValidator : IHeavyValidator
    {
        public string Kind => "DuplicateSource";

        // Trim long Source / Target strings so the 360 px popover doesn't blow
        // out vertically on essay-length translations.
        private const int DisplayMaxLength = 60;

        // How many sibling translations to name before "…". Three is enough to
        // see the shape of the disagreement without turning the note into a wall.
        private const int MaxNamedSiblings = 3;

        public void ValidateAll(IReadOnlyList<TranslationRow> rows)
        {
            // Bucket by Source (ordinal — case differences usually matter in
            // localization). Insertion order is preserved by Dictionary on
            // modern .NET, so the notes read in file order.
            var bySource = new Dictionary<string, List<TranslationRow>>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var src = row.Source ?? "";
                if (src.Length == 0) continue;
                if (!bySource.TryGetValue(src, out var list))
                    bySource[src] = list = new List<TranslationRow>();
                list.Add(row);
            }

            foreach (var (src, list) in bySource)
            {
                if (list.Count < 2) continue;

                // Untranslated rows are intentionally ignored — they are tracked by
                // the Untranslated counter and filter, not surfaced as problems, so
                // a partially-translated file only reports rows the user has
                // actually worked on (matching every other validator).
                var translated = list.Where(r => !r.IsUntranslated).ToList();
                if (translated.Count < 2) continue;

                var distinct = translated
                    .Select(r => r.Target ?? "")
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (distinct.Count < 2) continue;      // they all agree — nothing to say

                var srcDisplay = Truncate(src, DisplayMaxLength);

                foreach (var row in translated)
                {
                    var mine = row.Target ?? "";

                    // One example row per DIFFERENT translation: the key is the
                    // whole point — a note saying "this differs from something"
                    // that does not say from what cannot be acted on.
                    var others = translated
                        .Where(r => !string.Equals(r.Target ?? "", mine, StringComparison.Ordinal))
                        .GroupBy(r => r.Target ?? "", StringComparer.Ordinal)
                        .Select(g => g.First())
                        .ToList();
                    if (others.Count == 0) continue;

                    var named = string.Join(", ", others
                        .Take(MaxNamedSiblings)
                        .Select(r => string.Format(Loc.T("val.dup.entry"), Truncate(r.Target ?? "", DisplayMaxLength), r.Key)));
                    if (others.Count > MaxNamedSiblings) named += ", …";

                    row.Problems.Add(new Problem
                    {
                        Kind = Kind,
                        Severity = ProblemSeverity.Info,
                        Title = string.Format(Loc.T("val.dup.title"), srcDisplay, distinct.Count),
                        Detail = string.Format(Loc.T("val.dup.detail"), named),
                        // No Fix on purpose — see the class comment.
                    });
                }
            }
        }

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
