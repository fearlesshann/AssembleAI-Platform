using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Identity;
using AssembleAI.Platform.Identity.Sessions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AssembleAI.Platform.Connectors;

public static class ConnectorEndpoints
{
    private static readonly object SessionKey=new();
    private static readonly object DisconnectKey=new();
    public static WebApplicationBuilder AddPlatformConnectors(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ConnectorCatalog>();
        builder.Services.TryAddSingleton<IConnectorStore,PostgresConnectorStore>();
        builder.Services.TryAddSingleton<IConnectorClient,PostgresConnectorClient>();
        builder.Services.AddSingleton<ConnectorExecutor>();
        builder.Services.AddSingleton<ConnectorDelegationRegistry>();
        return builder;
    }
    public static WebApplication MapConnectorEndpoints(this WebApplication app)
    {
        var routes=app.MapGroup("/api/platform/v1/tenants/{tenantId:guid}/connectors")
            .RequireAuthorization(IdentityEndpoints.IdentityPolicy);
        routes.AddEndpointFilter(Boundary);
        routes.MapGet("",List);
        routes.MapGet("/{sourceId}/configuration",ReadConfiguration);
        routes.MapPut("/{sourceId}/configuration",SaveConfiguration);
        routes.MapGet("/{sourceId}/calls",Calls);
        routes.MapPost("/{sourceId}/test",Test);
        routes.MapPost("/{sourceId}/datasets/{datasetId}/query",Query);
        app.MapConnectorDelegationEndpoints();
        return app;
    }
    private static async ValueTask<object?> Boundary(EndpointFilterInvocationContext invocation,EndpointFilterDelegate next)
    {
        var context=invocation.HttpContext;
        context.Response.Headers.CacheControl="no-store";
        context.Response.Headers.XContentTypeOptions="nosniff";
        if (context.Request.Query.Count!=0) return Problem(400,"invalid_connector_request");
        if (!context.RequestServices.GetRequiredService<ConnectorCatalog>().Options.Enabled) return Problem(503,"connectors_unavailable");
        var disconnected=context.RequestAborted;
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(disconnected);
        budget.CancelAfter(TimeSpan.FromSeconds(HttpMethods.IsPost(context.Request.Method)?30:3));
        try
        {
            if (!Guid.TryParse(context.Request.RouteValues["tenantId"]?.ToString(),out var tenant)||tenant==Guid.Empty)
                return Problem(400,"invalid_connector_request");
            using var capture=CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            capture.CancelAfter(TimeSpan.FromSeconds(3));
            var session=await LiveBffSession.CaptureAsync(context,tenant,capture.Token);
            if (session is null) return Problem(403,"connector_access_denied");
            context.Items[SessionKey]=session;
            context.Items[DisconnectKey]=disconnected;
            context.RequestAborted=budget.Token;
            var result=await next(invocation);
            if (!await session.CheckAsync(budget.Token)) return Problem(403,"connector_access_denied");
            return result;
        }
        catch (Exception error) when (error is ConnectorUnavailableException or AuthorizationStoreUnavailableException or BffSessionStoreException)
        { return Problem(503,"connectors_unavailable"); }
        catch (OperationCanceledException) when (!disconnected.IsCancellationRequested)
        { return Problem(504,"connector_timeout"); }
        catch (JsonException) { return Problem(400,"invalid_connector_request"); }
        finally { context.RequestAborted=disconnected; context.Items.Remove(SessionKey); context.Items.Remove(DisconnectKey); }
    }
    private static LiveBffSession Session(HttpContext context)=>(LiveBffSession)context.Items[SessionKey]!;
    private static Task<AccessSnapshot?> Access(HttpContext context,Guid tenant,IAuthorizationStore store)=>
        store.ReadAccessAsync(Session(context).Subject,tenant,context.RequestAborted);
    private static bool Manage(AccessSnapshot? access,string source)=>ConnectorAuthorization.Allows(access,"datasource:manage","datasource",source);
    private static bool Same(AccessSnapshot? fresh,AccessSnapshot? original)=>fresh is not null && original is not null
        && fresh.UserId==original.UserId && fresh.TenantId==original.TenantId && fresh.AuthorizationVersion==original.AuthorizationVersion;
    private static async Task<bool> Verified(HttpContext context,IAntiforgery antiforgery,BffOptions options)=>
        await BffEndpoints.IsVerifiedRequest(context,antiforgery,options);
    private static async Task<IResult> List(Guid tenantId,HttpContext context,ConnectorCatalog catalog,IAuthorizationStore authorization,IConnectorStore store)
    {
        var access=await Access(context,tenantId,authorization);
        if (access is null) return Problem(403,"connector_access_denied");
        var items=new List<object>();
        foreach (var (id,source) in catalog.Options.Sources)
        {
            var manage=Manage(access,id);
            var datasets=catalog.Options.Datasets.Where(pair=>pair.Value.SourceId==id
                && (manage||ConnectorAuthorization.Organizations(access,id,pair.Key).Count>0))
                .Select(pair=>new {datasetId=pair.Key,pair.Value.DisplayName,
                    columns=pair.Value.Columns.Select(c=>new {name=c.Key,type=c.Value.Type}).ToArray()}).ToArray();
            if (!manage&&!ConnectorAuthorization.CanTest(access,id)&&datasets.Length==0) continue;
            var configuration=await store.ReadConfigurationAsync(tenantId,id,context.RequestAborted);
            items.Add(new {sourceId=id,source.DisplayName,enabled=source.Enabled&&configuration?.Enabled==true,canManage=manage,datasets});
        }
        if (!Same(await Access(context,tenantId,authorization),access)) return Problem(403,"connector_access_denied");
        return Results.Ok(new {items});
    }
    private static async Task<IResult> ReadConfiguration(Guid tenantId,string sourceId,HttpContext context,
        ConnectorCatalog catalog,IAuthorizationStore authorization,IConnectorStore store)
    {
        if (!Manage(await Access(context,tenantId,authorization),sourceId)) return Problem(403,"connector_access_denied");
        if (catalog.FindSource(sourceId) is null) return Problem(404,"connector_not_found");
        var configuration=await store.ReadConfigurationAsync(tenantId,sourceId,context.RequestAborted);
        if (!Manage(await Access(context,tenantId,authorization),sourceId)) return Problem(403,"connector_access_denied");
        return Results.Ok(new {configuration});
    }
    private static async Task<IResult> SaveConfiguration(Guid tenantId,string sourceId,HttpContext context,
        ConnectorCatalog catalog,IAuthorizationStore authorization,IConnectorStore store,IAntiforgery antiforgery,BffOptions options)
    {
        if (!await Verified(context,antiforgery,options)) return Problem(403,"request_verification_failed");
        var access=await Access(context,tenantId,authorization);
        if (!Manage(access,sourceId)) return Problem(403,"connector_access_denied");
        var source=catalog.FindSource(sourceId);
        if (source is null) return Problem(404,"connector_not_found");
        using var document=await Read(context.Request,context.RequestAborted);
        if (document is null) return Problem(400,"invalid_connector_request");
        var body=document.RootElement;
        if (!Fields(body,"enabled","maxRows","timeoutSeconds","expectedRevision")
            || !body.TryGetProperty("enabled",out var enabled)||enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !Integer(body,"maxRows",1,source.MaximumRows,out var maxRows)
            || !Integer(body,"timeoutSeconds",1,source.TimeoutSeconds,out var timeout)
            || !body.TryGetProperty("expectedRevision",out var revision)||revision.ValueKind!=JsonValueKind.Number
            || !revision.TryGetInt64(out var expected)||expected is <0 or long.MaxValue) return Problem(400,"invalid_connector_request");
        var fresh=await Access(context,tenantId,authorization);
        if (!Same(fresh,access)||!Manage(fresh,sourceId)||!await Session(context).CheckAsync(context.RequestAborted))
            return Problem(403,"connector_access_denied");
        var configuration=await store.SaveConfigurationAsync(tenantId,sourceId,enabled.GetBoolean(),maxRows,timeout,expected,
            access!.UserId,Guid.NewGuid().ToString("N"),context.RequestAborted);
        return configuration is null ? Problem(409,"connector_configuration_conflict") : Results.Ok(new {configuration});
    }
    private static async Task<IResult> Calls(Guid tenantId,string sourceId,HttpContext context,IAuthorizationStore authorization,IConnectorStore store)
    {
        if (!Manage(await Access(context,tenantId,authorization),sourceId)) return Problem(403,"connector_access_denied");
        var items=await store.ListCallsAsync(tenantId,sourceId,context.RequestAborted);
        if (!Manage(await Access(context,tenantId,authorization),sourceId)) return Problem(403,"connector_access_denied");
        return Results.Ok(new {items});
    }
    private static Task<IResult> Test(Guid tenantId,string sourceId,HttpContext context,ConnectorCatalog catalog,
        IAuthorizationStore authorization,IConnectorStore store,ConnectorExecutor executor,IAntiforgery antiforgery,BffOptions options)=>
        Execute(tenantId,sourceId,null,context,catalog,authorization,store,executor,antiforgery,options);
    private static Task<IResult> Query(Guid tenantId,string sourceId,string datasetId,HttpContext context,ConnectorCatalog catalog,
        IAuthorizationStore authorization,IConnectorStore store,ConnectorExecutor executor,IAntiforgery antiforgery,BffOptions options)=>
        Execute(tenantId,sourceId,datasetId,context,catalog,authorization,store,executor,antiforgery,options);
    private static async Task<IResult> Execute(Guid tenantId,string sourceId,string? datasetId,HttpContext context,
        ConnectorCatalog catalog,IAuthorizationStore authorization,IConnectorStore store,ConnectorExecutor executor,IAntiforgery antiforgery,BffOptions options)
    {
        if (!await Verified(context,antiforgery,options)) return Problem(403,"request_verification_failed");
        var access=await Access(context,tenantId,authorization);
        if (access is null || (datasetId is null ? !ConnectorAuthorization.CanTest(access,sourceId)
            : ConnectorAuthorization.Organizations(access,sourceId,datasetId).Count==0)) return Problem(403,"connector_access_denied");
        var source=catalog.FindSource(sourceId); var dataset=datasetId is null ? null : catalog.FindDataset(datasetId);
        if (source is null||!source.Enabled ||datasetId is not null&&(dataset is null||dataset.SourceId!=sourceId))
            return Problem(403,"connector_access_denied");
        var configuration=await store.ReadConfigurationAsync(tenantId,sourceId,context.RequestAborted);
        if (configuration is not {Enabled:true}) return Problem(403,"connector_access_denied");
        using var document=await Read(context.Request,context.RequestAborted);
        if (document is null) return Problem(400,"invalid_connector_request");
        var query=dataset is null ? null : ConnectorQueryCompiler.Parse(document.RootElement,dataset,Math.Min(source.MaximumRows,configuration.MaxRows));
        if (dataset is null ? !Fields(document.RootElement) : query is null) return Problem(400,"invalid_connector_request");
        return await executor.RunAsync(tenantId,sourceId,datasetId,query,Session(context),access,configuration,
            context.RequestAborted,(CancellationToken)context.Items[DisconnectKey]!);
    }
    private static bool Integer(JsonElement body,string name,int minimum,int maximum,out int value)
    {
        value=0;
        return body.TryGetProperty(name,out var field)&&field.ValueKind==JsonValueKind.Number&&field.TryGetInt32(out value)&&value>=minimum&&value<=maximum;
    }
    private static bool Fields(JsonElement value,params string[] allowed)=>value.ValueKind==JsonValueKind.Object
        && value.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()==value.EnumerateObject().Count()
        && value.EnumerateObject().All(p=>allowed.Contains(p.Name,StringComparer.Ordinal));
    internal static async Task<JsonDocument?> Read(HttpRequest request,CancellationToken token)
    {
        if (request.ContentLength>16384||request.Headers.ContentEncoding.Count!=0||!request.HasJsonContentType()) return null;
        await using var buffer=new MemoryStream(); var bytes=new byte[4096]; int count;
        while ((count=await request.Body.ReadAsync(bytes,token))!=0)
        {
            if (buffer.Length+count>16384) return null;
            await buffer.WriteAsync(bytes.AsMemory(0,count),token);
        }
        try { return JsonDocument.Parse(buffer.ToArray(),new(){MaxDepth=8}); } catch (JsonException) { return null; }
    }
    public static IResult Problem(int status,string code)=>Results.Problem(statusCode:status,title:"Data request could not be completed.",
        extensions:new Dictionary<string,object?>{["code"]=code});
}
