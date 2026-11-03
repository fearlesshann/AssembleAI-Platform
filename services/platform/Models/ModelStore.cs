namespace AssembleAI.Platform.Models;

public sealed record TenantModelConfiguration(Guid TenantId, string ModelId, bool Enabled, int MaxOutputTokens, long Revision);

public sealed record ModelCallRecord(Guid CallId, Guid TenantId, Guid UserId, string ModelId, string RequestId,
    string Outcome, long ElapsedMilliseconds, ModelUsage? Usage, DateTimeOffset StartedAt);

/// <summary>Tenant configuration and metadata-only model call history. Callers enforce current model permissions.</summary>
public interface IModelStore
{
    Task<TenantModelConfiguration?> ReadConfigurationAsync(Guid tenantId, string modelId, CancellationToken token);
    /// <summary>Expected revision zero creates; a null result indicates a concurrent edit.</summary>
    Task<TenantModelConfiguration?> SaveConfigurationAsync(Guid tenantId, string modelId, bool enabled, int maxOutputTokens,
        long expectedRevision, Guid actorUserId, string requestId, CancellationToken token);
    Task<Guid> BeginCallAsync(Guid tenantId, Guid userId, string modelId, string requestId, CancellationToken token);
    Task CompleteCallAsync(Guid callId, string outcome, long elapsedMilliseconds, ModelUsage? usage, CancellationToken token);
    Task<IReadOnlyList<ModelCallRecord>> ListCallsAsync(Guid tenantId, string modelId, CancellationToken token);
}

public sealed class ModelStoreUnavailableException() : Exception("Model storage is unavailable.");
