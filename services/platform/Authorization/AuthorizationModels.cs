namespace AssembleAI.Platform.Authorization;

// Identity is supplied by the validated authentication handler, never by a request header.
public sealed record SubjectIdentity(string Issuer, string Subject);

public sealed record TenantMembership(Guid TenantId, string Name);

public sealed record PermissionGrant(
    string Permission,
    string ResourceType,
    string ResourceId,
    bool TenantWide,
    IReadOnlySet<Guid> OrganizationIds);

public sealed record AccessSnapshot(
    Guid UserId,
    Guid TenantId,
    long AuthorizationVersion,
    IReadOnlyList<PermissionGrant> Grants,
    IReadOnlySet<Guid> EnabledOrganizationIds);

// A business handler must load these attributes from its own trusted resource store.
// This record must not be bound directly from client JSON, query parameters or headers.
public sealed record ProtectedResource(Guid TenantId, string Type, string Id, Guid? OrganizationId);

public interface IAuthorizationStore
{
    Task<IReadOnlyList<TenantMembership>> ListTenantsAsync(
        SubjectIdentity identity, CancellationToken cancellationToken = default);

    Task<AccessSnapshot?> ReadAccessAsync(
        SubjectIdentity identity, Guid tenantId, CancellationToken cancellationToken = default);
}

public sealed class AuthorizationStoreUnavailableException : Exception
{
    public AuthorizationStoreUnavailableException()
        : base("The authorization store is unavailable.") { }

    public AuthorizationStoreUnavailableException(Exception innerException)
        : base("The authorization store is unavailable.", innerException) { }
}
