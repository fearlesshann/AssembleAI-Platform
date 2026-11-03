using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Identity;
using AssembleAI.Platform.Identity.Sessions;
using AssembleAI.Platform.InternalCalls;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AssembleAI.Platform.SmokeTests;

/// <summary>Runs only after the dispatcher validates its isolated database marker.</summary>
public static class PostgresTrustTests
{
    public static async Task RunAsync(string connectionString, string runDirectory)
    {
        await using var identity = await OidcFixture.StartAsync();
        var fixture = new Fixture();
        string? sessionKey = null;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        try
        {
            await Seed(connection, fixture, identity.Authority);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Platform"] = connectionString
            }).Build();
            var store = new PostgresAuthorizationStore(configuration);
            var directory = Path.Combine(runDirectory, "trust-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); // The launcher owns this marked run's directory and cleanup.
            using var rsa = RSA.Create(2048);
            using var certificate = new CertificateRequest("CN=postgres-trust-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var pfx = Path.Combine(directory, "signer.pfx");
            var cer = Path.Combine(directory, "signer.cer");
            File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pfx, password));
            File.WriteAllBytes(cer, certificate.Export(X509ContentType.Cert));
            var signerOptions = Options(pfx, password, []);
            var consumerOptions = Options("", "", [cer]);
            signerOptions.Validate(false);
            consumerOptions.Validate(false);
            using var signerKeys = new InternalCallKeys(signerOptions);
            using var consumerKeys = new InternalCallKeys(consumerOptions);
            // Hold JWT time constant so the revoked-token check proves policy
            // revocation even on a slow machine, rather than incidental expiry.
            var clock = new FixedClock();
            var signer = new InternalCallTokenService(signerOptions, signerKeys, store, clock);
            var consumer = new InternalCallTokenService(consumerOptions, consumerKeys, store, clock);
            var target = new InternalCallTarget("production-test", "1.0.0", "assembleai:production-test", "production:read",
                new(fixture.TenantA, "dataset", "production", fixture.Organization), "GET", "/production");

            var receiverBuilder = WebApplication.CreateBuilder(["--environment", "Development", "--urls", "http://127.0.0.1:0"]);
            receiverBuilder.Logging.ClearProviders();
            await using var receiver = receiverBuilder.Build();
            receiver.MapGet("/production", async (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                var authorization = context.Request.Headers.Authorization;
                var requestIds = context.Request.Headers["X-Request-ID"];
                if (authorization.Count != 1 || requestIds.Count != 1
                    || !AuthenticationHeaderValue.TryParse(authorization[0], out var header)
                    || header.Scheme != "Bearer" || string.IsNullOrEmpty(header.Parameter))
                    return (IResult)Results.StatusCode(401);
                var verified = await consumer.ValidateAsync(header.Parameter, target, requestIds[0]!, context.RequestAborted);
                return verified is null ? Results.StatusCode(403)
                    : Results.Ok(new { verified.UserId, verified.TenantId, verified.AuthorizationVersion, verified.RequestId });
            });
            await receiver.StartAsync();
            try
            {
                using var downstream = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
                {
                    BaseAddress = new Uri(receiver.Urls.Single()), Timeout = TimeSpan.FromSeconds(15)
                };
                var origin = FreeOrigin();
                await using var platform = PlatformApplication.Build([
                    "--environment", "Development", "--urls", origin,
                    "--Authentication:Bff:Enabled", "true", "--Authentication:Bff:Authority", identity.Authority,
                    "--Authentication:Bff:ClientId", OidcFixture.ClientId, "--Authentication:Bff:ClientSecret", OidcFixture.ClientSecret,
                    "--Authentication:Bff:PublicOrigin", origin, "--ConnectionStrings:Platform", connectionString,
                    "--InternalCalls:Enabled", "false"
                ], builder => builder.Logging.ClearProviders());
                var cookieOptions = platform.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                    .Get(BffAuthentication.CookieScheme);
                var protection = platform.Services.GetRequiredService<IDataProtectionProvider>();
                var tickets = new PostgresTicketStore(configuration, protection, TimeProvider.System);
                cookieOptions.SessionStore = tickets;
                string? issuedToken = null;
                string? issuedRequestId = null;
                // This gateway route and receiver exist only inside the test host.
                // Targets are fixture-owned resource facts, never request-bound claims.
                platform.MapGet("/test/call/{tenantId:guid}", async (Guid tenantId, HttpContext context, IAuthorizationStore authorization) =>
                {
                    context.Response.Headers.CacheControl = "no-store";
                    var access = await TenantSession.ReadAccessAsync(context, tenantId, authorization, context.RequestAborted);
                    if (access is null) return (IResult)Results.StatusCode(403);
                    var requestId = Guid.NewGuid().ToString("N");
                    var token = await signer.IssueAsync(context.User, access.TenantId, target, requestId, context.RequestAborted);
                    if (token is null) return Results.StatusCode(403);
                    issuedToken = token;
                    issuedRequestId = requestId;
                    using var request = ReceiverRequest(token, requestId);
                    using var response = await downstream.SendAsync(request, context.RequestAborted);
                    var body = await response.Content.ReadAsStringAsync(context.RequestAborted);
                    return Results.Content(body, "application/json", statusCode: (int)response.StatusCode);
                }).RequireAuthorization(IdentityEndpoints.IdentityPolicy);
                await platform.StartAsync();
                try
                {
                    var cookies = new CookieContainer();
                    using var browser = Browser(origin, cookies);
                    await Login(browser);
                    var cookie = cookies.GetCookies(new Uri(origin))["assembleai-session"]!.Value;
                    var reference = cookieOptions.TicketDataFormat.Unprotect(cookie);
                    Assert(reference is not null && reference.Principal.Claims.Count() == 1, "The browser must hold only an opaque session reference.");
                    sessionKey = reference!.Principal.Claims.Single().Value;
                    using (var selected = await browser.GetAsync("/bff/tenant"))
                    {
                        using var json = JsonDocument.Parse(await Expect(selected, HttpStatusCode.OK));
                        Assert(json.RootElement.GetProperty("tenantId").ValueKind == JsonValueKind.Null, "OIDC login must not choose a default database tenant.");
                    }
                    using (var anonymous = await downstream.GetAsync("/production"))
                        await Expect(anonymous, HttpStatusCode.Unauthorized);
                    using (var unselected = await browser.GetAsync("/test/call/" + fixture.TenantA))
                        await Expect(unselected, HttpStatusCode.Forbidden);
                    var csrf = await Csrf(browser);
                    using (var selected = await Switch(browser, fixture.TenantA, origin, csrf))
                        await Expect(selected, HttpStatusCode.OK);
                    var restored = await new PostgresTicketStore(configuration, protection, TimeProvider.System).RetrieveAsync(sessionKey);
                    Assert(restored is not null && TenantSession.ReadSelectedTenant(restored.Properties) == fixture.TenantA
                        && restored.Properties.GetTokenValue("access_token") == OidcFixture.AccessToken,
                        "A new PostgreSQL store must recover the selected tenant and original server-side OIDC tokens.");
                    await VerifyCiphertext(connection, sessionKey, fixture.TenantA);
                    using (var refused = await Switch(browser, Guid.NewGuid(), origin, csrf))
                        await Expect(refused, HttpStatusCode.Forbidden);
                    using (var wrongTenant = await browser.GetAsync("/test/call/" + fixture.TenantB))
                        await Expect(wrongTenant, HttpStatusCode.Forbidden);
                    using (var selectedB = await Switch(browser, fixture.TenantB, origin, csrf))
                        await Expect(selectedB, HttpStatusCode.OK);
                    using (var wrongTarget = await browser.GetAsync("/test/call/" + fixture.TenantB))
                        await Expect(wrongTarget, HttpStatusCode.Forbidden);
                    using (var selectedA = await Switch(browser, fixture.TenantA, origin, csrf))
                        await Expect(selectedA, HttpStatusCode.OK);
                    long oldVersion;
                    using (var call = await browser.GetAsync("/test/call/" + fixture.TenantA))
                    {
                        var body = await Expect(call, HttpStatusCode.OK);
                        using var json = JsonDocument.Parse(body);
                        Assert(json.RootElement.GetProperty("userId").GetGuid() == fixture.User
                            && json.RootElement.GetProperty("tenantId").GetGuid() == fixture.TenantA,
                            "The downstream must return the verified platform identity and exact resource tenant.");
                        oldVersion = json.RootElement.GetProperty("authorizationVersion").GetInt64();
                        Assert(issuedToken is not null && !body.Contains(issuedToken, StringComparison.Ordinal),
                            "The server-to-server credential must not be returned to the browser.");
                    }
                    Assert(issuedToken is not null && issuedRequestId is not null, "A successful call must pass a signed credential to the actual HTTP receiver.");
                    using (var request = ReceiverRequest(issuedToken!, issuedRequestId!))
                    using (var accepted = await downstream.SendAsync(request))
                        await Expect(accepted, HttpStatusCode.OK);
                    await Execute(connection, "UPDATE public.role_permission_grants SET enabled=false WHERE tenant_id=$1 AND grant_id=$2", fixture.TenantA, fixture.Grant);
                    var revoked = await store.ReadAccessAsync(new(identity.Authority, OidcFixture.Subject), fixture.TenantA);
                    Assert(revoked is not null && revoked.AuthorizationVersion > oldVersion, "Committed grant revocation must advance the actual database policy version.");
                    using (var request = ReceiverRequest(issuedToken!, issuedRequestId!))
                    using (var rejected = await downstream.SendAsync(request))
                        await Expect(rejected, HttpStatusCode.Forbidden);
                    using (var rejected = await browser.GetAsync("/test/call/" + fixture.TenantA))
                        await Expect(rejected, HttpStatusCode.Forbidden);
                    await Execute(connection, "UPDATE public.tenant_memberships SET enabled=false WHERE tenant_id=$1 AND user_id=$2", fixture.TenantA, fixture.User);
                    using (var rejected = await browser.GetAsync("/bff/tenant"))
                        await Expect(rejected, HttpStatusCode.Forbidden);
                    using (var rejected = await Switch(browser, fixture.TenantA, origin, csrf))
                        await Expect(rejected, HttpStatusCode.Forbidden);
                    using (var logout = await Post(browser, "/bff/logout", null, origin, csrf))
                        await Expect(logout, HttpStatusCode.NoContent);
                    Assert(await tickets.RetrieveAsync(sessionKey) is null, "Logout must remove the selected PostgreSQL session.");
                    var replayCookies = new CookieContainer();
                    replayCookies.Add(new Uri(origin), new Cookie("assembleai-session", cookie, "/"));
                    using var replay = Browser(origin, replayCookies);
                    using var invalidSession = await replay.GetAsync("/bff/tenant");
                    await Expect(invalidSession, HttpStatusCode.Unauthorized);
                }
                finally { await platform.StopAsync(); }
            }
            finally { await receiver.StopAsync(); }
        }
        finally
        {
            // Delete only this fixture's identifiers. The owning launcher removes
            // the complete isolated cluster and its test certificates afterward.
            if (sessionKey is not null)
                await Execute(connection, "DELETE FROM public.platform_bff_sessions WHERE session_key=$1", sessionKey);
            await Execute(connection, "DELETE FROM public.platform_tenants WHERE tenant_id IN ($1,$2)", fixture.TenantA, fixture.TenantB);
            await Execute(connection, "DELETE FROM public.platform_users WHERE user_id=$1", fixture.User);
        }
        Console.WriteLine("PASS: PostgreSQL trust chain: OIDC HTTP login, persistent tenant selection, signed HTTP downstream call, current policy revocation and logout replay rejection.");
    }

    private static async Task Seed(NpgsqlConnection connection, Fixture f, string issuer)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, "INSERT INTO public.platform_users(user_id,issuer,subject,display_name) VALUES ($1,$2,$3,'Trust fixture')", transaction, f.User, issuer, OidcFixture.Subject);
        await Execute(connection, "INSERT INTO public.platform_tenants(tenant_id,name) VALUES ($1,'Trust A'),($2,'Trust B')", transaction, f.TenantA, f.TenantB);
        await Execute(connection, "INSERT INTO public.tenant_memberships(tenant_id,user_id) VALUES ($1,$3),($2,$3)", transaction, f.TenantA, f.TenantB, f.User);
        await Execute(connection, "INSERT INTO public.tenant_organizations(tenant_id,organization_id,name,kind) VALUES ($1,$2,'Trust factory','factory')", transaction, f.TenantA, f.Organization);
        await Execute(connection, "INSERT INTO public.tenant_roles(tenant_id,role_id,name) VALUES ($1,$2,'Trust production')", transaction, f.TenantA, f.Role);
        await Execute(connection, "INSERT INTO public.tenant_member_roles(tenant_id,user_id,role_id) VALUES ($1,$2,$3)", transaction, f.TenantA, f.User, f.Role);
        await Execute(connection, "INSERT INTO public.role_permission_grants(tenant_id,grant_id,role_id,permission,resource_type,resource_id) VALUES ($1,$2,$3,'production:read','dataset','production')", transaction, f.TenantA, f.Grant, f.Role);
        await Execute(connection, "INSERT INTO public.grant_organizations(tenant_id,grant_id,organization_id) VALUES ($1,$2,$3)", transaction, f.TenantA, f.Grant, f.Organization);
        await transaction.CommitAsync();
    }

    private static async Task VerifyCiphertext(NpgsqlConnection connection, string key, Guid tenant)
    {
        await using var command = new NpgsqlCommand("SELECT protected_ticket FROM public.platform_bff_sessions WHERE session_key=$1", connection);
        command.Parameters.AddWithValue(key);
        var bytes = await command.ExecuteScalarAsync() as byte[];
        Assert(bytes is { Length: > 0 } && bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(OidcFixture.AccessToken)) < 0
            && bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(tenant.ToString("D"))) < 0,
            "The selected tenant and OIDC credentials must exist only inside a real encrypted database ticket.");
    }

    private static Task Execute(NpgsqlConnection connection, string sql, params object[] values) => Execute(connection, sql, null, values);
    private static async Task Execute(NpgsqlConnection connection, string sql, NpgsqlTransaction? transaction, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }
    private static InternalCallOptions Options(string pfx, string password, string[] publicCertificates) => new()
    {
        Enabled = true, Issuer = "https://platform.example.test/internal", SigningCertificatePath = pfx,
        SigningCertificatePassword = password, ValidationCertificatePaths = publicCertificates
    };
    private static HttpRequestMessage ReceiverRequest(string token, string requestId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/production");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Request-ID", requestId);
        return request;
    }
    private static async Task Login(HttpClient client)
    {
        using var challenge = await client.GetAsync("/bff/login?returnUrl=/bff/session");
        Assert(challenge.StatusCode == HttpStatusCode.Redirect, "The real BFF must issue an OIDC challenge.");
        using var code = await client.GetAsync(challenge.Headers.Location);
        Assert(code.StatusCode == HttpStatusCode.Redirect, "The HTTP OIDC fixture must issue an authorization code.");
        using var callback = await client.GetAsync(code.Headers.Location);
        Assert(callback.StatusCode == HttpStatusCode.Redirect, "The BFF must validate the callback before creating a PostgreSQL session.");
    }
    private static async Task<string> Csrf(HttpClient client)
    {
        using var response = await client.GetAsync("/bff/csrf");
        using var json = JsonDocument.Parse(await Expect(response, HttpStatusCode.OK));
        return json.RootElement.GetProperty("requestToken").GetString()!;
    }
    private static Task<HttpResponseMessage> Switch(HttpClient client, Guid tenant, string origin, string csrf) =>
        Post(client, "/bff/tenant", JsonContent.Create(new { tenantId = tenant }), origin, csrf);
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, HttpContent? body, string origin, string csrf)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body };
        request.Headers.Add("Origin", origin);
        request.Headers.Add(BffEndpoints.CsrfHeaderName, csrf);
        return await client.SendAsync(request);
    }
    private static async Task<string> Expect(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert(response.StatusCode == status, $"PostgreSQL trust HTTP expected {status}, got {response.StatusCode}.");
        Assert(response.Headers.CacheControl?.NoStore == true, "Trust responses must prohibit caching.");
        var body = await response.Content.ReadAsStringAsync();
        Assert(!body.Contains(OidcFixture.AccessToken, StringComparison.Ordinal) && !body.Contains(OidcFixture.RefreshToken, StringComparison.Ordinal),
            "HTTP results must not disclose server-side OIDC credentials.");
        return body;
    }
    private static HttpClient Browser(string origin, CookieContainer cookies) => new(new HttpClientHandler
    {
        AllowAutoRedirect = false, CookieContainer = cookies
    }) { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(15) };
    private static string FreeOrigin()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        public override DateTimeOffset GetUtcNow() => now;
    }
    private sealed class Fixture
    {
        public Guid User { get; } = Guid.NewGuid();
        public Guid TenantA { get; } = Guid.NewGuid();
        public Guid TenantB { get; } = Guid.NewGuid();
        public Guid Organization { get; } = Guid.NewGuid();
        public Guid Role { get; } = Guid.NewGuid();
        public Guid Grant { get; } = Guid.NewGuid();
    }
}
