using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Identity;
using AssembleAI.Platform.Registry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AssembleAI.Platform.SmokeTests;

/// <summary>Real ChatBI artifact, OIDC, BFF, gateway, Python and read-only PostgreSQL.</summary>
public static class PostgresChatBiTests
{
    private const string Source = "factory-db", Dataset = "daily-production", Version = "0.1.0";
    private const string Sum = "{\"columns\":[],\"filters\":[],\"metrics\":[{\"function\":\"sum\",\"column\":\"produced\"}],\"limit\":100}";
    private const string Day = "{\"columns\":[],\"filters\":[{\"column\":\"day\",\"operator\":\"eq\",\"value\":\"2026-11-02\"}],\"metrics\":[{\"function\":\"sum\",\"column\":\"produced\"}],\"limit\":100}";
    private const string Group = "{\"columns\":[\"line\"],\"filters\":[],\"metrics\":[{\"function\":\"sum\",\"column\":\"produced\"}],\"limit\":100}";
    public static async Task RunAsync(string connection, string directory, bool realModel)
    {
        Check(!InteractiveDemo || !realModel, "The local interactive experience cannot call a real model provider.");
        await Exercise(connection, directory, null);
        if (realModel)
        {
            var path = Environment.GetEnvironmentVariable("ASSEMBLEAI_MODEL_ACCEPTANCE_CONFIG");
            Check(!string.IsNullOrEmpty(path), "Explicit model acceptance configuration is required.");
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path!));
            var value = json.RootElement;
            var actual = new Provider(value.GetProperty("endpoint").GetString()!, value.GetProperty("upstreamModel").GetString()!,
                value.GetProperty("credentialEnvironmentVariable").GetString()!, value.TryGetProperty("reasoningEffort", out var effort) ? effort.GetString() : null);
            Check(Uri.TryCreate(actual.Endpoint, UriKind.Absolute, out var uri) && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo)
                && string.IsNullOrEmpty(uri.Query) && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(actual.CredentialVariable)), "A configured HTTPS provider is required.");
            await Exercise(connection, directory, actual);
        }
    }

    private static async Task Exercise(string connection, string runDirectory, Provider? actual)
    {
        var interactive = InteractiveDemo && actual is null;
        var publicOrigin = interactive ? InteractiveOrigin() : FreeOrigin();
        var workspace = Workspace(); var f = new Fixture();
        var directory = Path.Combine(runDirectory, "chatbi-" + f.Tenant.ToString("N")); Directory.CreateDirectory(directory);
        var node = Environment.GetEnvironmentVariable("ASSEMBLEAI_NODE") ?? "node";
        var python = Environment.GetEnvironmentVariable("ASSEMBLEAI_PYTHON") ?? throw new InvalidOperationException("Locked Python venv is required.");
        var previewScript = Path.Combine(workspace, "tests/frontend/chatbi-preview-fixture.mjs");
        await Run(node, workspace, [previewScript, "prepare", directory], 60);
        using var artifacts = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "artifacts.json")));
        var artifact = artifacts.RootElement.GetProperty("artifacts")[0];
        var manifest = new ApplicationManifest(1, "chatbi", interactive ? "ChatBI 本地体验" : "生产数据分析", Version, "/apps/chatbi/0.1.0/", "chatbi", ["chatbi:use"], "draft",
            new(Version, artifact.GetProperty("sha256").GetString()!), Version, "制造运营", interactive
                ? "本地交互体验：制造业测试数据，3 个示例问题使用固定查询规划；真实数据库查询，未接入真实 AI。"
                : "制造业测试数据：按当前授权工厂查询每日产量");
        using var input = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(runDirectory, "test-config.json")));
        var previous = Environment.GetEnvironmentVariable(f.CredentialVariable);
        Environment.SetEnvironmentVariable(f.CredentialVariable, input.RootElement.GetProperty("connectorConnectionString").GetString());
        await using var identity = await OidcFixture.StartAsync();
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        var platformOrigin = FreeOrigin(); var backendOrigin = FreeOrigin();
        var plan = Sum; Hold? hold = null; var upstreamCalls = 0;
        var captureKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string? capturedCapability = null, capturedAuthorization = null;
        string? planningDiagnostic = null;
        string? connectorDiagnostic = null;
        var controlBuilder = WebApplication.CreateBuilder(["--environment", "Development", "--urls", "http://127.0.0.1:0"]);
        controlBuilder.Logging.ClearProviders(); await using var control = controlBuilder.Build();
        control.MapPost("/capture", async (HttpContext context) => {
            if (context.Request.Headers["X-Fixture-Control"] != captureKey) { context.Response.StatusCode = 403; return; }
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            capturedCapability = body.RootElement.GetProperty("capability").GetString(); capturedAuthorization = body.RootElement.GetProperty("authorization").GetString();
            context.Response.StatusCode = 204;
        });
        control.MapPost("/planning-diagnostic", async (HttpContext context) => {
            if (context.Request.Headers["X-Fixture-Control"] != captureKey) { context.Response.StatusCode = 403; return; }
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            Check(body.RootElement.EnumerateObject().All(item => item.Name is "finishReason" or "characters" or "markdownFence" or "jsonObject" or "requiredFields" or "extraFields" or "columnCount" or "metricCount"), "Planning diagnostics must remain metadata only.");
            planningDiagnostic = body.RootElement.GetRawText(); context.Response.StatusCode = 204;
        });
        control.MapPost("/connector-diagnostic", async (HttpContext context) => {
            if (context.Request.Headers["X-Fixture-Control"] != captureKey) { context.Response.StatusCode = 403; return; }
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            connectorDiagnostic = body.RootElement.GetRawText(); context.Response.StatusCode = 204;
        });
        control.MapPost("/v1/chat/completions", async (HttpContext context) => {
            Interlocked.Increment(ref upstreamCalls); var pause = hold; var content = plan;
            using var json = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            Check(!context.Request.Headers.ContainsKey("Cookie") && !context.Request.Headers.ContainsKey("X-Connector-Delegation")
                && !context.Request.Headers.ContainsKey("X-Model-Delegation") && json.RootElement.GetProperty("model").GetString() == "fixture-model"
                && !json.RootElement.GetProperty("stream").GetBoolean(), "Model planning must use trusted non-streaming provider settings.");
            var wire = json.RootElement.GetRawText();
            Check(interactive || (!wire.Contains("fixture-line-a", StringComparison.Ordinal) && !wire.Contains("9999", StringComparison.Ordinal)
                && !wire.Contains(f.Table, StringComparison.Ordinal)), "Model planning must contain no source rows or physical table names.");
            if (interactive) content = InteractivePlan(json.RootElement);
            try
            {
                if (pause is not null) { pause.Started.TrySetResult(); await pause.Release.Task.WaitAsync(context.RequestAborted); }
                await context.Response.WriteAsJsonAsync(new { choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 20, completion_tokens = 30, total_tokens = 50 } }, context.RequestAborted);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException) { pause?.Cancelled.TrySetResult(); }
        });
        await control.StartAsync();
        try
        {
            await Seed(db, f, identity.Authority);
            using var rsa = RSA.Create(2048);
            using var cert = new CertificateRequest("CN=chatbi-fixture", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var pfx = Path.Combine(directory, "signer.pfx"); var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await File.WriteAllBytesAsync(pfx, cert.Export(X509ContentType.Pfx, password));
            var settings = Settings(connection, backendOrigin, control.Urls.Single(), manifest, f, actual);
            var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var auth = new PostgresAuthorizationStore(config); var registry = new PostgresRegistryStore(config, new(config), auth);
            var owner = new SubjectIdentity(identity.Authority, OidcFixture.Subject); var reviewer = new SubjectIdentity(identity.Authority, f.Reviewer.ToString());
            Ok(await registry.SaveDraftAsync(owner, manifest, 0, Id())); Ok(await registry.TransitionAsync(owner, "chatbi", Version, "submit", 1, Id()));
            Ok(await registry.TransitionAsync(reviewer, "chatbi", Version, "publish", 2, Id()));
            Ok(await registry.SetTenantApplicationAsync(owner, f.Tenant, "chatbi", Version, true, 0, Id()));
            await using var platform = PlatformApplication.Build([
                "--environment", "Development", "--urls", platformOrigin,
                "--Authentication:Bff:Enabled", "true", "--Authentication:Bff:Authority", identity.Authority,
                "--Authentication:Bff:ClientId", OidcFixture.ClientId, "--Authentication:Bff:ClientSecret", OidcFixture.ClientSecret,
                "--Authentication:Bff:PublicOrigin", publicOrigin, "--InternalCalls:Enabled", "true", "--InternalCalls:ValidationEndpointEnabled", "true",
                "--InternalCalls:Issuer", "https://platform.example.test/internal", "--InternalCalls:SigningCertificatePath", pfx,
                "--InternalCalls:SigningCertificatePassword", password
            ], builder => { builder.Logging.ClearProviders(); builder.Configuration.AddInMemoryCollection(settings); });
            await platform.StartAsync();
            try
            {
                await using var backend = StartBackend(python, workspace, platformOrigin, backendOrigin, control.Urls.Single(), captureKey, f.CredentialVariable);
                await Ready(backend, backendOrigin + "/health/ready");
                await using var preview = Start(node, workspace, [previewScript, "serve", directory, platformOrigin, publicOrigin]);
                await Ready(preview, publicOrigin + "/");
                using var browser = await Browser.Login(publicOrigin); browser.Tenant = f.Tenant;
                using (var selected = await browser.Send("POST", "/bff/tenant", new { tenantId = f.Tenant })) await Expect(selected, 200);
                using (var configured = await browser.Send("PUT", $"/api/platform/v1/tenants/{f.Tenant}/models/assistant/configuration", new { enabled = true, maxOutputTokens = 2048, expectedRevision = 0 })) await Expect(configured, 200);
                using (var configured = await browser.Send("PUT", $"/api/platform/v1/tenants/{f.Tenant}/connectors/{Source}/configuration", new { enabled = true, maxRows = 100, timeoutSeconds = 15, expectedRevision = 0 })) await Expect(configured, 200);
                if (interactive)
                {
                    await WaitForInteractiveDemo(runDirectory, publicOrigin, f, backend, preview);
                    return;
                }
                await browser.StartRun();
                using (var datasets = await browser.Send("GET", "/api/apps/chatbi/releases/0.1.0/datasets"))
                {
                    using var json = JsonDocument.Parse(await Expect(datasets, 200));
                    Check(json.RootElement.GetProperty("items").GetArrayLength() == 1 && !json.RootElement.GetRawText().Contains(f.Table, StringComparison.Ordinal), "Datasets must expose only permitted semantic metadata.");
                }
                var questions = new[] { "2026年11月1日至2日的产量合计是多少？", "2026年11月2日的产量是多少？", "按产线汇总产量" };
                var plans = new[] { Sum, Day, Group };
                for (var index = 0; index < questions.Length; index++)
                {
                    plan = plans[index]; hold = actual is null && index == 0 ? new Hold() : null; planningDiagnostic = connectorDiagnostic = null;
                    using var response = await browser.Send("POST", "/api/apps/chatbi/releases/0.1.0/ask", new { datasetId = Dataset, question = questions[index] });
                    await StreamStatus(response); using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
                    var first = await Event(reader); Check(first.Type == "delta" && first.Value.GetProperty("stage").GetString() == "planning", "Planning stage must arrive before model completion.");
                    if (hold is not null) { await hold.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)); Check(!hold.Release.Task.IsCompleted, "SSE cannot buffer until model completion."); hold.Release.TrySetResult(); }
                    var result = await Collect(reader);
                    Check(result.Complete is { } && result.Rows.Count == 1,
                        $"ChatBI question {index+1} failed: complete={result.Complete is not null}, rowCount={result.Rows.Count}, error={result.Error ?? "none"}, planning={planningDiagnostic ?? "unavailable"}, connector={connectorDiagnostic ?? "unavailable"}. Every answer requires one guarded result row and explicit completion.");
                    var row = result.Rows[0]; var expected = index == 1 ? "200" : "300";
                    Check(row.Contains(expected) && (index != 2 || row.Contains("fixture-line-a"))
                        && !row.Contains("900") && !row.Contains("9999"), "ChatBI numeric baseline and cross-factory/tenant isolation must match real PostgreSQL.");
                    await VerifyAudit(db, f, result.Complete!.Value, browser.Run, response.Headers.GetValues("X-Request-ID").Single());
                }
                hold = null;
                if (actual is null)
                {
                    Check(!string.IsNullOrEmpty(capturedCapability) && capturedCapability != Browser.Forged, "Gateway must overwrite browser-forged connector delegation.");
                    using var direct = new HttpClient { BaseAddress = new Uri(platformOrigin) };
                    foreach (var credential in new[] { Browser.Forged, capturedCapability!, capturedAuthorization! })
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, "/internal/connectors/datasets");
                        request.Headers.TryAddWithoutValidation("Authorization", credential.StartsWith("Bearer ", StringComparison.Ordinal) ? credential : "Bearer " + credential);
                        using var denied = await direct.SendAsync(request); Check(denied.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, "Forged, completed and business JWT credentials cannot authorize connector calls.");
                    }
                    foreach (var invalid in new[] { "{\"sql\":\"SELECT * FROM forbidden\"}", Sum.Replace("produced", "tenant_id", StringComparison.Ordinal) })
                    {
                        plan = invalid; var before = await QueryCount(db, f);
                        using var rejected = await browser.Send("POST", "/api/apps/chatbi/releases/0.1.0/ask", new { datasetId = Dataset, question = "合计产量" });
                        await StreamStatus(rejected); using var reader = new StreamReader(await rejected.Content.ReadAsStreamAsync());
                        var result = await Collect(reader); Check(result.Complete is null && result.Error == "invalid_query_plan" && await QueryCount(db, f) == before, "Unsafe model plans must fail before database admission.");
                    }
                    plan = Sum;
                    var callsBefore = Volatile.Read(ref upstreamCalls);
                    using (var rejected = await browser.Send("POST", "/api/apps/chatbi/releases/0.1.0/ask", new { datasetId = "foreign-dataset", question = "产量" }))
                    { await StreamStatus(rejected); using var reader = new StreamReader(await rejected.Content.ReadAsStreamAsync()); var result = await Collect(reader); Check(result.Complete is null && Volatile.Read(ref upstreamCalls) == callsBefore, "Unknown datasets cannot invoke the model."); }
                    foreach (var revoke in new[] { true, false })
                    {
                        hold = new Hold(); var before = await QueryCount(db, f);
                        using var response = await browser.Send("POST", "/api/apps/chatbi/releases/0.1.0/ask", new { datasetId = Dataset, question = "产量合计" });
                        await StreamStatus(response); using var reader = new StreamReader(await response.Content.ReadAsStreamAsync()); await Event(reader);
                        await hold.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        if (revoke)
                        {
                            await Execute(db, "UPDATE public.role_permission_grants SET enabled=false WHERE tenant_id=$1 AND grant_id=$2", f.Tenant, f.DatasetGrant);
                            hold.Release.TrySetResult(); var result = await Collect(reader); Check(result.Complete is null && result.Rows.Count == 0, "Revoked data grants must prevent answer delivery.");
                            await Execute(db, "UPDATE public.role_permission_grants SET enabled=true WHERE tenant_id=$1 AND grant_id=$2", f.Tenant, f.DatasetGrant);
                        }
                        else { reader.Dispose(); response.Dispose(); await hold.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(12)); }
                        Check(await QueryCount(db, f) == before, "Revocation/cancellation during planning must never execute a source query.");
                    }
                    hold = null; plan = Sum;
                    var output = Path.Combine(workspace, ".artifacts/chatbi-postgres", f.Tenant.ToString("N")); Directory.CreateDirectory(output);
                    var browserConfig = Path.Combine(directory, "browser.json");
                    await File.WriteAllTextAsync(browserConfig, JsonSerializer.Serialize(new { publicOrigin, issuer = identity.Authority, userId = f.Owner, tenantId = f.Tenant, appId = "chatbi", outputDirectory = output }));
                    await Run(node, workspace, [Path.Combine(workspace, "tests/frontend/chatbi-postgres-browser.mjs"), browserConfig], 180);
                }
                Console.WriteLine(actual is null
                    ? "PASS: ChatBI real OIDC/BFF/YARP/Python/PostgreSQL and browser chain; 300/200/grouped baseline, factory/tenant isolation, unsafe plans, revocation, disconnect, capability boundaries and durable dual-call source audit."
                    : "PASS: ChatBI real-model acceptance: exactly three synthetic questions, verified PostgreSQL 300/200/grouped results, scoped rows and correlated model/query audit; no secrets, prompts or business results logged.");
            }
            finally { await platform.StopAsync(); }
        }
        finally
        {
            capturedCapability = null; capturedAuthorization = null; hold?.Release.TrySetResult(); await control.StopAsync();
            Environment.SetEnvironmentVariable(f.CredentialVariable, previous);
            await Execute(db, $"DROP TABLE IF EXISTS test_support.{f.Table}");
            foreach (var table in new[] { "platform_model_calls", "platform_model_configuration_audit", "platform_connector_calls", "platform_connector_configuration_audit" })
                await Execute(db, $"DELETE FROM public.{table} WHERE tenant_id=$1", f.Tenant);
            await Execute(db, "DELETE FROM public.platform_gateway_calls WHERE app_id='chatbi'");
            await Execute(db, "DELETE FROM public.platform_applications WHERE app_id='chatbi'");
            await Execute(db, "DELETE FROM public.platform_registry_audit WHERE app_id='chatbi'");
            await Execute(db, "DELETE FROM public.platform_tenants WHERE tenant_id IN ($1,$2)", f.Tenant, f.OtherTenant);
            await Execute(db, "DELETE FROM public.platform_users WHERE user_id IN ($1,$2)", f.Owner, f.Reviewer);
        }
    }

    private static bool InteractiveDemo => Environment.GetEnvironmentVariable("ASSEMBLEAI_INTERACTIVE_DEMO") == "1";

    private static string InteractiveOrigin()
    {
        var configured = Environment.GetEnvironmentVariable("ASSEMBLEAI_DEMO_PORT");
        var port = 4174;
        if (configured is not null && (!int.TryParse(configured, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out port) || port is < 1024 or > 65535))
            throw new InvalidOperationException("Local experience port must be an integer between 1024 and 65535.");
        using var socket = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        try { socket.Start(); }
        catch (SocketException error) { throw new InvalidOperationException($"Local experience port {port} is already in use. Choose another port.", error); }
        return $"http://127.0.0.1:{port}";
    }

    private static string InteractivePlan(JsonElement request)
    {
        const string refusal = "{\"refusal\":\"unsupported_question\"}";
        if (!request.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) return refusal;
        var users = messages.EnumerateArray().Where(message => message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("role", out var role) && role.ValueKind == JsonValueKind.String && role.GetString() == "user").ToArray();
        if (users.Length != 1 || !users[0].TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) return refusal;
        var prompt = content.GetString()!;
        // make_prompt appends the schema/question JSON after its instruction line. Never match instructions or substrings.
        try
        {
            using var payload = JsonDocument.Parse(prompt[(prompt.LastIndexOf('\n') + 1)..]);
            if (payload.RootElement.ValueKind != JsonValueKind.Object || !payload.RootElement.TryGetProperty("question", out var question)
                || question.ValueKind != JsonValueKind.String) return refusal;
            return question.GetString() switch
            {
                "2026年11月1日至2日的产量合计是多少？" => Sum,
                "2026年11月2日的产量是多少？" => Day,
                "按产线汇总产量" => Group,
                _ => refusal
            };
        }
        catch (JsonException) { return refusal; }
    }

    private static async Task WaitForInteractiveDemo(string runDirectory, string publicOrigin, Fixture fixture, Child backend, Child preview)
    {
        var readyPath = Path.Combine(runDirectory, "demo-ready.json");
        var stopPath = Path.Combine(runDirectory, "demo-stop");
        var expiresAtUtc = DateTimeOffset.UtcNow.AddHours(4);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromHours(4));
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; lifetime.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            await File.WriteAllTextAsync(readyPath + ".tmp", JsonSerializer.Serialize(new
            {
                publicOrigin, tenantId = fixture.Tenant, appId = "chatbi", modelMode = "fixed-example-plans", expiresAtUtc
            }), lifetime.Token);
            File.Move(readyPath + ".tmp", readyPath, overwrite: true);
            Console.WriteLine($"LOCAL EXPERIENCE READY: {publicOrigin}/ — manufacturing test data; three fixed example plans, no real AI provider. Expires at {expiresAtUtc:O}.");
            while (!lifetime.IsCancellationRequested && !File.Exists(stopPath))
            {
                Check(!backend.Process.HasExited && !preview.Process.HasExited, "A local experience service stopped unexpectedly.");
                await Task.Delay(TimeSpan.FromSeconds(1), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            Console.CancelKeyPress -= cancel;
            File.Delete(readyPath);
            File.Delete(readyPath + ".tmp");
        }
    }

    private static Dictionary<string, string?> Settings(string connection, string backend, string fixture, ApplicationManifest manifest, Fixture f, Provider? actual)
    {
        var values = ManifestTests.CatalogSettings(manifest);
        values["ConnectionStrings:Platform"] = connection; values["Registry:BackendBindings:0:Destination"] = backend;
        values["Registry:BackendBindings:0:Audience"] = "assembleai:chatbi"; values["Gateway:Enabled"] = "true";
        values["Gateway:CallSeconds"] = "180"; values["Gateway:IdleSeconds"] = "30";
        for (var i = 0; i < 2; i++)
        {
            var key = $"Gateway:Operations:{i}:";
            values[key + "Service"] = "chatbi"; values[key + "Version"] = Version; values[key + "Method"] = i == 0 ? "POST" : "GET";
            values[key + "Path"] = i == 0 ? "/ask" : "/datasets"; values[key + "Permission"] = "chatbi:use";
            values[key + "ConnectorDatasetIds:0"] = Dataset; if (i == 0) values[key + "ModelIds:0"] = "assistant";
        }
        values["Models:Enabled"] = "true"; values["Models:CallSeconds"] = "120";
        values["Models:Providers:chatbi:Endpoint"] = actual?.Endpoint ?? fixture + "/v1/chat/completions";
        values["Models:Providers:chatbi:AllowInsecureHttp"] = (actual is null).ToString();
        if (actual is not null) values["Models:Providers:chatbi:CredentialEnvironmentVariable"] = actual.CredentialVariable;
        values["Models:Catalog:assistant:Provider"] = "chatbi"; values["Models:Catalog:assistant:UpstreamModel"] = actual?.Model ?? "fixture-model";
        values["Models:Catalog:assistant:DisplayName"] = "ChatBI planning"; values["Models:Catalog:assistant:Enabled"] = "true";
        values["Models:Catalog:assistant:MaximumOutputTokens"] = "2048";
        if (actual?.ReasoningEffort is { } effort) values["Models:Catalog:assistant:ReasoningEffort"] = effort;
        values["Connectors:Enabled"] = "true";
        values[$"Connectors:Sources:{Source}:DisplayName"] = "制造业测试数据";
        values[$"Connectors:Sources:{Source}:ConnectionEnvironmentVariable"] = f.CredentialVariable;
        values[$"Connectors:Sources:{Source}:Enabled"] = "true"; values[$"Connectors:Sources:{Source}:MaximumRows"] = "100";
        // This budget includes uncached OIDC/session/run/policy checks and durable audit.
        // Dedicated T10 tests separately exercise three-second query deadlines.
        values[$"Connectors:Sources:{Source}:TimeoutSeconds"] = "15";
        values[$"Connectors:Datasets:{Dataset}:SourceId"] = Source; values[$"Connectors:Datasets:{Dataset}:DisplayName"] = "每日产量与不良数 · 测试数据";
        values[$"Connectors:Datasets:{Dataset}:Schema"] = "test_support"; values[$"Connectors:Datasets:{Dataset}:Table"] = f.Table;
        values[$"Connectors:Datasets:{Dataset}:TenantColumn"] = "tenant_id"; values[$"Connectors:Datasets:{Dataset}:OrganizationColumn"] = "factory_id";
        foreach (var (alias, physical, type) in new[] { ("line", "line_name", "text"), ("day", "production_day", "date"), ("produced", "output_count", "integer"), ("defects", "defect_count", "decimal") })
        { values[$"Connectors:Datasets:{Dataset}:Columns:{alias}:Column"] = physical; values[$"Connectors:Datasets:{Dataset}:Columns:{alias}:Type"] = type; }
        return values;
    }
    private static async Task Seed(NpgsqlConnection db, Fixture f, string issuer)
    {
        await Execute(db, "INSERT INTO public.platform_users(user_id,issuer,subject,display_name) VALUES($1,$3,$4,'ChatBI owner'),($2,$3,$5,'ChatBI reviewer')", f.Owner, f.Reviewer, issuer, OidcFixture.Subject, f.Reviewer.ToString());
        await Execute(db, "INSERT INTO public.platform_registry_operators(user_id,role) VALUES($1,'developer'),($2,'reviewer')", f.Owner, f.Reviewer);
        await Execute(db, "INSERT INTO public.platform_tenants(tenant_id,name) VALUES($1,'ChatBI 制造业测试'),($2,'Other tenant')", f.Tenant, f.OtherTenant);
        await Execute(db, "INSERT INTO public.tenant_memberships(tenant_id,user_id) VALUES($1,$2)", f.Tenant, f.Owner);
        await Execute(db, "INSERT INTO public.tenant_organizations(tenant_id,organization_id,name,kind) VALUES($1,$2,'Factory A','factory'),($1,$3,'Factory B','factory')", f.Tenant, f.FactoryA, f.FactoryB);
        await Execute(db, "INSERT INTO public.tenant_roles(tenant_id,role_id,name) VALUES($1,$2,'ChatBI operator')", f.Tenant, f.Role);
        await Execute(db, "INSERT INTO public.tenant_member_roles(tenant_id,user_id,role_id) VALUES($1,$2,$3)", f.Tenant, f.Owner, f.Role);
        foreach (var (permission, type, id) in new[] { ("registry:manage", "application", "chatbi"), ("chatbi:use", "application", "chatbi"), ("model:invoke", "model", "assistant"), ("model:manage", "model", "assistant"), ("datasource:manage", "datasource", Source) })
            await Execute(db, "INSERT INTO public.role_permission_grants(tenant_id,grant_id,role_id,permission,resource_type,resource_id,tenant_wide) VALUES($1,$2,$3,$4,$5,$6,true)", f.Tenant, Guid.NewGuid(), f.Role, permission, type, id);
        foreach (var (grant, permission, type, id) in new[] { (f.SourceGrant, "datasource:read", "datasource", Source), (f.DatasetGrant, "dataset:read", "dataset", Dataset), (f.ToolGrant, "tool:invoke", "tool", "connector.query") })
        {
            await Execute(db, "INSERT INTO public.role_permission_grants(tenant_id,grant_id,role_id,permission,resource_type,resource_id,tenant_wide) VALUES($1,$2,$3,$4,$5,$6,false)", f.Tenant, grant, f.Role, permission, type, id);
            await Execute(db, "INSERT INTO public.grant_organizations(tenant_id,grant_id,organization_id) VALUES($1,$2,$3)", f.Tenant, grant, f.FactoryA);
        }
        await Execute(db, "INSERT INTO public.grant_organizations(tenant_id,grant_id,organization_id) VALUES($1,$2,$3)", f.Tenant, f.SourceGrant, f.FactoryB);
        await Execute(db, $"CREATE TABLE test_support.{f.Table}(tenant_id uuid NOT NULL,factory_id uuid NOT NULL,line_name text NOT NULL,production_day date NOT NULL,output_count bigint NOT NULL,defect_count numeric NOT NULL)");
        await Execute(db, $"INSERT INTO test_support.{f.Table} VALUES ($1,$2,'fixture-line-a','2026-11-01',100,2),($1,$2,'fixture-line-a','2026-11-02',200,3),($1,$3,'fixture-line-b','2026-11-01',900,90),($4,$2,'fixture-foreign','2026-11-01',9999,999)", f.Tenant, f.FactoryA, f.FactoryB, f.OtherTenant);
        await Execute(db, "GRANT USAGE ON SCHEMA test_support TO assembleai_connector_reader");
        await Execute(db, $"GRANT SELECT ON test_support.{f.Table} TO assembleai_connector_reader");
    }
    private static async Task VerifyAudit(NpgsqlConnection db, Fixture f, JsonElement result, Guid run, string request)
    {
        Check(result.GetProperty("requestId").GetString() == request && result.GetProperty("datasetId").GetString() == Dataset && result.GetProperty("sourceId").GetString() == Source, "ChatBI evidence must preserve original correlation and dataset.");
        foreach (var (table, key) in new[] { ("platform_model_calls", "modelRequestId"), ("platform_connector_calls", "queryRequestId") })
        {
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM public.{table} WHERE tenant_id=$1 AND user_id=$2 AND request_id=$3 AND app_id='chatbi' AND release_version='0.1.0' AND run_id=$4 AND gateway_request_id=$5 AND outcome='completed' AND finished_at IS NOT NULL", db);
            foreach (var value in new object[] { f.Tenant, f.Owner, result.GetProperty(key).GetString()!, run, request }) command.Parameters.AddWithValue(value);
            Check(await command.ExecuteScalarAsync() is 1L, "Model and connector calls require durable matching user/tenant/application/run/request source audit.");
        }
    }
    private static async Task<long> QueryCount(NpgsqlConnection db, Fixture f) { await using var command = new NpgsqlCommand("SELECT count(*) FROM public.platform_connector_calls WHERE tenant_id=$1", db); command.Parameters.AddWithValue(f.Tenant); return (long)(await command.ExecuteScalarAsync())!; }
    private static async Task StreamStatus(HttpResponseMessage response) { Check(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == "text/event-stream", "ChatBI answer must use SSE."); await Task.CompletedTask; }
    private static async Task<(string Type, JsonElement Value)> Event(StreamReader reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150)); string? type = null, data = null;
        while (true) { var line = await reader.ReadLineAsync(timeout.Token); Check(line is not null, "ChatBI stream ended before a terminal event.");
            if (line!.StartsWith("event:", StringComparison.Ordinal)) type = line[6..].Trim(); else if (line.StartsWith("data:", StringComparison.Ordinal)) data = line[5..].Trim();
            else if (line.Length == 0 && type is not null && data is not null) { using var json = JsonDocument.Parse(data); return (type, json.RootElement.Clone()); } }
    }
    private static async Task<Answer> Collect(StreamReader reader)
    {
        var rows = new List<string[]>();
        for (var i = 0; i < 200; i++) { var item = await Event(reader); if (item.Type == "complete") return new(rows, item.Value, null);
            if (item.Type == "error") return new(rows, null, item.Value.GetProperty("code").GetString());
            if (item.Value.TryGetProperty("kind", out var kind) && kind.GetString() == "rows") foreach (var row in item.Value.GetProperty("rows").EnumerateArray()) rows.Add(row.EnumerateArray().Select(cell => cell.GetString() ?? "").ToArray()); }
        throw new InvalidOperationException("ChatBI stream exceeded its event budget.");
    }
    private static Child StartBackend(string python, string workspace, string platform, string backend, string control, string key, string sourceVariable)
    {
        var start = ProcessInfo(python, workspace, ["-m", "uvicorn", "chatbi_application_fixture:app", "--app-dir", Path.Combine(workspace, "tests/platform"), "--host", "127.0.0.1", "--port", new Uri(backend).Port.ToString(), "--no-access-log", "--log-level", "error"]);
        start.Environment["PYTHONPATH"] = Path.Combine(workspace, "apps/chatbi/backend"); start.Environment["ASSEMBLEAI_ENVIRONMENT"] = "Development";
        start.Environment["ASSEMBLEAI_SERVICE"] = "chatbi"; start.Environment["ASSEMBLEAI_SERVICE_VERSION"] = Version;
        start.Environment["ASSEMBLEAI_VALIDATION_URL"] = platform + "/internal/calls/validate";
        start.Environment["ASSEMBLEAI_MODEL_GATEWAY_ORIGIN"] = platform; start.Environment["ASSEMBLEAI_CONNECTOR_GATEWAY_ORIGIN"] = platform;
        start.Environment["ASSEMBLEAI_MODEL_ID"] = "assistant"; start.Environment["ASSEMBLEAI_MODEL_MAX_OUTPUT_TOKENS"] = "2048";
        start.Environment["ASSEMBLEAI_CHATBI_CONTROL_ORIGIN"] = control; start.Environment["ASSEMBLEAI_CHATBI_CONTROL_KEY"] = key;
        var forbidden = new List<string> { sourceVariable, "ASSEMBLEAI_MODEL_ACCEPTANCE_CONFIG" };
        forbidden.AddRange(start.Environment.Keys.Where(value => value.StartsWith("ASSEMBLEAI_POSTGRES", StringComparison.OrdinalIgnoreCase) || value.StartsWith("ConnectionStrings", StringComparison.OrdinalIgnoreCase)));
        if (Environment.GetEnvironmentVariable("ASSEMBLEAI_MODEL_ACCEPTANCE_CONFIG") is { } path) { using var json = JsonDocument.Parse(File.ReadAllText(path)); forbidden.Add(json.RootElement.GetProperty("credentialEnvironmentVariable").GetString()!); }
        foreach (var name in forbidden) start.Environment.Remove(name);
        start.Environment["ASSEMBLEAI_CHATBI_FORBIDDEN_VARIABLES"] = string.Join(',', forbidden);
        return Launch(start);
    }
    private static ProcessStartInfo ProcessInfo(string exe, string cwd, string[] args) { var start = new ProcessStartInfo(exe) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }; foreach (var arg in args) start.ArgumentList.Add(arg); return start; }
    private static Child Start(string exe, string cwd, string[] args) => Launch(ProcessInfo(exe, cwd, args));
    private static Child Launch(ProcessStartInfo start) { var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot launch ChatBI fixture."); return new(process, process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync()); }
    private static async Task Run(string exe, string cwd, string[] args, int seconds) { await using var child = Start(exe, cwd, args); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds)); await child.Process.WaitForExitAsync(deadline.Token); var output = await child.Output; var errors = await child.Errors; Check(child.Process.ExitCode == 0, "ChatBI fixture process failed: " + output + errors); if (!string.IsNullOrEmpty(output)) Console.WriteLine(output.Trim()); }
    private static async Task Ready(Child child, string url) { using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) }; using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25)); while (true) { Check(!child.Process.HasExited, "ChatBI child ended before readiness."); try { using var response = await client.GetAsync(url, deadline.Token); if (response.IsSuccessStatusCode) return; } catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { } await Task.Delay(100, deadline.Token); } }
    private static async Task<string> Expect(HttpResponseMessage response, int status) { Check((int)response.StatusCode == status, $"ChatBI HTTP expected {status}, received {(int)response.StatusCode}."); return await response.Content.ReadAsStringAsync(); }
    private sealed class Browser(HttpClient client, string origin, string csrf) : IDisposable
    {
        public const string Forged = "browser-forged-connector-capability"; public Guid Tenant { get; set; } public Guid Run { get; private set; }
        public static async Task<Browser> Login(string origin) { var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, CookieContainer = new(), MaxResponseDrainSize = 0 }) { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(160) }; try { using var challenge = await client.GetAsync("/bff/login?returnUrl=/bff/session"); Check(challenge.StatusCode == HttpStatusCode.Redirect, "OIDC challenge required."); using var code = await client.GetAsync(challenge.Headers.Location); using var callback = await client.GetAsync(code.Headers.Location); Check(callback.StatusCode == HttpStatusCode.Redirect, "OIDC code exchange required."); using var response = await client.GetAsync("/bff/csrf"); using var json = JsonDocument.Parse(await Expect(response, 200)); return new(client, origin, json.RootElement.GetProperty("requestToken").GetString()!); } catch { client.Dispose(); throw; } }
        public async Task StartRun() { using var response = await Send("POST", $"/api/platform/v1/tenants/{Tenant}/applications/chatbi/releases/0.1.0/runs"); using var json = JsonDocument.Parse(await Expect(response, 200)); Run = json.RootElement.GetProperty("runId").GetGuid(); }
        public async Task<HttpResponseMessage> Send(string method, string path, object? body = null) { using var request = new HttpRequestMessage(new HttpMethod(method), path); if (body is not null) request.Content = JsonContent.Create(body); if (method is "POST" or "PUT") { request.Headers.Add("Origin", origin); request.Headers.Add(BffEndpoints.CsrfHeaderName, csrf); } if (path.StartsWith("/api/apps/", StringComparison.Ordinal)) { request.Headers.Add("X-Run-ID", Run.ToString("D")); request.Headers.Add("X-Tenant-ID", Tenant.ToString("D")); request.Headers.Add("X-Connector-Delegation", Forged); } return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead); }
        public void Dispose() => client.Dispose();
    }
    private sealed record Child(Process Process, Task<string> Output, Task<string> Errors) : IAsyncDisposable { public async ValueTask DisposeAsync() { if (!Process.HasExited) Process.Kill(entireProcessTree: true); await Process.WaitForExitAsync(); Process.Dispose(); } }
    private sealed class Hold { public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    private sealed class Fixture { public Guid Owner { get; } = Guid.NewGuid(); public Guid Reviewer { get; } = Guid.NewGuid(); public Guid Tenant { get; } = Guid.NewGuid(); public Guid OtherTenant { get; } = Guid.NewGuid(); public Guid Role { get; } = Guid.NewGuid(); public Guid FactoryA { get; } = Guid.NewGuid(); public Guid FactoryB { get; } = Guid.NewGuid(); public Guid SourceGrant { get; } = Guid.NewGuid(); public Guid DatasetGrant { get; } = Guid.NewGuid(); public Guid ToolGrant { get; } = Guid.NewGuid(); public string Table { get; } = "chatbi_" + Id(); public string CredentialVariable { get; } = "ASSEMBLEAI_CHATBI_SOURCE_" + Id().ToUpperInvariant(); }
    private sealed record Provider(string Endpoint, string Model, string CredentialVariable, string? ReasoningEffort);
    private sealed record Answer(List<string[]> Rows, JsonElement? Complete, string? Error);
    private static string Id() => Guid.NewGuid().ToString("N");
    private static string FreeOrigin() { using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); return "http://127.0.0.1:" + ((IPEndPoint)socket.LocalEndpoint).Port; }
    private static string Workspace() { for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent) if (File.Exists(Path.Combine(path.FullName, "AssembleAI.slnx"))) return path.FullName; throw new InvalidOperationException("Workspace not found."); }
    private static async Task Execute(NpgsqlConnection db, string sql, params object[] values) { await using var command = new NpgsqlCommand(sql, db); foreach (var value in values) command.Parameters.AddWithValue(value); await command.ExecuteNonQueryAsync(); }
    private static void Ok<T>(RegistryResult<T> result) => Check(result.Error == RegistryError.None, "Real ChatBI registry transition failed.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
