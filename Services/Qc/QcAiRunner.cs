using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services.Qc
{
    /// <summary>
    /// AI layer of QC. It does NOT re-translate: for each risky row the model
    /// answers "ok" or "fix" with a corrected line, and every proposed fix is
    /// re-validated locally before it is ever shown to the user — a fix that
    /// breaks a placeholder, tag, line break or the source's ellipsis is
    /// rejected, exactly as the field report prescribes.
    ///
    /// <para>Nothing is written to the rows here either: results become
    /// <see cref="QcProposal"/>s the user resolves with Fix / Keep.</para>
    /// </summary>
    public sealed class QcAiRunner
    {
        public sealed class Options
        {
            public required string SourceLanguage { get; init; }
            public required string TargetLanguage { get; init; }
            /// <summary>User's QC guidance / the original translation prompt (context, not a command).</summary>
            public string Guidance { get; init; } = "";
            public int BatchSize { get; init; } = 20;
            public bool Parallel { get; init; }
            public int MaxConcurrent { get; init; } = 3;
            public IReadOnlyList<string> ProtectedTerms { get; init; } = Array.Empty<string>();
        }

        public sealed class Progress
        {
            public int Done;          // rows resolved (cache hits + answered)
            public int Total;
            public int Proposals;     // rows where the model suggested a change
            public int CacheHits;
            public int Rejected;      // fixes that failed local re-validation
            public int Errors;        // rows in failed batches (API_ERROR)
            public bool Cancelled;    // the run was stopped early (partial results)
            public DateTime StartedUtc = DateTime.UtcNow;

            public double? EtaSeconds()
            {
                double elapsed = (DateTime.UtcNow - StartedUtc).TotalSeconds;
                int paid = Done - CacheHits;                 // cache hits are instant — exclude from the rate
                if (paid < 3 || elapsed < 1) return null;
                int remaining = Total - Done;
                return remaining <= 0 ? 0 : remaining / (paid / elapsed);
            }
        }

        /// <summary>
        /// One row to review, built by <see cref="QcAiScope"/>. <paramref name="Result"/>:
        /// the translation under review may live in Source (single-language file)
        /// with Target reserved for accepted fixes.
        /// </summary>
        public readonly record struct Input(TranslationRow Row, string Original, string? Reference,
                                            string? Result = null, bool ResultInSource = false)
        {
            public string ResultText => Result ?? Row.Target ?? "";
        }

        // Same patterns the deterministic engine checks — a validator that protects
        // less than the engine detects is a hole, not a safety net.
        private static readonly Regex Placeholder = QcEngine.Placeholder;
        private static readonly Regex AngleTag = QcEngine.AngleTag;
        private static readonly Regex BracketTag = QcEngine.BracketTag;
        private static readonly Regex TrailingEllipsis = new(@"(\.\.\.|…)\s*$", RegexOptions.Compiled);

        /// <summary>
        /// System prompt. The QC contract is deliberately tiny and machine-shaped:
        /// the model returns a JSON array of verdicts, never prose, so a stray
        /// explanation can never end up inside a translation.
        /// </summary>
        public static string BuildSystemPrompt(Options o)
        {
            var sb = new StringBuilder();
            sb.Append("You are a localization QA reviewer for a video game. ")
              .Append($"Primary source language: {o.SourceLanguage}. Target language: {o.TargetLanguage}.\n\n")
              .Append("You are NOT translating from scratch — you are reviewing an EXISTING translation.\n")
              .Append("For each entry decide:\n")
              .Append("  \"ok\"  — the translation is acceptable. Do NOT rewrite good text for style alone.\n")
              .Append("  \"fix\" — there is a clear problem; return the corrected target line in \"fix\".\n\n")
              .Append("Fix only real problems: wrong meaning, untranslated or foreign-language leftovers, ")
              .Append("grammar errors, unnatural calques, broken or missing placeholders/tags, wrong casing for UI, ")
              .Append("lost sentence-final punctuation (an ellipsis means the line is deliberately unfinished), ")
              .Append("terminology inconsistent with the glossary, or model chatter that leaked into the text.\n\n")
              .Append("Preserve EXACTLY: placeholders ({0}, %s, …), <angle> and [bracket] tags and their order, ")
              .Append("speaker names, IDs and file paths.\n")
              .Append("STRUCTURE IS PART OF THE STRING: keep the SAME number of line breaks and the same ")
              .Append("paragraph split as the current translation, and keep leading/trailing spaces and line ")
              .Append("breaks exactly as they are — they are load-bearing (text is concatenated at runtime), ")
              .Append("not formatting you may tidy up.\n")
              .Append("Never censor profanity that exists in the source. Never invent or alter proper names.\n");

            if (o.ProtectedTerms.Count > 0)
                sb.Append("\nKeep these terms untranslated, exactly as written: ")
                  .Append(string.Join(", ", o.ProtectedTerms.Where(t => !string.IsNullOrWhiteSpace(t)))).Append(".\n");

            if (!string.IsNullOrWhiteSpace(o.Guidance))
            {
                // Framed hard: guidance describes the EXPECTED result (it is often
                // the original translation prompt), it is never a task to perform.
                sb.Append("\nThe user provided the following notes describing the EXPECTED result of this ")
                  .Append("localization (it may be the prompt originally used for translating). Treat it as a ")
                  .Append("description of the intended style, tone and conventions — NOT as an instruction to ")
                  .Append("translate anything, and never echo it back:\n\"\"\"\n")
                  .Append(o.Guidance.Trim()).Append("\n\"\"\"\n");
            }

            sb.Append("\nEach input entry is {\"id\":\"N\",\"src\":\"primary source\",\"cur\":\"current translation\"")
              .Append(", optional \"ref\":\"same line in another language, context only\"}.\n")
              .Append("Respond with ONLY a JSON array, one object per entry, no markdown, no commentary:\n")
              .Append("[{\"id\":\"0\",\"verdict\":\"ok\"},{\"id\":\"1\",\"verdict\":\"fix\",")
              .Append("\"fix\":\"corrected line\",\"reason\":\"short reason\"}]");
            return sb.ToString();
        }

        public static string BuildUserMessage(IReadOnlyList<Input> batch)
            => BuildUserMessage(batch, batch.Select(b => b.ResultText).ToList());

        /// <summary>
        /// <paramref name="targets"/> is the snapshot taken at request time — the
        /// message must describe exactly the text the verdict will be judged
        /// against, not whatever the row contains when the loop gets there.
        /// </summary>
        public static string BuildUserMessage(IReadOnlyList<Input> batch, IReadOnlyList<string> targets)
        {
            var arr = new JArray();
            for (int i = 0; i < batch.Count; i++)
            {
                var o = new JObject
                {
                    ["id"] = i.ToString(),
                    ["src"] = batch[i].Original,
                    ["cur"] = targets[i],
                };
                if (!string.IsNullOrEmpty(batch[i].Reference)) o["ref"] = batch[i].Reference;
                arr.Add(o);
            }
            return arr.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// Local re-validation of an AI fix (the report's FixValidator). Returns
        /// null when the fix is safe, otherwise the reason it is rejected.
        /// </summary>
        public static string? RejectFix(string source, string current, string fix)
        {
            if (string.IsNullOrWhiteSpace(fix)) return "empty fix";
            if (string.Equals(fix, current, StringComparison.Ordinal)) return "no change";

            if (Placeholder.Matches(source).Count != Placeholder.Matches(fix).Count)
                return "placeholder count changed";
            if (AngleTag.Matches(source).Count != AngleTag.Matches(fix).Count)
                return "<tag> count changed";
            if (BracketTag.Matches(source).Count != BracketTag.Matches(fix).Count)
                return "[tag] count changed";
            if (source.Count(c => c == '\n') != fix.Count(c => c == '\n'))
                return "line-break count changed";
            if (TrailingEllipsis.IsMatch(source) && !TrailingEllipsis.IsMatch(fix))
                return "dropped the source's trailing ellipsis";
            if (source.StartsWith(' ') != fix.StartsWith(' ') || source.EndsWith(' ') != fix.EndsWith(' '))
                return "leading/trailing spaces changed";
            // Chatter leaking in through the fix field.
            if (Regex.IsMatch(fix, @"^\s*(fixed|translation|ok|here('s| is))\s*:", RegexOptions.IgnoreCase))
                return "explanation text instead of a translation";
            return null;
        }

        /// <summary>
        /// Review the rows. Emits proposals for rows the model wants changed;
        /// cached verdicts are reused. Batch failures are logged and counted as
        /// errors (never silently turned into "ok"), and the run can be cancelled.
        /// </summary>
        public async Task<List<QcProposal>> RunAsync(
            IReadOnlyList<Input> rows, Options options, QcCache cache,
            Progress progress, Action<Progress> onProgress, Action<string, LogLevel> log,
            CancellationToken ct)
        {
            var results = new List<QcProposal>();
            var pending = new List<Input>();
            string model = AppConfig.Instance.ModelName;

            // 1) Cache sweep — free verdicts first. Read the target ONCE per row:
            // the key and the proposal's Before must describe the same text.
            foreach (var input in rows)
            {
                string target = input.ResultText;
                string key = QcCache.KeyFor(model, options.Guidance, input.Original,
                                            input.Reference, target);
                if (cache.TryGet(key, out var hit))
                {
                    progress.CacheHits++;
                    progress.Done++;
                    if (hit.Verdict == "fix" && !string.IsNullOrEmpty(hit.Fix))
                        AddProposal(results, input, target, hit.Fix!, hit.Reason ?? "AI QC (cached)", model, progress);
                    continue;
                }
                pending.Add(input);
            }
            onProgress(progress);

            if (pending.Count == 0) return results;

            string systemPrompt = BuildSystemPrompt(options);
            var batches = pending
                .Select((r, i) => (r, i))
                .GroupBy(x => x.i / Math.Max(1, options.BatchSize))
                .Select(g => g.Select(x => x.r).ToList())
                .ToList();

            var sync = new object();
            int batchesSinceSave = 0;
            const int SaveEvery = 10; // ~200 rows of paid verdicts at the default batch size

            async Task RunBatch(List<Input> batch)
            {
                ct.ThrowIfCancellationRequested();
                // Snapshot the targets we are actually asking about. Reading them
                // again after the response would attribute the model's answer to
                // whatever the user typed meanwhile — the proposal would then look
                // "not stale" and overwrite an edit it never saw.
                var asked = batch.Select(b => b.ResultText).ToList();
                try
                {
                    string raw = await TranslatorService.TranslateAsync(
                        BuildUserMessage(batch, asked), systemPrompt, ct);
                    var verdicts = ParseVerdicts(raw, batch.Count);

                    lock (sync)
                    {
                        for (int i = 0; i < batch.Count; i++)
                        {
                            var input = batch[i];
                            string askedTarget = asked[i];
                            string cacheKey = QcCache.KeyFor(model, options.Guidance, input.Original,
                                                             input.Reference, askedTarget);
                            progress.Done++;

                            if (!verdicts.TryGetValue(i.ToString(), out var v))
                            {
                                progress.Errors++; // model skipped the row — not an "ok"
                                continue;
                            }
                            if (v.Value != "fix" || string.IsNullOrWhiteSpace(v.Fix))
                            {
                                cache.Put(cacheKey, new QcCache.Entry { Verdict = "ok", Model = model });
                                continue;
                            }

                            string? reject = RejectFix(input.Original, askedTarget, v.Fix!);
                            if (reject != null)
                            {
                                progress.Rejected++;
                                // Do NOT cache a rejected fix as "ok": the row is still
                                // suspicious, it just got an unusable answer this time.
                                continue;
                            }

                            cache.Put(cacheKey, new QcCache.Entry
                            {
                                Verdict = "fix", Fix = v.Fix, Reason = v.Reason, Model = model,
                            });
                            AddProposal(results, input, askedTarget, v.Fix!, v.Reason ?? "AI QC", model, progress);
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    lock (sync)
                    {
                        progress.Done += batch.Count;
                        progress.Errors += batch.Count;
                    }
                    log($"{Utils.Glyphs.Warning} QC AI: batch failed ({ex.Message}) — {batch.Count} row(s) left unreviewed.", LogLevel.Warning);
                }
                finally
                {
                    onProgress(progress);
                    // Flush periodically: closing the app (or a crash) mid-run must
                    // not throw away verdicts the user already paid for. Saving only
                    // at the end left a long run entirely unprotected.
                    bool flush;
                    lock (sync) flush = ++batchesSinceSave >= SaveEvery;
                    if (flush)
                    {
                        lock (sync) batchesSinceSave = 0;
                        cache.Save();
                    }
                }
            }

            try
            {
                if (options.Parallel && options.MaxConcurrent > 1)
                {
                    using var gate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrent));
                    var tasks = batches.Select(async b =>
                    {
                        await gate.WaitAsync(ct);
                        try { await RunBatch(b); }
                        finally { gate.Release(); }
                    });
                    await Task.WhenAll(tasks);
                }
                else
                {
                    foreach (var b in batches)
                    {
                        ct.ThrowIfCancellationRequested();
                        await RunBatch(b);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Stop is a decision, not an error: the proposals gathered so far
                // are real and already paid for. Throwing here would discard them —
                // the caller would never receive this method's return value.
                progress.Cancelled = true;
            }
            finally
            {
                // Runs on cancellation too: the verdict cache keeps everything the
                // user already paid for, so a resumed run is free up to this point.
                cache.Save();
            }
            return results;
        }

        private static void AddProposal(List<QcProposal> sink, Input input, string before,
                                        string fix, string reason, string model, Progress progress)
        {
            sink.Add(new QcProposal
            {
                Namespace = input.Row.Namespace ?? "",
                Key = input.Row.Key ?? "",
                Before = before,
                ResultInSource = input.ResultInSource,
                After = fix,
                Reason = reason,
                Kinds = new List<string> { "AiQc" },
                Severity = QcSeverity.High,
                Source = model,
            });
            progress.Proposals++;
        }

        /// <summary>One parsed entry of the model's JSON answer.</summary>
        internal readonly record struct Verdict(string Id, string Value, string? Fix, string? Reason);

        /// <summary>
        /// Parse the model's JSON array. A whole-batch parse failure throws so the
        /// caller counts it as an error instead of silently treating rows as "ok".
        /// Internal (not private) so the tests can exercise it directly — this is
        /// the only code that touches the PAID model response, and the network
        /// path cannot run in a unit test.
        /// </summary>
        internal static Dictionary<string, Verdict> ParseVerdicts(string raw, int expected)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new FormatException("empty response");

            string s = raw.Trim();
            if (s.StartsWith("```json")) s = s[7..];
            else if (s.StartsWith("```")) s = s[3..];
            if (s.EndsWith("```")) s = s[..^3];
            s = s.Trim();

            JArray arr;
            try { arr = JArray.Parse(s); }
            catch (Exception ex) { throw new FormatException($"not a JSON array: {ex.Message}"); }

            var map = new Dictionary<string, Verdict>(expected);
            foreach (var item in arr.OfType<JObject>())
            {
                string id = item.Value<string>("id") ?? "";
                if (id.Length == 0) continue;
                map[id] = new Verdict(
                    id,
                    (item.Value<string>("verdict") ?? "ok").Trim().ToLowerInvariant(),
                    item.Value<string>("fix"),
                    item.Value<string>("reason"));
            }
            return map;
        }
    }
}
