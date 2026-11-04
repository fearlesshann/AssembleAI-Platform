using System.Text.RegularExpressions;
using AssembleAI.Platform.Registry;
using AssembleAI.Platform.Models;
using AssembleAI.Platform.Connectors;

namespace AssembleAI.Platform.Gateway;

public sealed class GatewayOptions
{
    public bool Enabled { get; set; }
    public int LeaseSeconds { get; set; } = 900;
    public int DrainSeconds { get; set; } = 120;
    public int CallSeconds { get; set; } = 300;
    public int ResponseHeadersSeconds { get; set; } = 10;
    public int IdleSeconds { get; set; } = 30;
    public int RecheckSeconds { get; set; } = 2;
    public int CheckTimeoutSeconds { get; set; } = 3;
    public int PerUserConcurrency { get; set; } = 4;
    public int GlobalConcurrency { get; set; } = 64;
    public int RequestsPerMinute { get; set; } = 60;
    public int MaximumPartitions { get; set; } = 10000;
    public string HealthPath { get; set; } = "/health/ready";
    public GatewayOperation[] Operations { get; set; } = [];
    public void Validate()
    {
        if (!Enabled) return;
        if (LeaseSeconds is < 1 or > 3600 || DrainSeconds is < 1 or > 300 || CallSeconds is < 1 or > 600
            || ResponseHeadersSeconds is < 1 or > 60 || IdleSeconds is < 1 or > 120
            || RecheckSeconds is < 1 or > 10 || CheckTimeoutSeconds is < 1 or > 5
            || PerUserConcurrency is < 1 or > 64 || GlobalConcurrency < PerUserConcurrency || GlobalConcurrency > 4096
            || RequestsPerMinute is < 1 or > 10000 || MaximumPartitions is < 1 or > 100000
            || !ValidPath(HealthPath) || Operations is null || Operations.Length > 10000) throw Invalid();
        var keys = new HashSet<(string, string, string, string)>();
        foreach (var item in Operations)
            if (item is null || !ManifestValidator.IsAppId(item.Service) || !ManifestValidator.IsVersion(item.Version)
                || !ValidPath(item.Path) || item.Method is not ("GET" or "POST")
                || item.Permission is null || !Regex.IsMatch(item.Permission, "\\A[a-z][a-z0-9:._-]{0,127}\\z")
                || item.ModelIds is null || item.ModelIds.Length > 100
                || item.ModelIds.Any(model => !ModelGatewayCatalog.ValidId(model))
                || item.ModelIds.Distinct(StringComparer.Ordinal).Count() != item.ModelIds.Length
                || item.ConnectorDatasetIds is null || item.ConnectorDatasetIds.Length > 100
                || item.ConnectorDatasetIds.Any(dataset => !ConnectorCatalog.ValidId(dataset))
                || item.ConnectorDatasetIds.Distinct(StringComparer.Ordinal).Count() != item.ConnectorDatasetIds.Length
                || !keys.Add((item.Service, item.Version, item.Method, item.Path))) throw Invalid();
    }
    public GatewayOperation? Find(ApplicationManifest manifest, string method, string path) => Operations.FirstOrDefault(item =>
        item.Service == manifest.BackendService && item.Version == manifest.BackendVersion && item.Method == method
        && item.Path == path && manifest.Permissions.Contains(item.Permission, StringComparer.Ordinal));
    public static bool ValidPath(string? path) => path is { Length: <= 512 }
        && Regex.IsMatch(path, "\\A/(?:[a-zA-Z0-9_-]+/)*[a-zA-Z0-9_-]+\\z");
    private static InvalidOperationException Invalid() => new("Gateway configuration is invalid.");
}

public sealed record GatewayOperation(string Service, string Version, string Method, string Path, string Permission)
{
    public string[] ModelIds { get; init; } = [];
    public string[] ConnectorDatasetIds { get; init; } = [];
}
