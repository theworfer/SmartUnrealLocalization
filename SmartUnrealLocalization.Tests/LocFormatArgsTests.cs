using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// Every <c>string.Format(Loc.T("key"), …)</c> / <c>string.Format(Loc.Plural("key", n), …)</c>
/// in the app passes at least as many arguments as the sentence uses — in EVERY
/// language. A translation that adds a <c>{2}</c> the code never passes throws a
/// FormatException at run time, in that one language only, which no English test sees.
/// </summary>
public class LocFormatArgsTests
{
    private static readonly Regex Call = new(@"string\.Format\(\s*Loc\.(?<kind>T|Plural)\(\s*""(?<key>[^""]+)""", RegexOptions.Compiled);
    private static readonly Regex Placeholder = new(@"(?<!\{)\{(?<n>\d+)(?:[,:][^}]*)?\}(?!\})", RegexOptions.Compiled);

    /// <summary>Arguments of the call starting at <paramref name="open"/> (the '(' after Format), split at top level.</summary>
    private static List<string> TopLevelArgs(string code, int open)
    {
        var args = new List<string>();
        int depth = 0, start = open + 1;
        bool inString = false, verbatim = false; char quote = '\0';
        for (int i = open; i < code.Length; i++)
        {
            char c = code[i];
            if (inString)
            {
                if (!verbatim && c == '\\') { i++; continue; }
                if (c == quote)
                {
                    if (verbatim && i + 1 < code.Length && code[i + 1] == '"') { i++; continue; }
                    inString = false;
                }
                continue;
            }
            if (c == '"' || c == '\'')
            {
                inString = true; quote = c;
                verbatim = i > 0 && (code[i - 1] == '@' || (code[i - 1] == '$' && i > 1 && code[i - 2] == '@'));
                continue;
            }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0) { args.Add(code[start..i]); return args; }
            }
            else if (c == ',' && depth == 1) { args.Add(code[start..i]); start = i + 1; }
        }
        return args;
    }

    private static int MaxIndex(string text)
    {
        int max = -1;
        foreach (Match m in Placeholder.Matches(text)) max = Math.Max(max, int.Parse(m.Groups["n"].Value));
        return max;
    }

    [Fact]
    public void No_language_uses_more_placeholders_than_the_code_passes()
    {
        string root = TestPaths.RepoRoot();
        var languages = Directory.GetFiles(Path.Combine(root, "Localization"), "strings.*.json")
            .ToDictionary(f => Path.GetFileName(f)!, f => JObject.Parse(File.ReadAllText(f)));

        var problems = new List<string>();
        int calls = 0;
        foreach (var dir in new[] { "Views", "Controls", "ViewModels", "Services", "Models", "Utils" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            {
                string code = File.ReadAllText(file);
                foreach (Match m in Call.Matches(code))
                {
                    calls++;
                    int open = code.IndexOf('(', m.Index);
                    var args = TopLevelArgs(code, open);
                    int passed = args.Count - 1;          // the first argument is the format itself
                    string key = m.Groups["key"].Value;
                    bool plural = m.Groups["kind"].Value == "Plural";

                    foreach (var (lang, json) in languages)
                    {
                        var texts = plural
                            ? json.Properties().Where(p => p.Name.StartsWith(key + ".", StringComparison.Ordinal)).Select(p => (string)p.Value!)
                            : json[key] is JValue v ? new[] { (string)v! } : Array.Empty<string>();
                        foreach (var text in texts)
                        {
                            int max = MaxIndex(text);
                            if (max >= passed)
                                problems.Add($"{Path.GetFileName(file)}: {key} in {lang} uses {{{max}}} but the code passes {passed} argument(s)");
                            // A brace that is neither {n} nor doubled ("{name}" as an example
                            // in a sentence) is a FormatException too.
                            string stripped = Placeholder.Replace(text.Replace("{{", "").Replace("}}", ""), "");
                            if (stripped.Contains('{') || stripped.Contains('}'))
                                problems.Add($"{Path.GetFileName(file)}: {key} in {lang} has a literal brace string.Format cannot parse: {text}");
                        }
                    }
                }
            }

        Assert.True(calls > 100, $"the scan found only {calls} calls — the pattern no longer matches the code");
        Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct()));
    }

    [Fact]
    public void Every_formatted_key_exists_in_English()
    {
        string root = TestPaths.RepoRoot();
        var en = JObject.Parse(File.ReadAllText(Path.Combine(root, "Localization", "strings.en.json")));
        var missing = new List<string>();
        foreach (var dir in new[] { "Views", "Controls", "ViewModels", "Services", "Models", "Utils" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
                foreach (Match m in Call.Matches(File.ReadAllText(file)))
                {
                    string key = m.Groups["key"].Value;
                    bool found = m.Groups["kind"].Value == "Plural"
                        ? en.Properties().Any(p => p.Name.StartsWith(key + ".", StringComparison.Ordinal))
                        : en[key] != null;
                    if (!found) missing.Add($"{Path.GetFileName(file)}: {key}");
                }
        Assert.True(missing.Count == 0, "Keys used in code but missing from strings.en.json:\n" + string.Join("\n", missing.Distinct()));
    }
}
