using System.Net;
using Microsoft.Extensions.Logging;

namespace AssembleAI.Platform.SmokeTests;

public static class CatalogHttpTests
{
    public static async Task RunAsync()
    {
        await using var app = PlatformApplication.Build([
            "--environment", "Development", "--urls", "http://127.0.0.1:0", "--Authentication:Bff:Enabled", "false"
        ], builder => builder.Logging.ClearProviders());
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var tenant = Guid.NewGuid();
            foreach (var path in new[] { $"/api/platform/v1/tenants/{tenant}/catalog", $"/api/platform/v1/tenants/{tenant}/catalog/reports" })
            {
                using var response = await client.GetAsync(path);
                if (response.StatusCode != HttpStatusCode.Unauthorized || response.Headers.CacheControl?.NoStore != true)
                    throw new InvalidOperationException($"Catalog requires authenticated identity and no-store; got {response.StatusCode}.");
            }
        }
        finally { await app.StopAsync(); }
        Console.WriteLine("PASS: catalog anonymous list and detail require authentication and prohibit caching.");
    }
}
