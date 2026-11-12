using System.Text.RegularExpressions;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// A QC run belongs to the file it started on (audit 2026-09 §7).
///
/// <para>The run awaits before it starts — loading the reference / original
/// file. The old local run pinned the file only after that await, so a tab
/// switch during the load labelled A's findings as B's. The AI run (the only
/// run since 2026-10-21) inherits the rule: pin first, check after the load,
/// and merge only into the pinned file. The panel needs a window, so this
/// reads its source.</para>
/// </summary>
public class QcRunPinningTests
{
    private static string OnRunAi()
    {
        string source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "Controls", "QcPanel.axaml.cs"));
        var m = Regex.Match(source, @"private async void OnRunAi\([^)]*\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline);
        Assert.True(m.Success, "QcPanel.OnRunAi not found");
        return Regex.Replace(m.Groups["body"].Value, @"//[^\n]*", "");
    }

    [Fact]
    public void The_file_is_pinned_before_the_first_await()
    {
        string body = OnRunAi();
        int pin = body.IndexOf("string runFile = QcService.Instance.CurrentFile", StringComparison.Ordinal);
        int session = body.IndexOf("var runSession = _vm.ActiveSession", StringComparison.Ordinal);
        int firstAwait = body.IndexOf("await ", StringComparison.Ordinal);
        Assert.True(pin >= 0 && session >= 0, "the run must capture its file and tab");
        Assert.True(pin < firstAwait && session < firstAwait, "…before it first yields");
    }

    [Fact]
    public void The_extra_file_loads_are_followed_by_an_ownership_check()
    {
        string body = OnRunAi();
        var loads = Regex.Matches(body, @"await Load\w+Async\(");
        Assert.True(loads.Count >= 2, "expected the reference load and the original load");
        int lastLoad = loads.Cast<Match>().Last().Index;
        int check = body.IndexOf("if (Superseded())", lastLoad, StringComparison.Ordinal);
        int run = body.IndexOf("runner.RunAsync(", StringComparison.Ordinal);
        Assert.True(check > lastLoad && check < run, "Superseded() must be checked after the loads, before anything is sent");
    }

    [Fact]
    public void Results_merge_only_into_the_pinned_file()
    {
        string body = OnRunAi();
        Assert.Contains("QcService.Instance.MergeProposals(aiProposals, runFile", body);
    }
}
