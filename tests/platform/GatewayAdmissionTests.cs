using System.Collections.Concurrent;
using AssembleAI.Platform.Gateway;

namespace AssembleAI.Platform.SmokeTests;

public static class GatewayAdmissionTests
{
    public static async Task RunAsync()
    {
        VerifyConfigurationAndKeys();
        VerifyPartitionLimitsAndWindow();
        VerifyGlobalConcurrencyAndIdempotentRelease();
        VerifyBoundedPartitionReclamation();
        VerifyClockAdjustments();
        await VerifyConcurrentAdmissions();
        Console.WriteLine("PASS: gateway admission: tenant/user isolation, fixed-window rate, global and per-user concurrency, idempotent release, bounded partitions and concurrent saturation.");
    }

    private static void VerifyConfigurationAndKeys()
    {
        var clock = new Clock();
        ExpectArgument(() => new GatewayAdmission(null!));
        ExpectArgument(() => new GatewayAdmission(clock, perUserConcurrency: 0));
        ExpectArgument(() => new GatewayAdmission(clock, globalConcurrency: -1));
        ExpectArgument(() => new GatewayAdmission(clock, requestsPerMinute: 0));
        ExpectArgument(() => new GatewayAdmission(clock, maximumPartitions: -1));
        var admission = new GatewayAdmission(clock);
        Assert(admission.TryAcquire(Guid.Empty, Guid.NewGuid()) is null
            && admission.TryAcquire(Guid.NewGuid(), Guid.Empty) is null && admission.PartitionCount == 0,
            "Empty tenant/user identifiers must not allocate partitions.");
    }

    private static void VerifyPartitionLimitsAndWindow()
    {
        var clock = new Clock();
        var admission = new GatewayAdmission(clock, perUserConcurrency: 2, globalConcurrency: 10, requestsPerMinute: 3);
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        using var first = Required(admission.TryAcquire(tenant, user));
        using var second = Required(admission.TryAcquire(tenant, user));
        Assert(admission.TryAcquire(tenant, user) is null, "An active user cannot exceed its concurrency limit.");
        using var otherTenant = Required(admission.TryAcquire(Guid.NewGuid(), user));
        using var otherUser = Required(admission.TryAcquire(tenant, Guid.NewGuid()));
        first.Dispose();
        using var third = Required(admission.TryAcquire(tenant, user));
        second.Dispose();
        Assert(admission.TryAcquire(tenant, user) is null, "Released concurrency must not reset the request rate.");
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert(admission.TryAcquire(tenant, user) is null, "The entire sixty-second window must elapse before rate renewal.");
        clock.Advance(TimeSpan.FromSeconds(1));
        using var nextWindow = Required(admission.TryAcquire(tenant, user));
        Assert(admission.TryAcquire(tenant, user) is null, "Renewing the rate window must retain outstanding concurrency.");
        third.Dispose();
        using var availableSlot = Required(admission.TryAcquire(tenant, user));
    }

    private static void VerifyGlobalConcurrencyAndIdempotentRelease()
    {
        var admission = new GatewayAdmission(new Clock(), perUserConcurrency: 2, globalConcurrency: 2);
        var tenant = Guid.NewGuid();
        using var first = Required(admission.TryAcquire(tenant, Guid.NewGuid()));
        using var second = Required(admission.TryAcquire(tenant, Guid.NewGuid()));
        Assert(admission.TryAcquire(Guid.NewGuid(), Guid.NewGuid()) is null && admission.PartitionCount == 2,
            "Global saturation must reject without allocating a new partition.");
        first.Dispose();
        first.Dispose();
        using var replacement = Required(admission.TryAcquire(tenant, Guid.NewGuid()));
        Assert(admission.TryAcquire(tenant, Guid.NewGuid()) is null, "Double disposal must release exactly one global slot.");
    }

    private static void VerifyBoundedPartitionReclamation()
    {
        var clock = new Clock();
        var admission = new GatewayAdmission(clock, perUserConcurrency: 1, globalConcurrency: 8, requestsPerMinute: 10, maximumPartitions: 2);
        var tenant = Guid.NewGuid();
        var activeUser = Guid.NewGuid();
        using var active = Required(admission.TryAcquire(tenant, activeUser));
        using var idle = Required(admission.TryAcquire(tenant, Guid.NewGuid()));
        idle.Dispose();
        Assert(admission.TryAcquire(tenant, Guid.NewGuid()) is null, "An idle partition with a live window cannot be evicted to bypass rate limits.");
        clock.Advance(TimeSpan.FromSeconds(60));
        using var reclaimed = Required(admission.TryAcquire(tenant, Guid.NewGuid()));
        Assert(admission.PartitionCount == 2 && admission.TryAcquire(tenant, activeUser) is null,
            "An expired active partition must retain its outstanding lease while expired idle partitions are reclaimed.");
        active.Dispose();
        using var next = Required(admission.TryAcquire(tenant, Guid.NewGuid()));
        Assert(admission.PartitionCount == 2, "Partition reclamation must preserve the configured memory bound.");
        idle.Dispose(); // The original entry has been evicted; this still cannot release someone else's slot.
        Assert(admission.TryAcquire(tenant, Guid.NewGuid()) is null, "Repeated disposal cannot evict or revive a newer partition.");
    }

    private static void VerifyClockAdjustments()
    {
        var clock = new Clock();
        var admission = new GatewayAdmission(clock, requestsPerMinute: 1);
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        Required(admission.TryAcquire(tenant, user)).Dispose();
        clock.UtcOffset = TimeSpan.FromDays(1);
        Assert(admission.TryAcquire(tenant, user) is null, "Wall-clock changes must not renew a fixed-rate window early.");
        clock.UtcOffset = TimeSpan.FromDays(-1);
        clock.Advance(TimeSpan.FromMinutes(1));
        using var renewed = Required(admission.TryAcquire(tenant, user));
    }

    private static async Task VerifyConcurrentAdmissions()
    {
        var clock = new Clock();
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        await Race(new GatewayAdmission(clock, perUserConcurrency: 4, globalConcurrency: 64, requestsPerMinute: 100),
            _ => (tenant, user), 4, "Per-user concurrency");
        await Race(new GatewayAdmission(clock, perUserConcurrency: 4, globalConcurrency: 7, requestsPerMinute: 100),
            _ => (Guid.NewGuid(), Guid.NewGuid()), 7, "Global concurrency");
        await Race(new GatewayAdmission(clock, perUserConcurrency: 100, globalConcurrency: 100, requestsPerMinute: 7),
            _ => (tenant, user), 7, "Per-user rate");
    }

    private static async Task Race(GatewayAdmission admission, Func<int, (Guid Tenant, Guid User)> key, int expected, string label)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leases = new ConcurrentBag<IDisposable>();
        var attempts = Enumerable.Range(0, 100).Select(index => Task.Run(async () =>
        {
            await start.Task;
            var (tenant, user) = key(index);
            var lease = admission.TryAcquire(tenant, user);
            if (lease is not null) leases.Add(lease);
        })).ToArray();
        try
        {
            start.SetResult();
            await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(10));
            Assert(leases.Count == expected, $"{label} must admit exactly {expected} out of 100 simultaneous attempts.");
        }
        finally { foreach (var lease in leases) lease.Dispose(); }
    }

    private static IDisposable Required(IDisposable? lease) => lease ?? throw new InvalidOperationException("A request within every configured bound must be admitted.");
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void ExpectArgument(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Invalid gateway limit configuration was accepted.");
    }
    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        public TimeSpan UtcOffset { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp()).Add(UtcOffset);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref timestamp, elapsed.Ticks);
    }
}
