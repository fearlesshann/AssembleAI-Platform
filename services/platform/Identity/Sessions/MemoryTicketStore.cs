using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace AssembleAI.Platform.Identity.Sessions;

/// <summary>Development-only sessions with bounded storage and an absolute lifetime.</summary>
public sealed class MemoryTicketStore(TimeProvider clock) : ITicketStore
{
    private const int Capacity = 1024;
    private readonly object gate = new();
    private readonly Dictionary<string, SessionTicket.Snapshot> sessions = new(StringComparer.Ordinal);

    public Task<string> StoreAsync(AuthenticationTicket ticket) => StoreAsync(ticket, CancellationToken.None);

    public Task<string> StoreAsync(AuthenticationTicket ticket, HttpContext context, CancellationToken cancellationToken = default) =>
        StoreAsync(ticket, cancellationToken);

    public Task<string> StoreAsync(AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = clock.GetUtcNow();
            var snapshot = SessionTicket.Copy(ticket, now);
            // At most 1,024 entries are inspected, with no background timer or unbounded queue.
            foreach (var expired in sessions.Where(entry => entry.Value.ExpiresUtc <= now).Select(entry => entry.Key).ToArray())
                sessions.Remove(expired);
            var key = SessionTicket.NewKey();
            if (snapshot.ExpiresUtc <= now) return Task.FromResult(key);
            if (sessions.Count >= Capacity) throw new BffSessionStoreException();
            while (sessions.ContainsKey(key)) key = SessionTicket.NewKey();
            sessions.Add(key, snapshot);
            return Task.FromResult(key);
        }
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket) => RenewAsync(key, ticket, CancellationToken.None);

    public Task RenewAsync(string key, AuthenticationTicket ticket, HttpContext context, CancellationToken cancellationToken = default) =>
        RenewAsync(key, ticket, cancellationToken);

    public Task RenewAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sessions.TryGetValue(key, out var previous)) return Task.CompletedTask;
            var now = clock.GetUtcNow();
            if (previous.ExpiresUtc <= now)
            {
                sessions.Remove(key);
                return Task.CompletedTask;
            }
            // The same lock protects removal and renewal, so renewal cannot undo revocation.
            sessions[key] = SessionTicket.Copy(ticket, now, previous.ExpiresUtc);
            return Task.CompletedTask;
        }
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key) => RetrieveAsync(key, CancellationToken.None);

    public async Task<AuthenticationTicket?> RetrieveAsync(string key, HttpContext context, CancellationToken cancellationToken = default)
    {
        context.Features.Set<LiveBffSessionReference>(null);
        var ticket = await RetrieveAsync(key, cancellationToken);
        if (ticket is not null) context.Features.Set(new LiveBffSessionReference(key));
        return ticket;
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sessions.TryGetValue(key, out var snapshot)) return Task.FromResult<AuthenticationTicket?>(null);
            if (snapshot.ExpiresUtc <= clock.GetUtcNow())
            {
                sessions.Remove(key);
                return Task.FromResult<AuthenticationTicket?>(null);
            }
            return Task.FromResult(TicketSerializer.Default.Deserialize(snapshot.Bytes));
        }
    }

    public Task RemoveAsync(string key) => RemoveAsync(key, CancellationToken.None);

    public Task RemoveAsync(string key, HttpContext context, CancellationToken cancellationToken = default) =>
        RemoveAsync(key, cancellationToken);

    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sessions.Remove(key);
        }
        return Task.CompletedTask;
    }
}
