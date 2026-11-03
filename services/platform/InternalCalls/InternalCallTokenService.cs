using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Identity;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AssembleAI.Platform.InternalCalls;

// Load from trusted routing and resource repositories. Never bind this type to client input.
public sealed record InternalCallTarget(string AppId, string ReleaseVersion, string Audience, string Permission,
    ProtectedResource Resource, string Method, string Path);
public sealed record VerifiedInternalCall(Guid UserId, Guid TenantId, long AuthorizationVersion, string RequestId);

/// <summary>
/// Internal authorization capability, scoped to one target and request identifier.
/// It is not a one-use/replay cache or BFF session: logout does not revoke an already
/// signed token before its 30-second expiry. Current policy is checked on every use.
/// </summary>
public sealed class InternalCallTokenService(
    InternalCallOptions options, InternalCallKeys keys, IAuthorizationStore store, TimeProvider clock)
{
    public const string TokenType = "assembleai-internal+jwt";
    private readonly string issuer = options.Issuer;
    private readonly bool enabled = options.Enabled;
    private readonly JsonWebTokenHandler handler = new() { MapInboundClaims = false, MaximumTokenSizeInBytes = 16384 };

    public async Task<string?> IssueAsync(ClaimsPrincipal principal, Guid selectedTenantId, InternalCallTarget target,
        string requestId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!enabled || keys.SigningCredentials is null || !ValidTarget(target, requestId)
            || target.Resource.TenantId != selectedTenantId) return null;
        var subject = TrustedSubject.Read(principal);
        if (subject is null || !Text(subject.Issuer, 2048) || !Text(subject.Subject, 1024)) return null;
        var access = await store.ReadAccessAsync(subject, selectedTenantId, cancellationToken);
        if (!PermissionEvaluator.Allows(access, target.Permission, target.Resource)) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.FromUnixTimeSeconds(clock.GetUtcNow().ToUnixTimeSeconds());
        var claims = new Dictionary<string, object>
        {
            ["sub"] = access!.UserId.ToString("D"), ["oidc_iss"] = subject.Issuer, ["oidc_sub"] = subject.Subject,
            ["tenant_id"] = selectedTenantId.ToString("D"), ["authz_ver"] = access.AuthorizationVersion,
            ["app_id"] = target.AppId, ["release"] = target.ReleaseVersion, ["permission"] = target.Permission,
            ["resource_type"] = target.Resource.Type, ["resource_id"] = target.Resource.Id,
            ["organization_id"] = target.Resource.OrganizationId?.ToString("D") ?? "",
            ["method"] = target.Method, ["path"] = target.Path, ["request_id"] = requestId, ["jti"] = Guid.NewGuid().ToString("N")
        };
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer, Audience = target.Audience, Claims = claims,
            IssuedAt = now.UtcDateTime, NotBefore = now.UtcDateTime, Expires = now.AddSeconds(30).UtcDateTime,
            TokenType = TokenType, SigningCredentials = keys.SigningCredentials
        });
        return token.Length <= 16384 ? token : null;
    }

    public async Task<VerifiedInternalCall?> ValidateAsync(string token, InternalCallTarget target, string requestId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!enabled || !ValidTarget(target, requestId) || string.IsNullOrEmpty(token) || token.Length > 16384) return null;
        SubjectIdentity subject;
        Guid userId;
        long version;
        long expiresAt;
        try
        {
            var segments = token.Split('.');
            if (segments.Length != 3 || segments.Any(segment => segment.Length == 0)) return null;
            using var headerDocument = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(segments[0]));
            using var payloadDocument = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(segments[1]));
            var header = headerDocument.RootElement;
            var payload = payloadDocument.RootElement;
            if (!UniqueObject(header) || !UniqueObject(payload)
                || header.EnumerateObject().Count() != 3
                || String(header, "alg") != "RS256" || String(header, "typ") != TokenType
                || String(header, "kid") is not { } kid || keys.FindValidationKey(kid) is not { } validationKey)
                return null;
            var now = clock.GetUtcNow().ToUnixTimeSeconds();
            if (!Integer(payload, "iat", out var issuedAt) || !Integer(payload, "nbf", out var notBefore)
                || !Integer(payload, "exp", out expiresAt) || issuedAt < 0 || notBefore != issuedAt
                || issuedAt > now || expiresAt <= now || expiresAt <= issuedAt || expiresAt - issuedAt > 30
                || !Integer(payload, "authz_ver", out version) || version < 1
                || String(payload, "iss") != issuer || String(payload, "aud") != target.Audience
                || !Guid.TryParseExact(String(payload, "sub"), "D", out userId) || userId == Guid.Empty
                || String(payload, "tenant_id") != target.Resource.TenantId.ToString("D")
                || !Text(String(payload, "oidc_iss"), 2048) || !Text(String(payload, "oidc_sub"), 1024)
                || !Guid.TryParseExact(String(payload, "jti"), "N", out var jti) || jti == Guid.Empty
                || !MatchesTarget(payload, target, requestId)) return null;

            var result = await handler.ValidateTokenAsync(token, new TokenValidationParameters
            {
                ValidIssuer = issuer, ValidateIssuer = true, ValidAudience = target.Audience, ValidateAudience = true,
                IgnoreTrailingSlashWhenValidatingAudience = false, RequireAudience = true,
                IssuerSigningKey = validationKey, ValidateIssuerSigningKey = true, RequireSignedTokens = true,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ValidTypes = [TokenType], TryAllIssuerSigningKeys = false,
                RequireExpirationTime = true, ValidateLifetime = true, ClockSkew = TimeSpan.Zero,
                LifetimeValidator = (start, end, _, _) => start.HasValue && end.HasValue
                    && start.Value <= clock.GetUtcNow().UtcDateTime && end.Value > clock.GetUtcNow().UtcDateTime,
                IncludeTokenOnFailedValidation = false, LogTokenId = false, LogValidationExceptions = false
            });
            if (!result.IsValid) return null;
            subject = new(String(payload, "oidc_iss")!, String(payload, "oidc_sub")!);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException
            or SecurityTokenException or CryptographicException or OverflowException)
        {
            return null;
        }
        // Read only after all cryptographic and target checks. Do not turn storage
        // unavailability or request cancellation into authorization success.
        cancellationToken.ThrowIfCancellationRequested();
        var access = await store.ReadAccessAsync(subject, target.Resource.TenantId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (clock.GetUtcNow().ToUnixTimeSeconds() >= expiresAt || access?.UserId != userId
            || access.AuthorizationVersion != version || !PermissionEvaluator.Allows(access, target.Permission, target.Resource)) return null;
        return new(userId, access.TenantId, version, requestId);
    }

    private static bool MatchesTarget(JsonElement payload, InternalCallTarget target, string requestId) =>
        String(payload, "app_id") == target.AppId && String(payload, "release") == target.ReleaseVersion
        && String(payload, "permission") == target.Permission && String(payload, "resource_type") == target.Resource.Type
        && String(payload, "resource_id") == target.Resource.Id
        && String(payload, "organization_id") == (target.Resource.OrganizationId?.ToString("D") ?? "")
        && String(payload, "method") == target.Method && String(payload, "path") == target.Path
        && String(payload, "request_id") == requestId;

    private static bool ValidTarget(InternalCallTarget target, string requestId) => target is not null && target.Resource is not null
        && Text(target.AppId, 128) && Text(target.ReleaseVersion, 128) && Text(target.Audience, 256)
        && Text(target.Permission, 256) && Text(target.Resource.Type, 128) && Text(target.Resource.Id, 1024)
        && target.Resource.TenantId != Guid.Empty && target.Resource.OrganizationId != Guid.Empty
        && Text(requestId, 128) && Text(target.Method, 16) && target.Method.All(character => character is >= 'A' and <= 'Z')
        && Text(target.Path, 2048) && target.Path[0] == '/' && !target.Path.StartsWith("//", StringComparison.Ordinal)
        && !target.Path.Contains('\\') && !target.Path.Contains('?') && !target.Path.Contains('#');

    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum
        && value == value.Trim() && !value.Any(char.IsControl);
    private static string? String(JsonElement value, string name) => value.TryGetProperty(name, out var item)
        && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static bool Integer(JsonElement value, string name, out long result)
    {
        result = 0;
        return value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out result);
    }
    private static bool UniqueObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(property => names.Add(property.Name));
    }
}
