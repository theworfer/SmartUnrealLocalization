using System;
using System.Collections.Generic;
using System.Linq;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services
{
    /// <summary>One change a fix would make, computed before anything is written.</summary>
    public sealed record PlannedFix(TranslationRow Row, string Before, string After, IReadOnlyList<string> Labels);

    /// <summary>
    /// Works out what the validator's fixes WOULD do, on a scratch copy of each
    /// row, so the user can see every change before it happens (2026-10-21).
    ///
    /// <para>Fix all used to write straight into the file: one fix per row per
    /// pass, re-validating between passes, nothing shown until it was done. The
    /// same loop now runs on a copy and returns before → after. A plan is
    /// DROPPED when the result carries an error or warning the original did not —
    /// a fix that breaks something else is not offered at all.</para>
    /// </summary>
    public static class FixPlanner
    {
        private const int MaxPasses = 6;

        public sealed record Result(IReadOnlyList<PlannedFix> Fixes, int Refused);

        public static Result Plan(IEnumerable<TranslationRow> rows, Func<TranslationRow, IEnumerable<Problem>>? only = null)
        {
            var fixes = new List<PlannedFix>();
            int refused = 0;
            foreach (var row in rows)
            {
                var start = (only?.Invoke(row) ?? row.Problems).FirstOrDefault(p => p.Fix is not null);
                if (start is null) continue;

                var plan = PlanRow(row, start);
                if (plan is null) refused++;
                else if (plan.After != plan.Before) fixes.Add(plan);
            }
            return new Result(fixes, refused);
        }

        /// <summary>Fixes for one row, starting with <paramref name="first"/>; null when refused.</summary>
        public static PlannedFix? PlanRow(TranslationRow row, Problem first)
        {
            string before = row.Target ?? "";
            var scratch = new TranslationRow { Namespace = row.Namespace, Key = row.Key, Source = row.Source, Target = before };
            ValidationService.Instance.RunForRow(scratch);
            var serious = SeriousKinds(scratch);

            var labels = new List<string>();
            var next = first;
            for (int pass = 0; pass < MaxPasses && next?.Fix is { } fix; pass++)
            {
                if (!string.IsNullOrEmpty(fix.Label) && !labels.Contains(fix.Label)) labels.Add(fix.Label);
                scratch.Target = fix.NewValue;
                ValidationService.Instance.RunForRow(scratch);
                next = scratch.Problems.FirstOrDefault(p => p.Fix is not null);
            }

            // Anything serious the original did not have? Then the fix broke it.
            var after = SeriousKinds(scratch);
            foreach (var (kind, count) in after)
                if (!serious.TryGetValue(kind, out int was) || count > was) return null;

            return new PlannedFix(row, before, scratch.Target ?? "", labels);
        }

        private static Dictionary<string, int> SeriousKinds(TranslationRow r)
            => r.Problems.Where(p => p.Severity is ProblemSeverity.Error or ProblemSeverity.Warning)
                         .GroupBy(p => p.Kind, StringComparer.Ordinal)
                         .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    }
}
