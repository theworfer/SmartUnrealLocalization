using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services
{
    /// <summary>
    /// UI-free batch translator (Etap 2). Takes a list of source strings + a
    /// system prompt, sends one <see cref="TranslatorService.TranslateAsync"/>
    /// request (JSON <c>[{id,text}]</c>), and returns the translations aligned by
    /// index. Mirrors AiTranslatePanel's glossary / token handling but without any
    /// TranslationRow / VM / Dispatcher coupling, so MultiTranslateJob can reuse it.
    ///
    /// <para>
    /// On any failure for a row (missing in the response, glossary placeholder
    /// dropped, or {curly-token} count mismatch) the result falls back to the
    /// SOURCE text for that row — same conservative behaviour as the panel.
    /// </para>
    /// </summary>
    public static class TranslationBatchRunner
    {
        private static readonly Regex CurlyToken = new(@"{[^}]+}", RegexOptions.Compiled);

        /// <summary>
        /// Instruction prepended to the USER message (never the cached system prompt) when a
        /// batch carries per-entry reviewer notes. Tells the model that a <c>note</c> is a
        /// row-scoped translator instruction and must not be echoed back. Shared with
        /// <c>AiTranslatePanel</c>, which builds its own payload but uses the same contract.
        /// </summary>
        public const string NoteInstruction =
            "Some entries include a \"note\" field — a translator instruction that applies ONLY to that entry. " +
            "Take it into account when translating that entry's \"text\". " +
            "Do NOT translate, echo, or include the \"note\" in your output — return only \"id\" and \"text\".";

        /// <summary>
        /// Declared trust level of the reference translation ("Additional source
        /// language" in the AI panel) — rides into <see cref="RefQualitySentence"/>
        /// so the model knows how much weight the reference deserves.
        /// </summary>
        public enum RefQuality
        {
            OfficialHuman,
            Community,
            Machine,
            Unknown,
        }

        /// <summary>
        /// Prepended when a batch carries per-entry <c>ref</c> fields. Assigns explicit
        /// ROLES so the model can't confuse the inputs: <c>text</c> = PRIMARY SOURCE
        /// (authoritative original, the thing being translated), <c>ref</c> = REFERENCE
        /// TRANSLATION (another language, meaning-clarification only). Input-only: the
        /// response contract stays exactly <c>[{id,text}]</c>.
        /// </summary>
        public const string RefInstructionBase =
            "Roles: each entry's \"text\" is the PRIMARY SOURCE — the authoritative original you translate into the target language. " +
            "Some entries also include a \"ref\" field — a REFERENCE TRANSLATION of the same string in another language, " +
            "provided only to clarify meaning (part of speech, gender, plurality). " +
            "When \"text\" and \"ref\" disagree, \"text\" always wins. " +
            "Never translate from \"ref\" alone and never echo or include it in your output — return only \"id\" and \"text\".";

        /// <summary>One-sentence trust rider appended to <see cref="RefInstructionBase"/>.</summary>
        public static string RefQualitySentence(RefQuality quality) => quality switch
        {
            RefQuality.OfficialHuman => "The reference is an official human translation — treat it as a reliable hint.",
            RefQuality.Community => "The reference is a community translation — usually helpful, but it may contain mistakes.",
            RefQuality.Machine => "The reference is MACHINE-TRANSLATED — use it with caution and never copy its errors; trust the primary source.",
            _ => "The reference's quality is unknown — use it only where it resolves ambiguity; trust the primary source.",
        };

        /// <summary>
        /// Prepended when a batch carries per-entry <c>key</c> fields — the string's identifier
        /// in the game data (e.g. <c>UI/BTN_Cancel</c>), useful as placement context. Input-only.
        /// </summary>
        public const string KeyInstruction =
            "Some entries include a \"key\" field — the string's identifier in the game data (e.g. a UI element name). " +
            "Use it only as a hint about where the text appears. " +
            "Do NOT translate, echo, or include \"key\" in your output — return only \"id\" and \"text\".";

        /// <param name="notes">
        /// Optional per-entry reviewer notes, aligned by index with <paramref name="sources"/>.
        /// A non-blank entry is sent inline as a <c>note</c> field for that row and triggers the
        /// <see cref="NoteInstruction"/> prefix. Null / blank entries behave exactly as before.
        /// </param>
        /// <returns>
        /// One entry per source. A non-null entry is the translation the model actually produced
        /// (which MAY equal the source — e.g. when a note says "keep as-is" — a legitimate result,
        /// not a failure). A <c>null</c> entry means this row could not be translated (batch error,
        /// missing id, token/glossary mismatch); the caller decides the fallback. Distinguishing
        /// null from an identity result lets the multi-translate commented pass honour "keep source"
        /// notes without confusing them with failures.
        /// </returns>
        public static async Task<List<string?>> TranslateBatchAsync(
            IReadOnlyList<string> sources, string systemPrompt, AppConfig cfg, CancellationToken ct,
            IReadOnlyList<string?>? notes = null)
        {
            int n = sources.Count;

            // Per-row glossary placeholder maps + the ordered {curly} tokens.
            var glossaries = new List<Dictionary<string, string>>(n);
            var orderedTokens = new List<List<string>>(n);
            var preTexts = new List<string>(n);

            for (int i = 0; i < n; i++)
            {
                string src = sources[i] ?? "";
                var glt = new Dictionary<string, string>();
                string pre = src;
                int idx = 1;
                foreach (var term in cfg.ProtectedTerms)
                {
                    if (string.IsNullOrWhiteSpace(term)) continue;
                    string trimmed = term.Trim();
                    // Boundaries exclude digits and '_' too (same fix as the AI panel):
                    // "Ork" must not match inside "Ork2"/"Ork_boss".
                    var pattern = $@"(?<![\p{{L}}\p{{Nd}}_]){Regex.Escape(trimmed)}(?![\p{{L}}\p{{Nd}}_])";
                    if (Regex.IsMatch(pre, pattern))
                    {
                        string ph = $"{{GLT_{idx++}}}";
                        glt[ph] = trimmed;
                        pre = Regex.Replace(pre, pattern, ph);
                    }
                }
                glossaries.Add(glt);
                orderedTokens.Add(CurlyToken.Matches(pre).Cast<Match>().Select(m => m.Value).ToList());
                preTexts.Add(pre);
            }

            // Default everything to null (= "not translated"); a row stays null unless the model
            // successfully answers it below. The caller maps null → its own fallback (source).
            var result = new List<string?>(n);
            for (int i = 0; i < n; i++) result.Add(null);

            string userMessage = BuildBatchUserMessage(preTexts, notes);

            string raw = await TranslatorService.TranslateAsync(userMessage, systemPrompt, ct);
            // A whole-batch empty/unparseable response is a FAILURE the caller must
            // count (circuit breaker, Partial marking) — returning the all-null list
            // here made it look like a successful batch of "keep source" rows, reset
            // the 3-strikes counter, and shipped Source as a finished translation.
            if (string.IsNullOrWhiteSpace(raw))
                throw new FormatException("AI returned an empty response for the batch.");

            JArray parsed;
            try { parsed = JArray.Parse(StripCodeFences(raw)); }
            catch (Exception ex)
            {
                throw new FormatException($"AI returned no parseable JSON array: {ex.Message}");
            }

            for (int i = 0; i < n; i++)
            {
                var match = parsed.FirstOrDefault(x => x["id"]?.ToString() == i.ToString());
                string t = match?["text"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(t)) continue; // keep fallback

                // {curly} token integrity: same count → fix positional swaps; different
                // count → reject (keep source). Matches ComputeRowOutcome.
                var srcToks = orderedTokens[i];
                var tgtToks = CurlyToken.Matches(t).Cast<Match>().Select(m => m.Value).ToList();
                if (srcToks.Count == tgtToks.Count)
                {
                    for (int j = 0; j < srcToks.Count; j++)
                        if (srcToks[j] != tgtToks[j])
                            t = new Regex(Regex.Escape(tgtToks[j])).Replace(t, srcToks[j], 1);
                }
                else if (srcToks.Count > 0)
                {
                    continue; // token count mismatch → keep source
                }

                // Glossary restore: every placeholder must come back, else reject.
                bool glossaryOk = true;
                foreach (var kv in glossaries[i])
                {
                    if (t.Contains(kv.Key)) t = t.Replace(kv.Key, kv.Value);
                    else { glossaryOk = false; break; }
                }
                if (!glossaryOk) continue; // missing glossary token → keep source

                result[i] = t;
            }
            return result;
        }

        /// <summary>
        /// Builds the user-message string for a batch: a JSON array of <c>{id,text}</c> entries,
        /// each carrying an optional inline <c>note</c> (a row-scoped reviewer instruction). When
        /// any entry has a note, <see cref="NoteInstruction"/> is prepended — otherwise the output
        /// is byte-identical to the historical note-free payload. The note data lives ONLY here in
        /// the user message, never in the cached system prompt. Shared by this runner (multi-
        /// translate) and <c>AiTranslatePanel</c> (single-file) so both flows emit the same shape.
        /// </summary>
        /// <param name="texts">Pre-processed (glossary/token-substituted) PRIMARY SOURCE text per entry.</param>
        /// <param name="notes">Optional reviewer notes aligned by index; null/blank entries are omitted.</param>
        /// <param name="refs">Optional REFERENCE TRANSLATION per entry (the same string in another
        /// language, meaning-clarification only); null/blank entries are omitted.</param>
        /// <param name="keys">Optional row-key context per entry (AI panel's "Send Key as context");
        /// null/blank entries are omitted.</param>
        /// <param name="refQuality">Declared trust level of the reference translations (one per run).</param>
        public static string BuildBatchUserMessage(IReadOnlyList<string> texts, IReadOnlyList<string?>? notes,
            IReadOnlyList<string?>? refs = null, IReadOnlyList<string?>? keys = null,
            RefQuality refQuality = RefQuality.Unknown)
        {
            var arr = new JArray();
            bool anyNote = false, anyRef = false, anyKey = false;
            for (int i = 0; i < texts.Count; i++)
            {
                var obj = new JObject { ["id"] = i.ToString(), ["text"] = texts[i] };
                if (notes != null && i < notes.Count && !string.IsNullOrWhiteSpace(notes[i]))
                {
                    obj["note"] = notes[i];
                    anyNote = true;
                }
                if (refs != null && i < refs.Count && !string.IsNullOrWhiteSpace(refs[i]))
                {
                    obj["ref"] = refs[i];
                    anyRef = true;
                }
                if (keys != null && i < keys.Count && !string.IsNullOrWhiteSpace(keys[i]))
                {
                    obj["key"] = keys[i];
                    anyKey = true;
                }
                arr.Add(obj);
            }

            // Instructions are prepended ONLY for the fields actually present, so a
            // payload with none of the extras stays byte-identical to the historical
            // shape (prompt caching + zero behavioral drift when the options are off).
            var prefixes = new List<string>(3);
            if (anyNote) prefixes.Add(NoteInstruction);
            if (anyRef) prefixes.Add(RefInstructionBase + " " + RefQualitySentence(refQuality));
            if (anyKey) prefixes.Add(KeyInstruction);
            return prefixes.Count > 0 ? string.Join("\n", prefixes) + "\n\n" + arr.ToString() : arr.ToString();
        }

        private static string StripCodeFences(string raw)
        {
            string s = raw.Trim();
            if (s.StartsWith("```json")) s = s.Substring(7);
            else if (s.StartsWith("```")) s = s.Substring(3);
            if (s.EndsWith("```")) s = s.Substring(0, s.Length - 3);
            return s.Trim();
        }
    }
}
