using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services
{
    /// <summary>
    /// CSV import/export that preserves Namespace column (when present).
    /// Supports 2-column (key, text), 3-column (key, source, target),
    /// and 4-column (namespace, key, source, target) layouts.
    /// </summary>
    public static class CsvFileService
    {
        public static List<TranslationRow> Import(string filePath)
            => Import(filePath, out _);

        /// <summary><paramref name="warning"/> reports layout anomalies (mixed
        /// column counts) the user should know about — fields may be misaligned.</summary>
        public static List<TranslationRow> Import(string filePath, out string? warning)
        {
            var rows = new List<TranslationRow>();
            var columnShapes = new Dictionary<int, int>(); // colCount → row count
            // Dedup identical strings across rows (same source/target/namespace repeat heavily) —
            // each distinct value is stored once instead of per row. Pool is discarded on return.
            var pool = new Utils.StringPool();
            // Decode ONCE, detecting the encoding: a non-UTF-8 file used to be read
            // with U+FFFD substitutions that destroyed the original bytes silently.
            string text = Utils.TextUtils.ReadTextDetectingEncoding(filePath, out string? encodingWarning);
            using var reader = new StringReader(text);
            // BadDataFound = null → don't throw on stray quotes; ignore the offending field.
            // This keeps Import robust against locres files exported with quirky escaping.
            using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = false,
                BadDataFound = null,
                MissingFieldFound = null,
            });

            bool first = true;

            try
            {
                while (csv.Read())
                {
                    if (first)
                    {
                        first = false;
                        // Header detection needs TWO matching fields — a real data row
                        // whose key happens to be the word "key" must not be dropped.
                        var f0 = (csv.GetField(0) ?? "").Trim().ToLowerInvariant();
                        var f1 = csv.Parser.Count > 1 ? (csv.GetField(1) ?? "").Trim().ToLowerInvariant() : "";
                        bool isHeader =
                            (f0 == "key" && (f1 == "source" || f1 == "src" || f1 == "text" || f1 == "original")) ||
                            (f0 == "namespace" && f1 == "key") ||
                            f0 == "msgid" || f0 == "#";
                        if (isHeader) continue;
                    }

                    // Read column count PER ROW — previously detected once from the first row,
                    // which silently misaligned fields if later rows had a different shape.
                    int colCount = csv.Parser.Count;
                    columnShapes[colCount] = columnShapes.TryGetValue(colCount, out var shapeRows) ? shapeRows + 1 : 1;

                    var row = new TranslationRow();
                    switch (colCount)
                    {
                        case >= 4:
                            row.Namespace = pool.Intern(csv.GetField(0));
                            row.Key = pool.Intern(csv.GetField(1));
                            row.Source = pool.Intern(csv.GetField(2));
                            row.Target = pool.Intern(csv.GetField(3));
                            break;
                        case 3:
                            row.Key = pool.Intern(csv.GetField(0));
                            row.Source = pool.Intern(csv.GetField(1));
                            row.Target = pool.Intern(csv.GetField(2));
                            break;
                        case 2:
                            row.Key = pool.Intern(csv.GetField(0));
                            row.Source = pool.Intern(csv.GetField(1));
                            row.Target = "";
                            break;
                        default:
                            // 1 column or 0 — keep only the key, skip blanks
                            row.Key = pool.Intern(csv.GetField(0));
                            break;
                    }

                    if (!string.IsNullOrEmpty(row.Key))
                        rows.Add(row);
                }
            }
            catch (CsvHelperException ex)
            {
                // Wrap CsvHelper's internal exceptions with a recognizable type and row hint
                // so callers (MainWindowViewModel) can show a meaningful StatusText.
                int rowNum = 0;
                try { rowNum = csv.Parser.Row; } catch { }
                throw new InvalidDataException(
                    $"CSV parse error near row {rowNum}: {ex.Message}", ex);
            }

            warning = columnShapes.Count > 1
                ? "mixed column layout: " + string.Join(", ",
                      columnShapes.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} rows × {kv.Key} cols"))
                  + " — some fields may be misaligned, check the file"
                : null;

            // An unterminated quote makes CsvHelper silently swallow the REST OF THE
            // FILE into one field (rows collapse, no exception, and with a single
            // resulting shape the mixed-layout warning above stays quiet). Replay the
            // quote state over the raw text: a well-formed CSV ends OUTSIDE quotes.
            try
            {
                if (EndsInsideQuote(text))
                {
                    const string quoteWarn = "a quote is never closed — everything after it was read as one field, rows may have merged";
                    warning = warning == null ? quoteWarn : warning + "; " + quoteWarn;
                }
            }
            catch { /* detection is best-effort */ }

            // Encoding first: it explains every other oddity in the file.
            if (encodingWarning != null)
                warning = warning == null ? encodingWarning : encodingWarning + "; " + warning;

            return rows;
        }

        /// <summary>Quote-state replay for the unterminated-quote warning above.</summary>
        private static bool EndsInsideQuote(string text)
        {
            bool inQuotes = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (!inQuotes)
                {
                    if (c == '"') inQuotes = true;
                }
                else if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') i++; // escaped ""
                    else inQuotes = false;
                }
            }
            return inQuotes;
        }

        public static void Export(string filePath, IEnumerable<TranslationRow> rows, bool includeNamespace = false)
            => Utils.AtomicFile.Write(filePath, stream => WriteCsv(stream, rows, includeNamespace));

        private static void WriteCsv(Stream stream, IEnumerable<TranslationRow> rows, bool includeNamespace)
        {
            using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
            using var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture));

            // Header
            if (includeNamespace) csv.WriteField("namespace");
            csv.WriteField("key"); csv.WriteField("source"); csv.WriteField("target");
            csv.NextRecord();

            foreach (var r in rows)
            {
                if (includeNamespace) csv.WriteField(r.Namespace);
                csv.WriteField(r.Key); csv.WriteField(r.Source); csv.WriteField(r.Target);
                csv.NextRecord();
            }
        }
    }
}
