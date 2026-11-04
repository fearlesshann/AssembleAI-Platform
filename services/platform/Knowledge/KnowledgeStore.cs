using AssembleAI.Platform.Authorization;

namespace AssembleAI.Platform.Knowledge;

public sealed record KnowledgeDocument(Guid TenantId, Guid DocumentId, string FileName, string MediaType,
    long SizeBytes, string Sha256, Guid? OrganizationId, string Status, long Revision, Guid CreatedBy, DateTimeOffset CreatedAt,
    string? FailureCode = null);
public sealed record KnowledgeJob(Guid JobId, Guid TenantId, Guid DocumentId, string Kind, long DocumentRevision, string Status, int Attempts);
public sealed record KnowledgeJobLease(KnowledgeJob Job, Guid LeaseId);
public sealed record KnowledgeChunk(int Index, string Text, string Locator);

// HTTP callers enforce current resource permissions; the store provides atomic durable lifecycle changes.
public interface IKnowledgeStore
{
    Task<KnowledgeDocument> CreateAsync(KnowledgeDocument document, string requestId, CancellationToken token);
    Task<KnowledgeDocument?> ReadAsync(Guid tenant, Guid document, CancellationToken token);
    Task<IReadOnlyList<KnowledgeDocument>> ListAsync(Guid tenant, CancellationToken token);
    Task<bool> DeleteAsync(Guid tenant, Guid document, long expectedRevision, Guid actor, string requestId, CancellationToken token);
}

// Internal worker contract, never exposed as a browser API. An unexpired lease and matching document revision
// are required for each transition. Object-store deletion must succeed before CompletePurgeAsync is called.
public interface IKnowledgeJobStore
{
    Task<IReadOnlyList<KnowledgeJob>> PendingAsync(CancellationToken token);
    Task<SubjectIdentity?> ReadCreatorAsync(Guid userId, CancellationToken token);
    Task<KnowledgeJobLease?> ClaimAsync(Guid jobId, CancellationToken token);
    Task<bool> CompleteIngestAsync(KnowledgeJobLease lease, IReadOnlyList<KnowledgeChunk> chunks, CancellationToken token);
    Task<bool> CompletePurgeAsync(KnowledgeJobLease lease, CancellationToken token);
    Task<bool> FailAsync(KnowledgeJobLease lease, CancellationToken token, string failureCode = "ingestion_failed");
}

public sealed class KnowledgeStoreUnavailableException() : Exception("Knowledge storage is unavailable.");
