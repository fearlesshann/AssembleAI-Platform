using System.Diagnostics;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Identity;
using AssembleAI.Platform.Identity.Sessions;
using AssembleAI.Platform.Gateway;
using AssembleAI.Platform.Models;

namespace AssembleAI.Platform.Connectors;

public sealed class ConnectorExecutor(ConnectorCatalog catalog, IAuthorizationStore authorization,
    IConnectorStore store, IConnectorClient client, TimeProvider clock)
{
    public Task<IResult> RunAsync(Guid tenantId, string sourceId, string? datasetId, ConnectorQuery? query,
        LiveBffSession session, AccessSnapshot access, TenantConnectorConfiguration configuration, CancellationToken token,
        CancellationToken disconnected) => RunCore(sourceId,datasetId,query,
            new(session.Subject,tenantId,access.UserId,session.ExpiresAt,session.CheckAsync,disconnected),
            access,configuration,token,disconnected,includeGuards:false);

    public Task<IResult> RunAsync(string sourceId, string datasetId, ConnectorQuery query, ModelCaller caller,
        AccessSnapshot access, TenantConnectorConfiguration configuration, CancellationToken token, CancellationToken disconnected) =>
        RunCore(sourceId,datasetId,query,caller,access,configuration,token,disconnected,includeGuards:true);

    private async Task<IResult> RunCore(string sourceId, string? datasetId, ConnectorQuery? query, ModelCaller caller,
        AccessSnapshot access, TenantConnectorConfiguration configuration, CancellationToken token, CancellationToken disconnected,
        bool includeGuards)
    {
        var tenantId=caller.TenantId;
        if (tenantId==Guid.Empty || caller.UserId==Guid.Empty || access.TenantId!=tenantId || access.UserId!=caller.UserId
            || configuration.TenantId!=tenantId || configuration.SourceId!=sourceId || caller.Lifetime.IsCancellationRequested
            || caller.ExpiresAt<=clock.GetUtcNow()) return ConnectorEndpoints.Problem(403,"connector_access_denied");
        var source = catalog.FindSource(sourceId);
        var dataset = datasetId is null ? null : catalog.FindDataset(datasetId);
        if (source is null || !source.Enabled || !configuration.Enabled || datasetId is not null && (dataset is null || dataset.SourceId != sourceId))
            return ConnectorEndpoints.Problem(403,"connector_access_denied");
        if (query is not null && (query.Limit<1 || query.Limit>Math.Min(source.MaximumRows,configuration.MaxRows)))
            return ConnectorEndpoints.Problem(400,"invalid_connector_request");
        var organizations = datasetId is null ? new HashSet<Guid>() : ConnectorAuthorization.Organizations(access,sourceId,datasetId);
        if (datasetId is null ? !ConnectorAuthorization.CanTest(access,sourceId) : organizations.Count == 0 || query is null)
            return ConnectorEndpoints.Problem(403,"connector_access_denied");
        using var admission = catalog.Admission.TryAcquire(tenantId,access.UserId);
        if (admission is null) return ConnectorEndpoints.Problem(429,"connector_busy");
        var seconds = Math.Min(source.TimeoutSeconds,configuration.TimeoutSeconds);
        var remaining=caller.ExpiresAt-clock.GetUtcNow();
        using var expires=new CancellationTokenSource(remaining>TimeSpan.Zero?remaining:TimeSpan.Zero,clock);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token,caller.Lifetime,expires.Token);
        budget.CancelAfter(TimeSpan.FromSeconds(seconds));
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        var stopwatch = Stopwatch.StartNew();
        var requestId = Guid.NewGuid().ToString("N");
        Guid? callId = null; var outcome = "failed"; int? rows = null;
        var auditDone = false; var revoked = false; var dependencyFailed = false;
        Task watcher = Task.CompletedTask;

        async Task<bool> Current(CancellationToken ct)
        {
            using var check=CancellationTokenSource.CreateLinkedTokenSource(ct);
            check.CancelAfter(TimeSpan.FromSeconds(3));
            if (caller.Lifetime.IsCancellationRequested || caller.ExpiresAt<=clock.GetUtcNow() || !await caller.CheckAsync(check.Token)) return false;
            var fresh = await authorization.ReadAccessAsync(caller.Subject,tenantId,check.Token);
            if (fresh is null || fresh.UserId != access.UserId || fresh.TenantId != tenantId
                || fresh.AuthorizationVersion != access.AuthorizationVersion) return false;
            if (datasetId is null ? !ConnectorAuthorization.CanTest(fresh,sourceId)
                : !organizations.SetEquals(ConnectorAuthorization.Organizations(fresh,sourceId,datasetId))) return false;
            var config = await store.ReadConfigurationAsync(tenantId,sourceId,check.Token);
            return config is { Enabled:true } && config.TenantId==tenantId && config.SourceId==sourceId && config.Revision == configuration.Revision;
        }
        async Task Watch()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(500,stopping.Token);
                    using var check = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                    check.CancelAfter(TimeSpan.FromSeconds(3));
                    if (await Current(check.Token)) continue;
                    revoked = true; budget.Cancel(); return;
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (Exception error) when (Unavailable(error) || error is OperationCanceledException)
            { dependencyFailed = true; budget.Cancel(); }
        }
        try
        {
            if (!await Current(budget.Token)) return ConnectorEndpoints.Problem(403,"connector_access_denied");
            callId = await store.BeginCallAsync(tenantId,access.UserId,sourceId,datasetId,datasetId is null ? "test" : "query",requestId,budget.Token,caller.Source);
            budget.Token.ThrowIfCancellationRequested();
            if (!await Current(budget.Token)) { outcome="revoked"; return ConnectorEndpoints.Problem(403,"connector_access_denied"); }
            watcher = Watch();
            ConnectorResult? result = null;
            if (dataset is null)
                await client.TestAsync(source,catalog.Options.Datasets.Values.Where(d => d.SourceId == sourceId).ToArray(),seconds,budget.Token);
            else
            {
                result = await client.QueryAsync(source,dataset,query!,tenantId,organizations,seconds,budget.Token);
                rows = result.Rows.Count;
            }
            budget.Token.ThrowIfCancellationRequested();
            if (!await Current(budget.Token)) { outcome="revoked"; return ConnectorEndpoints.Problem(403,"connector_access_denied"); }
            outcome="completed";
            await store.CompleteCallAsync(callId.Value,outcome,rows,stopwatch.ElapsedMilliseconds,budget.Token);
            auditDone=true;
            if (!await Current(budget.Token)) return ConnectorEndpoints.Problem(403,"connector_access_denied");
            budget.Token.ThrowIfCancellationRequested();
            return result is null ? Results.Ok(new {requestId,status="ready"}) : includeGuards
                ? Results.Ok(new {requestId,result.Columns,result.Rows,authorizationVersion=access.AuthorizationVersion,configurationRevision=configuration.Revision})
                : Results.Ok(new {requestId,result.Columns,result.Rows});
        }
        catch (ConnectorLimitException) { outcome="failed"; return ConnectorEndpoints.Problem(413,"connector_result_limit"); }
        catch (OperationCanceledException)
        {
            var callerRevoked=revoked||expires.IsCancellationRequested||caller.Lifetime.IsCancellationRequested&&!disconnected.IsCancellationRequested;
            outcome=callerRevoked ? "revoked" : disconnected.IsCancellationRequested ? "cancelled" : dependencyFailed ? "failed" : "timeout";
            return ConnectorEndpoints.Problem(callerRevoked ? 403 : dependencyFailed ? 503 : 504,
                callerRevoked ? "connector_access_denied" : dependencyFailed ? "connectors_unavailable" : "connector_timeout");
        }
        catch (Exception error) when (Unavailable(error))
        { outcome="upstream_error"; return ConnectorEndpoints.Problem(503,"connectors_unavailable"); }
        finally
        {
            stopping.Cancel(); await watcher;
            if (callId.HasValue && !auditDone)
            {
                using var auditBudget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await store.CompleteCallAsync(callId.Value,outcome,outcome=="completed"?rows:null,stopwatch.ElapsedMilliseconds,auditBudget.Token); }
                catch (Exception error) when (error is ConnectorUnavailableException or OperationCanceledException) { }
            }
        }
    }
    internal static bool Unavailable(Exception error)=>error is ConnectorUnavailableException or AuthorizationStoreUnavailableException
        or BffSessionStoreException or GatewayStoreUnavailableException;
}
