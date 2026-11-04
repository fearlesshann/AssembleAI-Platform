using System.Text.Json;
using AssembleAI.Platform.Authorization;

namespace AssembleAI.Platform.Models;

public static class ModelDelegationEndpoints
{
    public static async Task<IResult> Chat(string modelId, HttpContext context, ModelDelegationRegistry delegations,
        ModelGatewayCatalog catalog, IAuthorizationStore authorization, IModelStore store, OpenAiModelClient client)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (!ModelGatewayCatalog.ValidId(modelId) || context.Request.QueryString.HasValue)
            return ModelChatExecutor.Problem(400, "invalid_model_request");
        if (!catalog.Options.Enabled) return ModelChatExecutor.Problem(503, "models_unavailable");
        var headers = context.Request.Headers.Authorization;
        if (headers.Count != 1 || headers[0] is not { Length: 71 } bearer || !bearer.StartsWith("Bearer ", StringComparison.Ordinal))
            return ModelChatExecutor.Problem(401, "model_delegation_required");
        var caller = delegations.Resolve(bearer[7..], modelId);
        if (caller is null) return ModelChatExecutor.Problem(403, "model_access_denied");
        var disconnected = context.RequestAborted;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(disconnected, caller.Lifetime);
        budget.CancelAfter(TimeSpan.FromSeconds(catalog.Options.CallSeconds));
        context.RequestAborted = budget.Token;
        try
        {
            using var check = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            check.CancelAfter(TimeSpan.FromSeconds(3));
            if (!await caller.CheckAsync(check.Token)) return ModelChatExecutor.Problem(403, "model_access_denied");
            return await ModelChatExecutor.RunAsync(context, modelId, caller, catalog, authorization, store, client, disconnected);
        }
        catch (Exception error) when (ModelChatExecutor.Unavailable(error))
        { return ModelChatExecutor.Problem(503, "models_unavailable"); }
        catch (OperationCanceledException) when (!disconnected.IsCancellationRequested)
        { return ModelChatExecutor.Problem(caller.Lifetime.IsCancellationRequested ? 403 : 503,
            caller.Lifetime.IsCancellationRequested ? "model_access_denied" : "model_timeout"); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { return ModelChatExecutor.Problem(400, "invalid_model_request"); }
        finally { context.RequestAborted = disconnected; }
    }
}
