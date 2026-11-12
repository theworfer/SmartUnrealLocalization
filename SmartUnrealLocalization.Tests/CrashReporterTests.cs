using SmartUnrealLocalization.Services;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// A crash must leave a report the user is told about, and must never leak
/// what the user would not want in a file they might send someone.
///
/// <para>Before 2026-10-19 the 2026-09-01 crashes left only a Windows event,
/// found by an audit a week later, and the unsaved edits were gone. The fatal
/// path itself (rescue on the UI thread, then terminate) is exercised by the
/// render harness, stage <c>crashreal</c>, which crashes a real window.</para>
/// </summary>
public class CrashReporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sul-crash-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static Exception Thrown()
    {
        try { throw new InvalidOperationException("outer", new ArgumentException("inner cause")); }
        catch (Exception ex) { return ex; }
    }

    [Fact]
    public void The_report_carries_what_a_diagnosis_needs()
    {
        string text = CrashReporter.BuildReport("crash", Thrown(), "Open tabs: 2",
            new[] { @"C:\AutoSaved\Recovered_x\Game.locres" }, null, new[] { "[12:00:00] Info: opened" });

        Assert.Contains("Kind: crash", text);
        Assert.Contains("Version: ", text);
        Assert.Contains("Recovered: C:\\AutoSaved\\Recovered_x\\Game.locres", text);
        Assert.Contains("Open tabs: 2", text);
        Assert.Contains("[12:00:00] Info: opened", text);
        Assert.Contains("InvalidOperationException: outer", text);
        Assert.Contains("inner cause", text);           // the whole chain, not just the top
        Assert.Contains(nameof(Thrown), text);          // with the stack
    }

    [Fact]
    public void Each_report_is_announced_once_with_its_recovered_files()
    {
        CrashReporter.TryWrite(_dir, "crash", CrashReporter.BuildReport("crash", Thrown(), "", new[] { @"D:\r\a.locres", @"D:\r\b.csv" }, null, Array.Empty<string>()));
        CrashReporter.TryWrite(_dir, "hang", CrashReporter.BuildReport("hang", null, "", Array.Empty<string>(), null, Array.Empty<string>()));
        CrashReporter.TryWrite(_dir, "task", CrashReporter.BuildReport("unobserved task exception", Thrown(), "", Array.Empty<string>(), null, Array.Empty<string>()));

        var first = CrashReporter.TakeUnseenReports(_dir);
        Assert.Equal(new[] { "crash", "hang" }, first.Select(r => r.Kind).OrderBy(k => k));
        Assert.Equal(new[] { @"D:\r\a.locres", @"D:\r\b.csv" }, first.Single(r => r.Kind == "crash").Recovered);
        Assert.Empty(CrashReporter.TakeUnseenReports(_dir));
    }

    [Fact]
    public void A_missing_folder_is_not_an_error()
        => Assert.Empty(CrashReporter.TakeUnseenReports(Path.Combine(_dir, "never-created")));

    [Fact]
    public void Nothing_that_feeds_a_report_can_reach_the_API_key()
    {
        string root = TestPaths.RepoRoot();
        foreach (var file in new[] { Path.Combine("Services", "CrashReporter.cs"),
                                     Path.Combine("ViewModels", "MainWindowViewModel.CrashSupport.cs") })
        {
            string source = File.ReadAllText(Path.Combine(root, file));
            Assert.DoesNotContain("ApiKey", source);
        }
    }

    [Fact]
    public void The_process_still_dies_after_a_fatal_error()
    {
        // Keeping a window alive after an unknown failure is how a damaged state
        // gets saved over good work: the reporter observes, it never swallows.
        string source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "Services", "CrashReporter.cs"));
        Assert.DoesNotContain("Handled = true", source);
        Assert.DoesNotContain("UIThread.UnhandledException", source);
    }
}
