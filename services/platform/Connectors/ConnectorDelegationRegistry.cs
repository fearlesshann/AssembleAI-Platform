using AssembleAI.Platform.Models;

namespace AssembleAI.Platform.Connectors;

/// <summary>Uses the proven capability lifecycle with its own isolated dataset namespace and registry.</summary>
public sealed class ConnectorDelegationRegistry
{
    private readonly ModelDelegationRegistry inner;
    public ConnectorDelegationRegistry(TimeProvider clock, int maximumEntries = 4096) => inner = new(clock, maximumEntries);
    public ModelDelegationRegistry.Lease? Register(ModelCaller caller, IReadOnlyCollection<string> datasetIds) => inner.Register(caller, datasetIds);
    public ModelCaller? Resolve(string capability, string datasetId) => inner.Resolve(capability, datasetId);
}
