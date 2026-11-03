namespace AssembleAI.Platform.Registry;

public sealed record FrontendArtifactReference(string Version, string Sha256);
public sealed record ApplicationManifest(int SchemaVersion, string AppId, string Name, string Version,
    string FrontendEntry, string BackendService, IReadOnlyList<string> Permissions, string Status,
    FrontendArtifactReference FrontendArtifact, string BackendVersion, string Category, string Description);

public sealed record ApplicationReleaseView(ApplicationManifest Manifest, long Revision, Guid OwnerUserId,
    Guid? SubmittedBy, DateTimeOffset? PublishedAt);
public sealed record TenantApplicationView(Guid TenantId, string AppId, string ReleaseVersion,
    bool Enabled, bool Available, long Revision, ApplicationManifest Manifest);

public enum RegistryError { None, Forbidden, NotFound, Conflict, InvalidManifest, DeploymentNotAllowed }
public sealed record RegistryResult<T>(T? Value, RegistryError Error)
{
    public static RegistryResult<T> Success(T value) => new(value, RegistryError.None);
    public static RegistryResult<T> Failure(RegistryError error) => new(default, error);
}

public sealed class RegistryStoreUnavailableException : Exception
{
    public RegistryStoreUnavailableException() : base("The application registry is unavailable.") { }
}
