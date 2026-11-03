using AssembleAI.Platform.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace AssembleAI.Platform.Identity;

public static class IdentityEndpoints
{
    public const string IdentityPolicy = "PlatformIdentity";

    public static IServiceCollection AddPlatformAuthorization(this IServiceCollection services)
    {
        // The OIDC/BFF adapter will register the validated session scheme. No header or
        // development impersonation scheme is registered by the platform.
        services.AddAuthentication();
        services.AddAuthorization(options => options.AddPolicy(IdentityPolicy, policy =>
            policy.RequireAuthenticatedUser().RequireAssertion(context => TrustedSubject.Read(context.User) is not null)));
        services.AddHttpContextAccessor();
        services.AddScoped<IAuthorizationHandler, PlatformPermissionHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ApiAuthorizationResultHandler>();
        services.AddSingleton<IAuthorizationStore, PostgresAuthorizationStore>();
        return services;
    }

    public static void MapIdentityEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/platform/v1/tenants").RequireAuthorization(IdentityPolicy);
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try
            {
                return await next(context);
            }
            catch (AuthorizationStoreUnavailableException)
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Authorization data is unavailable.",
                    extensions: new Dictionary<string, object?> { ["code"] = "authorization_unavailable" });
            }
        });

        group.MapGet("", async (HttpContext context, IAuthorizationStore store, CancellationToken cancellationToken) =>
        {
            var subject = TrustedSubject.Read(context.User)!; // Enforced by IdentityPolicy.
            var tenants = await store.ListTenantsAsync(subject, cancellationToken);
            return Results.Ok(new { items = tenants });
        });

        group.MapGet("/{tenantId:guid}/access", async (Guid tenantId, HttpContext context,
            IAuthorizationStore store, CancellationToken cancellationToken) =>
        {
            var subject = TrustedSubject.Read(context.User)!;
            var access = await store.ReadAccessAsync(subject, tenantId, cancellationToken);
            // An unknown tenant and an existing tenant without membership have the same result.
            if (access is null || access.TenantId != tenantId)
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Tenant access denied.",
                    extensions: new Dictionary<string, object?> { ["code"] = "tenant_access_denied" });
            // EnabledOrganizationIds is an internal tenant-wide validation set, not
            // a list the caller is entitled to enumerate.
            return Results.Ok(new
            {
                access.UserId,
                access.TenantId,
                access.AuthorizationVersion,
                grants = access.Grants.Select(grant => new
                {
                    grant.Permission,
                    grant.ResourceType,
                    grant.ResourceId,
                    grant.TenantWide,
                    organizationIds = grant.OrganizationIds.Where(access.EnabledOrganizationIds.Contains).Order()
                })
            });
        });
    }

    private sealed class ApiAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
    {
        private readonly AuthorizationMiddlewareResultHandler fallback = new();

        public async Task HandleAsync(RequestDelegate next, HttpContext context,
            AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
        {
            if (context.Request.Path.StartsWithSegments("/api/platform") || context.Request.Path.StartsWithSegments("/api/apps"))
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!authorizeResult.Succeeded)
                {
                    var status = authorizeResult.Challenged
                        ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden;
                    await Results.Problem(statusCode: status,
                        title: status == 401 ? "Authentication required." : "Access denied.",
                        extensions: new Dictionary<string, object?>
                        {
                            ["code"] = status == 401 ? "authentication_required" : "access_denied"
                        }).ExecuteAsync(context);
                    return;
                }
            }
            await fallback.HandleAsync(next, context, policy, authorizeResult);
        }
    }
}
