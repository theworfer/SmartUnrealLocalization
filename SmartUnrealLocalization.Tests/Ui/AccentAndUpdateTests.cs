using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;
using Xunit;
using static SmartUnrealLocalization.Tests.Ui.UiSession;

namespace SmartUnrealLocalization.Tests.Ui;

/// <summary>
/// The accent colour picker (one pick → readable shades on both themes) and
/// Help → Check for updates (the answer of the releases API, without a network).
/// </summary>
[Collection("AppState")]
public class AccentAndUpdateTests
{
    private static readonly Color White = Colors.White;
    private static readonly Color DarkBackground = Color.FromRgb(0x1E, 0x1E, 0x1E);

    [Fact]
    public void Any_picked_colour_stays_readable_on_both_themes()
    {
        var picks = new List<Color> { Colors.White, Colors.Black, Colors.Yellow, Colors.Navy, Color.FromRgb(0x80, 0x80, 0x80) };
        for (int hue = 0; hue < 360; hue += 15)
            foreach (double lightness in new[] { 0.1, 0.5, 0.9 })
                picks.Add(AccentPalette.FromHsl(hue, 1, lightness));

        foreach (var pick in picks)
        {
            var dark = AccentPalette.For(pick, dark: true);
            Assert.True(AccentPalette.Contrast(White, dark.Accent) >= 2.9, $"{pick}: white text on the dark theme's accent");
            Assert.True(AccentPalette.Contrast(dark.Accent, DarkBackground) >= 3.4, $"{pick}: accent text on the dark background");

            var light = AccentPalette.For(pick, dark: false);
            Assert.True(AccentPalette.Contrast(White, light.Accent) >= 4.4, $"{pick}: white status-bar text on the light theme's accent");
        }
    }

    [Fact]
    public void A_pick_keeps_its_hue_and_the_shades_differ_per_theme()
    {
        var green = Color.FromRgb(0x2F, 0xA3, 0x6B);
        var dark = AccentPalette.For(green, dark: true);
        var light = AccentPalette.For(green, dark: false);
        AccentPalette.ToHsl(green, out double hue, out _, out _);
        AccentPalette.ToHsl(dark.Accent, out double hueDark, out _, out _);
        AccentPalette.ToHsl(light.Accent, out double hueLight, out _, out _);
        Assert.InRange(hueDark, hue - 2, hue + 2);
        Assert.InRange(hueLight, hue - 2, hue + 2);

        Assert.NotEqual(dark.Accent, light.Accent);                           // darker on the light theme
        Assert.True(AccentPalette.Luminance(dark.Hover) > AccentPalette.Luminance(dark.Accent));
        Assert.True(AccentPalette.Luminance(dark.Muted) < AccentPalette.Luminance(dark.Accent));
        Assert.True(AccentPalette.Luminance(light.Muted) > AccentPalette.Luminance(light.Accent));   // a pale tint
    }

    [Fact]
    public void The_themes_own_accents_give_the_row_colours_they_always_had()
    {
        var dark = AccentPalette.For(AccentPalette.DefaultDark, dark: true);
        Assert.Equal(AccentPalette.DefaultDark, dark.Accent);
        Assert.Equal("#203142", dark.RowSelected);
        Assert.Equal("#22415E", dark.RowSelectedHover);
        Assert.Equal("#332B8AE6", dark.RowEditing);

        var light = AccentPalette.For(AccentPalette.DefaultLight, dark: false);
        Assert.Equal(AccentPalette.DefaultLight, light.Accent);
        Assert.Equal("#EDDCCA", light.RowSelected);
        Assert.Equal("#E3CAB4", light.RowSelectedHover);
        Assert.Equal("#26A65A2A", light.RowEditing);
    }

    [Theory]
    [InlineData("#2FA36B", true)]
    [InlineData("2fa36b", true)]
    [InlineData("", false)]
    [InlineData("#12345", false)]
    [InlineData("#GGGGGG", false)]
    [InlineData("red", false)]
    public void Only_a_six_digit_hex_is_an_accent(string text, bool valid)
        => Assert.Equal(valid, AccentPalette.TryParse(text, out _));

    [Fact]
    public Task Picking_an_accent_recolours_the_app_and_Default_gives_the_theme_back()
        => WithWindowAsync(async (window, vm) =>
        {
            var app = Application.Current!;
            Color Token(string key) => ((ISolidColorBrush)app.FindResource(key)!).Color;
            try
            {
                ThemeManager.Apply(dark: true);
                Assert.Equal(AccentPalette.DefaultDark, Token("AccentColor"));

                ThemeManager.SetAccent("#2FA36B");
                await Settle();
                var dark = ThemeManager.Accent;
                Assert.Equal(dark.Accent, Token("AccentColor"));
                Assert.Equal(dark.Hover, Token("AccentHover"));
                Assert.Equal(dark.Accent, Token("GridCheckCheckedBackground"));
                Assert.Equal("#2FA36B", AppConfig.Instance.AccentColor);
                Assert.NotEqual(AccentPalette.DefaultDark, Token("AccentColor"));
                await Shot(window, vm, "accent-dark.png");

                // The same pick on the light theme: its own shade, status bar included.
                ThemeManager.Apply(dark: false);
                await Settle();
                Assert.Equal(ThemeManager.Accent.Accent, Token("AccentColor"));
                Assert.Equal(ThemeManager.Accent.Accent, Token("StatusBarBackground"));
                Assert.NotEqual(dark.Accent, ThemeManager.Accent.Accent);
                await Shot(window, vm, "accent-light.png");

                // Default: the theme's hand-tuned tokens are back, on both themes.
                ThemeManager.SetAccent(null);
                Assert.Equal(AccentPalette.DefaultLight, Token("AccentColor"));
                // Fluent's own accent (its "accent" buttons) follows the theme, not its default blue.
                Assert.Equal(AccentPalette.DefaultLight, (Color)app.FindResource("SystemAccentColor")!);
                Assert.Equal("", AppConfig.Instance.AccentColor);
                ThemeManager.Apply(dark: true);
                Assert.Equal(AccentPalette.DefaultDark, Token("AccentColor"));
                Assert.Equal(Color.Parse("#1F1F20"), Token("StatusBarBackground"));
            }
            finally
            {
                ThemeManager.SetAccent(null);
                ThemeManager.Apply(dark: true);
            }
        });

    /// <summary>Optional picture for a human to look at (SUL_SHOT_DIR), never an assertion.</summary>
    private static async Task Shot(Views.MainWindow window, ViewModels.MainWindowViewModel vm, string name)
    {
        string? dir = Environment.GetEnvironmentVariable("SUL_SHOT_DIR");
        if (dir == null) return;
        if (vm.AllRows.Count == 0)
        {
            await vm.OpenFileAsync(MakeLocres("accent.locres", 12, i => $"Line {i}"));
            vm.AllRows[1].IsSelected = true;
            vm.AllRows[2].IsSelected = true;
            vm.ShowSettingsPanel = true;
            window.SettingsPanelHost.SectionLanguage.BringIntoView();
        }
        vm.RefreshRowVisualStates();
        await Settle();
        Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)?.Save(Path.Combine(dir, name));

        // The picker itself, opened over the panel.
        var button = window.SettingsPanelHost.BtnAccent;
        button.BringIntoView();
        await Settle();
        button.Flyout!.ShowAt(button);
        await Settle();
        Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)?.Save(Path.Combine(dir, "picker-" + name));
        button.Flyout.Hide();
        await Settle();
    }

    // ── Check for updates ────────────────────────────────────────────────

    private static readonly Version Current = new(1, 0, 0);

    [Fact]
    public void A_newer_release_is_offered_with_its_own_page()
    {
        var r = UpdateChecker.Interpret(HttpStatusCode.OK,
            $"{{\"tag_name\":\"v1.2.0\",\"html_url\":\"https://github.com/{UpdateChecker.Repository}/releases/tag/v1.2.0\"}}", Current);
        Assert.Equal(UpdateChecker.Outcome.NewVersion, r.Outcome);
        Assert.Equal("1.2.0", r.Latest);
        Assert.EndsWith("/releases/tag/v1.2.0", r.Url);
    }

    [Fact]
    public void The_page_to_open_is_always_this_repository()
    {
        var r = UpdateChecker.Interpret(HttpStatusCode.OK,
            "{\"tag_name\":\"v9.0.0\",\"html_url\":\"https://example.com/malware.exe\"}", Current);
        Assert.Equal(UpdateChecker.Outcome.NewVersion, r.Outcome);
        Assert.Equal(UpdateChecker.ReleasesPage, r.Url);
    }

    [Theory]
    [InlineData("v1.0.0")]
    [InlineData("1.0")]
    [InlineData("v0.9.5")]
    public void The_same_or_an_older_release_means_up_to_date(string tag)
        => Assert.Equal(UpdateChecker.Outcome.UpToDate,
            UpdateChecker.Interpret(HttpStatusCode.OK, $"{{\"tag_name\":\"{tag}\"}}", Current).Outcome);

    [Fact]
    public void No_release_and_failures_are_told_apart()
    {
        Assert.Equal(UpdateChecker.Outcome.NoRelease, UpdateChecker.Interpret(HttpStatusCode.NotFound, "{}", Current).Outcome);
        Assert.Equal(UpdateChecker.Outcome.Failed, UpdateChecker.Interpret(HttpStatusCode.Forbidden, "rate limit", Current).Outcome);
        Assert.Equal(UpdateChecker.Outcome.Failed, UpdateChecker.Interpret(HttpStatusCode.OK, "<html>", Current).Outcome);
        Assert.Equal(UpdateChecker.Outcome.Failed, UpdateChecker.Interpret(HttpStatusCode.OK, "{\"tag_name\":\"nightly\"}", Current).Outcome);
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("V2", "2.0.0")]
    [InlineData("v1.4.0-beta.2", "1.4.0")]
    public void Release_tags_are_read_as_versions(string tag, string expected)
    {
        Assert.True(UpdateChecker.TryParseVersion(tag, out var v));
        Assert.Equal(expected, v.ToString(3));
    }
}
