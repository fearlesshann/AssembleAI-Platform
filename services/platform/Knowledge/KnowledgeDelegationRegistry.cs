using AssembleAI.Platform.Models;

namespace AssembleAI.Platform.Knowledge;

public sealed class KnowledgeDelegationRegistry
{
    private readonly ModelDelegationRegistry inner;
    public KnowledgeDelegationRegistry(TimeProvider clock,int maximumEntries=4096)=>inner=new(clock,maximumEntries);
    public ModelDelegationRegistry.Lease? Register(ModelCaller caller)=>inner.Register(caller,["documents"]);
    public ModelCaller? Resolve(string capability)=>inner.Resolve(capability,"documents");
}
