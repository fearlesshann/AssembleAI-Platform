using System.Net;
using System.Security.Cryptography;
using System.Text;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Knowledge;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssembleAI.Platform.SmokeTests;

// The actual worker and HTTP parser client run with explicit in-memory storage and transport fixtures.
// PostgreSQL fencing and Python process isolation have separate integration suites.
public static class KnowledgeWorkerTests
{
    private const string Variable = "ASSEMBLEAI_KNOWLEDGE_WORKER_TEST_KEY";
    private const string Secret = "fixture-private-parser-key";
    private const string Result = """{"kind":"txt","chunks":[{"index":0,"text":"生产说明 300","locator":"lines:1-1"}]}""";

    public static async Task RunAsync()
    {
        var previous = Environment.GetEnvironmentVariable(Variable);
        Environment.SetEnvironmentVariable(Variable, Secret);
        try
        {
            await SuccessAndDuplicate();
            await RevocationAndDeletion();
            await IntegrityAndPurge();
            await ParserFailures();
            await StopCancelsParser();
        }
        finally { Environment.SetEnvironmentVariable(Variable, previous); }
        Console.WriteLine("PASS: knowledge worker: attributed extraction, duplicate admission, current creator permission, deletion fence, object integrity, independent purge, safe parser failures and shutdown cancellation.");
    }

    private static async Task SuccessAndDuplicate()
    {
        var state = new State();
        using var http = Client(state);
        using var worker = Worker(state, http);
        await worker.ProcessAsync(state.Job.JobId, default);
        Check(state.Completed && state.Chunks is [KnowledgeChunk { Index: 0, Text: "生产说明 300", Locator: "lines:1-1" }]
            && state.Failed == 0 && state.ParserCalls == 1 && state.AuthorizationReads == 2, "Success must persist parsed chunks after both creator permission checks.");
        await worker.ProcessAsync(state.Job.JobId, default);
        Check(state.ParserCalls == 1 && state.CompletedAttempts == 1, "Duplicate delivery must not parse a terminal job again.");
    }

    private static async Task RevocationAndDeletion()
    {
        var denied = new State { Allowed = false };
        using (var http = Client(denied))
        using (var worker = Worker(denied, http))
            await worker.ProcessAsync(denied.Job.JobId, default);
        Check(denied.Failed == 1 && denied.ParserCalls == 0 && denied.ObjectReads == 0, "A creator without the current grant cannot fetch or parse document content.");

        foreach (var reason in new[] { "permission", "creator", "deleted", "revision" })
        {
            var state = new State { HoldParser = true };
            using var http = Client(state);
            using var worker = Worker(state, http);
            var pending = worker.ProcessAsync(state.Job.JobId, default);
            await state.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (reason == "permission") state.Allowed = false;
            else if (reason == "creator") state.CreatorPresent = false;
            else if (reason == "deleted") state.Document = state.Document with { Status = "deleted", Revision = 2 };
            else state.Document = state.Document with { Revision = 2 };
            state.Release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!state.Completed && state.Chunks.Count == 0, $"A late {reason} change must prevent chunk publication.");
            if (reason is "permission" or "creator") Check(state.CompletedAttempts == 0 && state.Failed == 1, "Creator revocation must fail before attempting repository completion.");
        }
    }

    private static async Task IntegrityAndPurge()
    {
        var mismatch = new State { CorruptObject = true };
        using (var http = Client(mismatch))
        using (var worker = Worker(mismatch, http))
            await worker.ProcessAsync(mismatch.Job.JobId, default);
        Check(mismatch.Failed == 1 && mismatch.ParserCalls == 0 && mismatch.CompletedAttempts == 0, "Object bytes must match both stored length and SHA256 before parser access.");

        var purge = new State { Allowed = false, CreatorPresent = false };
        purge.Job = purge.Job with { Kind = "purge", DocumentRevision = 2 };
        purge.Document = purge.Document with { Status = "deleted", Revision = 2 };
        using (var http = Client(purge))
        using (var worker = Worker(purge, http))
            await worker.ProcessAsync(purge.Job.JobId, default);
        Check(purge.Purged && purge.ObjectDeletes == 1 && purge.CreatorReads == 0 && purge.AuthorizationReads == 0 && purge.ParserCalls == 0,
            "A tombstone purge must remove content even after its original actor loses membership.");

        var deleteFailure = new State { DeleteFails = true };
        deleteFailure.Job = deleteFailure.Job with { Kind = "purge", DocumentRevision = 2 };
        deleteFailure.Document = deleteFailure.Document with { Status = "deleted", Revision = 2 };
        using (var http = Client(deleteFailure))
        using (var worker = Worker(deleteFailure, http))
            await worker.ProcessAsync(deleteFailure.Job.JobId, default);
        Check(!deleteFailure.Purged && deleteFailure.Failed == 1, "Storage deletion must finish before acknowledging a purge job.");
    }

    private static async Task ParserFailures()
    {
        foreach (var code in new[] { "ocr_required", "encrypted_document", "private-provider-detail" })
        {
            var state = new State { ResponseStatus = 422, ResponseBody = "{\"code\":\"" + code + "\"}" };
            using var http = Client(state);
            using var worker = Worker(state, http);
            await worker.ProcessAsync(state.Job.JobId, default);
            Check(state.LastFailure == (code == "private-provider-detail" ? "ingestion_failed" : code), "Only safe known parser reasons may reach document metadata.");
        }
        foreach (var (status, type, body) in new[] {
            (504, "application/json", "{\"code\":\"parser_timeout\"}"),
            (200, "application/json", "not-json-private-provider-detail"),
            (200, "application/json", "null"),
            (200, "application/json", "{\"chunks\":[]}"),
            (200, "application/json", Result.Replace("\"index\":0", "\"index\":99999999999999999999999999999", StringComparison.Ordinal)),
            (200, "application/json", Result.Replace("\"index\":0", "\"index\":2", StringComparison.Ordinal)),
            (200, "application/json", Result.Replace("\"kind\":\"txt\",", "", StringComparison.Ordinal)),
            (200, "application/json", Result.Replace("\"kind\":\"txt\"", "\"kind\":\"pdf\"", StringComparison.Ordinal)),
            (200, "application/json", Result.Replace("\"index\":0", "\"index\":0,\"index\":0", StringComparison.Ordinal)),
            (200, "application/json", Result.Replace("\"index\":0", "\"index\":0,\"unexpected\":true", StringComparison.Ordinal)),
            (200, "text/html", Result),
        })
        {
            var state = new State { ResponseStatus = status, ResponseType = type, ResponseBody = body };
            using var http = Client(state);
            using var worker = Worker(state, http);
            await worker.ProcessAsync(state.Job.JobId, default);
            Check(state.Failed == 1 && !state.Completed && state.Chunks.Count == 0,
                "Malformed, mismatched, ambiguous or unsuccessful parser output must fail the job safely.");
        }
    }

    private static async Task StopCancelsParser()
    {
        var state = new State { HoldParser = true };
        using var http = Client(state);
        using var worker = Worker(state, http);
        using var stopping = new CancellationTokenSource();
        var pending = worker.ProcessAsync(state.Job.JobId, stopping.Token);
        await state.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        stopping.Cancel();
        await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Check(state.ParserCancelled && state.Failed == 0 && !state.Completed, "Shutdown must cancel parser transport and leave its lease available for later recovery.");
    }

    private static KnowledgeWorker Worker(State state, HttpClient http) => new(state, state, state, state,
        new(http, new KnowledgeOptions { ParserEndpoint = "http://127.0.0.1:8093/internal/parse", ParserKeyEnvironmentVariable = Variable, AllowInsecureLoopback = true }),
        NullLogger<KnowledgeWorker>.Instance);
    private static HttpClient Client(State state) => new(new Handler(state)) { Timeout = Timeout.InfiniteTimeSpan };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class Handler(State state) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            state.ParserCalls++;
            Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/internal/parse"
                && Uri.UnescapeDataString(request.RequestUri.Query) == "?filename=生产 说明.txt"
                && request.Headers.Authorization?.ToString() == "Bearer " + Secret
                && request.Content!.Headers.ContentType?.MediaType == "application/octet-stream"
                && !request.Headers.Contains("Cookie") && !request.Headers.Contains("X-Tenant-Id"), "The private parser request must bind only its trusted filename, body and server credential.");
            Check((await request.Content!.ReadAsByteArrayAsync(token)).SequenceEqual(state.Bytes), "The parser must receive the verified original object.");
            state.Entered.TrySetResult();
            if (state.HoldParser)
                try { await state.Release.Task.WaitAsync(token); }
                catch (OperationCanceledException) { state.ParserCancelled = true; throw; }
            return new((HttpStatusCode)state.ResponseStatus) { Content = new StringContent(state.ResponseBody, Encoding.UTF8, state.ResponseType) };
        }
    }

    private sealed class State : IKnowledgeStore, IKnowledgeJobStore, IKnowledgeObjects, IAuthorizationStore
    {
        public readonly byte[] Bytes = Encoding.UTF8.GetBytes("生产说明 300");
        public KnowledgeDocument Document;
        public KnowledgeJob Job;
        public readonly Guid Lease = Guid.NewGuid();
        public bool Allowed = true, CreatorPresent = true, HoldParser, CorruptObject, DeleteFails, Completed, Purged, ParserCancelled;
        public int ParserCalls, AuthorizationReads, CreatorReads, ObjectReads, ObjectDeletes, Failed, CompletedAttempts;
        public int ResponseStatus = 200;
        public string ResponseBody = Result, ResponseType = "application/json";
        public IReadOnlyList<KnowledgeChunk> Chunks = [];
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public State()
        {
            Document = new(Guid.NewGuid(), Guid.NewGuid(), "生产 说明.txt", "text/plain", Bytes.Length,
                Convert.ToHexStringLower(SHA256.HashData(Bytes)), Guid.NewGuid(), "queued", 1, Guid.NewGuid(), DateTimeOffset.UtcNow);
            Job = new(Guid.NewGuid(), Document.TenantId, Document.DocumentId, "ingest", 1, "pending", 0);
        }
        public Task<KnowledgeDocument> CreateAsync(KnowledgeDocument document, string requestId, CancellationToken token) => throw new NotSupportedException();
        public Task<KnowledgeDocument?> ReadAsync(Guid tenant, Guid document, CancellationToken token) => Task.FromResult<KnowledgeDocument?>(tenant == Document.TenantId && document == Document.DocumentId ? Document : null);
        public Task<IReadOnlyList<KnowledgeDocument>> ListAsync(Guid tenant, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid tenant, Guid document, long expectedRevision, Guid actor, string requestId, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<KnowledgeJob>> PendingAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<KnowledgeJob>>([Job]);
        public Task<SubjectIdentity?> ReadCreatorAsync(Guid userId, CancellationToken token)
        { CreatorReads++; return Task.FromResult<SubjectIdentity?>(CreatorPresent && userId == Document.CreatedBy ? new("https://identity.fixture", "creator") : null); }
        public Task<KnowledgeJobLease?> ClaimAsync(Guid jobId, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult<KnowledgeJobLease?>(jobId == Job.JobId && !Completed && !Purged && Failed == 0 ? new(Job, Lease) : null); }
        public Task<bool> CompleteIngestAsync(KnowledgeJobLease lease, IReadOnlyList<KnowledgeChunk> chunks, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); CompletedAttempts++;
            if (lease.LeaseId != Lease || Document.Status == "deleted" || Document.Revision != lease.Job.DocumentRevision) return Task.FromResult(false);
            Chunks = chunks; Completed = true; return Task.FromResult(true);
        }
        public Task<bool> CompletePurgeAsync(KnowledgeJobLease lease, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Check(ObjectDeletes == 1 && !DeleteFails, "Purge acknowledgement must follow successful deletion."); Purged = true; return Task.FromResult(true); }
        public string? LastFailure;
        public Task<bool> FailAsync(KnowledgeJobLease lease, CancellationToken token, string failureCode = "ingestion_failed") { Failed++; LastFailure = failureCode; return Task.FromResult(true); }
        public Task PutAsync(Guid tenant, Guid document, byte[] bytes, CancellationToken token) => throw new NotSupportedException();
        public Task<byte[]> GetAsync(Guid tenant, Guid document, CancellationToken token)
        { ObjectReads++; return Task.FromResult(CorruptObject ? Encoding.UTF8.GetBytes("different bytes") : Bytes); }
        public Task DeleteAsync(Guid tenant, Guid document, CancellationToken token)
        { ObjectDeletes++; if (DeleteFails) throw new KnowledgeObjectUnavailableException(); return Task.CompletedTask; }
        public Task<IReadOnlyList<TenantMembership>> ListTenantsAsync(SubjectIdentity identity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AccessSnapshot?> ReadAccessAsync(SubjectIdentity identity, Guid tenantId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); AuthorizationReads++;
            return Task.FromResult<AccessSnapshot?>(new(Document.CreatedBy, Document.TenantId, Allowed ? 1 : 2,
                Allowed ? [new("knowledge:manage", "knowledge", "documents", false, new HashSet<Guid> { Document.OrganizationId!.Value })] : [],
                new HashSet<Guid> { Document.OrganizationId!.Value }));
        }
    }
}
