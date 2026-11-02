using System.Collections.Concurrent;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace AssembleAI.Platform;

// Development-only repository: .NET's key-ring warmup must not read the user's DPAPI profile.
internal sealed class DevelopmentKeyRepository : IXmlRepository
{
    private readonly ConcurrentQueue<XElement> elements = new();

    public IReadOnlyCollection<XElement> GetAllElements() => elements.Select(element => new XElement(element)).ToArray();

    public void StoreElement(XElement element, string friendlyName) => elements.Enqueue(new XElement(element));
}
