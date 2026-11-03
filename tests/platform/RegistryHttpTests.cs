using System.Net;
using Microsoft.Extensions.Logging;

namespace AssembleAI.Platform.SmokeTests;

public static class RegistryHttpTests
{
    public static async Task RunAsync()
    {
        ManifestTests.Run();
        await using var app = PlatformApplication.Build([
            "--environment", "Development", "--urls", "http://127.0.0.1:0", "--Authentication:Bff:Enabled", "false"
        ], builder => builder.Logging.ClearProviders());
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            foreach (var path in new[] { "/api/platform/v1/registry/apps/reports/releases",
                "/api/platform/v1/registry/apps/reports/releases/1.0.0",
                $"/api/platform/v1/tenants/{Guid.NewGuid()}/applications/reports" })
            {
                using var response = await client.GetAsync(path);
                if (response.StatusCode != HttpStatusCode.Unauthorized || response.Headers.CacheControl?.NoStore != true)
                    throw new InvalidOperationException($"Registry reads require authenticated identity and no-store; got {response.StatusCode}.");
            }
            foreach (var (method, path) in new[] {
                (HttpMethod.Put, "/api/platform/v1/registry/apps/reports/releases/1.0.0"),
                (HttpMethod.Post, "/api/platform/v1/registry/apps/reports/releases/1.0.0/transitions"),
                (HttpMethod.Put, $"/api/platform/v1/tenants/{Guid.NewGuid()}/applications/reports") })
            {
                using var request = new HttpRequestMessage(method, path);
                using var response = await client.SendAsync(request);
                if (response.StatusCode != HttpStatusCode.Unauthorized || response.Headers.CacheControl?.NoStore != true)
                    throw new InvalidOperationException("Registry writes must authenticate before parsing commands.");
            }
        }
        finally { await app.StopAsync(); }
        Console.WriteLine("PASS: registry HTTP anonymous reads/writes require authentication and prohibit caching.");
    }
}
