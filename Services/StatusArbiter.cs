using System;
using System.Threading;
using SmartUnrealLocalization.Models;

namespace SmartUnrealLocalization.Services
{
    /// <summary>
    /// Decides whether a log line may be copied into the status bar.
    ///
    /// <para>Every <c>AddLog</c> copies its message into the bar, but <c>AddLog</c>
    /// posts to the dispatcher, so the copy lands after the call that made it. Code
    /// that set a summary and logged its details — Import Target sets "Imported
    /// 12 431 translations", then logs how many rows were already equal, equal to
    /// the source, or unmatched — saw the summary replaced within the same frame by
    /// the last detail. Writing the summary last could not help: the deferred copy
    /// always lands after it.</para>
    ///
    /// <para>The rule, in three parts:</para>
    /// <list type="bullet">
    /// <item>A line the code sets owns the bar for the rest of the dispatcher
    /// turn it was set in. Info lines logged in that turn, before or after it,
    /// are its details: they stay in the log.</item>
    /// <item>A warning or an error always shows — a problem is never a detail.</item>
    /// <item>Otherwise the newest line shows, newest by when it was ISSUED: each
    /// line takes a ticket at the <c>AddLog</c> call, on the caller's thread,
    /// so a copy that arrives late cannot replace something issued after it.</item>
    /// </list>
    /// <para>The turn boundary is what lets "Opening Game.locres…" give way to the
    /// "Loaded 14 115 rows" logged when the open finishes — after an await, in a
    /// later turn. A first version ranked every explicit line above every info line
    /// and kept "Opening…" on the bar for a file that was already open.</para>
    /// </summary>
    public sealed class StatusArbiter
    {
        private readonly Action<Action> _afterThisTurn;
        private readonly object _lock = new();
        private long _issued;   // the last ticket handed out
        private long _shown;    // ticket of the line on the bar; 0 = empty
        private long _owner;    // ticket of an explicit line still inside its own turn; 0 = none

        /// <param name="afterThisTurn">Runs an action once the current dispatcher
        /// turn — and every copy posted during it — is done. The app passes a
        /// Background-priority post, which runs after the Normal-priority posts
        /// <c>AddLog</c> makes.</param>
        public StatusArbiter(Action<Action> afterThisTurn) => _afterThisTurn = afterThisTurn;

        /// <summary>A log line was written: its place in line is now, on the
        /// caller's thread — not when its post reaches the UI thread.</summary>
        public long Issue() => Interlocked.Increment(ref _issued);

        /// <summary>The code set the status text itself.</summary>
        public void Explicit()
        {
            long ticket = Issue();
            lock (_lock) { _shown = ticket; _owner = ticket; }
            _afterThisTurn(() => { lock (_lock) { if (_owner == ticket) _owner = 0; } });
        }

        /// <summary>The bar was cleared (by its timer or by code). An empty bar
        /// has nothing for a late line to be older than, and no owner.</summary>
        public void Cleared()
        {
            lock (_lock) { _shown = 0; _owner = 0; }
        }

        /// <summary>
        /// May the log line holding <paramref name="ticket"/> be copied to the bar
        /// now? If so, the bar is recorded as showing it.
        /// </summary>
        public bool TryMirror(long ticket, LogLevel level)
        {
            lock (_lock)
            {
                if (ticket < _shown) return false;                        // older than what is showing
                if (_owner != 0 && level == LogLevel.Info) return false;  // a detail of the line just set
                _shown = ticket;
                return true;
            }
        }
    }
}
