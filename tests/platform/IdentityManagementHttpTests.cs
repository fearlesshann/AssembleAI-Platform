using System.Net;
using Microsoft.Extensions.Logging;

namespace AssembleAI.Platform.SmokeTests;

public static class IdentityManagementHttpTests
{
    public static async Task RunAsync()
    {
        await using var app = PlatformApplication.Build(["--environment", "Development", "--urls", "http://127.0.0.1:0", "--Authentication:Bff:Enabled", "false"],
            builder => builder.Logging.ClearProviders());
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var tenant = Guid.NewGuid(); var id = Guid.NewGuid();
            const string root = "/api/platform/v1/identity-management";
            foreach (var path in new[] { root + "/workspace", root + $"/tenants/{tenant}" })
            {
                using var response = await client.GetAsync(path);
                Assert(response.StatusCode == HttpStatusCode.Unauthorized && response.Headers.CacheControl?.NoStore == true,
                    "Identity management reads require authenticated BFF sessions and no-store.");
            }
            foreach (var path in new[] { root + $"/users/{id}", root + $"/tenants/{tenant}",
                root + $"/tenants/{tenant}/members/{id}", root + $"/tenants/{tenant}/organizations/{id}", root + $"/tenants/{tenant}/roles/{id}" })
            {
                using var response = await client.PutAsync(path, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
                Assert(response.StatusCode == HttpStatusCode.Unauthorized && response.Headers.CacheControl?.NoStore == true,
                    "Identity management writes cannot accept anonymous or header-forged actors.");
            }
        }
        finally { await app.StopAsync(); }
        Console.WriteLine("PASS: identity management anonymous read/write authentication and no-store boundaries.");
    }

    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
