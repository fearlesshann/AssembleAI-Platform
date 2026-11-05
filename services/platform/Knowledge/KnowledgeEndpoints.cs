using System.Security.Cryptography;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Identity;
using AssembleAI.Platform.Identity.Sessions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AssembleAI.Platform.Knowledge;

public static class KnowledgeEndpoints
{
    private static readonly object SessionKey = new();
    public static WebApplicationBuilder AddPlatformKnowledge(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("Knowledge").Get<KnowledgeOptions>() ?? new();
        builder.Services.AddAntiforgery();
        builder.Services.AddSingleton(options);
        builder.Services.TryAddSingleton<PostgresKnowledgeStore>();
        builder.Services.TryAddSingleton<IKnowledgeStore>(p => p.GetRequiredService<PostgresKnowledgeStore>());
        builder.Services.TryAddSingleton<IKnowledgeJobStore>(p => p.GetRequiredService<PostgresKnowledgeStore>());
        builder.Services.TryAddSingleton<IKnowledgeIndexStore>(p => p.GetRequiredService<PostgresKnowledgeStore>());
        builder.Services.TryAddSingleton<IKnowledgeSearchStore>(p => p.GetRequiredService<PostgresKnowledgeStore>());
        builder.Services.TryAddSingleton<IKnowledgeBrokerStore>(p => p.GetRequiredService<PostgresKnowledgeStore>());
        builder.Services.TryAddSingleton<KnowledgeDelegationRegistry>();
        builder.Services.TryAddSingleton<KnowledgeSearchAdmission>();
        builder.Services.AddKnowledgeObjects(builder.Configuration);
        builder.Services.AddHttpClient<KnowledgeParserClient>(http => http.Timeout = TimeSpan.FromSeconds(25))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(3) })
            .RemoveAllLoggers();
        builder.Services.AddHttpClient<KnowledgeEmbeddingClient>(http => http.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(3) })
            .RemoveAllLoggers();
        if (options.Enabled && options.WorkerEnabled)
        {
            builder.Services.TryAddSingleton<KnowledgeWorker>();
            if (options.BrokerEnabled) builder.Services.AddHostedService<KnowledgeBrokerWorker>();
            else builder.Services.AddHostedService(p => p.GetRequiredService<KnowledgeWorker>());
        }
        return builder;
    }
    public static WebApplication MapKnowledgeEndpoints(this WebApplication app)
    {
        var routes = app.MapGroup("/api/platform/v1/tenants/{tenantId:guid}/knowledge/documents")
            .RequireAuthorization(IdentityEndpoints.IdentityPolicy);
        routes.AddEndpointFilter(Boundary);
        routes.MapGet("", List);
        routes.MapPost("", Upload);
        routes.MapGet("/{documentId:guid}", Read);
        routes.MapGet("/{documentId:guid}/content", Content);
        routes.MapDelete("/{documentId:guid}", Delete);
        return app;
    }
    private static async ValueTask<object?> Boundary(EndpointFilterInvocationContext call, EndpointFilterDelegate next)
    {
        var context = call.HttpContext;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (!context.RequestServices.GetRequiredService<KnowledgeOptions>().Enabled) return Problem(503, "knowledge_unavailable");
        if (context.Request.Query.Count != 0 && !HttpMethods.IsPost(context.Request.Method)) return Problem(400, "invalid_document_request");
        var disconnected = context.RequestAborted;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(disconnected);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        context.RequestAborted = budget.Token;
        try
        {
            var tenant = Guid.Parse(context.Request.RouteValues["tenantId"]!.ToString()!);
            using var capture = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            capture.CancelAfter(TimeSpan.FromSeconds(3));
            var session = await LiveBffSession.CaptureAsync(context, tenant, capture.Token);
            if (session is null) return Problem(403, "document_access_denied");
            context.Items[SessionKey] = session;
            var result = await next(call);
            if (!await session.CheckAsync(budget.Token)) return Problem(403, "document_access_denied");
            return result;
        }
        catch (Exception error) when (Unavailable(error)) { return Problem(503, "knowledge_unavailable"); }
        catch (OperationCanceledException) when (!disconnected.IsCancellationRequested) { return Problem(504, "knowledge_timeout"); }
        catch (JsonException) { return Problem(400, "invalid_document_request"); }
        finally { context.RequestAborted = disconnected; context.Items.Remove(SessionKey); }
    }
    private static LiveBffSession Session(HttpContext context) => (LiveBffSession)context.Items[SessionKey]!;
    private static Task<AccessSnapshot?> Access(HttpContext context, Guid tenant, IAuthorizationStore store) =>
        store.ReadAccessAsync(Session(context).Subject, tenant, context.RequestAborted);
    private static bool CanRead(AccessSnapshot? access, KnowledgeDocument doc) =>
        KnowledgeAuthorization.Allows(access, "knowledge:read", doc.TenantId, doc.OrganizationId, doc.DocumentId)
        || KnowledgeAuthorization.Allows(access, "knowledge:manage", doc.TenantId, doc.OrganizationId, doc.DocumentId);
    private static async Task<IResult> List(Guid tenantId, HttpContext context, IAuthorizationStore authorization, IKnowledgeStore store)
    {
        var access = await Access(context, tenantId, authorization);
        if (access is null) return Problem(403, "document_access_denied");
        var documents = await store.ListAsync(tenantId, context.RequestAborted);
        var fresh = await Access(context, tenantId, authorization);
        if (fresh?.UserId != access.UserId || fresh?.AuthorizationVersion != access.AuthorizationVersion) return Problem(403, "document_access_denied");
        return Results.Ok(new { items = documents.Where(d => d.Status != "deleted" && CanRead(fresh, d)), limit = 100 });
    }
    private static async Task<IResult> Read(Guid tenantId, Guid documentId, HttpContext context, IAuthorizationStore authorization, IKnowledgeStore store)
    {
        var document = await store.ReadAsync(tenantId, documentId, context.RequestAborted);
        if (document is null || document.Status == "deleted" || !CanRead(await Access(context, tenantId, authorization), document)) return Problem(404, "document_not_found");
        return Results.Ok(document);
    }
    private static async Task<IResult> Content(Guid tenantId, Guid documentId, HttpContext context, IAuthorizationStore authorization,
        IKnowledgeStore store, IKnowledgeObjects objects)
    {
        var doc = await store.ReadAsync(tenantId, documentId, context.RequestAborted);
        if (doc is null || doc.Status == "deleted" || !CanRead(await Access(context, tenantId, authorization), doc)) return Problem(404, "document_not_found");
        var bytes = await objects.GetAsync(tenantId, documentId, context.RequestAborted);
        var current = await store.ReadAsync(tenantId, documentId, context.RequestAborted);
        if (current is null || current.Status == "deleted" || current.Revision != doc.Revision
            || !CanRead(await Access(context, tenantId, authorization), current)) return Problem(404, "document_not_found");
        if (bytes.LongLength != doc.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != doc.Sha256)
            return Problem(503, "document_integrity_failed");
        // Attachment + octet-stream avoids executing Markdown/HTML in the platform origin.
        return Results.File(bytes, "application/octet-stream", doc.FileName, enableRangeProcessing: false);
    }
    private static async Task<IResult> Upload(Guid tenantId, HttpContext context, IAuthorizationStore authorization,
        IKnowledgeStore store, IKnowledgeObjects objects, IAntiforgery antiforgery, BffOptions bff)
    {
        if (!await BffEndpoints.IsVerifiedRequest(context, antiforgery, bff)) return Problem(403, "request_verification_failed");
        var query = context.Request.Query;
        if (query.Keys.Any(k => k is not ("filename" or "organizationId")) || query["filename"].Count != 1
            || !ValidName(query["filename"].ToString(), out var mediaType)) return Problem(400, "invalid_document_request");
        Guid? organization = null;
        if (query.ContainsKey("organizationId"))
        {
            if (query["organizationId"].Count != 1 || !Guid.TryParse(query["organizationId"], out var id) || id == Guid.Empty) return Problem(400, "invalid_document_request");
            organization = id;
        }
        var access = await Access(context, tenantId, authorization);
        if (!KnowledgeAuthorization.Allows(access, "knowledge:manage", tenantId, organization)) return Problem(403, "document_access_denied");
        if (context.Request.ContentType != "application/octet-stream" || context.Request.Headers.ContentEncoding.Count != 0) return Problem(400, "invalid_document_request");
        var content = await ReadBytes(context.Request, KnowledgeOptions.MaximumBytes, context.RequestAborted);
        if (content is null || content.Length == 0) return Problem(413, "document_size_limit");
        var fresh = await Access(context, tenantId, authorization);
        if (fresh?.UserId != access!.UserId || !KnowledgeAuthorization.Allows(fresh, "knowledge:manage", tenantId, organization)
            || !await Session(context).CheckAsync(context.RequestAborted)) return Problem(403, "document_access_denied");
        var documentId = Guid.NewGuid();
        await objects.PutAsync(tenantId, documentId, content, context.RequestAborted);
        fresh = await Access(context, tenantId, authorization);
        if (fresh?.UserId != access.UserId || !KnowledgeAuthorization.Allows(fresh, "knowledge:manage", tenantId, organization)
            || !await Session(context).CheckAsync(context.RequestAborted))
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await objects.DeleteAsync(tenantId, documentId, cleanup.Token); } catch (Exception e) when (e is KnowledgeObjectUnavailableException or OperationCanceledException) { }
            return Problem(403, "document_access_denied");
        }
        // A database timeout has ambiguous commit outcome: never delete a potentially committed object.
        var document = await store.CreateAsync(new(tenantId, documentId, query["filename"].ToString(), mediaType!, content.Length,
            Convert.ToHexStringLower(SHA256.HashData(content)), organization, "queued", 1, access.UserId, DateTimeOffset.UtcNow), Guid.NewGuid().ToString("N"), context.RequestAborted);
        return Results.Json(document, statusCode: 202);
    }
    private static async Task<IResult> Delete(Guid tenantId, Guid documentId, HttpContext context, IAuthorizationStore authorization,
        IKnowledgeStore store, IAntiforgery antiforgery, BffOptions bff)
    {
        if (!await BffEndpoints.IsVerifiedRequest(context, antiforgery, bff)) return Problem(403, "request_verification_failed");
        var doc = await store.ReadAsync(tenantId, documentId, context.RequestAborted);
        if (doc is null || !KnowledgeAuthorization.Allows(await Access(context, tenantId, authorization), "knowledge:manage", tenantId, doc.OrganizationId, documentId))
            return Problem(404, "document_not_found");
        if (!context.Request.HasJsonContentType() || context.Request.Headers.ContentEncoding.Count != 0) return Problem(400, "invalid_document_request");
        var bytes = await ReadBytes(context.Request, 1024, context.RequestAborted);
        if (bytes is null) return Problem(400, "invalid_document_request");
        using var json = JsonDocument.Parse(bytes, new() { MaxDepth = 2 });
        if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.EnumerateObject().Count() != 1
            || !json.RootElement.TryGetProperty("expectedRevision", out var field) || field.ValueKind != JsonValueKind.Number
            || !field.TryGetInt64(out var revision) || revision <= 0 || revision == long.MaxValue) return Problem(400, "invalid_document_request");
        var access = await Access(context, tenantId, authorization);
        if (!KnowledgeAuthorization.Allows(access, "knowledge:manage", tenantId, doc.OrganizationId, documentId)
            || !await Session(context).CheckAsync(context.RequestAborted)) return Problem(403, "document_access_denied");
        return await store.DeleteAsync(tenantId, documentId, revision, access!.UserId, Guid.NewGuid().ToString("N"), context.RequestAborted)
            ? Results.NoContent() : Problem(409, "document_revision_conflict");
    }
    private static bool ValidName(string name, out string? mediaType)
    {
        mediaType = Path.GetExtension(name).ToLowerInvariant() switch { ".pdf" => "application/pdf", ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".txt" => "text/plain", ".md" => "text/markdown", _ => null };
        return name.Length is >= 1 and <= 200 && name == name.Trim() && !name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)) && mediaType is not null;
    }
    private static async Task<byte[]?> ReadBytes(HttpRequest request, int maximum, CancellationToken token)
    {
        if (request.ContentLength > maximum) return null;
        using var buffer = new MemoryStream(); var part = new byte[8192]; int count;
        while ((count = await request.Body.ReadAsync(part, token)) != 0)
        { if (buffer.Length + count > maximum) return null; buffer.Write(part, 0, count); }
        return buffer.ToArray();
    }
    internal static bool Unavailable(Exception error) => error is KnowledgeStoreUnavailableException or KnowledgeObjectUnavailableException
        or AuthorizationStoreUnavailableException or BffSessionStoreException;
    private static IResult Problem(int status, string code) => Results.Problem(statusCode: status, title: "Document request could not be completed.", extensions: new Dictionary<string, object?> { ["code"] = code });
}
