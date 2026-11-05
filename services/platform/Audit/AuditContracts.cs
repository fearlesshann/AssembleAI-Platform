namespace AssembleAI.Platform.Audit;

public sealed record AuditEvent(string Id, string Source, Guid? TenantId, Guid ActorUserId, string ResourceType,
    string ResourceId, string Action, string Outcome, string Status, string RequestId, DateTimeOffset OccurredAt,
    string? AppId, string? ReleaseVersion, Guid? RunId, string? GatewayRequestId, IReadOnlyDictionary<string,string?> Details);
public sealed record AuditPolicy(int MaximumRangeDays = 31, int DefaultPageSize = 50, int MaximumPageSize = 100,
    int MaximumExportRecords = 1000, bool AutomaticRetention = false);
public sealed record AuditWorkspace(string Scope, Guid? TenantId, Guid ActorUserId, IReadOnlyList<string> Sources,
    bool CanExport, bool GlobalAvailable, AuditPolicy Policy);
public sealed record AuditPage(IReadOnlyList<AuditEvent> Items, string? NextCursor);
public sealed record AuditExport(IReadOnlyList<AuditEvent> Items, bool Truncated, DateTimeOffset ExportedAt);
public sealed class AuditRequestException(int status) : Exception("The audit request could not complete.") { public int Status { get; } = status; }
