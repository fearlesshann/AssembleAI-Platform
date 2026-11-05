using System.Text.Json;
using System.Text.RegularExpressions;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Identity;
using AssembleAI.Platform.Identity.Sessions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

namespace AssembleAI.Platform.Audit;

public static class AuditEndpoints
{
    private static readonly object SessionKey=new(); private static readonly object TenantKey=new();
    public static WebApplicationBuilder AddAuditConsole(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<AuditCursor>(); builder.Services.AddSingleton<PostgresAuditStore>(); return builder;
    }
    public static WebApplication MapAuditConsole(this WebApplication app)
    {
        var group=app.MapGroup("/api/platform/v1/audit").RequireAuthorization(IdentityEndpoints.IdentityPolicy);
        group.AddEndpointFilter(Boundary);
        group.MapGet("/workspace",async(HttpContext context,PostgresAuditStore store)=>{
            if(context.Request.Query.Any(p=>p.Key!="scope"||p.Value.Count!=1)) throw new AuditRequestException(400);
            return Results.Ok(await store.Workspace(Session(context),Tenant(context),context.RequestAborted));
        });
        group.MapGet("/events",async(HttpContext context,PostgresAuditStore store)=>Results.Ok(await store.Events(Session(context),Tenant(context),AuditQuery.Parse(context.Request.Query),null,null,context.RequestAborted)));
        group.MapGet("/events/{source}/{id}",async(HttpContext context,string source,string id,PostgresAuditStore store)=>{
            if(!AuditQuery.Sources.Contains(source)||!Regex.IsMatch(id,"\\A[a-f0-9]{32}\\z")) throw new AuditRequestException(400);
            var result=await store.Events(Session(context),Tenant(context),AuditQuery.Parse(context.Request.Query),source,id,context.RequestAborted);
            return result.Items.Count==1?Results.Ok(result.Items[0]):Problem(404);
        });
        group.MapPost("/export",async(HttpContext context,PostgresAuditStore store,IAntiforgery antiforgery,BffOptions settings)=>{
            if(!await BffEndpoints.IsVerifiedRequest(context,antiforgery,settings)) throw new AuditRequestException(403);
            if(context.Request.ContentLength>1024||context.Request.Headers.ContentEncoding.Count!=0||!context.Request.HasJsonContentType()) throw new AuditRequestException(400);
            using var content=new MemoryStream(); var buffer=new byte[1024]; int count;
            while((count=await context.Request.Body.ReadAsync(buffer,context.RequestAborted))>0) {
                if(content.Length+count>1024) throw new AuditRequestException(400); content.Write(buffer,0,count);
            }
            using var body=JsonDocument.Parse(content.ToArray());
            if(body.RootElement.ValueKind!=JsonValueKind.Object||body.RootElement.EnumerateObject().Any()) throw new AuditRequestException(400);
            return Results.Ok(await store.Export(Session(context),Tenant(context),AuditQuery.Parse(context.Request.Query,true),context.RequestAborted));
        });
        return app;
    }
    private static LiveBffSession Session(HttpContext context)=>(LiveBffSession)context.Items[SessionKey]!;
    private static Guid? Tenant(HttpContext context)=>context.Items[TenantKey] as Guid?;
    private static async ValueTask<object?> Boundary(EndpointFilterInvocationContext invocation,EndpointFilterDelegate next)
    {
        var context=invocation.HttpContext; context.Response.Headers.CacheControl="no-store"; context.Response.Headers.XContentTypeOptions="nosniff";
        var disconnected=context.RequestAborted; using var budget=CancellationTokenSource.CreateLinkedTokenSource(disconnected); budget.CancelAfter(TimeSpan.FromSeconds(6));
        try {
            if(context.Request.QueryString.Value is {Length:>12000}) throw new AuditRequestException(400);
            var scope=AuditQuery.ParseScope(context.Request.Query);
            Guid? tenant=null;
            if(scope=="tenant") {
                var authenticated=await context.AuthenticateAsync(BffAuthentication.CookieScheme);
                tenant=authenticated.Properties is null?null:TenantSession.ReadSelectedTenant(authenticated.Properties);
                if(tenant is null) throw new AuditRequestException(403);
            }
            var session=tenant is null?await LiveBffSession.CaptureIdentityAsync(context,budget.Token):await LiveBffSession.CaptureAsync(context,tenant.Value,budget.Token);
            if(session is null) throw new AuditRequestException(403);
            context.Items[SessionKey]=session; context.Items[TenantKey]=tenant; context.RequestAborted=budget.Token;
            var result=await next(invocation);
            return await session.CheckAsync(budget.Token)?result:Problem(403);
        }
        catch(AuditRequestException e) { return Problem(e.Status); }
        catch(JsonException) { return Problem(400); }
        catch(Exception e) when(e is BffSessionStoreException or AuthorizationStoreUnavailableException
            || e is OperationCanceledException && !disconnected.IsCancellationRequested) { return Problem(503); }
        finally { context.RequestAborted=disconnected; context.Items.Remove(SessionKey); context.Items.Remove(TenantKey); }
    }
    private static IResult Problem(int status)=>Results.Problem(statusCode:status,title:"The audit request could not complete.",extensions:new Dictionary<string,object?>{
        ["code"]=status switch {400=>"invalid_audit_request",403=>"audit_access_denied",404=>"audit_event_not_found",_=>"audit_unavailable"}});
}
