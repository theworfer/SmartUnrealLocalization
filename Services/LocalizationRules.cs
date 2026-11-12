using System.Text.RegularExpressions;

namespace SmartUnrealLocalization.Services
{
    /// <summary>
    /// ONE definition of what counts as a tag, a placeholder and sentence-final
    /// punctuation, shared by the live validators (F8, per-keystroke) and the QC
    /// engine (an explicit review pass).
    ///
    /// <para>The two systems stay separate by design — different lifecycle,
    /// different inputs, different cost — but they must not DISAGREE about what a
    /// placeholder is. They used to: the validators knew only <c>&lt;…&gt;</c> and
    /// <c>{…}</c>, so a dropped <c>%d</c> (which breaks the game) was reported by
    /// QC and by nothing else, and the bracket-prose rule existed on one side
    /// only. Anything both sides must answer identically belongs here.</para>
    /// </summary>
    internal static class LocalizationRules
    {
        // ── the raw patterns, so every composed regex below is built from the
        //    SAME source and the three systems cannot drift apart ──────────────

        private const string AngleTagPattern = @"<[^>]+>";
        private const string BraceTokenPattern = @"\{[^}]*\}";
        private const string PrintfPattern =
            @"%(?:\d+\$)?[-+#0]*\d*(?:\.\d+)?(?:hh|h|ll|l|z|j|t|L)?[sdiufFeEgGxXoc]";

        /// <summary>
        /// A bracket span is MARKUP only when it is shaped like code. Three shapes
        /// qualify, and nothing else does:
        /// <list type="bullet">
        ///   <item>a closing tag — <c>[/b]</c>, <c>[/color]</c></item>
        ///   <item>it carries a parameter or is a variable name — <c>[color=red]</c>,
        ///         <c>[PLAYER_NAME]</c> (contains <c>=</c> or <c>_</c>)</item>
        ///   <item>it has no capital letter at all — <c>[b]</c>, <c>[i]</c>, <c>[br]</c></item>
        /// </list>
        ///
        /// <para><b>Why capitals decide it.</b> The earlier rule was "no internal
        /// whitespace", which correctly excluded narrative asides like
        /// <c>[The rest of the page has been torn off]</c> but still swallowed the
        /// far more common one-word kind: <c>Mysterious Orb [Charged]</c>,
        /// <c>[Locked]</c>, <c>[NEW]</c>. Those are TEXT — a Polish translator
        /// correctly writes <c>[Naładowana]</c> — and reporting them as broken
        /// markup put a red Error on perfectly good rows. Markup is written in
        /// lower case by every convention that uses square brackets; a capitalised
        /// word inside brackets is a word.</para>
        ///
        /// <para><b>This is a heuristic, and it is project-dependent.</b> A project
        /// that writes its codes as <c>[BR]</c> or uses <c>[NEW]</c> as a
        /// non-translatable badge will be judged wrong by it. The default leans
        /// towards treating brackets as prose because the cost is asymmetric: a
        /// missed check is silent, while a false Error fires on every affected row
        /// and trains the user to ignore the colour.</para>
        ///
        /// <para>Still deliberately unmatched: a tag written with spaces
        /// (<c>[img id=3]</c>).</para>
        /// </summary>
        private const string BracketTagPattern =
            @"\[(?:/[^\]\s]*|[^\]\s]*[=_][^\]\s]*|[^\]\s\p{Lu}]+)\]";

        /// <summary>XML-ish markup: <c>&lt;b&gt;</c>, <c>&lt;font color="red"&gt;</c>.</summary>
        public static readonly Regex AngleTag = new(AngleTagPattern, RegexOptions.Compiled);

        /// <summary>Unreal's named substitution: <c>{count}</c>, <c>{0}</c>.</summary>
        public static readonly Regex BraceToken = new(BraceTokenPattern, RegexOptions.Compiled);

        /// <summary>Square-bracket markup. See <see cref="BracketTagPattern"/> for the rule.</summary>
        public static readonly Regex BracketTag = new(BracketTagPattern, RegexOptions.Compiled);

        /// <summary>
        /// printf-style placeholders: <c>%s</c>, <c>%d</c>, <c>%02d</c>,
        /// <c>%1$s</c>, <c>%.2f</c>, <c>%ld</c>, <c>%-5s</c>, <c>%X</c>.
        ///
        /// <para>The flag set deliberately EXCLUDES a space (<c>"% d"</c>), which
        /// real printf allows: ordinary prose is full of <c>"100% done"</c>, and
        /// accepting the space flag turns every one of those into a phantom
        /// placeholder. A padded-with-space format is vanishingly rare in game
        /// text; the false positives would not be.</para>
        ///
        /// <para><c>%%</c> is intentionally not matched — it is a literal percent
        /// sign, not a substitution.</para>
        /// </summary>
        public static readonly Regex Printf = new(PrintfPattern, RegexOptions.Compiled);

        /// <summary>Everything the game substitutes or renders — never prose.</summary>
        public static readonly Regex AnyToken = new(
            $"{AngleTagPattern}|{BracketTagPattern}|{BraceTokenPattern}|{PrintfPattern}",
            RegexOptions.Compiled);

        /// <summary>Braces and printf together — what "placeholder" means to both systems.</summary>
        public static readonly Regex Placeholder = new(
            $"{BraceTokenPattern}|{PrintfPattern}", RegexOptions.Compiled);

        /// <summary>Markup and substitutions, for "is this row prose at all?" checks.</summary>
        public static readonly Regex AnyMarkup = new(
            $"{AngleTagPattern}|{BracketTagPattern}|{BraceTokenPattern}", RegexOptions.Compiled);

        /// <summary>
        /// Rendering markup only — angle and bracket tags, NOT <c>{braces}</c>.
        /// Keeps the three token validators disjoint so their fixes never overlap.
        /// </summary>
        public static readonly Regex AnyMarkupTags = new(
            $"{AngleTagPattern}|{BracketTagPattern}", RegexOptions.Compiled);

        // ── sentence-final punctuation ───────────────────────────────────────

        /// <summary>
        /// Marks that carry meaning at the end of a string. A dropped <c>?</c>
        /// changes a question into a statement; a dropped <c>:</c> breaks a label
        /// that the UI concatenates a value onto.
        /// </summary>
        public static readonly char[] FinalPunctuation = { '.', '!', '?', '…', ':', ';' };

        /// <summary>The trailing run of <see cref="FinalPunctuation"/>, ignoring whitespace after it.</summary>
        public static string TrailingPunctuation(string s)
        {
            int end = s.Length;
            while (end > 0 && char.IsWhiteSpace(s[end - 1])) end--;

            int i = end;
            while (i > 0 && System.Array.IndexOf(FinalPunctuation, s[i - 1]) >= 0) i--;
            return s[i..end];
        }

        /// <summary>
        /// <paramref name="s"/> without its trailing punctuation run and the
        /// spaces/tabs around it. Line breaks are NOT touched — they are
        /// structural, and a punctuation fix must never change how a string wraps.
        /// </summary>
        public static string StripTrailingPunctuation(string s)
        {
            int i = s.Length;
            while (i > 0 && (s[i - 1] == ' ' || s[i - 1] == '\t')) i--;
            while (i > 0 && System.Array.IndexOf(FinalPunctuation, s[i - 1]) >= 0) i--;
            while (i > 0 && (s[i - 1] == ' ' || s[i - 1] == '\t')) i--;
            return s[..i];
        }

        // ── edge whitespace ──────────────────────────────────────────────────

        public static string LeadingWhitespace(string s)
        {
            int i = 0;
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            return s[..i];
        }

        /// <summary>
        /// Trailing whitespace run. Returns "" for an all-whitespace string so the
        /// leading and trailing runs can never overlap and double-count.
        /// </summary>
        public static string TrailingWhitespace(string s)
        {
            int i = s.Length;
            while (i > 0 && char.IsWhiteSpace(s[i - 1])) i--;
            return i == 0 ? "" : s[i..];
        }
    }
}
