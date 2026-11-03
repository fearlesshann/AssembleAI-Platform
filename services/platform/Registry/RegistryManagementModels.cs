using AssembleAI.Platform.Authorization;

namespace AssembleAI.Platform.Registry;

public sealed record RegistryWorkspace(Guid ActorUserId, bool CanCreate, bool CanReview, IReadOnlyList<RegistryWorkspaceApplication> Items);
public sealed record RegistryWorkspaceApplication(string AppId, string Name, string Category, Guid OwnerUserId, bool CanEdit, bool CanReview);
public sealed record TenantRegistryWorkspace(Guid ActorUserId, Guid TenantId, IReadOnlyList<TenantRegistryApplication> Items);
public sealed record TenantRegistryApplication(string AppId, string Name, string Category, TenantRegistryPointer? Current);
public sealed record TenantRegistryPointer(string ReleaseVersion, bool Enabled, bool Available, long Revision);
public sealed record RegistryAuditPage(IReadOnlyList<RegistryAuditItem> Items, bool HasMore);
public sealed record RegistryAuditItem(Guid EventId, Guid ActorUserId, Guid? TenantId, string AppId, string ReleaseVersion,
    string Action, string Result, string RequestId, DateTimeOffset OccurredAt);

public interface IRegistryManagementStore
{
    Task<RegistryResult<RegistryWorkspace>> ReadWorkspaceAsync(SubjectIdentity subject, CancellationToken token);
    Task<RegistryResult<TenantRegistryWorkspace>> ReadTenantWorkspaceAsync(SubjectIdentity subject, Guid tenantId, CancellationToken token);
    Task<RegistryResult<IReadOnlyList<ApplicationReleaseView>>> ReadTenantReleasesAsync(SubjectIdentity subject, Guid tenantId, string appId, CancellationToken token);
    Task<RegistryResult<RegistryAuditPage>> ReadAuditAsync(SubjectIdentity subject, Guid? tenantId, string appId, CancellationToken token);
}
