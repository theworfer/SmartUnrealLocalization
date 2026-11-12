using System.Text.RegularExpressions;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// Inline Runs of one TextBlock must not be separated by a line break.
///
/// <para>XAML turns the whitespace between two inline elements into a single
/// space, and a line break plus indentation is whitespace. The status bar laid
/// its labels out one Run per line AND put an explicit <c>&lt;Run Text=" "/&gt;</c>
/// between them, so every label rendered with three spaces — "Selected:   0",
/// "Showing   0   of   14115" — and the "(N shown)" hint as "( N   shown )"
/// (2026-09-23 render pass; five TextBlocks in the status bar and the Status
/// panel). Write the Runs of one label on one line, tag against tag, and put
/// every space you want in a Run of its own.</para>
/// </summary>
public class XamlRunSpacingTests
{
    private static readonly Regex TextBlock = new(
        @"<TextBlock\b[^>]*?(?<!/)>(?<inner>.*?)</TextBlock>", RegexOptions.Singleline);

    /// <summary>The end of one Run, a line break, the start of the next.</summary>
    private static readonly Regex SplitRuns = new(@"(?:/>|</Run>)[ \t]*\r?\n\s*<Run\b");

    [Fact]
    public void No_TextBlock_splits_its_Runs_across_lines()
    {
        string root = TestPaths.RepoRoot();
        var offenders = new List<string>();

        foreach (var dir in new[] { "Views", "Controls" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.axaml", SearchOption.AllDirectories))
            {
                string xaml = File.ReadAllText(file);
                string code = Regex.Replace(xaml, "<!--.*?-->", m => new string('\n', m.Value.Count(c => c == '\n')),
                                            RegexOptions.Singleline);
                foreach (Match tb in TextBlock.Matches(code))
                {
                    if (!SplitRuns.IsMatch(tb.Groups["inner"].Value)) continue;
                    int line = code.Take(tb.Index).Count(c => c == '\n') + 1;
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{line}");
                }
            }

        Assert.True(offenders.Count == 0,
            "TextBlock with Runs on separate lines — each line break renders as an extra space:\n" +
            string.Join("\n", offenders));
    }
}
