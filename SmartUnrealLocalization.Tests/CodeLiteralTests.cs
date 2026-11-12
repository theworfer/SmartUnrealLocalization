using System.Text.RegularExpressions;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// Text the user reads must come from the language files when CODE sets it, too —
/// <c>XamlLiteralTests</c> only sees XAML. The audit of 2026-11 found English set in
/// code across the app: AI panel progress, MCP and QC log lines, the fix toast,
/// dialog and file-picker titles, and every validator's problem title, explanation
/// and fix label — the whole Problems tab — shown unchanged in all 15 languages.
/// </summary>
public class CodeLiteralTests
{
    // A string literal (plain or interpolated) that starts with a letter.
    private static readonly Regex[] LinePatterns =
    {
        new(@"\bAddLog\(\s*\$?""\p{L}"),
        new(@"\bStatusText\s*=\s*\$?""\p{L}"),
        new(@"\b(Text|Content|Header|PlaceholderText|Watermark|ToolTip)\s*=\s*\$?""\p{L}"),
        new(@"\bToastService\.Instance\.Show\w*\(\s*\$?""\p{L}"),
        new(@"\b(Title|Detail|Label|Reason)\s*=\s*\$?""\p{L}"),      // problems, fixes, dialog titles
        new(@"\b(issues|parts)\.Add\(\s*\$?""\p{L}"),               // fragments of a problem text
    };

    // A file-picker title passed on the line after the call.
    private static readonly Regex PickerTitle = new(@"Requested\?\.Invoke\(\s*\$?""\p{L}[^""]*""");

    [Fact]
    public void No_user_visible_text_is_an_English_literal_in_code()
    {
        string root = TestPaths.RepoRoot();
        var offenders = new List<string>();
        foreach (var dir in new[] { "Views", "Controls", "ViewModels", "Services" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                foreach (Match m in PickerTitle.Matches(File.ReadAllText(file)))
                    offenders.Add($"{name}: {Regex.Replace(m.Value, @"\s+", " ")}");

                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = Regex.Replace(lines[i], @"//.*$", "");
                    if (LinePatterns.Any(p => p.IsMatch(line)))
                        offenders.Add($"{name}:{i + 1}: {lines[i].Trim()}");
                }
            }
        Assert.True(offenders.Count == 0, "Use Loc.T / Loc.Plural for text the user reads:\n" + string.Join("\n", offenders));
    }
}
