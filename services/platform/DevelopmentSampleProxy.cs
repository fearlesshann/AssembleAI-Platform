using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace AssembleAI.Platform;

internal static class DevelopmentSampleProxy
{
    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        var address = configuration["Services:Sample:Address"];
        if (!Uri.TryCreate(address, UriKind.Absolute, out var target)
            || target.Scheme is not ("http" or "https")
            || target.UserInfo.Length != 0 || target.Fragment.Length != 0
            || target.Query.Length != 0 || target.AbsolutePath != "/")
        {
            throw new InvalidOperationException("Services:Sample:Address must be an HTTP(S) service origin without credentials or a path.");
        }

        var routes = new[]
        {
            Route("sample-health", "/dev/sample/health", "/health/live"),
            Route("sample-stream", "/dev/sample/stream", "/stream")
        };
        var clusters = new[]
        {
            new ClusterConfig
            {
                ClusterId = "sample-service",
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["service"] = new() { Address = target.AbsoluteUri }
                },
                HttpRequest = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(30) }
            }
        };
        services.AddReverseProxy().LoadFromMemory(routes, clusters);
    }

    private static RouteConfig Route(string id, string incoming, string outgoing) => new()
    {
        RouteId = id,
        ClusterId = "sample-service",
        Match = new RouteMatch { Path = incoming, Methods = ["GET"] },
        Transforms =
        [
            new Dictionary<string, string> { ["PathSet"] = outgoing },
            // Sample routes cannot become an accidental credential forwarding channel.
            new Dictionary<string, string> { ["RequestHeadersCopy"] = "false" },
            new Dictionary<string, string> { ["X-Forwarded"] = "Off" }
        ]
    };
}
