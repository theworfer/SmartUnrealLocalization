using System.Text.RegularExpressions;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// An exception that leaves an <c>async void</c> handler is rethrown on the UI
/// thread and ends the process — a failed button (a locked Translation Memory file,
/// a folder that went away) used to close the app with unsaved work. 23 handlers had
/// no catch at all (2026-11-10). Every one now has a try, and a new one must too.
/// </summary>
public class AsyncVoidGuardTests
{
    private static readonly Regex Handler =
        new(@"async void (?<name>\w+)\([^)]*\)\s*(?<kind>=>|\{)", RegexOptions.Compiled);

    [Fact]
    public void Every_async_void_handler_catches_its_own_exceptions()
    {
        string root = TestPaths.RepoRoot();
        var offenders = new List<string>();
        foreach (var dir in new[] { "Views", "Controls", "ViewModels", "Services" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                foreach (Match m in Handler.Matches(text))
                {
                    string body;
                    if (m.Groups["kind"].Value == "=>")
                    {
                        body = text.Substring(m.Index, text.IndexOf(';', m.Index) - m.Index);
                        // An expression body cannot hold a try; the one shared guarded runner is allowed.
                        if (body.Contains("RunVMOperation")) continue;
                    }
                    else
                    {
                        int open = text.IndexOf('{', m.Index + m.Length - 1), depth = 0, i = open;
                        do { if (text[i] == '{') depth++; else if (text[i] == '}') depth--; i++; }
                        while (depth > 0 && i < text.Length);
                        body = text.Substring(open, i - open);
                    }
                    if (!Regex.IsMatch(body, @"\btry\b"))
                        offenders.Add($"{Path.GetFileName(file)}: {m.Groups["name"].Value}");
                }
            }
        Assert.True(offenders.Count == 0,
            "async void without a try (end it with catch → Services.HandlerGuard.Report):\n" + string.Join("\n", offenders));
    }
}
