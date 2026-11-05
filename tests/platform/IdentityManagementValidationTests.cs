using System.Text.Json;
using AssembleAI.Platform.IdentityManagement;

namespace AssembleAI.Platform.SmokeTests;

public static class IdentityManagementValidationTests
{
    public static void Run()
    {
        IdentityPermissionCatalogTests.Run();
        var user = """{"issuer":"https://identity.example/realm","subject":"trusted-subject","displayName":"成员","enabled":true,"platformAdministrator":false,"expectedRevision":0}""";
        Assert(Parse<SaveIdentityUser>(user), "Complete explicit user mutation is valid.");
        foreach (var invalidRevision in new[] { "\"0\"", "\"1\"", "0.0", "0e0", "1e0", "null", "false" })
            Assert(!Parse<SaveIdentityUser>(user.Replace("\"expectedRevision\":0", "\"expectedRevision\":" + invalidRevision)),
                "Revision must be an integer JSON number without string coercion, decimals or exponents: " + invalidRevision);
        Assert(!Parse<SaveIdentityUser>(user.Replace("\"enabled\":true,", "")), "Omitted boolean must never become an implicit state mutation.");
        Assert(!Parse<SaveIdentityUser>(user.Replace("\"enabled\":true", "\"enabled\":true,\"enabled\":false")), "Duplicate privilege/state keys are rejected.");
        Assert(!Parse<SaveIdentityUser>(user.Replace("\"subject\":", "\"Subject\":")), "Field names are exact.");
        Assert(!Parse<SaveIdentityUser>(user.Replace("\"expectedRevision\":0", "\"expectedRevision\":0,\"roles\":[\"superuser\"]")), "Unknown fields cannot carry undeclared authority.");
        Assert(!Parse<SaveIdentityUser>(user.Replace("\"expectedRevision\":0", "\"expectedRevision\":9007199254740991")), "Unsafe JSON revisions are rejected.");
        Assert(!Parse<SaveIdentityUser>(user.Replace("trusted-subject", " ")), "Blank identity is rejected.");
        Assert(!Parse<SaveIdentityUser>(user.Replace("https://identity.example/realm", "https://user:secret@identity.example/realm")), "Issuer URLs cannot carry credentials.");
        Assert(Parse<SaveIdentityTenant>("""{"name":"工厂","enabled":true,"expectedRevision":0}"""), "Tenant input is supported.");
        Assert(!Parse<SaveIdentityTenant>("""{"name":null,"enabled":true,"expectedRevision":0}"""), "Null names are rejected.");
        var id = Guid.NewGuid();
        var grant = new IdentityGrant("mes:production:read", "dataset", "daily-production", false, [id]);
        Assert(IdentityManagementValidation.Valid(new SaveIdentityRole("查看产量", true, [grant], 0)), "Exact scoped resource grant is valid.");
        Assert(!IdentityManagementValidation.Valid(new SaveIdentityRole("查看产量", true, [grant with { ResourceId = "*" }], 0)), "Wildcard resource grants are rejected.");
        Assert(!IdentityManagementValidation.Valid(new SaveIdentityRole("查看产量", true, [grant with { OrganizationIds = [] }], 0)), "Non-tenant grants require explicit scopes.");
        Assert(!IdentityManagementValidation.Valid(new SaveIdentityRole("查看产量", true, [grant with { TenantWide = true }], 0)), "Tenant-wide and organization scopes cannot mix.");
        Assert(!IdentityManagementValidation.Valid(new SaveIdentityRole("查看产量", true, [grant, grant], 0)), "Duplicate grant resources are rejected.");
        Assert(!IdentityManagementValidation.Valid(new SaveIdentityMember(true, false, [id, id], 0)), "Duplicate role assignments are rejected.");
        Assert(!IdentityManagementValidation.Valid(new SaveIdentityMember(true, false, [Guid.Empty], 0)), "Empty role IDs are rejected.");
        Assert(!IdentityManagementValidation.Valid(new SaveIdentityOrganization("工厂", "Factory", true, 0)), "Organization kinds use the storage grammar.");
        Console.WriteLine("PASS: identity management strict state, revision, identity, role/resource and organization input validation.");
    }
    private static bool Parse<T>(string json)
    { using var document = JsonDocument.Parse(json); return IdentityManagementValidation.TryParse<T>(document.RootElement, out _); }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
