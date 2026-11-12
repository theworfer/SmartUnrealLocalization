using System;

namespace SmartUnrealLocalization.Localization
{
    /// <summary>
    /// CLDR cardinal plural categories, for the languages this app ships.
    ///
    /// <para>English gets away with two forms, so the app used to write
    /// "{0} row(s)" and call it done. Ukrainian rendered that as
    /// "Замінено в 1 рядку(ах)" — visibly machine-made. Polish and Russian need
    /// three forms and would have read the same way; Chinese, Japanese, Korean
    /// and Vietnamese need exactly one, so the parenthetical there is pure
    /// noise.</para>
    ///
    /// <para>Only integers reach this code (row counts, file counts), so the
    /// CLDR operands reduce to <c>n = i</c> with <c>v = 0</c>. That collapses
    /// the published rules to what you see below. Negative counts cannot occur;
    /// they are folded to their absolute value rather than throwing, because a
    /// status message is the wrong place to crash.</para>
    /// </summary>
    public static class Plural
    {
        /// <summary>The CLDR category name used as the key suffix: one/few/many/other.</summary>
        public static string Category(string language, int n)
        {
            n = Math.Abs(n);
            // Accept "pt-BR" / "zh-Hans" style tags by looking at the base language,
            // except where the region genuinely changes the rules (it does not, for
            // any pair we ship — but the split keeps that assumption explicit).
            string lang = language;
            int dash = lang.IndexOf('-');
            string baseLang = dash < 0 ? lang : lang.Substring(0, dash);

            switch (baseLang)
            {
                // No plural distinction at all.
                case "zh":
                case "ja":
                case "ko":
                case "vi":
                    return "other";

                // one: i = 0 or 1
                case "fr":
                case "pt":
                    return n is 0 or 1 ? "one" : "other";

                // one: n = 1
                case "en":
                case "de":
                case "it":
                case "es":
                case "tr":
                    return n == 1 ? "one" : "other";

                // one:  i % 10 = 1 and i % 100 != 11
                // few:  i % 10 = 2..4 and i % 100 != 12..14
                // many: everything else (includes 0)
                case "ru":
                case "uk":
                    if (n % 10 == 1 && n % 100 != 11) return "one";
                    if (n % 10 is >= 2 and <= 4 && !(n % 100 is >= 12 and <= 14)) return "few";
                    return "many";

                // one:  i = 1 exactly (not 21, 31 — unlike Russian)
                // few:  i % 10 = 2..4 and i % 100 != 12..14
                // many: everything else
                case "pl":
                    if (n == 1) return "one";
                    if (n % 10 is >= 2 and <= 4 && !(n % 100 is >= 12 and <= 14)) return "few";
                    return "many";

                // An unknown language falls back to the English shape rather than
                // to "other" only: a missing form then shows a sane English string
                // instead of the raw key.
                default:
                    return n == 1 ? "one" : "other";
            }
        }

        /// <summary>
        /// Every category a language can produce — what a complete translation of
        /// a counted string must provide. Used by the localization tests, so a new
        /// language cannot ship with half its forms missing.
        /// </summary>
        public static string[] CategoriesFor(string language)
        {
            int dash = language.IndexOf('-');
            string baseLang = dash < 0 ? language : language.Substring(0, dash);
            return baseLang switch
            {
                "zh" or "ja" or "ko" or "vi" => new[] { "other" },
                "ru" or "uk" or "pl" => new[] { "one", "few", "many" },
                _ => new[] { "one", "other" },
            };
        }
    }
}
