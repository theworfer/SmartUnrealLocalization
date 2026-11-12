using System;
using System.Globalization;
using Avalonia.Media;

namespace SmartUnrealLocalization.Services
{
    /// <summary>
    /// Everything the interface draws in the accent colour, derived from ONE picked
    /// colour for one theme. The accent is never a single colour on screen — buttons,
    /// hover, selection, soft glows, the light theme's status bar — so the pick is
    /// turned into a family of shades, separately for the dark and the light theme.
    ///
    /// <para>The pick keeps its hue and saturation; its lightness is moved, only as
    /// far as needed, into the range where the theme stays readable: white text on an
    /// accent button, accent text on the theme's background. A pale yellow would give
    /// unreadable buttons, a navy would vanish on the dark theme — both come out as
    /// the nearest shade that works.</para>
    /// </summary>
    public sealed class AccentShades
    {
        public Color Accent { get; init; }
        /// <summary>The accent as "RRGGBB" (no '#'), for code that builds "#AARRGGBB" strings.</summary>
        public string Hex6 => $"{Accent.R:X2}{Accent.G:X2}{Accent.B:X2}";
        public Color Hover { get; init; }
        public Color Muted { get; init; }
        /// <summary>Translucent accent for soft fills and the editor glow.</summary>
        public Color Soft { get; init; }
        public Color Soft2 { get; init; }
        public double SelectionOpacity { get; init; }
        public double SelectionStrongOpacity { get; init; }
        /// <summary>Light theme: the status bar is accent-coloured (top stop of its gradient).</summary>
        public Color StatusBarTop { get; init; }

        // Row colours the grid asks for as strings (TranslationRow.RowBackground).
        // The selected-row colours are composited over the grid background, as the
        // row tints have always been, so they are not blended twice.
        public string RowEditing { get; init; } = "";
        public string RowSelected { get; init; } = "";
        public string RowSelectedHover { get; init; } = "";
        public string FindHit { get; init; } = "";
        public string FindHitCurrent { get; init; } = "";
    }

    public static class AccentPalette
    {
        /// <summary>The themes' own accents: VS blue (dark) and cinnamon (light).</summary>
        public static readonly Color DefaultDark = Color.FromRgb(0x2B, 0x8A, 0xE6);
        public static readonly Color DefaultLight = Color.FromRgb(0xA6, 0x5A, 0x2A);

        private static readonly Color GridDark = Color.FromRgb(0x1E, 0x1E, 0x1E);
        private static readonly Color GridLight = Color.FromRgb(0xFB, 0xF5, 0xE9);

        /// <summary>Swatches offered in the picker (any other colour comes from the sliders or the hex box).</summary>
        public static readonly string[] Presets =
        {
            "#2B8AE6", "#A65A2A", "#2FA36B", "#8E5BD9", "#D9534F", "#E08A1E",
            "#1AA6A6", "#D14F9A", "#6B7FD7", "#7A8A3A",
        };

        /// <summary>"#RRGGBB" (or "RRGGBB") → colour. Anything else is not an accent.</summary>
        public static bool TryParse(string? hex, out Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            string s = hex.Trim().TrimStart('#');
            if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return false;
            color = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
            return true;
        }

        public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        /// <summary>The shades for <paramref name="picked"/> on one theme.</summary>
        public static AccentShades For(Color picked, bool dark)
        {
            ToHsl(picked, out double h, out double s, out double l);

            // Relative-luminance window per theme (WCAG):
            //  dark  — white text on the accent ≥ 3:1 (≤ 0.30) and the accent on the
            //          dark background ≥ 3.5:1 (≥ 0.17);
            //  light — white text on the accent ≥ 4.5:1 (≤ 0.18): it carries the
            //          status bar's text.
            double lo = dark ? 0.17 : 0.05, hi = dark ? 0.30 : 0.18;
            l = FitLuminance(h, s, l, lo, hi);
            var accent = FromHsl(h, s, l);

            var hover = FromHsl(h, s, Math.Min(0.92, l + (dark ? 0.06 : 0.05)));
            var muted = dark
                ? FromHsl(h, s, Math.Max(0.08, l - 0.13))
                : FromHsl(h, Math.Min(s, 0.50), 0.73);           // a pale tint on light surfaces
            var grid = dark ? GridDark : GridLight;
            double sel = dark ? 0.18 : 0.16, selStrong = dark ? 0.32 : 0.28;

            return new AccentShades
            {
                Accent = accent,
                Hover = hover,
                Muted = muted,
                Soft = WithAlpha(accent, (byte)(dark ? 0x2D : 0x1F)),
                Soft2 = WithAlpha(accent, (byte)(dark ? 0x4D : 0x33)),
                SelectionOpacity = sel,
                SelectionStrongOpacity = selStrong,
                StatusBarTop = FromHsl(h, s, Math.Min(0.92, l + 0.02)),
                RowEditing = Hex8(WithAlpha(accent, (byte)(dark ? 0x33 : 0x26))),
                RowSelected = ToHex(Over(accent, grid, sel)),
                RowSelectedHover = ToHex(Over(accent, grid, selStrong)),
                FindHit = Hex8(WithAlpha(accent, 0x33)),
                FindHitCurrent = Hex8(WithAlpha(accent, 0x55)),
            };
        }

        /// <summary>Contrast ratio between two opaque colours (1 … 21).</summary>
        public static double Contrast(Color a, Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        public static double Luminance(Color c)
        {
            static double Lin(byte v)
            {
                double x = v / 255.0;
                return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        }

        /// <summary>Move lightness — and nothing else — until the luminance is inside [lo, hi].</summary>
        private static double FitLuminance(double h, double s, double l, double lo, double hi)
        {
            double lum = Luminance(FromHsl(h, s, l));
            if (lum >= lo && lum <= hi) return l;
            double target = lum < lo ? lo + 0.01 : hi - 0.01;
            double a = 0, b = 1;                                  // luminance rises with lightness
            for (int i = 0; i < 24; i++)
            {
                double mid = (a + b) / 2;
                if (Luminance(FromHsl(h, s, mid)) < target) a = mid; else b = mid;
            }
            return (a + b) / 2;
        }

        private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

        private static Color Over(Color top, Color bottom, double opacity)
        {
            static byte Mix(byte t, byte b, double o) => (byte)Math.Round(t * o + b * (1 - o));
            return Color.FromRgb(Mix(top.R, bottom.R, opacity), Mix(top.G, bottom.G, opacity), Mix(top.B, bottom.B, opacity));
        }

        private static string Hex8(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        public static void ToHsl(Color c, out double h, out double s, out double l)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            l = (max + min) / 2;
            double d = max - min;
            if (d < 1e-9) { h = 0; s = 0; return; }
            s = d / (1 - Math.Abs(2 * l - 1));
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }

        public static Color FromHsl(double h, double s, double l)
        {
            h = ((h % 360) + 360) % 360;
            double c = (1 - Math.Abs(2 * l - 1)) * s;
            double hp = h / 60.0;
            double x = c * (1 - Math.Abs(hp % 2 - 1));
            double r = 0, g = 0, b = 0;
            if (hp < 1) { r = c; g = x; }
            else if (hp < 2) { r = x; g = c; }
            else if (hp < 3) { g = c; b = x; }
            else if (hp < 4) { g = x; b = c; }
            else if (hp < 5) { r = x; b = c; }
            else { r = c; b = x; }
            double m = l - c / 2;
            static byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
            return Color.FromRgb(B(r + m), B(g + m), B(b + m));
        }
    }
}
