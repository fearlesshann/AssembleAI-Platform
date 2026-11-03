using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Identity;
using AssembleAI.Platform.Registry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AssembleAI.Platform.SmokeTests;

// Runs only after the dispatcher verifies its disposable database marker.
public static class PostgresRegistryTests
{
    public static async Task RunAsync(string connectionString)
    {
        await using var identity = await OidcFixture.StartAsync();
        var f = new Fixture();
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();
        try
        {
            await Seed(db, f, identity.Authority);
            var m1 = ManifestTests.Valid(f.App);
            var m2 = ManifestTests.Valid(f.App, "2.0.0");
            var settings = ManifestTests.CatalogSettings(m1);
            foreach (var pair in ManifestTests.CatalogSettings(m2).Where(p => p.Key.StartsWith("Registry:FrontendArtifacts:0:", StringComparison.Ordinal)))
                settings[pair.Key.Replace(":0:", ":1:")] = pair.Value;
            var origin = FreeOrigin();
            await using var app = PlatformApplication.Build([
                "--environment", "Development", "--urls", origin,
                "--Authentication:Bff:Enabled", "true", "--Authentication:Bff:Authority", identity.Authority,
                "--Authentication:Bff:ClientId", OidcFixture.ClientId, "--Authentication:Bff:ClientSecret", OidcFixture.ClientSecret,
                "--Authentication:Bff:PublicOrigin", origin, "--ConnectionStrings:Platform", connectionString,
                "--InternalCalls:Enabled", "false"
            ], builder => { builder.Logging.ClearProviders(); builder.Configuration.AddInMemoryCollection(settings); });
            await app.StartAsync();
            try
            {
                using var owner = await Browser.Login(identity, origin, f.Owner);
                using var reviewer = await Browser.Login(identity, origin, f.Reviewer);
                using var other = await Browser.Login(identity, origin, f.Other);
                using var admin = await Browser.Login(identity, origin, f.Admin);
                var path = $"/api/platform/v1/registry/apps/{f.App}/releases";
                var one = path + "/1.0.0";
                var two = path + "/2.0.0";
                var tenant = $"/api/platform/v1/tenants/{f.TenantA}/applications/{f.App}";
                var secondTenant = $"/api/platform/v1/tenants/{f.TenantB}/applications/{f.App}";
                var draft = new { expectedRevision = 0, manifest = m1 };
                await owner.Call("PUT", one, draft, 403, csrf: false);
                await owner.Call("PUT", one, draft, 403, origin: "https://untrusted.example");
                await owner.Call("PUT", one, "{", 400);
                await owner.Call("PUT", one, "{\"expectedRevision\":0,\"expectedRevision\":0,\"manifest\":{}}", 400);
                await owner.Call("PUT", one, new string(' ', 65537), 413);
                await owner.Call("PUT", one, new { expectedRevision = 0, manifest = m1 with { Status = "published" } }, 400);
                await admin.Call("PUT", one, draft, 403);
                Release(await owner.Call("PUT", one, draft), "draft", 1);
                await other.Call("PUT", one, new { expectedRevision = 1, manifest = m1 }, 403);
                await other.Call("GET", one, null, 403);
                Release(await owner.Call("PUT", one, new { expectedRevision = 1, manifest = m1 with { Description = "经校订的指标说明" } }), "draft", 2);
                await owner.Transition(one, "submit", 1, 409);
                Release(await owner.Transition(one, "submit", 2), "pending_review", 3);
                await owner.Call("PUT", one, new { expectedRevision = 3, manifest = m1 }, 409);
                await owner.Transition(one, "publish", 3, 403); // Owner has both roles; self-approval still fails.
                Release(await reviewer.Transition(one, "reject", 3), "draft", 4);
                Release(await owner.Transition(one, "submit", 4), "pending_review", 5);
                Release(await reviewer.Transition(one, "publish", 5), "published", 6);
                await owner.Call("PUT", one, new { expectedRevision = 6, manifest = m1 }, 409);
                await reviewer.Transition(one, "reject", 6, 409);
                Release(await owner.Call("PUT", two, new { expectedRevision = 0, manifest = m2 }), "draft", 1);
                await owner.Transition(two, "submit", 1);
                await reviewer.Transition(two, "publish", 2);
                var three = path + "/3.0.0";
                await owner.Call("PUT", three, new { expectedRevision = 0, manifest = ManifestTests.Valid(f.App, "3.0.0") });
                await owner.Transition(three, "submit", 1);
                await reviewer.Transition(three, "publish", 2, 400);
                var listing = await reviewer.Call("GET", path);
                Assert(listing.GetProperty("items").GetArrayLength() == 3, "Reviewer must see all releases including pending review.");
                await admin.Call("GET", tenant, null, 403);
                await admin.Call("POST", "/bff/tenant", new { tenantId = f.TenantA });
                await reviewer.Call("POST", "/bff/tenant", new { tenantId = f.TenantA });
                await reviewer.Call("PUT", tenant, Pointer(0, "1.0.0"), 403);
                await admin.Call("PUT", secondTenant, Pointer(0, "1.0.0"), 403);
                await admin.Call("GET", tenant, null, 404);
                await admin.Call("PUT", tenant, Pointer(0, "3.0.0"), 409);
                Tenant(await admin.Call("PUT", tenant, Pointer(0, "1.0.0")), "1.0.0", true, true, 1);
                var races = await Task.WhenAll(admin.Send("PUT", tenant, Pointer(1, "2.0.0")), admin.Send("PUT", tenant, Pointer(1, "2.0.0")));
                try
                {
                    Assert(races.Count(r => r.StatusCode == HttpStatusCode.OK) == 1 && races.Count(r => r.StatusCode == HttpStatusCode.Conflict) == 1,
                        "Exactly one concurrent pointer mutation may commit.");
                    foreach (var response in races) await Safe(response);
                }
                finally { foreach (var response in races) response.Dispose(); }
                Tenant(await admin.Call("GET", tenant), "2.0.0", true, true, 2);
                Tenant(await admin.Call("PUT", tenant, Pointer(2, "1.0.0")), "1.0.0", true, true, 3);
                Release(await reviewer.Transition(one, "disable", 6), "disabled", 7);
                Tenant(await admin.Call("GET", tenant), "1.0.0", true, false, 3);
                await admin.Call("PUT", tenant, Pointer(3, "1.0.0"), 409);
                Tenant(await admin.Call("PUT", tenant, Pointer(3, "1.0.0", false)), "1.0.0", false, false, 4);
                Release(await reviewer.Transition(one, "enable", 7), "published", 8);
                Tenant(await admin.Call("PUT", tenant, Pointer(4, "1.0.0")), "1.0.0", true, true, 5);
                await admin.Call("POST", "/bff/tenant", new { tenantId = f.TenantB });
                await admin.Call("GET", secondTenant, null, 403);
                await admin.Call("POST", "/bff/tenant", new { tenantId = f.TenantA });
                await Execute(db, "UPDATE public.role_permission_grants SET enabled=false WHERE tenant_id=$1 AND grant_id=$2", f.TenantA, f.Grant);
                await admin.Call("GET", tenant, null, 403);
                await admin.Call("PUT", tenant, Pointer(5, "2.0.0"), 403);
                await Execute(db, "UPDATE public.role_permission_grants SET enabled=true WHERE tenant_id=$1 AND grant_id=$2", f.TenantA, f.Grant);
                await Execute(db, "UPDATE public.platform_registry_operators SET enabled=false WHERE user_id=$1", f.Reviewer);
                await reviewer.Transition(one, "disable", 8, 403);
                await Execute(db, "UPDATE public.platform_registry_operators SET enabled=true WHERE user_id=$1", f.Reviewer);
                try
                {
                    await Execute(db, "UPDATE public.platform_application_releases SET manifest=jsonb_set(manifest,'{description}','\"changed\"'),revision=revision+1 WHERE app_id=$1 AND release_version='1.0.0'", f.App);
                    throw new InvalidOperationException("Published manifest was mutable in the database.");
                }
                catch (PostgresException e) when (e.SqlState == "23514") { }
                try
                {
                    await Execute(db, "INSERT INTO public.platform_application_releases(app_id,release_version,manifest) VALUES($1,'9.0.0','{}')", f.App);
                    throw new InvalidOperationException("Missing manifest identity fields bypassed the database constraint.");
                }
                catch (PostgresException e) when (e.SqlState == "23514") { }
                await Execute(db, "CREATE FUNCTION test_support.fail_registry_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture audit failure' USING ERRCODE='23514'; END $$");
                try
                {
                    // App is locally generated from a fixed prefix and UUID, never request input.
                    await Execute(db, $"CREATE TRIGGER fixture_registry_audit BEFORE INSERT ON public.platform_registry_audit FOR EACH ROW WHEN (NEW.app_id='{f.App}') EXECUTE FUNCTION test_support.fail_registry_audit()");
                    try
                    {
                        await reviewer.Transition(one, "disable", 8, 503);
                        Release(await reviewer.Call("GET", one), "published", 8);
                        await admin.Call("PUT", tenant, Pointer(5, "2.0.0"), 503);
                        Tenant(await admin.Call("GET", tenant), "1.0.0", true, true, 5);
                    }
                    finally { await Execute(db, "DROP TRIGGER fixture_registry_audit ON public.platform_registry_audit"); }
                }
                finally { await Execute(db, "DROP FUNCTION test_support.fail_registry_audit()"); }
                await using var audit = new NpgsqlCommand("SELECT count(*),count(*) FILTER (WHERE result='Forbidden'),count(*) FILTER (WHERE result='Conflict'),count(*) FILTER (WHERE length(request_id)>0 AND actor_user_id IS NOT NULL) FROM public.platform_registry_audit WHERE app_id=$1", db);
                audit.Parameters.AddWithValue(f.App);
                await using var reader = await audit.ExecuteReaderAsync();
                await reader.ReadAsync();
                Assert(reader.GetInt64(0) >= 25 && reader.GetInt64(1) >= 5 && reader.GetInt64(2) >= 4 && reader.GetInt64(3) == reader.GetInt64(0),
                    "Audit must attribute changes and rejected command attempts with request IDs.");
            }
            finally { await app.StopAsync(); }
            var missing = new ConfigurationBuilder().Build();
            var unavailable = new PostgresRegistryStore(missing, new(missing), new PostgresAuthorizationStore(missing));
            try { await unavailable.ReadReleaseAsync(new(identity.Authority, f.Owner.ToString()), f.App, "1.0.0"); throw new InvalidOperationException("Unconfigured registry did not fail closed."); }
            catch (RegistryStoreUnavailableException e) { Assert(e.InnerException is null, "Registry errors must hide database details."); }
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await unavailable.ReadReleaseAsync(new(identity.Authority, f.Owner.ToString()), f.App, "1.0.0", cancelled.Token); throw new InvalidOperationException("Cancellation was ignored."); }
            catch (OperationCanceledException) { }
        }
        finally
        {
            await Execute(db, "DELETE FROM public.platform_applications WHERE app_id=$1", f.App);
            await Execute(db, "DELETE FROM public.platform_registry_audit WHERE app_id=$1", f.App);
            await Execute(db, "DELETE FROM public.platform_tenants WHERE tenant_id IN ($1,$2)", f.TenantA, f.TenantB);
            await Execute(db, "DELETE FROM public.platform_users WHERE user_id IN ($1,$2,$3,$4)", f.Owner, f.Reviewer, f.Other, f.Admin);
        }
        Console.WriteLine("PASS: PostgreSQL registry + OIDC HTTP: ownership, independent review, strict requests, immutable releases, tenant grants/revocation, CAS/rollback, audit attribution and atomic failure recovery.");
    }
    private static async Task Seed(NpgsqlConnection db, Fixture f, string issuer)
    {
        foreach (var user in new[] { f.Owner, f.Reviewer, f.Other, f.Admin })
            await Execute(db, "INSERT INTO public.platform_users(user_id,issuer,subject,display_name) VALUES($1,$2,$3,'Registry fixture')", user, issuer, user.ToString());
        await Execute(db, "INSERT INTO public.platform_registry_operators(user_id,role) VALUES($1,'developer'),($1,'reviewer'),($2,'reviewer'),($3,'developer')", f.Owner, f.Reviewer, f.Other);
        await Execute(db, "INSERT INTO public.platform_tenants(tenant_id,name) VALUES($1,'Registry A'),($2,'Registry B')", f.TenantA, f.TenantB);
        await Execute(db, "INSERT INTO public.tenant_memberships(tenant_id,user_id) VALUES($1,$3),($2,$3),($1,$4)", f.TenantA, f.TenantB, f.Admin, f.Reviewer);
        await Execute(db, "INSERT INTO public.tenant_roles(tenant_id,role_id,name) VALUES($1,$2,'Registry administrator')", f.TenantA, f.Role);
        await Execute(db, "INSERT INTO public.tenant_member_roles(tenant_id,user_id,role_id) VALUES($1,$2,$3)", f.TenantA, f.Admin, f.Role);
        await Execute(db, "INSERT INTO public.role_permission_grants(tenant_id,grant_id,role_id,permission,resource_type,resource_id,tenant_wide) VALUES($1,$2,$3,'registry:manage','application',$4,true)", f.TenantA, f.Grant, f.Role, f.App);
    }
    private static object Pointer(long revision, string version, bool enabled = true) => new { expectedRevision = revision, releaseVersion = version, enabled };
    private static void Release(JsonElement value, string status, long revision) => Assert(value.GetProperty("manifest").GetProperty("status").GetString() == status
        && value.GetProperty("revision").GetInt64() == revision, "Unexpected release state or revision.");
    private static void Tenant(JsonElement value, string version, bool enabled, bool available, long revision) => Assert(
        value.GetProperty("releaseVersion").GetString() == version && value.GetProperty("enabled").GetBoolean() == enabled
        && value.GetProperty("available").GetBoolean() == available && value.GetProperty("revision").GetInt64() == revision, "Inconsistent tenant pointer.");
    private static async Task Execute(NpgsqlConnection db, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, db);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<string> Safe(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert(response.Headers.CacheControl?.NoStore == true, "Registry responses must prohibit caching.");
        Assert(!body.Contains(OidcFixture.AccessToken) && !body.Contains(OidcFixture.RefreshToken) && !body.Contains("127.0.0.1:9876")
            && !body.Contains("fixture audit failure"), "Registry response leaked credentials, a destination or database details.");
        return body;
    }
    private static string FreeOrigin()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Browser(HttpClient client, string publicOrigin, string requestToken) : IDisposable
    {
        public static async Task<Browser> Login(OidcFixture identity, string origin, Guid user)
        {
            identity.NextSubject = user.ToString();
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new() })
            { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(15) };
            try
            {
                using var challenge = await client.GetAsync("/bff/login?returnUrl=/bff/session");
                Assert(challenge.StatusCode == HttpStatusCode.Redirect, "Expected OIDC challenge.");
                using var code = await client.GetAsync(challenge.Headers.Location);
                Assert(code.StatusCode == HttpStatusCode.Redirect, "Expected OIDC authorization code.");
                using var callback = await client.GetAsync(code.Headers.Location);
                Assert(callback.StatusCode == HttpStatusCode.Redirect, "Expected validated OIDC callback.");
                using var response = await client.GetAsync("/bff/csrf");
                Assert(response.StatusCode == HttpStatusCode.OK, "Expected CSRF token.");
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return new(client, origin, body.RootElement.GetProperty("requestToken").GetString()!);
            }
            catch { client.Dispose(); throw; }
        }
        public async Task<HttpResponseMessage> Send(string method, string path, object? body = null, bool csrf = true, string? origin = null)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (body is not null) request.Content = body is string raw ? new StringContent(raw, Encoding.UTF8, "application/json") : JsonContent.Create(body);
            if (method != "GET")
            {
                request.Headers.Add("Origin", origin ?? publicOrigin);
                if (csrf) request.Headers.Add(BffEndpoints.CsrfHeaderName, requestToken);
            }
            return await client.SendAsync(request);
        }
        public async Task<JsonElement> Call(string method, string path, object? body = null, int expected = 200, bool csrf = true, string? origin = null)
        {
            using var response = await Send(method, path, body, csrf, origin);
            var payload = await Safe(response);
            Assert((int)response.StatusCode == expected, $"Registry {method} {path}: expected {expected}, got {(int)response.StatusCode}.");
            using var json = JsonDocument.Parse(payload);
            return json.RootElement.Clone();
        }
        public Task<JsonElement> Transition(string path, string action, long revision, int expected = 200) =>
            Call("POST", path + "/transitions", new { expectedRevision = revision, action }, expected);
        public void Dispose() => client.Dispose();
    }
    private sealed class Fixture
    {
        public string App { get; } = "registry-" + Guid.NewGuid().ToString("N");
        public Guid Owner { get; } = Guid.NewGuid();
        public Guid Reviewer { get; } = Guid.NewGuid();
        public Guid Other { get; } = Guid.NewGuid();
        public Guid Admin { get; } = Guid.NewGuid();
        public Guid TenantA { get; } = Guid.NewGuid();
        public Guid TenantB { get; } = Guid.NewGuid();
        public Guid Role { get; } = Guid.NewGuid();
        public Guid Grant { get; } = Guid.NewGuid();
    }
}
