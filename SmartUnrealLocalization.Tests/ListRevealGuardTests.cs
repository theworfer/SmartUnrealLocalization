using System.Text.RegularExpressions;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// No ListBox is scrolled directly — only through <c>Utils/ListReveal</c>.
///
/// <para>The 2026-09-01 crashes (two Windows records, "Invalid Arrange
/// rectangle", both through <c>OnGridSelectionChanged</c> →
/// <c>QcListBox.ScrollIntoView</c>) came from scrolling a virtualized list in
/// the same turn as inserting items above its visible part. On Avalonia 12.0.2
/// that arranges with NaN and kills the process. <c>ListReveal</c> runs an
/// ordinary layout pass first. The crash itself needs a real window — the render
/// harness reproduces it (stage <c>crashqc</c>) and shows the fix holds under a
/// 300-action stress loop; this test keeps the call sites honest.</para>
/// </summary>
public class ListRevealGuardTests
{
    /// <summary>"x.ScrollIntoView(" — captures the receiver.</summary>
    private static readonly Regex ScrollCall = new(@"(\w+)\s*\.\s*ScrollIntoView\s*\(");

    [Fact]
    public void ListBoxes_are_scrolled_only_through_ListReveal()
    {
        string root = TestPaths.RepoRoot();
        var offenders = new List<string>();

        foreach (var dir in new[] { "Views", "Controls" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            {
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string t = lines[i].TrimStart();
                    if (t.StartsWith("//") || t.StartsWith("*")) continue;
                    foreach (Match m in ScrollCall.Matches(lines[i]))
                    {
                        // The DataGrid has its own row virtualization, not the panel
                        // this is about; every DataGrid here is named …DataGrid.
                        if (m.Groups[1].Value.EndsWith("DataGrid", StringComparison.Ordinal)) continue;
                        offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {lines[i].Trim()}");
                    }
                }
            }

        Assert.True(offenders.Count == 0,
            "Scroll a ListBox through Utils.ListReveal (layout first, then scroll):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void ListReveal_lays_out_before_it_scrolls()
    {
        string source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "Utils", "ListReveal.cs"));
        foreach (Match m in Regex.Matches(source, @"public static void \w+\([^)]*\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline))
        {
            string body = m.Groups["body"].Value;
            int layout = body.IndexOf("UpdateLayout()", StringComparison.Ordinal);
            int scroll = body.IndexOf("ScrollIntoView(", StringComparison.Ordinal);
            Assert.True(layout >= 0 && scroll > layout, "every ListReveal method must call UpdateLayout() before ScrollIntoView()");
        }
    }

    [Fact]
    public void The_grid_to_panel_reveal_is_posted_not_run_inside_the_selection_change()
    {
        string source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "Views", "MainWindow.axaml.cs"));
        var handler = Regex.Match(source, @"private void OnGridSelectionChanged\([^)]*\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline);
        Assert.True(handler.Success, "OnGridSelectionChanged not found");
        string body = Regex.Replace(handler.Groups["body"].Value, @"//[^\n]*", "");   // code only
        Assert.Contains("Post(RevealSelectedRowInPanel", body);
        Assert.DoesNotContain("RevealQcEntry", body);
    }
}
