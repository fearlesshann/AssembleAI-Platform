namespace AssembleAI.Platform.Gateway;

/// <summary>
/// A call's non-extending time budget. Monotonic elapsed time consumes duration;
/// earlier absolute deadlines and UTC advancement can only shorten it. Once
/// observed, a UTC advancement cannot be undone by a later clock correction.
/// </summary>
public sealed class GatewayDeadline
{
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private long lastTimestamp;
    private DateTimeOffset effectiveUtc;
    private DateTimeOffset absoluteDeadline;
    private TimeSpan budget;

    public GatewayDeadline(TimeProvider clock, TimeSpan maximumDuration, DateTimeOffset absoluteDeadline)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (maximumDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        this.clock = clock;
        this.absoluteDeadline = absoluteDeadline.ToUniversalTime();
        effectiveUtc = clock.GetUtcNow().ToUniversalTime();
        lastTimestamp = clock.GetTimestamp();
        budget = Limit(maximumDuration, this.absoluteDeadline - effectiveUtc);
    }

    public TimeSpan Remaining(DateTimeOffset? tighterDeadline = null)
    {
        lock (gate)
        {
            if (budget == TimeSpan.Zero) return TimeSpan.Zero;
            var timestamp = clock.GetTimestamp();
            var elapsed = clock.GetElapsedTime(lastTimestamp, timestamp);
            lastTimestamp = timestamp;
            // TimeProvider timestamps are monotonic. Remain conservative even if a
            // custom provider violates that contract: negative elapsed adds no time.
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            budget = elapsed >= budget ? TimeSpan.Zero : budget - elapsed;

            // Keep an effective UTC timeline that advances with monotonic time.
            // This also protects a newly discovered drain deadline after UTC rollback.
            var untilMaximum = DateTimeOffset.MaxValue - effectiveUtc;
            effectiveUtc = elapsed >= untilMaximum ? DateTimeOffset.MaxValue : effectiveUtc.Add(elapsed);
            var utc = clock.GetUtcNow().ToUniversalTime();
            if (utc > effectiveUtc) effectiveUtc = utc;
            if (tighterDeadline is { } earlier && earlier < absoluteDeadline)
                absoluteDeadline = earlier.ToUniversalTime();
            budget = Limit(budget, absoluteDeadline - effectiveUtc);
            return budget;
        }
    }

    private static TimeSpan Limit(TimeSpan current, TimeSpan available) => available <= TimeSpan.Zero
        ? TimeSpan.Zero : available < current ? available : current;
}
