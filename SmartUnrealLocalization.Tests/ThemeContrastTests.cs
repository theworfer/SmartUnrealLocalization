using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// The status bar must be readable in both themes, measured rather than eyeballed.
///
/// <para>In the light theme the bar is a saturated orange, and the severity marks
/// on it — coloured with the same Danger / Warning / Info tokens as everywhere
/// else — measured 1.04–1.27 : 1 against it: the red "errors" mark was the bar's
/// own colour. The white status text measured 4.07 : 1, under the 4.5 : 1 WCAG AA
/// asks for text this size, and a 95 % opacity on the text style dimmed it
/// further. Nobody saw it on the dark theme, where every number passes easily
/// (found by the 2026-09-23 render pass).</para>
///
/// <para>This reads the token dictionaries directly, so a later colour tweak that
/// breaks legibility fails here instead of on a user's screen.</para>
/// </summary>
public class ThemeContrastTests
{
    private const double TextAA = 4.5;       // WCAG 2.x AA, normal-size text
    private const double NonTextAA = 3.0;    // WCAG 2.x AA, meaningful non-text marks

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static IEnumerable<object[]> Themes() => new[]
    {
        new object[] { "Tokens.Dark.axaml" },
        new object[] { "Tokens.Light.axaml" },
    };

    [Theory]
    [MemberData(nameof(Themes))]
    public void Status_bar_text_meets_AA_against_every_stop_of_the_bar(string file)
    {
        var (brushes, bar) = Load(file);
        foreach (var stop in bar)
        {
            double ratio = Contrast(brushes["TextOnAccent"], stop);
            Assert.True(ratio >= TextAA,
                $"{file}: TextOnAccent on status bar stop {stop} is {ratio:F2}:1, needs {TextAA}:1");
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void Status_bar_severity_marks_are_visible_against_every_stop_of_the_bar(string file)
    {
        var (brushes, bar) = Load(file);
        foreach (var mark in new[] { "StatusBarErrorMark", "StatusBarWarningMark", "StatusBarInfoMark" })
            foreach (var stop in bar)
            {
                Assert.True(brushes.ContainsKey(mark), $"{file}: {mark} is missing");
                double ratio = Contrast(brushes[mark], stop);
                Assert.True(ratio >= NonTextAA,
                    $"{file}: {mark} {brushes[mark]} on status bar stop {stop} is {ratio:F2}:1, needs {NonTextAA}:1");
            }
    }

    [Fact]
    public void The_status_text_style_does_not_dim_its_text()
    {
        // The tokens can pass and the style can still undo it: an Opacity on
        // TextBlock.statusItem multiplies straight into the contrast.
        string theme = File.ReadAllText(TestPaths.In("Themes", "AppTheme.axaml"));
        var style = Regex.Match(theme, @"<Style Selector=""TextBlock\.statusItem"">(.*?)</Style>", RegexOptions.Singleline);
        Assert.True(style.Success, "TextBlock.statusItem style not found");
        Assert.DoesNotContain("Opacity", style.Groups[1].Value);
    }

    // ── reading the tokens ──────────────────────────────────────────────

    private static (Dictionary<string, string> Brushes, List<string> Bar) Load(string file)
    {
        var doc = XDocument.Load(TestPaths.In("Themes", file));
        var brushes = doc.Descendants()
            .Where(e => e.Name.LocalName == "SolidColorBrush" && e.Attribute(X + "Key") != null)
            .ToDictionary(e => e.Attribute(X + "Key")!.Value, e => e.Attribute("Color")!.Value);

        var gradient = doc.Descendants()
            .Single(e => e.Name.LocalName == "LinearGradientBrush" && e.Attribute(X + "Key")?.Value == "StatusBarGradient");
        var stops = gradient.Descendants()
            .Where(e => e.Name.LocalName == "GradientStop")
            .Select(e => e.Attribute("Color")!.Value)
            .ToList();
        Assert.NotEmpty(stops);
        return (brushes, stops);
    }

    // ── WCAG 2.x relative luminance and contrast ratio ──────────────────

    private static double Contrast(string a, string b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        string h = hex.TrimStart('#');
        Assert.True(h.Length == 6, $"{hex}: only opaque #RRGGBB colours can be measured here");
        double Channel(int i)
        {
            double c = int.Parse(h.Substring(i, 2), NumberStyles.HexNumber) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }
}
