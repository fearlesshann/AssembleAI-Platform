using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Knowledge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AssembleAI.Platform.SmokeTests;

// Real OIDC/BFF/CSRF/HTTP with explicit in-memory document and object repositories.
public static class KnowledgeHttpTests
{
    private const string PrivateText = "fixture-private-knowledge-content-300";
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes(PrivateText);
    public static async Task RunAsync()
    {
        await using var identity = await OidcFixture.StartAsync();
        var state = new State(identity.Authority);
        var origin = FreeOrigin();
        await using var app = PlatformApplication.Build([
            "--environment", "Development", "--urls", origin, "--Authentication:Bff:Enabled", "true",
            "--Authentication:Bff:Authority", identity.Authority, "--Authentication:Bff:ClientId", OidcFixture.ClientId,
            "--Authentication:Bff:ClientSecret", OidcFixture.ClientSecret, "--Authentication:Bff:PublicOrigin", origin
        ], builder => {
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Knowledge:Enabled"] = "true", ["Knowledge:WorkerEnabled"] = "false" });
            builder.Services.AddSingleton<IAuthorizationStore>(state);
            builder.Services.AddSingleton<IKnowledgeStore>(state);
            builder.Services.AddSingleton<IKnowledgeObjects>(state);
        });
        await app.StartAsync();
        try
        {
            var root = $"/api/platform/v1/tenants/{state.Tenant}/knowledge/documents";
            var upload = root + "?filename=" + Uri.EscapeDataString("生产说明.txt") + "&organizationId=" + state.Factory;
            using var anonymous = new HttpClient { BaseAddress = new(origin) };
            using (var response = await anonymous.GetAsync(root)) await Expect(response, 401);
            using var browser = await Browser.Login(origin);
            using (var response = await browser.Send("GET", root)) await Denied(response, 403);
            await browser.Select(state.Tenant);
            using (var response = await browser.Send("GET", root.Replace(state.Tenant.ToString(), state.OtherTenant.ToString(), StringComparison.Ordinal))) await Denied(response, 403);
            using (var response = await browser.Send("POST", upload, Raw(), csrf: false)) await Denied(response, 403);
            using (var response = await browser.Send("POST", upload, Raw(), requestOrigin: "https://untrusted.invalid")) await Denied(response, 403);
            using (var response = await browser.Send("POST", upload.Replace(state.Factory.ToString(), state.OtherFactory.ToString(), StringComparison.Ordinal), Raw())) await Denied(response, 403);
            using (var response = await browser.Send("POST", root + "?filename=a.txt", Raw())) await Denied(response, 403);
            foreach (var filename in new[] { "../escape.txt", "bad.doc", "bad.exe", "bad.txt ", "bad\\a.txt", "bad:stream.txt" })
                using (var response = await browser.Send("POST", root + "?filename=" + Uri.EscapeDataString(filename) + "&organizationId=" + state.Factory, Raw())) await Denied(response, 400);
            using (var response = await browser.Send("POST", upload + "&filename=second.txt", Raw())) await Denied(response, 400);
            using (var response = await browser.Send("POST", upload + "&tenant=forged", Raw())) await Denied(response, 400);
            using (var response = await browser.Send("POST", upload, new StringContent("text", Encoding.UTF8, "text/plain"))) await Denied(response, 400);
            using (var response = await browser.Send("POST", upload, Raw([]))) await Denied(response, 413);
            using (var response = await browser.Send("POST", upload, Raw(new byte[KnowledgeOptions.MaximumBytes + 1]))) await Denied(response, 413);
            Check(state.ObjectPuts == 0 && state.Documents.IsEmpty, "Rejected uploads must not write object bytes or metadata.");

            Guid document;
            using (var response = await browser.Send("POST", upload, Raw()))
            {
                using var body = JsonDocument.Parse(await Expect(response, 202));
                document = body.RootElement.GetProperty("documentId").GetGuid();
                Check(body.RootElement.GetProperty("status").GetString() == "queued"
                    && body.RootElement.GetProperty("createdBy").GetGuid() == state.User
                    && body.RootElement.GetProperty("organizationId").GetGuid() == state.Factory,
                    "Upload must persist the authenticated creator and authorized scope, without claiming parsing success.");
            }
            Check(state.ObjectPuts == 1 && state.Creates == 1 && !state.CreateBeforeObject, "Metadata admission must follow confirmed object persistence.");
            var path = root + "/" + document;
            using (var response = await browser.Send("GET", path)) await Expect(response, 200);
            using (var response = await browser.Send("GET", path + "/content"))
            {
                Check(response.Content.Headers.ContentType?.MediaType == "application/octet-stream"
                    && response.Content.Headers.ContentDisposition?.DispositionType == "attachment", "Content must be downloaded as an attachment, never executed in the platform origin.");
                Check(await Expect(response, 200) == PrivateText, "Authorized download must return exactly the stored object bytes.");
            }
            state.Corrupt = true;
            using (var response = await browser.Send("GET", path + "/content")) await Denied(response, 503);
            state.Corrupt = false;

            var foreign = state.Documents[document] with { DocumentId = Guid.NewGuid(), FileName = "private-other-factory.txt", OrganizationId = state.OtherFactory };
            state.Documents[foreign.DocumentId] = foreign;
            using (var response = await browser.Send("GET", root))
            {
                var list = await Expect(response, 200);
                Check(list.Contains(document.ToString(), StringComparison.Ordinal) && !list.Contains(foreign.FileName, StringComparison.Ordinal), "Document metadata must be filtered by the same organization grants as content.");
            }
            using (var response = await browser.Send("GET", root + "/" + foreign.DocumentId)) await Denied(response, 404);
            using (var response = await browser.Send("GET", root + "/" + foreign.DocumentId + "/content")) await Denied(response, 404);

            foreach (var change in new[] { "permission", "delete", "logout", "tenant" })
            {
                state.Allowed = true;
                state.Documents[document] = state.Documents[document] with { Status = "queued", Revision = 1 };
                using var active = await Browser.Login(origin);
                await active.Select(state.Tenant);
                var gate = state.NextGet = new Gate();
                var pending = active.Send("GET", path + "/content");
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (change == "permission") state.Allowed = false;
                else if (change == "delete") { using var response = await active.Send("DELETE", path, JsonContent.Create(new { expectedRevision = 1 })); await Expect(response, 204); }
                else if (change == "logout") { using var response = await active.Send("POST", "/bff/logout"); await Expect(response, 204); }
                else await active.Select(state.OtherTenant);
                gate.Release.TrySetResult();
                using var denied = await pending;
                await Denied(denied, change is "permission" or "delete" ? 404 : 403);
            }
            state.Allowed = true;
            state.Documents[document] = state.Documents[document] with { Status = "queued", Revision = 1 };
            using (var response = await browser.Send("DELETE", path, JsonContent.Create(new { expectedRevision = 1 }), csrf: false)) await Denied(response, 403);
            using (var response = await browser.Send("DELETE", path, JsonContent.Create(new { expectedRevision = 2 }))) await Denied(response, 409);
            using (var response = await browser.Send("DELETE", path, new StringContent("{\"expectedRevision\":1,\"expectedRevision\":1}", Encoding.UTF8, "application/json"))) await Denied(response, 400);
            using (var response = await browser.Send("DELETE", path, JsonContent.Create(new { expectedRevision = 1 }))) await Expect(response, 204);
            using (var response = await browser.Send("GET", path)) await Denied(response, 404);
            using (var response = await browser.Send("GET", path + "/content")) await Denied(response, 404);

            // Revocation after object persistence must withhold metadata admission and clean the just-written object.
            var writes = state.Creates;
            var cleanupCount = state.ObjectDeletes;
            var put = state.NextPut = new Gate();
            var uploading = browser.Send("POST", upload, Raw());
            await put.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            state.Allowed = false;
            put.Release.TrySetResult();
            using (var response = await uploading) await Denied(response, 403);
            Check(state.Creates == writes && state.ObjectDeletes == cleanupCount + 1, "Late upload revocation must prevent a new document and clean the already-written private object.");
            state.Allowed = true;
            state.FailObjects = true;
            using (var response = await browser.Send("POST", upload, Raw())) await Denied(response, 503);
            Check(state.Creates == writes, "Failed object storage must not create document metadata.");
            state.FailObjects = false;
            state.FailStore = true;
            using (var response = await browser.Send("GET", root)) await Denied(response, 503);
            using (var response = await browser.Send("POST", upload, Raw())) await Denied(response, 503);
            state.FailStore = false;
            Check(state.ObjectDeletes == cleanupCount + 1, "An ambiguous metadata failure must not erase an object whose transaction may have committed.");
            Console.WriteLine("PASS: knowledge OIDC HTTP: selected tenant/CSRF, organization scope, bounded upload, attributed admission, guarded attachment/integrity, live revocation/deletion/logout/tenant changes, revision delete and safe storage failure.");
        }
        finally
        {
            state.NextGet?.Release.TrySetResult(); state.NextPut?.Release.TrySetResult();
            await app.StopAsync();
        }
    }

    private static ByteArrayContent Raw(byte[]? bytes = null)
    { var content = new ByteArrayContent(bytes ?? Bytes); content.Headers.ContentType = new("application/octet-stream"); return content; }
    private static string FreeOrigin() { using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); return "http://127.0.0.1:" + ((IPEndPoint)socket.LocalEndpoint).Port; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task<string> Expect(HttpResponseMessage response, int status)
    {
        Check((int)response.StatusCode == status, $"Knowledge HTTP expected {status}, received {(int)response.StatusCode}.");
        Check(response.Headers.CacheControl?.NoStore == true, "Knowledge and BFF responses must prohibit caching.");
        return await response.Content.ReadAsStringAsync();
    }
    private static async Task Denied(HttpResponseMessage response, int status) =>
        Check(!(await Expect(response, status)).Contains(PrivateText, StringComparison.Ordinal), "Rejected requests must not disclose document content.");
    private sealed class Gate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Browser(HttpClient client, string origin, string csrfToken) : IDisposable
    {
        public static async Task<Browser> Login(string origin)
        {
            var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, CookieContainer = new() }) { BaseAddress = new(origin), Timeout = TimeSpan.FromSeconds(15) };
            try
            {
                using var challenge = await client.GetAsync("/bff/login?returnUrl=/bff/session");
                using var code = await client.GetAsync(challenge.Headers.Location);
                using var callback = await client.GetAsync(code.Headers.Location);
                Check(callback.StatusCode == HttpStatusCode.Redirect, "OIDC must establish a real BFF session.");
                using var response = await client.GetAsync("/bff/csrf");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return new(client, origin, json.RootElement.GetProperty("requestToken").GetString()!);
            }
            catch { client.Dispose(); throw; }
        }
        public async Task<HttpResponseMessage> Send(string method, string path, HttpContent? content = null, bool csrf = true, string? requestOrigin = null)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = content };
            if (method is not "GET") { request.Headers.Add("Origin", requestOrigin ?? origin); if (csrf) request.Headers.Add("X-CSRF-TOKEN", csrfToken); }
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }
        public async Task Select(Guid tenant) { using var response = await Send("POST", "/bff/tenant", JsonContent.Create(new { tenantId = tenant })); await Expect(response, 200); }
        public void Dispose() => client.Dispose();
    }

    private sealed class State(string issuer) : IAuthorizationStore, IKnowledgeStore, IKnowledgeObjects
    {
        public Guid Tenant { get; } = Guid.NewGuid(); public Guid OtherTenant { get; } = Guid.NewGuid();
        public Guid Factory { get; } = Guid.NewGuid(); public Guid OtherFactory { get; } = Guid.NewGuid(); public Guid User { get; } = Guid.NewGuid();
        public bool Allowed = true, FailObjects, FailStore, Corrupt, CreateBeforeObject;
        public int ObjectPuts, ObjectDeletes, Creates;
        public Gate? NextGet, NextPut;
        public ConcurrentDictionary<Guid, KnowledgeDocument> Documents { get; } = new();
        public ConcurrentDictionary<(Guid Tenant, Guid Document), byte[]> Objects { get; } = new();
        public Task<IReadOnlyList<TenantMembership>> ListTenantsAsync(SubjectIdentity identity, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TenantMembership>>([new(Tenant, "A"), new(OtherTenant, "B")]);
        public Task<AccessSnapshot?> ReadAccessAsync(SubjectIdentity identity, Guid tenantId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (identity != new SubjectIdentity(issuer, OidcFixture.Subject) || tenantId != Tenant && tenantId != OtherTenant) return Task.FromResult<AccessSnapshot?>(null);
            var scope = new HashSet<Guid> { Factory };
            return Task.FromResult<AccessSnapshot?>(new(User, tenantId, Allowed ? 1 : 2,
                Allowed ? [new("knowledge:read", "knowledge", "documents", false, scope), new("knowledge:manage", "knowledge", "documents", false, scope)] : [],
                new HashSet<Guid> { Factory, OtherFactory }));
        }
        public Task<KnowledgeDocument> CreateAsync(KnowledgeDocument document, string requestId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); if (FailStore) throw new KnowledgeStoreUnavailableException();
            if (!Objects.ContainsKey((document.TenantId, document.DocumentId))) CreateBeforeObject = true;
            Check(document.CreatedBy == User && document.Sha256 == Convert.ToHexStringLower(SHA256.HashData(Bytes)) && requestId.Length == 32,
                "Trusted upload metadata and audit correlation must be server-derived.");
            Creates++; Documents[document.DocumentId] = document; return Task.FromResult(document);
        }
        public Task<KnowledgeDocument?> ReadAsync(Guid tenant, Guid document, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (FailStore) throw new KnowledgeStoreUnavailableException(); return Task.FromResult(Documents.TryGetValue(document, out var value) && value.TenantId == tenant ? value : null); }
        public Task<IReadOnlyList<KnowledgeDocument>> ListAsync(Guid tenant, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (FailStore) throw new KnowledgeStoreUnavailableException(); return Task.FromResult<IReadOnlyList<KnowledgeDocument>>(Documents.Values.Where(d => d.TenantId == tenant).ToArray()); }
        public Task<bool> DeleteAsync(Guid tenant, Guid document, long expectedRevision, Guid actor, string requestId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); if (FailStore) throw new KnowledgeStoreUnavailableException();
            if (!Documents.TryGetValue(document, out var value) || value.TenantId != tenant || value.Revision != expectedRevision) return Task.FromResult(false);
            Check(actor == User && requestId.Length == 32, "Delete audit must use the verified actor and server request ID.");
            Documents[document] = value with { Status = "deleted", Revision = expectedRevision + 1 }; return Task.FromResult(true);
        }
        public async Task PutAsync(Guid tenant, Guid document, byte[] content, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); if (FailObjects) throw new KnowledgeObjectUnavailableException();
            ObjectPuts++; Objects[(tenant, document)] = content.ToArray();
            if (Interlocked.Exchange(ref NextPut, null) is { } gate) { gate.Entered.TrySetResult(); await gate.Release.Task.WaitAsync(token); }
        }
        public async Task<byte[]> GetAsync(Guid tenant, Guid document, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); if (FailObjects) throw new KnowledgeObjectUnavailableException();
            if (Interlocked.Exchange(ref NextGet, null) is { } gate) { gate.Entered.TrySetResult(); await gate.Release.Task.WaitAsync(token); }
            var value = Objects[(tenant, document)].ToArray(); if (Corrupt) value[0] ^= 1; return value;
        }
        public Task DeleteAsync(Guid tenant, Guid document, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (FailObjects) throw new KnowledgeObjectUnavailableException(); ObjectDeletes++; Objects.TryRemove((tenant, document), out _); return Task.CompletedTask; }
    }
}
