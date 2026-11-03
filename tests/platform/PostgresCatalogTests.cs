using System.Net;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Catalog;
using AssembleAI.Platform.Gateway;
using AssembleAI.Platform.Identity;
using AssembleAI.Platform.Registry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AssembleAI.Platform.SmokeTests;

// The dispatcher has verified the disposable database marker before entering here.
public static class PostgresCatalogTests
{
    public static async Task RunAsync(string connectionString)
    {
        await using var identity = await OidcFixture.StartAsync();
        var fixture = new Fixture();
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();
        try
        {
            await Seed(db, fixture, identity.Authority);
            var settings = Settings(fixture, connectionString);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var authorization = new PostgresAuthorizationStore(configuration);
            var catalog = new PostgresCatalogStore(configuration, authorization, new(configuration));
            var gateway = new PostgresGatewayStore(configuration, authorization);
            var subject = new SubjectIdentity(identity.Authority, OidcFixture.Subject);
            var origin = FreeOrigin();
            await using var app = PlatformApplication.Build([
                "--environment", "Development", "--urls", origin,
                "--Authentication:Bff:Enabled", "true", "--Authentication:Bff:Authority", identity.Authority,
                "--Authentication:Bff:ClientId", OidcFixture.ClientId, "--Authentication:Bff:ClientSecret", OidcFixture.ClientSecret,
                "--Authentication:Bff:PublicOrigin", origin, "--ConnectionStrings:Platform", connectionString,
                "--InternalCalls:Enabled", "false", "--Gateway:Enabled", "false"
            ], builder => { builder.Logging.ClearProviders(); builder.Configuration.AddInMemoryCollection(settings); });
            var browserEnabled = Environment.GetEnvironmentVariable("ASSEMBLEAI_PORTAL_BROWSER_TESTS") == "1";
            var workspace = FindWorkspace();
            using var portalFiles = browserEnabled ? new PhysicalFileProvider(Path.Combine(workspace, "apps", "portal", "dist")) : null;
            if (portalFiles is not null)
            {
                var index = Path.Combine(portalFiles.Root, "index.html");
                Assert(File.Exists(index), "Build the real portal before browser acceptance.");
                app.UseStaticFiles(new StaticFileOptions { FileProvider = portalFiles });
                // Only product browser routes receive the SPA. Unknown API paths
                // must retain their actual status and never become HTML successes.
                app.MapGet("/", () => Results.File(index, "text/html"));
                app.MapGet("/applications/{**path}", () => Results.File(index, "text/html"));
            }
            await app.StartAsync();
            try
            {
                using var browser = await Browser.Login(identity, origin, OidcFixture.Subject);
                var path = $"/api/platform/v1/tenants/{fixture.Tenant}/catalog";
                await browser.Get(path, 403); // A URL or header cannot select a tenant.
                await browser.Select(fixture.Tenant);
                var listing = await browser.Get(path);
                Assert(Ids(listing).SequenceEqual(new[] { fixture.Main.AppId, fixture.Second.AppId }.Order(StringComparer.Ordinal)),
                    "Only authorized enabled published releases with trusted deployments may appear.");
                Assert(listing.GetProperty("categories").EnumerateArray().Select(x => x.GetString()).ToHashSet().SetEquals(["生产", "设备"]),
                    "Category names must be derived only from the authorized visible collection.");
                Assert(listing.EnumerateObject().Select(x => x.Name).ToHashSet().SetEquals(["items", "categories"]), "List must expose only its documented DTO.");
                foreach (var item in listing.GetProperty("items").EnumerateArray()) DisplayOnly(item);
                var detail = await browser.Get(path + "/" + fixture.Main.AppId);
                DisplayOnly(detail);
                Assert(detail.GetProperty("name").GetString() == fixture.Main.Name, "Display metadata must come from the pinned manifest, not mutable application metadata.");
                Assert(await gateway.ResolveStartAsync(subject, fixture.Tenant, fixture.Main.AppId, "1.0.0") is not null,
                    "Catalog admission and the gateway must agree on current entry permissions.");
                if (browserEnabled) await VerifyBrowser(workspace, origin, fixture.Tenant, fixture.Main.AppId);
                else Console.WriteLine("SKIP: real catalog browser acceptance (ASSEMBLEAI_PORTAL_BROWSER_TESTS is not 1).");
                foreach (var hidden in fixture.Hidden)
                    await browser.Get(path + "/" + hidden.AppId, 404);
                var unknown = await browser.Get(path + "/missing-application", 404);
                Assert(unknown.GetProperty("code").GetString() == "catalog_application_not_found", "Hidden and unknown details use the same safe error.");
                await browser.Get(path + "?search=hidden", 400);
                await browser.Get(path + "?limit=1&limit=500", 400);
                await browser.Get(path + "/INVALID", 400);
                await browser.Get($"/api/platform/v1/tenants/{fixture.OtherTenant}/catalog", 403);
                await browser.Select(fixture.OtherTenant);
                await browser.Get(path, 403);
                Assert((await browser.Get($"/api/platform/v1/tenants/{fixture.OtherTenant}/catalog")).GetProperty("items").GetArrayLength() == 0,
                    "Membership alone must not reveal another tenant's applications.");
                await browser.Select(fixture.Tenant);

                await Execute(db, "UPDATE public.role_permission_grants SET enabled=false WHERE tenant_id=$1 AND grant_id=$2", fixture.Tenant, fixture.SecondPermission);
                Assert(!Ids(await browser.Get(path)).Contains(fixture.Main.AppId), "Revoking any manifest permission must immediately remove the application.");
                await browser.Get(path + "/" + fixture.Main.AppId, 404);
                Assert(await gateway.ResolveStartAsync(subject, fixture.Tenant, fixture.Main.AppId, "1.0.0") is null,
                    "Catalog and gateway must both reject a partially authorized manifest.");
                await Execute(db, "UPDATE public.role_permission_grants SET enabled=true WHERE tenant_id=$1 AND grant_id=$2", fixture.Tenant, fixture.SecondPermission);

                await Execute(db, "UPDATE public.tenant_applications SET enabled=false,revision=revision+1 WHERE tenant_id=$1 AND app_id=$2", fixture.Tenant, fixture.Main.AppId);
                await browser.Get(path + "/" + fixture.Main.AppId, 404);
                Assert(await gateway.ResolveStartAsync(subject, fixture.Tenant, fixture.Main.AppId, "1.0.0") is null, "Tenant stop must match runtime admission.");
                await Execute(db, "UPDATE public.tenant_applications SET enabled=true,revision=revision+1 WHERE tenant_id=$1 AND app_id=$2", fixture.Tenant, fixture.Main.AppId);
                await Execute(db, "UPDATE public.platform_application_releases SET status='disabled',revision=revision+1 WHERE app_id=$1 AND release_version='1.0.0'", fixture.Main.AppId);
                await browser.Get(path + "/" + fixture.Main.AppId, 404);
                Assert(await gateway.ResolveStartAsync(subject, fixture.Tenant, fixture.Main.AppId, "1.0.0") is null, "Global stop must match runtime admission.");
                await Execute(db, "UPDATE public.platform_application_releases SET status='published',revision=revision+1 WHERE app_id=$1 AND release_version='1.0.0'", fixture.Main.AppId);

                await InsertRelease(db, fixture, fixture.Next, "published");
                await Execute(db, "UPDATE public.tenant_applications SET release_version='2.0.0',revision=revision+1 WHERE tenant_id=$1 AND app_id=$2", fixture.Tenant, fixture.Main.AppId);
                Assert((await browser.Get(path + "/" + fixture.Main.AppId)).GetProperty("releaseVersion").GetString() == "2.0.0", "Detail must follow the current tenant version.");
                Assert(await gateway.ResolveStartAsync(subject, fixture.Tenant, fixture.Main.AppId, "1.0.0") is null
                    && await gateway.ResolveStartAsync(subject, fixture.Tenant, fixture.Main.AppId, "2.0.0") is not null, "A stale catalog version must not create a run.");
                await Execute(db, "UPDATE public.tenant_applications SET release_version='1.0.0',revision=revision+1 WHERE tenant_id=$1 AND app_id=$2", fixture.Tenant, fixture.Main.AppId);

                await Execute(db, "UPDATE public.tenant_memberships SET enabled=false WHERE tenant_id=$1 AND user_id=$2", fixture.Tenant, fixture.User);
                await browser.Get(path, 403);
                await browser.Get(path + "/" + fixture.Main.AppId, 403);
                await Execute(db, "UPDATE public.tenant_memberships SET enabled=true WHERE tenant_id=$1 AND user_id=$2", fixture.Tenant, fixture.User);
                using (var other = await Browser.Login(identity, origin, fixture.OtherUser.ToString()))
                {
                    await other.Select(fixture.Tenant);
                    Assert(Ids(await other.Get(path)).Length == 0, "Another member must not inherit the first member's grants or cached results.");
                    await other.Get(path + "/" + fixture.Main.AppId, 404);
                }
                Assert(!(await catalog.ListAsync(new(identity.Authority + "/other", OidcFixture.Subject), fixture.Tenant)).IsMember, "Issuer is part of the subject identity.");
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    try { await catalog.ListAsync(subject, fixture.Tenant, cancelled.Token); throw new InvalidOperationException("Cancelled catalog query was accepted."); }
                    catch (OperationCanceledException) { }
                }
                var untrusted = new PostgresCatalogStore(configuration, authorization, new(new ConfigurationBuilder().Build()));
                Assert((await untrusted.ListAsync(subject, fixture.Tenant)).Value!.Items.Count == 0, "Removing a deployment binding must remove entries even though publication persists.");
                await SeedCapacity(db, fixture, 0, 498);
                Assert(Ids(await browser.Get(path)).Length == 500, "Exactly 500 visible applications must remain a complete successful catalog.");
                await SeedCapacity(db, fixture, 498, 1);
                var overflow = await browser.Get(path, 503);
                Assert(overflow.GetProperty("code").GetString() == "catalog_unavailable" && !overflow.TryGetProperty("items", out _),
                    "More than 500 authorized visible applications must fail explicitly, never return a silently truncated catalog.");
                await browser.Get(path + "/" + fixture.Main.AppId); // Detail does not enumerate the whole catalog.
            }
            finally { await app.StopAsync(); }
        }
        finally
        {
            await Execute(db, "DELETE FROM public.platform_applications WHERE owner_user_id=$1", fixture.User);
            await Execute(db, "DELETE FROM public.platform_tenants WHERE tenant_id=ANY($1)", new[] { fixture.Tenant, fixture.OtherTenant });
            await Execute(db, "DELETE FROM public.platform_users WHERE user_id=ANY($1)", new[] { fixture.User, fixture.OtherUser });
        }
        Console.WriteLine("PASS: PostgreSQL catalog + OIDC HTTP: selected tenant, complete grants, publication/deployment filtering, safe display DTOs, revocation, version/runtime agreement and explicit capacity failure.");
    }

    private static Dictionary<string, string?> Settings(Fixture f, string connectionString)
    {
        var settings = ManifestTests.CatalogSettings(f.Main);
        settings["ConnectionStrings:Platform"] = connectionString;
        var index = 0;
        foreach (var manifest in new[] { f.Main, f.Second, f.Next }.Concat(f.Hidden.Where(m => m != f.Untrusted)).Concat(f.Capacity))
        {
            var prefix = $"Registry:FrontendArtifacts:{index++}:";
            settings[prefix + "AppId"] = manifest.AppId;
            settings[prefix + "Version"] = manifest.FrontendArtifact.Version;
            settings[prefix + "Entry"] = manifest.FrontendEntry;
            settings[prefix + "Sha256"] = manifest.FrontendArtifact.Sha256;
        }
        return settings;
    }
    private static async Task Seed(NpgsqlConnection db, Fixture f, string issuer)
    {
        await Execute(db, "INSERT INTO public.platform_users(user_id,issuer,subject,display_name) VALUES($1,$3,$4,'Catalog member'),($2,$3,$5,'Other catalog member')", f.User, f.OtherUser, issuer, OidcFixture.Subject, f.OtherUser.ToString());
        await Execute(db, "INSERT INTO public.platform_tenants(tenant_id,name) VALUES($1,'Catalog manufacturing'),($2,'Catalog other tenant')", f.Tenant, f.OtherTenant);
        await Execute(db, "INSERT INTO public.tenant_memberships(tenant_id,user_id) VALUES($1,$3),($2,$3),($1,$4)", f.Tenant, f.OtherTenant, f.User, f.OtherUser);
        await Execute(db, "INSERT INTO public.tenant_roles(tenant_id,role_id,name) VALUES($1,$2,'Catalog user')", f.Tenant, f.Role);
        await Execute(db, "INSERT INTO public.tenant_member_roles(tenant_id,user_id,role_id) VALUES($1,$2,$3)", f.Tenant, f.User, f.Role);
        foreach (var manifest in new[] { f.Main, f.Second }.Concat(f.Hidden))
        {
            await Execute(db, "INSERT INTO public.platform_applications(app_id,name,category,owner_user_id) VALUES($1,'Mutable label must not leak','Mutable category',$2)", manifest.AppId, f.User);
            var status = manifest == f.Draft ? "draft" : manifest == f.Pending ? "pending_review" : manifest == f.Disabled ? "disabled" : "published";
            await InsertRelease(db, f, manifest, status);
            await Execute(db, "INSERT INTO public.tenant_applications(tenant_id,app_id,release_version,enabled) VALUES($1,$2,'1.0.0',$3)",
                manifest == f.Foreign ? f.OtherTenant : f.Tenant, manifest.AppId, manifest != f.TenantOff);
            await Execute(db, "INSERT INTO public.role_permission_grants(tenant_id,grant_id,role_id,permission,resource_type,resource_id,tenant_wide) VALUES($1,$2,$3,'production:read',$4,$5,$6)",
                f.Tenant, Guid.NewGuid(), f.Role, manifest == f.WrongType ? "dataset" : "application", manifest.AppId, manifest != f.OrgOnly);
        }
        await Execute(db, "INSERT INTO public.role_permission_grants(tenant_id,grant_id,role_id,permission,resource_type,resource_id,tenant_wide) VALUES($1,$2,$3,'reports:use','application',$4,true)",
            f.Tenant, f.SecondPermission, f.Role, f.Main.AppId);
    }
    private static Task InsertRelease(NpgsqlConnection db, Fixture f, ApplicationManifest manifest, string status) => Execute(db,
        "INSERT INTO public.platform_application_releases(app_id,release_version,manifest,status,submitted_by,published_at) VALUES($1,$2,$3::jsonb,$4,$5,CASE WHEN $4 IN ('published','disabled') THEN now() ELSE NULL END)",
        manifest.AppId, manifest.Version, ManifestValidator.Serialize(manifest), status, f.User);

    private static async Task SeedCapacity(NpgsqlConnection db, Fixture f, int start, int count)
    {
        // One transaction and set-based inserts avoid turning this into a startup/load test.
        await using var tx = await db.BeginTransactionAsync();
        var names = f.Capacity.Skip(start).Take(count).Select(m => m.AppId).ToArray();
        foreach (var sql in new[] {
            "INSERT INTO public.platform_applications(app_id,name,category,owner_user_id) SELECT id,'Capacity fixture','生产',$2 FROM unnest($1::text[]) id",
            "INSERT INTO public.platform_application_releases(app_id,release_version,manifest,status,submitted_by,published_at) SELECT id,'1.0.0',jsonb_set(jsonb_set($3::jsonb,'{appId}',to_jsonb(id)),'{frontendEntry}',to_jsonb('/apps/' || id || '/1.0.0/')),'published',$2,now() FROM unnest($1::text[]) id",
            "INSERT INTO public.tenant_applications(tenant_id,app_id,release_version,enabled) SELECT $4,id,'1.0.0',true FROM unnest($1::text[]) id",
            "INSERT INTO public.role_permission_grants(tenant_id,grant_id,role_id,permission,resource_type,resource_id,tenant_wide) SELECT $4,gen_random_uuid(),$5,'production:read','application',id,true FROM unnest($1::text[]) id"
        })
        {
            await using var command = new NpgsqlCommand(sql, db, tx);
            command.Parameters.AddWithValue(names); command.Parameters.AddWithValue(f.User);
            command.Parameters.AddWithValue(ManifestValidator.Serialize(f.Capacity[0]));
            command.Parameters.AddWithValue(f.Tenant); command.Parameters.AddWithValue(f.Role);
            await command.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }
    private static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AssembleAI.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Cannot locate the catalog browser fixture workspace.");
    }
    private static async Task VerifyBrowser(string workspace, string origin, Guid tenantId, string appId)
    {
        var script = Path.Combine(workspace, "tests", "frontend", "portal-postgres-browser.mjs");
        Assert(File.Exists(script), "The catalog browser acceptance script is missing.");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ASSEMBLEAI_NODE") ?? "node")
        {
            WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(script); start.ArgumentList.Add(origin);
        start.ArgumentList.Add(tenantId.ToString("D")); start.ArgumentList.Add(appId);
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("ASSEMBLEAI_POSTGRES", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("ConnectionStrings", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start catalog browser acceptance.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new InvalidOperationException("Catalog browser acceptance exceeded its time budget.");
        }
        var transcript = await output + await errors;
        Console.Write(transcript.Length > 20000 ? transcript[..20000] : transcript);
        Assert(process.ExitCode == 0, "Real catalog browser acceptance failed.");
    }
    private static string[] Ids(JsonElement listing) => listing.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("appId").GetString()!).Order(StringComparer.Ordinal).ToArray();
    private static void DisplayOnly(JsonElement item) => Assert(item.EnumerateObject().Select(p => p.Name).ToHashSet().SetEquals(["appId", "name", "releaseVersion", "category", "description"]), "Catalog DTO must not expose service, permissions, runtime credentials or manifest internals.");
    private static async Task Execute(NpgsqlConnection db, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, db);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }
    private static string FreeOrigin()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        return "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Browser(HttpClient client, string origin, string csrf) : IDisposable
    {
        public static async Task<Browser> Login(OidcFixture identity, string origin, string subject)
        {
            identity.NextSubject = subject;
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new() }) { BaseAddress = new(origin), Timeout = TimeSpan.FromSeconds(10) };
            try
            {
                using var challenge = await client.GetAsync("/bff/login?returnUrl=/bff/session");
                Assert(challenge.StatusCode == HttpStatusCode.Redirect, "Expected OIDC challenge.");
                using var code = await client.GetAsync(challenge.Headers.Location);
                Assert(code.StatusCode == HttpStatusCode.Redirect, "Expected authorization code.");
                using var callback = await client.GetAsync(code.Headers.Location);
                Assert(callback.StatusCode == HttpStatusCode.Redirect, "Expected validated callback.");
                using var response = await client.GetAsync("/bff/csrf");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return new(client, origin, json.RootElement.GetProperty("requestToken").GetString()!);
            }
            catch { client.Dispose(); throw; }
        }
        public async Task Select(Guid tenant)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/tenant") { Content = JsonContent.Create(new { tenantId = tenant }) };
            request.Headers.Add("Origin", origin); request.Headers.Add(BffEndpoints.CsrfHeaderName, csrf);
            using var response = await client.SendAsync(request);
            Assert(response.StatusCode == HttpStatusCode.OK, "Expected selected tenant.");
        }
        public async Task<JsonElement> Get(string path, int status = 200)
        {
            using var response = await client.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();
            Assert((int)response.StatusCode == status, $"Catalog expected {status}, got {(int)response.StatusCode} for {path}.");
            Assert(response.Headers.CacheControl?.NoStore == true && response.Headers.TryGetValues("X-Content-Type-Options", out var values) && values.Single() == "nosniff", "Catalog responses must prohibit caching and content sniffing.");
            Assert(!body.Contains(OidcFixture.AccessToken) && !body.Contains(OidcFixture.RefreshToken) && !body.Contains("127.0.0.1:9876"), "Catalog must not disclose credentials or destinations.");
            using var json = JsonDocument.Parse(body); return json.RootElement.Clone();
        }
        public void Dispose() => client.Dispose();
    }
    private sealed class Fixture
    {
        private readonly string prefix = "catalog-" + Guid.NewGuid().ToString("N");
        public Guid User { get; } = Guid.NewGuid();
        public Guid OtherUser { get; } = Guid.NewGuid();
        public Guid Tenant { get; } = Guid.NewGuid();
        public Guid OtherTenant { get; } = Guid.NewGuid();
        public Guid Role { get; } = Guid.NewGuid();
        public Guid SecondPermission { get; } = Guid.NewGuid();
        public ApplicationManifest Main { get; }
        public ApplicationManifest Second { get; }
        public ApplicationManifest Next { get; }
        public ApplicationManifest Draft { get; }
        public ApplicationManifest Pending { get; }
        public ApplicationManifest Disabled { get; }
        public ApplicationManifest TenantOff { get; }
        public ApplicationManifest Untrusted { get; }
        public ApplicationManifest Partial { get; }
        public ApplicationManifest OrgOnly { get; }
        public ApplicationManifest WrongType { get; }
        public ApplicationManifest Foreign { get; }
        public ApplicationManifest[] Hidden { get; }
        public ApplicationManifest[] Capacity { get; }
        public Fixture()
        {
            Main = Make("main") with { Name = "生产经营报表", Category = "生产", Permissions = ["production:read", "reports:use"] };
            Second = Make("second") with { Name = "设备运行分析", Category = "设备" };
            Next = Main with { Version = "2.0.0", FrontendEntry = $"/apps/{Main.AppId}/2.0.0/", FrontendArtifact = new("2.0.0", new string('a', 64)) };
            Draft = Make("draft"); Pending = Make("pending"); Disabled = Make("disabled"); TenantOff = Make("tenant-off");
            Untrusted = Make("untrusted"); Partial = Make("partial") with { Permissions = ["production:read", "reports:use"] };
            OrgOnly = Make("org-only"); WrongType = Make("wrong-type"); Foreign = Make("foreign");
            Hidden = [Draft, Pending, Disabled, TenantOff, Untrusted, Partial, OrgOnly, WrongType, Foreign];
            Capacity = Enumerable.Range(0, 501).Select(index => Make("cap-" + index) with { Category = "生产" }).ToArray();
        }
        private ApplicationManifest Make(string suffix) => ManifestTests.Valid(prefix + "-" + suffix) with { Category = "未授权分类" };
    }
}
