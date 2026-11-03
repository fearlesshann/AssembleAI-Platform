using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AssembleAI.Platform.Registry;

public static class RegistryManagementEndpoints
{
    public static WebApplicationBuilder AddRegistryManagement(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton<IRegistryManagementStore, PostgresRegistryManagementStore>();
        return builder;
    }
    public static WebApplication MapRegistryManagementEndpoints(this WebApplication app)
    {
        var global = app.MapGroup("/api/platform/v1/registry").RequireAuthorization(IdentityEndpoints.IdentityPolicy);
        global.MapGet("/workspace", (HttpContext context, IRegistryManagementStore store) =>
            Read(context, null, null, (subject, token) => store.ReadWorkspaceAsync(subject, token)));
        global.MapGet("/apps/{appId}/audit", (HttpContext context, string appId, IRegistryManagementStore store) =>
            Read(context, null, appId, (subject, token) => store.ReadAuditAsync(subject, null, appId, token)));
        var tenant = app.MapGroup("/api/platform/v1/tenants/{tenantId:guid}/registry").RequireAuthorization(IdentityEndpoints.IdentityPolicy);
        tenant.MapGet("/workspace", (HttpContext context, Guid tenantId, IRegistryManagementStore store) =>
            Read(context, tenantId, null, (subject, token) => store.ReadTenantWorkspaceAsync(subject, tenantId, token)));
        tenant.MapGet("/apps/{appId}/releases", (HttpContext context, Guid tenantId, string appId, IRegistryManagementStore store) =>
            Read(context, tenantId, appId, async (subject, token) =>
            {
                var result = await store.ReadTenantReleasesAsync(subject, tenantId, appId, token);
                return result.Error == RegistryError.None ? RegistryResult<object>.Success(new { items = result.Value }) : RegistryResult<object>.Failure(result.Error);
            }));
        tenant.MapGet("/apps/{appId}/audit", (HttpContext context, Guid tenantId, string appId, IRegistryManagementStore store) =>
            Read(context, tenantId, appId, (subject, token) => store.ReadAuditAsync(subject, tenantId, appId, token)));
        return app;
    }
    private static async Task<IResult> Read<T>(HttpContext context, Guid? tenantId, string? appId,
        Func<SubjectIdentity, CancellationToken, Task<RegistryResult<T>>> read)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (tenantId == Guid.Empty || appId is not null && !ManifestValidator.IsAppId(appId) || context.Request.QueryString.HasValue)
            return Problem(400, "invalid_registry_request");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        budget.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var session = tenantId.HasValue ? await LiveBffSession.CaptureAsync(context, tenantId.Value, budget.Token)
                : await LiveBffSession.CaptureIdentityAsync(context, budget.Token);
            if (session is null) return Problem(403, "registry_access_denied");
            var result = await read(session.Subject, budget.Token);
            if (!await session.CheckAsync(budget.Token)) return Problem(403, "registry_access_denied");
            return result.Error switch
            {
                RegistryError.None => Results.Ok(result.Value),
                RegistryError.NotFound => Problem(404, "registry_not_found"),
                RegistryError.Forbidden => Problem(403, "registry_access_denied"),
                _ => Problem(503, "registry_unavailable")
            };
        }
        catch (Exception error) when (error is RegistryStoreUnavailableException or AuthorizationStoreUnavailableException
            || error is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
        { return Problem(503, "registry_unavailable"); }
    }
    private static IResult Problem(int status, string code) => Results.Problem(statusCode: status, title: "The registry request could not complete.",
        extensions: new Dictionary<string, object?> { ["code"] = code });
}
