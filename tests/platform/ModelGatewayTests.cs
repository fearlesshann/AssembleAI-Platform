using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Identity;
using AssembleAI.Platform.Identity.Sessions;
using AssembleAI.Platform.Models;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AssembleAI.Platform.SmokeTests;

// Real OIDC, Cookie/CSRF and streaming HTTP; authorization and audit storage are
// explicitly in-memory. PostgreSQL durability is covered by the separate store suite.
public static class ModelGatewayTests
{
    private const string CredentialVariable = "ASSEMBLEAI_TEST_MODEL_KEY";
    private const string Credential = "fixture-model-credential-private";
    private const string Prompt = "fixture-prompt-private";
    public static async Task RunAsync()
    {
        var previousCredential = Environment.GetEnvironmentVariable(CredentialVariable);
        Environment.SetEnvironmentVariable(CredentialVariable, Credential);
        try { await ExerciseAsync(); }
        finally { Environment.SetEnvironmentVariable(CredentialVariable, previousCredential); }
    }

    private static async Task ExerciseAsync()
    {
        await using var identity = await OidcFixture.StartAsync();
        var state = new State(identity.Authority);
        var controls = new ConcurrentQueue<ProviderControl>();
        var providerBuilder = WebApplication.CreateBuilder(["--environment", "Development", "--urls", "http://127.0.0.1:0"]);
        providerBuilder.Logging.ClearProviders();
        await using var provider = providerBuilder.Build();
        provider.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            Interlocked.Increment(ref state.UpstreamCalls);
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var input = body.RootElement;
            if (!state.Calls.Values.Any(call => call.Outcome == "running")
                || context.Request.Headers.Authorization != "Bearer " + Credential
                || context.Request.Headers.ContainsKey("Cookie") || context.Request.Headers.ContainsKey("Origin")
                || input.GetProperty("model").GetString() != "fixture-model"
                || !input.TryGetProperty("reasoning_effort", out var effort) || effort.GetString() != "none"
                || input.GetProperty("max_tokens").GetInt32() != 32
                || input.GetProperty("messages")[0].GetProperty("content").GetString() != Prompt)
                state.UpstreamViolation = true;
            if (!controls.TryDequeue(out var control)) { context.Response.StatusCode = 500; return; }
            control.CallId = state.Calls.Values.Single(call => call.Outcome == "running").CallId;
            if (control.Hang)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
                catch (OperationCanceledException) { control.Cancelled.TrySetResult(); }
                return;
            }
            if (control.Fail)
            {
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new { error = Credential + " upstream-private-error" });
                return;
            }
            if (!input.GetProperty("stream").GetBoolean())
            {
                await context.Response.WriteAsJsonAsync(new {
                    choices = new[] { new { index = 0, message = new { role = "assistant", content = "模型回复" }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 4, completion_tokens = 2, total_tokens = 6 }
                });
                return;
            }
            context.Response.ContentType = "text/event-stream";
            try
            {
                await context.Response.WriteAsync("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"首段\"},\"finish_reason\":null}]}\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await control.Release.Task.WaitAsync(context.RequestAborted);
                await context.Response.WriteAsync("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
                    + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":4,\"completion_tokens\":2,\"total_tokens\":6}}\n\n"
                    + "data: [DONE]\n\n", context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { control.Cancelled.TrySetResult(); }
            catch (IOException) when (context.RequestAborted.IsCancellationRequested) { control.Cancelled.TrySetResult(); }
        });
        await provider.StartAsync();
        try
        {
            var origin = FreeOrigin();
            var settings = new Dictionary<string, string?> {
                ["Models:Enabled"] = "true", ["Models:CallSeconds"] = "3",
                ["Models:RecheckMilliseconds"] = "100", ["Models:PerUserConcurrency"] = "1",
                ["Models:Providers:local:Endpoint"] = provider.Urls.Single() + "/v1/chat/completions",
                ["Models:Providers:local:AllowInsecureHttp"] = "true",
                ["Models:Providers:local:CredentialEnvironmentVariable"] = CredentialVariable,
                ["Models:Catalog:demo:Provider"] = "local", ["Models:Catalog:demo:UpstreamModel"] = "fixture-model",
                ["Models:Catalog:demo:DisplayName"] = "Demo", ["Models:Catalog:demo:Enabled"] = "true",
                ["Models:Catalog:demo:MaximumOutputTokens"] = "128",
                ["Models:Catalog:demo:ReasoningEffort"] = "none"
            };
            await using var platform = PlatformApplication.Build([
                "--environment", "Development", "--urls", origin,
                "--Authentication:Bff:Enabled", "true", "--Authentication:Bff:Authority", identity.Authority,
                "--Authentication:Bff:ClientId", OidcFixture.ClientId, "--Authentication:Bff:ClientSecret", OidcFixture.ClientSecret,
                "--Authentication:Bff:PublicOrigin", origin
            ], builder => {
                builder.Logging.ClearProviders(); builder.Configuration.AddInMemoryCollection(settings);
                builder.Services.AddSingleton<IAuthorizationStore>(state); builder.Services.AddSingleton<IModelStore>(state);
                builder.Services.PostConfigure<CookieAuthenticationOptions>(BffAuthentication.CookieScheme, options => {
                    var signingIn = options.Events.OnSigningIn;
                    options.Events.OnSigningIn = async context => {
                        await signingIn(context);
                        // One explicitly armed fixture login; retain the production
                        // event and real MemoryTicketStore, including tenant renewal.
                        if (Interlocked.Exchange(ref state.ShortSignIn, 0) == 1)
                            context.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(3);
                    };
                });
            });
            await platform.StartAsync();
            try
            {
                var models = $"/api/platform/v1/tenants/{state.Tenant}/models";
                var configuration = models + "/demo/configuration";
                var chat = models + "/demo/chat";
                using var anonymous = new HttpClient { BaseAddress = new Uri(origin) };
                using (var response = await anonymous.GetAsync(models)) await Expect(response, 401);
                using var browser = await Browser.Login(origin);
                await browser.Expect("GET", models, 403);
                await browser.Expect("POST", "/bff/tenant", 200, new { tenantId = state.Tenant });
                await browser.Expect("POST", chat, 403, Chat()); // No tenant configuration means disabled.
                await browser.Expect("PUT", configuration, 403, Config(true, 0), csrf: false);
                await browser.Expect("PUT", configuration, 200, Config(true, 0));
                await browser.Expect("PUT", configuration, 409, Config(true, 0));
                await browser.Expect("PUT", configuration, 400, new { enabled = true, maxOutputTokens = 32, expectedRevision = 1, endpoint = "http://untrusted.invalid" });
                await browser.Expect("POST", chat, 403, Chat(), csrf: false);
                await browser.Expect("POST", chat, 403, Chat(), origin: "https://untrusted.invalid");
                await browser.Expect("POST", chat.Replace(state.Tenant.ToString(), state.OtherTenant.ToString()), 403, Chat());
                state.ResourceId = "another-model";
                await browser.Expect("POST", chat, 403, Chat());
                using (var filtered = await browser.Send("GET", models))
                    Check(!(await Expect(filtered, 200)).Contains("\"demo\"", StringComparison.Ordinal), "Catalog must hide aliases without a matching model resource grant.");
                state.ResourceId = "demo";
                await browser.Expect("POST", chat, 400, new { messages = new[] { new { role = "user", content = Prompt } }, maxOutputTokens = 32, stream = false, model = "arbitrary-model" });
                foreach (var raw in new[] {
                    """{"messages":[{"role":"user","content":"x"}],"maxOutputTokens":32,"maxOutputTokens":1}""",
                    """{"messages":[{"role":"user","content":"x","content":"y"}],"maxOutputTokens":32}""",
                    """{"messages":[{"role":"user","content":"\uD800"}],"maxOutputTokens":32}""",
                    new string(' ', 65_537) + JsonSerializer.Serialize(Chat())
                }) await browser.Expect("POST", chat, 400, new StringContent(raw, Encoding.UTF8, "application/json"));
                foreach (var limit in new[] { 0, 33, 129 }) await browser.Expect("POST", chat, 400, Chat(maxOutputTokens: limit));
                Check(state.UpstreamCalls == 0 && state.Calls.IsEmpty, "Rejected requests must never begin a call or reach the provider.");
                foreach (var path in new[] { models, configuration })
                {
                    using var response = await browser.Send("GET", path);
                    Safe(await Expect(response, 200), provider.Urls.Single());
                }
                state.FailBegin = true;
                await browser.Expect("POST", chat, 503, Chat());
                Check(state.UpstreamCalls == 0, "Failed start audit must prevent outbound requests.");
                state.FailBegin = false;
                controls.Enqueue(new());
                using (var response = await browser.Send("POST", chat, Chat()))
                {
                    using var result = JsonDocument.Parse(await Expect(response, 200));
                    Check(result.RootElement.GetProperty("content").GetString() == "模型回复"
                        && result.RootElement.GetProperty("finishReason").GetString() == "stop"
                        && result.RootElement.GetProperty("usage").GetProperty("totalTokens").GetInt64() == 6
                        && !string.IsNullOrWhiteSpace(result.RootElement.GetProperty("requestId").GetString()), "JSON must expose normalized content, usage and correlation.");
                }
                await Settled(state);
                var completed = state.Calls.Values.Single();
                Check(completed.Outcome == "completed" && completed.UserId == state.User && completed.TenantId == state.Tenant
                    && completed.ModelId == "demo" && completed.Usage?.TotalTokens == 6, "Completion audit must bind the verified actor, tenant, alias and actual usage.");
                controls.Enqueue(new() { Fail = true });
                using (var failed = await browser.Send("POST", chat, Chat())) Safe(await Expect(failed, 502), provider.Urls.Single());
                await Settled(state);
                var normalStream = new ProviderControl(); controls.Enqueue(normalStream);
                using (var response = await browser.Send("POST", chat, Chat(stream: true)))
                using (var reader = await FirstDelta(response))
                {
                    Check(!normalStream.Release.Task.IsCompleted, "First delta must arrive before the provider completes.");
                    await browser.Expect("POST", chat, 429, Chat());
                    normalStream.Release.TrySetResult();
                    var tail = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Check(tail.Contains("event: usage", StringComparison.Ordinal) && tail.Contains("event: complete", StringComparison.Ordinal), "A complete stream must deliver normalized usage and completion events.");
                    using var usage = JsonDocument.Parse(EventData(tail, "usage"));
                    var tokens = usage.RootElement.GetProperty("usage");
                    Check(tokens.GetProperty("promptTokens").GetInt64() == 4 && tokens.GetProperty("completionTokens").GetInt64() == 2
                        && tokens.GetProperty("totalTokens").GetInt64() == 6 && !tokens.TryGetProperty("TotalTokens", out _), "SSE usage must share the JSON API's camelCase token fields.");
                }
                await Settled(state);
                using (var history = await browser.Send("GET", models + "/demo/calls")) Safe(await Expect(history, 200), provider.Urls.Single());
                var hung = new ProviderControl { Hang = true }; controls.Enqueue(hung);
                using (var response = await browser.Send("POST", chat, Chat())) Safe(await Expect(response, 503), provider.Urls.Single());
                await hung.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Settled(state);
                Check(state.Calls[hung.CallId].Outcome == "timeout", "Total call budget must cancel a provider that never sends headers and persist timeout.");
                var disconnected = new ProviderControl(); controls.Enqueue(disconnected);
                using (var response = await browser.Send("POST", chat, Chat(stream: true)))
                using (var reader = await FirstDelta(response)) { } // Disposing the unread HTTP stream disconnects the browser.
                await disconnected.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Settled(state);
                Check(state.Calls[disconnected.CallId].Outcome == "cancelled", "Browser disconnect must cancel upstream and persist cancelled.");
                var completionGate = state.NextCompletionGate = new CompletionGate();
                var lateRevocation = new ProviderControl(); controls.Enqueue(lateRevocation);
                var pending = browser.Send("POST", chat, Chat());
                await completionGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                state.CanInvoke = false;
                await state.DeniedAccessRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
                completionGate.Release.TrySetResult();
                using (var response = await pending)
                    Check(!(await Expect(response, 403)).Contains("模型回复", StringComparison.Ordinal), "Revocation while final audit waits must prevent delivery of the generated content.");
                await Settled(state);
                Check(state.Calls[lateRevocation.CallId].Outcome == "completed", "Provider completion remains recorded even when later authorization denies delivery.");
                state.CanInvoke = true;
                foreach (var reason in new[] { "revoke", "disable", "dependency", "logout" })
                {
                    var control = new ProviderControl(); controls.Enqueue(control);
                    using var response = await browser.Send("POST", chat, Chat(stream: true));
                    using var reader = await FirstDelta(response);
                    if (reason == "revoke") state.CanInvoke = false;
                    else if (reason == "disable") await browser.Expect("PUT", configuration, 200, Config(false, state.Configuration!.Revision));
                    else if (reason == "dependency") state.ThrowSessionFailure = true;
                    else await browser.Expect("POST", "/bff/logout", 204);
                    await control.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await Interrupted(reader);
                    await Settled(state);
                    Check(state.Calls[control.CallId].Outcome == (reason == "dependency" ? "failed" : "revoked"), $"Cancellation audit must preserve its actual cause: {reason}.");
                    if (reason == "revoke") state.CanInvoke = true;
                    if (reason == "disable") await browser.Expect("PUT", configuration, 200, Config(true, state.Configuration!.Revision));
                    if (reason == "dependency") state.ThrowSessionFailure = false;
                }
                var modelOptions = platform.Services.GetRequiredService<ModelGatewayCatalog>().Options;
                var previousRecheck = modelOptions.RecheckMilliseconds; var previousBudget = modelOptions.CallSeconds;
                modelOptions.RecheckMilliseconds = 5000; modelOptions.CallSeconds = 10;
                state.ShortSignIn = 1;
                try
                {
                    using var expiring = await Browser.Login(origin);
                    await expiring.Expect("POST", "/bff/tenant", 200, new { tenantId = state.Tenant });
                    var expiry = new ProviderControl(); controls.Enqueue(expiry);
                    using var response = await expiring.Send("POST", chat, Chat(stream: true));
                    using var reader = await FirstDelta(response);
                    var reads = Volatile.Read(ref state.AuthorizationReads);
                    await expiry.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(4));
                    await Interrupted(reader); await Settled(state);
                    Check(state.Calls[expiry.CallId].Outcome == "revoked" && Volatile.Read(ref state.AuthorizationReads) == reads,
                        "The absolute session deadline must cancel streaming before the next authorization recheck and persist revoked.");
                }
                finally
                { state.ShortSignIn = 0; modelOptions.RecheckMilliseconds = previousRecheck; modelOptions.CallSeconds = previousBudget; }
                using var recovered = await Browser.Login(origin);
                await recovered.Expect("POST", "/bff/tenant", 200, new { tenantId = state.Tenant });
                var failedAudit = new ProviderControl(); controls.Enqueue(failedAudit);
                using (var response = await recovered.Send("POST", chat, Chat(stream: true)))
                using (var reader = await FirstDelta(response))
                {
                    state.FailComplete = true; failedAudit.Release.TrySetResult();
                    await state.CompletionFailed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await Interrupted(reader);
                    Check(state.Calls[failedAudit.CallId].Outcome == "running", "Failed final audit must leave durable admission unresolved, never invent a completed record.");
                }
                Check(!state.UpstreamViolation && controls.IsEmpty && state.UpstreamCalls == 12, "Provider receives only the trusted model and server credential, after audit admission.");
                Console.WriteLine("PASS: model HTTP: BFF/CSRF, exact grants, strict input, audit ordering/failure, JSON/SSE usage, limits, timeout/disconnect, absolute expiry and live revocation/dependency failure.");
            }
            finally { await platform.StopAsync(); }
        }
        finally { await provider.StopAsync(); }
    }

    private static object Chat(bool stream = false, int maxOutputTokens = 32) => new { messages = new[] { new { role = "user", content = Prompt } }, maxOutputTokens, temperature = 0.2, stream };
    private static object Config(bool enabled, long revision) => new { enabled, maxOutputTokens = 32, expectedRevision = revision };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Safe(string body, string endpoint) => Check(new[] { Credential, CredentialVariable, Prompt, "fixture-model", "upstream-private-error", endpoint }
        .All(secret => !body.Contains(secret, StringComparison.Ordinal)), "Public metadata or errors disclosed private configuration/content.");
    private static async Task<string> Expect(HttpResponseMessage response, int status)
    {
        Check((int)response.StatusCode == status, $"Model gateway expected {status}, received {(int)response.StatusCode}.");
        Check(response.Headers.CacheControl?.NoStore == true, "Model and BFF responses must prohibit caching.");
        return await response.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
    private static async Task<StreamReader> FirstDelta(HttpResponseMessage response)
    {
        Check(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == "text/event-stream", "Streaming call must return SSE.");
        var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            string? eventName = null, data = null;
            for (var i = 0; i < 12; i++)
            {
                var line = await reader.ReadLineAsync(timeout.Token);
                if (line is null) break;
                if (line.StartsWith("event:", StringComparison.Ordinal)) eventName = line[6..].Trim();
                if (line.StartsWith("data:", StringComparison.Ordinal)) data = line[5..].Trim();
                if (line.Length == 0 && eventName == "delta" && data is not null)
                {
                    using var json = JsonDocument.Parse(data);
                    Check(json.RootElement.GetProperty("content").GetString() == "首段", "Incremental content must preserve UTF-8.");
                    return reader;
                }
            }
            throw new InvalidOperationException("First model delta was absent.");
        }
        catch { reader.Dispose(); throw; }
    }
    private static async Task Settled(State state)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (state.Calls.Values.Any(call => call.Outcome == "running")) await Task.Delay(10, timeout.Token);
    }
    private static async Task Interrupted(StreamReader reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
                Check(line.Trim() != "event: complete", "Interrupted generation must not claim successful completion, even before a later connection abort.");
        }
        catch (Exception error) when (error is IOException or HttpRequestException) { }
    }
    private static string EventData(string stream, string name) => stream.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
        .Single(frame => frame.Split('\n').Contains("event: " + name)).Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..];
    private static string FreeOrigin()
    { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port; }
    private sealed class ProviderControl
    {
        public bool Fail { get; init; }
        public bool Hang { get; init; }
        public Guid CallId { get; set; }
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class CompletionGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Browser(HttpClient client, string origin, string csrfToken) : IDisposable
    {
        public static async Task<Browser> Login(string origin)
        {
            var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, CookieContainer = new(), MaxResponseDrainSize = 0 })
                { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(15) };
            try
            {
                using var challenge = await client.GetAsync("/bff/login?returnUrl=/bff/session");
                using var code = await client.GetAsync(challenge.Headers.Location);
                using var callback = await client.GetAsync(code.Headers.Location);
                Check(callback.StatusCode == HttpStatusCode.Redirect, "Real OIDC exchange must establish a BFF session.");
                using var csrf = await client.GetAsync("/bff/csrf");
                using var json = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());
                return new(client, origin, json.RootElement.GetProperty("requestToken").GetString()!);
            }
            catch { client.Dispose(); throw; }
        }
        public async Task<HttpResponseMessage> Send(string method, string path, object? body = null, bool csrf = true, string? requestOrigin = null)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (body is not null) request.Content = body as HttpContent ?? JsonContent.Create(body);
            if (method is "POST" or "PUT")
            { request.Headers.Add("Origin", requestOrigin ?? origin); if (csrf) request.Headers.Add("X-CSRF-TOKEN", csrfToken); }
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }
        public async Task Expect(string method, string path, int status, object? body = null, bool csrf = true, string? origin = null)
        { using var response = await Send(method, path, body, csrf, origin); await ModelGatewayTests.Expect(response, status); }
        public void Dispose() => client.Dispose();
    }
    private sealed class State(string issuer) : IAuthorizationStore, IModelStore
    {
        public Guid Tenant { get; } = Guid.NewGuid();
        public Guid OtherTenant { get; } = Guid.NewGuid();
        public Guid User { get; } = Guid.NewGuid();
        public volatile bool CanInvoke = true, FailBegin, FailComplete, ThrowSessionFailure, UpstreamViolation;
        public CompletionGate? NextCompletionGate;
        public TaskCompletionSource DeniedAccessRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CompletionFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile string ResourceId = "demo";
        public volatile TenantModelConfiguration? Configuration;
        public int UpstreamCalls, ShortSignIn, AuthorizationReads;
        public ConcurrentDictionary<Guid, ModelCallRecord> Calls { get; } = new();
        public Task<IReadOnlyList<TenantMembership>> ListTenantsAsync(SubjectIdentity identity, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TenantMembership>>([new(Tenant, "A"), new(OtherTenant, "B")]);
        public Task<AccessSnapshot?> ReadAccessAsync(SubjectIdentity identity, Guid tenantId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref AuthorizationReads);
            // Inject the session dependency exception at the shared recheck path;
            // this exercises failure handling, not a real broken ticket store.
            if (ThrowSessionFailure) throw new BffSessionStoreException();
            if (!CanInvoke) DeniedAccessRead.TrySetResult();
            if (identity.Issuer != issuer || identity.Subject != OidcFixture.Subject || tenantId != Tenant && tenantId != OtherTenant) return Task.FromResult<AccessSnapshot?>(null);
            var grants = new List<PermissionGrant> { new("model:manage", "model", ResourceId, true, new HashSet<Guid>()) };
            if (CanInvoke) grants.Add(new("model:invoke", "model", ResourceId, true, new HashSet<Guid>()));
            return Task.FromResult<AccessSnapshot?>(new(User, tenantId, CanInvoke ? 1 : 2, grants, new HashSet<Guid>()));
        }
        public Task<TenantModelConfiguration?> ReadConfigurationAsync(Guid tenantId, string modelId, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(Configuration is { } value && value.TenantId == tenantId && value.ModelId == modelId ? value : null); }
        public Task<TenantModelConfiguration?> SaveConfigurationAsync(Guid tenantId, string modelId, bool enabled, int maxOutputTokens, long expectedRevision, Guid actorUserId, string requestId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (Calls)
            {
                if ((Configuration?.Revision ?? 0) != expectedRevision) return Task.FromResult<TenantModelConfiguration?>(null);
                Configuration = new(tenantId, modelId, enabled, maxOutputTokens, expectedRevision + 1);
                return Task.FromResult<TenantModelConfiguration?>(Configuration);
            }
        }
        public Task<Guid> BeginCallAsync(Guid tenantId, Guid userId, string modelId, string requestId, CancellationToken token, ModelCallSource? source = null)
        {
            token.ThrowIfCancellationRequested(); if (FailBegin) throw new ModelStoreUnavailableException();
            var id = Guid.NewGuid(); Calls[id] = new(id, tenantId, userId, modelId, requestId, "running", 0, null, DateTimeOffset.UtcNow); return Task.FromResult(id);
        }
        public async Task CompleteCallAsync(Guid callId, string outcome, long elapsedMilliseconds, ModelUsage? usage, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (FailComplete) { CompletionFailed.TrySetResult(); throw new ModelStoreUnavailableException(); }
            if (Interlocked.Exchange(ref NextCompletionGate, null) is { } gate)
            { gate.Entered.TrySetResult(); await gate.Release.Task.WaitAsync(token); }
            Calls[callId] = Calls[callId] with { Outcome = outcome, ElapsedMilliseconds = elapsedMilliseconds, Usage = usage };
        }
        public Task<IReadOnlyList<ModelCallRecord>> ListCallsAsync(Guid tenantId, string modelId, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<ModelCallRecord>>(Calls.Values.Where(call => call.TenantId == tenantId && call.ModelId == modelId).ToArray()); }
    }
}
