using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services
{
    /// <summary>
    /// Runtime theme switcher. Swaps the merged token dictionary and the
    /// requested ThemeVariant so DynamicResource bindings re-evaluate live.
    /// </summary>
    public static class ThemeManager
    {
        private const string AssemblyUri = "avares://SmartUnrealLocalization/";
        private const string DarkTokensUri = "avares://SmartUnrealLocalization/Themes/Tokens.Dark.axaml";
        private const string LightTokensUri = "avares://SmartUnrealLocalization/Themes/Tokens.Light.axaml";

        public static event Action? ThemeChanged;
        public static bool IsDark { get; private set; } = true;

        public static void Apply(bool dark)
        {
            var app = Application.Current;
            if (app == null) return;

            // Remove any previously injected tokens dictionaries
            var existing = app.Resources.MergedDictionaries
                .OfType<ResourceInclude>()
                .Where(r => r.Source != null && r.Source.OriginalString.Contains("/Themes/Tokens."))
                .ToList();
            foreach (var d in existing)
                app.Resources.MergedDictionaries.Remove(d);

            // Add new tokens dictionary
            var tokens = new ResourceInclude(new Uri(AssemblyUri))
            {
                Source = new Uri(dark ? DarkTokensUri : LightTokensUri)
            };
            app.Resources.MergedDictionaries.Add(tokens);

            // Sync FluentTheme variant so built-in control templates match
            app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;

            IsDark = dark;
            ApplyAccent(app, dark);
            AppConfig.Instance.IsDarkTheme = dark;
            AppConfig.Instance.Save();

            ThemeChanged?.Invoke();
        }

        public static void Toggle() => Apply(!IsDark);

        // ── Accent colour ────────────────────────────────────────────────────

        /// <summary>The accent shades in effect — the theme's own or the user's pick.
        /// Code that paints with the accent (row backgrounds, find hits) reads it here.</summary>
        public static AccentShades Accent { get; private set; } = AccentPalette.For(AccentPalette.DefaultDark, dark: true);

        /// <summary>The picked accent as "#RRGGBB", or "" for the theme's own.</summary>
        public static string CustomAccent =>
            AccentPalette.TryParse(AppConfig.Instance.AccentColor, out var c) ? AccentPalette.ToHex(c) : "";

        // Token keys a custom accent overrides. They are set on the application's
        // own resources, which win over the merged token dictionary; removing them
        // lets the theme's hand-tuned tokens show again.
        private static readonly string[] AccentKeys =
        {
            "AccentColor", "AccentHover", "AccentMuted", "AccentSoft", "AccentSoft2",
            "SelectionBackground", "SelectionStrong",
            "GridCheckCheckedBackground", "GridCheckCheckedBorder", "EditorGlow",
            "StatusBarBackground", "StatusBarGradient",
            // FluentTheme's own accent (check boxes, sliders, focus, text selection).
            "SystemAccentColor", "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
            "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
        };

        /// <summary>
        /// Pick an accent colour ("#RRGGBB"), or pass null / "" to return to the
        /// theme's own. Applies at once on the current theme and is remembered; the
        /// other theme derives its own shades from the same pick when it is shown.
        /// </summary>
        public static void SetAccent(string? hex, bool persist = true)
        {
            AppConfig.Instance.AccentColor = AccentPalette.TryParse(hex, out var c) ? AccentPalette.ToHex(c) : "";
            // A slider drag calls this on every step; it saves once, when the picker closes.
            if (persist) AppConfig.Instance.Save();
            var app = Application.Current;
            if (app == null) return;
            ApplyAccent(app, IsDark);
            ThemeChanged?.Invoke();
        }

        private static void ApplyAccent(Application app, bool dark)
        {
            bool custom = AccentPalette.TryParse(AppConfig.Instance.AccentColor, out var picked);
            var a = AccentPalette.For(custom ? picked : (dark ? AccentPalette.DefaultDark : AccentPalette.DefaultLight), dark);
            Accent = a;

            foreach (var key in AccentKeys) app.Resources.Remove(key);

            // The built-in controls (Fluent's "accent" buttons, check boxes, sliders) take
            // their accent from these seven colours — always the theme's own accent,
            // or the light theme's buttons stayed Fluent's default blue.
            AccentPalette.ToHsl(a.Accent, out double h, out double s, out double l);
            app.Resources["SystemAccentColor"] = a.Accent;
            for (int i = 1; i <= 3; i++)
            {
                app.Resources["SystemAccentColorDark" + i] = AccentPalette.FromHsl(h, s, Math.Max(0, l - 0.08 * i));
                app.Resources["SystemAccentColorLight" + i] = AccentPalette.FromHsl(h, s, Math.Min(1, l + 0.08 * i));
            }
            if (!custom) return;

            app.Resources["AccentColor"] = new Avalonia.Media.SolidColorBrush(a.Accent);
            app.Resources["AccentHover"] = new Avalonia.Media.SolidColorBrush(a.Hover);
            app.Resources["AccentMuted"] = new Avalonia.Media.SolidColorBrush(a.Muted);
            app.Resources["AccentSoft"] = new Avalonia.Media.SolidColorBrush(a.Soft);
            app.Resources["AccentSoft2"] = new Avalonia.Media.SolidColorBrush(a.Soft2);
            app.Resources["SelectionBackground"] = new Avalonia.Media.SolidColorBrush(a.Accent, a.SelectionOpacity);
            app.Resources["SelectionStrong"] = new Avalonia.Media.SolidColorBrush(a.Accent, a.SelectionStrongOpacity);
            app.Resources["GridCheckCheckedBackground"] = new Avalonia.Media.SolidColorBrush(a.Accent);
            app.Resources["GridCheckCheckedBorder"] = new Avalonia.Media.SolidColorBrush(a.Accent);
            app.Resources["EditorGlow"] = new Avalonia.Media.BoxShadows(new Avalonia.Media.BoxShadow { Spread = 3, Color = a.Soft });


            // Only the light theme paints its status bar in the accent.
            if (!dark)
            {
                app.Resources["StatusBarBackground"] = new Avalonia.Media.SolidColorBrush(a.Accent);
                app.Resources["StatusBarGradient"] = new Avalonia.Media.LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new Avalonia.Media.GradientStop(a.StatusBarTop, 0),
                        new Avalonia.Media.GradientStop(a.Accent, 1),
                    },
                };
            }
        }

        /// <summary>
        /// Publish the current language's font stacks as application resources so
        /// every style picks them up through DynamicResource. Called after the
        /// language loads and again on every switch.
        ///
        /// <para>The stacks live in the language file, not here — see
        /// <see cref="Localization.Loc.FontFamily"/>. Chinese and Japanese are the
        /// reason: without an explicit face Windows resolves their glyphs to
        /// SimSun / MS Mincho, print serifs that look nothing like the rest of
        /// this UI.</para>
        /// </summary>
        // One FontFamily per distinct stack, reused forever. Most language
        // switches do not change the font at all — every Latin and Cyrillic file
        // names the same stack — but a fresh FontFamily instance holding the
        // same string still reads as a change to every DynamicResource that
        // consumes it, so the whole window re-resolved its typefaces for
        // nothing. Cheap to avoid now that switching is a button in the header
        // rather than a setting changed once.
        private static readonly Dictionary<string, Avalonia.Media.FontFamily> _fonts = new();

        private static Avalonia.Media.FontFamily Font(string stack)
        {
            if (!_fonts.TryGetValue(stack, out var family))
                _fonts[stack] = family = new Avalonia.Media.FontFamily(stack);
            return family;
        }

        public static void ApplyFonts()
        {
            var app = Application.Current;
            if (app == null) return;
            var loc = Localization.Loc.Instance;
            Set(app, "AppFontFamily", Font(loc.FontFamily));
            Set(app, "AppFontFamilyMono", Font(loc.FontFamilyMono));

            // Most language switches do not change the font at all (every Latin
            // and Cyrillic file names the same stack). Assigning the same value
            // back would still invalidate every DynamicResource that reads it.
            static void Set(Application app, string key, Avalonia.Media.FontFamily value)
            {
                if (!app.Resources.TryGetValue(key, out var current) || !ReferenceEquals(current, value))
                    app.Resources[key] = value;
            }
        }
    }
}
