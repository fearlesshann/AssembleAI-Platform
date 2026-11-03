using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Identity;
using AssembleAI.Platform.Registry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AssembleAI.Platform.SmokeTests;

// The dispatcher verifies its own disposable database marker before entering.
public static class PostgresRegistryManagementTests
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
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Platform"] = connectionString }).Build();
            var authorization = new PostgresAuthorizationStore(configuration);
            var registry = new PostgresRegistryStore(configuration, new(configuration), authorization);
            var ownerSubject = new SubjectIdentity(identity.Authority, f.Owner.ToString());
            var settings = ManifestTests.CatalogSettings(ManifestTests.Valid(f.App));
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
                using var member = await Browser.Login(identity, origin, f.Member);
                using var unknown = await Browser.Login(identity, origin, Guid.NewGuid());
                const string global = "/api/platform/v1/registry/workspace";
                var tenant = $"/api/platform/v1/tenants/{f.TenantA}/registry";
                var secondTenant = $"/api/platform/v1/tenants/{f.TenantB}/registry";
                var globalAudit = $"/api/platform/v1/registry/apps/{f.App}/audit";
                var releases = tenant + $"/apps/{f.App}/releases";
                var tenantAudit = tenant + $"/apps/{f.App}/audit";
                // Owner/reviewer have no selected tenant; global operations remain usable.
                var owned = await owner.Get(global);
                Assert(owned.GetProperty("actorUserId").GetGuid() == f.Owner && owned.GetProperty("canCreate").GetBoolean()
                    && !owned.GetProperty("canReview").GetBoolean(), "Global capabilities must come from current server roles.");
                Assert(Items(owned).GetArrayLength() == 1 && Items(owned)[0].GetProperty("appId").GetString() == f.App
                    && Items(owned)[0].GetProperty("canEdit").GetBoolean(), "A developer only lists its owned applications.");
                Assert(Items(await reviewer.Get(global)).GetArrayLength() == 3, "A reviewer can list all global applications without a tenant.");
                Assert(Items(await other.Get(global)).GetArrayLength() == 2, "Another developer cannot discover the first owner's app.");
                var adminGlobal = await admin.Get(global);
                Assert(Items(adminGlobal).GetArrayLength() == 0 && !adminGlobal.GetProperty("canCreate").GetBoolean(), "Tenant admin has no implicit global authority.");
                await unknown.Get(global, 403);
                await owner.Get(global + "?tenantId=" + f.TenantA, 400);
                await admin.Get(tenant + "/workspace", 403);
                await admin.Select(f.TenantA); await member.Select(f.TenantA);
                var adminWorkspace = await admin.Get(tenant + "/workspace");
                Assert(adminWorkspace.GetProperty("actorUserId").GetGuid() == f.Admin && adminWorkspace.GetProperty("tenantId").GetGuid() == f.TenantA,
                    "Tenant scope and actor must be explicit.");
                Assert(Items(adminWorkspace).GetArrayLength() == 1, "Tenant management list hides ungranted and draft-only applications.");
                Assert(Items(adminWorkspace)[0].GetProperty("name").GetString() == "生产报表", "Tenant metadata must come from a published/current release, not the initial draft application name.");
                var pointer = Items(adminWorkspace)[0].GetProperty("current");
                Assert(pointer.GetProperty("enabled").GetBoolean() && pointer.GetProperty("available").GetBoolean()
                    && pointer.GetProperty("revision").GetInt64() == 1, "Tenant list exposes exact current pointer and revision.");
                Assert(Items(await member.Get(tenant + "/workspace")).GetArrayLength() == 0, "Ordinary members have an empty management list.");
                await member.Get(releases, 403); await member.Get(tenantAudit, 403);
                await admin.Get(secondTenant + "/workspace", 403);
                var visible = Items(await admin.Get(releases));
                Assert(visible.GetArrayLength() == 1 && visible[0].GetProperty("manifest").GetProperty("status").GetString() == "published"
                    && visible[0].GetProperty("manifest").GetProperty("version").GetString() == "1.0.0", "Tenant version choices must hide draft/pending/disabled versions.");
                await admin.Get($"/api/platform/v1/registry/apps/{f.App}/releases", 403);
                await admin.Get(tenant + $"/apps/{f.OtherApp}/releases", 403);
                await admin.Get(releases + "?status=draft", 400);
                await admin.Get(globalAudit, 403); await other.Get(globalAudit, 403);
                var publicAudit = await reviewer.Get(globalAudit);
                Assert(Items(publicAudit).GetArrayLength() == 1 && Items(publicAudit)[0].GetProperty("tenantId").ValueKind == JsonValueKind.Null,
                    "Global reviewer cannot see tenant-private registry events.");
                var ownAudit = await admin.Get(tenantAudit);
                Assert(Items(ownAudit).GetArrayLength() == 1 && Items(ownAudit)[0].GetProperty("actorUserId").GetGuid() == f.Admin
                    && Items(ownAudit)[0].GetProperty("tenantId").GetGuid() == f.TenantA
                    && Items(ownAudit)[0].GetProperty("requestId").GetString() == "tenant-a-event", "Audit attributes actor and original request without crossing scopes.");
                await admin.Select(f.TenantB);
                Assert(Items(await admin.Get(secondTenant + "/workspace")).GetArrayLength() == 0, "Membership in another tenant does not transfer management grants.");
                await admin.Get(tenantAudit, 403);
                await admin.Get(secondTenant + $"/apps/{f.App}/audit", 403);
                await admin.Select(f.TenantA);
                await Execute(db, "UPDATE public.role_permission_grants SET enabled=false WHERE tenant_id=$1 AND resource_id=$2", f.TenantA, f.App);
                Assert(Items(await admin.Get(tenant + "/workspace")).GetArrayLength() == 0, "Committed grant revocation removes management visibility.");
                await admin.Get(releases, 403); await admin.Get(tenantAudit, 403);
                await Execute(db, "UPDATE public.role_permission_grants SET enabled=true WHERE tenant_id=$1 AND resource_id=$2", f.TenantA, f.App);
                await Execute(db, "UPDATE public.platform_registry_operators SET enabled=false WHERE user_id=$1", f.Owner);
                Assert(!((await owner.Get(global)).GetProperty("canCreate").GetBoolean()) && Items(await owner.Get(global)).GetArrayLength() == 0,
                    "Role revocation must remove capabilities and owned applications immediately.");
                await owner.Get(globalAudit, 403);
                await Execute(db, "UPDATE public.platform_registry_operators SET enabled=true WHERE user_id=$1", f.Owner);
                // Current disabled release remains identifiable/stoppable, but never a selectable published version.
                await Execute(db, "UPDATE public.platform_application_releases SET status='disabled',revision=revision+1 WHERE app_id=$1 AND release_version='1.0.0'", f.App);
                var disabled = Items(await admin.Get(tenant + "/workspace"))[0].GetProperty("current");
                Assert(disabled.GetProperty("enabled").GetBoolean() && !disabled.GetProperty("available").GetBoolean(), "Global disabled and local enabled are separate states.");
                Assert(Items(await admin.Get(releases)).GetArrayLength() == 0, "Disabled published releases cannot become version choices.");
                await Execute(db, "UPDATE public.platform_application_releases SET status='published',revision=revision+1 WHERE app_id=$1 AND release_version='1.0.0'", f.App);
                // New real HTTP mutation must be visible with its actor and trace ID.
                await owner.Put($"/api/platform/v1/registry/apps/{f.App}/releases/2.0.0", new { expectedRevision = 1, manifest = ManifestTests.Valid(f.App, "2.0.0") });
                var latest = Items(await owner.Get(globalAudit))[0];
                Assert(latest.GetProperty("actorUserId").GetGuid() == f.Owner && latest.GetProperty("action").GetString() == "save_draft"
                    && latest.GetProperty("requestId").GetString()!.Length > 0, "Read audit must correlate real HTTP writes with server actor/request ID.");
                await Execute(db, """
                    INSERT INTO public.platform_registry_audit(event_id,actor_user_id,app_id,release_version,action,result,request_id)
                    SELECT gen_random_uuid(),$1,$2,'1.0.0','publish','success','page-'||g FROM generate_series(1,50) g
                    """, f.Reviewer, f.App);
                var page = await reviewer.Get(globalAudit);
                Assert(Items(page).GetArrayLength() == 50 && page.GetProperty("hasMore").GetBoolean(), "Audit window must explicitly report more history.");
                await VerifyCapacity(db, f, owner, admin, registry, ownerSubject, global, releases);
                await admin.Post("/bff/logout", status: 204);
                await admin.Get(tenant + "/workspace", 401);
                await Execute(db, "UPDATE public.platform_users SET enabled=false WHERE user_id=$1", f.Owner);
                await owner.Get(global, 403);
            }
            finally { await app.StopAsync(); }
            var empty = new ConfigurationBuilder().Build();
            var unavailable = new PostgresRegistryManagementStore(empty, new PostgresAuthorizationStore(empty));
            try { await unavailable.ReadWorkspaceAsync(ownerSubject, default); throw new InvalidOperationException("Missing configuration must fail closed."); }
            catch (RegistryStoreUnavailableException error) { Assert(error.InnerException is null, "Store errors must be safe."); }
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await unavailable.ReadWorkspaceAsync(ownerSubject, cancelled.Token); throw new InvalidOperationException("Cancellation must propagate."); }
            catch (OperationCanceledException) { }
        }
        finally
        {
            await Execute(db, "DELETE FROM public.platform_applications WHERE owner_user_id IN ($1,$2)", f.Owner, f.Other);
            await Execute(db, "DELETE FROM public.platform_registry_audit WHERE app_id IN ($1,$2,$3)", f.App, f.OtherApp, f.DraftApp);
            await Execute(db, "DELETE FROM public.platform_tenants WHERE tenant_id IN ($1,$2)", f.TenantA, f.TenantB);
            await Execute(db, "DELETE FROM public.platform_users WHERE user_id IN ($1,$2,$3,$4,$5)", f.Owner, f.Reviewer, f.Other, f.Admin, f.Member);
        }
        Console.WriteLine("PASS: PostgreSQL management + OIDC HTTP: global/tenant role isolation, published-only versions, audit scope/attribution, live revocation, explicit limits and logout.");
    }

    private static async Task VerifyCapacity(NpgsqlConnection db, Fixture f, Browser owner, Browser admin, PostgresRegistryStore registry,
        SubjectIdentity subject, string global, string tenantReleases)
    {
        await Execute(db, """
            INSERT INTO public.platform_applications(app_id,name,category,owner_user_id)
            SELECT $1||g,'Capacity','Test',$2 FROM generate_series(1,499) g
            """, f.CapacityPrefix, f.Owner);
        Assert(Items(await owner.Get(global)).GetArrayLength() == 500, "Exactly 500 applications must remain complete.");
        await Execute(db, "INSERT INTO public.platform_applications(app_id,name,category,owner_user_id) VALUES($1,'Overflow','Test',$2)", f.CapacityPrefix + "500", f.Owner);
        await owner.Get(global, 503);
        await Execute(db, "DELETE FROM public.platform_applications WHERE owner_user_id=$1 AND app_id<>$2", f.Owner, f.App);
        // Four fixture releases + 96 = 100. No silent version truncation.
        for (var index = 5; index <= 100; index++)
            await AddRelease(db, f.App, $"{index}.0.0", "published", f.Owner);
        Assert((await registry.ListReleasesAsync(subject, f.App)).Value!.Count == 100, "Global release list must return all 100 versions.");
        await AddRelease(db, f.App, "101.0.0", "published", f.Owner);
        await owner.Get($"/api/platform/v1/registry/apps/{f.App}/releases", 503);
        // 98 published plus 3 non-published. Add two to the published boundary.
        await AddRelease(db, f.App, "102.0.0", "published", f.Owner);
        await AddRelease(db, f.App, "103.0.0", "published", f.Owner);
        Assert(Items(await admin.Get(tenantReleases)).GetArrayLength() == 100, "Tenant published choices must return all 100.");
        await AddRelease(db, f.App, "104.0.0", "published", f.Owner);
        await admin.Get(tenantReleases, 503);
    }
    private static async Task Seed(NpgsqlConnection db, Fixture f, string issuer)
    {
        foreach (var user in new[] { f.Owner, f.Reviewer, f.Other, f.Admin, f.Member })
            await Execute(db, "INSERT INTO public.platform_users(user_id,issuer,subject,display_name) VALUES($1,$2,$3,'Management fixture')", user, issuer, user.ToString());
        await Execute(db, "INSERT INTO public.platform_registry_operators(user_id,role) VALUES($1,'developer'),($2,'reviewer'),($3,'developer')", f.Owner, f.Reviewer, f.Other);
        await Execute(db, "INSERT INTO public.platform_tenants(tenant_id,name) VALUES($1,'Management A'),($2,'Management B')", f.TenantA, f.TenantB);
        await Execute(db, "INSERT INTO public.tenant_memberships(tenant_id,user_id) VALUES($1,$3),($2,$3),($1,$4)", f.TenantA, f.TenantB, f.Admin, f.Member);
        await Execute(db, "INSERT INTO public.tenant_roles(tenant_id,role_id,name) VALUES($1,$2,'Application management')", f.TenantA, f.Role);
        await Execute(db, "INSERT INTO public.tenant_member_roles(tenant_id,user_id,role_id) VALUES($1,$2,$3)", f.TenantA, f.Admin, f.Role);
        foreach (var app in new[] { f.App, f.DraftApp })
            await Execute(db, "INSERT INTO public.role_permission_grants(tenant_id,grant_id,role_id,permission,resource_type,resource_id,tenant_wide) VALUES($1,$2,$3,'registry:manage','application',$4,true)", f.TenantA, Guid.NewGuid(), f.Role, app);
        foreach (var (app, owner) in new[] { (f.App, f.Owner), (f.OtherApp, f.Other), (f.DraftApp, f.Other) })
            await Execute(db, "INSERT INTO public.platform_applications(app_id,name,category,owner_user_id) VALUES($1,$2,'Management',$3)", app, app, owner);
        await AddRelease(db, f.App, "1.0.0", "published", f.Owner);
        await AddRelease(db, f.App, "2.0.0", "draft", f.Owner);
        await AddRelease(db, f.App, "3.0.0", "disabled", f.Owner);
        await AddRelease(db, f.App, "4.0.0", "pending_review", f.Owner);
        await AddRelease(db, f.OtherApp, "1.0.0", "published", f.Other);
        await AddRelease(db, f.DraftApp, "1.0.0", "draft", f.Other);
        await Execute(db, "INSERT INTO public.tenant_applications(tenant_id,app_id,release_version,enabled) VALUES($1,$2,'1.0.0',true)", f.TenantA, f.App);
        await Execute(db, """
            INSERT INTO public.platform_registry_audit(event_id,actor_user_id,tenant_id,app_id,release_version,action,result,request_id)
            VALUES($1,$2,NULL,$3,'1.0.0','publish','success','global-event'),
            ($4,$5,$6,$3,'1.0.0','set_tenant','success','tenant-a-event'),
            ($7,$5,$8,$3,'1.0.0','set_tenant','success','tenant-b-event')
            """, Guid.NewGuid(), f.Reviewer, f.App, Guid.NewGuid(), f.Admin, f.TenantA, Guid.NewGuid(), f.TenantB);
    }
    private static Task AddRelease(NpgsqlConnection db, string app, string version, string status, Guid owner) => Execute(db, """
        INSERT INTO public.platform_application_releases(app_id,release_version,manifest,status,submitted_by,published_at)
        VALUES($1,$2,$3::jsonb,$4,$5,CASE WHEN $4 IN ('published','disabled') THEN now() ELSE NULL END)
        """, app, version, ManifestValidator.Serialize(ManifestTests.Valid(app, version)), status, owner);
    private static async Task Execute(NpgsqlConnection db, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, db);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }
    private static JsonElement Items(JsonElement value) => value.GetProperty("items");
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string FreeOrigin() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port; }
    private sealed class Fixture
    {
        private readonly string suffix = Guid.NewGuid().ToString("N");
        public string App => "manage-a-" + suffix;
        public string OtherApp => "manage-b-" + suffix;
        public string DraftApp => "manage-c-" + suffix;
        public string CapacityPrefix => "manage-cap-" + suffix + "-";
        public Guid Owner { get; } = Guid.NewGuid(); public Guid Reviewer { get; } = Guid.NewGuid();
        public Guid Other { get; } = Guid.NewGuid(); public Guid Admin { get; } = Guid.NewGuid(); public Guid Member { get; } = Guid.NewGuid();
        public Guid TenantA { get; } = Guid.NewGuid(); public Guid TenantB { get; } = Guid.NewGuid(); public Guid Role { get; } = Guid.NewGuid();
    }
    private sealed class Browser(HttpClient client, string origin, string csrf) : IDisposable
    {
        public static async Task<Browser> Login(OidcFixture identity, string origin, Guid user)
        {
            identity.NextSubject = user.ToString();
            var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, CookieContainer = new() }) { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(15) };
            try
            {
                using var login = await client.GetAsync("/bff/login?returnUrl=/bff/session");
                Assert(login.StatusCode == HttpStatusCode.Redirect, "OIDC challenge required.");
                using var code = await client.GetAsync(login.Headers.Location);
                Assert(code.StatusCode == HttpStatusCode.Redirect, "OIDC code required.");
                using var callback = await client.GetAsync(code.Headers.Location);
                Assert(callback.StatusCode == HttpStatusCode.Redirect, "Validated OIDC callback required.");
                using var response = await client.GetAsync("/bff/csrf");
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return new(client, origin, document.RootElement.GetProperty("requestToken").GetString()!);
            }
            catch { client.Dispose(); throw; }
        }
        public Task<JsonElement> Get(string path, int status = 200) => Send("GET", path, null, status);
        public Task<JsonElement> Put(string path, object body, int status = 200) => Send("PUT", path, body, status);
        public Task<JsonElement> Post(string path, object? body = null, int status = 200) => Send("POST", path, body, status);
        public Task<JsonElement> Select(Guid tenant) => Post("/bff/tenant", new { tenantId = tenant });
        private async Task<JsonElement> Send(string method, string path, object? body, int status)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method != "GET") { request.Headers.Add("Origin", origin); request.Headers.Add(BffEndpoints.CsrfHeaderName, csrf); }
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            Assert((int)response.StatusCode == status, $"Management {method} {path}: expected {status}, got {(int)response.StatusCode}.");
            Assert(response.Headers.CacheControl?.NoStore == true, "Management responses must not be cached.");
            Assert(!text.Contains(OidcFixture.AccessToken) && !text.Contains(OidcFixture.RefreshToken) && !text.Contains("127.0.0.1:9876"), "Management data cannot expose tokens or deployment targets.");
            if (text.Length == 0) return default;
            using var document = JsonDocument.Parse(text); return document.RootElement.Clone();
        }
        public void Dispose() => client.Dispose();
    }
}
