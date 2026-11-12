using System;
using System.Collections.Generic;
using System.Linq;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services.Qc
{
    /// <summary>
    /// Which rows the AI review sends (2026-10-21): every row in scope, minus the
    /// ones the user chose to skip.
    ///
    /// <para>The review used to send only a "risk bucket" built from QC's own
    /// local pass. That pass was removed — the live validator does that job now —
    /// and the AI review stands on its own, as it was always meant to. What
    /// it skips is the user's choice, one switch per reason, each with its count so
    /// the cost is visible before anything is sent.</para>
    /// </summary>
    public static class QcAiScope
    {
        public sealed class Options
        {
            /// <summary>No translation to review (or no original to compare with).</summary>
            public bool SkipEmpty { get; init; } = true;
            /// <summary>Only tags, numbers or placeholders — copied, nothing to translate.</summary>
            public bool SkipAutoCarried { get; init; } = true;
            /// <summary>The translation is the original verbatim (a name, a code, an untranslated copy).</summary>
            public bool SkipIdenticalToOriginal { get; init; } = true;
            /// <summary>A reviewer already marked the row approved.</summary>
            public bool SkipApproved { get; init; } = true;
            /// <summary>Only rows on which the validator found something.</summary>
            public bool OnlyFlaggedByValidator { get; init; }

            public static Options FromConfig(AppConfig c) => new()
            {
                SkipEmpty = c.QcSkipEmpty,
                SkipAutoCarried = c.QcSkipAutoCarried,
                SkipIdenticalToOriginal = c.QcSkipIdenticalToOriginal,
                SkipApproved = c.QcSkipApproved,
                OnlyFlaggedByValidator = c.QcOnlyValidatorFlagged,
            };
        }

        /// <summary>The rows that go, and how many each switch kept out.</summary>
        public sealed record Result(
            IReadOnlyList<QcAiRunner.Input> Inputs,
            int SkippedEmpty, int SkippedAutoCarried, int SkippedIdentical,
            int SkippedApproved, int SkippedUnflagged, int NoOriginal);

        /// <param name="rows">The scope: the whole file or the checked rows.</param>
        /// <param name="resultInTarget">Where the reviewed translation lives (Source for a single-language file).</param>
        /// <param name="originalMap">The original by key, when it comes from another file; else the row's Source.</param>
        public static Result Build(IEnumerable<TranslationRow> rows, Options options, bool resultInTarget = true,
            IReadOnlyDictionary<(string Ns, string Key), string>? originalMap = null,
            IReadOnlyDictionary<(string Ns, string Key), string>? referenceMap = null)
        {
            var inputs = new List<QcAiRunner.Input>();
            int empty = 0, carried = 0, identical = 0, approved = 0, unflagged = 0, noOriginal = 0;

            foreach (var r in rows)
            {
                var id = (r.Namespace ?? "", r.Key ?? "");
                string result = resultInTarget ? r.Target ?? "" : r.Source ?? "";
                string original;
                if (originalMap != null)
                {
                    if (!originalMap.TryGetValue(id, out original!)) { noOriginal++; continue; }
                }
                else original = r.Source ?? "";

                bool isEmpty = string.IsNullOrWhiteSpace(result) || string.IsNullOrWhiteSpace(original);
                if (isEmpty && options.SkipEmpty) { empty++; continue; }
                if (!isEmpty && options.SkipAutoCarried && QcEngine.ShouldSkipTexts(original, result)) { carried++; continue; }
                if (!isEmpty && options.SkipIdenticalToOriginal && string.Equals(original.Trim(), result.Trim(), StringComparison.Ordinal)) { identical++; continue; }
                if (options.SkipApproved && r.Review == ReviewStatus.Approved) { approved++; continue; }
                if (options.OnlyFlaggedByValidator && r.Problems.Count == 0) { unflagged++; continue; }

                string? reference = referenceMap != null && referenceMap.TryGetValue(id, out var rt) ? rt : null;
                inputs.Add(new QcAiRunner.Input(r, original, reference, result, ResultInSource: !resultInTarget));
            }
            return new Result(inputs, empty, carried, identical, approved, unflagged, noOriginal);
        }
    }
}
