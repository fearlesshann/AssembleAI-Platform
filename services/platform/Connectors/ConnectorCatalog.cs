using System.Text.RegularExpressions;
using AssembleAI.Platform.Gateway;

namespace AssembleAI.Platform.Connectors;

public sealed class ConnectorCatalog
{
    public ConnectorOptions Options { get; }
    public GatewayAdmission Admission { get; }

    public ConnectorCatalog(IConfiguration configuration, TimeProvider clock)
    {
        try
        {
            Options = configuration.GetSection("Connectors").Get<ConnectorOptions>() ?? new();
            if (Options.Sources is null || Options.Datasets is null || Options.Sources.Count > 100 || Options.Datasets.Count > 100)
                throw Invalid();
            // Rebuild with ordinal comparison even when a configuration provider is case-insensitive.
            Options.Sources = new(Options.Sources, StringComparer.Ordinal);
            Options.Datasets = new(Options.Datasets, StringComparer.Ordinal);
            foreach (var (id, source) in Options.Sources)
                if (!ValidId(id) || !ValidSource(source) || string.IsNullOrWhiteSpace(source.DisplayName)) throw Invalid();
            foreach (var (id, dataset) in Options.Datasets)
            {
                if (!ValidId(id) || !ValidDataset(dataset) || string.IsNullOrWhiteSpace(dataset.DisplayName)
                    || !Options.Sources.ContainsKey(dataset.SourceId)) throw Invalid();
                dataset.Columns = new(dataset.Columns, StringComparer.Ordinal);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        { throw Invalid(); }
        Admission = new GatewayAdmission(clock, 2, 32, 60);
    }

    public ConnectorSource? FindSource(string id) => Options.Enabled && ValidId(id)
        && Options.Sources.TryGetValue(id, out var source) ? source : null;
    public ConnectorDataset? FindDataset(string id) => Options.Enabled && ValidId(id)
        && Options.Datasets.TryGetValue(id, out var dataset) ? dataset : null;
    public static bool ValidId(string? id) => id is not null && Regex.IsMatch(id, "\\A[a-z][a-z0-9._-]{0,63}\\z");
    internal static bool PhysicalIdentifier(string? value) => value is not null
        && Regex.IsMatch(value, "\\A[A-Za-z_][A-Za-z0-9_]{0,62}\\z");
    internal static bool ValidSource(ConnectorSource? source) => source is not null
        && source.MaximumRows is >= 1 and <= 500 && source.TimeoutSeconds is >= 1 and <= 30
        && source.ConnectionEnvironmentVariable is not null
        && Regex.IsMatch(source.ConnectionEnvironmentVariable, "\\A[A-Za-z_][A-Za-z0-9_]{0,127}\\z")
        && DisplayName(source.DisplayName);
    internal static bool ValidDataset(ConnectorDataset? dataset)
    {
        if (dataset is null || !ValidId(dataset.SourceId) || !DisplayName(dataset.DisplayName)
            || !PhysicalIdentifier(dataset.Schema) || !PhysicalIdentifier(dataset.Table)
            || !PhysicalIdentifier(dataset.TenantColumn) || !PhysicalIdentifier(dataset.OrganizationColumn)
            || dataset.TenantColumn == dataset.OrganizationColumn || dataset.Columns is null || dataset.Columns.Count is < 1 or > 32)
            return false;
        var physical = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, column) in dataset.Columns)
            if (!ValidId(id) || column is null || !PhysicalIdentifier(column.Column)
                || column.Type is not ("text" or "integer" or "decimal" or "date")
                || column.Column == dataset.TenantColumn || column.Column == dataset.OrganizationColumn
                || !physical.Add(column.Column)) return false;
        return true;
    }
    private static bool DisplayName(string? value) => value is not null && value.Length <= 200 && !value.Any(char.IsControl);
    private static InvalidOperationException Invalid() => new("Connector configuration is invalid.");
}
