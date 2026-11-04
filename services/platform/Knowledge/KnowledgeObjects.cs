using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AssembleAI.Platform.Knowledge;

public interface IKnowledgeObjects
{
    Task PutAsync(Guid tenant, Guid document, byte[] content, CancellationToken token);
    Task<byte[]> GetAsync(Guid tenant, Guid document, CancellationToken token);
    Task DeleteAsync(Guid tenant, Guid document, CancellationToken token);
}

public sealed class KnowledgeObjectUnavailableException() : Exception("Document storage is unavailable.");

public sealed class KnowledgeObjectOptions
{
    public string Endpoint { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string AccessKeyEnvironmentVariable { get; set; } = "";
    public string SecretKeyEnvironmentVariable { get; set; } = "";
    public bool AllowInsecureLoopback { get; set; }
}

public static class KnowledgeObjectRegistration
{
    internal const string ClientName = "knowledge-objects";
    public static IServiceCollection AddKnowledgeObjects(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient(ClientName).ConfigurePrimaryHttpMessageHandler(() => new KnowledgeObjectTransport
        {
            InnerHandler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(5),
                AutomaticDecompression = System.Net.DecompressionMethods.None
            }
        }).RemoveAllLoggers();
        services.TryAddSingleton<IKnowledgeObjects, MinioKnowledgeObjects>();
        return services;
    }
}

// Bound content before SDK XML/error handling can buffer it. Storage errors never reach its parser.
internal sealed class KnowledgeObjectTransport : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var response = await base.SendAsync(request, token);
        try
        {
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentEncoding.Count != 0
                || response.Content.Headers.ContentLength > MinioKnowledgeObjects.MaximumBytes)
                throw new KnowledgeObjectUnavailableException();
            if (request.Method != HttpMethod.Head)
            {
                await using var source = await response.Content.ReadAsStreamAsync(token);
                using var buffer = new MemoryStream(); var bytes = new byte[8192]; int count;
                while ((count = await source.ReadAsync(bytes, token)) != 0)
                {
                    if (buffer.Length + count > MinioKnowledgeObjects.MaximumBytes) throw new KnowledgeObjectUnavailableException();
                    buffer.Write(bytes, 0, count);
                }
                var original = response.Content;
                var bounded = new ByteArrayContent(buffer.ToArray());
                foreach (var header in original.Headers) bounded.Headers.TryAddWithoutValidation(header.Key, header.Value);
                response.Content = bounded; original.Dispose();
            }
            return response;
        }
        catch { response.Dispose(); throw; }
    }
}
