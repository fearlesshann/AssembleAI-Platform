using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Connectors;
using AssembleAI.Platform.Gateway;
using AssembleAI.Platform.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AssembleAI.Platform.SmokeTests;

public static class ConnectorDelegationTests
{
    public static async Task RunAsync()
    {
        Registry();
        await HttpBoundary();
        Console.WriteLine("PASS: connector delegation: isolated dataset capability, parent lifetime, authorized metadata, fixed identity/source, query guards and late revocation.");
    }

    private static void Registry()
    {
        var clock = new Clock();
        var registry = new ConnectorDelegationRegistry(clock, maximumEntries: 1);
        var models = new ModelDelegationRegistry(clock);
        using var lifetime = new CancellationTokenSource();
        var caller = new ModelCaller(new("issuer", "subject"), Guid.NewGuid(), Guid.NewGuid(), clock.GetUtcNow().AddMinutes(3),
            _ => Task.FromResult(true), lifetime.Token, new("chatbi", "1.0.0", Guid.NewGuid(), Guid.NewGuid().ToString("N")));
        string[] aliases = ["production"];
        using var lease = registry.Register(caller, aliases)!;
        aliases[0] = "secret";
        Check(registry.Resolve(lease.Capability, "secret") is null && registry.Resolve(lease.Capability, "production")?.Source == caller.Source,
            "Dataset grants must be defensive exact copies of server configuration and keep captured source.");
        using var modelLease = models.Register(caller, ["production"])!;
        Check(registry.Resolve(modelLease.Capability, "production") is null && models.Resolve(lease.Capability, "production") is null,
            "Equal logical aliases must never make model and data capabilities interchangeable.");
        Check(registry.Register(caller, ["production"]) is null, "Connector registry capacity must remain bounded.");
        clock.Utc = clock.Utc.AddSeconds(31);
        Check(registry.Resolve(lease.Capability, "production") is not null, "Accepted calls can outlive the business JWT admission window.");
        var resolved = registry.Resolve(lease.Capability, "production")!;
        lifetime.Cancel();
        Check(resolved.Lifetime.IsCancellationRequested && registry.Resolve(lease.Capability, "production") is null,
            "Parent cancellation must immediately revoke outstanding holders and lookups.");
        using var replacement = registry.Register(caller with { Lifetime = default }, ["production"]);
        Check(replacement is not null, "Revocation must release capacity.");
        clock.Utc = clock.Utc.AddMinutes(5);
        Check(registry.Resolve(replacement!.Capability, "production") is null, "Expiry must revoke data capabilities.");
        var operation = new GatewayOperation("chatbi-service", "1.0.0", "POST", "/ask", "chatbi:use");
        Check(operation.ConnectorDatasetIds.Length == 0, "Ordinary gateway operations must not delegate datasets by default.");
        foreach (var ids in new string[][] { ["production", "production"], ["UPPER"], ["../data"], new string[101] })
        {
            try { new GatewayOptions { Enabled = true, Operations = [operation with { ConnectorDatasetIds = ids }] }.Validate(); }
            catch (InvalidOperationException) { continue; }
            throw new InvalidOperationException("Invalid dataset allowlists must fail deployment validation.");
        }
    }

    private static async Task HttpBoundary()
    {
        var state = new State(); var origin = Origin();
        await using var app = PlatformApplication.Build(["--environment", "Development", "--urls", origin], builder => {
            builder.Logging.ClearProviders(); builder.Configuration.AddInMemoryCollection(Settings());
            builder.Services.AddSingleton<IAuthorizationStore>(state); builder.Services.AddSingleton<IConnectorStore>(state);
            builder.Services.AddSingleton<IConnectorClient>(state);
        });
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new(origin), Timeout = TimeSpan.FromSeconds(8) };
            var registry = app.Services.GetRequiredService<ConnectorDelegationRegistry>();
            using var parent = new CancellationTokenSource();
            var caller = state.Caller(parent.Token);
            using var lease = registry.Register(caller, ["production", "private"] )!;
            const string metadata = "/internal/connectors/datasets";
            const string query = "/internal/connectors/manufacturing/datasets/production/query";
            const string check = "/internal/connectors/manufacturing/datasets/production/check";
            using (var response = await Send(http, "GET", metadata, null)) await Status(response, 401);
            using (var response = await Send(http, "GET", metadata, "a.b.c")) await Status(response, 401);
            using var model = app.Services.GetRequiredService<ModelDelegationRegistry>().Register(caller, ["production"])!;
            using (var response = await Send(http, "GET", metadata, model.Capability)) await Status(response, 403);
            using (var response = await Send(http, "GET", metadata, lease.Capability))
            {
                using var body = JsonDocument.Parse(await Status(response, 200));
                var items = body.RootElement.GetProperty("items");
                Check(items.GetArrayLength() == 1 && items[0].GetProperty("datasetId").GetString() == "production"
                    && items[0].GetProperty("maxRows").GetInt32() == 10, "Metadata must intersect deployment and current query grants plus tenant limits.");
                Check(items[0].EnumerateObject().Select(x => x.Name).ToHashSet().SetEquals(["sourceId", "datasetId", "displayName", "columns", "maxRows"])
                    && !body.RootElement.GetRawText().Contains("physical", StringComparison.Ordinal), "Metadata must expose only logical query fields.");
            }
            using (var response = await Send(http, "POST", query, lease.Capability, new { columns = new[] { "line" }, limit = 2 }))
            {
                using var body = JsonDocument.Parse(await Status(response, 200));
                Check(body.RootElement.GetProperty("authorizationVersion").GetInt64() == state.Version
                    && body.RootElement.GetProperty("configurationRevision").GetInt64() == state.Revision,
                    "Query guards must identify the exact snapshots checked before result delivery.");
                Check(state.Calls.Values.Single().Source == caller.Source && state.Calls.Values.Single().UserId == caller.UserId,
                    "Delegated query audit must retain captured application/run identity.");
            }
            var calls = state.ClientCalls;
            state.ParentChecks = 0;
            using (var response = await Send(http, "POST", check, lease.Capability, new { authorizationVersion = state.Version, configurationRevision = state.Revision }))
            { using var body = JsonDocument.Parse(await Status(response, 200)); Check(body.RootElement.GetProperty("valid").GetBoolean(), "Current result guards must validate."); }
            Check(state.ParentChecks == 2, "A result guard must check its parent exactly at the boundary entry and exit, without duplicate inner checks.");
            Check(state.ClientCalls == calls, "Guard validation must never query business data.");
            using (var lateParent = registry.Register(state.Caller(default), ["production"]))
            {
                state.ParentChecks = 0; state.RevokeParentDuringConfigurationRead = true;
                using (var response = await Send(http, "POST", check, lateParent!.Capability,
                    new { authorizationVersion = state.Version, configurationRevision = state.Revision })) await Status(response, 403);
                Check(state.ParentChecks == 2 && registry.Resolve(lateParent.Capability, "production") is null,
                    "The exit check must revoke a parent that changed while result guards were being read.");
                Check(state.ClientCalls == calls, "A parent revoked during guard reads must not execute source data.");
                state.ParentAlive = true;
            }
            using (var response = await Send(http, "POST", check, lease.Capability, new { authorizationVersion = state.Version - 1, configurationRevision = state.Revision })) await Status(response, 403);
            using (var response = await Send(http, "POST", check, lease.Capability, new { authorizationVersion = state.Version, configurationRevision = state.Revision - 1 })) await Status(response, 403);
            using (var response = await Send(http, "POST", check, lease.Capability, new { authorizationVersion = state.Version, configurationRevision = state.Revision, tenantId = state.Tenant })) await Status(response, 400);
            using (var response = await Send(http, "POST", query.Replace("manufacturing", "other", StringComparison.Ordinal), lease.Capability, new { columns = new[] { "line" } })) await Status(response, 403);
            state.CanRead = false; state.Version++;
            using (var response = await Send(http, "GET", metadata, lease.Capability))
            { using var body = JsonDocument.Parse(await Status(response, 200)); Check(body.RootElement.GetProperty("items").GetArrayLength() == 0, "No current query rights means no metadata disclosure."); }
            using (var response = await Send(http, "POST", query, lease.Capability, new { columns = new[] { "line" } })) await Status(response, 403);
            state.CanRead = true; state.Version++;
            state.Enabled = false; state.Revision++;
            using (var response = await Send(http, "GET", metadata, lease.Capability))
            { using var body = JsonDocument.Parse(await Status(response, 200)); Check(body.RootElement.GetProperty("items").GetArrayLength() == 0, "Disabled tenant sources must disappear from delegated metadata."); }
            using (var response = await Send(http, "POST", check, lease.Capability, new { authorizationVersion = state.Version, configurationRevision = state.Revision })) await Status(response, 403);
            state.Enabled = true; state.Revision++;
            using (var unavailable = registry.Register(caller with { CheckAsync = _ => throw new GatewayStoreUnavailableException() }, ["production"]))
                using (var response = await Send(http, "GET", metadata, unavailable!.Capability)) await Status(response, 503);
            state.NextClient = new Gate();
            var pending = Send(http, "POST", query, lease.Capability, new { columns = new[] { "line" } });
            await state.NextClient.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            parent.Cancel();
            await state.NextClient.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using (var response = await pending) await Status(response, 403);
            Check(state.Calls.Values.Any(x => x.Outcome == "revoked"), "Parent cancellation must be audited as revocation.");
            using (var response = await Send(http, "GET", metadata, lease.Capability)) await Status(response, 403);
        }
        finally { await app.StopAsync(); }
    }

    private sealed class State : IAuthorizationStore, IConnectorStore, IConnectorClient
    {
        public Guid Tenant { get; } = Guid.NewGuid(); public Guid User { get; } = Guid.NewGuid(); public Guid Factory { get; } = Guid.NewGuid();
        public long Version = 7; public long Revision = 3; public bool CanRead = true, Enabled = true; public int ClientCalls; public Gate? NextClient;
        public int ParentChecks; public bool ParentAlive = true, RevokeParentDuringConfigurationRead;
        public ConcurrentDictionary<Guid, ConnectorCallRecord> Calls { get; } = new();
        public ModelCaller Caller(CancellationToken token) => new(new("https://issuer.example", "fixture"), Tenant, User, DateTimeOffset.UtcNow.AddMinutes(2),
            _ => { Interlocked.Increment(ref ParentChecks); return Task.FromResult(ParentAlive); }, token,
            new("chatbi", "1.0.0", Guid.NewGuid(), Guid.NewGuid().ToString("N")));
        public Task<IReadOnlyList<TenantMembership>> ListTenantsAsync(SubjectIdentity subject, CancellationToken token = default) => Task.FromResult<IReadOnlyList<TenantMembership>>([]);
        public Task<AccessSnapshot?> ReadAccessAsync(SubjectIdentity subject, Guid tenantId, CancellationToken token = default) => Task.FromResult<AccessSnapshot?>(new(User, Tenant, Version,
            CanRead ? [new("datasource:read", "datasource", "manufacturing", false, new HashSet<Guid> { Factory }),
                new("dataset:read", "dataset", "production", false, new HashSet<Guid> { Factory }),
                new("tool:invoke", "tool", "connector.query", false, new HashSet<Guid> { Factory })] : [], new HashSet<Guid> { Factory }));
        public Task<TenantConnectorConfiguration?> ReadConfigurationAsync(Guid tenantId, string sourceId, CancellationToken token)
        {
            if (RevokeParentDuringConfigurationRead) { RevokeParentDuringConfigurationRead = false; ParentAlive = false; }
            return Task.FromResult<TenantConnectorConfiguration?>(new(Tenant, sourceId, Enabled, 10, 5, Revision));
        }
        public Task<TenantConnectorConfiguration?> SaveConfigurationAsync(Guid tenantId, string sourceId, bool enabled, int maxRows, int timeoutSeconds,
            long expectedRevision, Guid actorUserId, string requestId, CancellationToken token, Func<CancellationToken, Task<bool>>? currentAccess = null) => throw new NotSupportedException();
        public Task<Guid> BeginCallAsync(Guid tenantId, Guid userId, string sourceId, string? datasetId, string operation, string requestId, CancellationToken token, ModelCallSource? source = null)
        {
            var id = Guid.NewGuid(); Calls[id] = new(id, tenantId, userId, sourceId, datasetId, operation, requestId, "running", null, 0, DateTimeOffset.UtcNow, source); return Task.FromResult(id);
        }
        public Task CompleteCallAsync(Guid callId, string outcome, int? rowCount, long elapsedMilliseconds, CancellationToken token)
        { Calls[callId] = Calls[callId] with { Outcome = outcome, RowCount = rowCount, ElapsedMilliseconds = elapsedMilliseconds }; return Task.CompletedTask; }
        public Task<IReadOnlyList<ConnectorCallRecord>> ListCallsAsync(Guid tenantId, string sourceId, CancellationToken token) => Task.FromResult<IReadOnlyList<ConnectorCallRecord>>(Calls.Values.ToArray());
        public Task TestAsync(ConnectorSource source, IReadOnlyList<ConnectorDataset> datasets, int timeoutSeconds, CancellationToken token) => Task.CompletedTask;
        public async Task<ConnectorResult> QueryAsync(ConnectorSource source, ConnectorDataset dataset, ConnectorQuery query, Guid tenantId, IReadOnlySet<Guid> organizations, int timeoutSeconds, CancellationToken token)
        {
            ClientCalls++;
            if (NextClient is { } gate)
            {
                gate.Entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { gate.Cancelled.TrySetResult(); throw; }
            }
            return new(["line"], [["fixture-line"]]);
        }
    }
    private sealed class Gate { public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    private sealed class Clock : TimeProvider { public DateTimeOffset Utc = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Utc; }
    private static Dictionary<string, string?> Settings()
    {
        var values = new Dictionary<string, string?> {
            ["Connectors:Enabled"] = "true", ["Connectors:Sources:manufacturing:Enabled"] = "true",
            ["Connectors:Sources:manufacturing:DisplayName"] = "Manufacturing", ["Connectors:Sources:manufacturing:ConnectionEnvironmentVariable"] = "UNUSED_CONNECTOR_FIXTURE"
        };
        foreach (var id in new[] { "production", "private" })
        {
            var prefix = "Connectors:Datasets:" + id + ":";
            foreach (var (key, value) in new Dictionary<string, string> { ["SourceId"] = "manufacturing", ["DisplayName"] = id, ["Schema"] = "physical_schema", ["Table"] = "physical_table", ["Columns:line:Column"] = "physical_line" }) values[prefix + key] = value;
        }
        return values;
    }
    private static async Task<HttpResponseMessage> Send(HttpClient http, string method, string path, string? capability, object? body = null)
    {
        using var request = new HttpRequestMessage(new(method), path);
        if (capability is not null) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + capability);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await http.SendAsync(request);
    }
    private static async Task<string> Status(HttpResponseMessage response, int expected)
    { var body = await response.Content.ReadAsStringAsync(); Check((int)response.StatusCode == expected, $"Expected {expected}, got {(int)response.StatusCode}: {body}"); return body; }
    private static string Origin() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return $"http://127.0.0.1:{port}"; }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
