using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using SmartUnrealLocalization.Services.Qc;

namespace SmartUnrealLocalization.Utils
{
    /// <summary>
    /// The one way a before/after change is drawn: the QC popup, the problem
    /// popover and the fix review all use it, so a change looks the same
    /// wherever the user meets it.
    /// </summary>
    public static class DiffInlines
    {
        /// <summary>Word-level before/after inlines for two texts.</summary>
        public static (InlineCollection Before, InlineCollection After) Build(string before, string after)
        {
            var (b, a) = QcDiff.Compute(before ?? "", after ?? "");
            return (Paint(b), Paint(a));
        }

        /// <summary>
        /// Changed words get the accent colour, bold, and a tinted background, so a
        /// change is visible even when it is a single character.
        /// </summary>
        public static InlineCollection Paint(IEnumerable<QcDiff.Segment> segments)
        {
            var accent = Avalonia.Application.Current?.FindResource("AccentColor") as IBrush;
            var highlight = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xCC, 0x00));

            var inlines = new InlineCollection();
            foreach (var seg in segments)
            {
                var run = new Run(seg.Changed ? Visualize(seg.Text) : seg.Text);
                if (seg.Changed)
                {
                    if (accent != null) run.Foreground = accent;
                    run.FontWeight = FontWeight.Bold;
                    run.Background = highlight;
                }
                inlines.Add(run);
            }
            return inlines;
        }

        /// <summary>
        /// Make an invisible change visible. A lost leading or trailing space is
        /// the most common fix of all, and colouring a space changes nothing on
        /// screen. Only whitespace-ONLY segments are rewritten; a changed word is
        /// already obvious from its colour.
        /// </summary>
        public static string Visualize(string text)
        {
            if (text.Length == 0 || text.Trim().Length != 0) return text;
            return text.Replace(" ", "·").Replace("\t", "→").Replace("\n", "¶\n");
        }
    }
}
