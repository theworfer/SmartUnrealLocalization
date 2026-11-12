using SmartUnrealLocalization.Services;
using Xunit;

namespace SmartUnrealLocalization.Tests;

/// <summary>
/// A debounce must mean ONE run per burst.
///
/// <para>The find bar and the text filter each hand-rolled one that added a new
/// Tick handler on every request and removed it only when it fired — so a burst
/// of N requests ran the work N times, all at once, when the timer finally
/// ticked. With the find bar open, a bulk edit asks for one rescan per changed
/// row, and each rescan covers the whole file: on a large file that is a
/// quadratic freeze. The 2026-09-09 reliability audit counted 1000 handlers
/// after 1000 requests. These tests drive the real <see cref="Debouncer{T}"/>
/// through a fake clock, so they need no running dispatcher.</para>
/// </summary>
public class DebouncerTests
{
    private sealed class FakeClock : IDebounceClock
    {
        private Action? _elapsed;
        public int Restarts { get; private set; }
        public bool Running { get; private set; }
        public int Subscribers => _elapsed?.GetInvocationList().Length ?? 0;

        public event Action? Elapsed
        {
            add => _elapsed += value;
            remove => _elapsed -= value;
        }

        public void Restart() { Restarts++; Running = true; }
        public void Stop() => Running = false;

        /// <summary>The interval has passed with no further request.</summary>
        public void Tick() { if (Running) _elapsed?.Invoke(); }
    }

    private static (Debouncer<bool> Debouncer, FakeClock Clock, List<bool> Runs) Build()
    {
        var clock = new FakeClock();
        var runs = new List<bool>();
        var debouncer = new Debouncer<bool>(clock, (pending, incoming) => pending || incoming, runs.Add);
        return (debouncer, clock, runs);
    }

    // ── the property the audit asked for ────────────────────────────────

    [Fact]
    public void A_thousand_requests_inside_one_window_run_the_work_once()
    {
        var (d, clock, runs) = Build();

        for (int i = 0; i < 1000; i++) d.Request(false);
        clock.Tick();

        Assert.Single(runs);
    }

    [Fact]
    public void The_timer_is_subscribed_once_however_many_requests_arrive()
    {
        var (d, clock, _) = Build();

        for (int i = 0; i < 1000; i++) d.Request(i % 2 == 0);

        // The old code left 1000 here.
        Assert.Equal(1, clock.Subscribers);
    }

    [Fact]
    public void Every_request_restarts_the_window()
    {
        var (d, clock, _) = Build();

        d.Request(false);
        d.Request(false);
        d.Request(false);

        Assert.Equal(3, clock.Restarts);
        Assert.True(clock.Running);
    }

    // ── what the single run is told ─────────────────────────────────────

    [Fact]
    public void A_pending_cursor_reset_outranks_a_keep()
    {
        // The query changed (reset) while rows were also being edited (keep):
        // the old cursor indexes a result set from a different query.
        var (d, clock, runs) = Build();

        d.Request(false);
        d.Request(true);
        d.Request(false);
        clock.Tick();

        Assert.Equal(new[] { true }, runs);
    }

    [Fact]
    public void A_burst_of_keeps_stays_a_keep()
    {
        // The old code's first callback reset the flag to true, so the second
        // one scrolled the grid although nobody had asked for that.
        var (d, clock, runs) = Build();

        for (int i = 0; i < 1000; i++) d.Request(false);
        clock.Tick();

        Assert.Equal(new[] { false }, runs);
    }

    [Fact]
    public void Each_window_starts_fresh()
    {
        var (d, clock, runs) = Build();

        d.Request(true);
        clock.Tick();
        d.Request(false);
        clock.Tick();

        // A reset from the first window must not leak into the second.
        Assert.Equal(new[] { true, false }, runs);
    }

    // ── cancelling and claiming ─────────────────────────────────────────

    [Fact]
    public void Cancel_drops_the_pending_run()
    {
        var (d, clock, runs) = Build();

        d.Request(true);
        d.Cancel();
        clock.Tick();

        Assert.Empty(runs);
        Assert.False(d.IsPending);
    }

    [Fact]
    public void A_synchronous_scan_claims_the_pending_request_with_its_reset()
    {
        var (d, clock, runs) = Build();

        d.Request(false);
        d.Request(true);
        bool claimed = d.TryTakePending(out bool reset);
        clock.Tick();

        Assert.True(claimed);
        Assert.True(reset);      // the reset is handed over, not lost
        Assert.Empty(runs);      // and the deferred run does not repeat the work
    }

    [Fact]
    public void Nothing_is_claimed_when_nothing_is_pending()
    {
        var (d, _, _) = Build();
        Assert.False(d.TryTakePending(out _));
    }

    [Fact]
    public void A_request_made_by_the_run_itself_opens_a_new_window()
    {
        var clock = new FakeClock();
        int runs = 0;
        Debouncer<bool>? d = null;
        d = new Debouncer<bool>(clock, (a, b) => a || b, _ =>
        {
            runs++;
            if (runs == 1) d!.Request(false);   // e.g. a rescan that edits rows
        });

        d.Request(false);
        clock.Tick();
        Assert.True(d.IsPending);
        clock.Tick();

        Assert.Equal(2, runs);
    }

    [Fact]
    public void The_plain_debouncer_coalesces_the_same_way()
    {
        var clock = new FakeClock();
        int runs = 0;
        var d = new Debouncer(clock, () => runs++);

        for (int i = 0; i < 1000; i++) d.Request();
        clock.Tick();

        Assert.Equal(1, runs);
        Assert.Equal(1, clock.Subscribers);
    }
}
