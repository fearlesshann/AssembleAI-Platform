namespace AssembleAI.Platform.Gateway;

/// <summary>
/// Instance-local, non-queuing admission control. A successful acquisition consumes
/// one fixed-window request and holds a concurrency slot until its lease is disposed.
/// Use a shared external limiter if deployment requires a cluster-wide quota.
/// </summary>
public sealed class GatewayAdmission
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly int perUserConcurrency;
    private readonly int globalConcurrency;
    private readonly int requestsPerMinute;
    private readonly int maximumPartitions;
    private readonly Dictionary<(Guid Tenant, Guid User), Partition> partitions = [];
    private int active;

    public GatewayAdmission(TimeProvider clock, int perUserConcurrency = 4, int globalConcurrency = 64,
        int requestsPerMinute = 60, int maximumPartitions = 10000)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(perUserConcurrency, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(globalConcurrency, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(requestsPerMinute, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPartitions, 1);
        this.clock = clock;
        this.perUserConcurrency = perUserConcurrency;
        this.globalConcurrency = globalConcurrency;
        this.requestsPerMinute = requestsPerMinute;
        this.maximumPartitions = maximumPartitions;
    }

    public int PartitionCount { get { lock (gate) return partitions.Count; } }

    public IDisposable? TryAcquire(Guid tenantId, Guid userId)
    {
        if (tenantId == Guid.Empty || userId == Guid.Empty) return null;
        lock (gate)
        {
            if (active >= globalConcurrency) return null;
            var now = clock.GetTimestamp();
            var key = (tenantId, userId);
            if (!partitions.TryGetValue(key, out var partition))
            {
                if (partitions.Count >= maximumPartitions) ReclaimExpiredIdlePartitions(now);
                if (partitions.Count >= maximumPartitions) return null;
                partition = new Partition(now);
                partitions.Add(key, partition);
            }
            if (partition.Active >= perUserConcurrency) return null;
            if (Expired(partition, now))
            {
                partition.StartedAt = now;
                partition.Requests = 0;
                // Active requests survive renewal; they still occupy concurrency slots.
            }
            if (partition.Requests >= requestsPerMinute) return null;
            var lease = new AdmissionLease(this, partition);
            partition.Requests++;
            partition.Active++;
            active++;
            return lease;
        }
    }

    private bool Expired(Partition partition, long now) => clock.GetElapsedTime(partition.StartedAt, now) >= Window;

    private void ReclaimExpiredIdlePartitions(long now)
    {
        // Never evict an active partition or a live rate window to make room.
        // Sweep only at capacity; the work and temporary list are configuration-bounded.
        var expired = new List<(Guid Tenant, Guid User)>();
        foreach (var (key, value) in partitions)
            if (value.Active == 0 && Expired(value, now)) expired.Add(key);
        foreach (var key in expired) partitions.Remove(key);
    }

    private void Release(Partition partition)
    {
        lock (gate)
        {
            partition.Active--;
            active--;
        }
    }

    private sealed class Partition(long startedAt)
    {
        public long StartedAt = startedAt;
        public int Requests;
        public int Active;
    }

    private sealed class AdmissionLease(GatewayAdmission owner, Partition partition) : IDisposable
    {
        private GatewayAdmission? remainingOwner = owner;
        public void Dispose() => Interlocked.Exchange(ref remainingOwner, null)?.Release(partition);
    }
}
