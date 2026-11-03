using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.InternalCalls;
using AssembleAI.Platform.Registry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AssembleAI.Platform.SmokeTests;

// Opt-in through the marked disposable PostgreSQL dispatcher. Three actual OIDC
// browser sessions mutate the production registry; no UI/API interception.
public static class PostgresRegistryBrowserTests
{
    public static async Task RunAsync(string connectionString, string runDirectory)
    {
        var root = Workspace(); var fixture = new Fixture();
        var directory = Path.Combine(runDirectory, "registry-browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var node = Environment.GetEnvironmentVariable("ASSEMBLEAI_NODE") ?? "node";
        var previewScript = Path.Combine(root, "tests/frontend/registry-preview-fixture.mjs");
        await Run(node, root, [previewScript, "prepare", directory, fixture.AppId], 90);
        using var artifacts = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "artifacts.json")));
        var manifests = artifacts.RootElement.GetProperty("artifacts").EnumerateArray().Select(item =>
            ManifestTests.Valid(fixture.AppId, item.GetProperty("version").GetString()!) with
            { Name = "制造流程助手", FrontendArtifact = new(item.GetProperty("version").GetString()!, item.GetProperty("sha256").GetString()!) }).ToArray();
        await using var identity = await OidcFixture.StartAsync(new Dictionary<string, string>
        { [fixture.Owner.ToString()] = "研发账号", [fixture.Reviewer.ToString()] = "审核账号", [fixture.Admin.ToString()] = "租户管理员" });
        await using var db = new NpgsqlConnection(connectionString); await db.OpenAsync();
        try
        {
            await Seed(db, fixture, identity.Authority);
            using var rsa = RSA.Create(2048);
            using var certificate = new CertificateRequest("CN=registry-browser", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var password = Guid.NewGuid().ToString("N");
            var pfx = Path.Combine(directory, "signer.pfx"); var cer = Path.Combine(directory, "signer.cer");
            await File.WriteAllBytesAsync(pfx, certificate.Export(X509ContentType.Pfx, password));
            await File.WriteAllBytesAsync(cer, certificate.Export(X509ContentType.Cert));
            var authConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Platform"] = connectionString }).Build();
            var keysOptions = new InternalCallOptions { Enabled = true, Issuer = "https://platform.example/internal", ValidationCertificatePaths = [cer] };
            using var keys = new InternalCallKeys(keysOptions);
            var verifier = new InternalCallTokenService(keysOptions, keys, new PostgresAuthorizationStore(authConfiguration), TimeProvider.System);
            var downstreamBuilder = WebApplication.CreateBuilder(["--environment", "Development", "--urls", "http://127.0.0.1:0"]);
            downstreamBuilder.Logging.ClearProviders();
            await using var downstream = downstreamBuilder.Build();
            var activeStreams = 0; var cancelledStreams = 0;
            downstream.MapGet("/health/ready", () => Results.Json(new { status = "healthy", service = "production-api", version = "1.0.0", protocolVersion = 1 }));
            downstream.MapMethods("/{operation}", ["GET", "POST"], async (HttpContext context) =>
            {
                var operation = context.Request.Path.Value!;
                if (operation is not ("/probe" or "/events")) { context.Response.StatusCode = 404; return; }
                var id = context.Request.Headers["X-Request-ID"].ToString();
                var header = context.Request.Headers.Authorization;
                if (header.Count != 1 || !AuthenticationHeaderValue.TryParse(header[0], out var credential)
                    || credential.Scheme != "Bearer" || credential.Parameter is null) { context.Response.StatusCode = 403; return; }
                VerifiedInternalCall? call = null; string? release = null;
                foreach (var manifest in manifests)
                {
                    call = await verifier.ValidateAsync(credential.Parameter,
                        new(fixture.AppId, manifest.Version, "assembleai:production-api", "production:read",
                            new(fixture.Tenant, "application", fixture.AppId, null), context.Request.Method, operation), id, context.RequestAborted);
                    if (call is not null) { release = manifest.Version; break; }
                }
                if (call is null) { context.Response.StatusCode = 403; return; }
                if (operation == "/probe")
                {
                    await context.Response.WriteAsJsonAsync(new { status = "verified", call.UserId, call.TenantId, call.RequestId, appId = fixture.AppId, releaseVersion = release }, context.RequestAborted);
                    return;
                }
                Interlocked.Increment(ref activeStreams);
                context.Response.ContentType = "text/event-stream";
                try
                {
                    var index = 0;
                    while (!context.RequestAborted.IsCancellationRequested)
                    {
                        await context.Response.WriteAsync("event: delta\ndata: " + JsonSerializer.Serialize(new { index = index++, text = "验收增量", requestId = id }) + "\n\n", context.RequestAborted);
                        await context.Response.Body.FlushAsync(context.RequestAborted);
                        await Task.Delay(200, context.RequestAborted);
                    }
                }
                catch (Exception error) when (error is OperationCanceledException or IOException) { }
                finally { Interlocked.Decrement(ref activeStreams); Interlocked.Increment(ref cancelledStreams); }
            });
            await downstream.StartAsync();
            try
            {
                var platformOrigin = FreeOrigin(); var publicOrigin = FreeOrigin();
                var settings = Settings(connectionString, downstream.Urls.Single(), manifests);
                await using var platform = PlatformApplication.Build([
                    "--environment", "Development", "--urls", platformOrigin,
                    "--Authentication:Bff:Enabled", "true", "--Authentication:Bff:Authority", identity.Authority,
                    "--Authentication:Bff:ClientId", OidcFixture.ClientId, "--Authentication:Bff:ClientSecret", OidcFixture.ClientSecret,
                    "--Authentication:Bff:PublicOrigin", publicOrigin,
                    "--InternalCalls:Enabled", "true", "--InternalCalls:Issuer", keysOptions.Issuer,
                    "--InternalCalls:SigningCertificatePath", pfx, "--InternalCalls:SigningCertificatePassword", password
                ], builder => { builder.Logging.ClearProviders(); builder.Configuration.AddInMemoryCollection(settings); });
                await platform.StartAsync();
                try
                {
                    var browserConfiguration = Path.Combine(directory, "browser.json");
                    await File.WriteAllTextAsync(browserConfiguration, JsonSerializer.Serialize(new
                    {
                        origin = publicOrigin, appId = fixture.AppId, tenantId = fixture.Tenant, ownerId = fixture.Owner,
                        reviewerId = fixture.Reviewer, adminId = fixture.Admin,
                        manifests = manifests.Select(value => JsonSerializer.Deserialize<JsonElement>(ManifestValidator.Serialize(value)))
                    }));
                    await using var preview = Child.Start(node, root, [previewScript, "serve", directory, platformOrigin, publicOrigin]);
                    await Ready(preview, publicOrigin);
                    await Run(node, root, [Path.Combine(root, "tests/frontend/registry-postgres-browser.mjs"), browserConfiguration], 240);
                    using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    while (Volatile.Read(ref activeStreams) != 0) await Task.Delay(100, drain.Token);
                    Assert(cancelledStreams >= 3, "Version drain, global disable and tenant disable must reach real downstream disconnects.");
                    await VerifyAudit(db, fixture);
                }
                finally { await platform.StopAsync(); }
            }
            finally { await downstream.StopAsync(); }
        }
        finally
        {
            await Execute(db, "DELETE FROM public.platform_gateway_calls WHERE app_id=$1", fixture.AppId);
            await Execute(db, "DELETE FROM public.platform_applications WHERE app_id=$1", fixture.AppId);
            await Execute(db, "DELETE FROM public.platform_registry_audit WHERE app_id=$1", fixture.AppId);
            await Execute(db, "DELETE FROM public.platform_tenants WHERE tenant_id=$1", fixture.Tenant);
            await Execute(db, "DELETE FROM public.platform_users WHERE user_id IN ($1,$2,$3)", fixture.Owner, fixture.Reviewer, fixture.Admin);
        }
        Console.WriteLine("PASS: registry management real browser/OIDC/PostgreSQL: independent author/reviewer/admin, draft/review/publication, tenant version switch/rollback, live stream drain/stop and attributable audit.");
    }
    private static Dictionary<string, string?> Settings(string connection, string downstream, ApplicationManifest[] manifests)
    {
        var settings = ManifestTests.CatalogSettings(manifests[0]);
        settings["ConnectionStrings:Platform"] = connection; settings["Registry:BackendBindings:0:Destination"] = downstream;
        settings["Gateway:Enabled"] = "true"; settings["Gateway:DrainSeconds"] = "4"; settings["Gateway:RecheckSeconds"] = "1";
        settings["Gateway:CallSeconds"] = "60"; settings["Gateway:IdleSeconds"] = "10";
        for (var index = 0; index < manifests.Length; index++)
        {
            var prefix = $"Registry:FrontendArtifacts:{index}:"; var manifest = manifests[index];
            settings[prefix + "AppId"] = manifest.AppId; settings[prefix + "Version"] = manifest.FrontendArtifact.Version;
            settings[prefix + "Entry"] = manifest.FrontendEntry; settings[prefix + "Sha256"] = manifest.FrontendArtifact.Sha256;
        }
        for (var index = 0; index < 2; index++)
        {
            var prefix = $"Gateway:Operations:{index}:";
            settings[prefix + "Service"] = "production-api"; settings[prefix + "Version"] = "1.0.0";
            settings[prefix + "Method"] = index == 0 ? "POST" : "GET"; settings[prefix + "Path"] = index == 0 ? "/probe" : "/events";
            settings[prefix + "Permission"] = "production:read";
        }
        return settings;
    }
    private static async Task Seed(NpgsqlConnection db, Fixture f, string issuer)
    {
        foreach (var (id, name) in new[] { (f.Owner, "研发账号"), (f.Reviewer, "审核账号"), (f.Admin, "租户管理员") })
            await Execute(db, "INSERT INTO public.platform_users(user_id,issuer,subject,display_name) VALUES($1,$2,$3,$4)", id, issuer, id.ToString(), name);
        await Execute(db, "INSERT INTO public.platform_registry_operators(user_id,role) VALUES($1,'developer'),($1,'reviewer'),($2,'reviewer')", f.Owner, f.Reviewer);
        await Execute(db, "INSERT INTO public.platform_tenants(tenant_id,name) VALUES($1,'T8 制造租户')", f.Tenant);
        await Execute(db, "INSERT INTO public.tenant_memberships(tenant_id,user_id) VALUES($1,$2)", f.Tenant, f.Admin);
        await Execute(db, "INSERT INTO public.tenant_roles(tenant_id,role_id,name) VALUES($1,$2,'Registry manager')", f.Tenant, f.Role);
        await Execute(db, "INSERT INTO public.tenant_member_roles(tenant_id,user_id,role_id) VALUES($1,$2,$3)", f.Tenant, f.Admin, f.Role);
        foreach (var permission in new[] { "registry:manage", "production:read" })
            await Execute(db, "INSERT INTO public.role_permission_grants(tenant_id,grant_id,role_id,permission,resource_type,resource_id,tenant_wide) VALUES($1,$2,$3,$4,'application',$5,true)", f.Tenant, Guid.NewGuid(), f.Role, permission, f.AppId);
    }
    private static async Task VerifyAudit(NpgsqlConnection db, Fixture f)
    {
        await using var command = new NpgsqlCommand("SELECT actor_user_id,tenant_id,action,result,request_id FROM public.platform_registry_audit WHERE app_id=$1", db);
        command.Parameters.AddWithValue(f.AppId); await using var reader = await command.ExecuteReaderAsync();
        var actions = new Dictionary<string, int>();
        var requestIds = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            var action = reader.GetString(2); var actor = reader.GetGuid(0);
            // Registry records the server's HttpContext.TraceIdentifier. The
            // gateway's separate 32-hex correlation format is not this contract.
            var requestId = reader.GetString(4);
            Assert(!string.IsNullOrWhiteSpace(requestId) && requestId.Length <= 128 && !requestId.Any(char.IsControl)
                && requestIds.Add(requestId), "Registry audit must retain a distinct bounded server request identifier for every write.");
            if (reader.GetString(3) != "success") continue;
            actions[action] = actions.GetValueOrDefault(action) + 1;
            Assert(actor == (action is "save_draft" or "submit" ? f.Owner : action == "set_tenant" ? f.Admin : f.Reviewer), "The committed audit actor must match the independently logged-in operator.");
            Assert(action == "set_tenant" ? !reader.IsDBNull(1) && reader.GetGuid(1) == f.Tenant : reader.IsDBNull(1), "Global and tenant audit scopes cannot be conflated.");
        }
        Assert(actions.GetValueOrDefault("save_draft") >= 2 && actions.GetValueOrDefault("submit") == 2 && actions.GetValueOrDefault("publish") == 2
            && actions.GetValueOrDefault("set_tenant") >= 5 && actions.GetValueOrDefault("disable") == 1 && actions.GetValueOrDefault("enable") == 1, "UI flow must leave complete durable lifecycle audit.");
    }
    private static async Task Run(string file, string cwd, string[] args, int seconds)
    {
        await using var child = Child.Start(file, cwd, args); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        await child.Process.WaitForExitAsync(deadline.Token);
        var output = await child.Output + await child.Errors; Console.Write(output.Length > 20000 ? output[..20000] : output);
        Assert(child.Process.ExitCode == 0, "Registry browser fixture command failed.");
    }
    private static async Task Ready(Child child, string url)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) }; using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!deadline.IsCancellationRequested)
        {
            Assert(!child.Process.HasExited, "Registry preview ended before readiness.");
            try { using var response = await client.GetAsync(url, deadline.Token); if (response.IsSuccessStatusCode) return; }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { }
            await Task.Delay(100, deadline.Token);
        }
        throw new InvalidOperationException("Registry preview readiness timed out.");
    }
    private static async Task Execute(NpgsqlConnection db, string sql, params object[] values)
    { await using var command = new NpgsqlCommand(sql, db); foreach (var value in values) command.Parameters.AddWithValue(value); await command.ExecuteNonQueryAsync(); }
    private static string FreeOrigin() { using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); return "http://127.0.0.1:" + ((IPEndPoint)socket.LocalEndpoint).Port; }
    private static string Workspace() { for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent) if (File.Exists(Path.Combine(d.FullName, "AssembleAI.slnx"))) return d.FullName; throw new InvalidOperationException("Missing workspace."); }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Fixture
    {
        public string AppId { get; } = "registry-browser-" + Guid.NewGuid().ToString("N");
        public Guid Owner { get; } = Guid.NewGuid(); public Guid Reviewer { get; } = Guid.NewGuid(); public Guid Admin { get; } = Guid.NewGuid();
        public Guid Tenant { get; } = Guid.NewGuid(); public Guid Role { get; } = Guid.NewGuid();
    }
    private sealed class Child : IAsyncDisposable
    {
        public required Process Process { get; init; } public required Task<string> Output { get; init; } public required Task<string> Errors { get; init; }
        public static Child Start(string file, string cwd, string[] args)
        {
            var start = new ProcessStartInfo(file) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("ASSEMBLEAI_POSTGRES", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("ConnectionStrings", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
            var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start registry fixture.");
            return new() { Process = process, Output = process.StandardOutput.ReadToEndAsync(), Errors = process.StandardError.ReadToEndAsync() };
        }
        public async ValueTask DisposeAsync() { if (!Process.HasExited) Process.Kill(entireProcessTree: true); await Process.WaitForExitAsync(); Process.Dispose(); }
    }
}
