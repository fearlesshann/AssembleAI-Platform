using System.Text.Json;
using System.Text.RegularExpressions;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Gateway;
using AssembleAI.Platform.Registry;
using Microsoft.IdentityModel.Tokens;

namespace AssembleAI.Platform.InternalCalls;

/// <summary>Internal consumer adapter. Body fields describe the receiving service,
/// not an authorization target. Permissions and audiences come from deployment state.</summary>
public static class ApplicationCallValidation
{
    public static async Task Handle(HttpContext context, InternalCallTokenService tokens,
        IApplicationCallStore store, RegistryDeploymentCatalog catalog, GatewayOptions gateway, TimeProvider clock)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        budget.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            if (context.Request.QueryString.HasValue || context.Request.Headers.ContentEncoding.Count != 0
                || context.Request.ContentType is not { } contentType
                || !Regex.IsMatch(contentType, "\\Aapplication/json(?:\\s*;\\s*charset=(?:utf-8|\"utf-8\"))?\\z", RegexOptions.IgnoreCase))
            { await Failure(context, 400, "invalid_validation_request"); return; }
            var credentials = context.Request.Headers.Authorization;
            if (credentials.Count != 1 || credentials[0] is not { Length: <= 16391 } bearer
                || !Regex.IsMatch(bearer, "\\ABearer [A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\z"))
            { await Failure(context, 401, "internal_credentials_required"); return; }
            var requestIds = context.Request.Headers["X-Request-ID"];
            if (requestIds.Count != 1 || requestIds[0] is not { } requestId || !Regex.IsMatch(requestId, "\\A[a-f0-9]{32}\\z"))
            { await Failure(context, 400, "invalid_request_id"); return; }
            var body = await ReadBody(context.Request, budget.Token);
            if (body is null) { await Failure(context, 400, "invalid_validation_request"); return; }
            using var document = JsonDocument.Parse(body);
            var json = document.RootElement;
            if (json.ValueKind != JsonValueKind.Object || json.EnumerateObject().Count() != 4
                || json.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != 4
                || json.EnumerateObject().Any(item => item.Name is not ("service" or "version" or "method" or "path")))
            { await Failure(context, 400, "invalid_validation_request"); return; }
            var service = String(json, "service"); var version = String(json, "version");
            var method = String(json, "method"); var path = String(json, "path");
            if (!ManifestValidator.IsAppId(service) || !ManifestValidator.IsVersion(version)
                || method is not ("GET" or "POST") || !GatewayOptions.ValidPath(path))
            { await Failure(context, 400, "invalid_validation_request"); return; }
            var token = bearer[7..];
            // These hints select a candidate only. No identity, audience or grant is
            // accepted from the caller; ValidateAsync below verifies every binding
            // and signature before reading current authorization or returning identity.
            var hint = RoutingHint(token);
            if (hint is null) { await Failure(context, 403, "call_access_denied"); return; }
            var manifest = await store.ReadAsync(hint.TenantId, hint.AppId, hint.ReleaseVersion, budget.Token);
            var binding = manifest is null ? null : catalog.ResolveBackend(manifest.BackendService, manifest.BackendVersion);
            var operation = manifest is null ? null : gateway.Find(manifest, method, path!);
            if (manifest is null || binding is null || operation is null || !catalog.Allows(manifest)
                || manifest.AppId != hint.AppId || manifest.Version != hint.ReleaseVersion
                || manifest.BackendService != service || manifest.BackendVersion != version)
            { await Failure(context, 403, "call_access_denied"); return; }
            var target = new InternalCallTarget(manifest.AppId, manifest.Version, binding.Audience, operation.Permission,
                new(hint.TenantId, "application", manifest.AppId, null), method, path!);
            var verified = await tokens.ValidateAsync(token, target, requestId, budget.Token);
            if (verified is null) { await Failure(context, 403, "call_access_denied"); return; }
            // Re-read enable/publish state after authorization. Existing signed old
            // releases may drain; the current tenant version pointer is not compared.
            var current = await store.ReadAsync(verified.TenantId, manifest.AppId, manifest.Version, budget.Token);
            if (current is null || ManifestValidator.Serialize(current) != ManifestValidator.Serialize(manifest)
                || clock.GetUtcNow().ToUnixTimeSeconds() >= hint.ExpiresAt)
            { await Failure(context, 403, "call_access_denied"); return; }
            budget.Token.ThrowIfCancellationRequested();
            await context.Response.WriteAsJsonAsync(new { verified.UserId, verified.TenantId, verified.AuthorizationVersion,
                verified.RequestId, manifest.AppId, releaseVersion = manifest.Version }, budget.Token);
        }
        catch (JsonException) { await Failure(context, 400, "invalid_validation_request"); }
        catch (Exception error) when (error is ApplicationCallStoreUnavailableException or AuthorizationStoreUnavailableException
            or OperationCanceledException or IOException)
        {
            if (!context.RequestAborted.IsCancellationRequested) await Failure(context, 503, "validation_unavailable");
        }
    }

    private static async Task<byte[]?> ReadBody(HttpRequest request, CancellationToken token)
    {
        if (request.ContentLength > 2048) return null;
        var buffer = new byte[2049]; var count = 0;
        while (count < buffer.Length)
        {
            var read = await request.Body.ReadAsync(buffer.AsMemory(count), token);
            if (read == 0) return count == 0 ? null : buffer[..count];
            count += read;
        }
        return null;
    }
    private static Hint? RoutingHint(string token)
    {
        try
        {
            using var document = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.Split('.')[1]));
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count()) return null;
            var appId = String(value, "app_id"); var release = String(value, "release");
            return ManifestValidator.IsAppId(appId) && ManifestValidator.IsVersion(release)
                && Guid.TryParseExact(String(value, "tenant_id"), "D", out var tenant) && tenant != Guid.Empty
                && value.TryGetProperty("exp", out var expiry) && expiry.ValueKind == JsonValueKind.Number && expiry.TryGetInt64(out var expiresAt)
                ? new(appId!, release!, tenant, expiresAt) : null;
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException) { return null; }
    }
    private static string? String(JsonElement value, string name) => value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static Task Failure(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { code }, context.RequestAborted);
    }
    private sealed record Hint(string AppId, string ReleaseVersion, Guid TenantId, long ExpiresAt);
}
