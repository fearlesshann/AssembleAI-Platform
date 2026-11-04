using System.Text.RegularExpressions;
using AssembleAI.Platform.Gateway;

namespace AssembleAI.Platform.Models;

public sealed class ModelGatewayOptions
{
    public bool Enabled { get; set; }
    public int CallSeconds { get; set; } = 120;
    public int RecheckMilliseconds { get; set; } = 1000;
    public int PerUserConcurrency { get; set; } = 2;
    public int GlobalConcurrency { get; set; } = 32;
    public int RequestsPerMinute { get; set; } = 30;
    public Dictionary<string, ModelProviderConfiguration> Providers { get; set; } = [];
    public Dictionary<string, ModelDefinition> Catalog { get; set; } = [];
}

public sealed class ModelProviderConfiguration
{
    public string Endpoint { get; set; } = "";
    public string? CredentialEnvironmentVariable { get; set; }
    public bool AllowInsecureHttp { get; set; }
}

public sealed class ModelDefinition
{
    public string Provider { get; set; } = "";
    public string UpstreamModel { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Enabled { get; set; }
    public int MaximumOutputTokens { get; set; } = 4096;
    public string? ReasoningEffort { get; set; }
}

public sealed class ModelGatewayCatalog
{
    public ModelGatewayOptions Options { get; }
    public GatewayAdmission Admission { get; }
    public ModelGatewayCatalog(IConfiguration configuration, TimeProvider clock)
    {
        Options = configuration.GetSection("Models").Get<ModelGatewayOptions>() ?? new();
        if (Options.Enabled)
        {
            if (Options.CallSeconds is < 1 or > 600 || Options.RecheckMilliseconds is < 50 or > 5000
                || Options.PerUserConcurrency is < 1 or > 64 || Options.GlobalConcurrency < Options.PerUserConcurrency
                || Options.GlobalConcurrency > 4096 || Options.RequestsPerMinute is < 1 or > 10000
                || Options.Providers is null || Options.Catalog is null || Options.Providers.Count > 100 || Options.Catalog.Count > 100)
                throw Invalid();
            foreach (var (id, provider) in Options.Providers)
                if (!ValidId(id) || provider is null || !Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out var uri)
                    || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
                    || !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback && provider.AllowInsecureHttp)
                    || provider.CredentialEnvironmentVariable is { } variable && !Regex.IsMatch(variable, "\\A[A-Za-z_][A-Za-z0-9_]{0,127}\\z"))
                    throw Invalid();
            foreach (var (id, model) in Options.Catalog)
                if (!ValidId(id) || model is null || !Options.Providers.ContainsKey(model.Provider)
                    || string.IsNullOrWhiteSpace(model.UpstreamModel) || model.UpstreamModel.Length > 128
                    || model.UpstreamModel.Any(char.IsControl) || string.IsNullOrWhiteSpace(model.DisplayName)
                    || model.DisplayName.Length > 128 || model.MaximumOutputTokens is < 1 or > 32768
                    || model.ReasoningEffort is not (null or "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max"))
                    throw Invalid();
        }
        Admission = new(clock, Options.Enabled ? Options.PerUserConcurrency : 2,
            Options.Enabled ? Options.GlobalConcurrency : 32, Options.Enabled ? Options.RequestsPerMinute : 30);
    }
    public ModelDefinition? Find(string id) => Options.Enabled && ValidId(id)
        && Options.Catalog.TryGetValue(id, out var model) ? model : null;
    public static bool ValidId(string? id) => id is not null && Regex.IsMatch(id, "\\A[a-z][a-z0-9._-]{0,63}\\z");
    public static string? ReadCredential(ModelProviderConfiguration provider)
    {
        if (provider.CredentialEnvironmentVariable is null) return null;
        var value = Environment.GetEnvironmentVariable(provider.CredentialEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(char.IsControl))
            throw new ModelStoreUnavailableException();
        return value;
    }
    private static InvalidOperationException Invalid() => new("Model gateway configuration is invalid.");
}
