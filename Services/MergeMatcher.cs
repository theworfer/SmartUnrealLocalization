using System;
using System.Collections.Generic;
using System.Linq;
using Locres.IO;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services
{
    /// <summary>How a carried translation is proven to still belong to its row.</summary>
    public enum MergeVerification
    {
        /// <summary>Key match only — nothing confirms the original text is unchanged.</summary>
        KeyOnly,

        /// <summary>
        /// The old .locres entry's <c>SourceStringHash</c> equals the new one's.
        /// The hash fingerprints the ORIGINAL text, which a localized file does not
        /// contain, so this is the only proof available from a locres alone.
        /// </summary>
        Hash,

        /// <summary>
        /// The old file's source column equals the new version's text. Stronger than
        /// the hash because the actual wording is on hand, so a rejected row can be
        /// shown to the user instead of merely counted.
        /// </summary>
        SourceText,
    }

    /// <summary>One row of the OLD file, normalised so locres, CSV and PO look alike.</summary>
    public readonly record struct MergeSource(
        string Namespace,
        string Key,
        string Translation,     // what would be carried into Target
        uint? SourceHash,       // locres only
        string? SourceText);    // CSV/PO with a source column

    public sealed class MergeMatchResult
    {
        /// <summary>(namespace, key) → translation to write into Target.</summary>
        public Dictionary<(string Ns, string Key), string> Carried { get; init; } = new();

        /// <summary>Keys whose translation was REJECTED because the original changed.</summary>
        public HashSet<(string Ns, string Key)> Rejected { get; init; } = new();

        public MergeVerification Verification { get; init; }
        public bool UsedNamespace { get; init; }

        // Every count below is per ROW of the new version — one per grid line —
        // and the four outcomes add up to TotalRows exactly. They used to mix
        // scales: rows for some, DISTINCT KEYS for others, which a .locres may
        // legally repeat. On a file with duplicate keys the report then showed
        // numbers that did not add up to the row count it was reporting against.

        /// <summary>Rows in the new version — the size of the job.</summary>
        public int TotalRows { get; init; }

        /// <summary>Rows that get a translation back.</summary>
        public int CarriedRows { get; init; }

        /// <summary>Rows refused: the key matched but the original text changed.</summary>
        public int RejectedRows { get; init; }

        /// <summary>Rows whose key matched but the old file had no translation.</summary>
        public int EmptyRows { get; init; }

        /// <summary>Rows the old file has no key for at all.</summary>
        public int NewRows { get; init; }

        /// <summary>Keys the old file does not have — tinted green in the grid.</summary>
        public HashSet<(string Ns, string Key)> NewKeySet { get; init; } = new();

        /// <summary>Old keys the new version dropped — the game no longer reads them.</summary>
        public int RemovedKeys { get; init; }

        /// <summary>
        /// Only meaningful when matching WITHOUT namespaces (a 3-column CSV): rows
        /// whose bare key occurs in more than one namespace of the new file, so one
        /// old row was copied onto several unrelated entries.
        /// </summary>
        public int AmbiguousKeys { get; init; }

        /// <summary>
        /// Of the rows where verification actually had a say, how many passed — 0…1.
        ///
        /// <para>Rows with nothing to carry are excluded on purpose. Counting them
        /// made a half-translated old file read as "half the matches failed
        /// verification" and fire the alarm about rebuilt hashes, when
        /// verification had rejected nothing at all.</para>
        /// </summary>
        public double VerifiedShare =>
            CarriedRows + RejectedRows == 0 ? 1.0 : (double)CarriedRows / (CarriedRows + RejectedRows);
    }

    /// <summary>
    /// The matching half of "Merge to new version", kept pure so every rule can be
    /// tested without a window: which translations may be carried into the new
    /// version, and — just as important — which may NOT, and why.
    ///
    /// <para>The merge is the one operation in this app that can silently ruin a
    /// whole file: carry a translation onto an entry whose original text has since
    /// changed, and the game ships a line that says the wrong thing. Everything
    /// here exists to make that impossible to do by accident, and always countable
    /// before anything is written.</para>
    /// </summary>
    public static class MergeMatcher
    {
        /// <summary>
        /// Line endings only. A CSV that has been through Excel or Google Sheets
        /// comes back with CRLF where the locres had LF; comparing those literally
        /// fails on EVERY multi-line row and drops exactly the long, expensive
        /// translations. Nothing else is normalised — edge spaces are load-bearing
        /// in this app, and a difference in them is a real difference.
        /// </summary>
        internal static string NormalizeForCompare(string s)
            => s.IndexOf('\r') < 0 ? s : s.Replace("\r\n", "\n").Replace('\r', '\n');

        /// <summary>
        /// Normalise the old file's grid rows into <see cref="MergeSource"/>.
        ///
        /// <para>A CSV/PO with a filled Target column is a two-language file: Target
        /// is the translation, Source is what it was translated FROM and can verify
        /// the match. One with no Target at all is single-language like a locres —
        /// its Source column IS the translation, and nothing can verify it.</para>
        /// </summary>
        public static List<MergeSource> FromRows(IReadOnlyList<TranslationRow> rows, out bool hasTranslationColumn)
        {
            hasTranslationColumn = rows.Any(r => !string.IsNullOrEmpty(r.Target));

            var list = new List<MergeSource>(rows.Count);
            foreach (var r in rows)
            {
                string translation = hasTranslationColumn ? (r.Target ?? "") : (r.Source ?? "");
                string? verifyAgainst = hasTranslationColumn ? (r.Source ?? "") : null;
                list.Add(new MergeSource(r.Namespace ?? "", r.Key ?? "", translation, null, verifyAgainst));
            }
            return list;
        }

        /// <summary>Normalise an old .locres into <see cref="MergeSource"/> — value + hash.</summary>
        public static List<MergeSource> FromDocument(LocresDocument doc)
        {
            var list = new List<MergeSource>(doc.EntryCount);
            foreach (var ns in doc.Namespaces)
                foreach (var e in ns.Entries)
                    list.Add(new MergeSource(ns.Name, e.Key, e.Value, e.SourceStringHash, null));
            return list;
        }

        /// <summary>
        /// The strongest verification the old file can support. The user's
        /// "match by key only" setting overrides it to <see cref="MergeVerification.KeyOnly"/>.
        /// </summary>
        public static MergeVerification StrongestVerification(IReadOnlyList<MergeSource> old)
        {
            if (old.Count == 0) return MergeVerification.KeyOnly;
            if (old.Any(o => o.SourceHash.HasValue)) return MergeVerification.Hash;
            if (old.Any(o => o.SourceText is not null)) return MergeVerification.SourceText;
            return MergeVerification.KeyOnly;
        }

        /// <summary>
        /// Match the old file against the new version.
        ///
        /// <para>Namespaces are used whenever the old file has any. A 3-column CSV
        /// carries none, and then keys are matched bare — which is why
        /// <see cref="MergeMatchResult.AmbiguousKeys"/> is reported: a bare key can
        /// exist in several namespaces of a locres, and one old row would otherwise
        /// be copied onto all of them without a word.</para>
        /// </summary>
        public static MergeMatchResult Match(LocresDocument newVersion,
                                             IReadOnlyList<MergeSource> old,
                                             MergeVerification verification)
        {
            ArgumentNullException.ThrowIfNull(newVersion);
            ArgumentNullException.ThrowIfNull(old);

            bool useNamespace = old.Any(o => !string.IsNullOrEmpty(o.Namespace));

            // First occurrence wins, matching how row identity is resolved everywhere
            // else in the app (BuildOverrides, QcService.IndexRows, the QC badge).
            var index = new Dictionary<(string, string), MergeSource>();
            foreach (var o in old)
                index.TryAdd(Id(o.Namespace, o.Key, useNamespace), o);

            // Keys that occur in more than one namespace of the NEW file. Only a
            // hazard when matching without namespaces.
            var keyNamespaces = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            if (!useNamespace)
                foreach (var ns in newVersion.Namespaces)
                    foreach (var e in ns.Entries)
                    {
                        if (!keyNamespaces.TryGetValue(e.Key, out var set))
                            keyNamespaces[e.Key] = set = new HashSet<string>(StringComparer.Ordinal);
                        set.Add(ns.Name);
                    }

            var carried = new Dictionary<(string Ns, string Key), string>();
            var rejected = new HashSet<(string Ns, string Key)>();
            var newKeySet = new HashSet<(string Ns, string Key)>();
            int total = 0, carriedRows = 0, rejectedRows = 0, emptyRows = 0, newRows = 0, ambiguous = 0;

            foreach (var ns in newVersion.Namespaces)
                foreach (var entry in ns.Entries)
                {
                    total++;
                    var id = Id(ns.Name, entry.Key, useNamespace);

                    if (!index.TryGetValue(id, out var o))
                    {
                        newRows++;
                        newKeySet.Add((ns.Name, entry.Key));
                        continue;
                    }

                    if (!useNamespace && keyNamespaces.TryGetValue(entry.Key, out var owners) && owners.Count > 1)
                        ambiguous++;

                    if (o.Translation.Length == 0) { emptyRows++; continue; }

                    if (Verified(o, entry, verification))
                    {
                        carriedRows++;
                        carried[(ns.Name, entry.Key)] = o.Translation;
                    }
                    else
                    {
                        rejectedRows++;
                        rejected.Add((ns.Name, entry.Key));
                    }
                }

            // Old keys the new version no longer has.
            var newIds = new HashSet<(string, string)>();
            foreach (var ns in newVersion.Namespaces)
                foreach (var e in ns.Entries)
                    newIds.Add(Id(ns.Name, e.Key, useNamespace));
            int removed = old.Select(o => Id(o.Namespace, o.Key, useNamespace))
                             .Distinct()
                             .Count(k => !newIds.Contains(k));

            return new MergeMatchResult
            {
                Carried = carried,
                Rejected = rejected,
                NewKeySet = newKeySet,
                Verification = verification,
                UsedNamespace = useNamespace,
                TotalRows = total,
                CarriedRows = carriedRows,
                RejectedRows = rejectedRows,
                EmptyRows = emptyRows,
                NewRows = newRows,
                RemovedKeys = removed,
                AmbiguousKeys = ambiguous,
            };

            static (string, string) Id(string ns, string key, bool useNs) => (useNs ? ns : "", key);

            static bool Verified(MergeSource o, LocresEntry entry, MergeVerification mode) => mode switch
            {
                MergeVerification.KeyOnly => true,
                MergeVerification.Hash => o.SourceHash == entry.SourceStringHash,
                // entry.Value of the NEW version file IS the original text.
                MergeVerification.SourceText =>
                    o.SourceText is not null &&
                    string.Equals(NormalizeForCompare(o.SourceText),
                                  NormalizeForCompare(entry.Value), StringComparison.Ordinal),
                _ => true,
            };
        }
    }
}
