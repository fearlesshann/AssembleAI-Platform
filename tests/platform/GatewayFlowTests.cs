using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Gateway;
using AssembleAI.Platform.Identity;
using AssembleAI.Platform.InternalCalls;
using AssembleAI.Platform.Registry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AssembleAI.Platform.SmokeTests;

// Real OIDC, BFF cookies, HTTP forwarding and signed downstream calls; the
// authorization/registry fixtures below are explicitly in-memory, not PostgreSQL.
public static class GatewayFlowTests
{
    public static async Task RunAsync()
    {
        var root = FindWorkspace();
        var parent = Path.GetFullPath(Path.Combine(root, ".local-data", "gateway-flow-tests"));
        var directory = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var identity = await OidcFixture.StartAsync();
            var state = new Fixture(identity.Authority);
            using var rsa = RSA.Create(2048);
            using var certificate = new CertificateRequest("CN=gateway-flow-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var pfx = Path.Combine(directory, "signer.pfx");
            var cer = Path.Combine(directory, "signer.cer");
            var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pfx, password));
            File.WriteAllBytes(cer, certificate.Export(X509ContentType.Cert));
            var verifierOptions = new InternalCallOptions { Enabled = true, Issuer = "https://platform.example.test/internal", ValidationCertificatePaths = [cer] };
            using var verifierKeys = new InternalCallKeys(verifierOptions);
            var verifier = new InternalCallTokenService(verifierOptions, verifierKeys, state, TimeProvider.System);
            var controls = new ConcurrentQueue<StreamControl>();
            var allControls = new List<StreamControl>();
            var slowCancelled = Signal();
            var headersOnlyCancelled = Signal();
            var jsonEntered = 0;
            var jsonVerified = 0;
            var jsonBodyCompleted = 0;
            var downstreamBuilder = WebApplication.CreateBuilder(["--environment", "Development", "--urls", "http://127.0.0.1:0"]);
            downstreamBuilder.Logging.ClearProviders();
            await using var downstream = downstreamBuilder.Build();
            downstream.MapGet("/health/ready", () => Results.Ok(new { status = "healthy", service = "production-api", version = "1.0.0", protocolVersion = 1 }));
            downstream.MapMethods("/{operation}", ["GET", "POST"], async (HttpContext context) =>
            {
                if (context.Request.Path == "/json") Interlocked.Increment(ref jsonEntered);
                var requestId = context.Request.Headers["X-Request-ID"].ToString();
                var target = new InternalCallTarget(state.AppId, "1.0.0", "assembleai:production-api", "production:read",
                    new(state.TenantA, "application", state.AppId, null), context.Request.Method, context.Request.Path.Value!);
                var auth = context.Request.Headers.Authorization;
                if (auth.Count != 1 || !AuthenticationHeaderValue.TryParse(auth[0], out var header)
                    || header.Scheme != "Bearer" || header.Parameter is null
                    || await verifier.ValidateAsync(header.Parameter, target, requestId, context.RequestAborted) is null)
                { context.Response.StatusCode = 403; return; }
                if (context.Request.Path == "/json") Interlocked.Increment(ref jsonVerified);
                context.Response.Headers.Append("Set-Cookie", "assembleai-session=downstream-poison; Path=/");
                context.Response.Headers.Location = "https://downstream.example/private";
                context.Response.Headers.AccessControlAllowOrigin = "*";
                if (context.Request.Path == "/headers-only")
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.StartAsync(context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    try { await Task.Delay(TimeSpan.FromSeconds(20), context.RequestAborted); }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { headersOnlyCancelled.TrySetResult(); }
                    return;
                }
                if (context.Request.Path == "/latin")
                {
                    context.Response.ContentType = "application/json; charset=iso-8859-1";
                    await context.Response.Body.WriteAsync(Encoding.Latin1.GetBytes("{\"value\":\"fixture-latin-payload café\"}"), context.RequestAborted);
                    return;
                }
                if (context.Request.Path == "/redirect")
                {
                    context.Response.StatusCode = 302;
                    context.Response.ContentType = "text/html";
                    await context.Response.WriteAsync("fixture-hidden-redirect", context.RequestAborted);
                    return;
                }
                if (context.Request.Path == "/failure")
                {
                    context.Response.StatusCode = 500;
                    await context.Response.WriteAsJsonAsync(new { detail = "fixture-hidden-error" }, context.RequestAborted);
                    return;
                }
                if (context.Request.Path == "/json")
                {
                    string? input = null;
                    if (context.Request.Method == "POST")
                    {
                        using var payload = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                        input = payload.RootElement.GetProperty("question").GetString();
                        Interlocked.Increment(ref jsonBodyCompleted);
                    }
                    await context.Response.WriteAsJsonAsync(new
                    {
                        signed = true, requestId, input, query = context.Request.Query["q"].ToString(),
                        leaked = new[] { "Cookie", "Origin", "X-Internal-User", "X-Forwarded-For", "X-Tenant-ID", "X-Run-ID" }.Any(context.Request.Headers.ContainsKey),
                        forgedAuthorization = header.Parameter == "forged"
                    }, context.RequestAborted);
                    return;
                }
                if (context.Request.Path == "/slow")
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(20), context.RequestAborted); }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { slowCancelled.TrySetResult(); }
                    return;
                }
                context.Response.ContentType = "text/event-stream";
                if (context.Request.Path == "/error")
                {
                    await context.Response.WriteAsync("event: error\ndata: {\"code\":\"fixture_error\"}\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    return;
                }
                if (!controls.TryDequeue(out var control)) { context.Response.StatusCode = 500; return; }
                try
                {
                    await context.Response.WriteAsync("event: delta\ndata: first\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    if (control.Heartbeat)
                    {
                        while (!control.Complete.Task.IsCompleted)
                        {
                            await Task.WhenAny(control.Complete.Task, Task.Delay(100, context.RequestAborted));
                            context.RequestAborted.ThrowIfCancellationRequested();
                            await context.Response.WriteAsync(": heartbeat\n\n", context.RequestAborted);
                            await context.Response.Body.FlushAsync(context.RequestAborted);
                        }
                    }
                    else await control.Complete.Task.WaitAsync(context.RequestAborted);
                    await context.Response.WriteAsync("event: complete\ndata: done\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
                catch (Exception e) when (e is OperationCanceledException or IOException)
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
                var origin = FreeOrigin();
                using var proxyLogs = new ProxyLogs();
                var config = ManifestTests.CatalogSettings(state.Manifest);
                config["Logging:LogLevel:Yarp"] = "Trace";
                config["Registry:BackendBindings:0:Destination"] = downstream.Urls.Single();
                config["Gateway:Enabled"] = "true";
                config["Gateway:PerUserConcurrency"] = "1";
                config["Gateway:GlobalConcurrency"] = "8";
                config["Gateway:RequestsPerMinute"] = "1000";
                // Functional setup must not race first-use cryptography/JIT on a
                // busy machine. Deliberate timeout tests select short budgets below.
                config["Gateway:ResponseHeadersSeconds"] = "10";
                config["Gateway:IdleSeconds"] = "10";
                config["Gateway:CallSeconds"] = "30";
                config["Gateway:RecheckSeconds"] = "1";
                var operations = new[] { ("GET", "/stream"), ("GET", "/json"), ("POST", "/json"), ("GET", "/error"),
                    ("GET", "/slow"), ("GET", "/latin"), ("GET", "/redirect"), ("GET", "/failure"), ("GET", "/headers-only") };
                for (var i = 0; i < operations.Length; i++)
                {
                    config[$"Gateway:Operations:{i}:Service"] = "production-api";
                    config[$"Gateway:Operations:{i}:Version"] = "1.0.0";
                    config[$"Gateway:Operations:{i}:Method"] = operations[i].Item1;
                    config[$"Gateway:Operations:{i}:Path"] = operations[i].Item2;
                    config[$"Gateway:Operations:{i}:Permission"] = "production:read";
                }
                await using var platform = PlatformApplication.Build([
                    "--environment", "Development", "--urls", origin,
                    "--Authentication:Bff:Enabled", "true", "--Authentication:Bff:Authority", identity.Authority,
                    "--Authentication:Bff:ClientId", OidcFixture.ClientId, "--Authentication:Bff:ClientSecret", OidcFixture.ClientSecret,
                    "--Authentication:Bff:PublicOrigin", origin, "--InternalCalls:Enabled", "true",
                    "--InternalCalls:Issuer", verifierOptions.Issuer, "--InternalCalls:SigningCertificatePath", pfx,
                    "--InternalCalls:SigningCertificatePassword", password
                ], builder =>
                {
                    builder.Logging.ClearProviders();
                    builder.Logging.AddProvider(proxyLogs);
                    builder.Configuration.AddInMemoryCollection(config);
                    builder.Services.AddSingleton<IAuthorizationStore>(state);
                    builder.Services.AddSingleton<IGatewayStore>(state);
                });
                await platform.StartAsync();
                try
                {
                    using var browser = await Browser.Login(identity, origin, OidcFixture.Subject);
                    var create = $"/api/platform/v1/tenants/{state.TenantA}/applications/{state.AppId}/releases/1.0.0/runs";
                    await browser.Expect("POST", create, 403);
                    await browser.Select(state.TenantA);
                    await browser.Expect("POST", create, 403, csrf: false);
                    await browser.Expect("POST", create, 400, new { unexpected = "run-start-body" });
                    using var runResponse = await browser.Send("POST", create);
                    await Expect(runResponse, 200);
                    using var runJson = JsonDocument.Parse(await runResponse.Content.ReadAsStringAsync());
                    var runId = runJson.RootElement.GetProperty("runId").GetGuid();
                    var api = $"/api/apps/{state.AppId}/releases/1.0.0";
                    Assert(runJson.RootElement.GetProperty("apiBase").GetString()!.StartsWith(api, StringComparison.Ordinal), "Run must return a pinned release API base.");
                    browser.Run = runId; browser.Tenant = state.TenantA;
                    using (var missing = await browser.Send("GET", api + "/json", includeRun: false)) await Denied(missing);
                    using (var badTenant = await browser.Send("GET", api + "/json", tenant: state.TenantB)) await Denied(badTenant);
                    using (var guessed = await browser.Send("GET", api.Replace("1.0.0", "0.9.0") + "/json")) await Denied(guessed);
                    using (var unknown = await browser.Send("GET", api + "/not-registered")) await Denied(unknown);
                    await browser.Expect("POST", api + "/json", 403, new { question = "指标" }, csrf: false);
                    await browser.Expect("GET", api + "/json", 400, new { question = "get-body" });
                    using (var other = await Browser.Login(identity, origin, "bob"))
                    {
                        await other.Select(state.TenantA); other.Run = runId; other.Tenant = state.TenantA;
                        using var crossed = await other.Send("GET", api + "/json"); await Denied(crossed);
                    }
                    using (var json = await browser.Send("POST", api + "/json?q=%E4%BA%A7%E9%87%8F&private=gateway-log-secret-marker", new { question = "今日产量" }, forged: true))
                    {
                        Assert(json.StatusCode == HttpStatusCode.OK,
                            $"Normal JSON forwarding returned {(int)json.StatusCode}; outcome={state.LastOutcome}; downstream entered={Volatile.Read(ref jsonEntered)}, verified={Volatile.Read(ref jsonVerified)}, bodyComplete={Volatile.Read(ref jsonBodyCompleted)}.");
                        var result = await Expect(json, 200);
                        using var payload = JsonDocument.Parse(result);
                        Assert(payload.RootElement.GetProperty("signed").GetBoolean() && !payload.RootElement.GetProperty("leaked").GetBoolean()
                            && !payload.RootElement.GetProperty("forgedAuthorization").GetBoolean(), "Only the gateway's signed identity may reach the downstream.");
                        Assert(payload.RootElement.GetProperty("input").GetString() == "今日产量" && payload.RootElement.GetProperty("query").GetString() == "产量", "JSON body and UTF-8 query parameters must survive forwarding.");
                        Assert(json.Headers.GetValues("X-Request-ID").Single() == payload.RootElement.GetProperty("requestId").GetString(), "Request correlation must match the verified downstream token.");
                    }
                    await Settled(state);
                    Assert(!proxyLogs.Messages.Any(message => message.Contains(downstream.Urls.Single(), StringComparison.Ordinal)
                        || message.Contains("gateway-log-secret-marker", StringComparison.Ordinal)),
                        "A more specific YARP Trace logging configuration must not expose downstream URLs or raw query markers.");
                    // No request is in flight here. These are test-only mutable
                    // options on this isolated host, not production configuration.
                    var faultOptions = platform.Services.GetRequiredService<GatewayOptions>();
                    faultOptions.ResponseHeadersSeconds = 2;
                    faultOptions.IdleSeconds = 2;
                    faultOptions.CallSeconds = 6;
                    using (var invalidCharset = await browser.Send("POST", api + "/json", new { question = "café" }, contentType: "application/json; charset=iso-8859-1"))
                        await Expect(invalidCharset, 415);
                    using (var encoded = await browser.Send("POST", api + "/json", new { question = "content-coding" }, contentEncoding: "gzip"))
                        await Expect(encoded, 415);
                    using (var headerOnly = await browser.Send("GET", api + "/headers-only"))
                    {
                        Assert(headerOnly.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout,
                            "A downstream that sends headers and then stalls must fail, not finish with a successful empty body.");
                        Headers(headerOnly);
                        await headerOnly.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    await headersOnlyCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    await Settled(state);
                    foreach (var refusedPath in new[] { "/latin", "/redirect", "/failure" })
                    {
                        using var refused = await browser.Send("GET", api + refusedPath);
                        var body = await Expect(refused, 502);
                        Assert(!body.Contains("fixture-latin-payload", StringComparison.Ordinal)
                            && !body.Contains("fixture-hidden-", StringComparison.Ordinal)
                            && !body.Contains("downstream.example", StringComparison.Ordinal),
                            "Rejected downstream content and destinations must not leak through error bodies.");
                        await Settled(state);
                    }
                    var first = Enqueue(true);
                    using (var response = await browser.Send("GET", api + "/stream"))
                    using (var reader = await FirstDelta(response))
                    {
                        Assert(!first.Complete.Task.IsCompleted, "The first event must arrive before downstream completion.");
                        await browser.Expect("GET", api + "/json", 429);
                        first.Complete.TrySetResult();
                        var rest = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
                        Assert(rest.Contains("event: complete", StringComparison.Ordinal), "Normal completion must remain visible.");
                    }
                    await Settled(state);
                    await browser.Expect("GET", api + "/json", 200);
                    await Settled(state);
                    using (var error = await browser.Send("GET", api + "/error"))
                    {
                        var body = await Expect(error, 200);
                        Assert(body.Contains("event: error", StringComparison.Ordinal) && !body.Contains("event: complete", StringComparison.Ordinal), "A business error must not become successful completion.");
                    }
                    await Settled(state);
                    var disconnect = Enqueue(true);
                    using (var response = await browser.Send("GET", api + "/stream"))
                    using (await FirstDelta(response)) { }
                    await disconnect.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    await Settled(state);
                    await CancelAfter(() => { state.Authorized = false; return Task.CompletedTask; }, browser);
                    state.Authorized = true;
                    await CancelAfter(() => { state.Enabled = false; return Task.CompletedTask; }, browser);
                    state.Enabled = true;
                    await CancelAfter(() => browser.Select(state.TenantB), browser);
                    await browser.Select(state.TenantA);
                    var idle = Enqueue(false);
                    using (var response = await browser.Send("GET", api + "/stream"))
                    using (var reader = await FirstDelta(response)) await Interrupted(reader);
                    await idle.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    await Settled(state);
                    var total = Enqueue(true);
                    var deadline = Stopwatch.StartNew();
                    using (var response = await browser.Send("GET", api + "/stream"))
                    using (var reader = await FirstDelta(response)) await Interrupted(reader);
                    await total.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert(deadline.Elapsed < TimeSpan.FromSeconds(10), "Continuous heartbeat must not extend the total call deadline.");
                    await Settled(state);
                    Assert(state.LastOutcome == "deadline", "A total deadline must be recorded separately from revocation.");
                    using (var slow = await browser.Send("GET", api + "/slow")) await Expect(slow, 504);
                    await slowCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    await Settled(state);
                    // V29: persistence can consume the entire call budget. A
                    // successfully recorded start must still be finalized, while
                    // an already-expired call must never reach the downstream.
                    var enteredBeforeExpiredStart = Volatile.Read(ref jsonEntered);
                    var startsBeforeExpiredStart = state.Started;
                    faultOptions.CallSeconds = 1;
                    state.StartDelaySeconds = 2;
                    try
                    {
                        using var expiredBeforeForwarding = await browser.Send("GET", api + "/json");
                        await Expect(expiredBeforeForwarding, 504);
                        await Settled(state);
                        Assert(state.Started == startsBeforeExpiredStart + 1 && state.Finished == state.Started,
                            "V29: a start persisted after its deadline must receive a terminal audit update.");
                        Assert(Volatile.Read(ref jsonEntered) == enteredBeforeExpiredStart,
                            "V29: deadline expiry during start persistence must prevent any downstream dispatch.");
                    }
                    finally { faultOptions.CallSeconds = 6; state.StartDelaySeconds = 0; }
                    await CancelAfter(() => browser.Expect("POST", "/bff/logout", 204), browser);
                    Assert(state.Started > 5 && state.Finished == state.Started, "All started fixture calls must receive a terminal audit update, including cancellations.");
                    Console.WriteLine("PASS: gateway real OIDC/YARP/signed downstream flow with in-memory control state: pinned runs, tenant/session boundaries, header isolation, streaming, limiter recovery, disconnect/revocation/logout and deadlines.");

                    StreamControl Enqueue(bool heartbeat)
                    {
                        var control = new StreamControl(heartbeat); controls.Enqueue(control); allControls.Add(control); return control;
                    }
                    async Task CancelAfter(Func<Task> change, Browser current)
                    {
                        var control = Enqueue(true);
                        using var response = await current.Send("GET", api + "/stream");
                        using var reader = await FirstDelta(response);
                        await change();
                        await Interrupted(reader);
                        await control.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        await Settled(state);
                        Assert(state.LastOutcome == "revoked", "The changed authorization/session must cause cancellation before the unrelated total deadline.");
                    }
                }
                finally
                {
                    foreach (var control in allControls) control.Complete.TrySetResult();
                    await platform.StopAsync();
                }
            }
            finally { await downstream.StopAsync(); }
        }
        finally
        {
            var resolved = Path.GetFullPath(directory);
            Assert(Path.GetDirectoryName(resolved) == parent, "Only this owned test directory may be removed.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }

    private static async Task<StreamReader> FirstDelta(HttpResponseMessage response)
    {
        Assert(response.StatusCode == HttpStatusCode.OK, "Stream must return HTTP 200 before reading its first event.");
        Headers(response);
        var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            for (var i = 0; i < 8; i++)
                if (await reader.ReadLineAsync(timeout.Token) == "data: first") return reader;
            throw new InvalidOperationException("The first SSE delta was missing.");
        }
        catch { reader.Dispose(); throw; }
    }
    private static async Task Interrupted(StreamReader reader)
    {
        try
        {
            var remainder = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert(!remainder.Contains("event: complete", StringComparison.Ordinal), "An interrupted stream cannot report successful completion.");
        }
        catch (Exception e) when (e is IOException or HttpRequestException) { }
    }
    private static async Task Settled(Fixture state)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (state.Finished != state.Started) await Task.Delay(20, timeout.Token);
        // Completion audit can precede release of the endpoint's concurrency lease.
        await Task.Delay(40, timeout.Token);
    }
    private static void Headers(HttpResponseMessage response)
    {
        Assert(response.Headers.CacheControl?.NoStore == true, "Gateway results must prohibit caching.");
        Assert(response.Headers.TryGetValues("X-Request-ID", out var ids) && ids.Count() == 1 && !string.IsNullOrWhiteSpace(ids.Single()),
            "Gateway responses, including errors after downstream headers, must preserve the generated request ID.");
        Assert(!response.Headers.Contains("Set-Cookie") && !response.Headers.Contains("Location") && !response.Headers.Contains("Access-Control-Allow-Origin"), "Downstream cookies, redirects and CORS must not escape onto the platform origin.");
    }
    private static async Task<string> Expect(HttpResponseMessage response, int status)
    {
        Assert((int)response.StatusCode == status, $"Gateway flow expected {status}, got {(int)response.StatusCode}.");
        if (status != 204) Headers(response);
        return await response.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
    private static Task Denied(HttpResponseMessage response)
    {
        Assert(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Conflict, "Invalid runtime context must fail with an explicit rejection.");
        Headers(response); return Task.CompletedTask;
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class StreamControl(bool heartbeat)
    {
        public bool Heartbeat { get; } = heartbeat;
        public TaskCompletionSource Complete { get; } = Signal();
        public TaskCompletionSource Cancelled { get; } = Signal();
        public TaskCompletionSource Stopped { get; } = Signal();
    }
    private sealed class Browser(HttpClient client, string origin, string requestToken) : IDisposable
    {
        public Guid Run { get; set; }
        public Guid Tenant { get; set; }
        public static async Task<Browser> Login(OidcFixture identity, string origin, string subject)
        {
            identity.NextSubject = subject;
            var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, CookieContainer = new(), MaxResponseDrainSize = 0 })
            { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(15) };
            try
            {
                using var challenge = await client.GetAsync("/bff/login?returnUrl=/bff/session");
                Assert(challenge.StatusCode == HttpStatusCode.Redirect, "OIDC challenge required.");
                using var code = await client.GetAsync(challenge.Headers.Location);
                Assert(code.StatusCode == HttpStatusCode.Redirect, "OIDC authorization code required.");
                using var callback = await client.GetAsync(code.Headers.Location);
                Assert(callback.StatusCode == HttpStatusCode.Redirect, "Validated OIDC callback required.");
                using var response = await client.GetAsync("/bff/csrf");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return new(client, origin, json.RootElement.GetProperty("requestToken").GetString()!);
            }
            catch { client.Dispose(); throw; }
        }
        public async Task<HttpResponseMessage> Send(string method, string path, object? body = null, bool csrf = true, bool includeRun = true, Guid? tenant = null,
            bool forged = false, string? contentType = null, string? contentEncoding = null)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (body is not null) request.Content = JsonContent.Create(body);
            if (request.Content is not null && contentType is not null) request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            if (request.Content is not null && contentEncoding is not null) request.Content.Headers.ContentEncoding.Add(contentEncoding);
            if (method == "POST")
            {
                request.Headers.Add("Origin", origin);
                if (csrf) request.Headers.Add(BffEndpoints.CsrfHeaderName, requestToken);
            }
            if (path.StartsWith("/api/apps/", StringComparison.Ordinal) && includeRun)
            {
                request.Headers.Add("X-Run-ID", Run.ToString("D"));
                request.Headers.Add("X-Tenant-ID", (tenant ?? Tenant).ToString("D"));
            }
            if (forged)
            {
                request.Headers.Authorization = new("Bearer", "forged");
                request.Headers.Add("Cookie", "untrusted=browser-cookie");
                request.Headers.Add("X-Internal-User", "administrator");
                request.Headers.Add("X-Forwarded-For", "192.0.2.1");
                request.Headers.Add("X-Request-ID", "forged-request-id");
            }
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }
        public async Task Expect(string method, string path, int status, object? body = null, bool csrf = true)
        {
            using var response = await Send(method, path, body, csrf);
            if (path.StartsWith("/bff/", StringComparison.Ordinal))
                Assert((int)response.StatusCode == status && response.Headers.CacheControl?.NoStore == true, "BFF control operation failed.");
            else await GatewayFlowTests.Expect(response, status);
        }
        public Task Select(Guid tenant) => Expect("POST", "/bff/tenant", 200, new { tenantId = tenant });
        public void Dispose() => client.Dispose();
    }
    private sealed class Fixture(string issuer) : IAuthorizationStore, IGatewayStore
    {
        private readonly ConcurrentDictionary<Guid, (SubjectIdentity Subject, string Session, GatewayRuntime Runtime)> runs = new();
        private int started, finished;
        private string lastOutcome = "";
        private volatile bool authorized = true, enabled = true;
        public bool Authorized { get => authorized; set => authorized = value; }
        public bool Enabled { get => enabled; set => enabled = value; }
        public int Started => Volatile.Read(ref started);
        public int Finished => Volatile.Read(ref finished);
        public string LastOutcome => Volatile.Read(ref lastOutcome);
        public int StartDelaySeconds { get; set; }
        public string AppId => "production-reports";
        public Guid UserA { get; } = Guid.NewGuid();
        public Guid UserB { get; } = Guid.NewGuid();
        public Guid TenantA { get; } = Guid.NewGuid();
        public Guid TenantB { get; } = Guid.NewGuid();
        public ApplicationManifest Manifest => ManifestTests.Valid() with { Status = "published" };
        public Task<IReadOnlyList<TenantMembership>> ListTenantsAsync(SubjectIdentity subject, CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<TenantMembership>>([new(TenantA, "A"), new(TenantB, "B")]);
        public Task<AccessSnapshot?> ReadAccessAsync(SubjectIdentity subject, Guid tenantId, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (subject.Issuer != issuer || subject.Subject is not ("alice" or "bob") || tenantId != TenantA && tenantId != TenantB) return Task.FromResult<AccessSnapshot?>(null);
            return Task.FromResult<AccessSnapshot?>(new(subject.Subject == "alice" ? UserA : UserB, tenantId, 1,
                Authorized ? [new("production:read", "application", AppId, true, new HashSet<Guid>())] : [], new HashSet<Guid>()));
        }
        public Task<ApplicationManifest?> ResolveStartAsync(SubjectIdentity subject, Guid tenantId, string appId, string version, CancellationToken token = default) =>
            Task.FromResult<ApplicationManifest?>(Enabled && Authorized && tenantId == TenantA && appId == AppId && version == "1.0.0" ? Manifest : null);
        public async Task<GatewayRuntime?> CreateRunAsync(SubjectIdentity subject, string sessionKey, Guid tenantId, string appId, string version, TimeSpan lifetime, TimeSpan drainWindow, CancellationToken token = default)
        {
            if (await ResolveStartAsync(subject, tenantId, appId, version, token) is not { } manifest) return null;
            var runtime = new GatewayRuntime(Guid.NewGuid(), subject.Subject == "alice" ? UserA : UserB, tenantId, appId, version, manifest, DateTimeOffset.UtcNow.Add(lifetime), null);
            runs[runtime.RunId] = (subject, sessionKey, runtime); return runtime;
        }
        public Task<GatewayRuntime?> ReadRunAsync(SubjectIdentity subject, string sessionKey, Guid runId, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<GatewayRuntime?>(Enabled && Authorized && runs.TryGetValue(runId, out var entry) && entry.Subject == subject && entry.Session == sessionKey && entry.Runtime.ExpiresAt > DateTimeOffset.UtcNow ? entry.Runtime : null);
        }
        public async Task StartCallAsync(GatewayRuntime runtime, string requestId, string method, string path, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (StartDelaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(StartDelaySeconds), token);
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref started);
        }
        public Task FinishCallAsync(string requestId, int httpStatus, string outcome, long elapsedMilliseconds, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Volatile.Write(ref lastOutcome, outcome); Interlocked.Increment(ref finished); return Task.CompletedTask; }
    }
    private sealed class ProxyLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Collector(this, categoryName);
        public void Dispose() { }
        private sealed class Collector(ProxyLogs owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category.StartsWith("Yarp.", StringComparison.Ordinal)) owner.Messages.Enqueue(formatter(state, exception));
            }
        }
    }
    private static string FreeOrigin()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        return "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    private static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SPEC.md"))) return directory.FullName;
        throw new InvalidOperationException("Cannot locate the repository workspace.");
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
