using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services
{
    /// <summary>
    /// Gettext .po parser/generator with round-trip fidelity.
    ///
    /// <para>What survives import → edit → export unchanged: translator and
    /// extracted comments (<c>#</c>, <c>#.</c>, <c>#:</c>, <c>#|</c>), flags
    /// (<c>#,</c> — incl. <c>fuzzy</c>, which is dropped automatically when the
    /// translation is edited, matching Poedit), plural entries
    /// (<c>msgid_plural</c> / <c>msgstr[N]</c> — the grid edits form 0, the
    /// remaining forms are preserved verbatim). Obsolete entries (<c>#~</c>)
    /// are dropped. Entries may be separated by comments alone (no blank line
    /// required); duplicates keep the first occurrence.</para>
    ///
    /// <para>msgctxt carries the row Key; namespace-aware files (our
    /// X-Generator marker or Unreal's) pack it as "Namespace,Key" — see
    /// <see cref="Import(string, out string?)"/>.</para>
    /// </summary>
    public static class PoFileService
    {
        /// <summary>Round-trip payload attached to <see cref="TranslationRow.FormatMeta"/>.</summary>
        public sealed class PoEntryMeta
        {
            /// <summary>Raw comment lines verbatim (translator #, extracted #., references #:, previous #|).</summary>
            public List<string> CommentLines { get; } = new();
            /// <summary>Flags from the "#," line ("fuzzy", "c-format", …).</summary>
            public List<string> Flags { get; } = new();
            /// <summary>msgid_plural, when the entry is plural.</summary>
            public string? MsgidPlural { get; set; }
            /// <summary>msgstr[1..N] preserved verbatim (form 0 lives in the row's Target).</summary>
            public List<string> PluralTargets { get; } = new();
            /// <summary>Original msgstr[N] indexes aligned with <see cref="PluralTargets"/> —
            /// preserved so a (malformed but real) gapped set like [0],[2] is not silently
            /// renumbered to [0],[1] on export, which would relabel plural forms.</summary>
            public List<int> PluralIndexes { get; } = new();
            /// <summary>Target value at import time — editing past it clears the fuzzy flag on export.</summary>
            public string ImportedTarget { get; set; } = "";
        }

        public static List<TranslationRow> Import(string filePath)
            => Import(filePath, out _, out _);

        public static List<TranslationRow> Import(string filePath, out string? warning)
            => Import(filePath, out warning, out _);

        /// <summary>
        /// Parse a .po file. <paramref name="warning"/> summarizes anything the
        /// user should know (fuzzy count, plural entries, skipped duplicates).
        /// <paramref name="header"/> receives the raw header msgstr (the
        /// "Key: value\n" block — Plural-Forms, Project-Id-Version, …) so Export
        /// can carry it back out instead of discarding it.
        /// </summary>
        public static List<TranslationRow> Import(string filePath, out string? warning, out string? header)
        {
            var rows = new List<TranslationRow>();
            var pool = new Utils.StringPool();
            // Detect the encoding rather than assuming UTF-8: a .po written in a
            // legacy codepage used to import with U+FFFD substitutions, destroying
            // the original bytes with nothing said about it.
            var lines = Utils.TextUtils
                .ReadTextDetectingEncoding(filePath, out string? encodingWarning)
                .Split('\n');
            for (int i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd('\r');

            bool nsAwareCtx = false;
            bool headerSeen = false;
            string? capturedHeader = null; // raw header msgstr — surfaced via the out param
            int fuzzyCount = 0, pluralCount = 0, duplicateCount = 0;
            var seenKeys = new HashSet<(string Ctx, string Id)>();

            // ── per-entry accumulation state ──
            var comments = new List<string>();
            var flags = new List<string>();
            string? msgctxt = null, msgid = null, msgidPlural = null, msgstr = null;
            var plurals = new SortedDictionary<int, string>();
            bool obsolete = false;
            // What the current continuation line ("...") appends to.
            Action<string>? appendTo = null;

            void Reset()
            {
                comments = new List<string>();
                flags = new List<string>();
                msgctxt = null; msgid = null; msgidPlural = null; msgstr = null;
                plurals = new SortedDictionary<int, string>();
                obsolete = false;
                appendTo = null;
            }

            void Flush()
            {
                if (msgid == null || obsolete) { Reset(); return; }

                // Header entry: msgid "" — capture metadata, not a row.
                if (!headerSeen && msgid.Length == 0 && msgctxt == null)
                {
                    headerSeen = true;
                    var headerText = msgstr ?? "";
                    capturedHeader = headerText.Length > 0 ? headerText : null;
                    nsAwareCtx = headerText.Contains("X-Generator: SmartUnrealLocalization")
                              || headerText.Contains("Unreal Engine");
                    Reset();
                    return;
                }
                headerSeen = true;

                if (!seenKeys.Add((msgctxt ?? "", msgid)))
                {
                    duplicateCount++; // first occurrence wins — a duplicate would make msgfmt reject our export
                    Reset();
                    return;
                }

                string ns = "";
                string keyCtx = msgctxt ?? msgid;
                if (nsAwareCtx && msgctxt != null)
                {
                    int comma = msgctxt.IndexOf(',');
                    if (comma >= 0)
                    {
                        ns = msgctxt[..comma];
                        keyCtx = msgctxt[(comma + 1)..];
                    }
                }

                string target = plurals.TryGetValue(0, out var form0) ? form0 : (msgstr ?? "");
                var meta = new PoEntryMeta { MsgidPlural = msgidPlural, ImportedTarget = target };
                meta.CommentLines.AddRange(comments);
                meta.Flags.AddRange(flags);
                foreach (var kv in plurals)
                    if (kv.Key > 0) { meta.PluralTargets.Add(kv.Value); meta.PluralIndexes.Add(kv.Key); }

                if (flags.Contains("fuzzy")) fuzzyCount++;
                if (msgidPlural != null) pluralCount++;

                rows.Add(new TranslationRow
                {
                    Namespace = pool.Intern(ns),
                    Key = pool.Intern(keyCtx),
                    Source = pool.Intern(msgid),
                    Target = pool.Intern(target),
                    FormatMeta = meta,
                });
                Reset();
            }

            bool EntryHasContent() => msgid != null;

            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r');

                if (string.IsNullOrWhiteSpace(line)) { Flush(); continue; }

                if (line.StartsWith("#"))
                {
                    // A comment after a complete entry starts the NEXT entry — PO
                    // does not require a blank line between entries.
                    if (EntryHasContent()) Flush();

                    if (line.StartsWith("#~")) { obsolete = true; continue; } // obsolete block — dropped
                    if (line.StartsWith("#,"))
                    {
                        foreach (var f in line[2..].Split(','))
                        {
                            var t = f.Trim();
                            if (t.Length > 0) flags.Add(t);
                        }
                    }
                    else
                    {
                        comments.Add(line);
                    }
                    continue;
                }

                if (line.StartsWith("msgctxt "))
                {
                    if (EntryHasContent()) Flush();
                    msgctxt = ExtractQuoted(line, "msgctxt ".Length);
                    appendTo = s => msgctxt += s;
                }
                else if (line.StartsWith("msgid_plural "))
                {
                    msgidPlural = ExtractQuoted(line, "msgid_plural ".Length);
                    appendTo = s => msgidPlural += s;
                }
                else if (line.StartsWith("msgid "))
                {
                    // A new msgid while the previous entry already has a translation
                    // means a new entry began without any separator line.
                    if (msgstr != null || plurals.Count > 0) Flush();
                    msgid = ExtractQuoted(line, "msgid ".Length);
                    appendTo = s => msgid += s;
                }
                else if (line.StartsWith("msgstr["))
                {
                    int close = line.IndexOf(']');
                    if (close > 7 && int.TryParse(line.AsSpan(7, close - 7), out int idx))
                    {
                        plurals[idx] = ExtractQuoted(line, close + 1);
                        appendTo = s => plurals[idx] += s;
                    }
                }
                else if (line.StartsWith("msgstr "))
                {
                    msgstr = ExtractQuoted(line, "msgstr ".Length);
                    appendTo = s => msgstr += s;
                }
                else if (line.StartsWith("\"") && appendTo != null)
                {
                    // Strip exactly ONE quote each side (ExtractQuoted), NOT Trim('"'):
                    // a wrapped segment ending in an escaped quote (…\") would otherwise
                    // lose its closing " and keep a dangling backslash.
                    appendTo(ExtractQuoted(line, 0));
                }
            }
            Flush();

            var notes = new List<string>();
            if (encodingWarning != null) notes.Add(encodingWarning);   // explains every other oddity
            if (fuzzyCount > 0) notes.Add($"{fuzzyCount} fuzzy (unreviewed) translations imported — verify them");
            if (pluralCount > 0) notes.Add($"{pluralCount} plural entries (the grid edits form 0; other forms are preserved)");
            if (duplicateCount > 0) notes.Add($"{duplicateCount} duplicate entries skipped (first kept)");
            warning = notes.Count > 0 ? string.Join("; ", notes) : null;
            header = capturedHeader;
            return rows;
        }

        /// <summary>
        /// Export rows to .po. Per-entry metadata from a previous PO import
        /// (comments, flags, plural forms) is written back; the fuzzy flag is
        /// dropped for rows whose Target changed since import (Poedit semantics).
        /// </summary>
        public static void Export(string filePath, IEnumerable<TranslationRow> rows, string? language = null,
                                  string? originalHeader = null)
        {
            var sb = new StringBuilder();

            // Header. X-Generator marks the file as namespace-aware: msgctxt may
            // carry "Namespace,Key" (UE's own convention) — Import splits it only
            // when this marker (or Unreal's) is present.
            sb.Append("# Smart Unreal Localization — Generated .po file\n");
            sb.Append("msgid \"\"\n");
            sb.Append("msgstr \"\"\n");
            sb.Append("\"Content-Type: text/plain; charset=UTF-8\\n\"\n");
            sb.Append("\"Content-Transfer-Encoding: 8bit\\n\"\n");
            string? langValue = !string.IsNullOrWhiteSpace(language) ? language.Trim() : null;
            if (!string.IsNullOrEmpty(originalHeader))
            {
                // Carry the imported header's OTHER fields back out (Plural-Forms,
                // Project-Id-Version, Language-Team, …) — dropping them made every
                // non-Germanic language compile with the wrong plural rule. Fields
                // we own (charset/encoding/X-Generator) stay forced; an explicit
                // language argument wins over the original Language field.
                foreach (var rawLine in originalHeader.Split('\n'))
                {
                    var line = rawLine.TrimEnd('\r').Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (line.StartsWith("Content-Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (line.StartsWith("X-Generator:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (line.StartsWith("Language:", StringComparison.OrdinalIgnoreCase))
                    {
                        langValue ??= line["Language:".Length..].Trim();
                        continue;
                    }
                    sb.Append(EscapePoLine(line + "\n")).Append('\n');
                }
            }
            if (langValue != null)
                sb.Append($"\"Language: {langValue}\\n\"\n");
            sb.Append("\"X-Generator: SmartUnrealLocalization\\n\"\n");
            sb.Append('\n');

            // Seeded with the header's own msgid "" so a blank row (no context, empty
            // source) can't emit a SECOND msgid "" — msgfmt rejects a duplicate empty id.
            var written = new HashSet<(string Ctx, string Id)> { ("", "") };
            foreach (var row in rows)
            {
                var meta = row.FormatMeta as PoEntryMeta;

                // Pack (namespace, key) into msgctxt the way UE does — "Namespace,Key",
                // split on the FIRST comma on import. A non-empty namespace anchors the
                // split (the key keeps any later commas). With NO namespace, a comma inside
                // the key would be mis-read as a namespace boundary, so emit a leading comma
                // to pin ns="" (Import recovers ns="", key=rest). Also emit when only the
                // namespace is set (empty key) so "Namespace," round-trips.
                string? ctx = null;
                if (!string.IsNullOrEmpty(row.Namespace))
                    ctx = row.Namespace + "," + row.Key;
                else if (!string.IsNullOrEmpty(row.Key))
                    ctx = row.Key.Contains(',') ? "," + row.Key : row.Key;

                // msgfmt rejects duplicate (msgctxt, msgid) definitions — keep the first.
                if (!written.Add((ctx ?? "", row.Source ?? "")))
                    continue;

                if (meta != null)
                {
                    foreach (var c in meta.CommentLines)
                        sb.Append(c).Append('\n');

                    var flags = new List<string>(meta.Flags);
                    // Editing a fuzzy translation confirms it — drop the flag.
                    if ((row.Target ?? "") != meta.ImportedTarget)
                        flags.Remove("fuzzy");
                    if (flags.Count > 0)
                        sb.Append("#, ").Append(string.Join(", ", flags)).Append('\n');
                }

                if (ctx != null)
                    sb.Append("msgctxt ").Append(EscapePoLine(ctx)).Append('\n');

                sb.Append("msgid ").Append(EscapePoLine(row.Source ?? "")).Append('\n');

                if (meta?.MsgidPlural != null)
                {
                    sb.Append("msgid_plural ").Append(EscapePoLine(meta.MsgidPlural)).Append('\n');
                    sb.Append("msgstr[0] ").Append(EscapePoLine(row.Target ?? "")).Append('\n');
                    for (int i = 0; i < meta.PluralTargets.Count; i++)
                    {
                        // Original indexes preserved (gapped input stays gapped) —
                        // renumbering to a contiguous 1..N relabelled plural forms.
                        int idx = i < meta.PluralIndexes.Count ? meta.PluralIndexes[i] : i + 1;
                        sb.Append($"msgstr[{idx}] ").Append(EscapePoLine(meta.PluralTargets[i])).Append('\n');
                    }
                }
                else
                {
                    sb.Append("msgstr ").Append(EscapePoLine(row.Target ?? "")).Append('\n');
                }
                sb.Append('\n');
            }

            Utils.AtomicFile.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(false));
        }

        private static string ExtractQuoted(string line, int prefixLength)
        {
            var quoted = line[prefixLength..].Trim();
            if (quoted.Length >= 2 && quoted.StartsWith('"') && quoted.EndsWith('"'))
                quoted = quoted[1..^1];
            return UnescapePo(quoted);
        }

        private static string EscapePoLine(string text)
        {
            var sb = new StringBuilder(text.Length + 8);
            sb.Append('"');
            foreach (var c in text)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\a': sb.Append("\\a"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\v': sb.Append("\\v"); break;
                    case '\0': sb.Append("\\0"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// Single-pass unescape. (The old chained string.Replace corrupted inputs
        /// like <c>\\n</c> — an escaped backslash followed by a literal 'n' became
        /// a backslash + newline.) Unknown escapes pass through verbatim.
        /// </summary>
        private static string UnescapePo(string text)
        {
            if (text.IndexOf('\\') < 0) return text;
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '\\' || i + 1 >= text.Length)
                {
                    sb.Append(c);
                    continue;
                }
                char n = text[++i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'a': sb.Append('\a'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'v': sb.Append('\v'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '0': sb.Append('\0'); break;
                    default: sb.Append('\\').Append(n); break;
                }
            }
            return sb.ToString();
        }
    }
}
