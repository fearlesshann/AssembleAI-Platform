using System.Net;
using Microsoft.Extensions.Logging;

namespace AssembleAI.Platform.SmokeTests;

public static class GatewayHttpTests
{
    public static async Task RunAsync()
    {
        await using var app = PlatformApplication.Build(["--environment", "Development", "--urls", "http://127.0.0.1:0",
            "--Authentication:Bff:Enabled", "false"], builder => builder.Logging.ClearProviders());
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var response = await client.GetAsync("/api/apps/production-reports/releases/1.0.0/stream");
            if (response.StatusCode != HttpStatusCode.Unauthorized || response.Headers.CacheControl?.NoStore != true)
                throw new InvalidOperationException($"Application gateway must authenticate before processing a route; got {response.StatusCode}.");
        }
        finally { await app.StopAsync(); }
        Console.WriteLine("PASS: application gateway anonymous authentication boundary.");
    }
}
