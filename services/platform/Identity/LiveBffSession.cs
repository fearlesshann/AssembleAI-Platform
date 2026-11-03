using System.Security.Claims;
using AssembleAI.Platform.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace AssembleAI.Platform.Identity;

// Set only by a successful ticket-store read made by the Cookie handler. It is
// never read from request headers or claims supplied by a browser.
internal sealed record LiveBffSessionReference(string Key);

/// <summary>
/// Captures an authenticated BFF reference for a running request. CheckAsync
/// deliberately bypasses the Cookie handler's per-request authentication cache.
/// Key is server-only material and must not be returned or logged.
/// </summary>
public sealed class LiveBffSession
{
    private readonly ITicketStore store;
    private readonly TimeProvider clock;
    private readonly Guid tenant;

    private LiveBffSession(ITicketStore store, TimeProvider clock, string key, ClaimsPrincipal principal,
        SubjectIdentity subject, Guid tenant, DateTimeOffset expiresAt)
    {
        this.store = store;
        this.clock = clock;
        this.tenant = tenant;
        Key = key;
        Principal = new ClaimsPrincipal(principal.Identities.Select(identity => identity.Clone()));
        Subject = subject;
        ExpiresAt = expiresAt;
    }

    public string Key { get; }
    public ClaimsPrincipal Principal { get; }
    public SubjectIdentity Subject { get; }
    public DateTimeOffset ExpiresAt { get; }

    public static async Task<LiveBffSession?> CaptureAsync(HttpContext context, Guid expectedTenant,
        CancellationToken token = default)
    {
        if (!token.CanBeCanceled) token = context.RequestAborted;
        token.ThrowIfCancellationRequested();
        if (expectedTenant == Guid.Empty) return null;
        var options = context.RequestServices.GetService<IOptionsMonitor<CookieAuthenticationOptions>>()?.Get(BffAuthentication.CookieScheme);
        if (options?.SessionStore is not { } store) return null;
        var authenticated = await context.AuthenticateAsync(BffAuthentication.CookieScheme);
        token.ThrowIfCancellationRequested();
        var reference = context.Features.Get<LiveBffSessionReference>();
        var clock = context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
        if (!authenticated.Succeeded || authenticated.Principal is null || authenticated.Properties is null
            || reference is null || string.IsNullOrEmpty(reference.Key)
            || TrustedSubject.Read(authenticated.Principal) is not { } subject
            || TenantSession.ReadSelectedTenant(authenticated.Properties) != expectedTenant
            || authenticated.Properties.ExpiresUtc is not { } expires || expires <= clock.GetUtcNow()) return null;
        var captured = new LiveBffSession(store, clock, reference.Key, authenticated.Principal, subject, expectedTenant, expires);
        // A logout or tenant change may have committed since middleware first
        // authenticated this request. Do not capture an already-stale result.
        return await captured.CheckAsync(token) ? captured : null;
    }

    public async Task<bool> CheckAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (clock.GetUtcNow() >= ExpiresAt) return false;
        var current = await store.RetrieveAsync(Key, token);
        token.ThrowIfCancellationRequested();
        return current is not null && current.AuthenticationScheme == BffAuthentication.CookieScheme
            && TrustedSubject.Read(current.Principal) == Subject
            && TenantSession.ReadSelectedTenant(current.Properties) == tenant
            && current.Properties.ExpiresUtc == ExpiresAt && clock.GetUtcNow() < ExpiresAt;
    }
}
