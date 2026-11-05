using AssembleAI.Platform.Authorization;

namespace AssembleAI.Platform.IdentityManagement;

public sealed record IdentityUser(Guid UserId, string Issuer, string Subject, string DisplayName, bool Enabled, bool PlatformAdministrator, long Revision);
public sealed record IdentityTenant(Guid TenantId, string Name, bool Enabled, long Revision);
public sealed record IdentityMember(Guid UserId, string DisplayName, bool Enabled, bool UserEnabled, bool Administrator, IReadOnlyList<Guid> RoleIds, long Revision);
public sealed record IdentityOrganization(Guid OrganizationId, string Name, string Kind, bool Enabled, long Revision);
public sealed record IdentityGrant(string Permission, string ResourceType, string ResourceId, bool TenantWide, IReadOnlyList<Guid> OrganizationIds);
public sealed record IdentityRole(Guid RoleId, string Name, bool Enabled, long Revision, IReadOnlyList<IdentityGrant> Grants);
public sealed record IdentityAudit(Guid EventId, Guid ActorUserId, Guid? TenantId, string EntityType, Guid EntityId, string Action, string Result, string RequestId, DateTimeOffset OccurredAt);
public sealed record IdentityAuditPage(IReadOnlyList<IdentityAudit> Items, bool HasMore);
public sealed record IdentityWorkspace(Guid ActorUserId, bool PlatformAdministrator, IReadOnlyList<IdentityTenant> Tenants, IReadOnlyList<IdentityUser> Users, IdentityAuditPage Audit);
public sealed record IdentityTenantWorkspace(Guid ActorUserId, bool PlatformAdministrator, IdentityTenant Tenant, IReadOnlyList<IdentityMember> Members,
    IReadOnlyList<IdentityOrganization> Organizations, IReadOnlyList<IdentityRole> Roles, IdentityAuditPage Audit);
public sealed record IdentityRevision(long Revision);
public sealed record SaveIdentityUser(string Issuer, string Subject, string DisplayName, bool Enabled, bool PlatformAdministrator, long ExpectedRevision);
public sealed record SaveIdentityTenant(string Name, bool Enabled, long ExpectedRevision);
public sealed record SaveIdentityMember(bool Enabled, bool Administrator, IReadOnlyList<Guid> RoleIds, long ExpectedRevision);
public sealed record SaveIdentityOrganization(string Name, string Kind, bool Enabled, long ExpectedRevision);
public sealed record SaveIdentityRole(string Name, bool Enabled, IReadOnlyList<IdentityGrant> Grants, long ExpectedRevision);
public enum IdentityError { None, Forbidden, Invalid, NotFound, Conflict, Capacity }
public sealed record IdentityResult<T>(T? Value, IdentityError Error)
{
    public static IdentityResult<T> Success(T value) => new(value, IdentityError.None);
    public static IdentityResult<T> Failure(IdentityError error) => new(default, error);
}
public sealed class IdentityManagementUnavailableException : Exception;
public interface IIdentityManagementStore
{
    Task<IdentityResult<IdentityWorkspace>> ReadWorkspaceAsync(SubjectIdentity subject, CancellationToken token);
    Task<IdentityResult<IdentityTenantWorkspace>> ReadTenantAsync(SubjectIdentity subject, Guid tenant, Guid? selectedTenant, CancellationToken token);
    Task<IdentityResult<IdentityRevision>> SaveUserAsync(SubjectIdentity subject, Guid id, SaveIdentityUser value, string requestId, Func<CancellationToken, Task<bool>> currentSession, CancellationToken token);
    Task<IdentityResult<IdentityRevision>> SaveTenantAsync(SubjectIdentity subject, Guid id, SaveIdentityTenant value, string requestId, Func<CancellationToken, Task<bool>> currentSession, CancellationToken token);
    Task<IdentityResult<IdentityRevision>> SaveMemberAsync(SubjectIdentity subject, Guid tenant, Guid? selectedTenant, Guid id, SaveIdentityMember value, string requestId, Func<CancellationToken, Task<bool>> currentSession, CancellationToken token);
    Task<IdentityResult<IdentityRevision>> SaveOrganizationAsync(SubjectIdentity subject, Guid tenant, Guid? selectedTenant, Guid id, SaveIdentityOrganization value, string requestId, Func<CancellationToken, Task<bool>> currentSession, CancellationToken token);
    Task<IdentityResult<IdentityRevision>> SaveRoleAsync(SubjectIdentity subject, Guid tenant, Guid? selectedTenant, Guid id, SaveIdentityRole value, string requestId, Func<CancellationToken, Task<bool>> currentSession, CancellationToken token);
}
