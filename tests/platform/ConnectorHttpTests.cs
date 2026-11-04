using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Connectors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AssembleAI.Platform.SmokeTests;

// Real OIDC/BFF/CSRF/HTTP. Explicit memory stores and an interruptible client
// isolate authorization/audit delivery races from the separate PostgreSQL suite.
public static class ConnectorHttpTests
{
    private const string PrivateValue = "fixture-data-value-never-on-failure";
    public static async Task RunAsync()
    {
        await using var identity = await OidcFixture.StartAsync();
        var state = new State(identity.Authority);
        var origin = FreeOrigin();
        var settings = Settings();
        await using var app = PlatformApplication.Build([
            "--environment", "Development", "--urls", origin, "--Authentication:Bff:Enabled", "true",
            "--Authentication:Bff:Authority", identity.Authority, "--Authentication:Bff:ClientId", OidcFixture.ClientId,
            "--Authentication:Bff:ClientSecret", OidcFixture.ClientSecret, "--Authentication:Bff:PublicOrigin", origin
        ], builder => {
            builder.Logging.ClearProviders(); builder.Configuration.AddInMemoryCollection(settings);
            builder.Services.AddSingleton<IAuthorizationStore>(state); builder.Services.AddSingleton<IConnectorStore>(state);
            builder.Services.AddSingleton<IConnectorClient>(state);
        });
        await app.StartAsync();
        try
        {
            var path = $"/api/platform/v1/tenants/{state.Tenant}/connectors/manufacturing/datasets/production/query";
            using var anonymous = new HttpClient { BaseAddress = new(origin) };
            using (var response = await anonymous.PostAsJsonAsync(path, Query())) Check(response.StatusCode == HttpStatusCode.Unauthorized, "Anonymous queries require real login.");
            using var browser = await Browser.Login(origin);
            await browser.Select(state.Tenant);
            using (var valid = await browser.Send(path, Query()))
            {
                var body = await Expect(valid, 200);
                Check(body.Contains(PrivateValue, StringComparison.Ordinal) && state.ClientCalls == 1, "Successful queries deliver the actual client data after audit.");
                Check(state.Calls.Values.Single() is { Outcome: "completed", RowCount: 1 }, "The client result must be audited before success.");
            }
            var before = state.ClientCalls;
            using (var csrf = await browser.Send(path, Query(), csrf: false)) await Denied(csrf, 403);
            using (var unknown = await browser.Send(path, new { columns = new[] { "line" }, sql = "SELECT private", limit = 2 })) await Denied(unknown, 400);
            using (var oversized = await browser.Send(path, new StringContent(new string('x', 16385), Encoding.UTF8, "application/json"))) await Denied(oversized, 400);
            Check(state.ClientCalls == before, "CSRF or invalid-body requests must not reach the connector.");

            state.FailBegin = true;
            using (var failure = await browser.Send(path, Query())) await Denied(failure, 503);
            Check(state.ClientCalls == before, "Admission audit failure must produce zero client calls.");
            state.FailBegin = false;
            state.FailComplete = true;
            using (var failure = await browser.Send(path, Query())) await Denied(failure, 503);
            Check(state.ClientCalls == before + 1 && state.Calls.Values.Any(call => call.Outcome == "running"),
                "Completion audit failure must retain pending audit and withhold the result.");
            state.FailComplete = false;

            foreach (var change in new[] { "permission", "logout", "tenant" })
            {
                state.CanRead = true;
                using var active = await Browser.Login(origin);
                await active.Select(state.Tenant);
                var gate = state.NextCompletion = new();
                var response = active.Send(path, Query());
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (change == "permission") state.CanRead = false;
                else if (change == "logout") { using var logout = await active.Send("/bff/logout", null); await Expect(logout, 204); }
                else await active.Select(state.OtherTenant);
                gate.Release.TrySetResult();
                using var denied = await response;
                await Denied(denied, 403);
            }
            state.CanRead = true;
            var hanging = state.NextClient = new();
            var pending = browser.Send(path, Query());
            await hanging.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            state.CanRead = false;
            await hanging.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using (var denied = await pending) await Denied(denied, 403);
            state.CanRead = true;

            // The endpoint's request deadline starts before the execution deadline.
            // Expiring that outer budget must be audited as timeout, not disconnect.
            state.QueryTimeout = 30;
            using (var timedBrowser = await Browser.Login(origin, timeoutSeconds: 45))
            {
                await timedBrowser.Select(state.Tenant);
                var previousCalls = state.Calls.Keys.ToHashSet();
                var timedClient = state.NextClient = new();
                var timedResponse = timedBrowser.Send(path, Query());
                await timedClient.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                using var timeout = await timedResponse;
                await Denied(timeout, 504);
                await timedClient.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var timedCall = state.Calls.Values.Single(call => !previousCalls.Contains(call.CallId));
                Check(timedCall is { Outcome: "timeout", RowCount: null },
                    $"The outer request deadline must audit timeout, not {timedCall.Outcome}.");
            }
            state.QueryTimeout = 10;
            Check(!state.ClientAdmissionViolation, "Every client call requires an existing durable admission record.");
            Console.WriteLine("PASS: connector OIDC HTTP: strict request boundary, audit-before-client, completion-failure data withholding, late permission/logout/tenant revocation, active query cancellation and outer-deadline timeout audit.");
        }
        finally { state.NextCompletion?.Release.TrySetResult(); await app.StopAsync(); }

        // Default-off configuration must stay off even with a real selected BFF session.
        var disabledOrigin = FreeOrigin();
        await using var disabled = PlatformApplication.Build([
            "--environment", "Development", "--urls", disabledOrigin, "--Authentication:Bff:Enabled", "true",
            "--Authentication:Bff:Authority", identity.Authority, "--Authentication:Bff:ClientId", OidcFixture.ClientId,
            "--Authentication:Bff:ClientSecret", OidcFixture.ClientSecret, "--Authentication:Bff:PublicOrigin", disabledOrigin
        ], builder => { builder.Logging.ClearProviders(); builder.Services.AddSingleton<IAuthorizationStore>(state); });
        await disabled.StartAsync();
        try
        {
            using var browser = await Browser.Login(disabledOrigin);
            await browser.Select(state.Tenant);
            using var response = await browser.Send($"/api/platform/v1/tenants/{state.Tenant}/connectors/manufacturing/test", new { });
            await Denied(response, 503);
        }
        finally { await disabled.StopAsync(); }
    }

    private static Dictionary<string, string?> Settings() => new() {
        ["Connectors:Enabled"] = "true",
        ["Connectors:Sources:manufacturing:DisplayName"] = "Manufacturing fixture",
        ["Connectors:Sources:manufacturing:ConnectionEnvironmentVariable"] = "ASSEMBLEAI_CONNECTOR_FIXTURE_UNUSED",
        ["Connectors:Sources:manufacturing:Enabled"] = "true",
        ["Connectors:Sources:manufacturing:MaximumRows"] = "100",
        ["Connectors:Sources:manufacturing:TimeoutSeconds"] = "30",
        ["Connectors:Datasets:production:SourceId"] = "manufacturing",
        ["Connectors:Datasets:production:DisplayName"] = "Production fixture",
        ["Connectors:Datasets:production:Schema"] = "fixture",
        ["Connectors:Datasets:production:Table"] = "production",
        ["Connectors:Datasets:production:Columns:line:Column"] = "line_name",
        ["Connectors:Datasets:production:Columns:line:Type"] = "text"
    };
    private static object Query() => new { columns = new[] { "line" }, filters = Array.Empty<object>(), metrics = Array.Empty<object>(), limit = 2 };
    private static string FreeOrigin() { using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); return "http://127.0.0.1:" + ((IPEndPoint)socket.LocalEndpoint).Port; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task<string> Expect(HttpResponseMessage response, int status)
    {
        Check((int)response.StatusCode == status, $"Connector HTTP expected {status}, received {(int)response.StatusCode}.");
        Check(response.Headers.CacheControl?.NoStore == true, "Connector/BFF responses must prohibit caching.");
        return await response.Content.ReadAsStringAsync();
    }
    private static async Task Denied(HttpResponseMessage response, int status)
    { Check(!(await Expect(response, status)).Contains(PrivateValue, StringComparison.Ordinal), "Failed queries must not release business data."); }

    private sealed class Browser(HttpClient client, string origin, string csrfToken) : IDisposable
    {
        public static async Task<Browser> Login(string origin, int timeoutSeconds = 15)
        {
            var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, CookieContainer = new() }) { BaseAddress = new(origin), Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            try
            {
                using var challenge = await client.GetAsync("/bff/login?returnUrl=/bff/session");
                using var code = await client.GetAsync(challenge.Headers.Location);
                using var callback = await client.GetAsync(code.Headers.Location);
                Check(callback.StatusCode == HttpStatusCode.Redirect, "A real OIDC code exchange must establish the BFF session.");
                using var response = await client.GetAsync("/bff/csrf");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return new(client, origin, json.RootElement.GetProperty("requestToken").GetString()!);
            }
            catch { client.Dispose(); throw; }
        }
        public async Task<HttpResponseMessage> Send(string path, object? body, bool csrf = true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.Add("Origin", origin);
            if (csrf) request.Headers.Add("X-CSRF-TOKEN", csrfToken);
            if (body is not null) request.Content = body as HttpContent ?? JsonContent.Create(body);
            return await client.SendAsync(request);
        }
        public async Task Select(Guid tenant) { using var response = await Send("/bff/tenant", new { tenantId = tenant }); await Expect(response, 200); }
        public void Dispose() => client.Dispose();
    }
    private sealed class Gate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class State(string issuer) : IAuthorizationStore, IConnectorStore, IConnectorClient
    {
        public Guid Tenant { get; } = Guid.NewGuid(); public Guid OtherTenant { get; } = Guid.NewGuid();
        public Guid User { get; } = Guid.NewGuid(); public Guid Factory { get; } = Guid.NewGuid();
        public volatile bool CanRead = true, FailBegin, FailComplete, ClientAdmissionViolation;
        public int QueryTimeout = 10;
        public int ClientCalls;
        public Gate? NextCompletion, NextClient;
        public ConcurrentDictionary<Guid, ConnectorCallRecord> Calls { get; } = new();
        public Task<IReadOnlyList<TenantMembership>> ListTenantsAsync(SubjectIdentity identity, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TenantMembership>>([new(Tenant, "A"), new(OtherTenant, "B")]);
        public Task<AccessSnapshot?> ReadAccessAsync(SubjectIdentity identity, Guid tenantId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (identity != new SubjectIdentity(issuer, OidcFixture.Subject) || tenantId != Tenant && tenantId != OtherTenant) return Task.FromResult<AccessSnapshot?>(null);
            var grants = new List<PermissionGrant> { new("datasource:manage", "datasource", "manufacturing", true, new HashSet<Guid>()) };
            if (CanRead)
                grants.AddRange([new("datasource:read", "datasource", "manufacturing", true, new HashSet<Guid>()),
                    new("dataset:read", "dataset", "production", true, new HashSet<Guid>()), new("tool:invoke", "tool", "connector.query", true, new HashSet<Guid>())]);
            return Task.FromResult<AccessSnapshot?>(new(User, tenantId, CanRead ? 1 : 2, grants, new HashSet<Guid> { Factory }));
        }
        public Task<TenantConnectorConfiguration?> ReadConfigurationAsync(Guid tenantId, string sourceId, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult<TenantConnectorConfiguration?>(tenantId == Tenant && sourceId == "manufacturing" ? new(Tenant, sourceId, true, 100, QueryTimeout, 1) : null); }
        public Task<TenantConnectorConfiguration?> SaveConfigurationAsync(Guid tenantId, string sourceId, bool enabled, int maxRows, int timeoutSeconds, long expectedRevision, Guid actorUserId, string requestId, CancellationToken token) => throw new NotSupportedException();
        public Task<Guid> BeginCallAsync(Guid tenantId, Guid userId, string sourceId, string? datasetId, string operation, string requestId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); if (FailBegin) throw new ConnectorUnavailableException();
            var id = Guid.NewGuid(); Calls[id] = new(id, tenantId, userId, sourceId, datasetId, operation, requestId, "running", null, 0, DateTimeOffset.UtcNow); return Task.FromResult(id);
        }
        public async Task CompleteCallAsync(Guid callId, string outcome, int? rowCount, long elapsedMilliseconds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); if (FailComplete) throw new ConnectorUnavailableException();
            if (Interlocked.Exchange(ref NextCompletion, null) is { } gate)
            { gate.Entered.TrySetResult(); await gate.Release.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            // The gate intentionally models a commit that can win a cancellation race.
            Calls[callId] = Calls[callId] with { Outcome = outcome, RowCount = rowCount, ElapsedMilliseconds = elapsedMilliseconds };
        }
        public Task<IReadOnlyList<ConnectorCallRecord>> ListCallsAsync(Guid tenantId, string sourceId, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ConnectorCallRecord>>(Calls.Values.Where(value => value.TenantId == tenantId && value.SourceId == sourceId).ToArray());
        public Task TestAsync(ConnectorSource source, IReadOnlyList<ConnectorDataset> datasets, int timeoutSeconds, CancellationToken token) => Task.CompletedTask;
        public async Task<ConnectorResult> QueryAsync(ConnectorSource source, ConnectorDataset dataset, ConnectorQuery query, Guid tenantId, IReadOnlySet<Guid> organizations, int timeoutSeconds, CancellationToken token)
        {
            Interlocked.Increment(ref ClientCalls);
            if (!Calls.Values.Any(call => call.Outcome == "running") || tenantId != Tenant || !organizations.SetEquals(new[] { Factory })) ClientAdmissionViolation = true;
            if (Interlocked.Exchange(ref NextClient, null) is { } gate)
            {
                gate.Entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { gate.Cancelled.TrySetResult(); throw; }
            }
            return new(["line"], [[PrivateValue]]);
        }
    }
}
