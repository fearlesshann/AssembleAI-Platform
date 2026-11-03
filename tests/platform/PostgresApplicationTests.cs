using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Identity;
using AssembleAI.Platform.InternalCalls;
using AssembleAI.Platform.Registry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AssembleAI.Platform.SmokeTests;

// Only the dispatcher that verifies the disposable database marker calls this.
// No store, OIDC response, browser route or downstream credential check is mocked.
public static class PostgresApplicationTests
{
    public static async Task RunAsync(string connectionString, string runDirectory)
    {
        var workspace = FindWorkspace();
        var directory = Path.Combine(runDirectory, "application-chain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var node = Environment.GetEnvironmentVariable("ASSEMBLEAI_NODE") ?? "node";
        var python = Environment.GetEnvironmentVariable("ASSEMBLEAI_PYTHON")
            ?? Path.Combine(workspace, "apps", "sample", "backend", ".venv", "Scripts", "python.exe");
        Assert(File.Exists(python), "Application acceptance requires the locked project Python venv.");
        var previewScript = Path.Combine(workspace, "tests", "frontend", "application-preview-fixture.mjs");
        await RunProcess(node, workspace, [previewScript, "prepare", directory], 90);
        using var artifacts = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "artifacts.json")));
        var manifests = artifacts.RootElement.GetProperty("artifacts").EnumerateArray().Select(artifact => new ApplicationManifest(
            1, artifact.GetProperty("appId").GetString()!, "接入样例 " + artifact.GetProperty("appId").GetString(),
            artifact.GetProperty("appId").GetString() == "sample" ? "1.0.0" : "2.0.0",
            $"/apps/{artifact.GetProperty("appId").GetString()}/{artifact.GetProperty("version").GetString()}/",
            "sample", ["sample:use"], "draft", new(artifact.GetProperty("version").GetString()!, artifact.GetProperty("sha256").GetString()!),
            "0.1.0", "接入验证", "同一模板的独立部署与真实受保护调用")).ToArray();
        await using var identity = await OidcFixture.StartAsync();
        var fixture = new Fixture();
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();
        try
        {
            await Seed(db, fixture, identity.Authority, manifests);
            var platformOrigin = FreeOrigin();
            var publicOrigin = FreeOrigin();
            var backendOrigin = FreeOrigin();
            using var rsa = RSA.Create(2048);
            using var certificate = new CertificateRequest("CN=postgres-application-chain", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var pfx = Path.Combine(directory, "signer.pfx");
            var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await File.WriteAllBytesAsync(pfx, certificate.Export(X509ContentType.Pfx, password));
            var settings = Settings(connectionString, backendOrigin, manifests);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var authorization = new PostgresAuthorizationStore(configuration);
            var registry = new PostgresRegistryStore(configuration, new(configuration), authorization);
            var owner = new SubjectIdentity(identity.Authority, OidcFixture.Subject);
            var reviewer = new SubjectIdentity(identity.Authority, fixture.Reviewer.ToString());
            foreach (var manifest in manifests)
            {
                Ok(await registry.SaveDraftAsync(owner, manifest, 0, Id()));
                Ok(await registry.TransitionAsync(owner, manifest.AppId, manifest.Version, "submit", 1, Id()));
                Ok(await registry.TransitionAsync(reviewer, manifest.AppId, manifest.Version, "publish", 2, Id()));
                Ok(await registry.SetTenantApplicationAsync(owner, fixture.Tenant, manifest.AppId, manifest.Version, true, 0, Id()));
            }
            await using var platform = PlatformApplication.Build([
                "--environment", "Development", "--urls", platformOrigin,
                "--Authentication:Bff:Enabled", "true", "--Authentication:Bff:Authority", identity.Authority,
                "--Authentication:Bff:ClientId", OidcFixture.ClientId, "--Authentication:Bff:ClientSecret", OidcFixture.ClientSecret,
                "--Authentication:Bff:PublicOrigin", publicOrigin,
                "--InternalCalls:Enabled", "true", "--InternalCalls:ValidationEndpointEnabled", "true",
                "--InternalCalls:Issuer", "https://platform.example.test/internal",
                "--InternalCalls:SigningCertificatePath", pfx, "--InternalCalls:SigningCertificatePassword", password
            ], builder => { builder.Logging.ClearProviders(); builder.Configuration.AddInMemoryCollection(settings); });
            await platform.StartAsync();
            try
            {
                await using var backend = Child.Start(python, Path.Combine(workspace, "apps", "sample", "backend"),
                    ["-m", "uvicorn", "sample_backend.main:app", "--host", "127.0.0.1", "--port", new Uri(backendOrigin).Port.ToString(), "--log-level", "error", "--no-access-log"],
                    new() { ["ASSEMBLEAI_ENVIRONMENT"] = "Development", ["ASSEMBLEAI_VALIDATION_URL"] = platformOrigin + "/internal/calls/validate",
                        ["ASSEMBLEAI_SERVICE"] = "sample", ["ASSEMBLEAI_SERVICE_VERSION"] = "0.1.0" });
                await WaitReady(backend, backendOrigin + "/health/ready");
                await using var preview = Child.Start(node, workspace, [previewScript, "serve", directory, platformOrigin, publicOrigin]);
                await WaitReady(preview, publicOrigin + "/");
                using var direct = new HttpClient { BaseAddress = new Uri(backendOrigin), Timeout = TimeSpan.FromSeconds(8) };
                using (var forged = new HttpRequestMessage(HttpMethod.Post, "/probe") { Content = JsonContent.Create(new { message = "forged" }) })
                {
                    forged.Headers.Add("X-User-ID", fixture.Owner.ToString()); forged.Headers.Add("X-Tenant-ID", fixture.Tenant.ToString());
                    using var response = await direct.SendAsync(forged);
                    Assert(response.StatusCode == HttpStatusCode.Unauthorized, "Direct Python calls cannot trust caller-supplied identity headers.");
                }
                using (var browser = await Browser.Login(publicOrigin))
                {
                    using (var selected = await browser.Send("POST", "/bff/tenant", new { tenantId = fixture.Tenant })) await Expect(selected, 200);
                    browser.Tenant = fixture.Tenant;
                    foreach (var manifest in manifests)
                    {
                        var runPath = $"/api/platform/v1/tenants/{fixture.Tenant}/applications/{manifest.AppId}/releases/{manifest.Version}/runs";
                        using (var created = await browser.Send("POST", runPath))
                        {
                            using var result = JsonDocument.Parse(await Expect(created, 200));
                            browser.Run = result.RootElement.GetProperty("runId").GetGuid();
                        }
                        var api = $"/api/apps/{manifest.AppId}/releases/{manifest.Version}";
                        using (var response = await browser.Send("POST", api + "/probe", new { message = "真实受保护调用" }))
                        {
                            using var result = JsonDocument.Parse(await Expect(response, 200));
                            AssertContext(result.RootElement, fixture, manifest);
                            Assert(result.RootElement.GetProperty("requestId").GetString() == response.Headers.GetValues("X-Request-ID").Single(),
                                "Gateway, platform validator and Python must agree on the original request identity.");
                        }
                        var streamStarted = Stopwatch.StartNew();
                        using var stream = await browser.Send("GET", api + "/events?count=3&delay_ms=1000");
                        Assert(stream.StatusCode == HttpStatusCode.OK, "Real Python SSE must pass the gateway.");
                        using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync());
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                        Assert(await reader.ReadLineAsync(deadline.Token) == "event: delta", "First SSE event must be incrementally visible.");
                        Assert(streamStarted.Elapsed < TimeSpan.FromSeconds(1.5), "The first event cannot wait for the complete two-second stream.");
                        var first = await reader.ReadLineAsync(deadline.Token);
                        Assert(first?.Contains("requestId", StringComparison.Ordinal) == true, "SSE must preserve correlation.");
                        var remainder = await reader.ReadToEndAsync(deadline.Token);
                        Assert(remainder.Contains("event: complete", StringComparison.Ordinal), "The real Python stream needs explicit completion.");
                    }
                }
                await VerifyConsumerRevocation(db, direct, platform.Services.GetRequiredService<InternalCallTokenService>(), identity.Authority, fixture, manifests[0]);
                await RunProcess(node, workspace, [Path.Combine(workspace, "tests", "frontend", "application-postgres-browser.mjs"), publicOrigin,
                    fixture.Tenant.ToString("D"), fixture.Owner.ToString("D")], 150);
            }
            finally { await platform.StopAsync(); }
        }
        finally
        {
            foreach (var manifest in manifests)
            {
                await Execute(db, "DELETE FROM public.platform_gateway_calls WHERE app_id=$1", manifest.AppId);
                await Execute(db, "DELETE FROM public.platform_applications WHERE app_id=$1", manifest.AppId);
                await Execute(db, "DELETE FROM public.platform_registry_audit WHERE app_id=$1", manifest.AppId);
            }
            await Execute(db, "DELETE FROM public.platform_tenants WHERE tenant_id=$1", fixture.Tenant);
            await Execute(db, "DELETE FROM public.platform_users WHERE user_id IN ($1,$2)", fixture.Owner, fixture.Reviewer);
        }
        Console.WriteLine("PASS: PostgreSQL application chain: two real artifact registrations, independent backend/release versions, OIDC/BFF/YARP/Python JSON/SSE and consumer-side grant/tenant/publication revocation.");
    }

    private static async Task VerifyConsumerRevocation(NpgsqlConnection db, HttpClient direct, InternalCallTokenService issuer,
        string authority, Fixture fixture, ApplicationManifest manifest)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("iss", authority), new Claim("sub", OidcFixture.Subject)], "ValidatedFixture"));
        var target = new InternalCallTarget(manifest.AppId, manifest.Version, "assembleai:sample", "sample:use",
            new(fixture.Tenant, "application", manifest.AppId, null), "POST", "/probe");
        async Task<(string Token, string Id)> Issue()
        {
            var id = Id();
            var token = await issuer.IssueAsync(principal, fixture.Tenant, target, id);
            Assert(token is not null, "Real PostgreSQL permissions must allow credential issuance.");
            return (token!, id);
        }
        async Task Send((string Token, string Id) credential, int expected, string path = "/probe")
        {
            using var request = new HttpRequestMessage(path == "/probe" ? HttpMethod.Post : HttpMethod.Get, path);
            if (path == "/probe") request.Content = JsonContent.Create(new { message = "consumer authorization" });
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);
            request.Headers.Add("X-Request-ID", credential.Id);
            request.Headers.Add("X-Tenant-ID", Guid.NewGuid().ToString());
            using var response = await direct.SendAsync(request);
            Assert((int)response.StatusCode == expected, $"Python consumer policy check expected {expected}, got {(int)response.StatusCode}.");
            if (expected == 200)
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                AssertContext(body.RootElement, fixture, manifest);
            }
        }
        var credential = await Issue();
        await Send(credential, 200);
        await Send(credential, 403, "/events"); // Signed POST/path cannot become a different operation.
        await Send((credential.Token, Id()), 403);
        await Execute(db, "UPDATE public.role_permission_grants SET enabled=false WHERE tenant_id=$1 AND permission='sample:use' AND resource_id=$2", fixture.Tenant, manifest.AppId);
        await Send(credential, 403);
        await Execute(db, "UPDATE public.role_permission_grants SET enabled=true WHERE tenant_id=$1 AND permission='sample:use' AND resource_id=$2", fixture.Tenant, manifest.AppId);
        await Send(credential, 403); // Re-enabling cannot resurrect the old policy version.
        credential = await Issue();
        await Send(credential, 200);
        await Execute(db, "UPDATE public.tenant_applications SET enabled=false,revision=revision+1 WHERE tenant_id=$1 AND app_id=$2", fixture.Tenant, manifest.AppId);
        await Send(credential, 403);
        await Execute(db, "UPDATE public.tenant_applications SET enabled=true,revision=revision+1 WHERE tenant_id=$1 AND app_id=$2", fixture.Tenant, manifest.AppId);
        credential = await Issue();
        await Send(credential, 200);
        await Execute(db, "UPDATE public.platform_application_releases SET status='disabled',revision=revision+1 WHERE app_id=$1 AND release_version=$2", manifest.AppId, manifest.Version);
        await Send(credential, 403);
        await Execute(db, "UPDATE public.platform_application_releases SET status='published',revision=revision+1 WHERE app_id=$1 AND release_version=$2", manifest.AppId, manifest.Version);
        await Send(await Issue(), 200);
    }

    private static void AssertContext(JsonElement result, Fixture fixture, ApplicationManifest manifest) => Assert(
        result.GetProperty("status").GetString() == "verified" && result.GetProperty("userId").GetGuid() == fixture.Owner
        && result.GetProperty("tenantId").GetGuid() == fixture.Tenant && result.GetProperty("appId").GetString() == manifest.AppId
        && result.GetProperty("releaseVersion").GetString() == manifest.Version,
        "Python identity must come from real signature/current-policy validation and the registered release.");

    private static Dictionary<string, string?> Settings(string connectionString, string backendOrigin, ApplicationManifest[] manifests)
    {
        var values = ManifestTests.CatalogSettings(manifests[0]);
        values["ConnectionStrings:Platform"] = connectionString;
        values["Registry:BackendBindings:0:Destination"] = backendOrigin;
        values["Registry:BackendBindings:0:Audience"] = "assembleai:sample";
        values["Gateway:Enabled"] = "true";
        for (var index = 0; index < manifests.Length; index++)
        {
            var manifest = manifests[index];
            var prefix = $"Registry:FrontendArtifacts:{index}:";
            values[prefix + "AppId"] = manifest.AppId; values[prefix + "Version"] = manifest.FrontendArtifact.Version;
            values[prefix + "Entry"] = manifest.FrontendEntry; values[prefix + "Sha256"] = manifest.FrontendArtifact.Sha256;
        }
        for (var index = 0; index < 2; index++)
        {
            var prefix = $"Gateway:Operations:{index}:";
            values[prefix + "Service"] = "sample"; values[prefix + "Version"] = "0.1.0";
            values[prefix + "Method"] = index == 0 ? "POST" : "GET";
            values[prefix + "Path"] = index == 0 ? "/probe" : "/events";
            values[prefix + "Permission"] = "sample:use";
        }
        return values;
    }
    private static async Task Seed(NpgsqlConnection db, Fixture f, string issuer, ApplicationManifest[] manifests)
    {
        await Execute(db, "INSERT INTO public.platform_users(user_id,issuer,subject,display_name) VALUES($1,$3,$4,'Application owner'),($2,$3,$5,'Application reviewer')", f.Owner, f.Reviewer, issuer, OidcFixture.Subject, f.Reviewer.ToString());
        await Execute(db, "INSERT INTO public.platform_registry_operators(user_id,role) VALUES($1,'developer'),($2,'reviewer')", f.Owner, f.Reviewer);
        await Execute(db, "INSERT INTO public.platform_tenants(tenant_id,name) VALUES($1,'Application manufacturing')", f.Tenant);
        await Execute(db, "INSERT INTO public.tenant_memberships(tenant_id,user_id) VALUES($1,$2)", f.Tenant, f.Owner);
        await Execute(db, "INSERT INTO public.tenant_roles(tenant_id,role_id,name) VALUES($1,$2,'Application role')", f.Tenant, f.Role);
        await Execute(db, "INSERT INTO public.tenant_member_roles(tenant_id,user_id,role_id) VALUES($1,$2,$3)", f.Tenant, f.Owner, f.Role);
        foreach (var manifest in manifests)
            foreach (var permission in new[] { "sample:use", "registry:manage" })
                await Execute(db, "INSERT INTO public.role_permission_grants(tenant_id,grant_id,role_id,permission,resource_type,resource_id,tenant_wide) VALUES($1,$2,$3,$4,'application',$5,true)", f.Tenant, Guid.NewGuid(), f.Role, permission, manifest.AppId);
    }
    private static async Task<string> Expect(HttpResponseMessage response, int expected)
    {
        Assert((int)response.StatusCode == expected, $"Real application HTTP expected {expected}, got {(int)response.StatusCode}.");
        var body = await response.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert(!body.Contains(OidcFixture.AccessToken, StringComparison.Ordinal) && !body.Contains(OidcFixture.RefreshToken, StringComparison.Ordinal), "Application responses cannot expose OIDC credentials.");
        return body;
    }
    private static async Task WaitReady(Child child, string url)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!timeout.IsCancellationRequested)
        {
            Assert(!child.Process.HasExited, "Application fixture child process exited before becoming ready.");
            try { using var response = await client.GetAsync(url, timeout.Token); if (response.IsSuccessStatusCode) return; }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
            await Task.Delay(100, timeout.Token);
        }
        throw new InvalidOperationException("Application fixture readiness exceeded its budget.");
    }
    private static async Task RunProcess(string executable, string cwd, string[] arguments, int seconds)
    {
        await using var child = Child.Start(executable, cwd, arguments);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        await child.Process.WaitForExitAsync(deadline.Token);
        var output = await child.Output + await child.Errors;
        Console.Write(output.Length > 20000 ? output[..20000] : output);
        Assert(child.Process.ExitCode == 0, "Application fixture command failed.");
    }
    private static async Task Execute(NpgsqlConnection db, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, db);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }
    private static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AssembleAI.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Cannot locate the application fixture workspace.");
    }
    private static string FreeOrigin()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        return "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    private static string Id() => Guid.NewGuid().ToString("N");
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Ok<T>(RegistryResult<T> result) => Assert(result.Error == RegistryError.None, "Application registry operation failed.");
    private sealed class Fixture
    {
        public Guid Owner { get; } = Guid.NewGuid();
        public Guid Reviewer { get; } = Guid.NewGuid();
        public Guid Tenant { get; } = Guid.NewGuid();
        public Guid Role { get; } = Guid.NewGuid();
    }
    private sealed class Child : IAsyncDisposable
    {
        public required Process Process { get; init; }
        public required Task<string> Output { get; init; }
        public required Task<string> Errors { get; init; }
        public static Child Start(string executable, string cwd, string[] arguments, Dictionary<string, string?>? environment = null)
        {
            var start = new ProcessStartInfo(executable) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("ASSEMBLEAI_POSTGRES", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("ConnectionStrings", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
            if (environment is not null) foreach (var (key, value) in environment) start.Environment[key] = value;
            var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start application fixture process.");
            return new() { Process = process, Output = process.StandardOutput.ReadToEndAsync(), Errors = process.StandardError.ReadToEndAsync() };
        }
        public async ValueTask DisposeAsync()
        {
            if (!Process.HasExited) Process.Kill(entireProcessTree: true);
            await Process.WaitForExitAsync();
            Process.Dispose();
        }
    }
    private sealed class Browser(HttpClient client, string origin, string csrf) : IDisposable
    {
        public Guid Run { get; set; }
        public Guid Tenant { get; set; }
        public static async Task<Browser> Login(string origin)
        {
            var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, CookieContainer = new(), MaxResponseDrainSize = 0 })
            { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(10) };
            try
            {
                using var challenge = await client.GetAsync("/bff/login?returnUrl=/bff/session");
                Assert(challenge.StatusCode == HttpStatusCode.Redirect, "Real OIDC challenge required.");
                using var code = await client.GetAsync(challenge.Headers.Location);
                Assert(code.StatusCode == HttpStatusCode.Redirect, "Real OIDC code required.");
                using var callback = await client.GetAsync(code.Headers.Location);
                Assert(callback.StatusCode == HttpStatusCode.Redirect, "Validated OIDC callback required.");
                using var response = await client.GetAsync("/bff/csrf");
                using var body = JsonDocument.Parse(await Expect(response, 200));
                return new(client, origin, body.RootElement.GetProperty("requestToken").GetString()!);
            }
            catch { client.Dispose(); throw; }
        }
        public async Task<HttpResponseMessage> Send(string method, string path, object? body = null)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (body is not null) request.Content = JsonContent.Create(body);
            if (method == "POST") { request.Headers.Add("Origin", origin); request.Headers.Add(BffEndpoints.CsrfHeaderName, csrf); }
            if (path.StartsWith("/api/apps/", StringComparison.Ordinal))
            { request.Headers.Add("X-Run-ID", Run.ToString("D")); request.Headers.Add("X-Tenant-ID", Tenant.ToString("D")); }
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }
        public void Dispose() => client.Dispose();
    }
}
