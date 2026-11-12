using System;
using System.Collections.Generic;
using Locres.IO;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services
{
    /// <summary>
    /// App-side glue between the LocresIO core and the grid's row model.
    /// Opening yields BOTH the rows and the parsed document skeleton — the
    /// skeleton (version, entry order, per-entry SourceStringHash) lives in the
    /// tab session and is what every save writes through, so the app can always
    /// save back to .locres without re-reading or copying the original file.
    /// </summary>
    public static class LocresFileService
    {
        /// <summary>Parse a .locres into (document skeleton, grid rows). Safe to call off the UI thread.</summary>
        public static (LocresDocument Document, List<TranslationRow> Rows) Open(string path)
        {
            // Route every decoded string through the app's per-load dedup pool
            // (same pattern as the CSV/PO importers).
            var pool = new Utils.StringPool();
            var doc = LocresDocument.Load(path, new LocresLoadOptions { Interner = pool.Intern });

            var rows = new List<TranslationRow>(doc.EntryCount);
            foreach (var ns in doc.Namespaces)
                foreach (var entry in ns.Entries)
                    rows.Add(new TranslationRow
                    {
                        Namespace = ns.Name,
                        Key = entry.Key,
                        Source = entry.Value,
                        Target = "",
                    });
            return (doc, rows);
        }

        /// <summary>Human-readable format badge for the project pill / logs.</summary>
        public static string VersionLabel(LocresVersion version) => version switch
        {
            LocresVersion.Legacy => "locres v0 (Legacy)",
            LocresVersion.Compact => "locres v1 (Compact)",
            LocresVersion.Optimized => "locres v2 (Optimized)",
            _ => "locres v3 (CityHash64)",
        };

        /// <summary>
        /// Snapshot the grid's translations for <see cref="LocresDocument.SaveWith"/>.
        /// MUST be called on the UI thread — the grid mutates rows there.
        /// Rows with an empty Target are omitted: SaveWith keeps the base value
        /// for them by contract, and omitting keeps the result counts meaningful.
        /// </summary>
        public static Dictionary<(string Ns, string Key), string?> BuildOverrides(IReadOnlyList<TranslationRow> rows)
        {
            var overrides = new Dictionary<(string Ns, string Key), string?>(rows.Count);
            foreach (var r in rows)
            {
                if (string.IsNullOrEmpty(r.Target)) continue;
                // FIRST translated row wins for a duplicated (namespace, key) —
                // duplicates are legal in .locres and all of them receive the same
                // override anyway. First-wins matches how row identity is resolved
                // everywhere else (QcService.IndexRows, the QC badge), so the row
                // the user sees flagged is the row whose text is written.
                overrides.TryAdd((r.Namespace ?? "", r.Key ?? ""), r.Target);
            }
            return overrides;
        }

        /// <summary>
        /// Overrides for packing arbitrary grid rows into a DIFFERENT base
        /// document (Build Locres from CSV). Rows are matched to base entries
        /// exactly by (Namespace, Key) first, then by the legacy flattened
        /// "ns/key" form that pre-LocresIO 3-column CSV exports carry in their
        /// Key column (including the leading slash of the anonymous namespace).
        /// </summary>
        public static Dictionary<(string Ns, string Key), string?> BuildOverridesForBase(
            LocresDocument baseDoc, IReadOnlyList<TranslationRow> rows)
        {
            var exact = new Dictionary<(string Ns, string Key), string>();
            var flattened = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in rows)
            {
                if (string.IsNullOrEmpty(r.Target)) continue;
                exact.TryAdd((r.Namespace ?? "", r.Key ?? ""), r.Target);   // first wins, as above
                if (string.IsNullOrEmpty(r.Namespace) && !string.IsNullOrEmpty(r.Key))
                    flattened.TryAdd(r.Key, r.Target);
            }

            var overrides = new Dictionary<(string Ns, string Key), string?>();
            foreach (var ns in baseDoc.Namespaces)
            {
                foreach (var entry in ns.Entries)
                {
                    if (exact.TryGetValue((ns.Name, entry.Key), out var translated)
                        || flattened.TryGetValue(ns.Name + "/" + entry.Key, out translated))
                    {
                        overrides[(ns.Name, entry.Key)] = translated;
                    }
                }
            }
            return overrides;
        }
    }
}
