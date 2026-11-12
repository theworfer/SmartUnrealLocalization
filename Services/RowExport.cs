using System.Collections.Generic;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services
{
    /// <summary>
    /// Row shaping applied just before an export writes a file.
    /// </summary>
    public static class RowExport
    {
        /// <summary>
        /// Collapse the two columns into ONE language: each row's final text is
        /// its Target when translated, otherwise its Source — the exact rule
        /// <c>LocresDocument.SaveWith</c> already applies natively (an empty
        /// target keeps the base value). The result goes into Source with Target
        /// left empty, so re-importing the file yields a single-language file
        /// just like re-opening a saved .locres.
        ///
        /// <para>This is what makes the QC flow closable for CSV/PO: reviewing a
        /// single-language file puts the translation in Source and accepted fixes
        /// in Target, and flattening turns that pair back into one corrected
        /// column. Offered as a separate export so the ordinary two-column
        /// export — by far the more common one — never changes behaviour.</para>
        ///
        /// <para>Namespace, Key and every other field are copied unchanged; the
        /// input rows are not mutated.</para>
        /// </summary>
        public static List<TranslationRow> Flatten(IEnumerable<TranslationRow> rows)
        {
            var result = new List<TranslationRow>();
            foreach (var r in rows)
            {
                string final = string.IsNullOrEmpty(r.Target) ? r.Source ?? "" : r.Target;
                result.Add(new TranslationRow
                {
                    Namespace = r.Namespace,
                    Key = r.Key,
                    Source = final,
                    Target = "",
                    Comment = r.Comment,
                });
            }
            return result;
        }
    }
}
