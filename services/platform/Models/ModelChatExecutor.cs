using System.Diagnostics;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Gateway;
using AssembleAI.Platform.Identity.Sessions;

namespace AssembleAI.Platform.Models;

/// <summary>Shared model execution for BFF users and active application delegations.</summary>
public static class ModelChatExecutor
{
    private static readonly JsonSerializerOptions StreamJson = new(JsonSerializerDefaults.Web);
    public static async Task<IResult> RunAsync(HttpContext context, string modelId, ModelCaller caller,
        ModelGatewayCatalog catalog, IAuthorizationStore authorization, IModelStore store,
        OpenAiModelClient client, CancellationToken disconnected)
    {
        var tenantId = caller.TenantId;
        var access = await authorization.ReadAccessAsync(caller.Subject, tenantId, context.RequestAborted);
        if (access?.UserId != caller.UserId || !Allows(access, "model:invoke", tenantId, modelId))
            return Problem(403, "model_access_denied");
        var model = catalog.Find(modelId);
        if (model is null || !model.Enabled) return Problem(403, "model_access_denied");
        var configuration = await store.ReadConfigurationAsync(tenantId, modelId, context.RequestAborted);
        if (configuration is null || !configuration.Enabled) return Problem(403, "model_access_denied");
        using var document = await ModelRequestReader.ReadAsync(context.Request, context.RequestAborted);
        var request = document is null ? null : ModelRequestReader.Chat(document.RootElement,
            Math.Min(model.MaximumOutputTokens, configuration.MaxOutputTokens));
        if (request is null) return Problem(400, "invalid_model_request");
        var provider = catalog.Options.Providers[model.Provider];
        var credential = ModelGatewayCatalog.ReadCredential(provider);
        using var slot = catalog.Admission.TryAcquire(tenantId, access!.UserId);
        if (slot is null) return Problem(429, "model_rate_limited");
        if (!await Current(context.RequestAborted)) return Problem(403, "model_access_denied");
        var requestId = Guid.NewGuid().ToString("N");
        // Persist admission before any provider side effect. Never record content.
        var callId = await store.BeginCallAsync(tenantId, access.UserId, modelId, requestId, context.RequestAborted, caller.Source);
        var stopwatch = Stopwatch.StartNew();
        var outcome = "failed";
        ModelUsage? usage = null;
        var finalized = false;
        var reason = "timeout";
        var clock = context.RequestServices.GetRequiredService<TimeProvider>();
        var remainingSession = caller.ExpiresAt - clock.GetUtcNow();
        using var expires = new CancellationTokenSource(remainingSession > TimeSpan.Zero ? remainingSession : TimeSpan.Zero, clock);
        using var running = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, expires.Token, caller.Lifetime);
        using var monitorStop = new CancellationTokenSource();
        var monitor = Monitor();
        try
        {
            if (!await Current(running.Token)) { reason = "revoked"; running.Cancel(); running.Token.ThrowIfCancellationRequested(); }
            if (!request.Stream)
            {
                var result = await client.CompleteAsync(new(provider.Endpoint), model.UpstreamModel, credential, request, running.Token, model.ReasoningEffort);
                usage = result.Usage;
                if (!await Current(running.Token)) { reason = "revoked"; running.Cancel(); running.Token.ThrowIfCancellationRequested(); }
                await Finish("completed");
                if (!await Current(running.Token)) { reason = "revoked"; running.Cancel(); running.Token.ThrowIfCancellationRequested(); }
                return Results.Ok(new { result.Content, result.FinishReason, result.Usage, requestId });
            }
            await foreach (var item in client.StreamAsync(new(provider.Endpoint), model.UpstreamModel, credential, request, running.Token, model.ReasoningEffort))
            {
                if (item.Usage is not null) usage = item.Usage;
                if (item.Type == "complete")
                {
                    if (!await Current(running.Token)) { reason = "revoked"; running.Cancel(); running.Token.ThrowIfCancellationRequested(); }
                    await Finish("completed");
                    if (!await Current(running.Token)) { reason = "revoked"; running.Cancel(); running.Token.ThrowIfCancellationRequested(); }
                }
                if (!context.Response.HasStarted)
                {
                    context.Response.ContentType = "text/event-stream; charset=utf-8";
                    context.Response.Headers["X-Request-ID"] = requestId;
                }
                var data = JsonSerializer.Serialize(new { content = item.Content, finishReason = item.FinishReason, usage = item.Usage, requestId }, StreamJson);
                await context.Response.WriteAsync($"event: {item.Type}\ndata: {data}\n\n", running.Token);
                await context.Response.Body.FlushAsync(running.Token);
            }
            return Results.Empty;
        }
        catch (ModelUpstreamException)
        {
            outcome = "upstream_error";
            if (context.Response.HasStarted) { context.Abort(); return Results.Empty; }
            return Problem(502, "model_upstream_error");
        }
        catch (OperationCanceledException)
        {
            outcome = disconnected.IsCancellationRequested ? "cancelled"
                : expires.IsCancellationRequested || caller.Lifetime.IsCancellationRequested ? "revoked"
                : context.RequestAborted.IsCancellationRequested ? "timeout" : reason;
            if (context.Response.HasStarted) { context.Abort(); return Results.Empty; }
            return Problem(outcome == "revoked" ? 403 : 503, outcome == "revoked" ? "model_access_denied" : "model_timeout");
        }
        catch (Exception error) when (Unavailable(error))
        {
            if (context.Response.HasStarted) { context.Abort(); return Results.Empty; }
            return Problem(503, "models_unavailable");
        }
        finally
        {
            monitorStop.Cancel();
            await monitor;
            if (!finalized)
                try { await Finish(outcome); }
                catch (ModelStoreUnavailableException) { /* Durable admission remains unfinished; never claim success. */ }
                catch (OperationCanceledException) { /* Completion storage has its own finite budget. */ }
        }
        async Task Finish(string result)
        {
            using var auditBudget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await store.CompleteCallAsync(callId, result, stopwatch.ElapsedMilliseconds, usage, auditBudget.Token);
            outcome = result;
            finalized = true;
        }
        async Task<bool> Current(CancellationToken token)
        {
            using var check = CancellationTokenSource.CreateLinkedTokenSource(token);
            check.CancelAfter(TimeSpan.FromSeconds(3));
            if (!await caller.CheckAsync(check.Token)) return false;
            var fresh = await authorization.ReadAccessAsync(caller.Subject, tenantId, check.Token);
            if (fresh?.UserId != caller.UserId || !Allows(fresh, "model:invoke", tenantId, modelId)) return false;
            var current = await store.ReadConfigurationAsync(tenantId, modelId, check.Token);
            return current is { Enabled: true } && current.Revision == configuration.Revision;
        }
        async Task Monitor()
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(running.Token, monitorStop.Token);
            try
            {
                while (true)
                {
                    await Task.Delay(catalog.Options.RecheckMilliseconds, stop.Token);
                    if (!await Current(stop.Token)) { reason = "revoked"; running.Cancel(); return; }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception error) when (Unavailable(error) || error is OperationCanceledException)
            { reason = "failed"; running.Cancel(); }
        }
    }
    private static bool Allows(AccessSnapshot? access, string permission, Guid tenant, string model) =>
        PermissionEvaluator.Allows(access, permission, new(tenant, "model", model, null));
    internal static bool Unavailable(Exception error) => error is ModelStoreUnavailableException
        or AuthorizationStoreUnavailableException or BffSessionStoreException or GatewayStoreUnavailableException;
    internal static IResult Problem(int status, string code) => Results.Problem(statusCode: status,
        title: "Model request could not be completed.", extensions: new Dictionary<string, object?> { ["code"] = code });
}
