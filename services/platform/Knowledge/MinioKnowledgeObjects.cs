using System.Text.RegularExpressions;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace AssembleAI.Platform.Knowledge;

public sealed class MinioKnowledgeObjects(IConfiguration configuration, IHttpClientFactory clients) : IKnowledgeObjects
{
    public const int MaximumBytes = 5 * 1024 * 1024;
    public Task PutAsync(Guid tenant, Guid document, byte[] content, CancellationToken token) =>
        Run(tenant, document, async (client, bucket, key, budget) =>
        {
            if (content is not { Length: > 0 and <= MaximumBytes }) throw new KnowledgeObjectUnavailableException();
            using var source = new MemoryStream(content, writable: false);
            await client.PutObjectAsync(new PutObjectArgs().WithBucket(bucket).WithObject(key)
                .WithStreamData(source).WithObjectSize(content.Length).WithContentType("application/octet-stream")
                .WithHeaders(new Dictionary<string, string> { ["x-amz-acl"] = "private" }), budget);
            return true;
        }, token);

    public Task<byte[]> GetAsync(Guid tenant, Guid document, CancellationToken token) =>
        Run(tenant, document, async (client, bucket, key, budget) =>
        {
            using var content = new MemoryStream();
            var received = false;
            var stat = await client.GetObjectAsync(new GetObjectArgs().WithBucket(bucket).WithObject(key)
                .WithCallbackStream(async (source, cancellation) =>
                {
                    var bytes = new byte[8192]; int count;
                    while ((count = await source.ReadAsync(bytes, cancellation)) != 0)
                    {
                        if (content.Length + count > MaximumBytes) throw new KnowledgeObjectUnavailableException();
                        content.Write(bytes, 0, count);
                    }
                    received = true;
                }), budget);
            if (!received || content.Length != stat.Size || content.Length == 0) throw new KnowledgeObjectUnavailableException();
            return content.ToArray();
        }, token);

    public Task DeleteAsync(Guid tenant, Guid document, CancellationToken token) =>
        Run(tenant, document, async (client, bucket, key, budget) =>
        {
            // S3 delete is idempotent for an absent key. No bucket-wide/list operation is used.
            await client.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(bucket).WithObject(key), budget);
            return true;
        }, token);

    private async Task<T> Run<T>(Guid tenant, Guid document,
        Func<IMinioClient, string, string, CancellationToken, Task<T>> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (tenant == Guid.Empty || document == Guid.Empty) throw new KnowledgeObjectUnavailableException();
            var options = configuration.GetSection("Knowledge:Objects").Get<KnowledgeObjectOptions>() ?? new();
            if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
                || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
                || endpoint.AbsolutePath != "/"
                || !(endpoint.Scheme == "https" || endpoint.Scheme == "http" && endpoint.IsLoopback && options.AllowInsecureLoopback)
                || options.Bucket is null || !Regex.IsMatch(options.Bucket, "\\A[a-z0-9][a-z0-9-]{1,61}[a-z0-9]\\z"))
                throw new KnowledgeObjectUnavailableException();
            var access = Credential(options.AccessKeyEnvironmentVariable);
            var secret = Credential(options.SecretKeyEnvironmentVariable);
            using var http = clients.CreateClient(KnowledgeObjectRegistration.ClientName);
            // A fresh SDK client reads rotated credentials without sharing mutable SDK configuration.
            using var client = new MinioClient().WithEndpoint(endpoint).WithSSL(endpoint.Scheme == "https")
                .WithCredentials(access, secret).WithRegion("us-east-1").WithHttpClient(http)
                .WithRetryPolicy(send => send()).Build();
            return await action(client, options.Bucket, $"{tenant:N}/{document:N}", timeout.Token);
        }
        catch (Exception error) when (!(error is OperationCanceledException && token.IsCancellationRequested)
            && error is MinioException or HttpRequestException or IOException or ArgumentException or InvalidOperationException or OperationCanceledException)
        { throw new KnowledgeObjectUnavailableException(); }
    }
    private static string Credential(string variable)
    {
        if (variable is null || !Regex.IsMatch(variable, "\\A[A-Za-z_][A-Za-z0-9_]{0,127}\\z")) throw new KnowledgeObjectUnavailableException();
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl)) throw new KnowledgeObjectUnavailableException();
        return value;
    }
}
