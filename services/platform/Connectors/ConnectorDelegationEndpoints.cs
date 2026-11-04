using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Models;

namespace AssembleAI.Platform.Connectors;

/// <summary>Service-only callbacks authenticated by an active parent invocation, never by business JWT claims.</summary>
public static class ConnectorDelegationEndpoints
{
    private static readonly object CallerKey=new(), DisconnectKey=new(), CandidatesKey=new();

    public static WebApplication MapConnectorDelegationEndpoints(this WebApplication app)
    {
        var routes=app.MapGroup("/internal/connectors");
        routes.AddEndpointFilter(Boundary);
        routes.MapGet("/datasets",Datasets);
        routes.MapPost("/{sourceId}/datasets/{datasetId}/query",Query);
        routes.MapPost("/{sourceId}/datasets/{datasetId}/check",Check);
        return app;
    }

    private static async ValueTask<object?> Boundary(EndpointFilterInvocationContext invocation,EndpointFilterDelegate next)
    {
        var context=invocation.HttpContext;
        context.Response.Headers.CacheControl="no-store";
        context.Response.Headers.XContentTypeOptions="nosniff";
        if (context.Request.QueryString.HasValue) return Problem(400,"invalid_connector_request");
        var catalog=context.RequestServices.GetRequiredService<ConnectorCatalog>();
        if (!catalog.Options.Enabled) return Problem(503,"connectors_unavailable");
        var headers=context.Request.Headers.Authorization;
        if (headers.Count!=1 || headers[0] is not {Length:71} bearer || !bearer.StartsWith("Bearer ",StringComparison.Ordinal))
            return Problem(401,"connector_delegation_required");
        var registry=context.RequestServices.GetRequiredService<ConnectorDelegationRegistry>();
        var candidates=new List<string>(); ModelCaller? caller=null;
        if (context.Request.RouteValues["datasetId"] is string datasetId)
            caller=registry.Resolve(bearer[7..],datasetId);
        else
            foreach (var id in catalog.Options.Datasets.Keys)
                if (registry.Resolve(bearer[7..],id) is { } resolved) { caller??=resolved; candidates.Add(id); }
        if (caller is null) return Problem(403,"connector_access_denied");
        var clock=context.RequestServices.GetRequiredService<TimeProvider>();
        var disconnected=context.RequestAborted;
        var remaining=caller.ExpiresAt-clock.GetUtcNow();
        if (remaining<=TimeSpan.Zero) return Problem(403,"connector_access_denied");
        using var expiry=new CancellationTokenSource(remaining,clock);
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(disconnected,caller.Lifetime,expiry.Token);
        budget.CancelAfter(TimeSpan.FromSeconds(context.Request.Path.Value?.EndsWith("/query",StringComparison.Ordinal)==true?30:3));
        context.Items[CallerKey]=caller; context.Items[DisconnectKey]=disconnected; context.Items[CandidatesKey]=candidates;
        context.RequestAborted=budget.Token;
        try
        {
            using var admissionCheck=CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            admissionCheck.CancelAfter(TimeSpan.FromSeconds(3));
            if (!await caller.CheckAsync(admissionCheck.Token)) return Problem(403,"connector_access_denied");
            var result=await next(invocation);
            using var finalCheck=CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            finalCheck.CancelAfter(TimeSpan.FromSeconds(3));
            if (!await caller.CheckAsync(finalCheck.Token)) return Problem(403,"connector_access_denied");
            budget.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception error) when (ConnectorExecutor.Unavailable(error)) { return Problem(503,"connectors_unavailable"); }
        catch (OperationCanceledException) when (!disconnected.IsCancellationRequested)
        { return Problem(caller.Lifetime.IsCancellationRequested||expiry.IsCancellationRequested?403:504,
            caller.Lifetime.IsCancellationRequested||expiry.IsCancellationRequested?"connector_access_denied":"connector_timeout"); }
        catch (JsonException) { return Problem(400,"invalid_connector_request"); }
        finally
        {
            context.RequestAborted=disconnected;
            context.Items.Remove(CallerKey); context.Items.Remove(DisconnectKey); context.Items.Remove(CandidatesKey);
        }
    }

    private static ModelCaller Caller(HttpContext context)=>(ModelCaller)context.Items[CallerKey]!;
    private static Task<AccessSnapshot?> Access(HttpContext context,IAuthorizationStore authorization)=>
        authorization.ReadAccessAsync(Caller(context).Subject,Caller(context).TenantId,context.RequestAborted);
    private static bool Identity(AccessSnapshot? access,ModelCaller caller)=>access is not null
        && access.UserId==caller.UserId && access.TenantId==caller.TenantId && access.AuthorizationVersion>0;
    private static bool Configuration(TenantConnectorConfiguration? value,ModelCaller caller,string sourceId)=>
        value is {Enabled:true} && value.TenantId==caller.TenantId && value.SourceId==sourceId;

    private static async Task<IResult> Datasets(HttpContext context,ConnectorCatalog catalog,IAuthorizationStore authorization,IConnectorStore store)
    {
        var caller=Caller(context);
        using var admission=catalog.Admission.TryAcquire(caller.TenantId,caller.UserId);
        if (admission is null) return Problem(429,"connector_busy");
        var access=await Access(context,authorization);
        if (!Identity(access,caller)) return Problem(403,"connector_access_denied");
        var configurations=new Dictionary<string,TenantConnectorConfiguration?>(StringComparer.Ordinal);
        var included=new Dictionary<string,long>(StringComparer.Ordinal);
        var items=new List<object>();
        foreach (var id in (List<string>)context.Items[CandidatesKey]!)
        {
            var dataset=catalog.FindDataset(id);
            if (dataset is null || catalog.FindSource(dataset.SourceId) is not {Enabled:true} source
                || ConnectorAuthorization.Organizations(access,dataset.SourceId,id).Count==0) continue;
            if (!configurations.TryGetValue(dataset.SourceId,out var configuration))
            {
                configuration=await store.ReadConfigurationAsync(caller.TenantId,dataset.SourceId,context.RequestAborted);
                configurations.Add(dataset.SourceId,configuration);
            }
            if (!Configuration(configuration,caller,dataset.SourceId)) continue;
            included[dataset.SourceId]=configuration!.Revision;
            items.Add(new {sourceId=dataset.SourceId,datasetId=id,dataset.DisplayName,
                columns=dataset.Columns.Select(column=>new {name=column.Key,type=column.Value.Type}).ToArray(),
                maxRows=Math.Min(source.MaximumRows,configuration.MaxRows)});
        }
        foreach (var (sourceId,revision) in included)
        {
            var current=await store.ReadConfigurationAsync(caller.TenantId,sourceId,context.RequestAborted);
            if (!Configuration(current,caller,sourceId) || current!.Revision!=revision) return Problem(403,"connector_access_denied");
        }
        var fresh=await Access(context,authorization);
        if (!Identity(fresh,caller) || fresh!.AuthorizationVersion!=access!.AuthorizationVersion)
            return Problem(403,"connector_access_denied");
        return Results.Ok(new {items});
    }

    private static async Task<IResult> Query(string sourceId,string datasetId,HttpContext context,ConnectorCatalog catalog,
        IAuthorizationStore authorization,IConnectorStore store,ConnectorExecutor executor)
    {
        var caller=Caller(context);
        var dataset=catalog.FindDataset(datasetId); var source=catalog.FindSource(sourceId);
        if (dataset is null || dataset.SourceId!=sourceId || source is not {Enabled:true}) return Problem(403,"connector_access_denied");
        var access=await Access(context,authorization);
        if (!Identity(access,caller) || ConnectorAuthorization.Organizations(access,sourceId,datasetId).Count==0)
            return Problem(403,"connector_access_denied");
        var configuration=await store.ReadConfigurationAsync(caller.TenantId,sourceId,context.RequestAborted);
        if (!Configuration(configuration,caller,sourceId)) return Problem(403,"connector_access_denied");
        using var document=await ConnectorEndpoints.Read(context.Request,context.RequestAborted);
        var query=document is null?null:ConnectorQueryCompiler.Parse(document.RootElement,dataset,Math.Min(source.MaximumRows,configuration!.MaxRows));
        if (query is null) return Problem(400,"invalid_connector_request");
        return await executor.RunAsync(sourceId,datasetId,query,caller,access!,configuration!,context.RequestAborted,
            (CancellationToken)context.Items[DisconnectKey]!);
    }

    private static async Task<IResult> Check(string sourceId,string datasetId,HttpContext context,ConnectorCatalog catalog,
        IAuthorizationStore authorization,IConnectorStore store)
    {
        var caller=Caller(context);
        var dataset=catalog.FindDataset(datasetId);
        if (dataset is null || dataset.SourceId!=sourceId || catalog.FindSource(sourceId) is not {Enabled:true})
            return Problem(403,"connector_access_denied");
        using var admission=catalog.Admission.TryAcquire(caller.TenantId,caller.UserId);
        if (admission is null) return Problem(429,"connector_busy");
        using var document=await ConnectorEndpoints.Read(context.Request,context.RequestAborted);
        if (document is null || document.RootElement.ValueKind!=JsonValueKind.Object) return Problem(400,"invalid_connector_request");
        var fields=document.RootElement.EnumerateObject().ToArray();
        if (fields.Length!=2 || fields.Select(field=>field.Name).Distinct(StringComparer.Ordinal).Count()!=2
            || !Positive(document.RootElement,"authorizationVersion",out var version)
            || !Positive(document.RootElement,"configurationRevision",out var revision)) return Problem(400,"invalid_connector_request");
        for (var pass=0;pass<2;pass++)
        {
            var configuration=await store.ReadConfigurationAsync(caller.TenantId,sourceId,context.RequestAborted);
            if (!Configuration(configuration,caller,sourceId) || configuration!.Revision!=revision) return Problem(403,"connector_access_denied");
            var access=await Access(context,authorization);
            if (!Identity(access,caller) || access!.AuthorizationVersion!=version
                || ConnectorAuthorization.Organizations(access,sourceId,datasetId).Count==0) return Problem(403,"connector_access_denied");
        }
        return Results.Ok(new {valid=true});
    }
    private static bool Positive(JsonElement body,string property,out long value)
    { value=0; return body.TryGetProperty(property,out var field) && field.ValueKind==JsonValueKind.Number && field.TryGetInt64(out value) && value>0; }
    private static IResult Problem(int status,string code)=>ConnectorEndpoints.Problem(status,code);
}
