using AssembleAI.Platform.Authorization;

namespace AssembleAI.Platform.Connectors;

public static class ConnectorAuthorization
{
    public static bool Allows(AccessSnapshot? access, string permission, string type, string id) =>
        access is not null && PermissionEvaluator.Allows(access, permission, new(access.TenantId,type,id,null));

    public static IReadOnlySet<Guid> Organizations(AccessSnapshot? access, string sourceId, string datasetId)
    {
        if (access is null) return new HashSet<Guid>();
        return access.EnabledOrganizationIds.Where(org =>
            PermissionEvaluator.Allows(access,"datasource:read",new(access.TenantId,"datasource",sourceId,org))
            && PermissionEvaluator.Allows(access,"dataset:read",new(access.TenantId,"dataset",datasetId,org))
            && PermissionEvaluator.Allows(access,"tool:invoke",new(access.TenantId,"tool","connector.query",org))).ToHashSet();
    }
    public static bool CanTest(AccessSnapshot? access, string sourceId) =>
        Allows(access,"datasource:test","datasource",sourceId)
        && Allows(access,"tool:invoke","tool","connector.test");
}
