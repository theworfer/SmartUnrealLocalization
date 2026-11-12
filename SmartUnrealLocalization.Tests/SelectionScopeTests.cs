using System.Text.RegularExpressions;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// A guard against a class of bug, not a single one: "what is selected?" having
/// more than one answer.
///
/// <para>Selection lives on <c>TranslationRow.IsSelected</c>, and a filter
/// changes what is VISIBLE, never what is selected. So there is exactly one
/// correct answer — every checked row in the file — and one place that gives
/// it: <c>MainWindowViewModel.SelectedRows</c>.</para>
///
/// <para>Three answers used to coexist, and each cost the user something real:
/// <list type="bullet">
/// <item>the status bar counted only VISIBLE checked rows, so with a filter on
/// it could read "Selected: 0" while QC, the clipboard and MCP were acting on
/// two hundred;</item>
/// <item>the AI Translate panel used a SNAPSHOT taken when it opened, and an
/// empty snapshot fell through to the ELSE branch — checking rows after opening
/// the panel and pressing Start translated the WHOLE FILE through a paid API;</item>
/// <item>two view handlers used the visible collection while the commands they
/// fed used the whole file.</item>
/// </list></para>
///
/// <para>None of that throws. Each variant is a plausible number acting on a
/// plausible set, which is why it survived so long — so the guard is structural:
/// it reads the sources and fails if any consumer re-derives the selection
/// instead of asking for it.</para>
/// </summary>
public class SelectionScopeTests
{
    private static string RepoRoot() => TestPaths.RepoRoot();

    private static IEnumerable<(string Path, string Text)> SourceFiles()
    {
        string root = RepoRoot();
        foreach (var dir in new[] { "ViewModels", "Views", "Controls", "Services" })
            foreach (var f in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            {
                if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                 || f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                yield return (Path.GetRelativePath(root, f), File.ReadAllText(f));
            }
    }

    /// <summary>
    /// The one place allowed to walk the rows looking for checkboxes: the
    /// accessor itself, the counters that feed the status bar, and the
    /// filter/selection primitives that necessarily operate on positions.
    /// </summary>
    private static readonly string[] AllowedFiles =
    {
        Path.Combine("ViewModels", "MainWindowViewModel.cs"),
    };

    [Fact]
    public void No_consumer_re_derives_the_selection_from_a_collection_of_its_own()
    {
        // "AllRows.Where(r => r.IsSelected)" and "Rows.Where(r => r.IsSelected)"
        // are the two shapes that produced the wrong answers. Both must go
        // through SelectedRows now.
        var rogue = new Regex(@"\b(?:_?[A-Za-z]*\.)?(?:All)?Rows\s*\.\s*(?:Where|Count|Any)\s*\(\s*\w+\s*=>\s*\w+\.IsSelected",
                              RegexOptions.Compiled);
        var hits = new List<string>();

        foreach (var (path, text) in SourceFiles())
        {
            if (AllowedFiles.Contains(path)) continue;
            foreach (Match m in rogue.Matches(text))
            {
                int line = text.Take(m.Index).Count(c => c == '\n') + 1;
                hits.Add($"{path}:{line}  {m.Value.Trim()}");
            }
        }

        Assert.True(hits.Count == 0,
            "These re-derive the selection instead of using MainWindowViewModel.SelectedRows. "
          + "A second answer to \"what is selected\" is how the status bar came to disagree with "
          + "QC, and how the AI panel came to translate the whole file:\n  "
          + string.Join("\n  ", hits));
    }

    [Fact]
    public void The_status_bar_reports_the_whole_file_not_the_visible_rows()
    {
        string vm = File.ReadAllText(Path.Combine(RepoRoot(), "ViewModels", "MainWindowViewModel.cs"));

        // SelectedCount is what the status bar binds to; it must count _allRows.
        Assert.Matches(@"SelectedCount\s*=\s*_allRows\.Count\(r => r\.IsSelected\)", vm);
        // The visible tally exists too, but only for the tri-state Select-All box.
        Assert.Matches(@"SelectedVisibleCount\s*=\s*Rows\.Count\(r => r\.IsSelected\)", vm);

        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.axaml"));
        Assert.Contains("{Binding SelectedCount}", xaml);
        // ...and it says so when part of the selection is hidden by a filter.
        Assert.Contains("HasSelectionOutsideFilter", xaml);
    }

    [Fact]
    public void The_ai_panel_holds_no_selection_snapshot()
    {
        // The reported bug: open the panel, THEN check rows, press Start with
        // scope "Selected" — the snapshot was empty and the run silently became
        // "All rows". A snapshot field is the thing that made that possible.
        string panel = File.ReadAllText(Path.Combine(RepoRoot(), "Controls", "AiTranslatePanel.axaml.cs"));
        Assert.DoesNotContain("_selectedRows", panel);
        Assert.Contains("_vm.SelectedRows", panel);
    }

    [Fact]
    public void An_empty_selected_scope_refuses_instead_of_widening()
    {
        // The property that actually protects the user's API budget: when the
        // scope resolves to nothing, Start must return — never fall through to
        // the whole file.
        string panel = File.ReadAllText(Path.Combine(RepoRoot(), "Controls", "AiTranslatePanel.axaml.cs"));
        var m = Regex.Match(panel,
            @"if \(RbSelected\.IsChecked == true\)\s*\{(?<body>.*?)\n            \}",
            RegexOptions.Singleline);
        Assert.True(m.Success, "The 'Selected' scope branch was restructured — re-check this guard.");

        string body = m.Groups["body"].Value;
        Assert.Contains("rowsToProcess = _vm.SelectedRows", body);
        Assert.Contains("Count == 0", body);
        Assert.Contains("return;", body);
    }

    [Fact]
    public void The_panel_shows_how_many_rows_selected_means()
    {
        // A scope that costs money has to be readable BEFORE Start, and it has
        // to stay right while the panel is open — hence the live refresh.
        string panel = File.ReadAllText(Path.Combine(RepoRoot(), "Controls", "AiTranslatePanel.axaml.cs"));
        Assert.Contains("RefreshSelectedScopeLabel", panel);
        Assert.Contains("nameof(MainWindowViewModel.SelectedCount)", panel);
    }

    [Fact]
    public void A_command_with_its_own_scope_makes_it_the_real_selection()
    {
        // Right-click → AI Translate used to hand the panel a hidden scope. It
        // now checks exactly those rows, so the grid, the status bar and the
        // panel cannot disagree about what Start will run.
        string view = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.axaml.cs"));
        Assert.Matches(@"VM\.SetSelection\(VM\.GetContextScope\(row\)\)", view);
        Assert.DoesNotContain("Initialize(VM, scope)", view);
    }
}
