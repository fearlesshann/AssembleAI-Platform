using AssembleAI.Platform.Authorization;

namespace AssembleAI.Platform.Catalog;

// Display data only. Service addresses, permissions and frontend loading contracts
// belong to protected runtime admission, not the browse response.
public sealed record CatalogApplication(string AppId, string Name, string ReleaseVersion, string Category, string Description);
public sealed record CatalogListing(IReadOnlyList<CatalogApplication> Items, IReadOnlyList<string> Categories);
public sealed record CatalogResult<T>(bool IsMember, T? Value);

public interface ICatalogStore
{
    Task<CatalogResult<CatalogListing>> ListAsync(SubjectIdentity subject, Guid tenantId, CancellationToken token = default);
    Task<CatalogResult<CatalogApplication>> ReadAsync(SubjectIdentity subject, Guid tenantId, string appId, CancellationToken token = default);
}

public sealed class CatalogStoreUnavailableException : Exception
{
    public CatalogStoreUnavailableException() : base("The application catalog is unavailable.") { }
}
