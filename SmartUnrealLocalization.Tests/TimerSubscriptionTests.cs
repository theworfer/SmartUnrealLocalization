using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// A guard against a class of bug, not one instance of it: subscribing to a
/// timer's <c>Tick</c> somewhere other than where the timer is created.
///
/// <para>A subscription made in a method that runs repeatedly adds one more
/// handler every time it runs — and stopping the timer removes none of them.
/// That is how the find bar's and the text filter's debounces ran their work
/// once per request instead of once per burst (2026-09-09 audit, P1 §2). Every
/// correct use in this codebase subscribes on the line right after
/// <c>new DispatcherTimer</c>: once per timer, for that timer's life. Anything
/// that needs coalescing should use <c>Services.Debouncer</c>.</para>
/// </summary>
public class TimerSubscriptionTests
{
    private const int Window = 3;   // lines above a Tick += that may hold the construction

    /// <summary>"x.Tick +=" — captures the subscribed timer's name.</summary>
    private static readonly System.Text.RegularExpressions.Regex SubscribedTimer =
        new(@"(\w+)\s*(?:!)?\.Tick\s*\+=");

    [Fact]
    public void Every_Tick_subscription_sits_next_to_the_timer_it_subscribes_to()
    {
        string root = TestPaths.RepoRoot();
        var offenders = new List<string>();

        foreach (var dir in new[] { "ViewModels", "Views", "Controls", "Services", "Models", "Utils" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                 || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].TrimStart();
                    if (line.StartsWith("//") || line.StartsWith("///") || line.StartsWith("*")) continue;
                    var sub = SubscribedTimer.Match(lines[i]);
                    if (!sub.Success) continue;

                    // The timer being subscribed must have been CREATED just above —
                    // assigned with a plain "=", to that same variable. The lazy
                    // "timer ??= new DispatcherTimer" is the exact shape of the bug:
                    // the construction runs once, the subscription every call.
                    string name = sub.Groups[1].Value;
                    var createdHere = new System.Text.RegularExpressions.Regex(
                        $@"(?<![\w?])(?:var\s+)?{System.Text.RegularExpressions.Regex.Escape(name)}\s*=\s*new\s+(?:Avalonia\.Threading\.)?DispatcherTimer");

                    bool ok = false;
                    for (int j = Math.Max(0, i - Window); j < i; j++)
                        if (createdHere.IsMatch(lines[j]) && !lines[j].Contains("??=")) ok = true;

                    if (!ok)
                        offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }

        Assert.True(offenders.Count == 0,
            "Tick subscribed away from the timer's construction — it will accumulate one handler per call. " +
            "Use Services.Debouncer, or subscribe once where the timer is created:\n" + string.Join("\n", offenders));
    }
}
