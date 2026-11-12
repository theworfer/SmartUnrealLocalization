using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services
{
    /// <summary>
    /// Singleton that owns the active toast queue and the per-toast
    /// auto-dismiss timers. The <see cref="Controls.ToastStack"/> binds its
    /// <c>ItemsControl.ItemsSource</c> directly to <see cref="Active"/>;
    /// callers anywhere in the app push notifications via
    /// <see cref="ShowSuccess"/> / <see cref="ShowInfo"/> / <see cref="ShowWarning"/> /
    /// <see cref="ShowError"/> (or the lower-level <see cref="Show"/>).
    ///
    /// <para>
    /// Newest toast is added to the END of the collection so a bottom-
    /// anchored vertical StackPanel naturally renders the latest message
    /// closest to the status bar — matching IntelliJ / VS Code behaviour.
    /// </para>
    ///
    /// <para>
    /// Etap 8 hooks: ProblemPopover Apply Fix, LightbulbButton Apply Fix,
    /// ClearCellRecovered / ClearTagReplaced / ClearInfoFromSelected bulk
    /// commands. Later etaps add Replace All (Etap 9), Mark reviewed (Etap 11),
    /// and Fix All (Etap 10).
    /// </para>
    /// </summary>
    public sealed class ToastService
    {
        private static readonly Lazy<ToastService> _instance = new(() => new ToastService());
        public static ToastService Instance => _instance.Value;

        private ToastService() { }

        /// <summary>Live collection bound to the ToastStack ItemsControl.</summary>
        public ObservableCollection<ToastNotification> Active { get; } = new();

        // Per-toast timers so manual Dismiss can stop the auto-fire and
        // multiple in-flight toasts don't share a single tick.
        private readonly Dictionary<Guid, DispatcherTimer> _timers = new();

        // Cap the visible queue so a burst of clicks (10 rapid Apply Fix in
        // < 4 s) doesn't pile pills off the bottom of the viewport. When a
        // new toast arrives at the cap, drop the OLDEST instead of refusing
        // the new one — latest action is what the user expects to see.
        private const int MaxVisible = 4;

        /// <summary>
        /// Push a toast onto the queue. Safe to call from any thread — the
        /// collection mutation is marshalled to the UI thread because the
        /// timer infrastructure (and Avalonia's binding system) is
        /// single-threaded.
        /// </summary>
        public ToastNotification Show(string message, ToastKind kind = ToastKind.Info, Action? undo = null, int durationMs = 4000)
        {
            var toast = new ToastNotification
            {
                Message = message,
                Kind = kind,
                UndoAction = undo,
                DurationMs = durationMs,
            };

            // Always enqueue + start the timer on the UI thread.
            if (Dispatcher.UIThread.CheckAccess())
                Enqueue(toast);
            else
                Dispatcher.UIThread.Post(() => Enqueue(toast));

            return toast;
        }

        public ToastNotification ShowInfo(string message, Action? undo = null, int durationMs = 4000)
            => Show(message, ToastKind.Info, undo, durationMs);

        public ToastNotification ShowSuccess(string message, Action? undo = null, int durationMs = 4000)
            => Show(message, ToastKind.Success, undo, durationMs);

        public ToastNotification ShowWarning(string message, Action? undo = null, int durationMs = 4000)
            => Show(message, ToastKind.Warning, undo, durationMs);

        public ToastNotification ShowError(string message, Action? undo = null, int durationMs = 6000)
            => Show(message, ToastKind.Error, undo, durationMs);

        /// <summary>Manually remove a toast (X click or Undo click).</summary>
        public void Dismiss(ToastNotification toast)
        {
            if (toast is null) return;
            if (Dispatcher.UIThread.CheckAccess())
                RemoveInternal(toast);
            else
                Dispatcher.UIThread.Post(() => RemoveInternal(toast));
        }

        /// <summary>Drop every toast (used on file close + window close).</summary>
        public void Clear()
        {
            void Inner()
            {
                foreach (var (_, t) in _timers) t.Stop();
                _timers.Clear();
                Active.Clear();
            }
            if (Dispatcher.UIThread.CheckAccess()) Inner();
            else Dispatcher.UIThread.Post(Inner);
        }

        private void Enqueue(ToastNotification toast)
        {
            // Cap enforcement: drop oldest until we're under the limit BEFORE
            // adding the new one, so the visible count post-Add is exactly
            // MaxVisible (newest-bottom layout = newcomer always wins).
            while (Active.Count >= MaxVisible)
                RemoveInternal(Active[0]);

            Active.Add(toast);

            // DurationMs == 0 → sticky; no timer.
            if (toast.DurationMs <= 0) return;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(toast.DurationMs) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                RemoveInternal(toast);
            };
            _timers[toast.Id] = timer;
            timer.Start();
        }

        private void RemoveInternal(ToastNotification toast)
        {
            if (_timers.Remove(toast.Id, out var timer)) timer.Stop();
            Active.Remove(toast);
        }
    }
}
