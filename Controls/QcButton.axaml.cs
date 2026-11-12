using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using SmartUnrealLocalization.Localization;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services.Qc;
using SmartUnrealLocalization.ViewModels;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>
    /// QC badge for one Target cell: visible only while the row carries a pending
    /// suggestion. Clicking opens a wide popup with the FULL before/after texts,
    /// changed words highlighted in the accent colour on both sides, plus the
    /// Fix / Keep decision buttons. Nothing is written until Fix is pressed.
    /// </summary>
    public partial class QcButton : UserControl
    {
        private Flyout? _flyout;
        private TranslationRow? _row;
        private QcProposal? _proposal;

        public QcButton() => InitializeComponent();
        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        // The Button.Flyout opens itself; stop the press from also driving row selection.
        private void OnClick(object? sender, RoutedEventArgs e) => e.Handled = true;

        private void OnFlyoutOpened(object? sender, EventArgs e)
        {
            _flyout = sender as Flyout;
            _row = DataContext as TranslationRow;
            _proposal = _row?.QcProposal;
            if (_row == null || _proposal == null) return;

            var root = _flyout?.Content as Control;
            if (root == null) return;

            var before = Find<SelectableTextBlock>(root, "TxtBefore");
            var after = Find<SelectableTextBlock>(root, "TxtAfter");
            var reason = Find<TextBlock>(root, "LblReason");
            var severity = Find<TextBlock>(root, "LblSeverity");
            var stale = Find<TextBlock>(root, "LblStale");
            var fix = Find<Button>(root, "BtnFix");

            if (reason != null) reason.Text = _proposal.Reason;
            if (severity != null) severity.Text = _proposal.Severity.ToString().ToUpperInvariant();

            // Fix is offered only when there is something to apply: a row edited
            // after the run (stale) would be reverted, and a flagged-only finding
            // has no replacement at all — that one needs a human, so only Keep
            // (or editing the cell) makes sense.
            bool isStale = _proposal.IsStaleFor(_row);
            bool hasFix = !string.Equals(_proposal.Before, _proposal.After, StringComparison.Ordinal);
            if (stale != null) stale.IsVisible = isStale;
            if (fix != null)
            {
                fix.IsEnabled = !isStale && hasFix;
                // Always assign — the flyout control is reused across openings, so a
                // one-way "hide" here would keep Fix hidden for every later proposal.
                fix.IsVisible = hasFix;
            }
            if (!hasFix && reason != null)
                reason.Text = _proposal.Reason + "\n\n" + Loc.T("qc.diff.noFix");

            // Same reasoning as the Fix button: with no replacement there is no
            // "Suggested" text, and showing the current text twice under two
            // different headings reads as a diff with an invisible change.
            var afterPane = Find<StackPanel>(root, "AfterPane");
            if (afterPane != null) afterPane.IsVisible = hasFix;

            var (beforeSegs, afterSegs) = QcDiff.Compute(_proposal.Before, _proposal.After);
            Render(before, beforeSegs);
            if (hasFix) Render(after, afterSegs);
        }

        private static void Render(SelectableTextBlock? target, System.Collections.Generic.List<QcDiff.Segment> segments)
        {
            if (target != null) target.Inlines = Utils.DiffInlines.Paint(segments);
        }

        private void OnFix(object? sender, RoutedEventArgs e)
        {
            if (_row != null && _proposal != null)
                MainWindowViewModel.Current?.ApplyQcProposal(_row, _proposal);
            _flyout?.Hide();
        }

        private void OnKeep(object? sender, RoutedEventArgs e)
        {
            if (_row != null && _proposal != null)
                MainWindowViewModel.Current?.KeepQcProposal(_row, _proposal);
            _flyout?.Hide();
        }

        private static T? Find<T>(Control root, string name) where T : Control
            => root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);
    }
}
