using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AssembleAI.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AssembleAI.Platform.SmokeTests;

// Only invoked after the dispatcher proves that this is its disposable database.
public static class PostgresIdentityManagementTests
{
    public static async Task RunAsync(string connectionString)
    {
        await PostgresIdentityBootstrapTests.RunAsync(connectionString);
        var f = new IdentityManagementFixture();
        await using var oidc = await OidcFixture.StartAsync();
        await using var db = new NpgsqlConnection(connectionString); await db.OpenAsync();
        try
        {
            await f.Seed(db, oidc.Authority);
            var origin = IdentityManagementFixture.FreeOrigin();
            await using var app = IdentityManagementFixture.Application(connectionString, oidc, origin);
            await app.StartAsync();
            try
            {
                using var root = await IdentityBrowser.Login(oidc, origin, f.Root);
                using var admin = await IdentityBrowser.Login(oidc, origin, f.Admin);
                using var member = await IdentityBrowser.Login(oidc, origin, f.Member);
                using var developer = await IdentityBrowser.Login(oidc, origin, f.Developer);
                using var unknown = await IdentityBrowser.Login(oidc, origin, Guid.NewGuid());
                var path = IdentityManagementFixture.Base + "/tenants/" + f.Tenant;
                var other = IdentityManagementFixture.Base + "/tenants/" + f.OtherTenant;
                var workspace = IdentityManagementFixture.Base + "/workspace";
                var global = await root.Call("GET", workspace);
                IdentityManagementFixture.Assert(global.GetProperty("platformAdministrator").GetBoolean()
                    && global.GetProperty("users").GetArrayLength() == 4, "Global administration works without tenant membership or selection.");
                await root.Call("GET", path);
                await root.Call("GET", $"/api/platform/v1/tenants/{f.Tenant}/access", expected: 403);
                await root.Call("PUT", IdentityManagementFixture.Base + "/users/" + f.Root,
                    new { expectedRevision = 1, issuer = oidc.Authority, subject = f.Root.ToString(), displayName = "平台管理员", enabled = false, platformAdministrator = false }, 409);
                await unknown.Call("GET", workspace, expected: 403);
                // A registry operator, ordinary member, or matching subject at no platform identity cannot manage identity.
                await developer.Call("GET", workspace, expected: 403);
                await developer.Call("GET", path, expected: 403);
                await member.Select(f.Tenant); await member.Call("GET", path, expected: 403);
                await admin.Call("GET", path, expected: 403);
                await admin.Select(f.Tenant);
                var own = await admin.Call("GET", path);
                IdentityManagementFixture.Assert(own.GetProperty("members").GetArrayLength() == 2, "Tenant administrator sees only local members.");
                var restricted = await admin.Call("GET", workspace);
                IdentityManagementFixture.Assert(restricted.GetProperty("users").GetArrayLength() == 0
                    && restricted.GetProperty("tenants").GetArrayLength() == 1, "Tenant administration must not enumerate global users or other tenants.");
                await admin.Call("GET", other, expected: 403);
                await admin.Call("GET", path + "?tenantId=" + f.OtherTenant, expected: 400);
                await admin.Call("PUT", IdentityManagementFixture.Base + "/users/" + f.Member,
                    new { expectedRevision = 1, issuer = oidc.Authority, subject = f.Member.ToString(), displayName = "Changed", enabled = true, platformAdministrator = true }, 403);

                var createdUser = Guid.NewGuid(); f.AdditionalUsers.Add(createdUser);
                var userBody = new { expectedRevision = 0, issuer = oidc.Authority, subject = createdUser.ToString(), displayName = "新成员", enabled = true, platformAdministrator = false };
                await root.Call("PUT", IdentityManagementFixture.Base + "/users/" + createdUser, userBody);
                var duplicate = Guid.NewGuid();
                await root.Call("PUT", IdentityManagementFixture.Base + "/users/" + duplicate, userBody, 409);
                var memberPath = path + "/members/" + createdUser;
                await root.Call("PUT", memberPath, new { expectedRevision = 0, enabled = true, administrator = false, roleIds = Array.Empty<Guid>() });
                await admin.Call("PUT", memberPath, new { expectedRevision = 1, enabled = true, administrator = true, roleIds = Array.Empty<Guid>() }, 403);
                await admin.Call("PUT", path + "/members/" + f.Admin, new { expectedRevision = 1, enabled = true, administrator = false, roleIds = Array.Empty<Guid>() }, 403);

                var orgId = Guid.NewGuid(); var orgPath = path + "/organizations/" + orgId;
                var createOrg = new { expectedRevision = 0, name = "装配工厂", kind = "factory", enabled = true };
                await admin.Call("PUT", orgPath, createOrg, 403, csrf: false);
                await admin.Call("PUT", orgPath, createOrg, 403, requestOrigin: "https://foreign.example.test");
                await admin.Call("PUT", orgPath, "{\"expectedRevision\":0,\"expectedRevision\":0,\"name\":\"重复\",\"kind\":\"factory\",\"enabled\":true}", 400);
                await admin.Call("PUT", orgPath, createOrg);
                var roleId = Guid.NewGuid(); var rolePath = path + "/roles/" + roleId;
                object Grant(Guid organization, string resource = "production") => new { permission = "production:read", resourceType = "dataset", resourceId = resource, tenantWide = false, organizationIds = new[] { organization } };
                await admin.Call("PUT", rolePath, new { expectedRevision = 0, name = "生产只读", enabled = true, grants = new[] { Grant(f.OtherOrganization) } }, 400);
                await admin.Call("PUT", rolePath, new { expectedRevision = 0, name = "生产只读", enabled = true, grants = new[] { Grant(orgId, "*") } }, 400);
                await admin.Call("PUT", rolePath, new { expectedRevision = 0, name = "生产只读", enabled = true, grants = new[] { Grant(orgId) } });
                await admin.Call("PUT", memberPath, new { expectedRevision = 1, enabled = true, administrator = false, roleIds = new[] { f.OtherRole } }, 400);
                await admin.Call("PUT", memberPath, new { expectedRevision = 1, enabled = true, administrator = false, roleIds = new[] { roleId } });
                using var created = await IdentityBrowser.Login(oidc, origin, createdUser); await created.Select(f.Tenant);
                var accessPath = $"/api/platform/v1/tenants/{f.Tenant}/access";
                var access = await created.Call("GET", accessPath);
                IdentityManagementFixture.Assert(access.GetProperty("grants").GetArrayLength() == 1, "Saved member-role assignment must reach live authorization store.");
                await created.Call("GET", workspace, expected: 403);
                var oldVersion = access.GetProperty("authorizationVersion").GetInt64();
                var editedRole = new { expectedRevision = 1, name = "生产只读", enabled = false, grants = new[] { Grant(orgId) } };
                await admin.Call("PUT", rolePath, editedRole);
                var revoked = await created.Call("GET", accessPath);
                IdentityManagementFixture.Assert(revoked.GetProperty("grants").GetArrayLength() == 0
                    && revoked.GetProperty("authorizationVersion").GetInt64() > oldVersion, "Committed role revocation must affect the same login immediately.");

                // A real database race must commit only one mutation/audit entry.
                var beforeCount = await IdentityManagementFixture.Scalar<long>(db, "SELECT count(*) FROM public.platform_identity_audit WHERE entity_id=$1", orgId);
                var edits = await Task.WhenAll(admin.Status("PUT", orgPath, new { expectedRevision = 1, name = "工厂 A", kind = "factory", enabled = true }),
                    admin.Status("PUT", orgPath, new { expectedRevision = 1, name = "工厂 B", kind = "factory", enabled = true }));
                IdentityManagementFixture.Assert(edits.Order().SequenceEqual([200, 409]), "Concurrent same-revision edits must produce one success and one conflict.");
                IdentityManagementFixture.Assert(await IdentityManagementFixture.Scalar<long>(db, "SELECT identity_revision FROM public.tenant_organizations WHERE tenant_id=$1 AND organization_id=$2", f.Tenant, orgId) == 2,
                    "Concurrent updates cannot silently overwrite the committed revision.");
                var successCount = await IdentityManagementFixture.Scalar<long>(db, "SELECT count(*) FROM public.platform_identity_audit WHERE entity_id=$1 AND result='success'", orgId);
                IdentityManagementFixture.Assert(successCount == beforeCount + 1, "A stale update cannot create a successful audit entry.");

                await IdentityManagementFixture.Execute(db, $"ALTER TABLE public.platform_identity_audit ADD CONSTRAINT identity_test_audit_failure CHECK (entity_id <> '{orgId:D}'::uuid) NOT VALID");
                try
                {
                    await admin.Call("PUT", orgPath, new { expectedRevision = 2, name = "不得提交", kind = "factory", enabled = false }, 503, requestId: "identity-test-audit-failure");
                    IdentityManagementFixture.Assert(await IdentityManagementFixture.Scalar<long>(db, "SELECT identity_revision FROM public.tenant_organizations WHERE tenant_id=$1 AND organization_id=$2", f.Tenant, orgId) == 2,
                        "Audit persistence failure must roll back authorization data and revision.");
                }
                finally { await IdentityManagementFixture.Execute(db, "ALTER TABLE public.platform_identity_audit DROP CONSTRAINT identity_test_audit_failure"); }

                await VerifyWaitingSession(connectionString, db, oidc, origin, f, orgPath, orgId, logout: false);
                await VerifyWaitingSession(connectionString, db, oidc, origin, f, orgPath, orgId, logout: true);

                // Disabling a membership must remove data and write privileges in its existing BFF session.
                await root.Call("PUT", path + "/members/" + f.Admin,
                    new { expectedRevision = 1, enabled = false, administrator = true, roleIds = Array.Empty<Guid>() });
                await admin.Call("GET", path, expected: 403);
                await admin.Call("PUT", orgPath, new { expectedRevision = 2, name = "Forbidden", kind = "factory", enabled = true }, 403);
                await root.Call("PUT", path, new { expectedRevision = 1, name = "身份验收租户", enabled = false });
                await root.Call("GET", path); // Explicit global administrators can repair a disabled tenant.
                var final = await root.Call("GET", path);
                IdentityManagementFixture.Assert(final.GetProperty("audit").GetProperty("items").EnumerateArray().All(item =>
                    item.GetProperty("tenantId").GetGuid() == f.Tenant && item.GetProperty("requestId").GetString()!.Length > 0),
                    "Tenant audit must preserve scope, server actor and request correlation.");
                var globalAudit = (await root.Call("GET", workspace)).GetProperty("audit").GetProperty("items");
                IdentityManagementFixture.Assert(globalAudit.GetArrayLength() > 0 && globalAudit.EnumerateArray().All(item => item.GetProperty("tenantId").ValueKind == JsonValueKind.Null),
                    "Global user audit and tenant operation audit must have separate query scopes.");
                await IdentityManagementFixture.Execute(db, "UPDATE public.platform_identity_administrators SET enabled=false WHERE user_id=$1", f.Root);
                await root.Call("GET", path, expected: 403);
                await root.Call("GET", workspace, expected: 403);
            }
            finally { await app.StopAsync(); }
        }
        finally { await f.Cleanup(db); }
        Console.WriteLine("PASS: identity administration real OIDC/PostgreSQL: scope separation, controlled grants, strict commands, selected tenant, CAS race, audit rollback and same-session revocation.");
    }

    private static async Task VerifyWaitingSession(string connectionString, NpgsqlConnection observer, OidcFixture oidc,
        string origin, IdentityManagementFixture f, string path, Guid orgId, bool logout)
    {
        using var session = await IdentityBrowser.Login(oidc, origin, f.Admin); await session.Select(f.Tenant);
        await using var blocker = new NpgsqlConnection(connectionString); await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var locked = new NpgsqlCommand("LOCK TABLE public.platform_users IN SHARE MODE", blocker, transaction)) await locked.ExecuteNonQueryAsync();
        var pending = session.Status("PUT", path, new { expectedRevision = 2, name = "过期会话不得写入", kind = "factory", enabled = true });
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            while (!await IdentityManagementFixture.Scalar<bool>(observer,
                "SELECT EXISTS(SELECT 1 FROM pg_locks WHERE relation='public.platform_users'::regclass AND NOT granted)"))
                await Task.Delay(10, deadline.Token);
            if (logout) await session.Call("POST", "/bff/logout", expected: 204);
            else await session.Select(f.OtherTenant);
        }
        finally { await transaction.RollbackAsync(); }
        IdentityManagementFixture.Assert(await pending == 403, "A waiting mutation must recheck logout and selected-tenant change after acquiring its lock.");
        IdentityManagementFixture.Assert(await IdentityManagementFixture.Scalar<long>(observer,
            "SELECT identity_revision FROM public.tenant_organizations WHERE tenant_id=$1 AND organization_id=$2", f.Tenant, orgId) == 2,
            "A session revoked while waiting cannot mutate state or revision.");
    }
}

internal sealed class IdentityManagementFixture
{
    public const string Base = "/api/platform/v1/identity-management";
    public Guid Root { get; } = Guid.NewGuid(); public Guid Admin { get; } = Guid.NewGuid();
    public Guid Member { get; } = Guid.NewGuid(); public Guid Developer { get; } = Guid.NewGuid();
    public Guid Tenant { get; } = Guid.NewGuid(); public Guid OtherTenant { get; } = Guid.NewGuid();
    public Guid OtherRole { get; } = Guid.NewGuid(); public Guid OtherOrganization { get; } = Guid.NewGuid();
    public List<Guid> AdditionalUsers { get; } = [];
    public async Task Seed(NpgsqlConnection db, string issuer)
    {
        foreach (var (id, name) in new[] { (Root, "平台管理员"), (Admin, "租户管理员"), (Member, "普通成员"), (Developer, "研发审核员") })
            await Execute(db, "INSERT INTO public.platform_users(user_id,issuer,subject,display_name) VALUES($1,$2,$3,$4)", id, issuer, id.ToString(), name);
        await Execute(db, "INSERT INTO public.platform_identity_administrators(user_id) VALUES($1)", Root);
        await Execute(db, "INSERT INTO public.platform_registry_operators(user_id,role) VALUES($1,'developer'),($1,'reviewer')", Developer);
        await Execute(db, "INSERT INTO public.platform_tenants(tenant_id,name) VALUES($1,'身份验收租户'),($2,'隔离租户')", Tenant, OtherTenant);
        await Execute(db, "INSERT INTO public.tenant_memberships(tenant_id,user_id) VALUES($1,$3),($1,$4),($2,$3)", Tenant, OtherTenant, Admin, Member);
        await Execute(db, "INSERT INTO public.tenant_identity_administrators(tenant_id,user_id) VALUES($1,$2)", Tenant, Admin);
        await Execute(db, "INSERT INTO public.tenant_organizations(tenant_id,organization_id,name,kind) VALUES($1,$2,'外租户组织','factory')", OtherTenant, OtherOrganization);
        await Execute(db, "INSERT INTO public.tenant_roles(tenant_id,role_id,name) VALUES($1,$2,'外租户角色')", OtherTenant, OtherRole);
    }
    public async Task Cleanup(NpgsqlConnection db)
    {
        await Execute(db, "DELETE FROM public.platform_identity_audit WHERE actor_user_id=ANY($1)", new[] { Root, Admin, Member, Developer }.Concat(AdditionalUsers).ToArray());
        await Execute(db, "DELETE FROM public.platform_tenants WHERE tenant_id=ANY($1)", new[] { Tenant, OtherTenant });
        await Execute(db, "DELETE FROM public.platform_users WHERE user_id=ANY($1)", new[] { Root, Admin, Member, Developer }.Concat(AdditionalUsers).ToArray());
    }
    public static WebApplication Application(string connection, OidcFixture oidc, string origin) => PlatformApplication.Build([
        "--environment", "Development", "--urls", origin, "--Authentication:Bff:Enabled", "true",
        "--Authentication:Bff:Authority", oidc.Authority, "--Authentication:Bff:ClientId", OidcFixture.ClientId,
        "--Authentication:Bff:ClientSecret", OidcFixture.ClientSecret, "--Authentication:Bff:PublicOrigin", origin,
        "--ConnectionStrings:Platform", connection, "--InternalCalls:Enabled", "false", "--Gateway:Enabled", "false"
    ], builder => builder.Logging.ClearProviders());
    public static async Task Execute(NpgsqlConnection db, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, db);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }
    public static async Task<T> Scalar<T>(NpgsqlConnection db, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, db);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return (T)(await command.ExecuteScalarAsync())!;
    }
    public static string FreeOrigin() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port; }
    public static string Workspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AssembleAI.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Identity tests require the repository workspace.");
    }
    public static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

internal sealed class IdentityBrowser(HttpClient client, string origin, string token) : IDisposable
{
    public static async Task<IdentityBrowser> Login(OidcFixture identity, string origin, Guid user)
    {
        identity.NextSubject = user.ToString();
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, CookieContainer = new() }) { BaseAddress = new(origin), Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            using var challenge = await client.GetAsync("/bff/login?returnUrl=/bff/session");
            IdentityManagementFixture.Assert(challenge.StatusCode == HttpStatusCode.Redirect, "OIDC login required.");
            using var code = await client.GetAsync(challenge.Headers.Location);
            IdentityManagementFixture.Assert(code.StatusCode == HttpStatusCode.Redirect, "OIDC authorization code required.");
            using var callback = await client.GetAsync(code.Headers.Location);
            IdentityManagementFixture.Assert(callback.StatusCode == HttpStatusCode.Redirect, "Validated OIDC callback required.");
            using var csrf = await client.GetAsync("/bff/csrf");
            using var document = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());
            return new(client, origin, document.RootElement.GetProperty("requestToken").GetString()!);
        }
        catch { client.Dispose(); throw; }
    }
    public Task<JsonElement> Select(Guid tenant) => Call("POST", "/bff/tenant", new { tenantId = tenant });
    private async Task<HttpResponseMessage> Send(string method, string path, object? body, bool csrf, string? requestOrigin, string? requestId)
    {
        using var request = new HttpRequestMessage(new(method), path);
        if (body is not null) request.Content = body is string raw ? new StringContent(raw, Encoding.UTF8, "application/json") : JsonContent.Create(body);
        if (method != "GET") { request.Headers.Add("Origin", requestOrigin ?? origin); if (csrf) request.Headers.Add(BffEndpoints.CsrfHeaderName, token); }
        if (requestId is not null) request.Headers.Add("X-Request-ID", requestId);
        return await client.SendAsync(request);
    }
    public async Task<int> Status(string method, string path, object body)
    {
        using var response = await Send(method, path, body, true, null, null); return (int)response.StatusCode;
    }
    public async Task<JsonElement> Call(string method, string path, object? body = null, int expected = 200, bool csrf = true, string? requestOrigin = null, string? requestId = null)
    {
        using var response = await Send(method, path, body, csrf, requestOrigin, requestId);
        var text = await response.Content.ReadAsStringAsync();
        IdentityManagementFixture.Assert((int)response.StatusCode == expected, $"Identity {method} {path}: expected {expected}, got {(int)response.StatusCode}.");
        IdentityManagementFixture.Assert(response.Headers.CacheControl?.NoStore == true, "Identity response must never be cached.");
        IdentityManagementFixture.Assert(!text.Contains(OidcFixture.AccessToken) && !text.Contains(OidcFixture.RefreshToken), "Identity response cannot expose OIDC tokens.");
        if (text.Length == 0) return default;
        using var parsed = JsonDocument.Parse(text); return parsed.RootElement.Clone();
    }
    public void Dispose() => client.Dispose();
}
