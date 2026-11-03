using System.Collections.Concurrent;
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
using AssembleAI.Platform.InternalCalls;
using AssembleAI.Platform.Registry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AssembleAI.Platform.SmokeTests;

// Called only after the isolated database dispatcher validates its run marker.
// No authorization, registry or gateway store is replaced by a test implementation.
public static class PostgresGatewayFlowTests
{
    public static async Task RunAsync(string connectionString, string runDirectory)
    {
        await using var identity = await OidcFixture.StartAsync();
        var f = new Fixture();
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();
        try
        {
            await Seed(db, f, identity.Authority);
            var directory = Path.Combine(runDirectory, "gateway-chain-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); // The owning launcher cleans this marked run directory.
            using var rsa = RSA.Create(2048);
            using var certificate = new CertificateRequest("CN=postgres-gateway-chain", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var pfx = Path.Combine(directory, "signer.pfx");
            var cer = Path.Combine(directory, "signer.cer");
            var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pfx, password));
            File.WriteAllBytes(cer, certificate.Export(X509ContentType.Cert));
            var manifest = ManifestTests.Valid(f.App);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Platform"] = connectionString }).Build();
            var authorization = new PostgresAuthorizationStore(configuration);
            var verifierOptions = new InternalCallOptions
            { Enabled = true, Issuer = "https://platform.example.test/internal", ValidationCertificatePaths = [cer] };
            using var verifierKeys = new InternalCallKeys(verifierOptions);
            var verifier = new InternalCallTokenService(verifierOptions, verifierKeys, authorization, TimeProvider.System);
            var streams = new ConcurrentQueue<StreamControl>();
            var allStreams = new List<StreamControl>();
            var downstreamBuilder = WebApplication.CreateBuilder(["--environment", "Development", "--urls", "http://127.0.0.1:0"]);
            downstreamBuilder.Logging.ClearProviders();
            await using var downstream = downstreamBuilder.Build();
            downstream.MapGet("/health/ready", () => Results.Ok(new
            { status = "healthy", service = "production-api", version = "1.0.0", protocolVersion = 1 }));
            downstream.MapGet("/{operation}", async (HttpContext context) =>
            {
                var requestId = context.Request.Headers["X-Request-ID"].ToString();
                var target = new InternalCallTarget(f.App, "1.0.0", "assembleai:production-api", "production:read",
                    new(f.Tenant, "application", f.App, null), "GET", context.Request.Path.Value!);
                var auth = context.Request.Headers.Authorization;
                if (auth.Count != 1 || !AuthenticationHeaderValue.TryParse(auth[0], out var header)
                    || header.Scheme != "Bearer" || header.Parameter is null
                    || await verifier.ValidateAsync(header.Parameter, target, requestId, context.RequestAborted) is not { } verified)
                { context.Response.StatusCode = 403; return; }
                if (context.Request.Path == "/json")
                {
                    await context.Response.WriteAsJsonAsync(new
                    { signed = true, verified.UserId, verified.TenantId, verified.RequestId }, context.RequestAborted);
                    return;
                }
                if (context.Request.Path != "/stream" || !streams.TryDequeue(out var control))
                { context.Response.StatusCode = 404; return; }
                context.Response.ContentType = "text/event-stream";
                try
                {
                    await context.Response.WriteAsync("event: delta\ndata: 首段\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    while (!control.Complete.Task.IsCompleted)
                    {
                        await Task.WhenAny(control.Complete.Task, Task.Delay(100, context.RequestAborted));
                        context.RequestAborted.ThrowIfCancellationRequested();
                        await context.Response.WriteAsync(": heartbeat\n\n", context.RequestAborted);
                        await context.Response.Body.FlushAsync(context.RequestAborted);
                    }
                    await context.Response.WriteAsync("event: complete\ndata: done\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
                catch (Exception error) when (error is OperationCanceledException or IOException)
                { control.Cancelled.TrySetResult(); }
                finally
                {
                    if (context.RequestAborted.IsCancellationRequested) control.Cancelled.TrySetResult();
                    control.Stopped.TrySetResult();
                }
            });
            await downstream.StartAsync();
            try
            {
                var values = ManifestTests.CatalogSettings(manifest);
                values["ConnectionStrings:Platform"] = connectionString;
                values["Registry:BackendBindings:0:Destination"] = downstream.Urls.Single();
                values["Gateway:Enabled"] = "true";
                values["Gateway:CallSeconds"] = "30";
                values["Gateway:IdleSeconds"] = "10";
                values["Gateway:RecheckSeconds"] = "2";
                values["Gateway:CheckTimeoutSeconds"] = "3";
                for (var index = 0; index < 2; index++)
                {
                    values[$"Gateway:Operations:{index}:Service"] = "production-api";
                    values[$"Gateway:Operations:{index}:Version"] = "1.0.0";
                    values[$"Gateway:Operations:{index}:Method"] = "GET";
                    values[$"Gateway:Operations:{index}:Path"] = index == 0 ? "/json" : "/stream";
                    values[$"Gateway:Operations:{index}:Permission"] = "production:read";
                }
                var catalogConfiguration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
                var registry = new PostgresRegistryStore(catalogConfiguration, new(catalogConfiguration), authorization);
                var owner = new SubjectIdentity(identity.Authority, OidcFixture.Subject);
                var reviewer = new SubjectIdentity(identity.Authority, f.Reviewer.ToString());
                Ok(await registry.SaveDraftAsync(owner, manifest, 0, Id()));
                Ok(await registry.TransitionAsync(owner, f.App, "1.0.0", "submit", 1, Id()));
                Ok(await registry.TransitionAsync(reviewer, f.App, "1.0.0", "publish", 2, Id()));
                Ok(await registry.SetTenantApplicationAsync(owner, f.Tenant, f.App, "1.0.0", true, 0, Id()));
                var origin = FreeOrigin();
                await using var platform = PlatformApplication.Build([
                    "--environment", "Development", "--urls", origin,
                    "--Authentication:Bff:Enabled", "true", "--Authentication:Bff:Authority", identity.Authority,
                    "--Authentication:Bff:ClientId", OidcFixture.ClientId, "--Authentication:Bff:ClientSecret", OidcFixture.ClientSecret,
                    "--Authentication:Bff:PublicOrigin", origin, "--InternalCalls:Enabled", "true",
                    "--InternalCalls:Issuer", verifierOptions.Issuer, "--InternalCalls:SigningCertificatePath", pfx,
                    "--InternalCalls:SigningCertificatePassword", password
                ], builder => { builder.Logging.ClearProviders(); builder.Configuration.AddInMemoryCollection(values); });
                await platform.StartAsync();
                try
                {
                    using var browser = await Browser.Login(origin);
                    using (var selected = await browser.Send("POST", "/bff/tenant", new { tenantId = f.Tenant }))
                        await Expect(selected, 200);
                    var create = $"/api/platform/v1/tenants/{f.Tenant}/applications/{f.App}/releases/1.0.0/runs";
                    using (var response = await browser.Send("POST", create))
                    {
                        using var json = JsonDocument.Parse(await Expect(response, 200));
                        browser.Run = json.RootElement.GetProperty("runId").GetGuid();
                        browser.Tenant = f.Tenant;
                    }
                    var api = $"/api/apps/{f.App}/releases/1.0.0";
                    using (var response = await browser.Send("GET", api + "/json"))
                    {
                        using var json = JsonDocument.Parse(await Expect(response, 200));
                        var result = json.RootElement;
                        Assert(result.GetProperty("signed").GetBoolean() && result.GetProperty("userId").GetGuid() == f.Owner
                            && result.GetProperty("tenantId").GetGuid() == f.Tenant, "The real gateway must deliver a signed identity verified against PostgreSQL.");
                        var requestId = response.Headers.GetValues("X-Request-ID").Single();
                        Assert(result.GetProperty("requestId").GetString() == requestId, "HTTP request correlation must match the signed downstream identity.");
                        await Finished(db, requestId, f, "transport_completed");
                    }

                    var normal = QueueStream();
                    using (var response = await browser.Send("GET", api + "/stream"))
                    {
                        await using var source = await response.Content.ReadAsStreamAsync();
                        using var reader = new StreamReader(source, Encoding.UTF8);
                        await First(response, reader);
                        var requestId = response.Headers.GetValues("X-Request-ID").Single();
                        Assert(await Scalar(db, "SELECT outcome FROM public.platform_gateway_calls WHERE request_id=$1", requestId) is "running",
                            "SSE must be durably admitted while its first chunk is visible and completion is still blocked.");
                        normal.Complete.TrySetResult();
                        Assert((await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(8))).Contains("event: complete", StringComparison.Ordinal),
                            "An ordinary proxied SSE must retain its explicit completion event.");
                        await normal.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        await Finished(db, requestId, f, "transport_completed");
                    }

                    await CheckCancellation(async () => await Execute(db,
                        "UPDATE public.role_permission_grants SET enabled=false WHERE tenant_id=$1 AND grant_id=$2", f.Tenant, f.UseGrant));
                    using (var refused = await browser.Send("GET", api + "/json")) await Expect(refused, 403);
                    await Execute(db, "UPDATE public.role_permission_grants SET enabled=true WHERE tenant_id=$1 AND grant_id=$2", f.Tenant, f.UseGrant);
                    await CheckCancellation(async () => Ok(await registry.TransitionAsync(reviewer, f.App, "1.0.0", "disable", 3, Id())));
                    using (var refused = await browser.Send("GET", api + "/json")) await Expect(refused, 403);
                    using (var refused = await browser.Send("POST", create)) await Expect(refused, 403);

                    StreamControl QueueStream()
                    {
                        var control = new StreamControl(); allStreams.Add(control); streams.Enqueue(control); return control;
                    }
                    async Task CheckCancellation(Func<Task> revoke)
                    {
                        var control = QueueStream();
                        using var response = await browser.Send("GET", api + "/stream");
                        await using var source = await response.Content.ReadAsStreamAsync();
                        using var reader = new StreamReader(source, Encoding.UTF8);
                        await First(response, reader);
                        var requestId = response.Headers.GetValues("X-Request-ID").Single();
                        await revoke(); // Returns only after the actual database transaction commits.
                        await control.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        await control.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        Assert(!control.Complete.Task.IsCompleted, "Revocation must cancel the receiver before its normal completion is released.");
                        await Finished(db, requestId, f, "revoked");
                    }
                }
                finally
                {
                    foreach (var control in allStreams) control.Complete.TrySetResult();
                    await platform.StopAsync();
                }
            }
            finally { await downstream.StopAsync(); }
        }
        finally
        {
            await Execute(db, "DELETE FROM public.platform_gateway_calls WHERE app_id=$1", f.App);
            await Execute(db, "DELETE FROM public.platform_applications WHERE app_id=$1", f.App);
            await Execute(db, "DELETE FROM public.platform_registry_audit WHERE app_id=$1", f.App);
            await Execute(db, "DELETE FROM public.platform_tenants WHERE tenant_id=$1", f.Tenant);
            await Execute(db, "DELETE FROM public.platform_users WHERE user_id IN ($1,$2)", f.Owner, f.Reviewer);
        }
        Console.WriteLine("PASS: PostgreSQL production gateway chain: OIDC login, registered release/run, signed HTTP JSON/SSE, durable call lifecycle and in-flight SQL-grant/global-release revocation.");
    }

    private static async Task First(HttpResponseMessage response, StreamReader reader)
    {
        Assert(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == "text/event-stream", "Expected actual SSE forwarding.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Assert(await reader.ReadLineAsync(deadline.Token) == "event: delta" && await reader.ReadLineAsync(deadline.Token) == "data: 首段"
            && await reader.ReadLineAsync(deadline.Token) == "", "The first UTF-8 event must arrive before backend completion.");
    }
    private static async Task Finished(NpgsqlConnection db, string id, Fixture f, string outcome)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            await using var command = new NpgsqlCommand("SELECT outcome,finished_at IS NOT NULL,user_id,tenant_id,app_id,elapsed_milliseconds FROM public.platform_gateway_calls WHERE request_id=$1", db);
            command.Parameters.AddWithValue(id);
            await using (var reader = await command.ExecuteReaderAsync(deadline.Token))
            {
                Assert(await reader.ReadAsync(deadline.Token), "The real gateway must persist call admission before a response.");
                if (reader.GetBoolean(1))
                {
                    Assert(reader.GetString(0) == outcome && reader.GetGuid(2) == f.Owner && reader.GetGuid(3) == f.Tenant
                        && reader.GetString(4) == f.App && reader.GetInt64(5) >= 0, "The durable call terminal state or attribution is incorrect.");
                    return;
                }
            }
            await Task.Delay(20, deadline.Token);
        }
    }
    private static async Task Seed(NpgsqlConnection db, Fixture f, string issuer)
    {
        await Execute(db, "INSERT INTO public.platform_users(user_id,issuer,subject,display_name) VALUES($1,$3,$4,'Gateway HTTP owner'),($2,$3,$5,'Gateway HTTP reviewer')",
            f.Owner, f.Reviewer, issuer, OidcFixture.Subject, f.Reviewer.ToString());
        await Execute(db, "INSERT INTO public.platform_registry_operators(user_id,role) VALUES($1,'developer'),($2,'reviewer')", f.Owner, f.Reviewer);
        await Execute(db, "INSERT INTO public.platform_tenants(tenant_id,name) VALUES($1,'Gateway HTTP tenant')", f.Tenant);
        await Execute(db, "INSERT INTO public.tenant_memberships(tenant_id,user_id) VALUES($1,$2)", f.Tenant, f.Owner);
        await Execute(db, "INSERT INTO public.tenant_roles(tenant_id,role_id,name) VALUES($1,$2,'Gateway HTTP role')", f.Tenant, f.Role);
        await Execute(db, "INSERT INTO public.tenant_member_roles(tenant_id,user_id,role_id) VALUES($1,$2,$3)", f.Tenant, f.Owner, f.Role);
        foreach (var (grant, permission) in new[] { (f.UseGrant, "production:read"), (f.ManageGrant, "registry:manage") })
            await Execute(db, "INSERT INTO public.role_permission_grants(tenant_id,grant_id,role_id,permission,resource_type,resource_id,tenant_wide) VALUES($1,$2,$3,$4,'application',$5,true)",
                f.Tenant, grant, f.Role, permission, f.App);
    }
    private static async Task<string> Expect(HttpResponseMessage response, int status)
    {
        Assert((int)response.StatusCode == status && response.Headers.CacheControl?.NoStore == true, $"PostgreSQL gateway HTTP expected {status}, got {(int)response.StatusCode}.");
        var body = await response.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert(!body.Contains(OidcFixture.AccessToken, StringComparison.Ordinal) && !body.Contains(OidcFixture.RefreshToken, StringComparison.Ordinal), "Gateway results must not expose BFF credentials.");
        return body;
    }
    private static string Id() => Guid.NewGuid().ToString("N");
    private static void Ok<T>(RegistryResult<T> result) => Assert(result.Error == RegistryError.None, "Gateway chain registry operation failed.");
    private static async Task Execute(NpgsqlConnection db, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, db);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<object?> Scalar(NpgsqlConnection db, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, db);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return await command.ExecuteScalarAsync();
    }
    private static string FreeOrigin()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        return "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class StreamControl
    {
        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
                Assert(challenge.StatusCode == HttpStatusCode.Redirect, "OIDC challenge required.");
                using var code = await client.GetAsync(challenge.Headers.Location);
                Assert(code.StatusCode == HttpStatusCode.Redirect, "OIDC code required.");
                using var callback = await client.GetAsync(code.Headers.Location);
                Assert(callback.StatusCode == HttpStatusCode.Redirect, "Validated OIDC callback required.");
                using var response = await client.GetAsync("/bff/csrf");
                using var json = JsonDocument.Parse(await Expect(response, 200));
                return new(client, origin, json.RootElement.GetProperty("requestToken").GetString()!);
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
    private sealed class Fixture
    {
        public string App { get; } = "gateway-http-" + Guid.NewGuid().ToString("N");
        public Guid Owner { get; } = Guid.NewGuid();
        public Guid Reviewer { get; } = Guid.NewGuid();
        public Guid Tenant { get; } = Guid.NewGuid();
        public Guid Role { get; } = Guid.NewGuid();
        public Guid UseGrant { get; } = Guid.NewGuid();
        public Guid ManageGrant { get; } = Guid.NewGuid();
    }
}
