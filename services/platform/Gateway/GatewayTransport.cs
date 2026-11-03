using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AssembleAI.Platform.Registry;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssembleAI.Platform.Gateway;

public sealed class GatewayTransport : IDisposable
{
    // YARP's default logs contain full destination URLs and exception messages.
    // Keep this adapter's logging independent of deployment category overrides;
    // the gateway writes its own bounded, credential-free call records instead.
    private readonly ServiceProvider forwardingServices = new ServiceCollection()
        .AddSingleton(TimeProvider.System)
        .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
        .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
        .AddHttpForwarder().BuildServiceProvider();
    public IHttpForwarder Forwarder => forwardingServices.GetRequiredService<IHttpForwarder>();
    public HttpMessageInvoker Client { get; } = new(new SocketsHttpHandler
    {
        UseCookies = false, UseProxy = false, AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(3),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2), EnableMultipleHttp2Connections = true,
        ActivityHeadersPropagator = null
    });

    public static Uri Destination(string prefix, string path, QueryString query) =>
        RequestUtilities.MakeDestinationAddress(prefix.TrimEnd('/') + "/", new PathString(path), query);

    public async Task<bool> IsHealthy(RegistryDeploymentCatalog.BackendBinding binding, string healthPath, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Destination(binding.Destination, healthPath, QueryString.Empty));
            request.Headers.Accept.Add(new("application/json"));
            using var response = await Client.SendAsync(request, deadline.Token);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > 4096) return false;
            await using var source = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var bytes = new MemoryStream();
            var buffer = new byte[1024];
            int count;
            while ((count = await source.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if (bytes.Length + count > 4096) return false;
                bytes.Write(buffer, 0, count);
            }
            using var json = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
            var root = json.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() == "healthy"
                && root.TryGetProperty("service", out var service) && service.ValueKind == JsonValueKind.String && service.GetString() == binding.Service
                && root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String && version.GetString() == binding.Version
                && root.TryGetProperty("protocolVersion", out var protocol) && protocol.ValueKind == JsonValueKind.Number && protocol.TryGetInt32(out var number) && number == 1;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException || error is OperationCanceledException && !token.IsCancellationRequested)
        { return false; }
    }
    public void Dispose() { Client.Dispose(); forwardingServices.Dispose(); }
}

internal sealed class GatewayTransformer(Uri destination, string token, string requestId, Action headersReceived) : HttpTransformer
{
    public bool RejectedResponse { get; private set; }
    public override ValueTask TransformRequestAsync(HttpContext context, HttpRequestMessage request, string prefix, CancellationToken cancellationToken)
    {
        request.RequestUri = destination;
        request.Headers.Clear();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Request-ID", requestId);
        request.Headers.Accept.Add(new("application/json"));
        request.Headers.Accept.Add(new("text/event-stream"));
        // YARP creates streaming request content; its framing remains the forwarder's responsibility.
        if (request.Content is not null)
        {
            request.Content.Headers.Clear();
            request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
            request.Content.Headers.ContentLength = context.Request.ContentLength;
        }
        return ValueTask.CompletedTask;
    }
    public override async ValueTask<bool> TransformResponseAsync(HttpContext context, HttpResponseMessage? response, CancellationToken cancellationToken)
    {
        headersReceived();
        if (response is null) return false;
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!response.IsSuccessStatusCode || mediaType is not ("application/json" or "text/event-stream")
            || response.Content.Headers.ContentEncoding.Count > 0
            || response.Content.Headers.ContentType?.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))
        {
            RejectedResponse = true;
            context.Response.StatusCode = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? (int)response.StatusCode : 502;
            await Results.Problem(statusCode: context.Response.StatusCode, title: "The application response was rejected.",
                extensions: new Dictionary<string, object?> { ["code"] = "application_response_rejected", ["requestId"] = requestId }).ExecuteAsync(context);
            return false;
        }
        // No base call: no downstream Cookie, Location, CORS or authentication headers reach this origin.
        context.Response.Headers.ContentType = mediaType + "; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Request-ID"] = requestId;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (mediaType == "text/event-stream") context.Response.Headers["X-Accel-Buffering"] = "no";
        return true;
    }
    public override ValueTask TransformResponseTrailersAsync(HttpContext context, HttpResponseMessage response, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
