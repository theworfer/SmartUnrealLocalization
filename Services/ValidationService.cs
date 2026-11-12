using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services.Validators;

namespace SmartUnrealLocalization.Services
{
    /// <summary>
    /// Owns the pluggable validator pipeline and per-row validation lifecycle.
    /// All writes to <see cref="TranslationRow.Problems"/> in the codebase go
    /// through this service — keeps Problems a single-writer surface so the
    /// computed <c>Severity</c> / <c>HasFixableProblem</c> / <c>LeftEdgeBrush</c>
    /// stay coherent.
    ///
    /// <list type="bullet">
    ///   <item><see cref="LightValidators"/> — 7 fast per-row rules.
    ///         Run on every Target change via <see cref="ScheduleRowValidation"/>
    ///         (debounced 300 ms) and on initial file open via
    ///         <see cref="RunInitialAsync"/>.</item>
    ///   <item><c>HeavyValidators</c> — added in Etap 7. Run on file open +
    ///         F8 only because they're cross-row O(N²) or expensive parses.</item>
    /// </list>
    /// </summary>
    public sealed class ValidationService
    {
        private static readonly Lazy<ValidationService> _lazy = new(() => new ValidationService());
        public static ValidationService Instance => _lazy.Value;

        public IReadOnlyList<IValidator> LightValidators { get; }

        /// <summary>The open file's language as an English name ("Ukrainian"), for the
        /// leftover-language check. Empty = unknown, and that check stays silent.</summary>
        public string TargetLanguage { get; set; } = "";
        public IReadOnlyList<IHeavyValidator> HeavyValidators { get; }

        // Cached Kind sets so RunForRow can wipe only LIGHT problems (leaving
        // heavy ones intact between debounced edits) and RunHeavy can wipe
        // only heavy problems. Without this separation, an edit would clear
        // every problem then only re-run lights, dropping all heavy results
        // until the next F8 — confusing UX.
        private readonly HashSet<string> _lightKinds;
        private readonly HashSet<string> _heavyKinds;

        // Per-row debounce timers. Keyed by row reference because rows are
        // unique-per-instance for the lifetime of an open file (we never share
        // a row across files). Dictionary leaks one entry per row while the
        // file is open — bounded and cleared in <see cref="ClearDebounce"/>.
        private readonly Dictionary<TranslationRow, CancellationTokenSource> _debounce = new();
        private readonly object _debounceLock = new();
        private const int DebounceMs = 300;

        private ValidationService()
        {
            LightValidators = new IValidator[]
            {
                new TagMismatchValidator(),
                new TokenMismatchValidator(),
                // printf placeholders (%s, %d, %1$s). Added after an audit found
                // that NOTHING in the live pass looked at them — a dropped %d only
                // surfaced if the user happened to run QC.
                new PlaceholderMismatchValidator(),
                new WhitespaceValidator(),
                new PunctuationValidator(),
                new MixedScriptsValidator(),
                new ContentMismatchValidator(),
                // PluralIncomplete + LengthExcessive moved from heavy to
                // light in the post-Etap-7 audit pass. Both are per-row
                // checks (no cross-row state), and being light means they
                // auto-refresh 300 ms after a Target edit instead of waiting
                // for F8. DuplicateSource truly needs the whole row set, so
                // it stays heavy.
                new PluralIncompleteValidator(),
                new LengthExcessiveValidator(),
                // Validator stage 3 (2026-10-21): the checks only QC's local pass ran.
                new ModelChatterValidator(),
                new LineBreakValidator(),
                new LanguageLeakValidator(),
                new ProtectedTermValidator(),
                new TerminologyValidator(),
            };

            HeavyValidators = new IHeavyValidator[]
            {
                new DuplicateSourceValidator(),
            };

            _lightKinds = new HashSet<string>(LightValidators.Select(v => v.Kind));
            _heavyKinds = new HashSet<string>(HeavyValidators.Select(v => v.Kind));
        }

        /// <summary>
        /// Run every light validator against <paramref name="row"/> and replace
        /// only the LIGHT-kind problems on <see cref="TranslationRow.Problems"/>
        /// with the new findings. Heavy-kind problems (DuplicateSource — the only
        /// heavy validator) are preserved: it's cross-row, so we keep its findings
        /// until the next explicit <see cref="RunHeavy"/> call. MUST be called on the UI thread
        /// (mutates ObservableCollection).
        /// </summary>
        public void RunForRow(TranslationRow row)
        {
            if (row == null) return;

            var found = new List<Problem>();
            foreach (var v in LightValidators)
            {
                if (!IsCheckEnabled(v.Kind)) continue;   // its old findings go below with the rest
                try { v.Validate(row, found); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ValidationService] {v.Kind} threw on row '{row.Key}': {ex.Message}");
                }
            }

            // Replace ONLY light-kind problems. Walk backwards so RemoveAt indices stay valid.
            var problems = row.Problems;
            for (int i = problems.Count - 1; i >= 0; i--)
            {
                if (_lightKinds.Contains(problems[i].Kind))
                    problems.RemoveAt(i);
            }
            foreach (var p in found) problems.Add(p);
        }

        /// <summary>
        /// Run every heavy validator over the whole row set. Strips prior
        /// heavy-kind problems first so a Source change between calls doesn't
        /// leave stale DuplicateSource flags behind. UI-thread only.
        /// </summary>
        public void RunHeavy(IReadOnlyList<TranslationRow> rows)
        {
            if (rows == null || rows.Count == 0) return;

            // Clear prior heavy-kind problems from every row first.
            foreach (var row in rows)
            {
                var problems = row.Problems;
                for (int i = problems.Count - 1; i >= 0; i--)
                {
                    if (_heavyKinds.Contains(problems[i].Kind))
                        problems.RemoveAt(i);
                }
            }

            // Run each heavy validator over the full set.
            foreach (var v in HeavyValidators)
            {
                if (!IsCheckEnabled(v.Kind)) continue;
                try { v.ValidateAll(rows); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ValidationService] heavy {v.Kind} threw: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// The optional NOTES, each switched in Settings → Validation. Errors and
        /// warnings — what breaks the game or is almost certainly wrong — always run.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, Func<AppConfig, bool>> OptionalChecks =
            new Dictionary<string, Func<AppConfig, bool>>(StringComparer.Ordinal)
            {
                ["Whitespace"] = c => c.CheckEdgeWhitespace,
                ["Punctuation"] = c => c.CheckEndingPunctuation,
                ["LengthExcessive"] = c => c.CheckLengthRatio,
                ["DuplicateSource"] = c => c.CheckDuplicateSources,
            };

        /// <summary>Test seam: which config the switches are read from.</summary>
        internal Func<AppConfig> Config { get; set; } = () => AppConfig.Instance;

        public bool IsCheckEnabled(string kind)
            => !OptionalChecks.TryGetValue(kind, out var on) || on(Config());

        /// <summary>
        /// Re-run every check on every row — after a switch changed. A switched-off
        /// check's findings disappear, a switched-on one's appear. UI thread only.
        /// </summary>
        public void RevalidateAll(IReadOnlyList<TranslationRow> rows)
        {
            foreach (var row in rows) RunForRow(row);
            RunHeavy(rows);
        }

        /// <summary>
        /// Remove every <see cref="Problem"/> on <paramref name="row"/> whose
        /// Kind matches <paramref name="kind"/>. Used by bulk "Clear cell
        /// recovered" / "Clear tag replaced" commands that pre-date the
        /// validators but still need a programmatic clear path.
        /// </summary>
        public void RemoveProblems(TranslationRow row, string kind)
        {
            if (row == null) return;
            for (int i = row.Problems.Count - 1; i >= 0; i--)
            {
                if (row.Problems[i].Kind == kind)
                    row.Problems.RemoveAt(i);
            }
        }

        /// <summary>
        /// Schedule a per-row light-validation run 300 ms after the last call
        /// for the same row. Coalesces burst typing into a single validate
        /// pass per pause; cancels previous timer transparently.
        /// </summary>
        public void ScheduleRowValidation(TranslationRow row)
        {
            if (row == null) return;

            CancellationTokenSource cts;
            lock (_debounceLock)
            {
                if (_debounce.TryGetValue(row, out var existing))
                {
                    existing.Cancel();
                    existing.Dispose();
                }
                cts = new CancellationTokenSource();
                _debounce[row] = cts;
            }

            var token = cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(DebounceMs, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested) return;
                    await Dispatcher.UIThread.InvokeAsync(() => RunForRow(row));
                }
                catch (TaskCanceledException) { /* expected on re-schedule */ }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ValidationService] Debounce task failed: {ex.Message}");
                }
                finally
                {
                    lock (_debounceLock)
                    {
                        if (_debounce.TryGetValue(row, out var c) && c == cts)
                            _debounce.Remove(row);
                    }
                    cts.Dispose();
                }
            }, CancellationToken.None);
        }

        /// <summary>
        /// Drop every pending debounced validation. Called when switching
        /// files so a previous file's queued work doesn't fire against
        /// already-disposed rows.
        /// </summary>
        public void ClearDebounce()
        {
            lock (_debounceLock)
            {
                foreach (var cts in _debounce.Values)
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                _debounce.Clear();
            }
        }

        /// <summary>
        /// Run every light validator on every row in <paramref name="rows"/>,
        /// reporting progress every <c>BatchSize</c> rows and yielding to the
        /// UI thread between batches so the ValidationProgressDialog can
        /// repaint and stay responsive to Cancel.
        ///
        /// <para>
        /// Cancellation throws <see cref="OperationCanceledException"/>; the
        /// caller is expected to clear partial Problems if it wants a fully
        /// clean state on cancel.
        /// </para>
        /// </summary>
        public async Task RunInitialAsync(
            IReadOnlyList<TranslationRow> rows,
            IProgress<ValidationProgressUpdate>? progress,
            CancellationToken ct)
        {
            const int BatchSize = 100;
            int total = rows.Count;
            if (total == 0)
            {
                progress?.Report(new ValidationProgressUpdate(0, 0, "done"));
                return;
            }

            for (int batchStart = 0; batchStart < total; batchStart += BatchSize)
            {
                ct.ThrowIfCancellationRequested();
                int batchEnd = Math.Min(batchStart + BatchSize, total);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    for (int i = batchStart; i < batchEnd; i++)
                        RunForRow(rows[i]);
                }, DispatcherPriority.Background);

                progress?.Report(new ValidationProgressUpdate(batchEnd, total, "light"));
            }

            // Heavy validators after light, also on the UI thread so they
            // can mutate row.Problems safely. Reported as one combined
            // "heavy" phase since the per-validator timing tends to be
            // dominated by DuplicateSource's O(N) dictionary build.
            ct.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() => RunHeavy(rows),
                DispatcherPriority.Background);
            progress?.Report(new ValidationProgressUpdate(total, total, "heavy"));

            progress?.Report(new ValidationProgressUpdate(total, total, "done"));
        }
    }

    /// <summary>One progress tick from <see cref="ValidationService.RunInitialAsync"/>.</summary>
    public readonly record struct ValidationProgressUpdate(int Current, int Total, string Phase);
}
