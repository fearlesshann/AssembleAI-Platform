using System.Text.Json;
using AssembleAI.Platform.Models;

namespace AssembleAI.Platform.Connectors;

public sealed class ConnectorOptions
{
    public bool Enabled { get; set; }
    public Dictionary<string, ConnectorSource> Sources { get; set; } = [];
    public Dictionary<string, ConnectorDataset> Datasets { get; set; } = [];
}
public sealed class ConnectorSource
{
    public string DisplayName { get; set; } = "";
    public string ConnectionEnvironmentVariable { get; set; } = "";
    public bool Enabled { get; set; }
    public int MaximumRows { get; set; } = 500;
    public int TimeoutSeconds { get; set; } = 10;
}
public sealed class ConnectorDataset
{
    public string SourceId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Schema { get; set; } = "";
    public string Table { get; set; } = "";
    public string TenantColumn { get; set; } = "tenant_id";
    public string OrganizationColumn { get; set; } = "factory_id";
    public Dictionary<string, ConnectorColumn> Columns { get; set; } = [];
}
public sealed class ConnectorColumn
{
    public string Column { get; set; } = "";
    public string Type { get; set; } = "text";
}
public sealed record ConnectorFilter(string Column, string Operator, JsonElement Value);
public sealed record ConnectorMetric(string Function, string? Column);
public sealed record ConnectorQuery(IReadOnlyList<string> Columns, IReadOnlyList<ConnectorFilter> Filters,
    IReadOnlyList<ConnectorMetric> Metrics, int Limit);
public sealed record ConnectorResult(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows);
public sealed record TenantConnectorConfiguration(Guid TenantId, string SourceId, bool Enabled, int MaxRows, int TimeoutSeconds, long Revision);
public sealed record ConnectorCallRecord(Guid CallId, Guid TenantId, Guid UserId, string SourceId, string? DatasetId,
    string Operation, string RequestId, string Outcome, int? RowCount, long ElapsedMilliseconds, DateTimeOffset StartedAt, ModelCallSource? Source = null);
public interface IConnectorStore
{
    Task<TenantConnectorConfiguration?> ReadConfigurationAsync(Guid tenantId, string sourceId, CancellationToken token);
    Task<TenantConnectorConfiguration?> SaveConfigurationAsync(Guid tenantId, string sourceId, bool enabled, int maxRows,
        int timeoutSeconds, long expectedRevision, Guid actorUserId, string requestId, CancellationToken token);
    Task<Guid> BeginCallAsync(Guid tenantId, Guid userId, string sourceId, string? datasetId, string operation, string requestId, CancellationToken token, ModelCallSource? source = null);
    Task CompleteCallAsync(Guid callId, string outcome, int? rowCount, long elapsedMilliseconds, CancellationToken token);
    Task<IReadOnlyList<ConnectorCallRecord>> ListCallsAsync(Guid tenantId, string sourceId, CancellationToken token);
}
public interface IConnectorClient
{
    Task TestAsync(ConnectorSource source, IReadOnlyList<ConnectorDataset> datasets, int timeoutSeconds, CancellationToken token);
    Task<ConnectorResult> QueryAsync(ConnectorSource source, ConnectorDataset dataset, ConnectorQuery query,
        Guid tenantId, IReadOnlySet<Guid> organizations, int timeoutSeconds, CancellationToken token);
}
public sealed class ConnectorUnavailableException : Exception
{
    public ConnectorUnavailableException() : base("The data connector is unavailable.") { }
}
public sealed class ConnectorLimitException : Exception
{
    public ConnectorLimitException() : base("The data connector limit was exceeded.") { }
}
