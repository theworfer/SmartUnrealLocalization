using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// Every live check and the fix planner under generated, hostile rows (audit 2026-11):
/// a validator runs on every keystroke, so one that throws breaks editing; a planned
/// fix must change the text and must not add an error or warning the row did not have.
/// </summary>
[Collection("AppState")]
public class ValidatorFuzzTests
{
    private static readonly string[] Atoms =
    {
        "Hello", "Привіт", "что", "{0}", "{1}", "{name}", "%s", "%d", "%1$s", "%.2f", "%", "100%",
        "<b>", "</b>", "<br/>", "<font color=\"red\">", "</>", "<", ">", "[b]", "[/b]", "[Charged]", "[", "]",
        "{", "}", "{{", "}}", "\n", "\r\n", "\t", " ", "  ", "…", "...", "!", "?", ".", ":", "—",
        "Translation:", "\"", "'", "😀", "ä¸­", "\u200B", "\u00A0", "(s)", "|plural(one=x,other=y)", "{0}|plural(one=a,other=b)",
    };

    private static string Rand(Random rng, int max = 7)
    {
        int n = rng.Next(0, max);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < n; i++) sb.Append(Atoms[rng.Next(Atoms.Length)]);
        return sb.ToString();
    }

    [Fact]
    public void Validators_and_the_fix_planner_hold_up_under_hostile_rows()
    {
        var rng = new Random(4242);
        var svc = ValidationService.Instance;
        string was = svc.TargetLanguage;
        svc.TargetLanguage = "Ukrainian";
        try
        {
            for (int trial = 0; trial < 4000; trial++)
            {
                var row = new TranslationRow { Namespace = "NS", Key = "K" + trial, Source = Rand(rng), Target = Rand(rng) };
                var found = new List<Problem>();
                foreach (var v in svc.LightValidators)
                {
                    try { v.Validate(row, found); }
                    catch (Exception ex) { Assert.Fail($"{v.Kind} threw on [{Show(row)}]: {ex.GetType().Name}: {ex.Message}"); }
                }

                svc.RunForRow(row);
                var before = row.Problems.Where(p => p.Severity >= ProblemSeverity.Warning).Select(p => p.Kind).ToHashSet();
                FixPlanner.Result plan;
                try { plan = FixPlanner.Plan(new[] { row }); }
                catch (Exception ex) { Assert.Fail($"FixPlanner threw on [{Show(row)}]: {ex.GetType().Name}: {ex.Message}"); return; }

                foreach (var f in plan.Fixes)
                {
                    Assert.NotEqual(f.Before, f.After);
                    var probe = new TranslationRow { Namespace = row.Namespace, Key = row.Key, Source = row.Source, Target = f.After };
                    svc.RunForRow(probe);
                    var added = probe.Problems.Where(p => p.Severity >= ProblemSeverity.Warning && !before.Contains(p.Kind)).Select(p => p.Kind).ToList();
                    Assert.True(added.Count == 0, $"fix on [{Show(row)}] -> [{f.After}] adds {string.Join(",", added)}");
                }
            }
        }
        finally { svc.TargetLanguage = was; }
    }

    private static string Show(TranslationRow r) => $"{r.Source} => {r.Target}".Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
}
