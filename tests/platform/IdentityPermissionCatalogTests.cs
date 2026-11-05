using System.Text.Json;
using AssembleAI.Platform.IdentityManagement;
using AssembleAI.Platform.Connectors;
using AssembleAI.Platform.Models;

namespace AssembleAI.Platform.SmokeTests;

public static class IdentityPermissionCatalogTests
{
    public static void Run()
    {
        var tenant = Guid.NewGuid();
        var models = new ModelGatewayOptions { Enabled = true, Catalog = { ["assistant"] = new() { DisplayName = "工厂助手", UpstreamModel = "PRIVATE_MODEL", Provider = "PRIVATE_PROVIDER", Enabled = true } },
            Providers = { ["PRIVATE_PROVIDER"] = new() { Endpoint = "https://private.example/v1", CredentialEnvironmentVariable = "PRIVATE_CREDENTIAL" } } };
        var connectors = new ConnectorOptions { Enabled = true,
            Sources = { ["factory"] = new() { DisplayName = "工厂数据库", ConnectionEnvironmentVariable = "PRIVATE_CONNECTION", Enabled = true } },
            Datasets = { ["production"] = new() { SourceId = "factory", DisplayName = "每日生产", Schema = "PRIVATE_SCHEMA", Table = "PRIVATE_TABLE" } } };
        var result = IdentityPermissionCatalog.Build(tenant, [new("chatbi", "数据问答", true, ["chatbi:use", "report:use"])], models, connectors);
        var json = JsonSerializer.Serialize(result);
        Assert(result.TenantId == tenant && !json.Contains("PRIVATE_") && !json.Contains("private.example"), "Only public assignment metadata may leave the catalog.");
        IdentityPermissionResource Resource(string type, string id) => result.Resources.Single(resource => resource.ResourceType == type && resource.ResourceId == id);
        Assert(Resource("application", "chatbi").Actions.All(action => !action.OrganizationScope), "Application and tenant management operations require tenant-wide grants.");
        Assert(Resource("application", "chatbi").Actions.Single(action => action.Permission == "chatbi:use").Requires.Single().Permission == "report:use", "Manifest access permissions are conjunctive.");
        Assert(Resource("model", "assistant").Actions.All(action => !action.OrganizationScope), "Model invocation/management is tenant-wide.");
        var query = Resource("dataset", "production").Actions.Single();
        Assert(query.OrganizationScope && query.Requires.Contains(new("datasource:read", "datasource", "factory")) && query.Requires.Contains(new("tool:invoke", "tool", "connector.query")), "Dataset queries disclose their exact scoped prerequisites.");
        Assert(Resource("datasource", "factory").Actions.Single(action => action.Permission == "datasource:test").Requires.Single().ResourceId == "connector.test", "Connection tests disclose the tenant-wide tool dependency.");
        Assert(Resource("knowledge", "documents").Actions.All(action => action.OrganizationScope), "Document permissions retain organization scopes.");
        Assert(result.Resources.SelectMany(resource => resource.Actions.Select(action => (resource.ResourceType, resource.ResourceId, action.Permission))).Distinct().Count() == result.Resources.Sum(resource => resource.Actions.Count), "Catalog contains no duplicate grant keys.");
        var disabled = IdentityPermissionCatalog.Build(tenant, [], new(), new());
        Assert(!disabled.Resources.Any(resource => resource.Category is "models" or "data" or "tools"), "Unavailable configured services do not invent resource metadata.");
        Console.WriteLine("PASS: permission catalog display metadata, action scopes, exact dependencies and secret exclusion.");
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
