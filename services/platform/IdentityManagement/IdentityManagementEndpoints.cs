using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Identity;
using AssembleAI.Platform.Identity.Sessions;
using AssembleAI.Platform.Models;
using AssembleAI.Platform.Connectors;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AssembleAI.Platform.IdentityManagement;

public static class IdentityManagementEndpoints
{
    private static readonly object SessionKey = new();
    private static readonly object SelectedKey = new();
    public static WebApplicationBuilder AddIdentityManagement(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton<IIdentityManagementStore, PostgresIdentityManagementStore>();
        return builder;
    }
    public static WebApplication MapIdentityManagementEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/platform/v1/identity-management").RequireAuthorization(IdentityEndpoints.IdentityPolicy);
        group.AddEndpointFilter(Boundary);
        group.MapGet("/workspace", async (HttpContext context, IIdentityManagementStore store) =>
            Result(await store.ReadWorkspaceAsync(Session(context).Subject, context.RequestAborted)));
        group.MapGet("/tenants/{tenantId:guid}", async (Guid tenantId, HttpContext context, IIdentityManagementStore store) => tenantId == Guid.Empty ? Invalid()
            : Result(await store.ReadTenantAsync(Session(context).Subject, tenantId, Selected(context), context.RequestAborted)));
        group.MapGet("/tenants/{tenantId:guid}/permission-catalog", async (Guid tenantId, HttpContext context, IIdentityManagementStore store,
            ModelGatewayCatalog models, ConnectorCatalog connectors) => {
            if (tenantId == Guid.Empty) return Invalid();
            var applications = await store.ReadGrantApplicationsAsync(Session(context).Subject, tenantId, Selected(context), context.RequestAborted);
            return applications.Error != IdentityError.None ? Result(applications)
                : Results.Ok(IdentityPermissionCatalog.Build(tenantId, applications.Value!, models.Options, connectors.Options));
        });
        group.MapPut("/users/{userId:guid}", (Guid userId, HttpContext context, IIdentityManagementStore store) =>
            Save<SaveIdentityUser>(context, userId, (value, ct) => store.SaveUserAsync(Session(context).Subject, userId, value, context.TraceIdentifier, Session(context).CheckAsync, ct)));
        group.MapPut("/tenants/{tenantId:guid}", (Guid tenantId, HttpContext context, IIdentityManagementStore store) =>
            Save<SaveIdentityTenant>(context, tenantId, (value, ct) => store.SaveTenantAsync(Session(context).Subject, tenantId, value, context.TraceIdentifier, Session(context).CheckAsync, ct)));
        group.MapPut("/tenants/{tenantId:guid}/members/{userId:guid}", (Guid tenantId, Guid userId, HttpContext context, IIdentityManagementStore store) =>
            Save<SaveIdentityMember>(context, userId, (value, ct) => store.SaveMemberAsync(Session(context).Subject, tenantId, Selected(context), userId, value, context.TraceIdentifier, Session(context).CheckAsync, ct)));
        group.MapPut("/tenants/{tenantId:guid}/organizations/{organizationId:guid}", (Guid tenantId, Guid organizationId, HttpContext context, IIdentityManagementStore store) =>
            Save<SaveIdentityOrganization>(context, organizationId, (value, ct) => store.SaveOrganizationAsync(Session(context).Subject, tenantId, Selected(context), organizationId, value, context.TraceIdentifier, Session(context).CheckAsync, ct)));
        group.MapPut("/tenants/{tenantId:guid}/roles/{roleId:guid}", (Guid tenantId, Guid roleId, HttpContext context, IIdentityManagementStore store) =>
            Save<SaveIdentityRole>(context, roleId, (value, ct) => store.SaveRoleAsync(Session(context).Subject, tenantId, Selected(context), roleId, value, context.TraceIdentifier, Session(context).CheckAsync, ct)));
        return app;
    }
    private static async ValueTask<object?> Boundary(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (context.Request.QueryString.HasValue) return Invalid();
        var disconnected = context.RequestAborted;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(disconnected);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var session = await LiveBffSession.CaptureIdentityAsync(context, budget.Token);
            if (session is null) return Problem(403, "identity_access_denied");
            var authentication = await context.AuthenticateAsync(BffAuthentication.CookieScheme);
            context.Items[SessionKey] = session;
            context.Items[SelectedKey] = authentication.Properties is null ? null : TenantSession.ReadSelectedTenant(authentication.Properties);
            context.RequestAborted = budget.Token;
            var result = await next(invocation);
            if (HttpMethods.IsGet(context.Request.Method) && !await session.CheckAsync(budget.Token)) return Problem(403, "identity_access_denied");
            return result;
        }
        catch (Exception error) when (error is IdentityManagementUnavailableException or AuthorizationStoreUnavailableException or BffSessionStoreException)
        { return Problem(503, "identity_unavailable"); }
        catch (OperationCanceledException) when (!disconnected.IsCancellationRequested)
        { return Problem(503, "identity_unavailable"); }
        finally { context.RequestAborted = disconnected; context.Items.Remove(SessionKey); context.Items.Remove(SelectedKey); }
    }
    private static async Task<IResult> Save<T>(HttpContext context, Guid id, Func<T, CancellationToken, Task<IdentityResult<IdentityRevision>>> save)
    {
        var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
        var settings = context.RequestServices.GetRequiredService<BffOptions>();
        if (!await BffEndpoints.IsVerifiedRequest(context, antiforgery, settings)) return Problem(403, "request_verification_failed");
        if (id == Guid.Empty || context.Request.RouteValues.TryGetValue("tenantId", out var routeTenant) && Guid.TryParse(routeTenant?.ToString(), out var tenant) && tenant == Guid.Empty)
            return Invalid();
        if (!context.Request.HasJsonContentType()) return Problem(415, "json_required");
        const int maximumBodyBytes = 65536;
        if (context.Request.ContentLength > maximumBodyBytes) return Problem(413, "request_too_large");
        var ct = context.RequestAborted;
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        try
        {
            int length;
            while ((length = await context.Request.Body.ReadAsync(block, ct)) > 0)
            {
                if (buffer.Length + length > maximumBodyBytes) return Problem(413, "request_too_large");
                buffer.Write(block, 0, length);
            }
            using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            if (!IdentityManagementValidation.TryParse<T>(document.RootElement, out var value)) return Invalid();
            if (!await Session(context).CheckAsync(ct)) return Problem(403, "identity_access_denied");
            return Result(await save(value!, ct));
        }
        catch (JsonException) { return Invalid(); }
        catch (BadHttpRequestException error) { return Problem(error.StatusCode, "invalid_identity_request"); }
    }
    private static LiveBffSession Session(HttpContext context) => (LiveBffSession)context.Items[SessionKey]!;
    private static Guid? Selected(HttpContext context) => context.Items[SelectedKey] as Guid?;
    private static IResult Result<T>(IdentityResult<T> value) => value.Error switch
    {
        IdentityError.None => Results.Ok(value.Value),
        IdentityError.Forbidden => Problem(403, "identity_access_denied"),
        IdentityError.Conflict => Problem(409, "identity_conflict"),
        IdentityError.Capacity => Problem(409, "identity_capacity_reached"),
        IdentityError.NotFound => Problem(404, "identity_not_found"),
        _ => Invalid()
    };
    private static IResult Invalid() => Problem(400, "invalid_identity_request");
    private static IResult Problem(int status, string code) => Results.Problem(statusCode: status, title: "Identity management request could not be completed.",
        extensions: new Dictionary<string, object?> { ["code"] = code });
}
