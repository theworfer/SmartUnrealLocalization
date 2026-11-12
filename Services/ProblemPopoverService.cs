using System;
using Avalonia.Threading;
using SmartUnrealLocalization.Controls;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services
{
    /// <summary>
    /// Singleton hover-orchestrator for the <see cref="ProblemPopover"/>.
    /// Two consumers (status-icon TextBlock + wavy-underline segment Grids in
    /// TaggedTextBlock) call <see cref="RequestShow"/> on PointerEntered and
    /// <see cref="RequestHide"/> on PointerExited; the service runs 180 ms
    /// show / hide grace timers so the popover stays open while the cursor
    /// moves from the trigger to the popover content.
    ///
    /// <list type="bullet">
    ///   <item>RequestShow → cancel pending hide, start show timer (180 ms).</item>
    ///   <item>RequestHide → cancel show timer, start hide timer (180 ms).</item>
    ///   <item>CancelHide  → invoked by ProblemPopover.OnHoverIn so the popup
    ///         stays open while the cursor is over its content.</item>
    /// </list>
    ///
    /// <para>
    /// The popover is suppressed while the row is being edited — it would
    /// cover the inline editor and steal pointer events from the TextBox.
    /// </para>
    /// </summary>
    public sealed class ProblemPopoverService
    {
        private static ProblemPopoverService? _instance;
        public static ProblemPopoverService Instance => _instance ??= new ProblemPopoverService();

        private ProblemPopover? _popover;
        private DispatcherTimer? _showTimer;
        private DispatcherTimer? _hideTimer;
        private (Avalonia.Controls.Control anchor, Problem problem, TranslationRow row)? _pending;

        private const int ShowDelayMs = 180;
        private const int HideDelayMs = 180;

        private ProblemPopoverService() { }

        /// <summary>Wire the singleton instance of the popover host (called from MainWindow.OnLoaded).</summary>
        public void Attach(ProblemPopover popover) => _popover = popover;

        public void RequestShow(Avalonia.Controls.Control anchor, Problem problem, TranslationRow row)
        {
            if (_popover is null) return;
            if (row.IsEditing) return; // popover would cover the editor — skip.

            CancelHideInternal();
            _pending = (anchor, problem, row);

            if (_showTimer is null)
            {
                _showTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ShowDelayMs) };
                _showTimer.Tick += OnShowTick;
            }
            _showTimer.Stop();
            _showTimer.Start();
        }

        public void RequestHide()
        {
            _showTimer?.Stop();
            if (_popover is null) return;

            if (_hideTimer is null)
            {
                _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HideDelayMs) };
                _hideTimer.Tick += OnHideTick;
            }
            _hideTimer.Stop();
            _hideTimer.Start();
        }

        /// <summary>
        /// Called by <see cref="ProblemPopover"/> when the pointer enters the
        /// popover's own content — keeps the popup open instead of closing on
        /// the grace timer.
        /// </summary>
        public void CancelHide() => CancelHideInternal();

        private void CancelHideInternal() => _hideTimer?.Stop();

        /// <summary>Force-hide (e.g. after Apply Fix or Dismiss button).</summary>
        public void HideImmediate()
        {
            _showTimer?.Stop();
            _hideTimer?.Stop();
            _popover?.Hide();
        }

        private void OnShowTick(object? sender, EventArgs e)
        {
            _showTimer?.Stop();
            if (_popover is null || _pending is not { } p) return;

            // Race guard — row started editing while the 180 ms show timer was
            // in flight (user double-clicked Target after hovering its status
            // icon). Showing the popup would cover the new editor.
            if (p.row.IsEditing) return;

            // The anchor might be detached from the visual tree by the time
            // the timer fires (file closed, DataGrid recycled the row).
            // Avalonia's Popup throws on a dead PlacementTarget; swallow so
            // we don't crash on a benign race.
            try
            {
                _popover.Show(p.anchor, p.problem, p.row);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProblemPopoverService] Show failed (anchor likely detached): {ex.Message}");
            }
        }

        private void OnHideTick(object? sender, EventArgs e)
        {
            _hideTimer?.Stop();
            _popover?.Hide();
        }
    }
}
