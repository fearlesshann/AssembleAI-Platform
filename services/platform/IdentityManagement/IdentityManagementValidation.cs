using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AssembleAI.Platform.IdentityManagement;

public static class IdentityManagementValidation
{
    private static readonly Regex Slug = new("\\A[a-z][a-z0-9._-]{0,63}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex Permission = new("\\A[a-z][a-z0-9:._-]{0,127}\\z", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true
    };
    public static bool TryParse<T>(JsonElement json, out T? value)
    {
        value = default;
        if (!UniqueFields(json)) return false;
        try { value = json.Deserialize<T>(Json); return value is not null && Valid(value); }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException) { return false; }
    }
    public static bool Valid(object value) => value switch
    {
        SaveIdentityUser user => Text(user.Issuer, 2048) && Uri.TryCreate(user.Issuer, UriKind.Absolute, out var issuer)
            && issuer.Scheme is "http" or "https" && issuer.UserInfo.Length == 0 && issuer.Fragment.Length == 0 && issuer.Query.Length == 0
            && Text(user.Subject, 512) && Text(user.DisplayName, 256) && Revision(user.ExpectedRevision),
        SaveIdentityTenant tenant => Text(tenant.Name, 256) && Revision(tenant.ExpectedRevision),
        SaveIdentityMember member => Ids(member.RoleIds, 100) && Revision(member.ExpectedRevision),
        SaveIdentityOrganization org => Text(org.Name, 256) && org.Kind is not null && Slug.IsMatch(org.Kind) && Revision(org.ExpectedRevision),
        SaveIdentityRole role => Text(role.Name, 128) && Revision(role.ExpectedRevision) && role.Grants is { Count: <= 100 }
            && role.Grants.All(Grant) && role.Grants.Select(grant => (grant.Permission, grant.ResourceType, grant.ResourceId)).Distinct().Count() == role.Grants.Count,
        _ => false
    };
    private static bool Grant(IdentityGrant grant) => grant is not null && grant.Permission is not null && Permission.IsMatch(grant.Permission)
        && grant.ResourceType is not null && Slug.IsMatch(grant.ResourceType) && Text(grant.ResourceId, 256) && !grant.ResourceId.Contains('*')
        && Ids(grant.OrganizationIds, 100) && (grant.TenantWide ? grant.OrganizationIds.Count == 0 : grant.OrganizationIds.Count > 0);
    private static bool Ids(IReadOnlyList<Guid>? ids, int maximum) => ids is not null && ids.Count <= maximum
        && ids.All(id => id != Guid.Empty) && ids.Distinct().Count() == ids.Count;
    private static bool Text(string? value, int max) => value is not null && value.Length > 0 && value.EnumerateRunes().Count() <= max
        && value == value.Trim() && !value.Any(char.IsControl);
    private static bool Revision(long value) => value >= 0 && value < 9007199254740991L;
    private static bool UniqueFields(JsonElement json) => json.ValueKind switch
    {
        JsonValueKind.Object => json.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() == json.EnumerateObject().Count()
            && json.EnumerateObject().All(property => UniqueFields(property.Value)),
        JsonValueKind.Array => json.EnumerateArray().All(UniqueFields),
        _ => true
    };
}
