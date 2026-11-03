using System.Security.Claims;
using AssembleAI.Platform.Identity;
using AssembleAI.Platform.Identity.Sessions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AssembleAI.Platform.SmokeTests;

public static class LiveBffSessionTests
{
    public static async Task RunAsync()
    {
        var clock = new SessionClock();
        ITicketStore store = new MemoryTicketStore(clock);
        var tenant = Guid.NewGuid();
        var expiry = clock.GetUtcNow().AddHours(1);
        var key = await store.StoreAsync(Ticket(tenant, expiry));
        var cached = new CachedCookieAuthentication(store, key);
        using var services = Services(store, clock, cached);
        var context = Context(services);
        var session = await LiveBffSession.CaptureAsync(context, tenant);
        Assert(session is not null && session.Key == key && session.Subject.Subject == "alice"
            && session.ExpiresAt == expiry && await session.CheckAsync(), "Capture must bind a real store reference and its original identity/expiry.");
        Assert(await LiveBffSession.CaptureAsync(context, Guid.NewGuid()) is null, "A requested tenant cannot replace the selected one.");
        await store.RenewAsync(key, Ticket(Guid.NewGuid(), expiry));
        Assert((await context.AuthenticateAsync(BffAuthentication.CookieScheme)).Succeeded,
            "This fixture must reproduce the Cookie handler's cached authenticated result.");
        Assert(!await session!.CheckAsync() && await LiveBffSession.CaptureAsync(context, tenant) is null,
            "A live tenant switch must invalidate a running request despite cached authentication.");
        await store.RenewAsync(key, Ticket(tenant, expiry, "bob"));
        Assert(!await session.CheckAsync(), "A reused reference cannot change the captured subject.");
        await store.RemoveAsync(key);
        Assert(!await session.CheckAsync() && await LiveBffSession.CaptureAsync(context, tenant) is null,
            "Logout must invalidate live and newly captured state even with cached authentication.");

        var otherKey = await store.StoreAsync(Ticket(tenant, expiry));
        using var missingFeature = Services(store, clock, new CachedCookieAuthentication(store, otherKey, attachFeature: false));
        Assert(await LiveBffSession.CaptureAsync(Context(missingFeature), tenant) is null,
            "An authenticated principal alone must not fabricate a session-store reference.");
        using var unauthenticated = Services(store, clock, new CachedCookieAuthentication(store, "missing"));
        Assert(await LiveBffSession.CaptureAsync(Context(unauthenticated), tenant) is null,
            "A missing stored ticket cannot establish a live BFF session.");
        Assert(await LiveBffSession.CaptureIdentityAsync(Context(missingFeature)) is null,
            "Global control-plane access still requires a real session-store reference.");
        var globalKey = await store.StoreAsync(Ticket(null, expiry));
        using var globalServices = Services(store, clock, new CachedCookieAuthentication(store, globalKey));
        var globalContext = Context(globalServices);
        var global = await LiveBffSession.CaptureIdentityAsync(globalContext);
        Assert(global is not null && await global.CheckAsync(), "A global actor does not need a selected tenant.");
        Assert(await LiveBffSession.CaptureAsync(globalContext, tenant) is null
            && await LiveBffSession.CaptureAsync(globalContext, Guid.Empty) is null,
            "Global identity does not imply a tenant selection.");
        await store.RenewAsync(globalKey, Ticket(tenant, expiry));
        Assert(!await global!.CheckAsync(), "An in-request tenant selection change invalidates the captured global snapshot.");
        await store.RemoveAsync(globalKey);
        Assert(await LiveBffSession.CaptureIdentityAsync(globalContext) is null,
            "Global capture cannot recover a logged-out cached principal.");

        using var validServices = Services(store, clock, new CachedCookieAuthentication(store, otherKey));
        var validContext = Context(validServices);
        var expiring = await LiveBffSession.CaptureAsync(validContext, tenant);
        Assert(expiring is not null, "The independent session must be valid before expiry.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ExpectCancellation(() => expiring!.CheckAsync(cancelled.Token));
        validContext.RequestAborted = cancelled.Token;
        await ExpectCancellation(() => LiveBffSession.CaptureAsync(validContext, tenant));
        validContext.RequestAborted = default;
        await store.RenewAsync(otherKey, Ticket(tenant, expiry.AddMinutes(-1)));
        Assert(!await expiring!.CheckAsync(), "A changed original expiry must invalidate the captured binding.");
        clock.Advance(TimeSpan.FromHours(1));
        Assert(!await expiring.CheckAsync(), "The original absolute deadline must stop a running request.");
        Console.WriteLine("PASS: live BFF session: store-derived reference, cached-auth bypass, logout/tenant/subject/expiry invalidation and cancellation.");
    }

    private static ServiceProvider Services(ITicketStore store, TimeProvider clock, IAuthenticationService authentication)
    {
        var services = new ServiceCollection();
        services.AddSingleton(clock);
        services.AddSingleton(authentication);
        services.Configure<CookieAuthenticationOptions>(BffAuthentication.CookieScheme, options => options.SessionStore = store);
        return services.BuildServiceProvider();
    }
    private static DefaultHttpContext Context(IServiceProvider services) => new() { RequestServices = services };
    private static AuthenticationTicket Ticket(Guid? tenant, DateTimeOffset expires, string subject = "alice") => new(
        new ClaimsPrincipal(new ClaimsIdentity([new("iss", "https://identity.example.test"), new("sub", subject)], BffAuthentication.CookieScheme)),
        new AuthenticationProperties(new Dictionary<string, string?> { [".assembleai.tenant"] = tenant?.ToString("D") }) { ExpiresUtc = expires },
        BffAuthentication.CookieScheme);
    private static async Task ExpectCancellation(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Live session checks must preserve caller cancellation.");
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class SessionClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }
    // Test-only authentication models the real handler's once-per-request cache;
    // HTTP/OIDC tests separately verify the actual handler and store integration.
    private sealed class CachedCookieAuthentication(ITicketStore store, string key, bool attachFeature = true) : IAuthenticationService
    {
        private AuthenticateResult? cached;
        public async Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            Assert(scheme == BffAuthentication.CookieScheme, "Capture must explicitly authenticate the BFF cookie scheme.");
            if (cached is not null) return cached;
            var ticket = attachFeature ? await store.RetrieveAsync(key, context, CancellationToken.None) : await store.RetrieveAsync(key);
            return cached = ticket is null ? AuthenticateResult.NoResult() : AuthenticateResult.Success(ticket);
        }
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
    }
}
