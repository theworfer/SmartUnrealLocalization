using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>
    /// Tiny 18×18 quick-fix affordance pinned to the right edge of the Target
    /// cell. Visible when <c>TranslationRow.HasFixableProblem</c>; click
    /// invokes the first <see cref="Problem.Fix"/> on the row. The fix flows
    /// through the standard <c>row.Target = NewValue</c> setter, so the
    /// debounced ValidationService.ScheduleRowValidation hook re-runs the
    /// validators automatically — the lightbulb disappears once the row no
    /// longer has any fixable Problem.
    ///
    /// <para>
    /// Etap 8 — click also fires a green-stripe toast with an Undo button
    /// (one-step revert via UndoRedoManager.Undo on the captured VM).
    /// </para>
    /// </summary>
    public partial class LightbulbButton : UserControl
    {
        public LightbulbButton() => InitializeComponent();

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        private void OnClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not TranslationRow row) return;

            var firstFixable = row.Problems.FirstOrDefault(p => p.Fix is not null);
            if (firstFixable is null) return;
            // Close the hover preview anchored here, then show the change in the
            // review — the click used to write it immediately, before the user had
            // necessarily seen what it would do (2026-10-21).
            Services.ProblemPopoverService.Instance.HideImmediate();
            _ = ViewModels.MainWindowViewModel.Current?.ReviewProblemFix(row, firstFixable);
        }

        // Etap 6 popover — same hover model as the status icon + wavy spans:
        // 180 ms grace timer in the service, cancels show/hide cleanly,
        // suppresses during edit. Surfaces the same Fix the click would apply
        // (first-fixable problem on the row), so the user can preview the
        // diff before committing.
        private void OnPointerEntered(object? sender, PointerEventArgs e)
        {
            if (sender is not Control c) return;
            if (DataContext is not TranslationRow row) return;
            var firstFixable = row.Problems.FirstOrDefault(p => p.Fix is not null);
            if (firstFixable is null) return;
            Services.ProblemPopoverService.Instance.RequestShow(c, firstFixable, row);
        }

        private void OnPointerExited(object? sender, PointerEventArgs e)
            => Services.ProblemPopoverService.Instance.RequestHide();
    }
}
