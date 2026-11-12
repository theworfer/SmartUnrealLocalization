using Avalonia.Media;

namespace SmartUnrealLocalization.Utils
{
    /// <summary>
    /// The app's icon-mark vocabulary — seven marks, all monochrome.
    ///
    /// <para>What keeps them monochrome is <see cref="MonochromeFallback"/>,
    /// installed once in <c>Program.BuildAvaloniaApp</c>. None of these characters
    /// exists in Inter, so every one of them goes to font fallback — and on
    /// Avalonia 12.0.2 + Skia under Windows that fallback depends on what the
    /// process has already loaded: measured with <c>TextLayout</c> on 2026-09-23,
    /// the same string in the same font stack came back as Segoe UI Symbol
    /// (monochrome) in one run and Segoe UI Emoji (a yellow cartoon, for ⚠, ℹ and
    /// even ✓) in another. That is the "randomly flips" pitfall in docs/ARCHITECTURE.md.
    /// Naming Segoe UI Symbol for exactly these ranges takes the choice away from
    /// history.</para>
    ///
    /// <para>The variation selector U+FE0E does NOT do that job here — bare ⚠,
    /// ⚠+U+FE0E and ⚠+U+FE0F were resolved identically. It stays on the two
    /// dual-presentation marks anyway: it is what Unicode prescribes, costs
    /// nothing, and a renderer that honours it (a browser showing an exported log,
    /// a terminal) gets the text face without our fallback.</para>
    ///
    /// <para>Nothing outside this list is allowed in user-visible text; there is
    /// no monochrome form of a clipboard, a robot or a floppy disk, and a colour
    /// pictograph in an IDE-style log is decoration, not information.
    /// <c>EmojiPolicyTests</c> fails the build if a colour emoji reappears in a
    /// source file or a language file, and if a mark falls outside
    /// <see cref="MarkRanges"/>.</para>
    /// </summary>
    public static class Glyphs
    {
        /// <summary>✓ — an operation finished successfully.</summary>
        public const string Ok = "✓";

        /// <summary>✕ — an operation failed.</summary>
        public const string Fail = "✕";

        /// <summary>✗ — a row was rejected in review (distinct from a failure).</summary>
        public const string Rejected = "✗";

        /// <summary>⚠ — warning. Written as escapes: the trailing U+FE0E is
        /// invisible, and an invisible character must not be silently droppable by
        /// an editor.</summary>
        public const string Warning = "⚠︎";

        /// <summary>ℹ — information, with U+FE0E the same way.</summary>
        public const string Info = "ℹ︎";

        // The Problems list, the QC groups, the status-icon column, the toasts and
        // the status bar show severity as a column of icons rather than as a prefix
        // inside a sentence. There the enclosed forms line up with each other and
        // with the warning triangle better than a bare cross or i would — the same
        // choice Visual Studio's Error List makes.

        /// <summary>ⓧ — error, in an icon column.</summary>
        public const string ErrorRound = "ⓧ";

        /// <summary>ⓘ — information, in an icon column.</summary>
        public const string InfoRound = "ⓘ";

        /// <summary>
        /// The blocks every mark above lives in: Letterlike Symbols (ℹ), Enclosed
        /// Alphanumerics (ⓧ ⓘ), Miscellaneous Symbols and Dingbats (⚠ ✓ ✕ ✗).
        /// </summary>
        public const string MarkRanges = "U+2139, U+2460-24FF, U+2600-27BF";

        /// <summary>
        /// Font options that resolve every character in <see cref="MarkRanges"/>
        /// from Segoe UI Symbol — monochrome, and present on every Windows since
        /// 8 — before the platform's own fallback is asked. Only characters the
        /// primary font lacks reach it, so Inter's text and the CJK faces in the
        /// language stacks are untouched.
        /// </summary>
        public static FontManagerOptions MonochromeFallback => new()
        {
            FontFallbacks = new[]
            {
                new FontFallback
                {
                    FontFamily = new FontFamily("Segoe UI Symbol"),
                    UnicodeRange = UnicodeRange.Parse(MarkRanges),
                },
            },
        };
    }
}
