using System.Text;
using Newtonsoft.Json;
using SmartUnrealLocalization.Utils;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// A guard against colour emoji coming back.
///
/// <para>The app is an IDE-style editor and its log, its Problems list and its
/// tree read as one dense monochrome surface. A colour cartoon dropped into
/// that surface does not just look out of place — it also cannot take a
/// foreground brush, so it ignores the theme, ignores selection highlighting,
/// and (per the Avalonia/Windows pitfall in docs/ARCHITECTURE.md) re-resolves between the
/// colour and text faces on its own as styles change.</para>
///
/// <para>The vocabulary is therefore fixed and small: <see cref="Glyphs"/>. The
/// emoji-presentation selector U+FE0F is banned outright — its presence anywhere
/// in user-visible text is the single clearest signal that someone typed an
/// emoji.</para>
///
/// <para>Keeping the vocabulary's characters in the strings is only half of it.
/// None of them exists in Inter, so each goes to font fallback, and on
/// Avalonia 12.0.2 + Skia that fallback depended on process history: the SAME
/// string in the SAME stack came back as Segoe UI Emoji in one run and Segoe UI
/// Symbol in another (measured with TextLayout, 2026-09-23). U+FE0E did not
/// change the outcome. What does is <see cref="Glyphs.MonochromeFallback"/> —
/// the last three tests below hold it in place.</para>
///
/// <para>Nothing here throws at runtime and nothing looks wrong in a diff: a
/// pasted ✅ is a perfectly good string. It only shows up as a cartoon on a
/// user's screen, in one of fifteen languages, which is why this is a test and
/// not a review note.</para>
/// </summary>
public class EmojiPolicyTests
{
    /// <summary>The marks allowed to appear in user-visible text.</summary>
    private static readonly HashSet<int> Allowed = new()
    {
        0x2713, // ✓ check mark            (text-only in Unicode)
        0x2715, // ✕ multiplication x      (text-only)
        0x2717, // ✗ ballot x              (text-only)
        0x26A0, // ⚠ warning sign          (allowed only when followed by U+FE0E)
        0x2139, // ℹ information source    (allowed only when followed by U+FE0E)
    };

    private const char TextPresentation = '\uFE0E';
    private const char EmojiPresentation = '\uFE0F';

    /// <summary>
    /// True for a codepoint that Windows will render from Segoe UI Emoji — the
    /// pictograph blocks, plus the two selector characters.
    /// </summary>
    private static bool IsPictograph(int cp)
        => (cp >= 0x1F000 && cp <= 0x1FAFF)   // Misc Symbols and Pictographs .. Symbols Extended-A
        || (cp >= 0x2600 && cp <= 0x27BF)     // Misc Symbols, Dingbats
        || (cp >= 0x2B00 && cp <= 0x2BFF)     // Misc Symbols and Arrows
        || (cp >= 0x23E9 && cp <= 0x23FA)     // media controls, hourglass
        || cp == 0x2139
        || cp == EmojiPresentation;

    /// <summary>Every offending mark in <paramref name="text"/>, as "U+XXXX".</summary>
    private static List<string> Offenders(string text)
    {
        var bad = new List<string>();
        for (int i = 0; i < text.Length; i++)
        {
            int cp;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                cp = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }
            else cp = text[i];
            if (!IsPictograph(cp)) continue;

            if (cp == EmojiPresentation)
            {
                bad.Add("U+FE0F (emoji presentation selector)");
                continue;
            }
            if (!Allowed.Contains(cp))
            {
                bad.Add($"U+{cp:X4}");
                continue;
            }
            // A dual-presentation mark must carry the text selector, or Windows
            // is free to paint it as an emoji.
            if ((cp == 0x26A0 || cp == 0x2139)
                && (i + 1 >= text.Length || text[i + 1] != TextPresentation))
                bad.Add($"U+{cp:X4} without U+FE0E");
        }
        return bad;
    }

    // ── the fifteen language files ──────────────────────────────────────

    [Fact]
    public void No_language_file_carries_a_colour_emoji()
    {
        var report = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(
                     TestPaths.In("Localization"), "strings.*.json"))
        {
            var strings = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                File.ReadAllText(file)) ?? new();
            foreach (var (key, value) in strings)
            {
                var bad = Offenders(value);
                if (bad.Count > 0)
                    report.AppendLine($"{Path.GetFileName(file)} [{key}]: {string.Join(", ", bad)}");
            }
        }

        Assert.True(report.Length == 0,
            "Colour emoji in a language file. Use a mark from Utils.Glyphs, or none:\n" + report);
    }

    // ── the app's own sources ───────────────────────────────────────────

    /// <summary>
    /// The part of <paramref name="line"/> that is not a comment, carrying block
    /// state across lines in <paramref name="inBlock"/>.
    ///
    /// <para>Line-by-line matching is not enough: the interesting comments here
    /// are multi-line XAML blocks explaining which emoji a vector icon replaced,
    /// and only their FIRST line starts with the opener.</para>
    /// </summary>
    private static string CodeOnly(string line, bool xaml, ref bool inBlock)
    {
        string open = xaml ? "<!--" : "/*";
        string close = xaml ? "-->" : "*/";
        var code = new StringBuilder();
        int i = 0;

        while (i < line.Length)
        {
            if (inBlock)
            {
                int end = line.IndexOf(close, i, StringComparison.Ordinal);
                if (end < 0) return code.ToString();
                inBlock = false;
                i = end + close.Length;
                continue;
            }

            int start = line.IndexOf(open, i, StringComparison.Ordinal);
            int lineComment = xaml ? -1 : IndexOfLineComment(line, i);
            if (lineComment >= 0 && (start < 0 || lineComment < start))
            {
                code.Append(line, i, lineComment - i);
                return code.ToString();
            }
            if (start < 0)
            {
                code.Append(line, i, line.Length - i);
                return code.ToString();
            }
            code.Append(line, i, start - i);
            inBlock = true;
            i = start + open.Length;
        }
        return code.ToString();
    }

    /// <summary>Index of the first <c>//</c> outside a string literal, or -1.</summary>
    private static int IndexOfLineComment(string line, int from)
    {
        bool inString = false;
        for (int i = from; i < line.Length - 1; i++)
        {
            if (line[i] == '\\' && inString) { i++; continue; }
            if (line[i] == '"') inString = !inString;
            else if (!inString && line[i] == '/' && line[i + 1] == '/') return i;
        }
        return -1;
    }

    [Fact]
    public void No_source_file_carries_a_colour_emoji_in_user_visible_text()
    {
        string root = TestPaths.RepoRoot();
        var report = new StringBuilder();

        foreach (var dir in new[] { "Views", "Controls", "Models", "Services", "ViewModels", "Utils" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".cs") && !file.EndsWith(".axaml")) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                 || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                // Glyphs.cs is where the vocabulary is defined, and its own doc
                // comments name the characters it exists to constrain.
                if (Path.GetFileName(file) == "Glyphs.cs") continue;

                bool xaml = file.EndsWith(".axaml");
                bool inBlock = false;
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    // Comments may discuss the emoji that used to be there — that
                    // is the record of why it is gone, and it reaches no user.
                    var bad = Offenders(CodeOnly(lines[i], xaml, ref inBlock));
                    if (bad.Count > 0)
                        report.AppendLine($"{Path.GetRelativePath(root, file)}:{i + 1}: {string.Join(", ", bad)}");
                }
            }

        Assert.True(report.Length == 0,
            "Colour emoji in source. Reference Utils.Glyphs instead:\n" + report);
    }

    // ── the vocabulary itself ───────────────────────────────────────────

    [Fact]
    public void Every_glyph_in_the_vocabulary_is_text_presentation()
    {
        foreach (var mark in new[] { Glyphs.Ok, Glyphs.Fail, Glyphs.Rejected, Glyphs.Warning, Glyphs.Info })
            Assert.Empty(Offenders(mark));
    }

    [Fact]
    public void The_two_dual_presentation_marks_carry_the_text_selector()
    {
        // Not what keeps them monochrome in this app (the fallback below does),
        // but what Unicode prescribes and what any OTHER renderer of an exported
        // log will honour.
        Assert.EndsWith("\uFE0E", Glyphs.Warning);
        Assert.EndsWith("\uFE0E", Glyphs.Info);
    }

    // ── what actually keeps them monochrome ─────────────────────────────

    private static readonly string[] Vocabulary =
        { Glyphs.Ok, Glyphs.Fail, Glyphs.Rejected, Glyphs.Warning, Glyphs.Info, Glyphs.ErrorRound, Glyphs.InfoRound };

    [Fact]
    public void Every_mark_falls_inside_the_ranges_the_fallback_covers()
    {
        // A mark added outside MarkRanges would go back to the platform's
        // history-dependent fallback — Segoe UI Emoji, some of the time.
        var ranges = Avalonia.Media.UnicodeRange.Parse(Glyphs.MarkRanges);
        foreach (var mark in Vocabulary)
            Assert.True(ranges.IsInRange(char.ConvertToUtf32(mark, 0)),
                $"U+{char.ConvertToUtf32(mark, 0):X4} is not covered by Glyphs.MarkRanges");
    }

    [Fact]
    public void The_fallback_names_a_monochrome_face_for_those_ranges()
    {
        var fallback = Assert.Single(Glyphs.MonochromeFallback.FontFallbacks!);
        Assert.Equal("Segoe UI Symbol", fallback.FontFamily.Name);
        Assert.True(fallback.UnicodeRange.IsInRange(0x26A0));   // the one that turned yellow
    }

    [Fact]
    public void The_app_builder_installs_the_fallback()
    {
        // Defining the options does nothing unless the builder applies them.
        string program = File.ReadAllText(TestPaths.In("Program.cs"));
        Assert.Contains(".With(Utils.Glyphs.MonochromeFallback)", program);
    }
}
