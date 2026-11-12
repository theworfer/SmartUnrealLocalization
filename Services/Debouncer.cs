using System;
using Avalonia.Threading;

namespace SmartUnrealLocalization.Services
{
    /// <summary>
    /// Coalesces a burst of requests into ONE run, a fixed delay after the last.
    ///
    /// <para>This exists because the obvious hand-rolled version is wrong in a way
    /// that looks right. The find bar and the text filter both did this on every
    /// request:</para>
    /// <code>
    ///     timer.Stop();
    ///     void Handler(...) { timer.Stop(); timer.Tick -= Handler; Work(); }
    ///     timer.Tick += Handler;
    ///     timer.Start();
    /// </code>
    /// <para>Stopping a timer does not unsubscribe anything. A thousand requests
    /// inside one interval left a thousand handlers on the timer, and when it
    /// finally ticked, all of them ran — a thousand full-file scans for what was
    /// meant to be one. Typing produced a handful; a bulk edit with the find bar
    /// open (an AI batch, a pasted column, an undo) produced one per changed row,
    /// which on a large file is quadratic and freezes the window. Found by the
    /// 2026-09-09 reliability audit.</para>
    ///
    /// <para>Here the timer is subscribed exactly once, when the debouncer is
    /// built, and a request only updates the pending value and restarts it.
    /// <c>DebouncerTests</c> pins the behaviour; <c>TimerSubscriptionTests</c>
    /// fails if a <c>Tick +=</c> appears anywhere except next to the timer it
    /// subscribes to.</para>
    /// </summary>
    /// <typeparam name="T">What a request carries — e.g. "reset the cursor".
    /// Requests that land in the same window are folded together with
    /// <c>merge</c> before the single run sees them.</typeparam>
    public sealed class Debouncer<T>
    {
        private readonly IDebounceClock _clock;
        private readonly Func<T, T, T> _merge;
        private readonly Action<T> _run;
        private bool _pending;
        private T _value = default!;

        public Debouncer(TimeSpan delay, Func<T, T, T> merge, Action<T> run)
            : this(new DispatcherDebounceClock(delay), merge, run) { }

        internal Debouncer(IDebounceClock clock, Func<T, T, T> merge, Action<T> run)
        {
            _clock = clock;
            _merge = merge;
            _run = run;
            _clock.Elapsed += Fire;   // the only subscription, for the debouncer's whole life
        }

        /// <summary>True while a run is scheduled and has not happened yet.</summary>
        public bool IsPending => _pending;

        /// <summary>Schedule a run <c>delay</c> from now, folding <paramref name="value"/>
        /// into whatever is already pending.</summary>
        public void Request(T value)
        {
            _value = _pending ? _merge(_value, value) : value;
            _pending = true;
            _clock.Restart();
        }

        /// <summary>Drop any pending run — for when what it would act on is gone
        /// (the file closed, the tab changed, the bar was dismissed).</summary>
        public void Cancel()
        {
            _clock.Stop();
            _pending = false;
            _value = default!;
        }

        /// <summary>
        /// Claim the pending request, if any, and cancel its run — for a caller
        /// that is about to do the same work synchronously. The run would only
        /// repeat it, but the request's VALUE still matters: a pending "reset the
        /// cursor" must be honoured by the synchronous run, not silently dropped.
        /// </summary>
        public bool TryTakePending(out T value)
        {
            value = _value;
            if (!_pending) return false;
            Cancel();
            return true;
        }

        private void Fire()
        {
            _clock.Stop();
            if (!_pending) return;
            T value = _value;
            // Clear BEFORE running: the run may itself request again (a rescan
            // that edits rows, say), and that request must start a fresh window
            // rather than be swallowed by this one.
            _pending = false;
            _value = default!;
            _run(value);
        }
    }

    /// <summary>A <see cref="Debouncer{T}"/> for work that carries no value.</summary>
    public sealed class Debouncer
    {
        private readonly Debouncer<bool> _inner;

        public Debouncer(TimeSpan delay, Action run)
            => _inner = new Debouncer<bool>(delay, (_, _) => true, _ => run());

        internal Debouncer(IDebounceClock clock, Action run)
            => _inner = new Debouncer<bool>(clock, (_, _) => true, _ => run());

        public bool IsPending => _inner.IsPending;
        public void Request() => _inner.Request(true);
        public void Cancel() => _inner.Cancel();
    }

    /// <summary>The one thing a debouncer needs from a timer — abstracted so the
    /// coalescing can be tested without a running dispatcher.</summary>
    internal interface IDebounceClock
    {
        event Action? Elapsed;
        void Restart();
        void Stop();
    }

    internal sealed class DispatcherDebounceClock : IDebounceClock
    {
        private readonly DispatcherTimer _timer;
        public event Action? Elapsed;

        public DispatcherDebounceClock(TimeSpan delay)
        {
            _timer = new DispatcherTimer { Interval = delay };
            _timer.Tick += (_, _) => Elapsed?.Invoke();
        }

        public void Restart()
        {
            _timer.Stop();
            _timer.Start();
        }

        public void Stop() => _timer.Stop();
    }
}
