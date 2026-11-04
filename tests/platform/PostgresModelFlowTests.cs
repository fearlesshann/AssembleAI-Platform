using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Identity;
using AssembleAI.Platform.Models;
using AssembleAI.Platform.Registry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AssembleAI.Platform.SmokeTests;

/// <summary>Launcher-owned PG, real OIDC/BFF/YARP and uvicorn. No application/storage substitutes.</summary>
public static class PostgresModelFlowTests
{
    private const string Alias = "assistant";
    private const string Prompt = "Reply with the single word OK.";
    public static async Task RunAsync(string connection, string directory, bool realModel)
    {
        await Exercise(connection, directory, null);
        if (realModel)
        {
            var path = Environment.GetEnvironmentVariable("ASSEMBLEAI_MODEL_ACCEPTANCE_CONFIG");
            Check(!string.IsNullOrWhiteSpace(path), "Explicit real-model acceptance configuration is required.");
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path!));
            var value = json.RootElement;
            Check(value.ValueKind == JsonValueKind.Object && value.EnumerateObject().All(item => item.Name is "endpoint" or "upstreamModel" or "credentialEnvironmentVariable" or "reasoningEffort"),
                "Only endpoint/model/credential-reference acceptance configuration is supported.");
            var actual = new Provider(value.GetProperty("endpoint").GetString()!, value.GetProperty("upstreamModel").GetString()!,
                value.GetProperty("credentialEnvironmentVariable").GetString()!,
                value.TryGetProperty("reasoningEffort", out var effort) ? effort.GetString() : null);
            Check(Uri.TryCreate(actual.Endpoint, UriKind.Absolute, out var endpoint) && endpoint.Scheme == "https"
                && string.IsNullOrEmpty(endpoint.UserInfo) && string.IsNullOrEmpty(endpoint.Query)
                && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(actual.CredentialVariable)),
                "A configured HTTPS model provider and process-local credential are required.");
            await Exercise(connection, directory, actual);
        }
    }

    private static async Task Exercise(string connection, string directory, Provider? actual)
    {
        var f = new Fixture();
        var workspace = Workspace();
        var python = Environment.GetEnvironmentVariable("ASSEMBLEAI_PYTHON") ?? throw new InvalidOperationException("Python executable is required.");
        await using var identity = await OidcFixture.StartAsync();
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        var platformOrigin = FreeOrigin(); var backendOrigin = FreeOrigin();
        var controls = new ConcurrentQueue<StreamControl>();
        var upstreamCalls = 0;
        var captureKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string? capturedCapability = null, capturedAuthorization = null;
        var controlBuilder = WebApplication.CreateBuilder(["--environment", "Development", "--urls", "http://127.0.0.1:0"]);
        controlBuilder.Logging.ClearProviders();
        await using var control = controlBuilder.Build();
        control.MapPost("/capture", async (HttpContext context) => {
            if (context.Request.Headers["X-Fixture-Control"] != captureKey) { context.Response.StatusCode = 403; return; }
            using var json = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            capturedCapability = json.RootElement.GetProperty("capability").GetString();
            capturedAuthorization = json.RootElement.GetProperty("authorization").GetString();
            context.Response.StatusCode = 204;
        });
        control.MapPost("/v1/chat/completions", async (HttpContext context) => {
            Interlocked.Increment(ref upstreamCalls);
            using var json = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var input = json.RootElement;
            Check(!context.Request.Headers.ContainsKey("Cookie") && !context.Request.Headers.ContainsKey("X-Model-Delegation")
                && !context.Request.Headers.ContainsKey("Origin") && input.GetProperty("model").GetString() == "fixture-model"
                && input.GetProperty("max_tokens").GetInt32() == 128, "Provider must receive only trusted model settings and no caller credentials.");
            if (!input.GetProperty("stream").GetBoolean())
            {
                await context.Response.WriteAsJsonAsync(new {
                    choices = new[] { new { index = 0, message = new { role = "assistant", content = "OK" }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 5, completion_tokens = 1, total_tokens = 6 }
                });
                return;
            }
            Check(controls.TryDequeue(out var streaming), "An explicit streaming fixture control is required.");
            context.Response.ContentType = "text/event-stream";
            try
            {
                await context.Response.WriteAsync("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"OK\"},\"finish_reason\":null}]}\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                streaming!.Started.TrySetResult();
                await streaming.Release.Task.WaitAsync(context.RequestAborted);
                await context.Response.WriteAsync("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
                    + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":1,\"total_tokens\":6}}\n\n"
                    + "data: [DONE]\n\n", context.RequestAborted);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException) { streaming!.Cancelled.TrySetResult(); }
        });
        await control.StartAsync();
        try
        {
            await Seed(db, f, identity.Authority);
            using var rsa = RSA.Create(2048);
            using var certificate = new CertificateRequest("CN=model-fixture", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var pfx = Path.Combine(directory, "model-" + f.Tenant.ToString("N") + ".pfx");
            var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await File.WriteAllBytesAsync(pfx, certificate.Export(X509ContentType.Pfx, password));
            var manifest = ManifestTests.Valid() with { AppId = f.App, FrontendEntry = $"/apps/{f.App}/1.0.0/",
                BackendService = "sample", BackendVersion = "0.1.0", Permissions = ["sample:use"] };
            var values = Settings(connection, backendOrigin, control.Urls.Single(), manifest, actual);
            var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var authorization = new PostgresAuthorizationStore(config);
            var registry = new PostgresRegistryStore(config, new(config), authorization);
            var owner = new SubjectIdentity(identity.Authority, OidcFixture.Subject);
            var reviewer = new SubjectIdentity(identity.Authority, f.Reviewer.ToString());
            Ok(await registry.SaveDraftAsync(owner, manifest, 0, Id()));
            Ok(await registry.TransitionAsync(owner, f.App, "1.0.0", "submit", 1, Id()));
            Ok(await registry.TransitionAsync(reviewer, f.App, "1.0.0", "publish", 2, Id()));
            Ok(await registry.SetTenantApplicationAsync(owner, f.Tenant, f.App, "1.0.0", true, 0, Id()));
            await using var platform = PlatformApplication.Build([
                "--environment", "Development", "--urls", platformOrigin,
                "--Authentication:Bff:Enabled", "true", "--Authentication:Bff:Authority", identity.Authority,
                "--Authentication:Bff:ClientId", OidcFixture.ClientId, "--Authentication:Bff:ClientSecret", OidcFixture.ClientSecret,
                "--Authentication:Bff:PublicOrigin", platformOrigin,
                "--InternalCalls:Enabled", "true", "--InternalCalls:ValidationEndpointEnabled", "true",
                "--InternalCalls:Issuer", "https://platform.example.test/internal",
                "--InternalCalls:SigningCertificatePath", pfx, "--InternalCalls:SigningCertificatePassword", password
            ], builder => { builder.Logging.ClearProviders(); builder.Configuration.AddInMemoryCollection(values); });
            await platform.StartAsync();
            try
            {
                await using var backend = StartBackend(python, workspace, platformOrigin, backendOrigin, control.Urls.Single(), captureKey, actual?.CredentialVariable);
                await Ready(backend, backendOrigin + "/health/ready");
                using var browser = await Browser.Login(platformOrigin);
                browser.Tenant = f.Tenant;
                using (var selected = await browser.Send("POST", "/bff/tenant", new { tenantId = f.Tenant })) await Expect(selected, 200);
                var configurationPath = $"/api/platform/v1/tenants/{f.Tenant}/models/{Alias}/configuration";
                using (var configured = await browser.Send("PUT", configurationPath, new { enabled = true, maxOutputTokens = 128, expectedRevision = 0 })) await Expect(configured, 200);
                await browser.StartRun(f.App);
                var path = $"/api/apps/{f.App}/releases/1.0.0/model-chat";
                var expected = new List<(string ModelRequest, string GatewayRequest, Guid Run)>();
                using (var response = await browser.Send("POST", path, new { message = Prompt, stream = false }))
                {
                    using var body = JsonDocument.Parse(await Expect(response, 200));
                    Check(body.RootElement.GetProperty("content").GetString() is { Length: > 0 }, "Delegated JSON must contain a real provider result.");
                    Check(!response.Headers.Contains("X-Model-Delegation"), "A browser response cannot expose delegation credentials.");
                    expected.Add((body.RootElement.GetProperty("requestId").GetString()!, response.Headers.GetValues("X-Request-ID").Single(), browser.Run));
                }
                Check(!string.IsNullOrEmpty(capturedCapability) && capturedCapability != Browser.SpoofedCapability,
                    "YARP must replace the spoofed caller capability with its own active-call credential.");
                if (actual is null)
                {
                    using var direct = new HttpClient { BaseAddress = new Uri(platformOrigin), Timeout = TimeSpan.FromSeconds(10) };
                    var before = Volatile.Read(ref upstreamCalls);
                    foreach (var credential in new[] { Browser.SpoofedCapability, capturedCapability!, capturedAuthorization! })
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/models/{Alias}/chat") {
                            Content = JsonContent.Create(new { messages = new[] { new { role = "user", content = Prompt } }, maxOutputTokens = 128, stream = false }) };
                        request.Headers.TryAddWithoutValidation("Authorization", credential.StartsWith("Bearer ", StringComparison.Ordinal) ? credential : "Bearer " + credential);
                        using var rejected = await direct.SendAsync(request);
                        Check(rejected.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
                            "Fabricated, completed-call and ordinary business JWT credentials must not authorize a model call.");
                    }
                    Check(Volatile.Read(ref upstreamCalls) == before, "Rejected capabilities must cause no provider request.");
                }
                capturedCapability = null; capturedAuthorization = null;
                var complete = new StreamControl(); if (actual is null) controls.Enqueue(complete);
                using (var response = await browser.Send("POST", path, new { message = Prompt, stream = true }))
                {
                    using var reader = await ReadStream(response);
                    var first = await Event(reader);
                    Check(first.Type == "delta" && !string.IsNullOrEmpty(first.Content), "The first model chunk must reach the application caller incrementally.");
                    if (actual is null)
                    {
                        Check(!complete.Release.Task.IsCompleted, "The first application chunk cannot wait for the provider's completion.");
                        // The application JWT admits the operation once. Active-call
                        // delegation must continue after that original 30-second JWT expires.
                        await Task.Delay(TimeSpan.FromSeconds(31));
                        Check(!complete.Cancelled.Task.IsCompleted, "A valid active model call must not inherit the expired business-JWT duration.");
                    }
                    complete.Release.TrySetResult();
                    var completed = false;
                    for (var i = 0; i < 512; i++)
                    {
                        var item = await Event(reader);
                        if (item.Type == "complete") { completed = true; break; }
                    }
                    Check(completed, "Delegated SSE must end in an explicit successfully audited complete event.");
                    expected.Add((first.RequestId, response.Headers.GetValues("X-Request-ID").Single(), browser.Run));
                }
                if (actual is null)
                {
                    foreach (var stop in new[] { "disconnect", "revoke", "disable" })
                    {
                        await browser.StartRun(f.App);
                        var streaming = new StreamControl(); controls.Enqueue(streaming);
                        using var response = await browser.Send("POST", path, new { message = Prompt, stream = true });
                        using var reader = await ReadStream(response);
                        var first = await Event(reader);
                        Check(first.Type == "delta", "Cancellation scenario requires an active real model stream.");
                        expected.Add((first.RequestId, response.Headers.GetValues("X-Request-ID").Single(), browser.Run));
                        if (stop == "disconnect") { reader.Dispose(); response.Dispose(); }
                        else
                        {
                            if (stop == "revoke") await Execute(db, "UPDATE public.role_permission_grants SET enabled=false WHERE tenant_id=$1 AND permission='model:invoke'", f.Tenant);
                            else using (var disabled = await browser.Send("PUT", configurationPath, new { enabled = false, maxOutputTokens = 128, expectedRevision = 1 })) await Expect(disabled, 200);
                            await Interrupted(reader);
                        }
                        await streaming.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        if (stop == "revoke") await Execute(db, "UPDATE public.role_permission_grants SET enabled=true WHERE tenant_id=$1 AND permission='model:invoke'", f.Tenant);
                    }
                }
                await VerifyAudit(db, f, expected);
                Check(controls.IsEmpty, "Every queued local provider scenario must have been consumed.");
                Console.WriteLine(actual is null
                    ? "PASS: model application chain: real OIDC/BFF/PostgreSQL/YARP/FastAPI, JSON/incremental SSE, disconnect/revocation/disable, expired/forged credentials and durable source attribution."
                    : "PASS: real model application acceptance: one JSON and one SSE generation through OIDC/BFF/PostgreSQL/YARP/FastAPI, explicit completion and durable source attribution; no model content logged.");
            }
            finally { await platform.StopAsync(); }
        }
        finally
        {
            capturedCapability = null; capturedAuthorization = null;
            await control.StopAsync();
            await Execute(db, "DELETE FROM public.platform_model_calls WHERE tenant_id=$1", f.Tenant);
            await Execute(db, "DELETE FROM public.platform_model_configuration_audit WHERE tenant_id=$1", f.Tenant);
            await Execute(db, "DELETE FROM public.platform_gateway_calls WHERE app_id=$1", f.App);
            await Execute(db, "DELETE FROM public.platform_applications WHERE app_id=$1", f.App);
            await Execute(db, "DELETE FROM public.platform_registry_audit WHERE app_id=$1", f.App);
            await Execute(db, "DELETE FROM public.platform_tenants WHERE tenant_id=$1", f.Tenant);
            await Execute(db, "DELETE FROM public.platform_users WHERE user_id IN ($1,$2)", f.Owner, f.Reviewer);
        }
    }

    private static Dictionary<string, string?> Settings(string connection, string backend, string fixture, ApplicationManifest manifest, Provider? actual)
    {
        var values = ManifestTests.CatalogSettings(manifest);
        values["ConnectionStrings:Platform"] = connection;
        values["Registry:BackendBindings:0:Destination"] = backend;
        values["Registry:BackendBindings:0:Audience"] = "assembleai:sample";
        values["Gateway:Enabled"] = "true"; values["Gateway:CallSeconds"] = "180";
        values["Gateway:IdleSeconds"] = "60"; values["Gateway:ResponseHeadersSeconds"] = "60";
        values["Gateway:Operations:0:Service"] = "sample"; values["Gateway:Operations:0:Version"] = "0.1.0";
        values["Gateway:Operations:0:Method"] = "POST"; values["Gateway:Operations:0:Path"] = "/model-chat";
        values["Gateway:Operations:0:Permission"] = "sample:use"; values["Gateway:Operations:0:ModelIds:0"] = Alias;
        values["Models:Enabled"] = "true"; values["Models:CallSeconds"] = "120";
        values["Models:Providers:acceptance:Endpoint"] = actual?.Endpoint ?? fixture + "/v1/chat/completions";
        values["Models:Providers:acceptance:AllowInsecureHttp"] = (actual is null).ToString();
        if (actual is not null) values["Models:Providers:acceptance:CredentialEnvironmentVariable"] = actual.CredentialVariable;
        values["Models:Catalog:assistant:Provider"] = "acceptance";
        values["Models:Catalog:assistant:UpstreamModel"] = actual?.Model ?? "fixture-model";
        values["Models:Catalog:assistant:DisplayName"] = "Model acceptance";
        values["Models:Catalog:assistant:Enabled"] = "true"; values["Models:Catalog:assistant:MaximumOutputTokens"] = "128";
        if (actual?.ReasoningEffort is { } effort) values["Models:Catalog:assistant:ReasoningEffort"] = effort;
        return values;
    }
    private static async Task Seed(NpgsqlConnection db, Fixture f, string issuer)
    {
        await Execute(db, "INSERT INTO public.platform_users(user_id,issuer,subject,display_name) VALUES($1,$3,$4,'Model owner'),($2,$3,$5,'Model reviewer')", f.Owner, f.Reviewer, issuer, OidcFixture.Subject, f.Reviewer.ToString());
        await Execute(db, "INSERT INTO public.platform_registry_operators(user_id,role) VALUES($1,'developer'),($2,'reviewer')", f.Owner, f.Reviewer);
        await Execute(db, "INSERT INTO public.platform_tenants(tenant_id,name) VALUES($1,'Model manufacturing')", f.Tenant);
        await Execute(db, "INSERT INTO public.tenant_memberships(tenant_id,user_id) VALUES($1,$2)", f.Tenant, f.Owner);
        await Execute(db, "INSERT INTO public.tenant_roles(tenant_id,role_id,name) VALUES($1,$2,'Model operator')", f.Tenant, f.Role);
        await Execute(db, "INSERT INTO public.tenant_member_roles(tenant_id,user_id,role_id) VALUES($1,$2,$3)", f.Tenant, f.Owner, f.Role);
        foreach (var (permission, type, id) in new[] { ("registry:manage", "application", f.App), ("sample:use", "application", f.App), ("model:invoke", "model", Alias), ("model:manage", "model", Alias) })
            await Execute(db, "INSERT INTO public.role_permission_grants(tenant_id,grant_id,role_id,permission,resource_type,resource_id,tenant_wide) VALUES($1,$2,$3,$4,$5,$6,true)", f.Tenant, Guid.NewGuid(), f.Role, permission, type, id);
    }
    private static async Task VerifyAudit(NpgsqlConnection db, Fixture f, List<(string ModelRequest, string GatewayRequest, Guid Run)> expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (true)
        {
            await using var count = new NpgsqlCommand("SELECT count(*) FROM public.platform_model_calls WHERE tenant_id=$1 AND outcome='running'", db);
            count.Parameters.AddWithValue(f.Tenant);
            if (await count.ExecuteScalarAsync(deadline.Token) is 0L) break;
            await Task.Delay(100, deadline.Token);
        }
        foreach (var row in expected)
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM public.platform_model_calls WHERE tenant_id=$1 AND user_id=$2 AND model_id=$3 AND request_id=$4 AND app_id=$5 AND release_version='1.0.0' AND run_id=$6 AND gateway_request_id=$7 AND outcome<>'running' AND finished_at IS NOT NULL", db);
            foreach (var value in new object[] { f.Tenant, f.Owner, Alias, row.ModelRequest, f.App, row.Run, row.GatewayRequest }) command.Parameters.AddWithValue(value);
            Check(await command.ExecuteScalarAsync() is 1L, "Every delegated call requires exact durable user/tenant/model/application/release/run/gateway-request attribution.");
        }
        await using var completed = new NpgsqlCommand("SELECT count(*) FROM public.platform_model_calls WHERE tenant_id=$1 AND outcome='completed'", db);
        completed.Parameters.AddWithValue(f.Tenant);
        Check(await completed.ExecuteScalarAsync() is 2L, "Exactly JSON and completed SSE calls may be recorded as completed.");
    }
    private static async Task<string> Expect(HttpResponseMessage response, int status)
    {
        Check((int)response.StatusCode == status, $"Model application HTTP expected {status}, received {(int)response.StatusCode}.");
        return await response.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(130));
    }
    private static async Task<StreamReader> ReadStream(HttpResponseMessage response)
    {
        Check(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == "text/event-stream", "Application model stream must return SSE.");
        return new(await response.Content.ReadAsStreamAsync());
    }
    private static async Task<StreamItem> Event(StreamReader reader)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(130));
        string? type = null, data = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(deadline.Token);
            Check(line is not null, "Model stream ended before explicit completion.");
            if (line!.StartsWith("event:", StringComparison.Ordinal)) type = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal)) data = line[5..].Trim();
            else if (line.Length == 0 && type is not null && data is not null)
            {
                using var json = JsonDocument.Parse(data);
                // This is the application's SDK stream, not the internal model
                // protocol: Python maps model content to delta.text and usage to complete.
                var content = json.RootElement.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                return new(type, content, json.RootElement.GetProperty("requestId").GetString()!);
            }
        }
    }
    private static async Task Interrupted(StreamReader reader)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            while (await reader.ReadLineAsync(deadline.Token) is { } line)
                Check(line != "event: complete", "Revoked application model stream cannot claim completion.");
        }
        catch (IOException) { }
        catch (HttpRequestException) { }
    }
    private static Child StartBackend(string python, string workspace, string platform, string backend, string control, string key, string? secretVariable)
    {
        var start = new ProcessStartInfo(python) { WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var item in new[] { "-m", "uvicorn", "model_application_fixture:app", "--app-dir", Path.Combine(workspace, "tests/platform"), "--host", "127.0.0.1", "--port", new Uri(backend).Port.ToString(), "--no-access-log", "--log-level", "error" }) start.ArgumentList.Add(item);
        start.Environment["PYTHONPATH"] = Path.Combine(workspace, "apps/sample/backend");
        start.Environment["ASSEMBLEAI_ENVIRONMENT"] = "Development";
        start.Environment["ASSEMBLEAI_SERVICE"] = "sample"; start.Environment["ASSEMBLEAI_SERVICE_VERSION"] = "0.1.0";
        start.Environment["ASSEMBLEAI_VALIDATION_URL"] = platform + "/internal/calls/validate";
        start.Environment["ASSEMBLEAI_MODEL_GATEWAY_ORIGIN"] = platform; start.Environment["ASSEMBLEAI_MODEL_ID"] = Alias;
        start.Environment["ASSEMBLEAI_MODEL_MAX_OUTPUT_TOKENS"] = "128";
        start.Environment["ASSEMBLEAI_MODEL_TEST_CONTROL_ORIGIN"] = control; start.Environment["ASSEMBLEAI_MODEL_TEST_CONTROL_KEY"] = key;
        foreach (var variable in start.Environment.Keys.Where(value => value.StartsWith("ASSEMBLEAI_POSTGRES", StringComparison.OrdinalIgnoreCase) || value.StartsWith("ConnectionStrings", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(variable);
        // --real-model runs the local fixture first in the same process. Strip
        // the configured real credential from that child too, even though this
        // particular stage has no real Provider object.
        if (Environment.GetEnvironmentVariable("ASSEMBLEAI_MODEL_ACCEPTANCE_CONFIG") is { } acceptancePath)
        {
            using var acceptance = JsonDocument.Parse(File.ReadAllText(acceptancePath));
            var credentialName = acceptance.RootElement.GetProperty("credentialEnvironmentVariable").GetString();
            if (!string.IsNullOrEmpty(credentialName))
            {
                start.Environment.Remove(credentialName);
                start.Environment["ASSEMBLEAI_MODEL_TEST_FORBIDDEN_CREDENTIAL_VARIABLE"] = credentialName;
            }
        }
        start.Environment.Remove("ASSEMBLEAI_MODEL_ACCEPTANCE_CONFIG");
        if (secretVariable is not null)
        {
            start.Environment.Remove(secretVariable);
            start.Environment["ASSEMBLEAI_MODEL_TEST_FORBIDDEN_CREDENTIAL_VARIABLE"] = secretVariable;
        }
        var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot launch the Python application fixture.");
        return new(process, process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
    }
    private static async Task Ready(Child child, string path)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!timeout.IsCancellationRequested)
        {
            Check(!child.Process.HasExited, "Python application ended before readiness; inspect fixture configuration.");
            try { using var response = await client.GetAsync(path, timeout.Token); if (response.IsSuccessStatusCode) return; }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
            await Task.Delay(100, timeout.Token);
        }
        throw new InvalidOperationException("Python application readiness timed out.");
    }
    private sealed class Browser(HttpClient client, string origin, string csrf) : IDisposable
    {
        public const string SpoofedCapability = "browser-forged-model-capability";
        public Guid Tenant { get; set; } public Guid Run { get; private set; }
        public static async Task<Browser> Login(string origin)
        {
            var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, CookieContainer = new(), MaxResponseDrainSize = 0 }) { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(150) };
            try
            {
                using var challenge = await client.GetAsync("/bff/login?returnUrl=/bff/session");
                Check(challenge.StatusCode == HttpStatusCode.Redirect, "OIDC challenge is required.");
                using var code = await client.GetAsync(challenge.Headers.Location);
                using var callback = await client.GetAsync(code.Headers.Location);
                Check(callback.StatusCode == HttpStatusCode.Redirect, "OIDC code exchange must complete.");
                using var response = await client.GetAsync("/bff/csrf");
                using var body = JsonDocument.Parse(await Expect(response, 200));
                return new(client, origin, body.RootElement.GetProperty("requestToken").GetString()!);
            }
            catch { client.Dispose(); throw; }
        }
        public async Task StartRun(string app)
        {
            using var response = await Send("POST", $"/api/platform/v1/tenants/{Tenant}/applications/{app}/releases/1.0.0/runs");
            using var json = JsonDocument.Parse(await Expect(response, 200)); Run = json.RootElement.GetProperty("runId").GetGuid();
        }
        public async Task<HttpResponseMessage> Send(string method, string path, object? body = null)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (body is not null) request.Content = JsonContent.Create(body);
            if (method is "POST" or "PUT") { request.Headers.Add("Origin", origin); request.Headers.Add(BffEndpoints.CsrfHeaderName, csrf); }
            if (path.StartsWith("/api/apps/", StringComparison.Ordinal))
            {
                request.Headers.Add("X-Run-ID", Run.ToString("D")); request.Headers.Add("X-Tenant-ID", Tenant.ToString("D"));
                request.Headers.Add("X-Model-Delegation", SpoofedCapability);
            }
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }
        public void Dispose() => client.Dispose();
    }
    private sealed record Child(Process Process, Task<string> Output, Task<string> Errors) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { if (!Process.HasExited) Process.Kill(entireProcessTree: true); await Process.WaitForExitAsync(); Process.Dispose(); }
    }
    private sealed class StreamControl
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Fixture
    {
        public string App { get; } = "model-flow-" + Id();
        public Guid Owner { get; } = Guid.NewGuid(); public Guid Reviewer { get; } = Guid.NewGuid();
        public Guid Tenant { get; } = Guid.NewGuid(); public Guid Role { get; } = Guid.NewGuid();
    }
    private sealed record Provider(string Endpoint, string Model, string CredentialVariable, string? ReasoningEffort);
    private sealed record StreamItem(string Type, string? Content, string RequestId);
    private static string Id() => Guid.NewGuid().ToString("N");
    private static string FreeOrigin() { using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); return "http://127.0.0.1:" + ((IPEndPoint)socket.LocalEndpoint).Port; }
    private static string Workspace() { for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent) if (File.Exists(Path.Combine(path.FullName, "AssembleAI.slnx"))) return path.FullName; throw new InvalidOperationException("Workspace not found."); }
    private static async Task Execute(NpgsqlConnection db, string sql, params object[] values) { await using var command = new NpgsqlCommand(sql, db); foreach (var value in values) command.Parameters.AddWithValue(value); await command.ExecuteNonQueryAsync(); }
    private static void Ok<T>(RegistryResult<T> result) => Check(result.Error == RegistryError.None, "Real registry state transition failed.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
