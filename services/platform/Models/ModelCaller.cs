using AssembleAI.Platform.Authorization;

namespace AssembleAI.Platform.Models;

/// <summary>Server-captured identity and lifetime; never constructed from a model request body.</summary>
public sealed record ModelCaller(SubjectIdentity Subject, Guid TenantId, Guid UserId, DateTimeOffset ExpiresAt,
    Func<CancellationToken, Task<bool>> CheckAsync, CancellationToken Lifetime, ModelCallSource? Source = null);

public sealed record ModelCallSource(string AppId, string ReleaseVersion, Guid RunId, string GatewayRequestId);
