using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AssembleAI.Platform.Registry;

public static class RegistryEndpoints
{
    private const int MaximumBodyBytes = 65536;
    private static readonly object SessionKey = new();

    public static WebApplicationBuilder AddPlatformRegistry(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(new RegistryDeploymentCatalog(builder.Configuration));
        builder.Services.TryAddSingleton<IRegistryStore, PostgresRegistryStore>();
        builder.AddRegistryManagement();
        return builder;
    }

    public static WebApplication MapRegistryEndpoints(this WebApplication app)
    {
        var releases = app.MapGroup("/api/platform/v1/registry/apps/{appId}/releases")
            .RequireAuthorization(IdentityEndpoints.IdentityPolicy);
        releases.AddEndpointFilter(Boundary);
        releases.MapGet("", async (string appId, HttpContext context, IRegistryStore store, CancellationToken token) =>
        {
            if (!ManifestValidator.IsAppId(appId)) return Invalid();
            var result = await store.ListReleasesAsync(Subject(context), appId, token);
            return result.Error == RegistryError.None ? Results.Ok(new { items = result.Value }) : Error(result.Error);
        });
        releases.MapGet("/{version}", async (string appId, string version, HttpContext context, IRegistryStore store, CancellationToken token) =>
        {
            if (!ValidRoute(appId, version)) return Invalid();
            return Result(await store.ReadReleaseAsync(Subject(context), appId, version, token));
        });
        releases.MapPut("/{version}", async (string appId, string version, HttpContext context, IRegistryStore store,
            IAntiforgery antiforgery, BffOptions settings, CancellationToken token) =>
        {
            if (!await BffEndpoints.IsVerifiedRequest(context, antiforgery, settings)) return VerificationFailed();
            if (!ValidRoute(appId, version)) return Invalid();
            var (body, error) = await ReadBody(context, token);
            if (error is not null) return error;
            if (!ManifestValidator.HasFields(body, "expectedRevision", "manifest") || !Revision(body, out var revision)
                || !ManifestValidator.TryParse(body.GetProperty("manifest"), out var manifest)
                || manifest!.AppId != appId || manifest.Version != version) return Error(RegistryError.InvalidManifest);
            if (!await CurrentSession(context, token)) return Error(RegistryError.Forbidden);
            return Result(await store.SaveDraftAsync(Subject(context), manifest, revision, context.TraceIdentifier, token));
        });
        releases.MapPost("/{version}/transitions", async (string appId, string version, HttpContext context, IRegistryStore store,
            IAntiforgery antiforgery, BffOptions settings, CancellationToken token) =>
        {
            if (!await BffEndpoints.IsVerifiedRequest(context, antiforgery, settings)) return VerificationFailed();
            if (!ValidRoute(appId, version)) return Invalid();
            var (body, error) = await ReadBody(context, token);
            if (error is not null) return error;
            if (!ManifestValidator.HasFields(body, "expectedRevision", "action") || !Revision(body, out var revision)
                || revision == 0 || body.GetProperty("action").ValueKind != JsonValueKind.String) return Invalid();
            var action = body.GetProperty("action").GetString()!;
            if (action is not ("submit" or "reject" or "publish" or "disable" or "enable")) return Invalid();
            if (!await CurrentSession(context, token)) return Error(RegistryError.Forbidden);
            return Result(await store.TransitionAsync(Subject(context), appId, version, action, revision, context.TraceIdentifier, token));
        });

        var tenants = app.MapGroup("/api/platform/v1/tenants/{tenantId:guid}/applications")
            .RequireAuthorization(IdentityEndpoints.IdentityPolicy);
        tenants.AddEndpointFilter(Boundary);
        tenants.MapGet("/{appId}", async (Guid tenantId, string appId, HttpContext context, IRegistryStore store,
            IAuthorizationStore authorization, CancellationToken token) =>
        {
            if (!ManifestValidator.IsAppId(appId) || tenantId == Guid.Empty) return Invalid();
            if (await TenantSession.ReadAccessAsync(context, tenantId, authorization, token) is null) return Error(RegistryError.Forbidden);
            return Result(await store.ReadTenantApplicationAsync(Subject(context), tenantId, appId, token));
        });
        tenants.MapPut("/{appId}", async (Guid tenantId, string appId, HttpContext context, IRegistryStore store,
            IAuthorizationStore authorization, IAntiforgery antiforgery, BffOptions settings, CancellationToken token) =>
        {
            if (!await BffEndpoints.IsVerifiedRequest(context, antiforgery, settings)) return VerificationFailed();
            if (!ManifestValidator.IsAppId(appId) || tenantId == Guid.Empty) return Invalid();
            if (await TenantSession.ReadAccessAsync(context, tenantId, authorization, token) is null) return Error(RegistryError.Forbidden);
            var (body, error) = await ReadBody(context, token);
            if (error is not null) return error;
            if (!ManifestValidator.HasFields(body, "expectedRevision", "releaseVersion", "enabled") || !Revision(body, out var revision)
                || body.GetProperty("releaseVersion").ValueKind != JsonValueKind.String
                || !ManifestValidator.IsVersion(body.GetProperty("releaseVersion").GetString())
                || body.GetProperty("enabled").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Invalid();
            if (!await CurrentSession(context, token)) return Error(RegistryError.Forbidden);
            return Result(await store.SetTenantApplicationAsync(Subject(context), tenantId, appId,
                body.GetProperty("releaseVersion").GetString()!, body.GetProperty("enabled").GetBoolean(),
                revision, context.TraceIdentifier, token));
        });
        return app.MapRegistryManagementEndpoints();
    }

    private static async ValueTask<object?> Boundary(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (context.Request.Query.Count != 0) return Invalid();
        var disconnected = context.RequestAborted;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(disconnected);
        budget.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var session = context.Request.RouteValues.TryGetValue("tenantId", out var routeTenant)
                && Guid.TryParse(routeTenant?.ToString(), out var tenant)
                ? await LiveBffSession.CaptureAsync(context, tenant, budget.Token)
                : await LiveBffSession.CaptureIdentityAsync(context, budget.Token);
            if (session is null) return Error(RegistryError.Forbidden);
            context.Items[SessionKey] = session;
            // Minimal-API binding happens before filters. Supply the same bounded
            // token to both the bound handler and its HttpContext consumers.
            for (var index = 0; index < invocation.Arguments.Count; index++)
                if (invocation.Arguments[index] is CancellationToken) invocation.Arguments[index] = budget.Token;
            context.RequestAborted = budget.Token;
            var result = await next(invocation);
            if (HttpMethods.IsGet(context.Request.Method) && !await session.CheckAsync(budget.Token))
                return Error(RegistryError.Forbidden);
            return result;
        }
        catch (Exception error) when (error is RegistryStoreUnavailableException or AuthorizationStoreUnavailableException)
        {
            return Problem(503, "Application registry is unavailable.", "registry_unavailable");
        }
        catch (OperationCanceledException) when (!disconnected.IsCancellationRequested)
        { return Problem(503, "Application registry is unavailable.", "registry_unavailable"); }
        finally { context.RequestAborted = disconnected; context.Items.Remove(SessionKey); }
    }
    private static Task<bool> CurrentSession(HttpContext context, CancellationToken token) =>
        context.Items.TryGetValue(SessionKey, out var value) && value is LiveBffSession session
            ? session.CheckAsync(token) : Task.FromResult(false);
    private static SubjectIdentity Subject(HttpContext context) => context.Items.TryGetValue(SessionKey, out var value)
        && value is LiveBffSession session ? session.Subject : throw new RegistryStoreUnavailableException();
    private static bool ValidRoute(string appId, string version) => ManifestValidator.IsAppId(appId) && ManifestValidator.IsVersion(version);
    private static bool Revision(JsonElement value, out long revision)
    {
        revision = 0;
        return value.GetProperty("expectedRevision").ValueKind == JsonValueKind.Number
            && value.GetProperty("expectedRevision").TryGetInt64(out revision) && revision >= 0;
    }
    private static IResult Result<T>(RegistryResult<T> result) => result.Error == RegistryError.None
        ? Results.Ok(result.Value) : Error(result.Error);
    private static IResult Error(RegistryError error) => error switch
    {
        RegistryError.Forbidden => Problem(403, "Registry access denied.", "registry_access_denied"),
        RegistryError.NotFound => Problem(404, "Registry record not found.", "registry_not_found"),
        RegistryError.Conflict => Problem(409, "The state or revision has changed.", "registry_conflict"),
        RegistryError.InvalidManifest => Problem(400, "The application manifest is invalid.", "invalid_manifest"),
        RegistryError.DeploymentNotAllowed => Problem(400, "The release does not match an approved deployment.", "deployment_not_allowed"),
        _ => Problem(503, "Application registry is unavailable.", "registry_unavailable")
    };
    private static IResult Invalid() => Problem(400, "The request is invalid.", "invalid_registry_request");
    private static IResult VerificationFailed() => Problem(403, "Request verification failed.", "request_verification_failed");
    private static IResult Problem(int status, string title, string code) => Results.Problem(statusCode: status, title: title,
        extensions: new Dictionary<string, object?> { ["code"] = code });

    private static async Task<(JsonElement Body, IResult? Error)> ReadBody(HttpContext context, CancellationToken token)
    {
        if (!context.Request.HasJsonContentType()) return (default, Problem(415, "JSON content is required.", "json_required"));
        if (context.Request.ContentLength > MaximumBodyBytes) return (default, Problem(413, "The request is too large.", "request_too_large"));
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        try
        {
            int length;
            while ((length = await context.Request.Body.ReadAsync(block, token)) > 0)
            {
                if (buffer.Length + length > MaximumBodyBytes) return (default, Problem(413, "The request is too large.", "request_too_large"));
                buffer.Write(block, 0, length);
            }
            using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            return (document.RootElement.Clone(), null);
        }
        catch (JsonException) { return (default, Invalid()); }
        catch (BadHttpRequestException error) { return (default, Problem(error.StatusCode, "The request is invalid.", "invalid_registry_request")); }
    }
}
