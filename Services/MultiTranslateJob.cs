using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CsvHelper;
using CsvHelper.Configuration;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Utils;

namespace SmartUnrealLocalization.Services
{
    /// <summary>One target language for a multi-translate run.</summary>
    public sealed class MultiTranslateTarget
    {
        public string Locale { get; init; } = "";
        /// <summary>Extra instructions appended to the auto base prompt (shared or per-language).</summary>
        public string Prompt { get; init; } = "";
    }

    /// <summary>Inputs for a multi-language translation run.</summary>
    public sealed class MultiTranslateRequest
    {
        public IReadOnlyList<TranslationRow> SourceRows { get; init; } = Array.Empty<TranslationRow>();
        public string SourceLanguage { get; init; } = "";
        public IReadOnlyList<MultiTranslateTarget> Targets { get; init; } = Array.Empty<MultiTranslateTarget>();
        public string OutputFolder { get; init; } = "";
        public FileFormat Format { get; init; }
        public string OriginalFileName { get; init; } = "";
        /// <summary>
        /// Base .locres document to overlay each language's translations onto
        /// (required for .locres output). Immutable — N language writes and a
        /// concurrent app autosave can all share it safely.
        /// </summary>
        public Locres.IO.LocresDocument? BaseDocument { get; init; }
        public int BatchSize { get; init; } = 30;
        public bool UseTranslationMemory { get; init; } = true;
        public bool SaveTranslationMemory { get; init; } = true;
        /// <summary>
        /// When true, rows carrying a reviewer <see cref="TranslationRow.Comment"/> are pulled
        /// OUT of source-dedup and translated per-Key with their note (Variant A), bypassing TM.
        /// </summary>
        public bool SendComments { get; init; } = true;
        /// <summary>Resume: skip a language whose output file already exists.</summary>
        public bool SkipExisting { get; init; }
    }

    /// <summary>Mutable progress snapshot pushed to <see cref="MultiTranslateJob.ProgressChanged"/>.</summary>
    public sealed class MultiTranslateProgress
    {
        public int LanguagesDone { get; set; }
        public int LanguagesTotal { get; set; }
        public string CurrentLocale { get; set; } = "";
        public int UnitsDone { get; set; }
        public int UnitsTotal { get; set; }
        public string Phase { get; set; } = ""; // translating / writing / skipped / failed / done
    }

    public sealed class MultiTranslateResult
    {
        public List<string> WrittenFiles { get; } = new();
        public List<string> FailedLocales { get; } = new();
        public List<string> SkippedLocales { get; } = new();
        /// <summary>Locales whose file WAS written but is incomplete (circuit breaker
        /// stopped after repeated batch failures; untranslated rows kept source text).</summary>
        public List<string> PartialLocales { get; } = new();
        public bool Cancelled { get; set; }
    }

    /// <summary>
    /// Disk-streaming multi-language translation engine (Etap 2). Runs each target
    /// language as its OWN sequential phase (own per-language system prompt, own TM
    /// pair) so the AI cache is naturally isolated per language and they never bleed
    /// into one. Only a Dictionary&lt;source,target&gt; is held per language; each
    /// file is written to disk then released, so N×50k rows never sit in RAM at once.
    /// Unique sources are de-duplicated (+ TM) so each string is translated once per
    /// language. No UI — MainWindow wires the panel in Etap 3.
    /// </summary>
    public sealed class MultiTranslateJob
    {
        public event Action<MultiTranslateProgress>? ProgressChanged;
        public event Action<string>? Log;

        // AppContext, not Assembly.Location: empty in the single-file release exe.
        private static string ExeDir => AppContext.BaseDirectory;

        // Strip BOTH <angle> tags and {curly} tokens — a string that is ENTIRELY tags
        // (e.g. "<Place the horn and summon Velgar>") has nothing translatable and must
        // not be sent to the AI (it would translate inside the tag and trip the validator).
        private static readonly Regex TokenStrip = new(@"<[^>]+>|\{[^}]+\}", RegexOptions.Compiled);
        private static readonly Regex AnyLetter = new(@"\p{L}", RegexOptions.Compiled);

        /// <summary>A source needs the AI only if it has a letter once &lt;tags&gt;/{tokens} are
        /// stripped (pure numbers / punctuation / tag-only strings translate to themselves).</summary>
        private static bool NeedsTranslation(string s) => AnyLetter.IsMatch(TokenStrip.Replace(s, ""));

        private static List<string> UniqueSources(IEnumerable<TranslationRow> rows) =>
            rows.SelectMany(r => new[]
                {
                    r.Source ?? "",
                    // PO plural entries: msgid_plural is a source string in its own
                    // right — translating it lets WriteOutputAsync fill msgstr[1..N]
                    // in the TARGET language instead of copying the old language.
                    (r.FormatMeta as PoFileService.PoEntryMeta)?.MsgidPlural ?? "",
                })
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .ToList();

        /// <summary>Rows pulled out of dedup for per-Key, note-aware translation (Variant A).
        /// Empty when comments are off. Letterless sources are skipped (nothing to translate).</summary>
        private static List<TranslationRow> CommentedRows(MultiTranslateRequest req) =>
            req.SendComments
                ? req.SourceRows.Where(r => r.HasComment && NeedsTranslation(r.Source ?? "")).ToList()
                : new List<TranslationRow>();

        /// <summary>Unique sources of the rows that stay on dedup (everything except commented
        /// rows when comments are on). A source used by both commented and uncommented rows still
        /// appears here via its uncommented row, so the dedup translation + TM still cover it.</summary>
        private static List<string> UncommentedUniqueSources(MultiTranslateRequest req) =>
            UniqueSources(req.SourceRows.Where(r => !(req.SendComments && r.HasComment)));

        /// <summary>Pre-run cost estimate: unique translatable sources per language (TM-aware),
        /// plus the commented rows (per-Key, TM-bypassed → 1 unit each, every language), summed.</summary>
        public int EstimateUnits(MultiTranslateRequest req)
        {
            var unique = UncommentedUniqueSources(req).Where(NeedsTranslation).ToList();
            int commented = CommentedRows(req).Count;
            if (!req.UseTranslationMemory) return (unique.Count + commented) * req.Targets.Count;

            int total = 0;
            var tm = new TranslationMemoryService();
            foreach (var t in req.Targets)
            {
                tm.SetLanguagePair(req.SourceLanguage, t.Locale);
                total += unique.Count(s => !tm.TryGet(s, out _)) + commented;
            }
            return total;
        }

        public async Task<MultiTranslateResult> RunAsync(MultiTranslateRequest req, CancellationToken ct)
        {
            var result = new MultiTranslateResult();
            var unique = UncommentedUniqueSources(req);
            var commentedRows = CommentedRows(req);

            var progress = new MultiTranslateProgress
            {
                LanguagesTotal = req.Targets.Count,
                UnitsTotal = EstimateUnits(req),
            };
            int unitsDone = 0;

            Directory.CreateDirectory(req.OutputFolder);
            var tm = new TranslationMemoryService();

            foreach (var target in req.Targets)
            {
                if (ct.IsCancellationRequested) { result.Cancelled = true; break; }
                progress.CurrentLocale = target.Locale;

                string destPath = OutputPathFor(req, target.Locale);
                if (req.SkipExisting && File.Exists(destPath))
                {
                    result.SkippedLocales.Add(target.Locale);
                    progress.LanguagesDone++;
                    progress.Phase = "skipped";
                    Emit(progress);
                    Log?.Invoke($"↷ {target.Locale}: output already exists — skipped.");
                    continue;
                }

                try
                {
                    var translation = await TranslateLanguageAsync(req, target, unique, commentedRows, tm, progress,
                        n => { unitsDone += n; progress.UnitsDone = unitsDone; }, ct);
                    ct.ThrowIfCancellationRequested();

                    progress.Phase = "writing";
                    Emit(progress);
                    await WriteOutputAsync(req, translation, destPath, ct);
                    result.WrittenFiles.Add(destPath);
                    if (translation.Partial) result.PartialLocales.Add(target.Locale);
                    if (req.SaveTranslationMemory) tm.Save();

                    progress.LanguagesDone++;
                    progress.Phase = translation.Partial ? "partial" : "done";
                    Emit(progress);
                    Log?.Invoke(translation.Partial
                        ? $"{Utils.Glyphs.Warning} {target.Locale}: wrote PARTIAL {Path.GetFileName(destPath)} — re-run to finish"
                        : $"{Utils.Glyphs.Ok} {target.Locale}: wrote {Path.GetFileName(destPath)}");
                }
                catch (OperationCanceledException) { result.Cancelled = true; break; }
                catch (Exception ex)
                {
                    // Failure isolation: one bad language doesn't abort the rest.
                    result.FailedLocales.Add(target.Locale);
                    progress.LanguagesDone++;
                    progress.Phase = "failed";
                    Emit(progress);
                    Log?.Invoke($"{Utils.Glyphs.Fail} {target.Locale}: {ex.Message}");
                }
            }

            progress.Phase = "done";
            Emit(progress);
            return result;
        }

        private async Task<LanguageTranslation> TranslateLanguageAsync(
            MultiTranslateRequest req, MultiTranslateTarget target, List<string> unique,
            List<TranslationRow> commentedRows,
            TranslationMemoryService tm, MultiTranslateProgress progress, Action<int> addUnits, CancellationToken ct)
        {
            var cfg = AppConfig.Instance;
            string systemPrompt = BuildSystemPrompt(req.SourceLanguage, target.Locale, target.Prompt);

            var translation = new LanguageTranslation();
            var map = translation.SourceMap;
            var toTranslate = new List<string>();

            if (req.UseTranslationMemory || req.SaveTranslationMemory)
                tm.SetLanguagePair(req.SourceLanguage, target.Locale);

            foreach (var s in unique)
            {
                if (!NeedsTranslation(s)) { map[s] = s; continue; } // letterless → as-is
                if (req.UseTranslationMemory && tm.TryGet(s, out var cached)) map[s] = cached;
                else toTranslate.Add(s);
            }

            progress.Phase = "translating";
            Emit(progress);

            int consecutiveFails = 0;
            int batchSize = Math.Max(1, req.BatchSize);
            for (int start = 0; start < toTranslate.Count; start += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = toTranslate.GetRange(start, Math.Min(batchSize, toTranslate.Count - start));
                try
                {
                    List<string?> translated;
                    try
                    {
                        translated = await TranslationBatchRunner.TranslateBatchAsync(batch, systemPrompt, cfg, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && cfg.AutoRetryEnabled)
                    {
                        // Settings → "Auto-retry failed batches": one delayed second
                        // attempt before the batch counts toward the 3-strikes stop.
                        int delayS = Math.Max(1, cfg.AutoRetryDelaySeconds);
                        Log?.Invoke($"{target.Locale}: batch failed ({ex.Message}) — retrying in {delayS}s…");
                        await Task.Delay(TimeSpan.FromSeconds(delayS), ct);
                        translated = await TranslationBatchRunner.TranslateBatchAsync(batch, systemPrompt, cfg, ct);
                    }
                    for (int i = 0; i < batch.Count; i++)
                    {
                        map[batch[i]] = translated[i] ?? batch[i]; // null = failed → keep source
                        if (req.SaveTranslationMemory && translated[i] is { } t && t != batch[i])
                            tm.Add(batch[i], t);
                    }
                    consecutiveFails = 0;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log?.Invoke($"{Utils.Glyphs.Warning} {target.Locale}: batch failed ({ex.Message}); keeping source for {batch.Count} row(s).");
                    foreach (var s in batch) map[s] = s; // conservative fallback
                    if (++consecutiveFails >= 3)
                    {
                        // STOP this language but DON'T throw — returning the partial map
                        // lets RunAsync still write the file (translated rows + source
                        // fallback for the rest) and Save the TM, so the API spend so far
                        // isn't discarded. The remaining unreached sources fall back to
                        // source in WriteOutputAsync's fan-out.
                        Log?.Invoke($"{Utils.Glyphs.Warning} {target.Locale}: 3 consecutive batch failures — wrote a PARTIAL result (remaining rows kept as source). Re-run to finish.");
                        translation.Partial = true;
                        break;
                    }
                }
                addUnits(batch.Count);
                Emit(progress);
            }

            // Variant A: commented rows translated per-Key with their own note, OUT of dedup and
            // bypassing TM (no read, no save) — so each note is honoured and the TM is not
            // poisoned with a context-specific translation. Rare → negligible extra API cost.
            await TranslateCommentedAsync(commentedRows, systemPrompt, cfg, translation,
                batchSize, addUnits, progress, target.Locale, ct);

            return translation;
        }

        /// <summary>Per-row, note-aware translation pass for commented rows (Variant A). An override
        /// is written for every row the model ANSWERED — including an identity result (how a
        /// "keep as-is / do not translate" note legitimately lands). Only a genuine failure (null)
        /// is left to fall back to the deduped/source value at write time.</summary>
        private async Task TranslateCommentedAsync(
            List<TranslationRow> commentedRows, string systemPrompt, AppConfig cfg,
            LanguageTranslation translation, int batchSize, Action<int> addUnits,
            MultiTranslateProgress progress, string locale, CancellationToken ct)
        {
            for (int start = 0; start < commentedRows.Count; start += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                int len = Math.Min(batchSize, commentedRows.Count - start);
                var slice = commentedRows.GetRange(start, len);
                var sources = slice.Select(r => r.Source ?? "").ToList();
                var notes = slice.Select(r => (string?)r.Comment).ToList();
                try
                {
                    var translated = await TranslationBatchRunner.TranslateBatchAsync(sources, systemPrompt, cfg, ct, notes);
                    for (int i = 0; i < len; i++)
                        if (translated[i] is { } t) // answered (incl. identity) → override; null → fall back
                            translation.KeyOverrides[(slice[i].Namespace ?? "", slice[i].Key ?? "")] = t;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log?.Invoke($"{Utils.Glyphs.Warning} {locale}: commented batch failed ({ex.Message}); {len} row(s) fall back to the standard translation.");
                }
                addUnits(len);
                Emit(progress);
            }
        }

        /// <summary>Per-language output: deduped source→target map plus per-row overrides for
        /// commented rows (Variant A). An override wins over the SourceMap at write time. Keyed by
        /// the (Namespace, Key) tuple — the row's real identity — because Key alone repeats across
        /// namespaces; keying by Key alone would let one commented row's note-aware translation
        /// corrupt a different same-Key row. (Same identity convention as <c>ReviewService</c>.)</summary>
        private sealed class LanguageTranslation
        {
            /// <summary>Set when the circuit breaker aborted this language mid-run.</summary>
            public bool Partial { get; set; }
            public Dictionary<string, string> SourceMap { get; } = new();
            public Dictionary<(string Ns, string Key), string> KeyOverrides { get; } = new();
        }

        /// <summary>
        /// The run's prompts as one text for the prompt history. Languages that get
        /// the same instructions share a block — the prompt is shown once with
        /// <c>{language}</c> where each language's name goes — so a 20-language run
        /// with one shared prompt is one readable prompt, not twenty copies.
        /// </summary>
        internal static string DescribePrompts(MultiTranslateRequest req)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var group in req.Targets.GroupBy(t => t.Prompt ?? ""))
            {
                var locales = group.Select(t => t.Locale).ToList();
                if (sb.Length > 0) sb.Append('\n');
                if (locales.Count == 1)
                {
                    sb.Append('[').Append(locales[0]).Append("]\n\n")
                      .Append(BuildSystemPrompt(req.SourceLanguage, locales[0], group.Key));
                }
                else
                {
                    sb.Append('[').Append(string.Join(", ", locales))
                      .Append("] - sent once per language, with its name in place of {language}\n\n")
                      .Append(BuildSystemPrompt(req.SourceLanguage, "{language}", group.Key));
                }
            }
            return sb.ToString();
        }

        internal static string BuildSystemPrompt(string src, string locale, string extraPrompt)
        {
            var sp = $"You are an expert game localizer. Translate from {src} to {locale}.\n" +
                     "Rules:\n" +
                     "1. DO NOT translate, alter, or reorder anything inside <angle brackets> or {curly braces}. " +
                     "Keep these tags EXACTLY as in the source (same characters, same order). Translate only the readable text outside them; if a string is nothing but such a tag, return it unchanged.\n" +
                     "2. Keep all line breaks and formatting exactly as in the source.\n" +
                     "3. Return the answer STRICTLY as a JSON array in the exact same format as the input.\n" +
                     "   Keep all escaped \\n characters intact where they were.\n" +
                     "   Example: [{\"id\":\"0\",\"text\":\"translated text\"}]\n";
            if (!string.IsNullOrWhiteSpace(extraPrompt))
                sp += $"\nAdditional Instructions: {extraPrompt}\n";
            return sp;
        }

        private static string OutputPathFor(MultiTranslateRequest req, string locale)
            => Path.Combine(req.OutputFolder, locale, req.OriginalFileName);

        private async Task WriteOutputAsync(MultiTranslateRequest req, LanguageTranslation translation,
                                            string destPath, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            // Fan-out: a commented row takes its per-Key override (note-aware); every other row
            // takes its source's deduped translation, or the source itself as the last fallback.
            // Transient rows, released right after the write — no per-language row set is retained.
            var outRows = req.SourceRows.Select(r => new TranslationRow
            {
                Namespace = r.Namespace,
                Key = r.Key,
                Source = r.Source,
                Target = translation.KeyOverrides.TryGetValue((r.Namespace ?? "", r.Key ?? ""), out var ko) ? ko
                       : translation.SourceMap.TryGetValue(r.Source ?? "", out var t) ? t
                       : (r.Source ?? ""),
                // PO round-trip payload (comments/flags/plurals). Plural entries get a
                // translated clone — carrying the source meta verbatim shipped the OLD
                // language's msgstr[1..N] into the new language's file.
                FormatMeta = req.Format == FileFormat.Po
                    ? TranslatePluralMeta(r.FormatMeta, translation)
                    : r.FormatMeta,
            }).ToList();

            switch (req.Format)
            {
                case FileFormat.Po:
                    // Output layout is <folder>/<locale>/<file> — the parent folder IS the language.
                    PoFileService.Export(destPath, outRows, Path.GetFileName(Path.GetDirectoryName(destPath)));
                    break;
                case FileFormat.Locres:
                    WriteLocres(req, outRows, destPath, ct);
                    break;
                default: // Csv (and any fallback)
                    CsvFileService.Export(destPath, outRows, outRows.Any(r => !string.IsNullOrEmpty(r.Namespace)));
                    break;
            }
        }

        /// <summary>
        /// Clone a PO plural entry's meta with msgstr[1..N] set to the TRANSLATED
        /// msgid_plural (form 0 comes from the row's Target as usual), flagged fuzzy:
        /// how many plural forms the target locale needs — and which counts they map
        /// to — differs per language, so a human pass is still required; but the text
        /// is at least in the right language instead of the source file's.
        /// Non-plural metas pass through untouched.
        /// </summary>
        private static object? TranslatePluralMeta(object? formatMeta, LanguageTranslation translation)
        {
            if (formatMeta is not PoFileService.PoEntryMeta m || m.MsgidPlural is null)
                return formatMeta;

            string plural = translation.SourceMap.TryGetValue(m.MsgidPlural, out var t) ? t : m.MsgidPlural;
            var clone = new PoFileService.PoEntryMeta { MsgidPlural = m.MsgidPlural, ImportedTarget = "" };
            clone.CommentLines.AddRange(m.CommentLines);
            clone.Flags.AddRange(m.Flags);
            if (!clone.Flags.Contains("fuzzy")) clone.Flags.Add("fuzzy");
            for (int i = 0; i < m.PluralTargets.Count; i++) clone.PluralTargets.Add(plural);
            clone.PluralIndexes.AddRange(m.PluralIndexes);
            return clone;
        }

        private static void WriteLocres(MultiTranslateRequest req, List<TranslationRow> outRows,
                                        string destPath, CancellationToken ct)
        {
            var doc = req.BaseDocument
                ?? throw new Exception("Base .locres not available to write into.");

            // In-process, atomic, and collision-free by construction: the document
            // is immutable and SaveWith never mutates it, so N language writes and
            // a concurrent app autosave can all overlay the same skeleton.
            var overrides = LocresFileService.BuildOverrides(outRows);
            var result = doc.SaveWith(overrides, destPath, null, ct);

            // A total key mismatch means the wrong base — surface it instead of
            // silently shipping an untranslated file (the old "Translated 0/N").
            if (result.OverridesApplied == 0 && overrides.Count > 0)
                throw new Exception("No rows matched the base .locres keys — wrong base document?");
        }

        private void Emit(MultiTranslateProgress p) => ProgressChanged?.Invoke(p);
    }
}
