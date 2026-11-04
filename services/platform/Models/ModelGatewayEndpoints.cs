using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Identity;
using AssembleAI.Platform.Identity.Sessions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AssembleAI.Platform.Models;

public static class ModelGatewayEndpoints
{
    private static readonly object SessionKey = new();
    private static readonly object DisconnectKey = new();
    public static WebApplicationBuilder AddPlatformModels(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ModelGatewayCatalog>();
        builder.Services.AddSingleton<ModelDelegationRegistry>();
        builder.Services.TryAddSingleton<IModelStore, PostgresModelStore>();
        builder.Services.AddHttpClient<OpenAiModelClient>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10),
                AutomaticDecompression = System.Net.DecompressionMethods.None,
            }).RemoveAllLoggers();
        return builder;
    }
    public static WebApplication MapModelEndpoints(this WebApplication app)
    {
        var routes = app.MapGroup("/api/platform/v1/tenants/{tenantId:guid}/models")
            .RequireAuthorization(IdentityEndpoints.IdentityPolicy);
        routes.AddEndpointFilter(Boundary);
        routes.MapGet("", List);
        routes.MapGet("/{modelId}/configuration", ReadConfiguration);
        routes.MapPut("/{modelId}/configuration", SaveConfiguration);
        routes.MapGet("/{modelId}/calls", Calls);
        routes.MapPost("/{modelId}/chat", Chat);
        app.MapPost("/internal/models/{modelId}/chat", ModelDelegationEndpoints.Chat);
        return app;
    }
    private static async ValueTask<object?> Boundary(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (context.Request.Query.Count != 0) return Problem(400, "invalid_model_request");
        var catalog = context.RequestServices.GetRequiredService<ModelGatewayCatalog>();
        if (!catalog.Options.Enabled) return Problem(503, "models_unavailable");
        var disconnected = context.RequestAborted;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(disconnected);
        budget.CancelAfter(TimeSpan.FromSeconds(context.Request.Path.Value!.EndsWith("/chat", StringComparison.Ordinal)
            ? catalog.Options.CallSeconds : 3));
        try
        {
            if (!Guid.TryParse(context.Request.RouteValues["tenantId"]?.ToString(), out var tenant) || tenant == Guid.Empty)
                return Problem(400, "invalid_model_request");
            using var captureBudget = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            captureBudget.CancelAfter(TimeSpan.FromSeconds(3));
            var session = await LiveBffSession.CaptureAsync(context, tenant, captureBudget.Token);
            if (session is null) return Problem(403, "model_access_denied");
            context.Items[SessionKey] = session;
            context.Items[DisconnectKey] = disconnected;
            context.RequestAborted = budget.Token;
            var result = await next(invocation);
            if (HttpMethods.IsGet(context.Request.Method) && !await session.CheckAsync(budget.Token))
                return Problem(403, "model_access_denied");
            return result;
        }
        catch (Exception error) when (Unavailable(error))
        { return Problem(503, "models_unavailable"); }
        catch (OperationCanceledException) when (!disconnected.IsCancellationRequested)
        { return Problem(503, "model_timeout"); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { return Problem(400, "invalid_model_request"); }
        finally
        {
            context.RequestAborted = disconnected;
            context.Items.Remove(SessionKey);
            context.Items.Remove(DisconnectKey);
        }
    }
    private static LiveBffSession Session(HttpContext context) => (LiveBffSession)context.Items[SessionKey]!;
    private static bool Allows(AccessSnapshot? access, string permission, Guid tenant, string model) =>
        PermissionEvaluator.Allows(access, permission, new(tenant, "model", model, null));
    private static Task<AccessSnapshot?> Access(HttpContext context, Guid tenant, IAuthorizationStore authorization) =>
        authorization.ReadAccessAsync(Session(context).Subject, tenant, context.RequestAborted);
    private static async Task<IResult> List(Guid tenantId, HttpContext context, ModelGatewayCatalog catalog,
        IAuthorizationStore authorization, IModelStore store)
    {
        var access = await Access(context, tenantId, authorization);
        if (access is null) return Problem(403, "model_access_denied");
        var items = new List<object>();
        foreach (var (id, model) in catalog.Options.Catalog)
        {
            if (!Allows(access, "model:invoke", tenantId, id) && !Allows(access, "model:manage", tenantId, id)) continue;
            var configuration = await store.ReadConfigurationAsync(tenantId, id, context.RequestAborted);
            items.Add(new { modelId = id, model.DisplayName, enabled = model.Enabled && configuration?.Enabled == true,
                maximumOutputTokens = Math.Min(model.MaximumOutputTokens, configuration?.MaxOutputTokens ?? model.MaximumOutputTokens),
                canManage = Allows(access, "model:manage", tenantId, id) });
        }
        var fresh = await Access(context, tenantId, authorization);
        if (fresh is null || fresh.UserId != access.UserId || fresh.AuthorizationVersion != access.AuthorizationVersion)
            return Problem(403, "model_access_denied");
        return Results.Ok(new { items });
    }
    private static async Task<IResult> ReadConfiguration(Guid tenantId, string modelId, HttpContext context,
        ModelGatewayCatalog catalog, IAuthorizationStore authorization, IModelStore store)
    {
        if (!Allows(await Access(context, tenantId, authorization), "model:manage", tenantId, modelId)) return Problem(403, "model_access_denied");
        if (catalog.Find(modelId) is null) return Problem(404, "model_not_found");
        var value = await store.ReadConfigurationAsync(tenantId, modelId, context.RequestAborted);
        if (!Allows(await Access(context, tenantId, authorization), "model:manage", tenantId, modelId)) return Problem(403, "model_access_denied");
        return Results.Ok(value);
    }
    private static async Task<IResult> SaveConfiguration(Guid tenantId, string modelId, HttpContext context,
        ModelGatewayCatalog catalog, IAuthorizationStore authorization, IModelStore store, IAntiforgery antiforgery, BffOptions settings)
    {
        if (!await BffEndpoints.IsVerifiedRequest(context, antiforgery, settings)) return Problem(403, "request_verification_failed");
        if (!Allows(await Access(context, tenantId, authorization), "model:manage", tenantId, modelId)) return Problem(403, "model_access_denied");
        var model = catalog.Find(modelId);
        if (model is null) return Problem(404, "model_not_found");
        using var document = await ModelRequestReader.ReadAsync(context.Request, context.RequestAborted);
        if (document is null) return Problem(400, "invalid_model_request");
        var body = document.RootElement;
        if (!ModelRequestReader.Fields(body, "enabled", "maxOutputTokens", "expectedRevision")
            || !body.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !body.TryGetProperty("maxOutputTokens", out var max) || max.ValueKind != JsonValueKind.Number || !max.TryGetInt32(out var limit)
            || limit < 1 || limit > model.MaximumOutputTokens || !body.TryGetProperty("expectedRevision", out var expected)
            || expected.ValueKind != JsonValueKind.Number || !expected.TryGetInt64(out var revision) || revision < 0)
            return Problem(400, "invalid_model_request");
        var access = await Access(context, tenantId, authorization);
        if (!Allows(access, "model:manage", tenantId, modelId) || !await Session(context).CheckAsync(context.RequestAborted))
            return Problem(403, "model_access_denied");
        var result = await store.SaveConfigurationAsync(tenantId, modelId, enabled.GetBoolean(), limit, revision,
            access!.UserId, Guid.NewGuid().ToString("N"), context.RequestAborted);
        return result is null ? Problem(409, "model_configuration_conflict") : Results.Ok(result);
    }
    private static async Task<IResult> Calls(Guid tenantId, string modelId, HttpContext context,
        IAuthorizationStore authorization, IModelStore store)
    {
        if (!Allows(await Access(context, tenantId, authorization), "model:manage", tenantId, modelId)) return Problem(403, "model_access_denied");
        var items = await store.ListCallsAsync(tenantId, modelId, context.RequestAborted);
        if (!Allows(await Access(context, tenantId, authorization), "model:manage", tenantId, modelId)) return Problem(403, "model_access_denied");
        return Results.Ok(new { items });
    }
    private static async Task<IResult> Chat(Guid tenantId, string modelId, HttpContext context,
        ModelGatewayCatalog catalog, IAuthorizationStore authorization, IModelStore store,
        OpenAiModelClient client, IAntiforgery antiforgery, BffOptions settings)
    {
        if (!await BffEndpoints.IsVerifiedRequest(context, antiforgery, settings)) return Problem(403, "request_verification_failed");
        var access = await Access(context, tenantId, authorization);
        if (access is null) return Problem(403, "model_access_denied");
        var session = Session(context);
        var caller = new ModelCaller(session.Subject, tenantId, access.UserId, session.ExpiresAt, session.CheckAsync, CancellationToken.None);
        return await ModelChatExecutor.RunAsync(context, modelId, caller, catalog, authorization, store, client,
            (CancellationToken)context.Items[DisconnectKey]!);
    }
    private static bool Unavailable(Exception error) => error is ModelStoreUnavailableException
        or AuthorizationStoreUnavailableException or BffSessionStoreException;
    private static IResult Problem(int status, string code) => Results.Problem(statusCode: status,
        title: "Model request could not be completed.", extensions: new Dictionary<string, object?> { ["code"] = code });
}
