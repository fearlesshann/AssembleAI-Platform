using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Connectors;

namespace AssembleAI.Platform.SmokeTests;

public static class ConnectorAuthorizationTests
{
    public static Task RunAsync()
    {
        var tenant = Guid.NewGuid(); var a = Guid.NewGuid(); var b = Guid.NewGuid(); var disabled = Guid.NewGuid();
        PermissionGrant Grant(string permission, string type, string id, params Guid[] orgs) => new(permission,type,id,false,orgs.ToHashSet());
        var grants = new List<PermissionGrant> {
            Grant("datasource:read","datasource","mes",a,b,disabled),
            Grant("dataset:read","dataset","production",a,disabled),
            Grant("tool:invoke","tool","connector.query",a,b,disabled)
        };
        var access = new AccessSnapshot(Guid.NewGuid(),tenant,1,grants,new HashSet<Guid>{a,b});
        Assert(ConnectorAuthorization.Organizations(access,"mes","production").SetEquals([a]), "V5: exact three-way scope intersection before query.");
        grants[2] = Grant("tool:invoke","tool","connector.query",b);
        Assert(ConnectorAuthorization.Organizations(access,"mes","production").Count == 0, "V7: disjoint tool scope grants no data.");
        grants.Add(Grant("dataset:read","dataset","other",b));
        Assert(ConnectorAuthorization.Organizations(access,"mes","production").Count == 0, "V7: another resource cannot donate its scope.");
        grants[2] = new("tool:invoke","tool","connector.query",true,new HashSet<Guid>());
        Assert(ConnectorAuthorization.Organizations(access,"mes","production").SetEquals([a]), "Tenant-wide tool access cannot widen dataset access.");
        Assert(!ConnectorAuthorization.Allows(access,"datasource:manage","datasource","mes"), "Read grants do not confer management.");
        Assert(ConnectorAuthorization.Organizations(null,"mes","production").Count == 0, "Missing membership fails closed.");
        Console.WriteLine("PASS: connector exact resource grants, enabled organizations and three-way scope intersection.");
        return Task.CompletedTask;
    }
    private static void Assert(bool value,string message) { if (!value) throw new InvalidOperationException(message); }
}
