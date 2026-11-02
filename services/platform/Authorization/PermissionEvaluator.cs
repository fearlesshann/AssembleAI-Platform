namespace AssembleAI.Platform.Authorization;

public static class PermissionEvaluator
{
    public static bool Allows(AccessSnapshot? snapshot, string permission, ProtectedResource resource)
    {
        if (snapshot is null || resource is null || snapshot.UserId == Guid.Empty
            || snapshot.TenantId == Guid.Empty || snapshot.AuthorizationVersion < 1
            || snapshot.TenantId != resource.TenantId
            || string.IsNullOrWhiteSpace(permission)
            || string.IsNullOrWhiteSpace(resource.Type) || string.IsNullOrWhiteSpace(resource.Id))
            return false;

        // A tenant-wide grant still cannot access a disabled/foreign organization.
        if (resource.OrganizationId is { } organizationId
            && (organizationId == Guid.Empty || !snapshot.EnabledOrganizationIds.Contains(organizationId)))
            return false;

        // Keep each grant's permission, resource and organization scope together.
        // Combining a permission from one grant with another grant's scope would expand authority.
        return snapshot.Grants.Any(grant =>
            string.Equals(grant.Permission, permission, StringComparison.Ordinal)
            && string.Equals(grant.ResourceType, resource.Type, StringComparison.Ordinal)
            && string.Equals(grant.ResourceId, resource.Id, StringComparison.Ordinal)
            && (grant.TenantWide || (resource.OrganizationId is { } id && grant.OrganizationIds.Contains(id))));
    }
}
