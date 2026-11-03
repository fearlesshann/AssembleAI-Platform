using System.Security.Claims;
using AssembleAI.Platform.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

namespace AssembleAI.Platform.Identity;

/// <summary>A selected tenant is a server-side intent, never an authorization grant.</summary>
public static class TenantSession
{
    private const string SelectionKey = ".assembleai.tenant";

    public static Guid? ReadSelectedTenant(AuthenticationProperties properties) =>
        properties.Items.TryGetValue(SelectionKey, out var value)
        && Guid.TryParseExact(value, "D", out var tenantId) && tenantId != Guid.Empty ? tenantId : null;

    /// <summary>Callers must supply their explicit route/resource tenant, not silently use the selection.</summary>
    public static async Task<AccessSnapshot?> ReadAccessAsync(HttpContext context, Guid expectedTenantId,
        IAuthorizationStore store, CancellationToken cancellationToken = default)
    {
        if (!cancellationToken.CanBeCanceled) cancellationToken = context.RequestAborted;
        cancellationToken.ThrowIfCancellationRequested();
        var session = await context.AuthenticateAsync(BffAuthentication.CookieScheme);
        if (!session.Succeeded || session.Properties is null || session.Principal is null
            || ReadSelectedTenant(session.Properties) != expectedTenantId) return null;
        var subject = TrustedSubject.Read(session.Principal);
        if (subject is null) return null;
        var access = await store.ReadAccessAsync(subject, expectedTenantId, cancellationToken);
        return access?.TenantId == expectedTenantId ? access : null;
    }

    internal static void MapEndpoints(RouteGroupBuilder group, BffOptions settings)
    {
        group.MapGet("/tenant", async (HttpContext context, IAuthorizationStore store, CancellationToken cancellationToken) =>
        {
            var session = await context.AuthenticateAsync(BffAuthentication.CookieScheme);
            if (!session.Succeeded || session.Properties is null) return AuthenticationRequired();
            var selected = ReadSelectedTenant(session.Properties);
            if (selected is null) return Results.Ok(new TenantSelection(null, null));
            var access = await ReadAccessAsync(context, selected.Value, store, cancellationToken);
            return access is null ? AccessDenied() : Results.Ok(new TenantSelection(selected, access.AuthorizationVersion));
        }).RequireAuthorization(IdentityEndpoints.IdentityPolicy);

        group.MapPost("/tenant", async (TenantSelectionRequest request, HttpContext context,
            IAntiforgery antiforgery, IAuthorizationStore store, CancellationToken cancellationToken) =>
        {
            if (!await BffEndpoints.IsVerifiedRequest(context, antiforgery, settings))
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Request verification failed.",
                    extensions: new Dictionary<string, object?> { ["code"] = "request_verification_failed" });
            if (request.TenantId == Guid.Empty)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A tenant identifier is required.",
                    extensions: new Dictionary<string, object?> { ["code"] = "invalid_tenant" });
            // Explicit Cookie authentication ensures SignIn renews the same session
            // reference. A request header or unrelated authentication handler cannot select it.
            var session = await context.AuthenticateAsync(BffAuthentication.CookieScheme);
            if (!session.Succeeded || session.Properties is null || session.Principal is null) return AuthenticationRequired();
            var subject = TrustedSubject.Read(session.Principal);
            if (subject is null) return AuthenticationRequired();
            var access = await store.ReadAccessAsync(subject, request.TenantId, cancellationToken);
            if (access is null || access.TenantId != request.TenantId) return AccessDenied();
            var properties = new AuthenticationProperties(new Dictionary<string, string?>(session.Properties.Items, StringComparer.Ordinal));
            properties.Items[SelectionKey] = request.TenantId.ToString("D");
            // A login return URI is transient and must not turn this JSON write into a redirect.
            properties.RedirectUri = null;
            cancellationToken.ThrowIfCancellationRequested();
            await context.SignInAsync(BffAuthentication.CookieScheme,
                new ClaimsPrincipal(session.Principal.Identities.Select(identity => identity.Clone())), properties);
            return Results.Ok(new TenantSelection(request.TenantId, access.AuthorizationVersion));
        }).RequireAuthorization(IdentityEndpoints.IdentityPolicy);
    }

    private static IResult AuthenticationRequired() => Results.Problem(statusCode: StatusCodes.Status401Unauthorized,
        title: "Authentication required.", extensions: new Dictionary<string, object?> { ["code"] = "authentication_required" });

    private static IResult AccessDenied() => Results.Problem(statusCode: StatusCodes.Status403Forbidden,
        title: "Tenant access denied.", extensions: new Dictionary<string, object?> { ["code"] = "tenant_access_denied" });

    public sealed record TenantSelectionRequest(Guid TenantId);
    private sealed record TenantSelection(Guid? TenantId, long? AuthorizationVersion);
}
