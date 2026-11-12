using System.Text.RegularExpressions;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// What the status bar shows when the code sets a line and the log adds its own.
///
/// <para><c>AddLog</c> posts to the dispatcher, so its copy of a line reached the
/// bar AFTER whatever the caller set in the same turn: Import Target's "Imported
/// N translations" was replaced within the frame by its last detail line, and
/// writing the summary last could not help (backlog item 8, 2026-09-23). A first
/// fix ranked explicit lines above info lines and failed in the render pass: the
/// explicit "Opening Game.locres…" then outranked the "Loaded 14 115 rows" logged
/// when the open finished, and stayed on the bar for a file already open. These
/// tests pin the rule that serves both: an explicit line owns the rest of its own
/// turn, problems always show, and otherwise the newest ISSUED line wins.</para>
/// </summary>
public class StatusArbiterTests
{
    /// <summary>A dispatcher stand-in: "end of turn" runs only when the test says so.</summary>
    private sealed class Turns
    {
        private readonly List<Action> _pending = new();
        public void Schedule(Action a) => _pending.Add(a);
        public void End() { var run = _pending.ToArray(); _pending.Clear(); foreach (var a in run) a(); }
    }

    private static (StatusArbiter Arbiter, Turns Turns) Build()
    {
        var turns = new Turns();
        return (new StatusArbiter(turns.Schedule), turns);
    }

    [Fact]
    public void An_empty_bar_takes_a_log_line()
    {
        var (arbiter, _) = Build();
        Assert.True(arbiter.TryMirror(arbiter.Issue(), LogLevel.Info));
    }

    [Fact]
    public void Details_logged_after_a_summary_in_the_same_turn_stay_in_the_log()
    {
        // Import Target: StatusText = "Imported N…", then three AddLog details.
        var (arbiter, turns) = Build();
        arbiter.Explicit();
        long a = arbiter.Issue(), b = arbiter.Issue(), c = arbiter.Issue();
        Assert.False(arbiter.TryMirror(a, LogLevel.Info));   // the posts run —
        Assert.False(arbiter.TryMirror(b, LogLevel.Info));   // still inside the
        Assert.False(arbiter.TryMirror(c, LogLevel.Info));   // summary's turn
        turns.End();
    }

    [Fact]
    public void A_line_logged_before_a_summary_does_not_replace_it_when_it_arrives()
    {
        var (arbiter, turns) = Build();
        long logged = arbiter.Issue();   // AddLog: ticket taken, copy posted
        arbiter.Explicit();              // StatusText = summary, synchronously
        Assert.False(arbiter.TryMirror(logged, LogLevel.Info));
        turns.End();
        Assert.False(arbiter.TryMirror(logged, LogLevel.Info));   // older, turn or no turn
    }

    [Fact]
    public void A_line_logged_in_a_later_turn_replaces_the_explicit_one()
    {
        // "Opening Game.locres…" set; await the parse; "Loaded 14115 rows" logged.
        var (arbiter, turns) = Build();
        arbiter.Explicit();
        turns.End();
        Assert.True(arbiter.TryMirror(arbiter.Issue(), LogLevel.Info));
    }

    [Theory]
    [InlineData(LogLevel.Warning)]
    [InlineData(LogLevel.Error)]
    public void A_problem_is_never_a_detail(LogLevel level)
    {
        // Import Target again, when nothing matched: the warning must show.
        var (arbiter, _) = Build();
        arbiter.Explicit();
        Assert.True(arbiter.TryMirror(arbiter.Issue(), level));
    }

    [Fact]
    public void An_info_detail_does_not_bury_a_problem_shown_in_the_same_turn()
    {
        var (arbiter, _) = Build();
        arbiter.Explicit();
        Assert.True(arbiter.TryMirror(arbiter.Issue(), LogLevel.Warning));
        Assert.False(arbiter.TryMirror(arbiter.Issue(), LogLevel.Info));
    }

    [Fact]
    public void A_problem_logged_before_the_summary_of_it_does_not_replace_the_summary()
    {
        // AddLog(raw exception, Error); StatusText = "Error: short".
        var (arbiter, _) = Build();
        long raw = arbiter.Issue();
        arbiter.Explicit();
        Assert.False(arbiter.TryMirror(raw, LogLevel.Error));
    }

    [Fact]
    public void Log_lines_show_in_the_order_they_were_issued()
    {
        // Two threads log; the second one's post reaches the UI thread first.
        var (arbiter, _) = Build();
        long first = arbiter.Issue(), second = arbiter.Issue();
        Assert.True(arbiter.TryMirror(second, LogLevel.Info));
        Assert.False(arbiter.TryMirror(first, LogLevel.Error));
    }

    [Fact]
    public void Only_the_newest_explicit_line_is_released_by_its_turn_ending()
    {
        // A second explicit line set before the first one's turn-end ran: the
        // stale turn-end must not strip the second one of its turn.
        var turns = new List<Action>();
        var arbiter = new StatusArbiter(turns.Add);
        arbiter.Explicit();
        arbiter.Explicit();
        turns[0]();   // the FIRST line's turn ends late
        Assert.False(arbiter.TryMirror(arbiter.Issue(), LogLevel.Info));
        turns[1]();
        Assert.True(arbiter.TryMirror(arbiter.Issue(), LogLevel.Info));
    }

    [Fact]
    public void Once_the_bar_clears_anything_may_take_it()
    {
        var (arbiter, _) = Build();
        long logged = arbiter.Issue();
        arbiter.Explicit();
        arbiter.Cleared();
        Assert.True(arbiter.TryMirror(logged, LogLevel.Info));
    }

    [Fact]
    public void Tickets_are_unique_across_threads()
    {
        var (arbiter, _) = Build();
        var tickets = new System.Collections.Concurrent.ConcurrentBag<long>();
        Parallel.For(0, 10_000, _ => tickets.Add(arbiter.Issue()));
        Assert.Equal(10_000, tickets.Distinct().Count());
    }

    /// <summary>The arbiter only helps if the log actually goes through it, with a
    /// ticket taken BEFORE the post, and if "end of turn" really comes after the
    /// posted copies. A ticket taken inside the posted lambda, a plain
    /// <c>StatusText = message</c> back in <c>AddLog</c>, or a turn-end posted at
    /// a priority that can overtake the copies would each restore the overwrite
    /// without failing any test above.</summary>
    [Fact]
    public void The_view_model_wires_the_arbiter_as_the_rule_assumes()
    {
        string source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "ViewModels", "MainWindowViewModel.cs"));

        var addLog = Regex.Match(source, @"public void AddLog\(string message[^)]*\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline);
        Assert.True(addLog.Success, "AddLog not found in MainWindowViewModel.cs");
        string body = addLog.Groups["body"].Value;
        int issue = body.IndexOf("_statusArbiter.Issue()", StringComparison.Ordinal);
        int post = body.IndexOf(".Post(", StringComparison.Ordinal);
        Assert.True(issue >= 0 && post > issue, "AddLog must take its ticket before it posts");
        Assert.DoesNotMatch(@"\.Post\([^;]*DispatcherPriority", body);   // the copy posts at the default (Normal) priority
        Assert.Contains("MirrorToStatus(message, ticket, level)", body);
        Assert.DoesNotMatch(@"StatusText\s*=", body);

        var mirror = Regex.Match(source, @"private void MirrorToStatus\([^)]*\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline);
        Assert.True(mirror.Success, "MirrorToStatus not found in MainWindowViewModel.cs");
        Assert.Contains("_statusArbiter.TryMirror(ticket, level)", mirror.Groups["body"].Value);

        var field = Regex.Match(source, @"StatusArbiter _statusArbiter = new\((?<args>.*?)\);", RegexOptions.Singleline);
        Assert.True(field.Success, "the _statusArbiter field not found");
        Assert.Contains("DispatcherPriority.Background", field.Groups["args"].Value);
    }
}
