namespace AssembleAI.Platform.Registry;

/// <summary>Trusted deployment declarations; this catalog does not probe health or fetch caller URLs.</summary>
public sealed class RegistryDeploymentCatalog
{
    private readonly Dictionary<(string, string), FrontendDeployment> frontends = [];
    private readonly Dictionary<(string, string), BackendBinding> backends = [];

    public RegistryDeploymentCatalog(IConfiguration configuration)
    {
        try
        {
            var frontend = configuration.GetSection("Registry:FrontendArtifacts").Get<FrontendDeployment[]>() ?? [];
            var backend = configuration.GetSection("Registry:BackendBindings").Get<BackendBinding[]>() ?? [];
            if (frontend.Length > 10000 || backend.Length > 10000) throw Invalid();
            foreach (var item in frontend)
            {
                if (item is null || !ManifestValidator.IsAppId(item.AppId) || !ManifestValidator.IsVersion(item.Version)
                    || !ManifestValidator.IsDigest(item.Sha256) || item.Entry != $"/apps/{item.AppId}/{item.Version}/"
                    || !frontends.TryAdd((item.AppId, item.Version), item)) throw Invalid();
            }
            foreach (var item in backend)
            {
                if (item is null || !ManifestValidator.IsAppId(item.Service) || !ManifestValidator.IsVersion(item.Version)
                    || !ManifestValidator.Text(item.Audience, 1, 256) || !ManifestValidator.Text(item.Destination, 1, 2048)
                    || item.Destination.Contains('\\') || !Uri.TryCreate(item.Destination, UriKind.Absolute, out var uri)
                    || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0
                    || uri.Fragment.Length != 0 || !backends.TryAdd((item.Service, item.Version), item)) throw Invalid();
            }
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or FormatException)
        {
            throw Invalid();
        }
    }

    public bool Allows(ApplicationManifest manifest) => ManifestValidator.IsValid(manifest with { Status = "draft" })
        && frontends.TryGetValue((manifest.AppId, manifest.FrontendArtifact.Version), out var frontend)
        && frontend.Entry == manifest.FrontendEntry && frontend.Sha256 == manifest.FrontendArtifact.Sha256
        && backends.ContainsKey((manifest.BackendService, manifest.BackendVersion));

    // The destination is for trusted server routing code, never a browser response DTO.
    public BackendBinding? ResolveBackend(string service, string version) => backends.GetValueOrDefault((service, version));
    private static InvalidOperationException Invalid() => new("Registry deployment configuration is invalid.");
    public sealed record FrontendDeployment(string AppId, string Version, string Entry, string Sha256);
    public sealed record BackendBinding(string Service, string Version, string Audience, string Destination);
}
