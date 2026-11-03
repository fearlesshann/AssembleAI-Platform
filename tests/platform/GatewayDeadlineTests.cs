using AssembleAI.Platform.Gateway;

namespace AssembleAI.Platform.SmokeTests;

public static class GatewayDeadlineTests
{
    public static async Task RunAsync()
    {
        VerifyInitialBounds();
        VerifyMonotonicBudget();
        VerifyBackwardUtcAndNewDrain();
        VerifyForwardUtcCannotBeUndone();
        VerifyOnlyEarlierDeadlinesApply();
        VerifyExpiryCannotRevive();
        await VerifyConcurrentReads();
        Console.WriteLine("PASS: gateway deadline: monotonic duration, absolute expiry, non-extending drain changes, UTC jumps and permanent exhaustion.");
    }

    private static void VerifyInitialBounds()
    {
        var clock = new Clock();
        Equal(new GatewayDeadline(clock, Seconds(10), clock.GetUtcNow().AddSeconds(20)).Remaining(), Seconds(10), "Call duration must bound a later lease expiry.");
        Equal(new GatewayDeadline(clock, Seconds(10), clock.GetUtcNow().AddSeconds(4)).Remaining(), Seconds(4), "An earlier lease expiry must bound call duration.");
        Equal(new GatewayDeadline(clock, Seconds(10), clock.GetUtcNow().AddSeconds(-1)).Remaining(), TimeSpan.Zero, "An already-expired absolute deadline must remain exhausted.");
        ExpectArgument(() => new GatewayDeadline(null!, Seconds(1), clock.GetUtcNow()));
        ExpectArgument(() => new GatewayDeadline(clock, TimeSpan.Zero, clock.GetUtcNow()));
        ExpectArgument(() => new GatewayDeadline(clock, Seconds(-1), clock.GetUtcNow()));
    }

    private static void VerifyMonotonicBudget()
    {
        var clock = new Clock();
        var deadline = new GatewayDeadline(clock, Seconds(10), clock.GetUtcNow().AddMinutes(1));
        clock.Advance(Seconds(2));
        Equal(deadline.Remaining(), Seconds(8), "Elapsed time must consume the maximum call duration.");
        clock.AdvanceMonotonic(Seconds(3));
        Equal(deadline.Remaining(), Seconds(5), "A paused UTC clock must not pause the maximum call duration.");
        Equal(deadline.Remaining(), Seconds(5), "Repeated reads without elapsed time must not consume the budget again.");
    }

    private static void VerifyBackwardUtcAndNewDrain()
    {
        var clock = new Clock();
        var initialUtc = clock.GetUtcNow();
        var deadline = new GatewayDeadline(clock, Seconds(100), initialUtc.AddSeconds(100));
        clock.Advance(Seconds(2));
        Equal(deadline.Remaining(), Seconds(98), "Initial elapsed time must be accounted for.");
        clock.MoveUtc(Seconds(-60));
        Equal(deadline.Remaining(), Seconds(98), "UTC rollback must not extend an established call budget.");
        clock.AdvanceMonotonic(Seconds(2));
        Equal(deadline.Remaining(initialUtc.AddSeconds(10)), Seconds(6), "A newly discovered drain deadline must not gain time from UTC rollback.");
    }

    private static void VerifyForwardUtcCannotBeUndone()
    {
        var clock = new Clock();
        var deadline = new GatewayDeadline(clock, Seconds(10), clock.GetUtcNow().AddSeconds(10));
        clock.Advance(Seconds(2));
        clock.MoveUtc(Seconds(7));
        Equal(deadline.Remaining(), Seconds(1), "UTC advancement may shorten an absolute lease deadline.");
        clock.MoveUtc(Seconds(-100));
        Equal(deadline.Remaining(), Seconds(1), "Rolling UTC back cannot undo a previously observed shortening.");
        clock.AdvanceMonotonic(Seconds(1));
        Equal(deadline.Remaining(), TimeSpan.Zero, "Elapsed monotonic time must exhaust the shortened deadline.");
    }

    private static void VerifyOnlyEarlierDeadlinesApply()
    {
        var clock = new Clock();
        var initialUtc = clock.GetUtcNow();
        var deadline = new GatewayDeadline(clock, Seconds(100), initialUtc.AddSeconds(120));
        clock.Advance(Seconds(3));
        Equal(deadline.Remaining(initialUtc.AddSeconds(30)), Seconds(27), "A new drain deadline must shorten the call.");
        Equal(deadline.Remaining(initialUtc.AddSeconds(110)), Seconds(27), "A later deadline must not undo the drain window.");
        clock.Advance(Seconds(1));
        Equal(deadline.Remaining(), Seconds(26), "Omitting a drain update must retain the earlier deadline.");
        Equal(deadline.Remaining(initialUtc.AddSeconds(9)), Seconds(5), "A second earlier deadline may shorten the budget again.");
    }

    private static void VerifyExpiryCannotRevive()
    {
        var clock = new Clock();
        var deadline = new GatewayDeadline(clock, Seconds(1), clock.GetUtcNow().AddHours(1));
        clock.AdvanceMonotonic(Seconds(2));
        Equal(deadline.Remaining(), TimeSpan.Zero, "Expired duration must clamp to zero.");
        clock.MoveUtc(TimeSpan.FromDays(-1));
        Equal(deadline.Remaining(DateTimeOffset.MaxValue), TimeSpan.Zero, "Neither a later expiry nor a UTC rollback can revive an expired call.");
        var atMaximum = new Clock(DateTimeOffset.MaxValue.AddSeconds(-1));
        var bounded = new GatewayDeadline(atMaximum, Seconds(10), DateTimeOffset.MaxValue);
        atMaximum.AdvanceMonotonic(Seconds(2));
        Equal(bounded.Remaining(), TimeSpan.Zero, "Absolute time arithmetic must saturate safely at the representable boundary.");
    }

    private static async Task VerifyConcurrentReads()
    {
        var clock = new Clock();
        var initialUtc = clock.GetUtcNow();
        var deadline = new GatewayDeadline(clock, Seconds(100), initialUtc.AddSeconds(100));
        clock.Advance(Seconds(1));
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() => deadline.Remaining(initialUtc.AddSeconds(2 + i)))));
        Equal(deadline.Remaining(), Seconds(1), "Concurrent changes must retain the earliest deadline without extending or double-consuming elapsed time.");
    }

    private static TimeSpan Seconds(int count) => TimeSpan.FromSeconds(count);
    private static void Equal(TimeSpan actual, TimeSpan expected, string message)
    {
        if (actual != expected) throw new InvalidOperationException($"{message} Expected {expected}, got {actual}.");
    }
    private static void ExpectArgument(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Invalid gateway deadline configuration was accepted.");
    }
    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        private DateTimeOffset utc;
        public Clock(DateTimeOffset? initial = null) => utc = initial ?? new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        public override DateTimeOffset GetUtcNow() => utc;
        public void Advance(TimeSpan amount) { AdvanceMonotonic(amount); MoveUtc(amount); }
        public void AdvanceMonotonic(TimeSpan amount) => Interlocked.Add(ref timestamp, amount.Ticks);
        public void MoveUtc(TimeSpan amount) => utc = utc.Add(amount);
    }
}
