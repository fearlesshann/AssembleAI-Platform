using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AssembleAI.Platform.Registry;

public static class ManifestValidator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict
    };
    private static readonly Regex Slug = new("\\A[a-z][a-z0-9-]{0,63}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex Version = new("\\A(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\z", RegexOptions.CultureInvariant);
    private static readonly Regex Permission = new("\\A[a-z][a-z0-9:._-]{0,127}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex Digest = new("\\A[a-f0-9]{64}\\z", RegexOptions.CultureInvariant);
    public static bool IsAppId(string? value) => value is not null && Slug.IsMatch(value);
    public static bool IsVersion(string? value) => value is { Length: <= 64 } && Version.IsMatch(value);
    public static bool IsDigest(string? value) => value is not null && Digest.IsMatch(value);
    public static string Serialize(ApplicationManifest manifest) => JsonSerializer.Serialize(manifest, Json);

    public static bool TryParse(JsonElement json, out ApplicationManifest? manifest)
    {
        manifest = null;
        if (Encoding.UTF8.GetByteCount(json.GetRawText()) > 32768
            || !HasFields(json, "schemaVersion", "appId", "name", "version", "frontendEntry", "backendService",
                "permissions", "status", "frontendArtifact", "backendVersion", "category", "description")) return false;
        try
        {
            if (!HasFields(json.GetProperty("frontendArtifact"), "version", "sha256")) return false;
            manifest = json.Deserialize<ApplicationManifest>(Json);
            if (manifest is not null && IsValid(manifest)) return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException) { }
        manifest = null;
        return false;
    }

    public static bool IsValid(ApplicationManifest? manifest) => manifest is not null && manifest.SchemaVersion == 1
        && IsAppId(manifest.AppId) && IsVersion(manifest.Version) && Text(manifest.Name, 1, 128)
        && Text(manifest.Category, 1, 64) && Text(manifest.Description, 0, 2000)
        && IsAppId(manifest.BackendService) && IsVersion(manifest.BackendVersion) && manifest.Status == "draft"
        && manifest.FrontendArtifact is not null && IsVersion(manifest.FrontendArtifact.Version)
        && IsDigest(manifest.FrontendArtifact.Sha256)
        && manifest.FrontendEntry == $"/apps/{manifest.AppId}/{manifest.FrontendArtifact.Version}/"
        && manifest.Permissions is { Count: >= 1 and <= 32 }
        && manifest.Permissions.All(value => value is not null && Permission.IsMatch(value))
        && manifest.Permissions.Distinct(StringComparer.Ordinal).Count() == manifest.Permissions.Count;

    internal static bool Text(string? value, int minimum, int maximum) => value is not null
        && value.EnumerateRunes().Count() >= minimum && value.EnumerateRunes().Count() <= maximum
        && value == value.Trim() && !value.Any(char.IsControl);

    internal static bool HasFields(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(property => names.Add(property.Name)) && names.SetEquals(expected);
    }
}
