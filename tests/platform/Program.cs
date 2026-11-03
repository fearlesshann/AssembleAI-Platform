using System.Net;
using AssembleAI.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using System.Xml.Linq;
using AssembleAI.Platform.SmokeTests;

var finishStream = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var downstreamBuilder = WebApplication.CreateBuilder();
downstreamBuilder.Logging.ClearProviders();
await using var downstream = downstreamBuilder.Build();
downstream.Urls.Add("http://127.0.0.1:0");
downstream.MapGet("/health/live", (HttpContext context) => Results.Json(new
{
    status = "healthy",
    hasAuthorization = context.Request.Headers.ContainsKey("Authorization"),
    hasCookie = context.Request.Headers.ContainsKey("Cookie"),
    hasInternalIdentity = context.Request.Headers.ContainsKey("X-Internal-User")
}));
downstream.MapGet("/stream", async (HttpContext context) =>
{
    context.Response.ContentType = "text/event-stream";
    await context.Response.WriteAsync("event: delta\ndata: first\n\n", context.RequestAborted);
    await context.Response.Body.FlushAsync(context.RequestAborted);
    await finishStream.Task.WaitAsync(context.RequestAborted);
    await context.Response.WriteAsync("event: complete\ndata: done\n\n", context.RequestAborted);
});
await downstream.StartAsync();

try
{
    await using (var development = PlatformApplication.Build(
    [
        "--environment", "Development",
        "--urls", "http://127.0.0.1:0",
        "--DevelopmentSample:Enabled", "true",
        "--Services:Sample:Address", downstream.Urls.Single(),
        "--ConnectionStrings:Platform", "",
        "--Readiness:RedisHost", "",
        "--Logging:LogLevel:Default", "Warning"
    ], IsolateTestKeys))
    {
        await development.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(development.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
        await ExpectStatus(client, "/health/live", HttpStatusCode.OK);
        await ExpectStatus(client, "/health/ready", HttpStatusCode.ServiceUnavailable);
        await ExpectStatus(client, "/dev/sample/not-allowed", HttpStatusCode.NotFound);
        using (var post = await client.PostAsync("/dev/sample/health", null))
            Assert(post.StatusCode == HttpStatusCode.MethodNotAllowed, "Sample routes must reject POST.");

        using (var request = new HttpRequestMessage(HttpMethod.Get, "/dev/sample/health"))
        {
            request.Headers.Authorization = new("Bearer", "not-forwarded");
            request.Headers.Add("Cookie", "private=not-forwarded");
            request.Headers.Add("X-Internal-User", "forged");
            using var response = await client.SendAsync(request);
            Assert(response.IsSuccessStatusCode, "The allowed health route must reach the configured service.");
            var payload = await response.Content.ReadAsStringAsync();
            Assert(payload.Contains("\"hasAuthorization\":false")
                && payload.Contains("\"hasCookie\":false")
                && payload.Contains("\"hasInternalIdentity\":false"), "Sample proxy must not forward identity headers.");
        }

        using (var stream = await client.GetAsync("/dev/sample/stream", HttpCompletionOption.ResponseHeadersRead))
        {
            Assert(stream.IsSuccessStatusCode, "SSE proxy request must succeed.");
            Assert(stream.Content.Headers.ContentType?.MediaType == "text/event-stream", "SSE media type must survive proxying.");
            using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync());
            using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert(await reader.ReadLineAsync(readTimeout.Token) == "event: delta", "First event must arrive before downstream completion.");
            Assert(!finishStream.Task.IsCompleted, "The response must remain open while its first chunk is readable.");
            finishStream.TrySetResult();
            Assert((await reader.ReadToEndAsync(readTimeout.Token)).Contains("event: complete"), "Completion event must be forwarded.");
        }
        await development.StopAsync();
    }

    await using (var production = PlatformApplication.Build(
    [
        "--environment", "Production",
        "--urls", "http://127.0.0.1:0",
        "--DevelopmentSample:Enabled", "true",
        "--Services:Sample:Address", "invalid-must-not-be-used",
        "--Logging:LogLevel:Default", "Warning"
    ], IsolateTestKeys))
    {
        await production.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(production.Urls.Single()) };
        await ExpectStatus(client, "/health/live", HttpStatusCode.OK);
        await ExpectStatus(client, "/health/ready", HttpStatusCode.ServiceUnavailable);
        await ExpectStatus(client, "/dev/sample/health", HttpStatusCode.NotFound);
        await ExpectStatus(client, "/dev/sample/stream", HttpStatusCode.NotFound);
        await production.StopAsync();
    }
    Console.WriteLine("PASS: liveness, fail-closed readiness, exact sample routes, identity-header removal, incremental SSE and production isolation.");
    await AuthorizationTests.RunAsync();
    await TicketStoreTests.RunAsync();
    await BffKeyPersistenceTests.RunAsync();
    await BffTests.RunAsync();
}
finally
{
    finishStream.TrySetResult();
    await downstream.StopAsync();
}

static async Task ExpectStatus(HttpClient client, string path, HttpStatusCode expected)
{
    using var response = await client.GetAsync(path);
    Assert(response.StatusCode == expected, $"{path}: expected {expected}, got {response.StatusCode}.");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void IsolateTestKeys(WebApplicationBuilder builder)
{
    builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
    builder.Services.Configure<KeyManagementOptions>(options => options.XmlRepository = new TestKeyRepository());
}

sealed class TestKeyRepository : IXmlRepository
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<XElement> elements = new();
    public IReadOnlyCollection<XElement> GetAllElements() => elements.Select(element => new XElement(element)).ToArray();
    public void StoreElement(XElement element, string friendlyName) => elements.Enqueue(new XElement(element));
}
