using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SmartUnrealLocalization.Localization;
using SmartUnrealLocalization.Services;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>What the user decided at the merge report.</summary>
    public enum MergeReportChoice
    {
        /// <summary>
        /// Change nothing. FIRST on purpose: closing a dialog with the title-bar
        /// X calls Close() with no value, and ShowDialog then returns default(T).
        /// With Apply first, that gesture APPLIED the merge — the exact opposite
        /// of what someone reaching for X means.
        /// </summary>
        Cancel,
        /// <summary>Write the result as matched.</summary>
        Apply,
        /// <summary>Redo the match ignoring the original-text check.</summary>
        KeyOnly,
    }

    /// <summary>
    /// The last stop before a merge is applied: what matched, what did not, and
    /// why — with the looser rule offered as a button rather than buried in
    /// settings.
    ///
    /// <para>This dialog exists because the strict rule can legitimately match
    /// NOTHING: an old file whose hashes another tool rebuilt looks 100% changed,
    /// and applying that silently would replace a finished translation with an
    /// empty one. Showing the counts first makes that impossible to miss.</para>
    /// </summary>
    public partial class MergeReportDialog : Window
    {
        // Parameterless ctor for the Avalonia runtime XAML loader (AVLN3001).
        public MergeReportDialog() => InitializeComponent();

        private MergeReportDialog(MergeMatchResult r, string oldFile, string newFile) : this()
        {
            string title = string.Format(Loc.T("merge.report.title"), r.CarriedRows, r.TotalRows);
            Title = title;
            if (this.FindControl<DialogTitleBar>("TitleBar") is { } tb) tb.Title = title;

            if (this.FindControl<TextBlock>("ModeText") is { } mode)
                mode.Text = r.Verification switch
                {
                    MergeVerification.Hash => Loc.T("merge.report.mode.hash"),
                    MergeVerification.SourceText => Loc.T("merge.report.mode.source"),
                    _ => Loc.T("merge.report.mode.key"),
                };

            if (this.FindControl<TextBlock>("CountsText") is { } counts)
                counts.Text = string.Join("\n", BuildLines(r, oldFile, newFile));

            // Warn on the two shapes that deserve a second look: almost nothing
            // survived verification, or bare keys were used across namespaces.
            var warnings = new List<string>();
            if (r.Verification != MergeVerification.KeyOnly && r.RejectedRows > 0 && r.VerifiedShare < 0.5)
                warnings.Add(Loc.T("merge.report.lowMatch"));
            if (r.AmbiguousKeys > 0)
                warnings.Add(string.Format(Loc.T("merge.report.ambiguousWarn"), r.AmbiguousKeys));

            if (warnings.Count > 0)
            {
                if (this.FindControl<Border>("WarnBox") is { } box) box.IsVisible = true;
                if (this.FindControl<TextBlock>("WarnText") is { } wt) wt.Text = string.Join("\n\n", warnings);
            }

            if (this.FindControl<Button>("BtnApply") is { } apply)
                apply.Content = string.Format(Loc.T("merge.report.apply"), r.CarriedRows);
            if (this.FindControl<Button>("BtnCancel") is { } cancel)
                cancel.Content = Loc.T("common.cancel");
            if (this.FindControl<Button>("BtnKeyOnly") is { } keyOnly)
            {
                // Nothing looser to offer when the match was already key-only.
                keyOnly.IsVisible = r.Verification != MergeVerification.KeyOnly;
                keyOnly.Content = Loc.T("merge.report.keyOnly");
            }
        }

        private static IEnumerable<string> BuildLines(MergeMatchResult r, string oldFile, string newFile)
        {
            yield return string.Format(Loc.T("merge.report.line.files"), oldFile, newFile);
            yield return string.Format(Loc.T("merge.report.line.carried"), r.CarriedRows, r.TotalRows);
            if (r.RejectedRows > 0)
                yield return string.Format(Loc.T("merge.report.line.rejected"), r.RejectedRows);
            if (r.EmptyRows > 0)
                yield return string.Format(Loc.T("merge.report.line.empty"), r.EmptyRows);
            if (r.NewRows > 0)
                yield return string.Format(Loc.T("merge.report.line.new"), r.NewRows);
            if (r.RemovedKeys > 0)
                yield return string.Format(Loc.T("merge.report.line.removed"), r.RemovedKeys);
            if (!r.UsedNamespace)
                yield return Loc.T("merge.report.line.noNamespace");
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        private void OnApply(object? sender, RoutedEventArgs e) => Close(MergeReportChoice.Apply);
        private void OnKeyOnly(object? sender, RoutedEventArgs e) => Close(MergeReportChoice.KeyOnly);
        private void OnCancel(object? sender, RoutedEventArgs e) => Close(MergeReportChoice.Cancel);

        /// <summary>Show modally; the title-bar close returns Cancel.</summary>
        public static async Task<MergeReportChoice> ShowAsync(Window owner, MergeMatchResult result,
                                                              string oldFile, string newFile)
            => await new MergeReportDialog(result, oldFile, newFile)
                     .ShowDialog<MergeReportChoice>(owner);
    }
}
