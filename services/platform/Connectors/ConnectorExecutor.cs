using System.Diagnostics;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Identity;

namespace AssembleAI.Platform.Connectors;

public sealed class ConnectorExecutor(ConnectorCatalog catalog, IAuthorizationStore authorization,
    IConnectorStore store, IConnectorClient client)
{
    public async Task<IResult> RunAsync(Guid tenantId, string sourceId, string? datasetId, ConnectorQuery? query,
        LiveBffSession session, AccessSnapshot access, TenantConnectorConfiguration configuration, CancellationToken token,
        CancellationToken disconnected)
    {
        var source = catalog.FindSource(sourceId);
        var dataset = datasetId is null ? null : catalog.FindDataset(datasetId);
        if (source is null || !source.Enabled || !configuration.Enabled || datasetId is not null && (dataset is null || dataset.SourceId != sourceId))
            return ConnectorEndpoints.Problem(403,"connector_access_denied");
        var organizations = datasetId is null ? new HashSet<Guid>() : ConnectorAuthorization.Organizations(access,sourceId,datasetId);
        if (datasetId is null ? !ConnectorAuthorization.CanTest(access,sourceId) : organizations.Count == 0 || query is null)
            return ConnectorEndpoints.Problem(403,"connector_access_denied");
        using var admission = catalog.Admission.TryAcquire(tenantId,access.UserId);
        if (admission is null) return ConnectorEndpoints.Problem(429,"connector_busy");
        var seconds = Math.Min(source.TimeoutSeconds,configuration.TimeoutSeconds);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(seconds));
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        var stopwatch = Stopwatch.StartNew();
        var requestId = Guid.NewGuid().ToString("N");
        Guid? callId = null; var outcome = "failed"; int? rows = null;
        var auditDone = false; var revoked = false; var dependencyFailed = false;
        Task watcher = Task.CompletedTask;

        async Task<bool> Current(CancellationToken ct)
        {
            if (!await session.CheckAsync(ct)) return false;
            var fresh = await authorization.ReadAccessAsync(session.Subject,tenantId,ct);
            if (fresh is null || fresh.UserId != access.UserId || fresh.TenantId != tenantId
                || fresh.AuthorizationVersion != access.AuthorizationVersion) return false;
            if (datasetId is null ? !ConnectorAuthorization.CanTest(fresh,sourceId)
                : !organizations.SetEquals(ConnectorAuthorization.Organizations(fresh,sourceId,datasetId))) return false;
            var config = await store.ReadConfigurationAsync(tenantId,sourceId,ct);
            return config is { Enabled:true } && config.Revision == configuration.Revision;
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
            catch (Exception error) when (error is ConnectorUnavailableException or AuthorizationStoreUnavailableException
                or AssembleAI.Platform.Identity.Sessions.BffSessionStoreException or OperationCanceledException)
            { dependencyFailed = true; budget.Cancel(); }
        }
        try
        {
            if (!await Current(budget.Token)) return ConnectorEndpoints.Problem(403,"connector_access_denied");
            callId = await store.BeginCallAsync(tenantId,access.UserId,sourceId,datasetId,datasetId is null ? "test" : "query",requestId,budget.Token);
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
            return result is null ? Results.Ok(new {requestId,status="ready"}) : Results.Ok(new {requestId,result.Columns,result.Rows});
        }
        catch (ConnectorLimitException) { outcome="failed"; return ConnectorEndpoints.Problem(413,"connector_result_limit"); }
        catch (OperationCanceledException)
        {
            outcome=revoked ? "revoked" : disconnected.IsCancellationRequested ? "cancelled" : dependencyFailed ? "failed" : "timeout";
            return ConnectorEndpoints.Problem(revoked ? 403 : dependencyFailed ? 503 : 504,
                revoked ? "connector_access_denied" : dependencyFailed ? "connectors_unavailable" : "connector_timeout");
        }
        catch (Exception error) when (error is ConnectorUnavailableException or AuthorizationStoreUnavailableException
            or AssembleAI.Platform.Identity.Sessions.BffSessionStoreException)
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
}
