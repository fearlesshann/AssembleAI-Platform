using System.Security.Cryptography;
using System.Text;

namespace AssembleAI.Platform.Models;

/// <summary>Instance-local model capabilities, owned by a live gateway invocation.
/// Only digests are indexed; callers cannot supply identity or widen the allowlist.</summary>
public sealed class ModelDelegationRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly TimeProvider clock;
    private readonly int maximumEntries;

    public ModelDelegationRegistry(TimeProvider clock, int maximumEntries = 4096)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (maximumEntries is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        this.clock = clock; this.maximumEntries = maximumEntries;
    }

    public Lease? Register(ModelCaller caller, IReadOnlyCollection<string> modelIds)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (modelIds is null || modelIds.Count is < 1 or > 100 || caller.Subject is null
            || caller.UserId == Guid.Empty || caller.TenantId == Guid.Empty || caller.CheckAsync is null
            || caller.ExpiresAt <= clock.GetUtcNow() || caller.Lifetime.IsCancellationRequested) return null;
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in modelIds) if (!ModelGatewayCatalog.ValidId(id) || !allowed.Add(id)) return null;
        Entry[] expired;
        lock (gate) expired = entries.Values.Where(entry => !entry.Alive).ToArray();
        foreach (var entry in expired) entry.Revoke();
        var capability = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var key = Digest(capability);
        var created = new Entry(this, key, caller, allowed);
        lock (gate)
        {
            if (entries.Count >= maximumEntries) { created.Revoke(); return null; }
            // A collision cannot reuse or replace another active invocation.
            if (!entries.TryAdd(key, created)) { created.Revoke(); return null; }
        }
        created.ObserveCancellation();
        if (!created.Alive) { created.Revoke(); return null; }
        return new Lease(capability, created.Revoke);
    }

    public ModelCaller? Resolve(string capability, string modelId)
    {
        if (capability is not { Length: 64 } || capability.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            || !ModelGatewayCatalog.ValidId(modelId)) return null;
        Entry? entry;
        lock (gate) entries.TryGetValue(Digest(capability), out entry);
        if (entry is null) return null;
        if (!entry.Alive) { entry.Revoke(); return null; }
        return entry.Allows(modelId) ? entry.Caller : null;
    }

    private void Remove(string key, Entry entry)
    {
        lock (gate)
            if (entries.TryGetValue(key, out var existing) && ReferenceEquals(existing, entry)) entries.Remove(key);
    }
    private static string Digest(string capability) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(capability)));

    public sealed class Lease : IDisposable
    {
        private readonly Action revoke;
        internal Lease(string capability, Action revoke) { Capability = capability; this.revoke = revoke; }
        public string Capability { get; }
        public void Dispose() => revoke();
    }

    private sealed class Entry
    {
        private readonly object gate = new();
        private readonly ModelDelegationRegistry owner;
        private readonly string key;
        private readonly ModelCaller original;
        private readonly HashSet<string> allowed;
        private readonly CancellationTokenSource stop = new();
        private CancellationTokenRegistration parentRegistration;
        private int active = 1;
        public Entry(ModelDelegationRegistry owner, string key, ModelCaller original, HashSet<string> allowed)
        {
            this.owner = owner; this.key = key; this.original = original; this.allowed = allowed;
            Caller = original with { Lifetime = stop.Token, CheckAsync = Check };
        }
        public ModelCaller Caller { get; }
        public bool Alive => Volatile.Read(ref active) == 1 && !original.Lifetime.IsCancellationRequested
            && owner.clock.GetUtcNow() < original.ExpiresAt;
        public bool Allows(string model) => allowed.Contains(model);
        public void ObserveCancellation()
        {
            var registration = original.Lifetime.Register(static value => ((Entry)value!).Revoke(), this);
            lock (gate)
            {
                if (Volatile.Read(ref active) == 1) { parentRegistration = registration; return; }
            }
            registration.Dispose();
        }
        private async Task<bool> Check(CancellationToken token)
        {
            if (!Alive) { Revoke(); return false; }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, Caller.Lifetime);
            var current = await original.CheckAsync(linked.Token);
            if (!current || !Alive) { Revoke(); return false; }
            linked.Token.ThrowIfCancellationRequested();
            return true;
        }
        public void Revoke()
        {
            if (Interlocked.Exchange(ref active, 0) == 0) return;
            owner.Remove(key, this);
            try { stop.Cancel(); }
            finally
            {
                CancellationTokenRegistration registration;
                lock (gate) { registration = parentRegistration; parentRegistration = default; }
                registration.Dispose();
                stop.Dispose();
            }
        }
    }
}
