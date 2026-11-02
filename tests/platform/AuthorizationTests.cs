using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssembleAI.Platform.SmokeTests;

/// <summary>Real HTTP and authorization-pipeline tests; the store and validated login are test fixtures.</summary>
public static class AuthorizationTests
{
    private const string Issuer = "https://identity.example.test/realms/manufacturing";
    private const string OtherIssuer = "https://other-identity.example.test/realms/manufacturing";
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid FactoryA1 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid FactoryA2 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid FactoryB1 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid FactoryB2 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid DisabledFactory = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
    private static readonly Guid UnlistedFactory = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004");
    private static readonly Guid UserA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    public static async Task RunAsync()
    {
        VerifyTrustedSubject();
        VerifyResourceBoundGrants();
        await VerifyNoAuthenticationShortcut();
        await VerifyHttpAndAuthorizationPipeline();
        Console.WriteLine("PASS: T3 V4/V5/V6/V7/V14/V25 authorization foundation: tenant/factory isolation, bound grants, trusted subjects, HTTP 401/403/503, revocation and cancellation.");
    }

    private static void VerifyTrustedSubject()
    {
        var alice = Principal(Issuer, "alice");
        Assert(TrustedSubject.Read(alice) == new SubjectIdentity(Issuer, "alice"), "V14: a validated issuer/subject must identify the user.");
        Assert(TrustedSubject.Read(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("iss", Issuer), new Claim("sub", "alice")
        ]))) is null, "V14: claims without authentication must never establish a subject.");
        Assert(TrustedSubject.Read(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("email", "alice@example.test")
        ], "test-only"))) is null, "V14: email cannot replace issuer and subject.");
        Assert(TrustedSubject.Read(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("iss", Issuer), new Claim("sub", "alice"), new Claim("sub", "bob")
        ], "test-only"))) is null, "V14: ambiguous duplicate subjects must be denied.");
        Assert(TrustedSubject.Read(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("iss", Issuer), new Claim("iss", OtherIssuer), new Claim("sub", "alice")
        ], "test-only"))) is null, "V14: ambiguous duplicate issuers must be denied.");
        Assert(TrustedSubject.Read(new ClaimsPrincipal([
            new ClaimsIdentity([new Claim("iss", Issuer)], "test-only"),
            new ClaimsIdentity([new Claim("sub", "alice")], "test-only")
        ])) is null, "V14: claims from different authenticated identities must not be combined.");
        Assert(TrustedSubject.Read(Principal(OtherIssuer, "alice")) != TrustedSubject.Read(alice),
            "V4/V14: equal subjects at different issuers identify different users.");
    }

    private static void VerifyResourceBoundGrants()
    {
        var snapshot = AccessA();
        var production = new ProtectedResource(TenantA, "dataset", "production", FactoryA1);
        Assert(PermissionEvaluator.Allows(snapshot, "production:read", production), "V5: an explicit factory grant must allow its dataset.");
        Assert(!PermissionEvaluator.Allows(snapshot, "production:read", production with { OrganizationId = FactoryA2 }),
            "V5/V7: permission, resource and organization must match one grant; unrelated grants cannot be unioned.");
        Assert(!PermissionEvaluator.Allows(snapshot, "production:read", production with { TenantId = TenantB }),
            "V4: a matching resource ID in another tenant must be denied.");
        Assert(!PermissionEvaluator.Allows(snapshot, "production:read", production with { OrganizationId = FactoryB1 }),
            "V4/V5: another tenant's organization must be denied even if the request asserts tenant A.");
        Assert(!PermissionEvaluator.Allows(snapshot, "production:read", production with { OrganizationId = null }),
            "V5: omitting an organization cannot broaden an organization-scoped grant.");
        Assert(!PermissionEvaluator.Allows(snapshot, "production:write", production), "V7: read permission cannot authorize a write.");
        Assert(!PermissionEvaluator.Allows(snapshot, "production:read", production with { Type = "model" }), "V7: resource types are part of the grant.");
        Assert(!PermissionEvaluator.Allows(snapshot, "production:read", production with { Id = "unregistered" }), "V7: resource IDs must match exactly.");
        Assert(!PermissionEvaluator.Allows(snapshot, "PRODUCTION:READ", production), "V7: permission identifiers must match exactly.");
        Assert(!PermissionEvaluator.Allows(null, "production:read", production), "V6: missing membership must be denied.");
        Assert(!PermissionEvaluator.Allows(snapshot with { Grants = [] }, "production:read", production), "V6: membership alone does not grant access.");

        var model = new ProtectedResource(TenantA, "model", "manufacturing-assistant", null);
        Assert(PermissionEvaluator.Allows(snapshot, "model:invoke", model), "V7: an explicit tenant-wide grant can authorize a tenant-level resource.");
        Assert(PermissionEvaluator.Allows(snapshot, "model:invoke", model with { OrganizationId = FactoryA2 }),
            "V5: an explicit tenant-wide grant covers enabled organizations in its own tenant.");
        Assert(!PermissionEvaluator.Allows(snapshot, "model:invoke", model with { OrganizationId = DisabledFactory }),
            "V5/V25: even tenant-wide grants cannot authorize a disabled organization.");
        Assert(!PermissionEvaluator.Allows(snapshot, "model:invoke", model with { OrganizationId = FactoryB2 }),
            "V4: tenant-wide does not mean organizations across tenants.");
        Assert(!PermissionEvaluator.Allows(snapshot, "model:invoke", model with { TenantId = TenantB }),
            "V4: tenant-wide grants are still bound to one tenant.");
        var other = AccessB();
        Assert(PermissionEvaluator.Allows(other, "production:read", new(TenantB, "dataset", "production", FactoryB1)),
            "V4/V5: tenant B's independent grant must work in its own factory.");
        Assert(!PermissionEvaluator.Allows(other, "production:read", new(TenantB, "dataset", "production", FactoryB2)),
            "V5: tenant B must also enforce its second factory's boundary.");
    }

    private static async Task VerifyNoAuthenticationShortcut()
    {
        var store = new FixtureStore();
        await using var app = PlatformApplication.Build([
            "--environment", "Production", "--urls", "http://127.0.0.1:0"
        ], builder =>
        {
            builder.Logging.ClearProviders();
            builder.Services.RemoveAll<IAuthorizationStore>();
            builder.Services.AddSingleton<IAuthorizationStore>(store);
        });
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/platform/v1/tenants");
            request.Headers.Authorization = new("Bearer", "unvalidated-token");
            request.Headers.Add("Cookie", "assembleai-session=forged");
            request.Headers.Add("X-Test-Subject", "alice");
            request.Headers.Add("X-Internal-User", UserA.ToString());
            request.Headers.Add("X-Tenant-Id", TenantA.ToString());
            using var response = await client.SendAsync(request);
            await AssertResponse(response, HttpStatusCode.Unauthorized, "authentication_required");
            Assert(store.ReadCount == 0 && store.ListCount == 0, "V14: production must reject forged identities before reading authorization data.");
        }
        finally { await app.StopAsync(); }
    }

    private static async Task VerifyHttpAndAuthorizationPipeline()
    {
        var store = new FixtureStore();
        await using var app = PlatformApplication.Build([
            "--environment", "Production", "--urls", "http://127.0.0.1:0"
        ], builder =>
        {
            builder.Logging.ClearProviders();
            builder.Services.RemoveAll<IAuthorizationStore>();
            builder.Services.AddSingleton<IAuthorizationStore>(store);
            builder.Services.AddAuthentication("TestOnly")
                .AddScheme<AuthenticationSchemeOptions, FixtureAuthenticationHandler>("TestOnly", _ => { });
        });
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
            using (var anonymous = await Send(client, "/api/platform/v1/tenants"))
                await AssertResponse(anonymous, HttpStatusCode.Unauthorized, "authentication_required");
            using (var malformedIdentity = await Send(client, "/api/platform/v1/tenants", "missing-sub"))
                await AssertResponse(malformedIdentity, HttpStatusCode.Forbidden, "access_denied");

            using (var ownTenants = await Send(client, "/api/platform/v1/tenants", "alice"))
            {
                await AssertResponse(ownTenants, HttpStatusCode.OK);
                using var payload = JsonDocument.Parse(await ownTenants.Content.ReadAsStringAsync());
                var items = payload.RootElement.GetProperty("items");
                Assert(items.GetArrayLength() == 1 && items[0].GetProperty("tenantId").GetGuid() == TenantA,
                    "V4: users must see only their own enabled tenant memberships.");
            }
            using (var otherIssuer = await Send(client, "/api/platform/v1/tenants", "other-issuer"))
            {
                await AssertResponse(otherIssuer, HttpStatusCode.OK);
                using var payload = JsonDocument.Parse(await otherIssuer.Content.ReadAsStringAsync());
                Assert(payload.RootElement.GetProperty("items")[0].GetProperty("tenantId").GetGuid() == TenantB,
                    "V4/V14: same subject at another issuer must resolve its own tenant membership.");
            }
            using (var unregistered = await Send(client, "/api/platform/v1/tenants", "unregistered"))
            {
                await AssertResponse(unregistered, HttpStatusCode.OK);
                using var payload = JsonDocument.Parse(await unregistered.Content.ReadAsStringAsync());
                Assert(payload.RootElement.GetProperty("items").GetArrayLength() == 0,
                    "V6: validated but unregistered users have no automatic memberships.");
            }
            using (var foreignAccess = await Send(client, $"/api/platform/v1/tenants/{TenantB}/access", "alice"))
                await AssertResponse(foreignAccess, HttpStatusCode.Forbidden, "tenant_access_denied");
            using (var unknownAccess = await Send(client, $"/api/platform/v1/tenants/{Guid.NewGuid()}/access", "alice"))
                await AssertResponse(unknownAccess, HttpStatusCode.Forbidden, "tenant_access_denied");

            using (var forged = new HttpRequestMessage(HttpMethod.Get, $"/api/platform/v1/tenants/{TenantA}/access"))
            {
                forged.Headers.Add("X-Test-Subject", "alice");
                forged.Headers.Add("X-Tenant-Id", TenantB.ToString());
                forged.Headers.Add("X-Organization-Id", FactoryA2.ToString());
                forged.Headers.Add("X-Internal-User", UserB.ToString());
                forged.Headers.Add("X-Permissions", "production:write");
                using var response = await client.SendAsync(forged);
                await AssertResponse(response, HttpStatusCode.OK);
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert(payload.RootElement.GetProperty("tenantId").GetGuid() == TenantA
                    && payload.RootElement.GetProperty("userId").GetGuid() == UserA,
                    "V4/V14: client headers cannot replace validated subject or explicit membership lookup.");
                Assert(!(await response.Content.ReadAsStringAsync()).Contains("production:write", StringComparison.Ordinal),
                    "V14: client permission headers cannot add grants.");
                Assert(!payload.RootElement.TryGetProperty("enabledOrganizationIds", out _)
                    && !(await response.Content.ReadAsStringAsync()).Contains(UnlistedFactory.ToString(), StringComparison.Ordinal),
                    "V5: internal enabled-organization validation data must not become a tenant organization directory response.");
            }

            store.WrongTenant = true;
            using (var mismatched = await Send(client, $"/api/platform/v1/tenants/{TenantA}/access", "alice"))
                await AssertResponse(mismatched, HttpStatusCode.Forbidden, "tenant_access_denied");
            store.WrongTenant = false;

            await using var scope = app.Services.CreateAsyncScope();
            var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
            var requirement = new PlatformPermissionRequirement("production:read");
            var resource = new ProtectedResource(TenantA, "dataset", "production", FactoryA1);
            Assert((await authorization.AuthorizeAsync(Principal(Issuer, "alice"), resource, [requirement])).Succeeded,
                "V6: the ASP.NET resource handler must allow an authorized server-loaded resource.");
            Assert(!(await authorization.AuthorizeAsync(Principal(Issuer, "alice"), resource with { OrganizationId = FactoryA2 }, [requirement])).Succeeded,
                "V5/V6: the actual ASP.NET handler must deny a different factory.");
            Assert(!(await authorization.AuthorizeAsync(Principal(OtherIssuer, "alice"), resource, [requirement])).Succeeded,
                "V4: the same subject at a different issuer cannot borrow another user's grants.");
            Assert(!(await authorization.AuthorizeAsync(new ClaimsPrincipal(), resource, [requirement])).Succeeded,
                "V6: direct authorization calls cannot bypass authentication.");

            store.RevokedGrants = true;
            Assert(!(await authorization.AuthorizeAsync(Principal(Issuer, "alice"), resource, [requirement])).Succeeded,
                "V25: revoking grants while membership remains enabled must affect the next authorization check.");
            using (var removedGrants = await Send(client, $"/api/platform/v1/tenants/{TenantA}/access", "alice"))
            {
                await AssertResponse(removedGrants, HttpStatusCode.OK);
                using var payload = JsonDocument.Parse(await removedGrants.Content.ReadAsStringAsync());
                Assert(payload.RootElement.GetProperty("grants").GetArrayLength() == 0
                    && payload.RootElement.GetProperty("authorizationVersion").GetInt64() == 2,
                    "V25: access metadata must reflect fresh grants and authorization version.");
            }
            store.RevokedGrants = false;

            var readsBeforeRevocation = store.ReadCount;
            store.Revoked = true;
            using (var revoked = await Send(client, $"/api/platform/v1/tenants/{TenantA}/access", "alice"))
                await AssertResponse(revoked, HttpStatusCode.Forbidden, "tenant_access_denied");
            Assert(!(await authorization.AuthorizeAsync(Principal(Issuer, "alice"), resource, [requirement])).Succeeded,
                "V25: revoked membership must be denied even by the previously used authorization service.");
            Assert(store.ReadCount >= readsBeforeRevocation + 2, "V25: requests must reread the store after revocation, without a stale positive cache.");
            store.Revoked = false;

            store.Unavailable = true;
            using (var unavailable = await Send(client, $"/api/platform/v1/tenants/{TenantA}/access", "alice"))
            {
                await AssertResponse(unavailable, HttpStatusCode.ServiceUnavailable, "authorization_unavailable");
                Assert(!(await unavailable.Content.ReadAsStringAsync()).Contains("do-not-leak", StringComparison.Ordinal),
                    "V6: store failures must not disclose connection or credential details.");
            }
            using (var unavailableList = await Send(client, "/api/platform/v1/tenants", "alice"))
                await AssertResponse(unavailableList, HttpStatusCode.ServiceUnavailable, "authorization_unavailable");
            store.Unavailable = false;

            await VerifyRequestCancellation(client, store);
        }
        finally
        {
            store.Release.TrySetResult();
            await app.StopAsync();
        }
    }

    private static async Task VerifyRequestCancellation(HttpClient client, FixtureStore store)
    {
        store.BlockReads = true;
        using var cancellation = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/platform/v1/tenants/{TenantA}/access");
        request.Headers.Add("X-Test-Subject", "alice");
        var pending = client.SendAsync(request, cancellation.Token);
        try
        {
            await store.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            try
            {
                using var unexpected = await pending;
                throw new InvalidOperationException("Request cancellation unexpectedly returned an HTTP response.");
            }
            catch (OperationCanceledException) { }
            await store.ReadCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            store.Release.TrySetResult();
            store.BlockReads = false;
        }
    }

    private static AccessSnapshot AccessA() => new(UserA, TenantA, 1, [
        new("production:read", "dataset", "production", false, new HashSet<Guid> { FactoryA1 }),
        new("report:export", "dataset", "production", false, new HashSet<Guid> { FactoryA2 }),
        new("production:read", "dataset", "other", false, new HashSet<Guid> { FactoryA2 }),
        new("model:invoke", "model", "manufacturing-assistant", true, new HashSet<Guid>())
    ], new HashSet<Guid> { FactoryA1, FactoryA2, UnlistedFactory });

    private static AccessSnapshot AccessB() => new(UserB, TenantB, 1, [
        new("production:read", "dataset", "production", false, new HashSet<Guid> { FactoryB1 })
    ], new HashSet<Guid> { FactoryB1, FactoryB2 });

    private static ClaimsPrincipal Principal(string issuer, string subject) => new(new ClaimsIdentity([
        new Claim("iss", issuer), new Claim("sub", subject), new Claim("email", "shared@example.test")
    ], "test-only"));

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, string? identity = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (identity is not null) request.Headers.Add("X-Test-Subject", identity);
        return await client.SendAsync(request);
    }

    private static async Task AssertResponse(HttpResponseMessage response, HttpStatusCode expected, string? code = null)
    {
        Assert(response.StatusCode == expected, $"Expected {expected}, received {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert(response.Headers.CacheControl?.NoStore == true, "V25: identity and authorization responses must not be cached by clients or proxies.");
        Assert(response.Headers.Location is null, "API authorization errors must not redirect to an interactive login.");
        if (code is null) return;
        Assert(response.Content.Headers.ContentType?.MediaType == "application/problem+json", "Authorization failures use the problem-details contract.");
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert(payload.RootElement.GetProperty("code").GetString() == code, $"Expected problem code {code}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FixtureStore : IAuthorizationStore
    {
        public bool Revoked { get; set; }
        public bool RevokedGrants { get; set; }
        public bool WrongTenant { get; set; }
        public bool Unavailable { get; set; }
        public bool BlockReads { get; set; }
        public int ReadCount { get; private set; }
        public int ListCount { get; private set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<TenantMembership>> ListTenantsAsync(SubjectIdentity identity, CancellationToken cancellationToken = default)
        {
            ListCount++;
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfUnavailable();
            IReadOnlyList<TenantMembership> result = identity switch
            {
                { Issuer: Issuer, Subject: "alice" } when !Revoked => [new(TenantA, "Manufacturing A")],
                { Issuer: OtherIssuer, Subject: "alice" } => [new(TenantB, "Manufacturing B")],
                _ => []
            };
            return Task.FromResult(result);
        }

        public async Task<AccessSnapshot?> ReadAccessAsync(SubjectIdentity identity, Guid tenantId, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfUnavailable();
            if (BlockReads)
            {
                ReadStarted.TrySetResult();
                try { await Release.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException)
                {
                    ReadCancelled.TrySetResult();
                    throw;
                }
            }
            return identity switch
            {
                { Issuer: Issuer, Subject: "alice" } when tenantId == TenantA && !Revoked => WrongTenant
                    ? AccessB() : RevokedGrants ? AccessA() with { Grants = [], AuthorizationVersion = 2 } : AccessA(),
                { Issuer: OtherIssuer, Subject: "alice" } when tenantId == TenantB => AccessB(),
                _ => null
            };
        }

        private void ThrowIfUnavailable()
        {
            if (Unavailable) throw new AuthorizationStoreUnavailableException(new IOException("Password=do-not-leak"));
        }
    }

    // This handler is compiled only into the smoke-test executable. Production never registers it.
    private sealed class FixtureAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            ClaimsPrincipal? principal = Request.Headers["X-Test-Subject"].ToString() switch
            {
                "alice" => Principal(Issuer, "alice"),
                "other-issuer" => Principal(OtherIssuer, "alice"),
                "unregistered" => Principal(Issuer, "unregistered"),
                "missing-sub" => new ClaimsPrincipal(new ClaimsIdentity([new Claim("iss", Issuer)], "test-only")),
                _ => null
            };
            return Task.FromResult(principal is null
                ? AuthenticateResult.NoResult()
                : AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
