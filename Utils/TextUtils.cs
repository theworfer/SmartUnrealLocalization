using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SmartUnrealLocalization.Utils
{
    public static class TextUtils
    {
        /// <summary>
        /// Read a text file, detecting its encoding instead of assuming UTF-8.
        /// A BOM decides; otherwise the bytes must be valid UTF-8.
        ///
        /// <para>Anything else is decoded as Latin-1 and REPORTED. Latin-1 maps
        /// every byte 1:1, so the content survives and the user can re-save the
        /// file correctly — whereas the previous hard-coded UTF-8 read replaced
        /// each invalid byte with U+FFFD, silently destroying the original bytes.
        /// The codepage is deliberately not guessed: telling Windows-1251 from
        /// Windows-1252 by heuristics is unreliable, and a wrong guess is just a
        /// quieter kind of corruption.</para>
        /// </summary>
        public static string ReadTextDetectingEncoding(string path, out string? warning)
        {
            warning = null;
            byte[] bytes = File.ReadAllBytes(path);

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

            try
            {
                return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                warning = "the file is not UTF-8 — it was read byte-for-byte as Latin-1, so accented and "
                        + "Cyrillic text will look wrong; re-save the file as UTF-8 and import it again";
                return Encoding.Latin1.GetString(bytes);
            }
        }

        /// <summary>
        /// Parses clipboard text (possibly from Excel/Sheets) into individual lines,
        /// respecting CSV-style quoting with escaped double-quotes inside quoted fields.
        /// Handles \r\n, \n, and trailing newline stripping.
        /// </summary>
        public static string[] ParseClipboardLines(string text)
        {
            var cells = ParseClipboardCells(text);
            // A spreadsheet ends its copy with a line break: that last break
            // terminates the final row, it does not start an empty one.
            if (cells.Count > 0 && cells[^1].Length == 0 && text.EndsWith('\n'))
                cells.RemoveAt(cells.Count - 1);
            return cells.ToArray();
        }

        /// <summary>
        /// The clipboard text as the lines to put into <paramref name="expected"/> rows.
        ///
        /// <para>Whether a final line break ends the last row (a spreadsheet's copy)
        /// or separates it from an empty one (this app's copy of a column whose last
        /// cells are empty) cannot be read from the text — "a⏎⏎" is one row from Excel
        /// and "a", "" from here. The row count the paste is going into decides: an
        /// exact fit wins, then the fit without the final empty line. Without this a
        /// column copied here refused to paste back whenever it ended in empty cells.</para>
        /// </summary>
        public static string[] ParseClipboardLines(string text, int expected)
        {
            var cells = ParseClipboardCells(text);
            if (cells.Count == expected) return cells.ToArray();
            if (cells.Count > 0 && cells[^1].Length == 0 && text.EndsWith('\n'))
                cells.RemoveAt(cells.Count - 1);
            return cells.ToArray();
        }

        /// <summary>
        /// Split clipboard text into cells, one per line, the way a spreadsheet writes
        /// them: a cell that STARTS with a double quote is quoted (line breaks inside
        /// belong to it, <c>""</c> is one quote); a quote anywhere else is an ordinary
        /// character — <c>5" pipe</c> used to open a "quoted" run that swallowed the
        /// following lines. A final line break yields a final empty cell; the callers
        /// decide what it means. Text whose quotes never close is split line by line.
        /// </summary>
        internal static List<string> ParseClipboardCells(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;

            bool inQuotes = false, cellStart = true;
            var current = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inQuotes)
                {
                    if (c != '"') { current.Append(c); continue; }
                    if (i + 1 < text.Length && text[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                    continue;
                }
                if (c == '"' && cellStart) { inQuotes = true; cellStart = false; continue; }
                if (c == '\n' || (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n'))
                {
                    if (c == '\r') i++;
                    result.Add(current.ToString());
                    current.Clear();
                    cellStart = true;
                    continue;
                }
                current.Append(c);
                cellStart = false;
            }
            result.Add(current.ToString());

            if (inQuotes)
                return new List<string>(text.Replace("\r\n", "\n").Split('\n'));
            return result;
        }

        /// <summary>
        /// CSV-quote a single cell value for the clipboard so embedded newlines,
        /// tabs and quotes survive a round-trip through <see cref="ParseClipboardLines"/>
        /// (and paste cleanly into Excel/Sheets). A value containing <c>\n</c>,
        /// <c>\r</c>, <c>\t</c> or <c>"</c> is wrapped in double-quotes with inner
        /// quotes doubled; otherwise it is returned unchanged. Null → empty string.
        ///
        /// <para>Every clipboard copy path (bulk selection, Copy column, and the row
        /// context-menu Copy Source/Target/Key) MUST route through this so a copied
        /// column and a pasted column agree on row count — a raw <c>string.Join("\n", …)</c>
        /// splits multi-line cells into extra rows.</para>
        /// </summary>
        public static string EscapeForClipboard(string? val)
        {
            if (string.IsNullOrEmpty(val)) return "";
            if (val.Contains('\n') || val.Contains('\r') || val.Contains('\t') || val.Contains('"'))
                return $"\"{val.Replace("\"", "\"\"")}\"";
            return val;
        }

        /// <summary>
        /// Convert a validator <c>Problem.Kind</c> (PascalCase, e.g.
        /// <c>"TagMismatch"</c>) into a SCREAMING-KEBAB label
        /// (<c>"TAG-MISMATCH"</c>) for the popover header and Problems-tab rows.
        /// Already-uppercase / dashed input passes through unchanged-ish.
        /// </summary>
        public static string KindToLabel(string? kind)
        {
            if (string.IsNullOrEmpty(kind)) return string.Empty;
            var sb = new StringBuilder(kind.Length + 4);
            for (int i = 0; i < kind.Length; i++)
            {
                char c = kind[i];
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(kind[i - 1]) && kind[i - 1] != '-')
                    sb.Append('-');
                sb.Append(char.ToUpperInvariant(c));
            }
            return sb.ToString();
        }
    }
}
