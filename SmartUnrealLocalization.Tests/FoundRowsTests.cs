using System.Text.RegularExpressions;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// "Found rows" shows every matching row — the navigation cap is not a filter
/// (audit 2026-09 §4). The filter's row set used to be derived from the list Find
/// Next steps through, which stops at 5 000 hits, so a query matching 5 001 rows
/// filtered to 5 000 and the rest of the file silently disappeared. The render
/// harness (stage <c>foundrows</c>) checks a real file: 31 795 matching rows,
/// 31 795 shown. The scan lives in the view model, so these read its source.
/// </summary>
public class FoundRowsTests
{
    private static string FindBar()
        => File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "ViewModels", "MainWindowViewModel.FindBar.cs"));

    [Fact]
    public void The_found_rows_set_is_not_derived_from_the_capped_match_list()
    {
        var refresh = Regex.Match(FindBar(), @"private void RefreshFoundFilter\(\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline);
        Assert.True(refresh.Success, "RefreshFoundFilter not found");
        Assert.DoesNotContain("_findMatches", refresh.Groups["body"].Value);
    }

    [Fact]
    public void The_scan_records_every_matching_row_before_the_cap_applies()
    {
        string source = FindBar();
        int add = source.IndexOf("_rowsWithFindMatch.Add(row)", StringComparison.Ordinal);
        int navigate = source.IndexOf("Navigate(targetHits", StringComparison.Ordinal);
        Assert.True(add >= 0, "the scan must add matching rows itself");
        Assert.True(add < navigate, "…before the capped navigation list is filled");
    }
}
