using AssembleAI.Platform.Identity.Sessions;
using AssembleAI.Platform.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace AssembleAI.Platform.Identity;

public static class BffEndpoints
{
    public const string CsrfHeaderName = "X-CSRF-TOKEN";

    /// <summary>Runs before authentication so storage failures also cover ticket reads.</summary>
    internal static WebApplication UseBffSessionBoundary(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<BffOptions>();
        app.Use(async (context, next) =>
        {
            var bffRequest = context.Request.Path.StartsWithSegments("/bff")
                || context.Request.Path == "/signin-oidc";
            if (bffRequest)
            {
                context.Response.Headers.CacheControl = "no-store";
                if (settings.Enabled && !context.Request.IsHttps
                    && (!app.Environment.IsDevelopment() || !IsLoopbackRequest(context)))
                {
                    await Problem(context, StatusCodes.Status400BadRequest,
                        "A secure connection is required.", "secure_connection_required");
                    return;
                }
            }
            try { await next(context); }
            catch (BadHttpRequestException exception) when (bffRequest && !context.Response.HasStarted)
            {
                context.Response.Clear();
                await Problem(context, exception.StatusCode, "The request is invalid.", "invalid_request");
            }
            catch (BffSessionStoreException) when (!context.Response.HasStarted)
            {
                context.Response.Clear();
                await Problem(context, StatusCodes.Status503ServiceUnavailable,
                    "Session storage is unavailable.", "session_unavailable");
            }
            catch (AuthorizationStoreUnavailableException) when (!context.Response.HasStarted)
            {
                context.Response.Clear();
                await Problem(context, StatusCodes.Status503ServiceUnavailable,
                    "Authorization data is unavailable.", "authorization_unavailable");
            }
        });
        return app;
    }

    public static WebApplication MapBffEndpoints(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<BffOptions>();
        var group = app.MapGroup("/bff");
        if (!settings.Enabled)
        {
            group.MapGet("/login", (HttpContext context) => Problem(context,
                StatusCodes.Status503ServiceUnavailable, "Sign-in is not configured.", "authentication_unavailable"));
            group.MapGet("/csrf", AuthenticationRequired);
            group.MapGet("/session", AuthenticationRequired);
            group.MapPost("/logout", AuthenticationRequired);
            group.MapGet("/tenant", AuthenticationRequired);
            group.MapPost("/tenant", AuthenticationRequired);
            return app;
        }

        group.MapGet("/login", async (HttpContext context) =>
        {
            var values = context.Request.Query["returnUrl"];
            var returnUrl = values.Count == 0 ? "/" : values.ToString();
            if (values.Count > 1 || !IsLocalReturnUrl(returnUrl))
            {
                await Problem(context, StatusCodes.Status400BadRequest,
                    "The return path is invalid.", "invalid_return_url");
                return;
            }
            var prompt = context.Request.Query["prompt"];
            if (prompt.Count > 1 || (prompt.Count == 1 && prompt[0] != "login"))
            {
                await Problem(context, StatusCodes.Status400BadRequest,
                    "The sign-in prompt is invalid.", "invalid_login_prompt");
                return;
            }
            if ((await context.AuthenticateAsync(BffAuthentication.CookieScheme)).Succeeded)
            {
                await Problem(context, StatusCodes.Status409Conflict,
                    "A session already exists. Sign out before signing in again.", "session_already_exists");
                return;
            }
            await context.ChallengeAsync(BffAuthentication.OidcScheme,
                new OpenIdConnectChallengeProperties { RedirectUri = returnUrl, Prompt = prompt.Count == 1 ? "login" : null });
        }).AllowAnonymous();

        group.MapGet("/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new { tokens.RequestToken, headerName = CsrfHeaderName });
        }).AllowAnonymous();

        group.MapGet("/session", (HttpContext context) =>
        {
            var subject = TrustedSubject.Read(context.User)!; // Enforced by IdentityPolicy.
            return Results.Ok(new { iss = subject.Issuer, sub = subject.Subject, name = context.User.Identity?.Name });
        }).RequireAuthorization(IdentityEndpoints.IdentityPolicy);

        group.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await IsVerifiedRequest(context, antiforgery, settings))
            {
                await Problem(context, StatusCodes.Status403Forbidden,
                    "Request verification failed.", "request_verification_failed");
                return;
            }
            await context.SignOutAsync(BffAuthentication.CookieScheme);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }).RequireAuthorization(IdentityEndpoints.IdentityPolicy);
        TenantSession.MapEndpoints(group, settings);
        return app;
    }

    internal static async Task<bool> IsVerifiedRequest(HttpContext context, IAntiforgery antiforgery, BffOptions settings) =>
        context.Request.Headers.Origin.Count == 1
        && string.Equals(context.Request.Headers.Origin[0], settings.PublicOrigin, StringComparison.Ordinal)
        && context.Request.Headers[CsrfHeaderName].Count == 1
        && await antiforgery.IsRequestValidAsync(context);

    internal static Task Problem(HttpContext context, int status, string title, string code)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Problem(statusCode: status, title: title,
            extensions: new Dictionary<string, object?> { ["code"] = code }).ExecuteAsync(context);
    }

    private static Task AuthenticationRequired(HttpContext context) => Problem(context,
        StatusCodes.Status401Unauthorized, "Authentication required.", "authentication_required");

    private static bool IsLoopbackRequest(HttpContext context) =>
        Uri.TryCreate("http://" + context.Request.Host.Value, UriKind.Absolute, out var origin)
        && origin.IsLoopback
        && (context.Connection.RemoteIpAddress is null || System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress));

    private static bool IsLocalReturnUrl(string value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '/' || (value.Length > 1 && value[1] == '/')
            || value.Contains('\\') || value.Any(char.IsControl)) return false;
        try
        {
            // Reject encoded slash/backslash/control prefixes too, including nested
            // escapes; callers may use ordinary encoded path and query text.
            var decoded = value;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var next = Uri.UnescapeDataString(decoded);
                if (next.StartsWith("//", StringComparison.Ordinal) || next.Contains('\\') || next.Any(char.IsControl))
                    return false;
                if (next == decoded) return true;
                decoded = next;
            }
            return !decoded.Contains('%');
        }
        catch (UriFormatException) { return false; }
    }
}
