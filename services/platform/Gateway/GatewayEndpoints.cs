using System.Diagnostics;
using System.Net.Http.Headers;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Identity;
using AssembleAI.Platform.Identity.Sessions;
using AssembleAI.Platform.InternalCalls;
using AssembleAI.Platform.Models;
using AssembleAI.Platform.Registry;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Yarp.ReverseProxy.Forwarder;

namespace AssembleAI.Platform.Gateway;

public static class GatewayEndpoints
{
    private static readonly object RequestIdKey = new();
    public static WebApplicationBuilder AddPlatformGateway(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("Gateway").Get<GatewayOptions>() ?? new();
        options.Validate();
        if (options.Enabled && (!builder.Configuration.GetValue<bool>("Authentication:Bff:Enabled")
            || !builder.Configuration.GetValue<bool>("InternalCalls:Enabled")
            || string.IsNullOrWhiteSpace(builder.Configuration["InternalCalls:SigningCertificatePath"])))
            throw new InvalidOperationException("Gateway requires BFF authentication and an internal signing key.");
        builder.Services.AddSingleton(options);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IGatewayStore, PostgresGatewayStore>();
        builder.Services.AddSingleton<GatewayTransport>();
        builder.Services.AddSingleton(provider => new GatewayAdmission(provider.GetRequiredService<TimeProvider>(),
            options.PerUserConcurrency, options.GlobalConcurrency, options.RequestsPerMinute, options.MaximumPartitions));
        return builder;
    }

    public static WebApplication MapGatewayEndpoints(this WebApplication app)
    {
        app.MapPost("/api/platform/v1/tenants/{tenantId:guid}/applications/{appId}/releases/{version}/runs", StartRun)
            .RequireAuthorization(IdentityEndpoints.IdentityPolicy);
        app.MapMethods("/api/apps/{appId}/releases/{version}/{**operation}", ["GET", "POST"], Forward)
            .RequireAuthorization(IdentityEndpoints.IdentityPolicy);
        return app;
    }

    private static async Task StartRun(HttpContext context, Guid tenantId, string appId, string version, GatewayOptions options,
        IGatewayStore store, RegistryDeploymentCatalog catalog, GatewayTransport transport, IAuthorizationStore authorization,
        GatewayAdmission admission)
    {
        Prepare(context);
        try
        {
            if (!options.Enabled) { await Problem(context, 503, "gateway_unavailable"); return; }
            if (!await Verified(context)) { await Problem(context, 403, "request_verification_failed"); return; }
            if (tenantId == Guid.Empty || !ManifestValidator.IsAppId(appId) || !ManifestValidator.IsVersion(version)
                || HasBody(context))
            { await Problem(context, 400, "invalid_run_request"); return; }
            var session = await LiveBffSession.CaptureAsync(context, tenantId, context.RequestAborted);
            var access = session is null ? null : await authorization.ReadAccessAsync(session.Subject, tenantId, context.RequestAborted);
            if (session is null || access is null) { await Problem(context, 403, "run_access_denied"); return; }
            using var permit = admission.TryAcquire(tenantId, access.UserId);
            if (permit is null) { await Limited(context); return; }
            var manifest = await store.ResolveStartAsync(session.Subject, tenantId, appId, version, context.RequestAborted);
            if (manifest is null) { await Problem(context, 403, "run_access_denied"); return; }
            var binding = catalog.ResolveBackend(manifest.BackendService, manifest.BackendVersion);
            if (binding is null || !catalog.Allows(manifest)
                || !options.Operations.Any(operation => options.Find(manifest, operation.Method, operation.Path) is not null)
                || !await transport.IsHealthy(binding, options.HealthPath, context.RequestAborted))
            { await Problem(context, 503, "application_unavailable"); return; }
            if (!await session.CheckAsync(context.RequestAborted)) { await Problem(context, 403, "run_access_denied"); return; }
            var lifetime = TimeSpan.FromSeconds(options.LeaseSeconds);
            var remaining = session.ExpiresAt - context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            if (remaining < TimeSpan.FromSeconds(1)) { await Problem(context, 403, "run_access_denied"); return; }
            if (remaining < lifetime) lifetime = TimeSpan.FromSeconds(Math.Floor(remaining.TotalSeconds));
            var run = await store.CreateRunAsync(session.Subject, session.Key, tenantId, appId, version,
                lifetime, TimeSpan.FromSeconds(options.DrainSeconds), context.RequestAborted);
            if (run is null) { await Problem(context, 403, "run_access_denied"); return; }
            await Results.Ok(new { run.RunId, run.TenantId, run.AppId, run.ReleaseVersion, run.Manifest, run.ExpiresAt,
                apiBase = $"/api/apps/{appId}/releases/{version}/" }).ExecuteAsync(context);
        }
        catch (Exception error) when (Unavailable(error)) { await Fail(context, 503, "gateway_unavailable"); }
    }

    private static async Task Forward(HttpContext context, string appId, string version, string? operation,
        GatewayOptions options, IGatewayStore store, RegistryDeploymentCatalog catalog, GatewayTransport transport,
        GatewayAdmission admission)
    {
        Prepare(context);
        try
        {
            if (!options.Enabled) { await Problem(context, 503, "gateway_unavailable"); return; }
            if (!TryGuidHeader(context, "X-Run-ID", out var runId) || !TryGuidHeader(context, "X-Tenant-ID", out var tenant)
                || !ManifestValidator.IsAppId(appId) || !ManifestValidator.IsVersion(version)
                || !GatewayOptions.ValidPath("/" + operation) || !CanonicalPath(context)
                || context.Request.QueryString.Value?.Length > 2048 || context.Request.Headers.Upgrade.Count > 0)
            { await Problem(context, 400, "invalid_application_request"); return; }
            if (context.Request.Method == "POST" && !await Verified(context))
            { await Problem(context, 403, "request_verification_failed"); return; }
            var session = await LiveBffSession.CaptureAsync(context, tenant, context.RequestAborted);
            var run = session is null ? null : await store.ReadRunAsync(session.Subject, session.Key, runId, context.RequestAborted);
            if (session is null || run is null || run.TenantId != tenant || run.AppId != appId || run.ReleaseVersion != version)
            { await Problem(context, 403, "run_access_denied"); return; }
            var targetOperation = options.Find(run.Manifest, context.Request.Method, "/" + operation);
            if (targetOperation is null) { await Problem(context, 404, "application_operation_not_found"); return; }
            var binding = catalog.ResolveBackend(run.Manifest.BackendService, run.Manifest.BackendVersion);
            if (binding is null || !catalog.Allows(run.Manifest)) { await Problem(context, 503, "application_unavailable"); return; }
            using var permit = admission.TryAcquire(tenant, run.UserId);
            if (permit is null) { await Limited(context); return; }
            using var body = await ReadRequest(context);
            if (body is null) return;
            var previousBody = context.Request.Body;
            context.Request.Body = body;
            context.Request.ContentLength = body.Length;
            try { await Proxy(context, session, run, targetOperation, binding, options, store, transport, transport.Forwarder); }
            finally { context.Request.Body = previousBody; }
        }
        catch (BadHttpRequestException error) { await Fail(context, error.StatusCode, "invalid_application_request"); }
        catch (Exception error) when (Unavailable(error)) { await Fail(context, 503, "gateway_unavailable"); }
    }

    private static async Task Proxy(HttpContext context, LiveBffSession session, GatewayRuntime run, GatewayOperation operation,
        RegistryDeploymentCatalog.BackendBinding binding, GatewayOptions options, IGatewayStore store,
        GatewayTransport transport, IHttpForwarder forwarder)
    {
        var clock = context.RequestServices.GetRequiredService<TimeProvider>();
        var requestId = context.Response.Headers["X-Request-ID"].ToString();
        var destination = GatewayTransport.Destination(binding.Destination, operation.Path, context.Request.QueryString);
        var target = new InternalCallTarget(run.AppId, run.ReleaseVersion, binding.Audience, operation.Permission,
            new(run.TenantId, "application", run.AppId, null), operation.Method, destination.AbsolutePath);
        var signer = context.RequestServices.GetRequiredService<InternalCallTokenService>();
        var token = await signer.IssueAsync(session.Principal, run.TenantId, target, requestId, context.RequestAborted);
        if (token is null) { await Problem(context, 403, "run_access_denied"); return; }
        if (!await session.CheckAsync(context.RequestAborted)
            || await store.ReadRunAsync(session.Subject, session.Key, run.RunId, context.RequestAborted) is not { } current)
        { await Problem(context, 403, "run_access_denied"); return; }
        var absoluteDeadline = new[] { session.ExpiresAt, current.ExpiresAt,
            current.DrainUntil ?? DateTimeOffset.MaxValue }.Min();
        var deadline = new GatewayDeadline(clock, TimeSpan.FromSeconds(options.CallSeconds), absoluteDeadline);
        if (deadline.Remaining() == TimeSpan.Zero) { await Problem(context, 403, "run_access_denied"); return; }
        await store.StartCallAsync(run, requestId, operation.Method, operation.Path, context.RequestAborted);
        var timer = Stopwatch.StartNew();
        var outcome = "forwarding_failed";
        using var callStop = new CancellationTokenSource();
        var remaining = deadline.Remaining();
        callStop.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        using var headersTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.ResponseHeadersSeconds));
        using var forwarding = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, callStop.Token, headersTimeout.Token);
        var forwardingToken = forwarding.Token;
        using var watchStop = CancellationTokenSource.CreateLinkedTokenSource(forwarding.Token);
        string? stoppedReason = null;
        var watcher = Watch();
        ModelDelegationRegistry.Lease? delegation = null;
        ModelDelegationRegistry.Lease? connectorDelegation = null;
        try
        {
            // A slow durable admission may have consumed the lease/call budget.
            // Do not race an asynchronous CancelAfter(0) against a network send.
            if (remaining == TimeSpan.Zero)
            {
                outcome = "deadline";
                await Problem(context, 504, "application_call_interrupted");
                return;
            }
            if (operation.ModelIds.Length > 0 || operation.ConnectorDatasetIds.Length > 0)
            {
                var caller = new ModelCaller(session.Subject, run.TenantId, run.UserId, absoluteDeadline,
                    CheckDelegation, forwardingToken, new(run.AppId, run.ReleaseVersion, run.RunId, requestId));
                if (operation.ModelIds.Length > 0)
                {
                    delegation = context.RequestServices.GetRequiredService<ModelDelegationRegistry>().Register(caller, operation.ModelIds);
                    if (delegation is null) { await Problem(context, 503, "model_delegation_unavailable"); return; }
                }
                if (operation.ConnectorDatasetIds.Length > 0)
                {
                    connectorDelegation = context.RequestServices.GetRequiredService<AssembleAI.Platform.Connectors.ConnectorDelegationRegistry>()
                        .Register(caller, operation.ConnectorDatasetIds);
                    if (connectorDelegation is null) { await Problem(context, 503, "connector_delegation_unavailable"); return; }
                }
            }
            var transformer = new GatewayTransformer(destination, token, requestId,
                () => headersTimeout.CancelAfter(Timeout.InfiniteTimeSpan), delegation?.Capability, connectorDelegation?.Capability);
            var result = await forwarder.SendAsync(context, binding.Destination, transport.Client,
                new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(options.IdleSeconds), AllowResponseBuffering = false },
                transformer, forwarding.Token);
            outcome = Volatile.Read(ref stoppedReason) ?? (headersTimeout.IsCancellationRequested ? "headers_timeout"
                : callStop.IsCancellationRequested ? "deadline" : context.RequestAborted.IsCancellationRequested ? "disconnected"
                : transformer.RejectedResponse ? "downstream_rejected" : result == ForwarderError.None ? "transport_completed" : "forwarding_failed");
            if (result != ForwarderError.None && !context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
                await Fail(context, outcome is "headers_timeout" or "deadline" || result.ToString().Contains("TimedOut", StringComparison.Ordinal) ? 504 : 502,
                    "application_forwarding_failed");
        }
        catch (OperationCanceledException) when (forwarding.IsCancellationRequested)
        {
            outcome = Volatile.Read(ref stoppedReason) ?? (context.RequestAborted.IsCancellationRequested ? "disconnected" : "deadline");
            if (!context.RequestAborted.IsCancellationRequested) await Fail(context, 504, "application_call_interrupted");
        }
        finally
        {
            // Revoke before watcher/completion auditing: a normally completed
            // outer request must leave no interval for late model callbacks.
            delegation?.Dispose();
            connectorDelegation?.Dispose();
            await watchStop.CancelAsync();
            await watcher;
            using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await store.FinishCallAsync(requestId, context.Response.StatusCode, outcome, timer.ElapsedMilliseconds, finish.Token); }
            catch (Exception error) when (Unavailable(error) || error is OperationCanceledException)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GatewayAudit")
                    .LogError("Gateway completion for {RequestId} could not persist; call remains running and must be reconciled.", requestId);
            }
        }

        async Task<bool> CheckDelegation(CancellationToken cancellation)
        {
            using var check = CancellationTokenSource.CreateLinkedTokenSource(cancellation, forwardingToken);
            check.CancelAfter(TimeSpan.FromSeconds(options.CheckTimeoutSeconds));
            if (deadline.Remaining() <= TimeSpan.Zero || !await session.CheckAsync(check.Token)) return false;
            var state = await store.ReadRunAsync(session.Subject, session.Key, run.RunId, check.Token);
            if (state is null || state.UserId != run.UserId || state.TenantId != run.TenantId
                || state.AppId != run.AppId || state.ReleaseVersion != run.ReleaseVersion) return false;
            var left = deadline.Remaining(state.DrainUntil is { } drain && drain < state.ExpiresAt ? drain : state.ExpiresAt);
            if (left <= TimeSpan.Zero) return false;
            check.Token.ThrowIfCancellationRequested();
            return true;
        }

        async Task Watch()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(options.RecheckSeconds), watchStop.Token);
                    using var check = CancellationTokenSource.CreateLinkedTokenSource(watchStop.Token);
                    check.CancelAfter(TimeSpan.FromSeconds(options.CheckTimeoutSeconds));
                    var alive = await session.CheckAsync(check.Token);
                    var state = alive ? await store.ReadRunAsync(session.Subject, session.Key, run.RunId, check.Token) : null;
                    if (state is null) { Volatile.Write(ref stoppedReason, "revoked"); await callStop.CancelAsync(); return; }
                    var left = deadline.Remaining(state.DrainUntil is { } drain && drain < state.ExpiresAt ? drain : state.ExpiresAt);
                    if (left <= TimeSpan.Zero) { Volatile.Write(ref stoppedReason, "deadline"); await callStop.CancelAsync(); return; }
                    callStop.CancelAfter(left);
                }
            }
            catch (OperationCanceledException) when (watchStop.IsCancellationRequested) { }
            catch (Exception error) when (Unavailable(error) || error is OperationCanceledException)
            { Volatile.Write(ref stoppedReason, "recheck_unavailable"); await callStop.CancelAsync(); }
        }
    }

    private static async Task<MemoryStream?> ReadRequest(HttpContext context)
    {
        const int maximum = 1024 * 1024;
        if (context.Request.Method == "GET")
        {
            if (HasBody(context))
            { await Problem(context, 400, "get_body_not_allowed"); return null; }
            return new MemoryStream();
        }
        if (!context.Request.HasJsonContentType() || context.Request.Headers.ContentEncoding.Count > 0
            || !MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var media)
            || media.CharSet is { } charset && !charset.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))
        { await Problem(context, 415, "utf8_json_required"); return null; }
        if (context.Request.ContentLength > maximum) { await Problem(context, 413, "request_too_large"); return null; }
        var body = new MemoryStream();
        using var read = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        read.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var block = new byte[8192];
            int count;
            while ((count = await context.Request.Body.ReadAsync(block, read.Token)) > 0)
            {
                if (body.Length + count > maximum) { await Problem(context, 413, "request_too_large"); body.Dispose(); return null; }
                body.Write(block, 0, count);
            }
            body.Position = 0;
            return body;
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        { body.Dispose(); await Problem(context, 408, "request_body_timeout"); return null; }
        catch { body.Dispose(); throw; }
    }
    private static bool CanonicalPath(HttpContext context)
    {
        var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        return raw is not null && raw.Split('?', 2)[0] == context.Request.PathBase.Value + context.Request.Path.Value;
    }
    private static bool HasBody(HttpContext context) => context.Request.ContentLength is > 0
        || context.Request.Headers.TransferEncoding.Count > 0
        || context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true;
    private static bool TryGuidHeader(HttpContext context, string name, out Guid value)
    {
        var header = context.Request.Headers[name];
        value = Guid.Empty;
        return header.Count == 1 && Guid.TryParseExact(header[0], "D", out value) && value != Guid.Empty;
    }
    private static Task<bool> Verified(HttpContext context) => BffEndpoints.IsVerifiedRequest(context,
        context.RequestServices.GetRequiredService<IAntiforgery>(), context.RequestServices.GetRequiredService<BffOptions>());
    private static bool Unavailable(Exception error) => error is GatewayStoreUnavailableException or RegistryStoreUnavailableException
        or AuthorizationStoreUnavailableException or BffSessionStoreException;
    private static void Prepare(HttpContext context)
    {
        if (!context.Items.TryGetValue(RequestIdKey, out var requestId))
            context.Items[RequestIdKey] = requestId = Guid.NewGuid().ToString("N");
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Request-ID"] = (string)requestId!;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }
    private static Task Limited(HttpContext context)
    {
        context.Response.Headers.RetryAfter = "60";
        return Problem(context, 429, "gateway_limit_exceeded");
    }
    private static Task Problem(HttpContext context, int status, string code)
    {
        Prepare(context);
        return Results.Problem(statusCode: status, title: "The application request could not be completed.",
            extensions: new Dictionary<string, object?> { ["code"] = code, ["requestId"] = context.Items[RequestIdKey] }).ExecuteAsync(context);
    }
    private static Task Fail(HttpContext context, int status, string code)
    {
        if (context.Response.HasStarted) { context.Abort(); return Task.CompletedTask; }
        return Problem(context, status, code);
    }
}
