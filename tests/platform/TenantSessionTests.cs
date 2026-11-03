using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Identity;
using AssembleAI.Platform.Identity.Sessions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssembleAI.Platform.SmokeTests;

public static class TenantSessionTests
{
    public static async Task RunAsync()
    {
        await VerifyDisabled();
        await using var provider = await OidcFixture.StartAsync();
        var origin = FreeOrigin();
        var clock = new TenantClock();
        var authorization = new TenantAuthorization(provider.Authority);
        await using var app = PlatformApplication.Build([
            "--environment", "Development", "--urls", origin,
            "--Authentication:Bff:Enabled", "true",
            "--Authentication:Bff:Authority", provider.Authority,
            "--Authentication:Bff:ClientId", OidcFixture.ClientId,
            "--Authentication:Bff:ClientSecret", OidcFixture.ClientSecret,
            "--Authentication:Bff:PublicOrigin", origin
        ], builder =>
        {
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.AddSingleton<IAuthorizationStore>(authorization);
        });
        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(BffAuthentication.CookieScheme);
        var tickets = new ControlledTickets(options.SessionStore!);
        options.SessionStore = tickets;
        // Test-only routes exercise the production helper without registering
        // a pretend business API or authentication mechanism in the platform.
        app.MapGet("/tenant-tests/{tenantId:guid}", async (Guid tenantId, HttpContext context, IAuthorizationStore store) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var access = await TenantSession.ReadAccessAsync(context, tenantId, store, context.RequestAborted);
            return access is null ? (IResult)Results.StatusCode(403)
                : Results.Ok(new { access.TenantId, access.AuthorizationVersion });
        }).RequireAuthorization(IdentityEndpoints.IdentityPolicy);
        app.MapGet("/tenant-tests/cancelled/{tenantId:guid}", async (Guid tenantId, HttpContext context, IAuthorizationStore store) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await TenantSession.ReadAccessAsync(context, tenantId, store, cancelled.Token); }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { return Results.StatusCode(499); }
            return Results.StatusCode(500);
        }).RequireAuthorization(IdentityEndpoints.IdentityPolicy);
        await app.StartAsync();
        try
        {
            var cookies = new CookieContainer();
            using var client = Client(origin, cookies);
            using (var anonymous = await client.GetAsync("/bff/tenant"))
                await Expect(anonymous, HttpStatusCode.Unauthorized);
            using (var anonymous = await Switch(client, authorization.TenantA, origin, null))
                await Expect(anonymous, HttpStatusCode.Unauthorized);
            await Login(client);
            using (var unselected = await client.GetAsync("/bff/tenant"))
                await ExpectTenant(unselected, null, null);
            var reads = authorization.Reads;
            using (var unselected = await client.GetAsync("/tenant-tests/" + authorization.TenantA))
                await Expect(unselected, HttpStatusCode.Forbidden);
            Assert(authorization.Reads == reads, "A request cannot implicitly select its requested tenant or query its authorization.");
            var originalKey = tickets.LastStoredKey!;
            var original = (await tickets.RetrieveAsync(originalKey))!;
            var originalCookie = cookies.GetCookies(new Uri(origin))["assembleai-session"]!.Value;
            await VerifySwitches(client, origin, cookies, originalCookie, options, tickets, authorization);
            await VerifyUnavailable(client, origin, tickets, authorization);
            await VerifyLogoutRace(origin, tickets, authorization);
            await VerifyAbsoluteLifetime(client, origin, clock, tickets, authorization, originalKey, original);
        }
        finally { await app.StopAsync(); }
        Console.WriteLine("PASS: BFF tenant selection: OIDC identity, Origin/CSRF, fresh membership, server-side persistence, revocation, storage failure, absolute expiry and logout/renew race.");
    }

    private static async Task VerifyDisabled()
    {
        await using var app = PlatformApplication.Build([
            "--environment", "Development", "--urls", "http://127.0.0.1:0", "--Authentication:Bff:Enabled", "false"
        ], builder => builder.Logging.ClearProviders());
        await app.StartAsync();
        try
        {
            using var client = Client(app.Urls.Single(), new CookieContainer());
            using (var get = await client.GetAsync("/bff/tenant"))
                await Expect(get, HttpStatusCode.Unauthorized);
            using (var post = await Switch(client, Guid.NewGuid(), client.BaseAddress!.GetLeftPart(UriPartial.Authority), null))
                await Expect(post, HttpStatusCode.Unauthorized);
        }
        finally { await app.StopAsync(); }
    }

    private static async Task VerifySwitches(HttpClient client, string origin, CookieContainer cookies,
        string originalCookie, CookieAuthenticationOptions options, ControlledTickets tickets, TenantAuthorization authorization)
    {
        var csrf = await Csrf(client);
        foreach (var (requestOrigin, requestToken) in new (string?, string?)[]
        {
            (origin, null), (null, csrf), ("https://foreign.example", csrf), (origin, "incorrect-token")
        })
        {
            using var refused = await Switch(client, authorization.TenantA, requestOrigin, requestToken);
            await Expect(refused, HttpStatusCode.Forbidden);
        }
        foreach (var payload in new object[] { new { tenantId = "not-a-uuid" }, new { tenantId = Guid.Empty }, new { unexpected = "missing tenant" } })
        {
            using var refused = await Post(client, "/bff/tenant", JsonContent.Create(payload), origin, csrf);
            await Expect(refused, HttpStatusCode.BadRequest);
        }
        using (var untouched = await client.GetAsync("/bff/tenant"))
            await ExpectTenant(untouched, null, null);
        using (var selected = await Switch(client, authorization.TenantA, origin, csrf))
            await ExpectTenant(selected, authorization.TenantA, authorization.VersionA);

        using (var correctTenant = await client.GetAsync("/tenant-tests/" + authorization.TenantA))
            await ExpectTenant(correctTenant, authorization.TenantA, authorization.VersionA);
        var reads = authorization.Reads;
        using (var mismatchedTenant = await client.GetAsync("/tenant-tests/" + authorization.TenantB))
            await Expect(mismatchedTenant, HttpStatusCode.Forbidden);
        Assert(authorization.Reads == reads, "A different explicit target cannot silently replace the session's selected tenant.");
        using (var cancelled = await client.GetAsync("/tenant-tests/cancelled/" + authorization.TenantA))
            await Expect(cancelled, (HttpStatusCode)499);

        var writes = tickets.Writes;
        using (var selected = await client.GetAsync("/bff/tenant"))
        {
            await ExpectTenant(selected, authorization.TenantA, authorization.VersionA);
            Assert(!selected.Headers.Contains("Set-Cookie"), "Reading the tenant must not replace the session cookie.");
        }
        Assert(tickets.Writes == writes, "GET tenant must not mutate the server-side ticket.");
        using (var refused = await Switch(client, Guid.NewGuid(), origin, csrf))
            await Expect(refused, HttpStatusCode.Forbidden);
        using (var selected = await client.GetAsync("/bff/tenant"))
            await ExpectTenant(selected, authorization.TenantA, authorization.VersionA);
        Assert(tickets.Writes == writes, "A refused tenant switch cannot alter the ticket.");

        authorization.VersionA++;
        using (var selected = await client.GetAsync("/bff/tenant"))
            await ExpectTenant(selected, authorization.TenantA, authorization.VersionA);
        authorization.RevokeA = true;
        using (var refused = await client.GetAsync("/bff/tenant"))
            await Expect(refused, HttpStatusCode.Forbidden);
        using (var refused = await Switch(client, authorization.TenantA, origin, csrf))
            await Expect(refused, HttpStatusCode.Forbidden);
        Assert(tickets.Writes == writes, "Membership revocation cannot trigger a hidden GET session rewrite.");
        using (var switched = await Switch(client, authorization.TenantB, origin, csrf))
            await ExpectTenant(switched, authorization.TenantB, authorization.VersionB);
        authorization.RevokeA = false;

        var staleTabCookies = new CookieContainer();
        staleTabCookies.Add(new Uri(origin), new Cookie("assembleai-session", originalCookie, "/"));
        using var staleTab = Client(origin, staleTabCookies);
        using (var selected = await staleTab.GetAsync("/bff/tenant"))
            await ExpectTenant(selected, authorization.TenantB, authorization.VersionB);
        using (var staleTarget = await staleTab.GetAsync("/tenant-tests/" + authorization.TenantA))
            await Expect(staleTarget, HttpStatusCode.Forbidden);
        using (var session = await staleTab.GetAsync("/bff/session"))
        {
            using var json = JsonDocument.Parse(await Expect(session, HttpStatusCode.OK));
            Assert(json.RootElement.GetProperty("sub").GetString() == OidcFixture.Subject,
                "Switching tenants must preserve the original OIDC subject.");
        }
        Assert(tickets.StoredCount == 1, "Tenant switching must renew the existing reference rather than create another session.");
        var cookie = cookies.GetCookies(new Uri(origin))["assembleai-session"]!;
        var reference = options.TicketDataFormat.Unprotect(cookie.Value);
        Assert(cookie.HttpOnly && reference is not null && reference.Principal.Claims.Count() == 1
            && !reference.Properties.GetTokens().Any()
            && !reference.Properties.Items.Values.Contains(authorization.TenantB.ToString()),
            "The browser cookie must retain only the opaque reference, not tenant authorization or OIDC tokens.");
    }

    private static async Task VerifyUnavailable(HttpClient client, string origin, ControlledTickets tickets, TenantAuthorization authorization)
    {
        var csrf = await Csrf(client);
        var writes = tickets.Writes;
        authorization.Unavailable = true;
        try
        {
            using (var failed = await client.GetAsync("/bff/tenant"))
                await Expect(failed, HttpStatusCode.ServiceUnavailable);
            using (var failed = await Switch(client, authorization.TenantA, origin, csrf))
                await Expect(failed, HttpStatusCode.ServiceUnavailable);
            Assert(tickets.Writes == writes, "Authorization failures cannot update a selected tenant.");
        }
        finally { authorization.Unavailable = false; }
        tickets.Unavailable = true;
        try
        {
            using var failed = await client.GetAsync("/bff/tenant");
            await Expect(failed, HttpStatusCode.ServiceUnavailable);
        }
        finally { tickets.Unavailable = false; }
        using var unchanged = await client.GetAsync("/bff/tenant");
        await ExpectTenant(unchanged, authorization.TenantB, authorization.VersionB);
    }

    private static async Task VerifyLogoutRace(string origin, ControlledTickets tickets, TenantAuthorization authorization)
    {
        var cookies = new CookieContainer();
        using var switchingClient = Client(origin, cookies);
        await Login(switchingClient);
        var key = tickets.LastStoredKey!;
        var csrf = await Csrf(switchingClient);
        var exitingCookies = new CookieContainer();
        exitingCookies.Add(new Uri(origin), new Cookie("assembleai-session",
            cookies.GetCookies(new Uri(origin))["assembleai-session"]!.Value, "/"));
        using var exitingClient = Client(origin, exitingCookies);
        var exitingCsrf = await Csrf(exitingClient);
        var storedCount = tickets.StoredCount;
        tickets.BlockRenewFor(key);
        var switching = Switch(switchingClient, authorization.TenantA, origin, csrf);
        try
        {
            await tickets.RenewEntered.WaitAsync(TimeSpan.FromSeconds(10));
            using var exited = await Post(exitingClient, "/bff/logout", null, origin, exitingCsrf);
            await Expect(exited, HttpStatusCode.NoContent);
        }
        finally { tickets.ReleaseRenew(); }
        using (var switched = await switching)
            Assert(switched.StatusCode is HttpStatusCode.OK or HttpStatusCode.Unauthorized,
                "A switch racing logout may finish its already-authorized response or observe revocation.");
        Assert(await tickets.RetrieveAsync(key) is null && tickets.StoredCount == storedCount,
            "A late tenant update must not recreate a logged-out session key.");
        using var rejected = await switchingClient.GetAsync("/bff/tenant");
        await Expect(rejected, HttpStatusCode.Unauthorized);
    }

    private static async Task VerifyAbsoluteLifetime(HttpClient client, string origin, TenantClock clock,
        ControlledTickets tickets, TenantAuthorization authorization, string key, AuthenticationTicket original)
    {
        clock.Advance(TimeSpan.FromHours(7));
        var csrf = await Csrf(client);
        using (var selected = await Switch(client, authorization.TenantA, origin, csrf))
            await ExpectTenant(selected, authorization.TenantA, authorization.VersionA);
        var updated = await tickets.RetrieveAsync(key);
        Assert(updated is not null && updated.Properties.IssuedUtc == original.Properties.IssuedUtc
            && updated.Properties.ExpiresUtc == original.Properties.ExpiresUtc,
            "A tenant switch must preserve both the original issue time and absolute eight-hour deadline.");
        Assert(updated!.Properties.GetTokenValue("access_token") == OidcFixture.AccessToken
            && updated.Properties.GetTokenValue("refresh_token") == OidcFixture.RefreshToken,
            "Tenant selection must preserve backend-only OIDC credentials.");
        clock.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(1)));
        using (var expired = await client.GetAsync("/bff/tenant"))
            await Expect(expired, HttpStatusCode.Unauthorized);
        using (var expired = await Switch(client, authorization.TenantB, origin, csrf))
            await Expect(expired, HttpStatusCode.Unauthorized);
    }

    private static async Task Login(HttpClient client)
    {
        using var challenge = await client.GetAsync("/bff/login?returnUrl=/bff/session");
        Assert(challenge.StatusCode == HttpStatusCode.Redirect, "Tenant tests require the real BFF OIDC challenge.");
        using var authorization = await client.GetAsync(challenge.Headers.Location);
        Assert(authorization.StatusCode == HttpStatusCode.Redirect, "OIDC fixture must issue a code.");
        using var callback = await client.GetAsync(authorization.Headers.Location);
        Assert(callback.StatusCode == HttpStatusCode.Redirect, "A validated OIDC callback must create the tenant-test session.");
    }

    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/bff/csrf");
        var body = await Expect(response, HttpStatusCode.OK);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("requestToken").GetString()!;
    }

    private static Task<HttpResponseMessage> Switch(HttpClient client, Guid tenant, string? origin, string? csrf) =>
        Post(client, "/bff/tenant", JsonContent.Create(new { tenantId = tenant }), origin, csrf);

    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, HttpContent? content, string? origin, string? csrf)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (csrf is not null) request.Headers.Add(BffEndpoints.CsrfHeaderName, csrf);
        return await client.SendAsync(request);
    }

    private static async Task ExpectTenant(HttpResponseMessage response, Guid? tenant, long? version)
    {
        var body = await Expect(response, HttpStatusCode.OK);
        using var json = JsonDocument.Parse(body);
        var actualTenant = json.RootElement.GetProperty("tenantId");
        var actualVersion = json.RootElement.GetProperty("authorizationVersion");
        Assert(tenant is null ? actualTenant.ValueKind == JsonValueKind.Null : actualTenant.GetGuid() == tenant,
            "The selected tenant must come from the current server-side session.");
        Assert(version is null ? actualVersion.ValueKind == JsonValueKind.Null : actualVersion.GetInt64() == version,
            "Displayed policy version must come from a fresh authorization read.");
    }

    private static async Task<string> Expect(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert(response.StatusCode == status, $"Tenant endpoint expected {status}, got {response.StatusCode}.");
        Assert(response.Headers.CacheControl?.NoStore == true, "Tenant responses must prohibit caching, including refusals.");
        var body = await response.Content.ReadAsStringAsync();
        Assert(!body.Contains(OidcFixture.AccessToken, StringComparison.Ordinal)
            && !body.Contains(OidcFixture.RefreshToken, StringComparison.Ordinal), "Tenant responses cannot disclose OIDC tokens.");
        return body;
    }

    private static HttpClient Client(string origin, CookieContainer cookies) => new(new HttpClientHandler
    {
        AllowAutoRedirect = false, CookieContainer = cookies
    }) { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(15) };

    private static string FreeOrigin()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TenantClock : TimeProvider
    {
        private TimeSpan offset;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.Add(offset);
        public void Advance(TimeSpan duration) => offset += duration;
    }

    private sealed class ControlledTickets(ITicketStore inner) : ITicketStore
    {
        private string? blockedKey;
        private TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Unavailable { get; set; }
        public int StoredCount { get; private set; }
        public int Writes { get; private set; }
        public string? LastStoredKey { get; private set; }
        public Task RenewEntered => entered.Task;

        public void BlockRenewFor(string key)
        {
            blockedKey = key;
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseRenew() => release.TrySetResult();
        public Task<string> StoreAsync(AuthenticationTicket ticket) => StoreAsync(ticket, CancellationToken.None);
        public Task<string> StoreAsync(AuthenticationTicket ticket, HttpContext context, CancellationToken cancellationToken = default) => StoreAsync(ticket, cancellationToken);
        public async Task<string> StoreAsync(AuthenticationTicket ticket, CancellationToken cancellationToken)
        {
            if (Unavailable) throw new BffSessionStoreException();
            var key = await inner.StoreAsync(ticket, cancellationToken);
            LastStoredKey = key;
            StoredCount++;
            Writes++;
            return key;
        }
        public Task RenewAsync(string key, AuthenticationTicket ticket) => RenewAsync(key, ticket, CancellationToken.None);
        public Task RenewAsync(string key, AuthenticationTicket ticket, HttpContext context, CancellationToken cancellationToken = default) => RenewAsync(key, ticket, cancellationToken);
        public async Task RenewAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken)
        {
            if (Unavailable) throw new BffSessionStoreException();
            if (key == blockedKey)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            await inner.RenewAsync(key, ticket, cancellationToken);
            Writes++;
        }
        public Task<AuthenticationTicket?> RetrieveAsync(string key) => RetrieveAsync(key, CancellationToken.None);
        public Task<AuthenticationTicket?> RetrieveAsync(string key, HttpContext context, CancellationToken cancellationToken = default) => RetrieveAsync(key, cancellationToken);
        public Task<AuthenticationTicket?> RetrieveAsync(string key, CancellationToken cancellationToken)
        {
            if (Unavailable) throw new BffSessionStoreException();
            return inner.RetrieveAsync(key, cancellationToken);
        }
        public Task RemoveAsync(string key) => RemoveAsync(key, CancellationToken.None);
        public Task RemoveAsync(string key, HttpContext context, CancellationToken cancellationToken = default) => RemoveAsync(key, cancellationToken);
        public async Task RemoveAsync(string key, CancellationToken cancellationToken)
        {
            if (Unavailable) throw new BffSessionStoreException();
            await inner.RemoveAsync(key, cancellationToken);
            Writes++;
        }
    }

    private sealed class TenantAuthorization(string issuer) : IAuthorizationStore
    {
        public Guid TenantA { get; } = Guid.NewGuid();
        public Guid TenantB { get; } = Guid.NewGuid();
        public Guid User { get; } = Guid.NewGuid();
        public bool RevokeA { get; set; }
        public bool Unavailable { get; set; }
        public long VersionA { get; set; } = 10;
        public long VersionB { get; set; } = 20;
        public int Reads { get; private set; }

        public async Task<IReadOnlyList<TenantMembership>> ListTenantsAsync(SubjectIdentity identity, CancellationToken cancellationToken = default)
        {
            var result = new List<TenantMembership>();
            foreach (var tenant in new[] { TenantA, TenantB })
                if (await ReadAccessAsync(identity, tenant, cancellationToken) is not null)
                    result.Add(new(tenant, "Fixture tenant"));
            return result;
        }

        public Task<AccessSnapshot?> ReadAccessAsync(SubjectIdentity identity, Guid tenantId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            if (Unavailable) throw new AuthorizationStoreUnavailableException();
            if (identity.Issuer != issuer || identity.Subject != OidcFixture.Subject
                || (tenantId != TenantA && tenantId != TenantB) || (tenantId == TenantA && RevokeA))
                return Task.FromResult<AccessSnapshot?>(null);
            return Task.FromResult<AccessSnapshot?>(new(User, tenantId, tenantId == TenantA ? VersionA : VersionB,
                [], new HashSet<Guid>()));
        }
    }
}
