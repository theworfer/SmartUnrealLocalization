using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// User-visible text in XAML must come from the language files.
///
/// <para><c>LocalizationTests</c> checks the KEYS: that every key the XAML binds
/// exists in all fifteen languages. It cannot see text that never became a key.
/// The placeholder in every empty Target cell — "— untranslated —", the most
/// repeated string on screen in a fresh project — was a literal in
/// <c>MainWindow.axaml</c>, English in all fifteen languages; so was the whole
/// About window, and the hours unit in the Status panel. The 2026-09-23 render
/// pass found the first; this guard's first run found the other two.</para>
///
/// <para>Glyphs pass (they carry no letters). Anything else with letters must be
/// on the list below, with the reason it stays as it is.</para>
/// </summary>
public class XamlLiteralTests
{
    private static readonly Dictionary<string, string> Allowed = new()
    {
        ["S"] = "the logo mark",
        ["SUL"] = "the product's short name in the logo",
        ["Smart Unreal Localization"] = "the product name",
        ["QC"] = "glossary identifier, English in every language",
        ["KEY"] = "column name — glossary identifier",
        ["SOURCE"] = "column name — glossary identifier",
        ["TARGET"] = "column name — glossary identifier",
        ["Ctrl+O"] = "keyboard shortcut, verbatim in every language",
        ["Aa"] = "the match-case toggle's typographic glyph",
        ["ab"] = "the whole-word toggle's typographic glyph",
        [".csv"] = "file format name",
        [".po (gettext)"] = "file format name",
        [".locres (UE binary)"] = "file format name",
    };

    private static readonly Regex TextAttribute = new(
        @"\b(Text|Content|Header|ToolTip\.Tip|Watermark|PlaceholderText|Title|Label)=""([^""]*)""");

    [Fact]
    public void No_user_visible_literal_escapes_the_language_files()
    {
        string root = TestPaths.RepoRoot();
        var offenders = new List<string>();

        foreach (var dir in new[] { "Views", "Controls" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.axaml", SearchOption.AllDirectories))
            {
                string xaml = File.ReadAllText(file);
                // Comments describe the UI; they are not the UI.
                string code = Regex.Replace(xaml, "<!--.*?-->", m => new string('\n', m.Value.Count(c => c == '\n')),
                                            RegexOptions.Singleline);

                foreach (Match m in TextAttribute.Matches(code))
                {
                    string raw = m.Groups[2].Value;
                    if (raw.StartsWith("{")) continue;                 // a binding or markup extension
                    string text = WebUtility.HtmlDecode(raw);
                    if (!text.Any(char.IsLetter)) continue;            // a glyph, a number, punctuation
                    if (Allowed.ContainsKey(text)) continue;

                    int line = code[..m.Index].Count(c => c == '\n') + 1;
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{line}: {m.Groups[1].Value}=\"{text}\"");
                }
            }

        Assert.True(offenders.Count == 0,
            "Literal user-visible text in XAML — bind it to a key in Localization/strings.*.json " +
            "(or, if it is truly an identifier, add it to Allowed with the reason):\n" + string.Join("\n", offenders));
    }
}
