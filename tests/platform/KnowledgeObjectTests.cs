using System.Collections.Concurrent;
using System.Net;
using System.Text;
using AssembleAI.Platform.Knowledge;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AssembleAI.Platform.SmokeTests;

// The actual MinIO SDK talks HTTP to this S3 protocol fixture. This is not a MinIO server acceptance test.
public static class KnowledgeObjectTests
{
    private const string AccessVariable = "ASSEMBLEAI_TEST_OBJECT_ACCESS";
    private const string SecretVariable = "ASSEMBLEAI_TEST_OBJECT_SECRET";
    public static async Task RunAsync()
    {
        var previousAccess = Environment.GetEnvironmentVariable(AccessVariable);
        var previousSecret = Environment.GetEnvironmentVariable(SecretVariable);
        Environment.SetEnvironmentVariable(AccessVariable, "fixture-private-access");
        Environment.SetEnvironmentVariable(SecretVariable, "fixture-private-secret");
        try { await ExerciseAsync(); }
        finally
        {
            Environment.SetEnvironmentVariable(AccessVariable, previousAccess);
            Environment.SetEnvironmentVariable(SecretVariable, previousSecret);
        }
    }
    private static async Task ExerciseAsync()
    {
        var builder = WebApplication.CreateBuilder(["--environment", "Development", "--urls", "http://127.0.0.1:0"]);
        builder.Logging.ClearProviders();
        await using var fixture = builder.Build();
        var objects = new ConcurrentDictionary<string, byte[]>();
        var parts = new ConcurrentDictionary<string, byte[]>();
        var requests = new ConcurrentQueue<string>();
        var mode = "normal";
        var unexpected = false;
        fixture.MapMethods("/{**path}", ["GET", "HEAD", "PUT", "DELETE", "POST"], async context =>
        {
            var key = context.Request.Path.Value!;
            requests.Enqueue(context.Request.Method + " " + key);
            var multipart = context.Request.Query.Count == 1 && (context.Request.Query.ContainsKey("uploads")
                || context.Request.Query["uploadId"] == "fixture-upload")
                || context.Request.Query.Count == 2 && context.Request.Query["uploadId"] == "fixture-upload" && context.Request.Query["partNumber"] == "1";
            if (!(key.StartsWith("/knowledge/", StringComparison.Ordinal) || key == "/knowledge" && context.Request.Method == "HEAD") || context.Request.Query.Count != 0 && !multipart
                || !context.Request.Headers.Authorization.ToString().StartsWith("AWS4-HMAC-SHA256 ", StringComparison.Ordinal)
                || context.Request.Headers.ContainsKey("Cookie") || context.Request.Headers.ContainsKey("Origin")) unexpected = true;
            if (mode == "redirect") { context.Response.StatusCode = 307; context.Response.Headers.Location = "/secret-target"; return; }
            if (mode == "failure")
            {
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync("fixture-private-secret fixture-private-object-body"); return;
            }
            if (mode == "hanging") { try { await Task.Delay(10000, context.RequestAborted); } catch (OperationCanceledException) { } return; }
            if (key is "/knowledge" or "/knowledge/" && context.Request.Method == "HEAD") return;
            if (context.Request.Method == "POST")
            {
                context.Response.ContentType = "application/xml";
                if (context.Request.Query.ContainsKey("uploads"))
                {
                    if (context.Request.Headers["x-amz-acl"] != "private") unexpected = true;
                    await context.Response.WriteAsync("<InitiateMultipartUploadResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Bucket>knowledge</Bucket><Key>fixture</Key><UploadId>fixture-upload</UploadId></InitiateMultipartUploadResult>");
                    return;
                }
                if (!parts.TryRemove(key, out var part)) { context.Response.StatusCode = 400; return; }
                objects[key] = part;
                await context.Response.WriteAsync("<CompleteMultipartUploadResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Location>fixture</Location><Bucket>knowledge</Bucket><Key>fixture</Key><ETag>fixture-etag</ETag></CompleteMultipartUploadResult>");
                return;
            }
            if (context.Request.Method == "PUT")
            {
                if (context.Request.Headers["x-amz-acl"] != "private") unexpected = true;
                using var buffer = new MemoryStream(); await context.Request.Body.CopyToAsync(buffer);
                (multipart ? parts : objects)[key] = buffer.ToArray(); context.Response.Headers.ETag = "\"fixture-etag\""; return;
            }
            if (context.Request.Method == "DELETE") { objects.TryRemove(key, out _); context.Response.StatusCode = 204; return; }
            if (!objects.TryGetValue(key, out var content)) { context.Response.StatusCode = 404; return; }
            context.Response.ContentType = "application/octet-stream";
            context.Response.Headers.ETag = "\"fixture-etag\"";
            context.Response.Headers.LastModified = "Wed, 04 Nov 2026 01:00:00 GMT";
            if (context.Request.Method == "HEAD") { context.Response.ContentLength = content.Length; return; }
            if (mode == "oversize")
            {
                await context.Response.StartAsync(); // Unknown wire length exercises the byte counter.
                await context.Response.Body.WriteAsync(new byte[MinioKnowledgeObjects.MaximumBytes + 1]); return;
            }
            context.Response.ContentLength = content.Length;
            await context.Response.Body.WriteAsync(content);
        });
        await fixture.StartAsync();
        try
        {
            var tenant = Guid.NewGuid(); var secondTenant = Guid.NewGuid(); var document = Guid.NewGuid();
            var settings = new Dictionary<string, string?>
            {
                ["Knowledge:Objects:Endpoint"] = fixture.Urls.Single(), ["Knowledge:Objects:Bucket"] = "knowledge",
                ["Knowledge:Objects:AccessKeyEnvironmentVariable"] = AccessVariable,
                ["Knowledge:Objects:SecretKeyEnvironmentVariable"] = SecretVariable,
                ["Knowledge:Objects:AllowInsecureLoopback"] = "true"
            };
            using var provider = Provider(settings);
            var store = provider.GetRequiredService<IKnowledgeObjects>();
            var bytes = Encoding.UTF8.GetBytes("文档内容 — fixture-private-object-body");
            await store.PutAsync(tenant, document, bytes, default);
            Check((await store.GetAsync(tenant, document, default)).SequenceEqual(bytes), "SDK roundtrip must preserve exact bytes.");
            await store.PutAsync(secondTenant, document, [1, 2, 3], default);
            Check(objects.Count == 2 && objects.ContainsKey($"/knowledge/{tenant:N}/{document:N}"), "Object paths must bind both tenant and document.");
            await store.DeleteAsync(tenant, document, default);
            await store.DeleteAsync(tenant, document, default);
            Check(objects.Count == 1 && (await store.GetAsync(secondTenant, document, default)).SequenceEqual(new byte[] { 1, 2, 3 }), "Deletion must be idempotent and tenant isolated.");
            await Unavailable(() => store.GetAsync(tenant, document, default));
            await store.PutAsync(tenant, document, new byte[MinioKnowledgeObjects.MaximumBytes], default);
            Check((await store.GetAsync(tenant, document, default)).Length == MinioKnowledgeObjects.MaximumBytes, "Exact maximum size remains supported.");
            await store.DeleteAsync(tenant, document, default);
            var count = requests.Count;
            await Unavailable(() => store.PutAsync(Guid.Empty, document, bytes, default));
            await Unavailable(() => store.GetAsync(tenant, Guid.Empty, default));
            await Unavailable(() => store.PutAsync(tenant, document, new byte[MinioKnowledgeObjects.MaximumBytes + 1], default));
            Check(requests.Count == count, "Invalid identity or oversized upload must fail before network access.");
            foreach (var invalid in new[] { "http://outside.invalid", "https://user:secret@outside.invalid", fixture.Urls.Single() + "/prefix", fixture.Urls.Single() + "?secret=1" })
            {
                using var rejected = Provider(new(settings) { ["Knowledge:Objects:Endpoint"] = invalid });
                await Unavailable(() => rejected.GetRequiredService<IKnowledgeObjects>().GetAsync(tenant, document, default));
            }
            Environment.SetEnvironmentVariable(SecretVariable, null);
            await Unavailable(() => store.GetAsync(secondTenant, document, default));
            Environment.SetEnvironmentVariable(SecretVariable, "fixture-private-secret");
            Check(requests.Count == count, "Invalid endpoint or missing credential must never contact a provider.");
            mode = "redirect"; count = requests.Count;
            await Unavailable(() => store.GetAsync(secondTenant, document, default));
            Check(requests.Count == count + 1 && !requests.Any(x => x.Contains("secret-target", StringComparison.Ordinal)), "Redirects must never be followed or retried.");
            mode = "failure"; await Unavailable(() => store.GetAsync(secondTenant, document, default));
            mode = "oversize"; await Unavailable(() => store.GetAsync(secondTenant, document, default));
            mode = "hanging";
            using (var cancellation = new CancellationTokenSource(100))
            {
                try { await store.GetAsync(secondTenant, document, cancellation.Token); throw new InvalidOperationException("Storage cancellation was ignored."); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
            Check(!unexpected, "SDK requests must have only trusted object paths and service credentials.");
            Console.WriteLine("PASS: knowledge objects: MinIO SDK HTTP roundtrip, tenant paths, private credentials, deletion, bounded bodies, cancellation and safe failures.");
        }
        finally { await fixture.StopAsync(); }
    }
    private static ServiceProvider Provider(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection(); services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(builder => builder.ClearProviders()); services.AddKnowledgeObjects(configuration);
        return services.BuildServiceProvider();
    }
    private static async Task Unavailable(Func<Task> action)
    {
        try { await action(); throw new InvalidOperationException("Expected bounded safe storage failure."); }
        catch (KnowledgeObjectUnavailableException error)
        { Check(error.InnerException is null && !error.ToString().Contains("fixture-private", StringComparison.Ordinal), "Storage failure must not expose credentials or object content."); }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
