using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SmartUnrealLocalization.Models;

using SmartUnrealLocalization.Localization;

namespace SmartUnrealLocalization.Services.Qc
{
    /// <summary>
    /// QC's check library. It used to be a separate local pass over the file;
    /// that pass was removed on 2026-10-21 (the live validator reports those
    /// problems on every edit). What lives on here:
    /// <list type="bullet">
    /// <item>the checks the validator borrows — model chatter, leftover source
    /// language, protected terms, terminology rules, lost line structure;</item>
    /// <item>the "nothing to review" rule the AI review uses to skip rows;</item>
    /// <item>the placeholder/tag shapes <see cref="QcAiRunner.RejectFix"/> uses to
    /// re-validate every AI fix.</item>
    /// </list>
    ///
    /// <para>Language-agnostic by design: the leak detector works off the target
    /// language's own alphabet (letters that cannot exist in it), so it is not
    /// limited to the ru→uk pair it was born from; word lists only refine it.</para>
    /// </summary>
    public sealed class QcEngine
    {
        public sealed class Options
        {
            /// <summary>Target language name as shown in the AI panel (e.g. "Ukrainian").</summary>
            public string TargetLanguage { get; set; } = "";
            /// <summary>Source language name, when known (used for leak hints).</summary>
            public string SourceLanguage { get; set; } = "";
            /// <summary>User's glossary — terms that must survive untranslated.</summary>
            public IReadOnlyList<string> ProtectedTerms { get; set; } = Array.Empty<string>();
            /// <summary>Words that look like leaks but are legitimate (user-editable).</summary>
            public IReadOnlyCollection<string> LeakExceptions { get; set; } = Array.Empty<string>();
            /// <summary>Known bad → good replacements (terminology fixes).</summary>
            public IReadOnlyDictionary<string, string> TermFixes { get; set; } = new Dictionary<string, string>();
        }

        /// <summary>
        /// One finding. <paramref name="Resolved"/> marks the ones the mechanical
        /// fix actually repairs; everything else still needs a human even after
        /// the user presses Fix, so the badge must survive (see ApplyQcProposals).
        /// </summary>
        private readonly record struct Finding(string Kind, QcSeverity Severity, string Reason, bool Resolved);

        /// <summary>
        /// Parse the user's terminology list ("wrong=correct" lines, the storage
        /// format of <c>AppConfig.QcTermFixes</c>) into the replacement map.
        /// Only the FIRST '=' splits, so a correction may itself contain '='.
        /// Malformed lines (no '=', an empty side) are skipped, never guessed at;
        /// on duplicate keys the first line wins.
        /// </summary>
        public static Dictionary<string, string> ParseTermFixes(IEnumerable<string> lines)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string bad = line[..eq].Trim();
                string good = line[(eq + 1)..].Trim();
                if (bad.Length == 0 || good.Length == 0) continue;
                map.TryAdd(bad, good);
            }
            return map;
        }

        /// <summary>
        /// Placeholder / tag shapes. These are ALIASES of
        /// <see cref="LocalizationRules"/> — the single definition shared with the
        /// live validators, so F8 and QC can never disagree about what a
        /// placeholder is. They stay exposed here because <see cref="QcAiRunner"/>
        /// reuses them to re-validate every AI fix.
        /// </summary>
        internal static readonly Regex Placeholder = LocalizationRules.Placeholder;
        internal static readonly Regex AngleTag = LocalizationRules.AngleTag;
        internal static readonly Regex BracketTag = LocalizationRules.BracketTag;

        private static readonly Regex AnyLetter = new(@"\p{L}", RegexOptions.Compiled);
        // Markup only (no printf): "is there prose here at all?".
        private static readonly Regex TokenStrip = LocalizationRules.AnyMarkup;

        /// <summary>
        /// AI chatter that leaked into a translation instead of being the
        /// translation ("Translation:", "FIXED:", "Here is ...", wrapping quotes).
        /// </summary>
        private static readonly Regex[] AiNoise =
        {
            new(@"^\s*(translation|переклад|перевод)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"^\s*(fixed|ok|result|output|answer)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"^\s*(here('s| is)|sure[,!]|certainly[,!]|i would|let's|as an ai)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"^\s*id\s*\d+\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"^\s*\d+\s*[.)]\s*$", RegexOptions.Compiled),
        };

        // Letters that exist in one Cyrillic language but not the other. Used by
        // the alphabet layer of the leak detector — no dictionary needed.
        private static readonly Dictionary<string, string> ForeignLetters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ukrainian"] = "ыэъё",            // Russian-only letters can't appear in Ukrainian
            ["russian"] = "їієґ",              // Ukrainian-only letters can't appear in Russian
            ["belarusian"] = "щыїієґ".Replace("ы", ""),
        };

        // Frequent function words that betray an untranslated Russian fragment in
        // Ukrainian text. Deliberately conservative: only words with no Ukrainian
        // homograph, so false positives stay rare (the user can add exceptions).
        private static readonly Dictionary<string, string[]> LeakWords = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ukrainian"] = new[]
            {
                "что","чтобы","сейчас","должен","должна","должно","тебя","меня","нельзя","никогда",
                "всегда","здесь","там","этот","этого","этому","который","которая","которые","очень",
                "хорошо","плохо","спасибо","пожалуйста","конечно","только","ничего","что-то","кто-то",
                "сюда","туда","откуда","почему","зачем","потому","значит","просто","снова","опять",
            },
        };

        /// <summary>Rows whose Target should be skipped entirely (empty, untranslated, tag-only).</summary>
        public static bool ShouldSkip(TranslationRow row)
            => ShouldSkipTexts(row.Source ?? "", row.Target ?? "");

        /// <summary>
        /// The same rule stated over the two texts directly, for callers that pair
        /// an original from ANOTHER file with a result taken from either column.
        /// </summary>
        public static bool ShouldSkipTexts(string original, string result)
        {
            if (string.IsNullOrWhiteSpace(original)) return true;             // nothing to compare against
            if (string.IsNullOrWhiteSpace(result)) return true;               // untranslated — not QC's job
            if (!AnyLetter.IsMatch(TokenStrip.Replace(original, ""))) return true; // tag/number only (AutoCarry)
            return false;
        }

        // ── checks the live validator borrows (validator stage 3, 2026-10-21) ──
        // The validator runs on every edit; these reuse the engine's tested logic
        // rather than a second copy of it. Each returns null when the row is fine.

        /// <summary>The target without model chatter (a "Translation:" prefix, wrapping
        /// quotes the source lacks), or null when there is none or nothing would remain.</summary>
        internal static string? StripModelChatter(string source, string target)
        {
            string text = target;
            var findings = new List<Finding>();
            CheckAiNoise(source, ref text, findings);
            return findings.Count == 0 || string.IsNullOrWhiteSpace(text) ? null : text;
        }

        /// <summary>True when the target is ONLY model chatter (stripping leaves nothing).</summary>
        internal static bool IsOnlyModelChatter(string source, string target)
        {
            string text = target;
            var findings = new List<Finding>();
            CheckAiNoise(source, ref text, findings);
            return findings.Count > 0 && string.IsNullOrWhiteSpace(text);
        }

        /// <summary>Why the target looks like it still holds source-language text, if it does.</summary>
        internal static string? LanguageLeak(string target, string targetLanguage, IReadOnlyCollection<string> exceptions)
        {
            if (string.IsNullOrWhiteSpace(targetLanguage)) return null;
            var findings = new List<Finding>();
            CheckLanguageLeak(target, new Options { TargetLanguage = targetLanguage, LeakExceptions = exceptions }, findings);
            return findings.Count == 0 ? null : string.Join("; ", findings.Select(f => f.Reason));
        }

        /// <summary>Protected terms present in the source and missing from the target.</summary>
        internal static string? MissingProtectedTerms(string source, string target, IReadOnlyList<string> terms)
        {
            var findings = new List<Finding>();
            CheckProtectedTerms(source, target, new Options { ProtectedTerms = terms }, findings);
            return findings.Count == 0 ? null : string.Join("; ", findings.Select(f => f.Reason));
        }

        /// <summary>The target with the user's terminology rules applied, or null when none matched.</summary>
        internal static (string Fixed, string Reason)? ApplyTerminology(string target, IReadOnlyDictionary<string, string> fixes)
        {
            if (fixes.Count == 0) return null;
            string text = target;
            var findings = new List<Finding>();
            ApplyTermFixes(ref text, new Options { TermFixes = fixes }, findings);
            return findings.Count == 0 || text == target ? null : (text, string.Join("; ", findings.Select(f => f.Reason)));
        }

        /// <summary>A lost or invented line structure: every break gone, or breaks the
        /// source does not have. A different COUNT alone is a translator reflowing
        /// prose and is not reported.</summary>
        internal static string? LineBreakProblem(string source, string target)
        {
            var findings = new List<Finding>();
            CheckLineBreaks(source, target, findings);
            var serious = findings.Where(f => f.Severity >= QcSeverity.High).ToList();
            return serious.Count == 0 ? null : string.Join("; ", serious.Select(f => f.Reason));
        }

        // ── individual checks ────────────────────────────────────────────

        /// <summary>
        /// Line breaks INSIDE the text. Edge breaks are excluded: they are the
        /// edge-whitespace check's business, and counting them here too would
        /// report the same thing twice.
        ///
        /// <para>Severity is split by what actually happened. Losing every break
        /// destroys the layout and is Critical; a translator reflowing a page of
        /// prose from 33 breaks to 32 is normal and only worth a note. Reporting
        /// both as errors is what made a real 13 k-row file look alarming.</para>
        /// </summary>
        private static void CheckLineBreaks(string source, string target,
            List<Finding> findings)
        {
            int s = source.Trim().Count(c => c == '\n');
            int t = target.Trim().Count(c => c == '\n');
            if (s == t) return;

            if (s > 0 && t == 0)
            {
                findings.Add(new Finding("LineBreakMismatch", QcSeverity.Critical,
                    string.Format(Loc.T("val.reason.lbLost"), s), false));
            }
            else if (t > 0 && s == 0)
            {
                findings.Add(new Finding("LineBreakMismatch", QcSeverity.Critical,
                    string.Format(Loc.T("val.reason.lbAdded"), t), false));
            }
            else
            {
                findings.Add(new Finding("LineBreakMismatch", QcSeverity.Low,
                    $"Line-break count differs ({s} → {t}) — normal when a translator reflows prose", false));
            }
        }

        private static void CheckAiNoise(string source, ref string text,
            List<Finding> findings)
        {
            string before = text;
            foreach (var re in AiNoise)
            {
                // Source-aware: if the ORIGINAL itself carries the same shape
                // ("Result: %d", "Translation: …" as a real game string), the
                // translation legitimately mirrors it — that is not chatter.
                if (re.IsMatch(source)) continue;
                var m = re.Match(text);
                if (m.Success && m.Index == 0)
                    text = text[m.Length..].TrimStart();
            }
            // A whole line wrapped in quotes — but only when the SOURCE is not
            // quote-wrapped itself (quoted dialogue must keep its quotes).
            string sTrim = source.Trim();
            bool sourceQuoted = sTrim.Length > 1 && sTrim[0] == '"' && sTrim[^1] == '"';
            string trimmed = text.Trim();
            if (!sourceQuoted && trimmed.Length > 1 && trimmed[0] == '"' && trimmed[^1] == '"'
                && !trimmed[1..^1].Contains('"'))
                text = trimmed[1..^1];

            if (!string.Equals(text, before, StringComparison.Ordinal))
                findings.Add(new Finding("AiNoise", QcSeverity.Critical,
                    "Model chatter leaked into the translation (prefix or wrapping quotes) — removed", true));
        }

        private static void CheckLanguageLeak(string target, Options options,
            List<Finding> findings)
        {
            string lang = options.TargetLanguage.ToLowerInvariant();

            // Layer 1 — alphabet: letters that cannot exist in the target language.
            if (ForeignLetters.TryGetValue(lang, out var foreign))
            {
                var hits = target.Where(c => foreign.Contains(char.ToLowerInvariant(c))).Distinct().ToList();
                if (hits.Count > 0)
                    findings.Add(new Finding("LanguageLeak", QcSeverity.Critical,
                        string.Format(Loc.T("val.reason.leakLetters"), options.TargetLanguage, string.Join(" ", hits)), false));
            }

            // Layer 2 — words: frequent function words of the source language that
            // have no homograph in the target (minus the user's exceptions).
            if (LeakWords.TryGetValue(lang, out var words))
            {
                // Case-insensitive: the user's exception list is free text, so
                // "Что" must silence "что" just as well.
                var exceptions = new HashSet<string>(options.LeakExceptions, StringComparer.OrdinalIgnoreCase);
                var found = new List<string>();
                foreach (var w in words)
                {
                    if (exceptions.Contains(w)) continue;
                    if (Regex.IsMatch(target, $@"(?<![\p{{L}}\p{{Nd}}_]){Regex.Escape(w)}(?![\p{{L}}\p{{Nd}}_])",
                                      RegexOptions.IgnoreCase))
                        found.Add(w);
                }
                if (found.Count > 0)
                    findings.Add(new Finding("LanguageLeak", QcSeverity.Critical,
                        string.Format(Loc.T("val.reason.leakWords"), string.Join(", ", found.Distinct())), false));
            }
        }

        private static void CheckProtectedTerms(string source, string target, Options options,
            List<Finding> findings)
        {
            foreach (var term in options.ProtectedTerms)
            {
                if (string.IsNullOrWhiteSpace(term)) continue;
                string t = term.Trim();
                var pattern = $@"(?<![\p{{L}}\p{{Nd}}_]){Regex.Escape(t)}(?![\p{{L}}\p{{Nd}}_])";
                if (Regex.IsMatch(source, pattern, RegexOptions.IgnoreCase)
                    && !Regex.IsMatch(target, pattern, RegexOptions.IgnoreCase))
                {
                    findings.Add(new Finding("ProtectedTerm", QcSeverity.High,
                        string.Format(Loc.T("val.reason.protected"), t), false));
                }
            }
        }

        private static void ApplyTermFixes(ref string text, Options options,
            List<Finding> findings)
        {
            foreach (var (bad, good) in options.TermFixes)
            {
                if (string.IsNullOrWhiteSpace(bad)) continue;
                var pattern = $@"(?<![\p{{L}}\p{{Nd}}_]){Regex.Escape(bad)}(?![\p{{L}}\p{{Nd}}_])";
                if (!Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase)) continue;
                // Carry the replaced word's case onto the replacement. The match is
                // case-insensitive but the replacement used to be literal, so a
                // sentence-initial "Пролом" came back as "пробоїна" — the fix
                // silently lower-cased the start of the sentence.
                text = Regex.Replace(text, pattern, m => MatchCase(m.Value, good), RegexOptions.IgnoreCase);
                findings.Add(new Finding("Terminology", QcSeverity.High, string.Format(Loc.T("val.reason.terminology"), bad, good), true));
            }
        }

        /// <summary>
        /// Give <paramref name="replacement"/> the case profile of the text it
        /// replaces. Only ever RAISES case — a rule written as "kyiv=Київ" keeps
        /// the capital the user typed, while "пролом=пробоїна" matching "ПРОЛОМ"
        /// yields "ПРОБОЇНА".
        /// </summary>
        private static string MatchCase(string matched, string replacement)
        {
            if (matched.Length == 0 || replacement.Length == 0) return replacement;

            bool anyLetter = matched.Any(char.IsLetter);
            if (anyLetter && matched.Length > 1 && matched.All(c => !char.IsLetter(c) || char.IsUpper(c)))
                return replacement.ToUpperInvariant();

            if (char.IsUpper(matched[0]) && char.IsLower(replacement[0]))
                return char.ToUpperInvariant(replacement[0]) + replacement[1..];

            return replacement;
        }
    }
}
