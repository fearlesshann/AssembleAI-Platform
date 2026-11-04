using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using AssembleAI.Platform.Authorization;

namespace AssembleAI.Platform.Knowledge;

public sealed class KnowledgeParserException : Exception
{
    public string Code { get; }
    public KnowledgeParserException(string code = "ingestion_failed") : base("Document parsing is unavailable.")
    {
        Code = code is "ocr_required" or "encrypted_document" or "unsupported_document" or "empty_document"
            or "extracted_text_too_large" or "document_too_complex" or "invalid_document" or "parser_timeout" or "parser_unavailable"
            ? code : "ingestion_failed";
    }
}

public sealed class KnowledgeParserClient(HttpClient client, KnowledgeOptions options)
{
    public async Task<IReadOnlyList<KnowledgeChunk>> ParseAsync(string fileName, byte[] bytes, CancellationToken token)
    {
        if (!Uri.TryCreate(options.ParserEndpoint, UriKind.Absolute, out var endpoint) || endpoint.UserInfo.Length != 0
            || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || !(endpoint.Scheme == "https" || endpoint.Scheme == "http" && endpoint.IsLoopback && options.AllowInsecureLoopback))
            throw new KnowledgeParserException();
        if (string.IsNullOrEmpty(options.ParserKeyEnvironmentVariable)
            || !System.Text.RegularExpressions.Regex.IsMatch(options.ParserKeyEnvironmentVariable, "\\A[A-Za-z_][A-Za-z0-9_]{0,127}\\z"))
            throw new KnowledgeParserException();
        var key = Environment.GetEnvironmentVariable(options.ParserKeyEnvironmentVariable);
        if (key is not { Length: >= 16 and <= 256 } || key.Any(c => c is < '!' or > '~')) throw new KnowledgeParserException();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.AbsoluteUri + "?filename=" + Uri.EscapeDataString(fileName)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new("application/octet-stream");
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.Content.Headers.ContentType?.MediaType != "application/json"
                || response.Content.Headers.ContentEncoding.Count != 0 || response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new KnowledgeParserException();
            using var buffer = new MemoryStream();
            await using var source = await response.Content.ReadAsStreamAsync(token);
            var part = new byte[8192]; int count;
            while ((count = await source.ReadAsync(part, token)) != 0)
            { if (buffer.Length + count > 8 * 1024 * 1024) throw new KnowledgeParserException(); buffer.Write(part, 0, count); }
            using var json = JsonDocument.Parse(buffer.ToArray(), new() { MaxDepth = 5 });
            if (!response.IsSuccessStatusCode)
                throw new KnowledgeParserException(Fields(json.RootElement, "code") && json.RootElement.GetProperty("code").ValueKind == JsonValueKind.String
                    ? json.RootElement.GetProperty("code").GetString()! : "ingestion_failed");
            if (!Fields(json.RootElement, "kind", "chunks") || json.RootElement.GetProperty("kind").GetString() != Path.GetExtension(fileName).ToLowerInvariant()[1..]
                || !json.RootElement.TryGetProperty("chunks", out var chunks) || chunks.ValueKind != JsonValueKind.Array
                || chunks.GetArrayLength() is < 1 or > 2000) throw new KnowledgeParserException();
            var result = new List<KnowledgeChunk>(); var characters = 0;
            foreach (var chunk in chunks.EnumerateArray())
            {
                if (!Fields(chunk, "index", "text", "locator")) throw new KnowledgeParserException();
                var index = chunk.GetProperty("index").GetInt32();
                var text = chunk.GetProperty("text").GetString();
                var locator = chunk.GetProperty("locator").GetString();
                if (index != result.Count || string.IsNullOrWhiteSpace(text) || text.Length > 2400 || text.Contains('\0')
                    || string.IsNullOrWhiteSpace(locator) || locator.Length > 200 || locator.Any(char.IsControl)
                    || (characters += text.Length) > 2_000_000) throw new KnowledgeParserException();
                result.Add(new(index, text, locator));
            }
            return result;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new KnowledgeParserException(); }
    }
    private static bool Fields(JsonElement value, params string[] fields) => value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Count() == fields.Length
        && value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == fields.Length
        && value.EnumerateObject().All(p => fields.Contains(p.Name, StringComparer.Ordinal));
}

// Durable DB outbox claims survive process restarts. RabbitMQ dispatch and vector generation are a later T12 slice.
public sealed class KnowledgeWorker(IKnowledgeStore documents, IKnowledgeJobStore jobs, IKnowledgeObjects objects,
    IAuthorizationStore authorization, KnowledgeParserClient parser, ILogger<KnowledgeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var job in await jobs.PendingAsync(stoppingToken))
                    await ProcessAsync(job.JobId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (KnowledgeEndpoints.Unavailable(error) || error is OperationCanceledException)
            { logger.LogWarning("Knowledge worker dependency unavailable."); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
    public async Task ProcessAsync(Guid jobId, CancellationToken stoppingToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        var token = budget.Token;
        var lease = await jobs.ClaimAsync(jobId, token);
        if (lease is null) return;
        try
        {
            var job = lease.Job;
            if (job.Kind == "purge")
            {
                await objects.DeleteAsync(job.TenantId, job.DocumentId, token);
                await jobs.CompletePurgeAsync(lease, token);
                return;
            }
            var doc = await documents.ReadAsync(job.TenantId, job.DocumentId, token);
            if (doc is null || doc.Status == "deleted" || doc.Revision != job.DocumentRevision || !await Allowed(doc, token))
                throw new KnowledgeParserException();
            var content = await objects.GetAsync(doc.TenantId, doc.DocumentId, token);
            if (content.LongLength != doc.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(content)) != doc.Sha256)
                throw new KnowledgeParserException();
            var chunks = await parser.ParseAsync(doc.FileName, content, token);
            if (!await Allowed(doc, token)) throw new KnowledgeParserException();
            // Repository rechecks document revision, lease and active identity in the same transaction as the chunk write.
            await jobs.CompleteIngestAsync(lease, chunks, token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* Expired leases are reclaimable. */ }
        catch (Exception error) when (KnowledgeEndpoints.Unavailable(error) || error is KnowledgeParserException or OperationCanceledException)
        {
            using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await jobs.FailAsync(lease, finish.Token, error is KnowledgeParserException parsing ? parsing.Code : "ingestion_failed");
        }
    }
    private async Task<bool> Allowed(KnowledgeDocument doc, CancellationToken token)
    {
        var subject = await jobs.ReadCreatorAsync(doc.CreatedBy, token);
        if (subject is null) return false;
        var access = await authorization.ReadAccessAsync(subject, doc.TenantId, token);
        return access?.UserId == doc.CreatedBy && KnowledgeAuthorization.Allows(access, "knowledge:manage", doc.TenantId, doc.OrganizationId, doc.DocumentId);
    }
}
