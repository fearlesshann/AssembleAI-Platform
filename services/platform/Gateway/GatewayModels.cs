using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Registry;

namespace AssembleAI.Platform.Gateway;

public sealed record GatewayRuntime(Guid RunId, Guid UserId, Guid TenantId, string AppId, string ReleaseVersion,
    ApplicationManifest Manifest, DateTimeOffset ExpiresAt, DateTimeOffset? DrainUntil);

public interface IGatewayStore
{
    Task<ApplicationManifest?> ResolveStartAsync(SubjectIdentity subject, Guid tenantId, string appId, string version, CancellationToken token = default);
    Task<GatewayRuntime?> CreateRunAsync(SubjectIdentity subject, string sessionKey, Guid tenantId, string appId, string version,
        TimeSpan lifetime, TimeSpan drainWindow, CancellationToken token = default);
    Task<GatewayRuntime?> ReadRunAsync(SubjectIdentity subject, string sessionKey, Guid runId, CancellationToken token = default);
    Task StartCallAsync(GatewayRuntime runtime, string requestId, string method, string path, CancellationToken token = default);
    Task FinishCallAsync(string requestId, int httpStatus, string outcome, long elapsedMilliseconds, CancellationToken token = default);
}

public sealed class GatewayStoreUnavailableException : Exception
{
    public GatewayStoreUnavailableException() : base("The gateway store is unavailable.") { }
}
