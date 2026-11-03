using System.Text.Json;
using System.Text.Json.Nodes;
using AssembleAI.Platform.Registry;
using Microsoft.Extensions.Configuration;

namespace AssembleAI.Platform.SmokeTests;

public static class ManifestTests
{
    public static ApplicationManifest Valid(string appId = "production-reports", string version = "1.0.0") => new(
        1, appId, "生产报表", version, $"/apps/{appId}/{version}/", "production-api", ["production:read"], "draft",
        new(version, new string('a', 64)), "1.0.0", "生产", "生产指标与报表");

    public static void Run()
    {
        var valid = ManifestValidator.Serialize(Valid());
        Assert(Parse(valid), "A complete Unicode manifest must round-trip.");
        var contractDirectory = Path.Combine(AppContext.BaseDirectory, "contracts", "manifests");
        Assert(Parse(File.ReadAllText(Path.Combine(contractDirectory, "production-reports.valid.json"))), "Published valid example must be accepted by the actual API validator.");
        Assert(!Parse(File.ReadAllText(Path.Combine(contractDirectory, "external-entry.invalid.json"))), "Published external-entry example must be rejected.");
        foreach (var invalid in new[] { "schemaVersion", "appId", "frontendArtifact", "description" })
        {
            var value = JsonNode.Parse(valid)!.AsObject();
            value.Remove(invalid);
            Assert(!Parse(value.ToJsonString()), "Missing required field: " + invalid);
        }
        foreach (var (field, value) in new (string, JsonNode?)[]
        {
            ("schemaVersion", JsonValue.Create("1")), ("schemaVersion", JsonValue.Create(2)),
            ("appId", JsonValue.Create("Production")), ("version", JsonValue.Create("01.0.0")),
            ("version", JsonValue.Create("1.0.0-beta")), ("name", JsonValue.Create(" name ")),
            ("name", JsonValue.Create("line\nbreak")), ("name", null), ("description", null),
            ("frontendEntry", JsonValue.Create("https://evil.example/app/")),
            ("frontendEntry", JsonValue.Create("/apps/production-reports/%2e%2e/")),
            ("frontendEntry", JsonValue.Create("/apps/other/1.0.0/")),
            ("backendService", JsonValue.Create("http://localhost:9000")),
            ("status", JsonValue.Create("published")), ("permissions", new JsonArray()),
            ("permissions", new JsonArray("production:read", "production:read")),
            ("permissions", new JsonArray("production:read", (JsonNode?)null)),
            ("frontendArtifact", null), ("unknown", JsonValue.Create(true))
        })
        {
            var document = JsonNode.Parse(valid)!.AsObject();
            document[field] = value;
            Assert(!Parse(document.ToJsonString()), "Invalid manifest field must be rejected: " + field);
        }
        Assert(!Parse(valid.Insert(1, "\"schemaVersion\":1,")), "Duplicate JSON names must be rejected before normalization.");
        Assert(!Parse(valid.Replace("\"sha256\":", "\"extra\":true,\"sha256\":")), "Nested unknown fields must be rejected.");
        Assert(!Parse(valid.Replace(new string('a', 64), new string('A', 64))), "Digest must be canonical lowercase SHA-256.");
        Assert(!Parse("[" + valid + "]"), "The manifest root must be an object.");
        Assert(!Parse(valid.Insert(1, new string(' ', 32768))), "The wire manifest size is bounded.");
        var settings = CatalogSettings(Valid());
        var catalog = Catalog(settings);
        Assert(catalog.Allows(Valid() with { Status = "pending_review" }), "Approved deployment must match submitted release.");
        Assert(!catalog.Allows(Valid() with { BackendVersion = "2.0.0" }), "Unconfigured backend versions must fail closed.");
        Assert(!catalog.Allows(Valid() with { FrontendArtifact = new("1.0.0", new string('b', 64)) }), "Artifact hash mismatch must fail closed.");
        Assert(!Catalog(new()).Allows(Valid()), "Empty deployment configuration must not publish anything.");
        foreach (var destination in new[] { "file:///tmp/service", "https://user:pass@service/", "https://service/?token=secret", "https://service/#fragment", "https:\\service" })
        {
            var bad = new Dictionary<string, string?>(settings) { ["Registry:BackendBindings:0:Destination"] = destination };
            try { Catalog(bad); throw new Exception("Invalid deployment configuration was accepted."); }
            catch (InvalidOperationException e) { Assert(e.Message == "Registry deployment configuration is invalid.", "Configuration errors must not disclose destinations."); }
        }
        var duplicate = new Dictionary<string, string?>(settings);
        foreach (var pair in settings.Where(p => p.Key.StartsWith("Registry:FrontendArtifacts:0:", StringComparison.Ordinal)))
            duplicate[pair.Key.Replace(":0:", ":1:")] = pair.Value;
        try { Catalog(duplicate); throw new Exception("Duplicate artifact declaration was accepted."); }
        catch (InvalidOperationException) { }
        Console.WriteLine("PASS: manifest contract: strict JSON types/fields, bounded paths and permissions, immutable artifact identity and trusted deployment matching.");
    }

    public static Dictionary<string, string?> CatalogSettings(ApplicationManifest manifest) => new()
    {
        ["Registry:FrontendArtifacts:0:AppId"] = manifest.AppId,
        ["Registry:FrontendArtifacts:0:Version"] = manifest.FrontendArtifact.Version,
        ["Registry:FrontendArtifacts:0:Entry"] = manifest.FrontendEntry,
        ["Registry:FrontendArtifacts:0:Sha256"] = manifest.FrontendArtifact.Sha256,
        ["Registry:BackendBindings:0:Service"] = manifest.BackendService,
        ["Registry:BackendBindings:0:Version"] = manifest.BackendVersion,
        ["Registry:BackendBindings:0:Audience"] = "assembleai:production-api",
        ["Registry:BackendBindings:0:Destination"] = "http://127.0.0.1:9876/"
    };
    private static RegistryDeploymentCatalog Catalog(Dictionary<string, string?> settings) => new(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    private static bool Parse(string value)
    {
        using var json = JsonDocument.Parse(value);
        return ManifestValidator.TryParse(json.RootElement, out _);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
