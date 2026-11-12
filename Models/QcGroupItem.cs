using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartUnrealLocalization.Localization;
using SmartUnrealLocalization.Utils;

namespace SmartUnrealLocalization.Models
{
    /// <summary>
    /// One CATEGORY of QC findings in the QC tab — "every row that lost its
    /// ending punctuation", not one row at a time.
    ///
    /// <para>Grouping is the whole point of the tab. A real 13 k-row file produces
    /// well over a thousand findings, and a flat list of them is unreadable: the
    /// thirty rows that genuinely need a human are buried under hundreds of
    /// mechanical ones. Collapsed by category the same run reads as a handful of
    /// lines, each with its own bulk action.</para>
    /// </summary>
    public sealed partial class QcGroupItem : ObservableObject
    {
        public QcGroupItem(string kind)
        {
            Kind = kind;
            Label = TextUtils.KindToLabel(kind);
        }

        /// <summary>Machine kind ("TagOrder") — the grouping key.</summary>
        public string Kind { get; }

        /// <summary>SCREAMING-KEBAB display label ("TAG-ORDER").</summary>
        public string Label { get; }

        /// <summary>
        /// One plain sentence explaining what this category means and whether it
        /// usually matters. Localised; falls back to the raw kind so a category
        /// added later still renders something sane instead of a blank line.
        /// </summary>
        public string Hint
        {
            get
            {
                var text = Loc.Instance["qc.kind." + Kind];
                return string.IsNullOrEmpty(text) || text.StartsWith("qc.kind.") ? "" : text;
            }
        }

        public ObservableCollection<ProblemListItem> Items { get; } = new();

        /// <summary>Rows in this category (the list itself may be capped for display).</summary>
        [ObservableProperty] private int _count;

        /// <summary>How many of them carry a replacement the bulk button can apply.</summary>
        [ObservableProperty] private int _fixableCount;

        /// <summary>Worst severity in the group — drives the glyph colour and the order.</summary>
        [ObservableProperty] private ProblemSeverity _severity = ProblemSeverity.Info;

        /// <summary>Collapsed by default: the header line IS the summary.</summary>
        [ObservableProperty] private bool _isExpanded;

        public bool HasFixes => FixableCount > 0;
        partial void OnFixableCountChanged(int value) => OnPropertyChanged(nameof(HasFixes));

        public string Glyph => Severity switch
        {
            ProblemSeverity.Error => Utils.Glyphs.ErrorRound,
            ProblemSeverity.Warning => Utils.Glyphs.Warning,
            _ => Utils.Glyphs.InfoRound,
        };

        public string SeverityName => Severity.ToString();

        partial void OnSeverityChanged(ProblemSeverity value)
        {
            OnPropertyChanged(nameof(Glyph));
            OnPropertyChanged(nameof(SeverityName));
        }

        /// <summary>Chevron shown in the header, so the row reads as expandable.</summary>
        public string ExpandGlyph => IsExpanded ? "▾" : "▸";
        partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandGlyph));
    }
}
