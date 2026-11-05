using System.Diagnostics;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Gateway;
using AssembleAI.Platform.Models;

namespace AssembleAI.Platform.Knowledge;

public sealed class KnowledgeSearchAdmission(TimeProvider clock)
{
    public GatewayAdmission Admission { get; }=new(clock,2,32,60);
}

public static class KnowledgeDelegationEndpoints
{
    private static readonly object CallerKey=new(),AuditKey=new();
    public static WebApplication MapKnowledgeDelegationEndpoints(this WebApplication app)
    {
        var routes=app.MapGroup("/internal/knowledge"); routes.AddEndpointFilter(Boundary);
        routes.MapPost("/search",Search); routes.MapPost("/check",Check);
        return app;
    }
    private static async ValueTask<object?> Boundary(EndpointFilterInvocationContext invocation,EndpointFilterDelegate next)
    {
        var context=invocation.HttpContext;
        context.Response.Headers.CacheControl="no-store"; context.Response.Headers.XContentTypeOptions="nosniff";
        var options=context.RequestServices.GetRequiredService<KnowledgeOptions>();
        if(!options.Enabled || !options.IndexEnabled) return Problem(503,"knowledge_unavailable");
        if(context.Request.QueryString.HasValue) return Problem(400,"invalid_knowledge_request");
        var headers=context.Request.Headers.Authorization;
        if(headers.Count!=1 || headers[0] is not {Length:71} bearer || !bearer.StartsWith("Bearer ",StringComparison.Ordinal))
            return Problem(401,"knowledge_delegation_required");
        var caller=context.RequestServices.GetRequiredService<KnowledgeDelegationRegistry>().Resolve(bearer[7..]);
        if(caller is null) return Problem(403,"knowledge_access_denied");
        var clock=context.RequestServices.GetRequiredService<TimeProvider>(); var left=caller.ExpiresAt-clock.GetUtcNow();
        if(left<=TimeSpan.Zero) return Problem(403,"knowledge_access_denied");
        var disconnected=context.RequestAborted;
        using var expiry=new CancellationTokenSource(left,clock);
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(disconnected,caller.Lifetime,expiry.Token);
        budget.CancelAfter(TimeSpan.FromSeconds(context.Request.Path.Value?.EndsWith("/search",StringComparison.Ordinal)==true?30:3));
        context.RequestAborted=budget.Token; context.Items[CallerKey]=caller;
        using var admission=context.RequestServices.GetRequiredService<KnowledgeSearchAdmission>().Admission.TryAcquire(caller.TenantId,caller.UserId);
        var outcome="failed";
        try
        {
            if(admission is null) return Problem(429,"knowledge_busy");
            using(var check=CancellationTokenSource.CreateLinkedTokenSource(budget.Token))
            { check.CancelAfter(TimeSpan.FromSeconds(3)); if(!await caller.CheckAsync(check.Token)) return Problem(403,"knowledge_access_denied"); }
            var result=await next(invocation);
            using(var check=CancellationTokenSource.CreateLinkedTokenSource(budget.Token))
            {
                check.CancelAfter(TimeSpan.FromSeconds(3));
                if(!await caller.CheckAsync(check.Token)) { outcome="revoked"; return Problem(403,"knowledge_access_denied"); }
            }
            budget.Token.ThrowIfCancellationRequested();
            if(context.Items[AuditKey] is SearchAudit audit)
            {
                using var finish=CancellationTokenSource.CreateLinkedTokenSource(budget.Token); finish.CancelAfter(TimeSpan.FromSeconds(3));
                await context.RequestServices.GetRequiredService<IKnowledgeSearchStore>().CompleteSearchAsync(audit.CallId,audit.Outcome,audit.Count,audit.Timer.ElapsedMilliseconds,finish.Token);
                audit.Finished=true;
            }
            return result;
        }
        catch(Exception error) when(KnowledgeEndpoints.Unavailable(error) || error is KnowledgeEmbeddingException)
        { return Problem(503,"knowledge_unavailable"); }
        catch(OperationCanceledException) when(!disconnected.IsCancellationRequested)
        {
            outcome=caller.Lifetime.IsCancellationRequested || expiry.IsCancellationRequested?"revoked":"timeout";
            return Problem(outcome=="revoked"?403:504,outcome=="revoked"?"knowledge_access_denied":"knowledge_timeout");
        }
        catch(JsonException) { return Problem(400,"invalid_knowledge_request"); }
        finally
        {
            if(context.Items[AuditKey] is SearchAudit {Finished:false} audit)
            {
                using var finish=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await context.RequestServices.GetRequiredService<IKnowledgeSearchStore>().CompleteSearchAsync(audit.CallId,disconnected.IsCancellationRequested?"cancelled":outcome,0,audit.Timer.ElapsedMilliseconds,finish.Token); }
                catch(Exception error) when(KnowledgeEndpoints.Unavailable(error) || error is OperationCanceledException)
                { context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("KnowledgeAudit").LogError("Knowledge completion for {RequestId} could not persist.",audit.RequestId); }
            }
            context.RequestAborted=disconnected; context.Items.Remove(CallerKey); context.Items.Remove(AuditKey);
        }
    }
    private static ModelCaller Caller(HttpContext context)=>(ModelCaller)context.Items[CallerKey]!;
    private static bool Identity(AccessSnapshot? access,ModelCaller caller)=>access is not null && access.UserId==caller.UserId
        && access.TenantId==caller.TenantId && access.AuthorizationVersion>0;
    private static bool HasScope(AccessSnapshot access)=>access.Grants.Any(g=>g.Permission is "knowledge:read" or "knowledge:manage"
        && (g.ResourceType=="knowledge" && g.ResourceId=="documents" || g.ResourceType=="document" && Guid.TryParseExact(g.ResourceId,"N",out var id) && id!=Guid.Empty)
        && (g.TenantWide || g.OrganizationIds.Any(access.EnabledOrganizationIds.Contains)));
    private static async Task<IResult> Search(HttpContext context,IAuthorizationStore authorization,IKnowledgeSearchStore store,KnowledgeEmbeddingClient embeddings)
    {
        using var json=await Read(context.Request,context.RequestAborted);
        if(json is null || !KnowledgeEmbeddingClient.Fields(json.RootElement,"question","limit")) return Problem(400,"invalid_knowledge_request");
        string? question;
        try { question=json.RootElement.GetProperty("question").GetString(); }
        catch(InvalidOperationException) { return Problem(400,"invalid_knowledge_request"); }
        if(!KnowledgeEmbeddingClient.ValidQuestion(question) || json.RootElement.GetProperty("limit").ValueKind!=JsonValueKind.Number
            || !json.RootElement.GetProperty("limit").TryGetInt32(out var limit) || limit is <1 or >5) return Problem(400,"invalid_knowledge_request");
        var caller=Caller(context); var token=context.RequestAborted;
        var access=await authorization.ReadAccessAsync(caller.Subject,caller.TenantId,token);
        if(!Identity(access,caller) || !HasScope(access!)) return Problem(403,"knowledge_access_denied");
        var requestId=Guid.NewGuid().ToString("N"); context.Response.Headers["X-Request-ID"]=requestId;
        var audit=new SearchAudit(await store.BeginSearchAsync(caller,requestId,access!.AuthorizationVersion,token),requestId);
        context.Items[AuditKey]=audit;
        var vector=await embeddings.EmbedQueryAsync(question!,token);
        var fresh=await authorization.ReadAccessAsync(caller.Subject,caller.TenantId,token);
        if(!Identity(fresh,caller) || fresh!.AuthorizationVersion!=access.AuthorizationVersion)
        { audit.Outcome="revoked"; return Problem(403,"knowledge_access_denied"); }
        var hits=await store.SearchAsync(fresh,vector,limit,token);
        var documents=hits.Select(h=>new KnowledgeDocumentReference(h.DocumentId,h.DocumentRevision)).Distinct().ToArray();
        if(!await store.CheckDocumentsAsync(fresh,documents,token))
        { audit.Outcome="revoked"; return Problem(403,"knowledge_access_denied"); }
        audit.Outcome="completed"; audit.Count=hits.Count;
        return Results.Ok(new {requestId,authorizationVersion=fresh.AuthorizationVersion,profile=KnowledgeIndexProfile.Id,hits});
    }
    private static async Task<IResult> Check(HttpContext context,IAuthorizationStore authorization,IKnowledgeSearchStore store)
    {
        using var json=await Read(context.Request,context.RequestAborted);
        if(json is null || !KnowledgeEmbeddingClient.Fields(json.RootElement,"authorizationVersion","profile","documents")) return Problem(400,"invalid_knowledge_request");
        var root=json.RootElement;
        if(root.GetProperty("authorizationVersion").ValueKind!=JsonValueKind.Number || !root.GetProperty("authorizationVersion").TryGetInt64(out var version) || version<1
            || !Profile(root.GetProperty("profile"))
            || root.GetProperty("documents") is not {ValueKind:JsonValueKind.Array} values || values.GetArrayLength()>5) return Problem(400,"invalid_knowledge_request");
        var documents=new List<KnowledgeDocumentReference>();
        foreach(var value in values.EnumerateArray())
        {
            if(!KnowledgeEmbeddingClient.Fields(value,"documentId","revision") || value.GetProperty("documentId").ValueKind!=JsonValueKind.String
                || !value.GetProperty("documentId").TryGetGuid(out var id) || id==Guid.Empty || value.GetProperty("revision").ValueKind!=JsonValueKind.Number
                || !value.GetProperty("revision").TryGetInt64(out var revision) || revision<1 || documents.Any(d=>d.DocumentId==id)) return Problem(400,"invalid_knowledge_request");
            documents.Add(new(id,revision));
        }
        var caller=Caller(context); var token=context.RequestAborted;
        var access=await authorization.ReadAccessAsync(caller.Subject,caller.TenantId,token);
        if(!Identity(access,caller) || access!.AuthorizationVersion!=version || !HasScope(access)
            || !await store.CheckDocumentsAsync(access,documents,token)) return Problem(403,"knowledge_access_denied");
        var fresh=await authorization.ReadAccessAsync(caller.Subject,caller.TenantId,token);
        return Identity(fresh,caller) && fresh!.AuthorizationVersion==version?Results.Ok(new {valid=true}):Problem(403,"knowledge_access_denied");
    }
    private static async Task<JsonDocument?> Read(HttpRequest request,CancellationToken token)
    {
        const int maximum=32*1024;
        if(!request.HasJsonContentType() || request.Headers.ContentEncoding.Count!=0 || request.ContentLength>maximum) return null;
        using var bytes=new MemoryStream(); var buffer=new byte[4096]; int count;
        while((count=await request.Body.ReadAsync(buffer,token))!=0)
        { if(bytes.Length+count>maximum) return null; bytes.Write(buffer,0,count); }
        return JsonDocument.Parse(bytes.ToArray(),new(){MaxDepth=5});
    }
    private static bool Profile(JsonElement value)
    {
        try { return value.ValueKind==JsonValueKind.String && value.GetString()==KnowledgeIndexProfile.Id; }
        catch(InvalidOperationException) { return false; }
    }
    private static IResult Problem(int status,string code)=>Results.Problem(statusCode:status,title:"Knowledge request could not be completed.",extensions:new Dictionary<string,object?>{["code"]=code});
    private sealed class SearchAudit(Guid callId,string requestId)
    {
        public Guid CallId { get; }=callId; public string RequestId { get; }=requestId;
        public Stopwatch Timer { get; }=Stopwatch.StartNew(); public string Outcome { get; set; }="failed";
        public int Count { get; set; } public bool Finished { get; set; }
    }
}
