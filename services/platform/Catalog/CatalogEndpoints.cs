using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Identity;
using AssembleAI.Platform.Registry;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AssembleAI.Platform.Catalog;

public static class CatalogEndpoints
{
    public static WebApplicationBuilder AddPlatformCatalog(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton<ICatalogStore, PostgresCatalogStore>();
        return builder;
    }

    public static WebApplication MapCatalogEndpoints(this WebApplication app)
    {
        var routes = app.MapGroup("/api/platform/v1/tenants/{tenantId:guid}/catalog")
            .RequireAuthorization(IdentityEndpoints.IdentityPolicy);
        routes.MapGet("", (HttpContext context, Guid tenantId, ICatalogStore store) =>
            Read(context, tenantId, null, store));
        routes.MapGet("/{appId}", (HttpContext context, Guid tenantId, string appId, ICatalogStore store) =>
            Read(context, tenantId, appId, store));
        return app;
    }

    private static async Task<IResult> Read(HttpContext context, Guid tenantId, string? appId, ICatalogStore store)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (tenantId == Guid.Empty || appId is not null && !ManifestValidator.IsAppId(appId) || context.Request.Query.Count != 0)
            return Problem(400, "invalid_catalog_request", "The catalog request is invalid.");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        budget.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var session = await LiveBffSession.CaptureAsync(context, tenantId, budget.Token);
            if (session is null) return Forbidden();
            object? value;
            bool member;
            if (appId is null)
            {
                var result = await store.ListAsync(session.Subject, tenantId, budget.Token);
                (member, value) = (result.IsMember, result.Value);
            }
            else
            {
                var result = await store.ReadAsync(session.Subject, tenantId, appId, budget.Token);
                (member, value) = (result.IsMember, result.Value);
            }
            if (!member || !await session.CheckAsync(budget.Token)) return Forbidden();
            return value is null ? Problem(404, "catalog_application_not_found", "The application is not available.") : Results.Ok(value);
        }
        catch (Exception error) when (error is CatalogStoreUnavailableException or AuthorizationStoreUnavailableException
            || error is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
        { return Problem(503, "catalog_unavailable", "The application catalog is unavailable."); }
    }

    private static IResult Forbidden() => Problem(403, "catalog_access_denied", "Catalog access denied.");
    private static IResult Problem(int status, string code, string title) => Results.Problem(statusCode: status, title: title,
        extensions: new Dictionary<string, object?> { ["code"] = code });
}
