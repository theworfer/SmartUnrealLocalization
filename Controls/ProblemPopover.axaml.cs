using System;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>
    /// Floating 360 px popup that shows one <see cref="Problem"/>'s details
    /// (severity glyph + title + kind + detail + optional diff + Apply fix
    /// button) on hover. Visibility is orchestrated by
    /// <see cref="ProblemPopoverService"/>; clicking Apply fix runs the
    /// <see cref="Fix.Apply"/>, validators re-run via the debounced pipeline,
    /// and the popup closes.
    /// </summary>
    public partial class ProblemPopover : UserControl
    {
        private TranslationRow? _currentRow;
        private Problem? _currentProblem;

        public ProblemPopover() => InitializeComponent();

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        /// <summary>The row whose problem is currently displayed (null when hidden).</summary>
        public TranslationRow? CurrentRow => _currentRow;

        /// <summary>
        /// Open the popover anchored beneath <paramref name="anchor"/>, populating
        /// it with <paramref name="problem"/>'s data. Idempotent — repeated calls
        /// with the same problem just re-anchor.
        /// </summary>
        public void Show(Control anchor, Problem problem, TranslationRow row)
        {
            _currentProblem = problem;
            _currentRow = row;

            var popup = this.FindControl<Popup>("InnerPopup");
            if (popup is null) return;

            UpdateContent(problem);

            popup.PlacementTarget = anchor;
            popup.Placement = PlacementMode.Bottom;
            popup.IsOpen = true;
        }

        /// <summary>Close the popover. Safe to call when already closed.</summary>
        public void Hide()
        {
            var popup = this.FindControl<Popup>("InnerPopup");
            if (popup is not null) popup.IsOpen = false;
            _currentProblem = null;
            _currentRow = null;
        }

        private void UpdateContent(Problem problem)
        {
            // Severity glyph + colour mirror the status-icon column mapping so
            // the popover header matches whatever icon the user hovered.
            var (glyph, brush) = SeverityVisual(problem.Severity);
            this.FindControl<TextBlock>("SeverityGlyph")!.Text = glyph;
            this.FindControl<TextBlock>("SeverityGlyph")!.Foreground = brush;
            this.FindControl<TextBlock>("TitleText")!.Text = problem.Title;
            this.FindControl<TextBlock>("KindText")!.Text = FormatKindLabel(problem.Kind);
            this.FindControl<TextBlock>("DetailText")!.Text = problem.Detail;

            // "Has different translations": the walk buttons and "use this one".
            bool isDup = problem.Kind == "DuplicateSource" && _currentRow is not null;
            this.FindControl<StackPanel>("DupPanel")!.IsVisible = isDup;
            if (isDup && ViewModels.MainWindowViewModel.Current is { } vm)
            {
                var group = vm.SameSourceRows(_currentRow!);
                int at = 0;
                for (int i = 0; i < group.Count; i++) if (ReferenceEquals(group[i], _currentRow)) { at = i; break; }
                this.FindControl<TextBlock>("DupPosition")!.Text =
                    string.Format(Localization.Loc.T("val.dup.position"), at + 1, group.Count);
            }

            var diffPanel = this.FindControl<Border>("DiffPanel")!;
            var applyBtn = this.FindControl<Button>("ApplyFixButton")!;

            if (problem.Fix is { } fix)
            {
                applyBtn.IsVisible = true;
                // Always use the generic localized "Apply fix" label. Earlier
                // versions piped fix.Label ("Apply 'Close window'", "Apply
                // 'You have {count}|plural(one=…'", "Append missing tags", …)
                // straight onto the button — which blew out the 360 px popover
                // for any long canonical value (DuplicateSource especially)
                // and pushed the button off the right edge. The diff block
                // immediately above already shows exactly what value will be
                // written, so the per-fix label was redundant + harmful.
                applyBtn.Content = Localization.Loc.T("popover.applyFix");
                // Truncate long values so the diff doesn't blow up the popup.
                diffPanel.IsVisible = true;
                foreach (var name in new[] { "DiffOld", "DiffNew" })
                {
                    var d = this.FindControl<DiffText>(name)!;
                    d.OldText = fix.OldValue;
                    d.NewText = fix.NewValue;
                }

            }
            else
            {
                applyBtn.IsVisible = false;
                diffPanel.IsVisible = false;
            }
        }

        private static (string glyph, IBrush brush) SeverityVisual(ProblemSeverity sev)
        {
            bool dark = AppConfig.Instance.IsDarkTheme;
            return sev switch
            {
                ProblemSeverity.Error => (Utils.Glyphs.ErrorRound, SolidColorBrush.Parse(dark ? "#F48771" : "#B44A2A")),
                ProblemSeverity.Warning => (Utils.Glyphs.Warning, SolidColorBrush.Parse(dark ? "#DCDCAA" : "#B5891D")),
                _ => (Utils.Glyphs.InfoRound, SolidColorBrush.Parse(dark ? "#9CDCFE" : "#2B6CB0")),
            };
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "(empty)";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        // PascalCase → SCREAMING-KEBAB-CASE for the popover Kind tag.
        // "DuplicateSource" → "DUPLICATE-SOURCE", "TagMismatch" → "TAG-MISMATCH",
        // matching the handoff preview's `INFO · DUPLICATE-SOURCE` header style.
        // Previous version was a plain ToUpperInvariant which produced
        // unreadable mashes like "DUPLICATESOURCE".
        private static string FormatKindLabel(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return string.Empty;
            var sb = new StringBuilder(kind.Length + 4);
            for (int i = 0; i < kind.Length; i++)
            {
                char c = kind[i];
                if (i > 0 && char.IsUpper(c)) sb.Append('-');
                sb.Append(char.ToUpperInvariant(c));
            }
            return sb.ToString();
        }

        // The popover's own hover handlers keep the popup alive while the user
        // glides from the trigger element onto the popup content. CancelHide
        // dismisses any pending hide timer in the service; RequestHide restarts
        // the 180 ms hide grace when the cursor leaves the popup.
        private void OnHoverIn(object? sender, PointerEventArgs e)
            => ProblemPopoverService.Instance.CancelHide();

        private void OnHoverOut(object? sender, PointerEventArgs e)
            => ProblemPopoverService.Instance.RequestHide();

        private void OnApplyFix(object? sender, RoutedEventArgs e)
        {
            if (_currentProblem?.Fix is { } fix && _currentRow is { } row)
            {
                // Capture the pre-fix Target so the toast's Undo button
                // can restore THIS specific change, not just "pop top of
                // UndoManager stack". Without this, a sequence of two
                // Apply Fix actions in <4 s left two toasts visible; clicking
                // Undo on the OLDER toast would revert the NEWER fix because
                // UndoManager.Undo() is positional — the toast lied about
                // what it was undoing.
                var oldTarget = row.Target ?? "";

                // Optimistic: drop THIS Problem first so the wavy underline
                // (which is positioned against pre-fix text indices) goes away
                // immediately — without this, the wavy stayed painted on top
                // of stale positions until the 300 ms debounced validator pass
                // ran and replaced the Problem set. The validator may still
                // re-add a (different) Problem if the fix wasn't perfect; the
                // overlay then re-paints against the correct new text.
                row.Problems.Remove(_currentProblem);
                fix.Apply(row);

                // Etap 8 — surface the fix as a toast with one-click Undo.
                // The Undo closure sets Target back to oldTarget directly;
                // the setter records its own CellEditAction so Ctrl+Z still
                // works (it'll redo the fix), AND the light validators
                // re-run 300 ms later against the restored Target so the
                // original Problem set comes back automatically.
                var label = string.IsNullOrEmpty(fix.Label)
                    ? Localization.Loc.T("popover.applyFix")
                    : fix.Label;
                var capturedRow = row;
                Action undo = () =>
                {
                    if ((capturedRow.Target ?? "") != oldTarget)
                        capturedRow.Target = oldTarget;
                };
                ToastService.Instance.ShowSuccess(string.Format(Localization.Loc.T("toast.fixApplied"), label), undo: undo);
            }
            ProblemPopoverService.Instance.HideImmediate();
        }

        private void OnDismiss(object? sender, RoutedEventArgs e)
            => ProblemPopoverService.Instance.HideImmediate();

        private void OnDupPrevious(object? sender, RoutedEventArgs e) => DupAction((vm, row) => vm.GoToSameSourceRowAsync(row, forward: false));
        private void OnDupNext(object? sender, RoutedEventArgs e) => DupAction((vm, row) => vm.GoToSameSourceRowAsync(row, forward: true));
        private void OnDupUse(object? sender, RoutedEventArgs e) => DupAction((vm, row) => vm.UseTranslationForSameSourceAsync(row));

        /// <summary>The popover belongs to the row it was opened on; the action moves
        /// on (another row, a review dialog), so it closes first.</summary>
        private static async void DupActionCore(Func<System.Threading.Tasks.Task> run)
        {
            try { await run(); }
            catch (Exception ex) { Console.WriteLine($"[popover] {ex.Message}"); }
        }

        private void DupAction(Func<ViewModels.MainWindowViewModel, TranslationRow, System.Threading.Tasks.Task> action)
        {
            var row = _currentRow;
            var vm = ViewModels.MainWindowViewModel.Current;
            ProblemPopoverService.Instance.HideImmediate();
            if (row is null || vm is null) return;
            DupActionCore(() => action(vm, row));
        }
    }
}
