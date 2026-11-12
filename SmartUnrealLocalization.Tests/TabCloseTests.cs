using System.Text.RegularExpressions;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// Closing a tab must load a never-opened neighbour, and must never close the
/// other tabs (audit 2026-09 §8, 2026-10-19).
///
/// <para>Closing the active tab restored its neighbour directly. A neighbour
/// restored from the last session but never opened has no rows, so it showed
/// as an EMPTY file and was marked active — clicking it did not load it either.
/// The first fix loaded it after <c>CloseCurrentFile()</c>, which drops EVERY
/// tab; the render harness (stage <c>lazytab</c>) caught that before it
/// shipped. The failed-load path had the same trap: with no previous tab it
/// closed everything, unsaved edits of other tabs included. The view model
/// needs a window to run, so these read its source; the harness runs the real
/// sequences.</para>
/// </summary>
public class TabCloseTests
{
    private static string Method(string source, string signature)
    {
        var m = Regex.Match(source, Regex.Escape(signature) + @"[^\n]*\n\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline);
        Assert.True(m.Success, signature + " not found");
        return Regex.Replace(m.Groups["body"].Value, @"//[^\n]*", "");   // code only
    }

    private static string Tabs() => File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "ViewModels", "MainWindowViewModel.Tabs.cs"));

    [Fact]
    public void Closing_a_tab_loads_a_lazy_neighbour_and_never_closes_the_others()
    {
        string close = Method(Tabs(), "public async Task CloseTab(");
        Assert.Contains("next.IsLoaded", close);
        Assert.Contains("await LoadIntoSessionAsync(next)", close);
        // CloseCurrentFile drops every tab: only allowed when NO tab is left.
        int closeAll = close.IndexOf("CloseCurrentFile()", StringComparison.Ordinal);
        Assert.True(closeAll < 0 || close.LastIndexOf("else", closeAll, StringComparison.Ordinal) > close.IndexOf("OpenSessions.Count > 0", StringComparison.Ordinal),
            "CloseTab may call CloseCurrentFile only in the no-tabs-left branch");
        Assert.Single(Regex.Matches(close, @"CloseCurrentFile\(\)"));
    }

    [Fact]
    public void A_failed_lazy_load_falls_back_to_a_loaded_tab_before_closing_anything()
    {
        string load = Method(Tabs(), "private async Task LoadIntoSessionAsync(");
        int fallback = load.IndexOf("OpenSessions.FirstOrDefault(x => x.IsLoaded)", StringComparison.Ordinal);
        int closeAll = load.IndexOf("CloseCurrentFile()", StringComparison.Ordinal);
        Assert.True(fallback >= 0, "the failure path must look for a loaded tab");
        Assert.True(closeAll > fallback, "CloseCurrentFile only after no loaded tab was found");
    }
}
