using AssembleAI.Platform.Registry;

namespace AssembleAI.Platform.InternalCalls;

public interface IApplicationCallStore
{
    // Only a globally published release in an enabled tenant application. The
    // current pointer may differ during T5 draining; it is not a lease validator.
    Task<ApplicationManifest?> ReadAsync(Guid tenantId, string appId, string releaseVersion, CancellationToken token);
}
public sealed class ApplicationCallStoreUnavailableException : Exception;
