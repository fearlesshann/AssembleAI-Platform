using AssembleAI.Platform.Authorization;

namespace AssembleAI.Platform.Registry;

public interface IRegistryStore
{
    Task<RegistryResult<ApplicationReleaseView>> SaveDraftAsync(SubjectIdentity actor, ApplicationManifest manifest,
        long expectedRevision, string requestId, CancellationToken cancellationToken = default);
    Task<RegistryResult<ApplicationReleaseView>> TransitionAsync(SubjectIdentity actor, string appId, string version,
        string action, long expectedRevision, string requestId, CancellationToken cancellationToken = default);
    Task<RegistryResult<ApplicationReleaseView>> ReadReleaseAsync(SubjectIdentity actor, string appId, string version,
        CancellationToken cancellationToken = default);
    Task<RegistryResult<IReadOnlyList<ApplicationReleaseView>>> ListReleasesAsync(SubjectIdentity actor, string appId,
        CancellationToken cancellationToken = default);
    Task<RegistryResult<TenantApplicationView>> ReadTenantApplicationAsync(SubjectIdentity actor, Guid tenantId,
        string appId, CancellationToken cancellationToken = default);
    Task<RegistryResult<TenantApplicationView>> SetTenantApplicationAsync(SubjectIdentity actor, Guid tenantId,
        string appId, string releaseVersion, bool enabled, long expectedRevision, string requestId,
        CancellationToken cancellationToken = default);
}
