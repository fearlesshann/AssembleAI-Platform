using System.Net;
using Microsoft.Extensions.Logging;

namespace AssembleAI.Platform.SmokeTests;

public static class RegistryManagementHttpTests
{
    public static async Task RunAsync()
    {
        await using var app = PlatformApplication.Build(["--environment", "Development", "--urls", "http://127.0.0.1:0", "--Authentication:Bff:Enabled", "false"],
            builder => builder.Logging.ClearProviders());
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var tenant = Guid.NewGuid();
            foreach (var path in new[] { "/api/platform/v1/registry/workspace", "/api/platform/v1/registry/apps/reports/audit",
                $"/api/platform/v1/tenants/{tenant}/registry/workspace", $"/api/platform/v1/tenants/{tenant}/registry/apps/reports/releases",
                $"/api/platform/v1/tenants/{tenant}/registry/apps/reports/audit" })
            {
                using var response = await client.GetAsync(path);
                if (response.StatusCode != HttpStatusCode.Unauthorized || response.Headers.CacheControl?.NoStore != true)
                    throw new InvalidOperationException($"Management reads require authentication and no-store; got {response.StatusCode}.");
            }
        }
        finally { await app.StopAsync(); }
        Console.WriteLine("PASS: registry management anonymous workspace, releases and audit authentication boundaries.");
    }
}
