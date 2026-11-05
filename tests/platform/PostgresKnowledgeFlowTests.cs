using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Identity;
using AssembleAI.Platform.Knowledge;
using AssembleAI.Platform.Registry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AssembleAI.Platform.SmokeTests;

/// <summary>Actual OIDC, document bytes, MinIO, RabbitMQ, ONNX, pgvector and delegated answers.</summary>
public static class PostgresKnowledgeFlowTests
{
    private const string Version = "0.1.0", Profile = "e5-small-dd476dd0-384-v1";
    private const string Maintenance = "制造业合成测试资料：轴承温度过高时，应立即停机检查润滑油和冷却系统，确认故障排除后再启动设备。";
    private const string Coolant = "Synthetic manufacturing procedure: Set coolant pressure to 0.35 MPa before starting the cutting machine.";
    public static async Task RunAsync(string connection, string directory, bool realModel)
    {
        await Exercise(connection, directory, null);
        if (!realModel) return;
        var path = Environment.GetEnvironmentVariable("ASSEMBLEAI_MODEL_ACCEPTANCE_CONFIG");
        Check(!string.IsNullOrEmpty(path), "Explicit provider acceptance configuration is required.");
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path!));
        var value = json.RootElement;
        var actual = new Provider(value.GetProperty("endpoint").GetString()!, value.GetProperty("upstreamModel").GetString()!,
            value.GetProperty("credentialEnvironmentVariable").GetString()!, value.TryGetProperty("reasoningEffort", out var effort) ? effort.GetString() : null);
        Check(Uri.TryCreate(actual.Endpoint, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0
            && uri.Query.Length == 0 && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(actual.CredentialVariable)), "Configured HTTPS provider required.");
        await Exercise(connection, directory, actual);
    }

    private static async Task Exercise(string connection, string runDirectory, Provider? actual)
    {
        var workspace = Workspace(); var f = new Fixture();
        var directory = Path.Combine(runDirectory, "knowledge-" + f.Tenant.ToString("N")); Directory.CreateDirectory(directory);
        var servicesPath = Environment.GetEnvironmentVariable("ASSEMBLEAI_KNOWLEDGE_SERVICES_CONFIG");
        Check(!string.IsNullOrEmpty(servicesPath), "Native knowledge services configuration is required.");
        using var services = JsonDocument.Parse(await File.ReadAllTextAsync(servicesPath!));
        var minio = services.RootElement.GetProperty("minio"); var rabbit = services.RootElement.GetProperty("rabbit");
        var modelDirectory = Environment.GetEnvironmentVariable("ASSEMBLEAI_KNOWLEDGE_MODEL_DIRECTORY");
        Check(!string.IsNullOrEmpty(modelDirectory) && Directory.Exists(modelDirectory), "Pinned local model directory is required.");
        var python = Path.Combine(workspace, "apps/knowledge/backend/.venv/Scripts/python.exe");
        Check(File.Exists(python), "Locked knowledge Python environment is required.");
        var node = Environment.GetEnvironmentVariable("ASSEMBLEAI_NODE") ?? "node";
        var previewScript = Path.Combine(workspace, "tests/frontend/knowledge-preview-fixture.mjs");
        await Run(node, workspace, [previewScript, "prepare", directory], 60);
        using var artifacts = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "artifacts.json")));
        var digest = artifacts.RootElement.GetProperty("artifacts")[0].GetProperty("sha256").GetString()!;
        var manifest = new ApplicationManifest(1, "knowledge", "企业知识库 · 制造业测试", Version, "/apps/knowledge/0.1.0/", "knowledge",
            ["knowledge:use"], "draft", new(Version, digest), Version, "制造运营", "根据当前授权操作规程回答并提供原文引用");
        var publicOrigin = FreeOrigin(); var platformOrigin = FreeOrigin(); var parserOrigin = FreeOrigin(); var backendOrigin = FreeOrigin();
        var environment = new Dictionary<string, string?>();
        void Set(string name, string value) { environment[name] = Environment.GetEnvironmentVariable(name); Environment.SetEnvironmentVariable(name, value); }
        var parserKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        Set(f.ParserVariable, parserKey); Set(f.AccessVariable, minio.GetProperty("accessKey").GetString()!); Set(f.SecretVariable, minio.GetProperty("secretKey").GetString()!);
        var amqp = $"amqp://{Uri.EscapeDataString(rabbit.GetProperty("username").GetString()!)}:{Uri.EscapeDataString(rabbit.GetProperty("password").GetString()!)}@{rabbit.GetProperty("host").GetString()}:{rabbit.GetProperty("port").GetInt32()}/{Uri.EscapeDataString(rabbit.GetProperty("virtualHost").GetString()!)}";
        Set(f.BrokerVariable, amqp);
        await using var identity = await OidcFixture.StartAsync();
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        Hold? hold = null; var upstreamCalls = 0;
        var controlBuilder = WebApplication.CreateBuilder(["--environment", "Development", "--urls", "http://127.0.0.1:0"]);
        controlBuilder.Logging.ClearProviders(); await using var control = controlBuilder.Build();
        control.MapPost("/v1/chat/completions", async (HttpContext context) => {
            Interlocked.Increment(ref upstreamCalls); var pause = hold;
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            Check(body.RootElement.GetProperty("model").GetString() == "fixture-model" && !body.RootElement.GetProperty("stream").GetBoolean()
                && !context.Request.Headers.ContainsKey("Cookie") && !context.Request.Headers.ContainsKey("X-Knowledge-Delegation"), "Provider request must contain only configured model settings and authorized context.");
            var prompt = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
            Check(!prompt.Contains("RESTRICTED_FACTORY_B", StringComparison.Ordinal) && !prompt.Contains("RESTRICTED_OTHER_TENANT", StringComparison.Ordinal), "Retrieval must filter unauthorized factory and tenant documents before ranking.");
            var start = prompt.LastIndexOf("\n{", StringComparison.Ordinal); Check(start >= 0, "Knowledge prompt requires bounded JSON context.");
            using var contextJson = JsonDocument.Parse(prompt[(start + 1)..]);
            var question = contextJson.RootElement.GetProperty("question").GetString()!;
            var contexts = contextJson.RootElement.GetProperty("contexts").EnumerateArray().ToArray();
            object result;
            if (question.Contains("火星", StringComparison.Ordinal)) result = new { refusal = "insufficient_evidence" };
            else
            {
                var needle = question.Contains("coolant", StringComparison.OrdinalIgnoreCase) ? "0.35 MPa" : "轴承";
                var selected = contexts.FirstOrDefault(item => item.GetProperty("text").GetString()!.Contains(needle, StringComparison.Ordinal));
                Check(selected.ValueKind == JsonValueKind.Object, "Semantic retrieval must include the expected real document.");
                var quote = selected.GetProperty("text").GetString()!;
                result = new { answer = quote, citations = new[] { new { chunkId = selected.GetProperty("chunkId").GetString(), quote } } };
            }
            try
            {
                if (pause is not null) { pause.Started.TrySetResult(); await pause.Release.Task.WaitAsync(context.RequestAborted); }
                await context.Response.WriteAsJsonAsync(new { choices = new[] { new { index = 0, message = new { role = "assistant", content = JsonSerializer.Serialize(result) }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 80, completion_tokens = 60, total_tokens = 140 } }, context.RequestAborted);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException) { pause?.Cancelled.TrySetResult(); }
        });
        await control.StartAsync();
        try
        {
            await Seed(db, f, identity.Authority);
            using var rsa = RSA.Create(2048);
            using var cert = new CertificateRequest("CN=knowledge-fixture", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var pfx = Path.Combine(directory, "signer.pfx"); var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await File.WriteAllBytesAsync(pfx, cert.Export(X509ContentType.Pfx, password));
            var settings = Settings(connection, backendOrigin, parserOrigin, control.Urls.Single(), manifest, f, minio, actual);
            var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var auth = new PostgresAuthorizationStore(config); var registry = new PostgresRegistryStore(config, new(config), auth);
            var owner = new SubjectIdentity(identity.Authority, OidcFixture.Subject); var reviewer = new SubjectIdentity(identity.Authority, f.Reviewer.ToString());
            Ok(await registry.SaveDraftAsync(owner, manifest, 0, Id())); Ok(await registry.TransitionAsync(owner, "knowledge", Version, "submit", 1, Id()));
            Ok(await registry.TransitionAsync(reviewer, "knowledge", Version, "publish", 2, Id()));
            Ok(await registry.SetTenantApplicationAsync(owner, f.Tenant, "knowledge", Version, true, 0, Id()));
            await using var parser = StartPython(python, workspace, "knowledge_backend.main:app", parserOrigin, parserKey, modelDirectory!, null);
            await Ready(parser, parserOrigin + "/health/ready");
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
                await using var backend = StartPython(python, workspace, "knowledge_backend.application:app", backendOrigin, null, null, platformOrigin);
                await Ready(backend, backendOrigin + "/health/ready");
                await using var preview = Start(node, workspace, [previewScript, "serve", directory, platformOrigin, publicOrigin]);
                await Ready(preview, publicOrigin + "/");
                using var browser = await Browser.Login(publicOrigin);
                await browser.SelectTenant(f.Tenant);
                using (var configured = await browser.Send("PUT", $"/api/platform/v1/tenants/{f.Tenant}/models/assistant/configuration", new { enabled = true, maxOutputTokens = 2048, expectedRevision = 0 })) await Expect(configured, 200);
                var docx = Docx(Maintenance); var pdf = Pdf(Coolant);
                var maintenance = await Upload(browser, "制造业测试-轴承维护.docx", docx, f.FactoryA);
                var coolant = await Upload(browser, "synthetic-coolant.pdf", pdf, f.FactoryA);
                var forbidden = await Upload(browser, "restricted-factory.docx", Docx("RESTRICTED_FACTORY_B 轴承过热时继续生产，无需停机。"), f.FactoryB);
                await browser.SelectTenant(f.OtherTenant);
                var foreign = await Upload(browser, "restricted-tenant.docx", Docx("RESTRICTED_OTHER_TENANT 冷却压力为9.99MPa。"), null);
                await ReadyDocuments(db, [maintenance, coolant, forbidden, foreign]);
                await DuplicateNotifications(db, rabbit, f, maintenance);
                await browser.SelectTenant(f.Tenant);
                await Execute(db, "UPDATE public.role_permission_grants SET tenant_wide=false WHERE tenant_id=$1 AND grant_id IN ($2,$3)", f.Tenant, f.ReadGrant, f.ManageGrant);
                foreach (var grant in new[] { f.ReadGrant, f.ManageGrant })
                    await Execute(db, "INSERT INTO public.grant_organizations(tenant_id,grant_id,organization_id) VALUES($1,$2,$3)", f.Tenant, grant, f.FactoryA);
                await browser.StartRun();
                using (var list = await browser.Send("GET", Documents(f.Tenant)))
                {
                    using var json = JsonDocument.Parse(await Expect(list, 200));
                    Check(json.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("documentId").GetGuid()).ToHashSet().SetEquals([maintenance, coolant]), "Library visibility must match current factory scope.");
                }
                foreach (var blocked in new[] { forbidden, foreign })
                { using var denied = await browser.Send("GET", Documents(f.Tenant) + $"/{blocked}/content"); await Expect(denied, 404); }
                var questions = new[] { "轴承温度过高时，应该先采取什么措施？", "What coolant pressure is required before starting the cutting machine?", "火星地下矿区的班车票价是多少？" };
                for (var i = 0; i < questions.Length; i++)
                {
                    hold = actual is null && i == 0 ? new Hold() : null;
                    using var response = await browser.Send("POST", AskPath, new { question = questions[i] });
                    await StreamStatus(response); using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
                    var first = await Event(reader); Check(first.Type == "delta" && first.Value.GetProperty("stage").GetString() == "retrieving", "Retrieval stage must arrive before model completion.");
                    if (hold is not null) { await hold.Started.Task.WaitAsync(TimeSpan.FromSeconds(40)); hold.Release.TrySetResult(); }
                    var result = await Collect(reader);
                    Check(result.Complete is not null, $"Knowledge question {i + 1} failed with safe error {result.Error ?? "none"}.");
                    var complete = result.Complete!.Value;
                    Check(complete.GetProperty("profile").GetString() == Profile && complete.GetProperty("refusal").GetBoolean() == (i == 2), "Answer/refusal must match the actual source evidence.");
                    Check(complete.GetProperty("documents").EnumerateArray().All(d => new[] { maintenance, coolant }.Contains(d.GetProperty("documentId").GetGuid())), "Every model context must be currently authorized.");
                    if (i < 2)
                    {
                        var expectedId = i == 0 ? maintenance : coolant;
                        var citations = complete.GetProperty("citations").EnumerateArray().ToArray();
                        Check(citations.Any(c => c.GetProperty("documentId").GetGuid() == expectedId), "Answer must cite the expected DOCX/PDF document.");
                        foreach (var citation in citations)
                        {
                            var id = citation.GetProperty("documentId").GetGuid(); var source = id == maintenance ? Maintenance : Coolant;
                            Check(source.Contains(citation.GetProperty("quote").GetString()!, StringComparison.Ordinal), "Displayed quote must be an exact source substring.");
                        }
                        Check(complete.GetProperty("answer").GetString()!.Contains(i == 0 ? "停机" : "0.35", StringComparison.Ordinal), "Answer must preserve the manufacturing baseline.");
                        using var content = await browser.Send("GET", Documents(f.Tenant) + $"/{expectedId}/content"); await Expect(content, 200);
                        Check((await content.Content.ReadAsByteArrayAsync()).SequenceEqual(i == 0 ? docx : pdf) && content.Content.Headers.ContentDisposition?.DispositionType == "attachment", "Original evidence download must preserve uploaded bytes and attachment disposition.");
                    }
                    else Check(complete.GetProperty("citations").GetArrayLength() == 0, "Insufficient evidence must contain no fabricated citation.");
                    await VerifyAudit(db, f, complete, browser.Run, response.Headers.GetValues("X-Request-ID").Single());
                    using var references = await browser.Send("POST", ReferencesPath, new { profile = Profile,
                        authorizationVersion = complete.GetProperty("authorizationVersion").GetString(), documents = complete.GetProperty("documents") });
                    await Expect(references, 200);
                }
                hold = null;
                if (actual is null)
                {
                    var output = Path.Combine(workspace, ".artifacts/knowledge-postgres", f.Tenant.ToString("N")); Directory.CreateDirectory(output);
                    var uploadFile = Path.Combine(directory, "browser-upload.docx"); await File.WriteAllBytesAsync(uploadFile, Docx("制造业合成测试：质量巡检后记录检测时间，发现异常立即通知质量负责人。"));
                    var browserConfig = Path.Combine(directory, "browser.json");
                    await File.WriteAllTextAsync(browserConfig, JsonSerializer.Serialize(new { publicOrigin, issuer = identity.Authority, userId = f.Owner, tenantId = f.Tenant,
                        appId = "knowledge", outputDirectory = output, uploadFile, maintenanceDocumentId = maintenance, factoryId = f.FactoryA }));
                    await Run(node, workspace, [Path.Combine(workspace, "tests/frontend/knowledge-postgres-browser.mjs"), browserConfig], 240);
                    foreach (var revoke in new[] { true, false })
                    {
                        hold = new Hold();
                        using var response = await browser.Send("POST", AskPath, new { question = "轴承过热怎么办？" });
                        await StreamStatus(response); using var reader = new StreamReader(await response.Content.ReadAsStreamAsync()); await Event(reader);
                        await hold.Started.Task.WaitAsync(TimeSpan.FromSeconds(40));
                        if (revoke)
                            await Execute(db, "UPDATE public.role_permission_grants SET enabled=false WHERE tenant_id=$1 AND grant_id IN ($2,$3)", f.Tenant, f.ReadGrant, f.ManageGrant);
                        else
                        { using var deleted = await browser.Send("DELETE", Documents(f.Tenant) + $"/{coolant}", new { expectedRevision = 1 }); await Expect(deleted, 204); }
                        hold.Release.TrySetResult();
                        await ExpectDenied(reader);
                        if (revoke) await Execute(db, "UPDATE public.role_permission_grants SET enabled=true WHERE tenant_id=$1 AND grant_id IN ($2,$3)", f.Tenant, f.ReadGrant, f.ManageGrant);
                    }
                    hold = null;
                }
                Console.WriteLine(actual is null
                    ? "PASS: Knowledge real OIDC/BFF, DOCX/PDF, MinIO/RabbitMQ, CPU E5/pgvector and browser chain; original citations, scoped retrieval, refusal, revocation/deletion and correlated audit."
                    : "PASS: Knowledge actual-model acceptance: three synthetic questions, DOCX/PDF citations, insufficient-evidence refusal and correlated audit; no prompts or credentials logged.");
            }
            finally
            {
                hold?.Release.TrySetResult();
                await platform.StopAsync();
                var objects = platform.Services.GetRequiredService<IKnowledgeObjects>();
                await using var command = new NpgsqlCommand("SELECT tenant_id,document_id FROM public.knowledge_documents WHERE tenant_id IN ($1,$2)", db);
                command.Parameters.AddWithValue(f.Tenant); command.Parameters.AddWithValue(f.OtherTenant);
                var keys = new List<(Guid Tenant, Guid Document)>(); await using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) keys.Add((reader.GetGuid(0), reader.GetGuid(1)));
                foreach (var key in keys) await objects.DeleteAsync(key.Tenant, key.Document, CancellationToken.None);
            }
        }
        finally
        {
            hold?.Release.TrySetResult(); await control.StopAsync();
            foreach (var pair in environment) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            foreach (var table in new[] { "platform_model_calls", "platform_model_configuration_audit", "platform_knowledge_searches", "platform_knowledge_audit", "knowledge_jobs", "knowledge_embeddings", "knowledge_chunks", "knowledge_documents" })
                await Execute(db, $"DELETE FROM public.{table} WHERE tenant_id IN ($1,$2)", f.Tenant, f.OtherTenant);
            await Execute(db, "DELETE FROM public.platform_gateway_calls WHERE app_id='knowledge'");
            await Execute(db, "DELETE FROM public.platform_applications WHERE app_id='knowledge'");
            await Execute(db, "DELETE FROM public.platform_registry_audit WHERE app_id='knowledge'");
            await Execute(db, "DELETE FROM public.platform_tenants WHERE tenant_id IN ($1,$2)", f.Tenant, f.OtherTenant);
            await Execute(db, "DELETE FROM public.platform_users WHERE user_id IN ($1,$2)", f.Owner, f.Reviewer);
        }
    }

    private static Dictionary<string,string?> Settings(string connection, string backend, string parser, string fixture,
        ApplicationManifest manifest, Fixture f, JsonElement minio, Provider? actual)
    {
        var values = ManifestTests.CatalogSettings(manifest);
        values["ConnectionStrings:Platform"] = connection; values["Registry:BackendBindings:0:Destination"] = backend;
        values["Registry:BackendBindings:0:Audience"] = "assembleai:knowledge"; values["Gateway:Enabled"] = "true";
        values["Gateway:CallSeconds"] = "180"; values["Gateway:IdleSeconds"] = "30";
        foreach (var (index,path) in new[] { (0,"/ask"), (1,"/references") })
        {
            var key = $"Gateway:Operations:{index}:"; values[key+"Service"] = "knowledge"; values[key+"Version"] = Version;
            values[key+"Method"] = "POST"; values[key+"Path"] = path; values[key+"Permission"] = "knowledge:use"; values[key+"KnowledgeRead"] = "true";
            if (index == 0) values[key+"ModelIds:0"] = "assistant";
        }
        values["Models:Enabled"] = "true"; values["Models:CallSeconds"] = "120";
        values["Models:Providers:knowledge:Endpoint"] = actual?.Endpoint ?? fixture+"/v1/chat/completions";
        values["Models:Providers:knowledge:AllowInsecureHttp"] = (actual is null).ToString();
        if (actual is not null) values["Models:Providers:knowledge:CredentialEnvironmentVariable"] = actual.CredentialVariable;
        values["Models:Catalog:assistant:Provider"] = "knowledge"; values["Models:Catalog:assistant:UpstreamModel"] = actual?.Model ?? "fixture-model";
        values["Models:Catalog:assistant:DisplayName"] = "Knowledge answers"; values["Models:Catalog:assistant:Enabled"] = "true";
        values["Models:Catalog:assistant:MaximumOutputTokens"] = "2048";
        if (actual?.ReasoningEffort is {} effort) values["Models:Catalog:assistant:ReasoningEffort"] = effort;
        values["Knowledge:Enabled"] = "true"; values["Knowledge:WorkerEnabled"] = "true"; values["Knowledge:IndexEnabled"] = "true";
        values["Knowledge:ParserEndpoint"] = parser+"/internal/parse"; values["Knowledge:EmbeddingEndpoint"] = parser;
        values["Knowledge:ParserKeyEnvironmentVariable"] = f.ParserVariable; values["Knowledge:AllowInsecureLoopback"] = "true";
        values["Knowledge:BrokerEnabled"] = "true"; values["Knowledge:BrokerConnectionEnvironmentVariable"] = f.BrokerVariable;
        values["Knowledge:BrokerQueue"] = f.Queue;
        values["Knowledge:Objects:Endpoint"] = minio.GetProperty("endpoint").GetString(); values["Knowledge:Objects:Bucket"] = minio.GetProperty("bucket").GetString();
        values["Knowledge:Objects:AccessKeyEnvironmentVariable"] = f.AccessVariable; values["Knowledge:Objects:SecretKeyEnvironmentVariable"] = f.SecretVariable;
        values["Knowledge:Objects:AllowInsecureLoopback"] = "true";
        return values;
    }

    private static async Task Seed(NpgsqlConnection db, Fixture f, string issuer)
    {
        await Execute(db,"INSERT INTO public.platform_users(user_id,issuer,subject,display_name) VALUES($1,$3,$4,'Knowledge owner'),($2,$3,$5,'Knowledge reviewer')",f.Owner,f.Reviewer,issuer,OidcFixture.Subject,f.Reviewer.ToString());
        await Execute(db,"INSERT INTO public.platform_registry_operators(user_id,role) VALUES($1,'developer'),($2,'reviewer')",f.Owner,f.Reviewer);
        await Execute(db,"INSERT INTO public.platform_tenants(tenant_id,name) VALUES($1,'Knowledge 制造业测试'),($2,'Knowledge other tenant')",f.Tenant,f.OtherTenant);
        await Execute(db,"INSERT INTO public.tenant_memberships(tenant_id,user_id) VALUES($1,$3),($2,$3)",f.Tenant,f.OtherTenant,f.Owner);
        await Execute(db,"INSERT INTO public.tenant_organizations(tenant_id,organization_id,name,kind) VALUES($1,$2,'Factory A','factory'),($1,$3,'Factory B','factory')",f.Tenant,f.FactoryA,f.FactoryB);
        foreach (var tenant in new[] { f.Tenant, f.OtherTenant })
        {
            await Execute(db,"INSERT INTO public.tenant_roles(tenant_id,role_id,name) VALUES($1,$2,'Knowledge operator')",tenant,f.Role);
            await Execute(db,"INSERT INTO public.tenant_member_roles(tenant_id,user_id,role_id) VALUES($1,$2,$3)",tenant,f.Owner,f.Role);
            foreach (var (permission,type,id) in new[] { ("registry:manage","application","knowledge"), ("knowledge:use","application","knowledge"), ("model:invoke","model","assistant"), ("model:manage","model","assistant") })
                await Execute(db,"INSERT INTO public.role_permission_grants(tenant_id,grant_id,role_id,permission,resource_type,resource_id,tenant_wide) VALUES($1,$2,$3,$4,$5,$6,true)",tenant,Guid.NewGuid(),f.Role,permission,type,id);
            foreach (var (grant,permission) in new[] { (f.ReadGrant,"knowledge:read"), (f.ManageGrant,"knowledge:manage") })
                await Execute(db,"INSERT INTO public.role_permission_grants(tenant_id,grant_id,role_id,permission,resource_type,resource_id,tenant_wide) VALUES($1,$2,$3,$4,'knowledge','documents',true)",tenant,grant,f.Role,permission);
        }
    }

    private static async Task<Guid> Upload(Browser browser,string filename,byte[] content,Guid? organization)
    {
        var path = Documents(browser.Tenant)+"?filename="+Uri.EscapeDataString(filename)+(organization is {} id ? "&organizationId="+id : "");
        using var response = await browser.Bytes(path,content); using var json = JsonDocument.Parse(await Expect(response,202));
        Check(json.RootElement.GetProperty("status").GetString()=="queued", "Upload must persist a durable pending document.");
        return json.RootElement.GetProperty("documentId").GetGuid();
    }
    private static async Task ReadyDocuments(NpgsqlConnection db,Guid[] documents)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        while(true)
        {
            await using var command = new NpgsqlCommand("SELECT document_id,status,failure_code FROM public.knowledge_documents WHERE document_id=ANY($1)",db);
            command.Parameters.AddWithValue(documents); var ready=0;
            await using(var reader=await command.ExecuteReaderAsync(deadline.Token)) while(await reader.ReadAsync(deadline.Token))
            { var status=reader.GetString(1); Check(status!="failed", "Real document ingestion failed with safe code "+(reader.IsDBNull(2)?"none":reader.GetString(2))); if(status=="ready") ready++; }
            if(ready==documents.Length) return;
            await Task.Delay(250,deadline.Token);
        }
    }
    private static async Task VerifyAudit(NpgsqlConnection db,Fixture f,JsonElement result,Guid run,string request)
    {
        Check(result.GetProperty("requestId").GetString()==request,"Answer must preserve gateway correlation.");
        foreach(var (table,key) in new[] { ("platform_model_calls","modelRequestId"), ("platform_knowledge_searches","searchRequestId") })
        {
            await using var command=new NpgsqlCommand($"SELECT count(*) FROM public.{table} WHERE tenant_id=$1 AND user_id=$2 AND request_id=$3 AND app_id='knowledge' AND release_version='0.1.0' AND run_id=$4 AND gateway_request_id=$5 AND outcome='completed' AND finished_at IS NOT NULL",db);
            foreach(var value in new object[] { f.Tenant,f.Owner,result.GetProperty(key).GetString()!,run,request }) command.Parameters.AddWithValue(value);
            Check(await command.ExecuteScalarAsync() is 1L,"Search and model audits must preserve tenant/user/application/run/source request.");
        }
    }
    private static async Task DuplicateNotifications(NpgsqlConnection db,JsonElement rabbit,Fixture f,Guid document)
    {
        var before = await IndexState(db,document);
        await using var command = new NpgsqlCommand("SELECT job_id FROM public.knowledge_jobs WHERE tenant_id=$1 AND document_id=$2 AND kind='embed' AND status='completed'",db);
        command.Parameters.AddWithValue(f.Tenant);command.Parameters.AddWithValue(document);
        var job=(Guid)(await command.ExecuteScalarAsync())!;
        using var http = new HttpClient { BaseAddress=new Uri(rabbit.GetProperty("managementEndpoint").GetString()!),Timeout=TimeSpan.FromSeconds(10) };
        var credential=Convert.ToBase64String(Encoding.UTF8.GetBytes(rabbit.GetProperty("username").GetString()+":"+rabbit.GetProperty("password").GetString()));
        http.DefaultRequestHeaders.Authorization=new("Basic",credential);
        var vhost=Uri.EscapeDataString(rabbit.GetProperty("virtualHost").GetString()!);
        for(var i=0;i<2;i++)
        {
            using var response=await http.PostAsJsonAsync($"/api/exchanges/{vhost}/amq.default/publish",new {
                properties=new { content_type="application/json",delivery_mode=2,message_id=job.ToString("N") },routing_key=f.Queue,
                payload=JsonSerializer.Serialize(new { schemaVersion=1,jobId=job }),payload_encoding="string" });
            using var json=JsonDocument.Parse(await Expect(response,200));Check(json.RootElement.GetProperty("routed").GetBoolean(),"Duplicate notification must reach the real durable queue.");
        }
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while(true)
        {
            using var response=await http.GetAsync($"/api/queues/{vhost}/{Uri.EscapeDataString(f.Queue)}",deadline.Token);
            using var json=JsonDocument.Parse(await Expect(response,200));var queue=json.RootElement;
            if(queue.TryGetProperty("messages",out var pending) && pending.GetInt32()==0)break;
            await Task.Delay(250,deadline.Token);
        }
        Check(await IndexState(db,document)==before,"Duplicate completed-job notifications cannot create duplicate vectors or reopen terminal work.");
    }
    private static async Task<string> IndexState(NpgsqlConnection db,Guid document)
    {
        await using var command=new NpgsqlCommand("SELECT (SELECT count(*) FROM public.knowledge_embeddings WHERE document_id=$1)::text||':'||(SELECT string_agg(kind||':'||status||':'||attempts::text,',' ORDER BY kind) FROM public.knowledge_jobs WHERE document_id=$1)",db);
        command.Parameters.AddWithValue(document);return (string)(await command.ExecuteScalarAsync())!;
    }
    private static byte[] Docx(string text)
    {
        using var bytes=new MemoryStream();
        using(var zip=new ZipArchive(bytes,ZipArchiveMode.Create,true))
        {
            void Add(string name,string value) { using var output=new StreamWriter(zip.CreateEntry(name).Open(),new UTF8Encoding(false)); output.Write(value); }
            Add("[Content_Types].xml","<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            Add("word/document.xml","<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>"+SecurityElement.Escape(text)+"</w:t></w:r></w:p></w:body></w:document>");
        }
        return bytes.ToArray();
    }
    private static byte[] Pdf(string text)
    {
        Check(text.All(c=>c is >= ' ' and <= '~'),"PDF fixture text must be ASCII.");
        var escaped=text.Replace("\\","\\\\",StringComparison.Ordinal).Replace("(","\\(",StringComparison.Ordinal).Replace(")","\\)",StringComparison.Ordinal);
        var stream=$"BT /F1 10 Tf 20 780 Td ({escaped}) Tj ET";
        var objects=new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 800 820] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}\nendstream" };
        var builder=new StringBuilder("%PDF-1.4\n"); var offsets=new List<int>();
        for(var i=0;i<objects.Length;i++) { offsets.Add(builder.Length); builder.Append($"{i+1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref=builder.Length; builder.Append("xref\n0 6\n0000000000 65535 f \n"); foreach(var offset in offsets) builder.Append($"{offset:0000000000} 00000 n \n");
        builder.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); return Encoding.ASCII.GetBytes(builder.ToString());
    }
    private static Child StartPython(string python,string workspace,string app,string origin,string? key,string? model,string? platform)
    {
        var start=ProcessInfo(python,Path.Combine(workspace,"apps/knowledge/backend"),["-m","uvicorn",app,"--host","127.0.0.1","--port",new Uri(origin).Port.ToString(),"--no-access-log","--log-level","error"]);
        start.Environment["PYTHONIOENCODING"]="utf-8"; start.Environment["ASSEMBLEAI_ENVIRONMENT"]="Development";
        if(key is not null) { start.Environment["ASSEMBLEAI_KNOWLEDGE_PARSER_KEY"]=key; start.Environment["ASSEMBLEAI_KNOWLEDGE_MODEL_DIRECTORY"]=model!; }
        if(platform is not null)
        {
            start.Environment["ASSEMBLEAI_SERVICE"]="knowledge"; start.Environment["ASSEMBLEAI_SERVICE_VERSION"]=Version;
            start.Environment["ASSEMBLEAI_VALIDATION_URL"]=platform+"/internal/calls/validate";
            start.Environment["ASSEMBLEAI_MODEL_GATEWAY_ORIGIN"]=platform; start.Environment["ASSEMBLEAI_KNOWLEDGE_GATEWAY_ORIGIN"]=platform;
            start.Environment["ASSEMBLEAI_MODEL_ID"]="assistant"; start.Environment["ASSEMBLEAI_MODEL_MAX_OUTPUT_TOKENS"]="2048";
        }
        Check(start.Environment.Keys.All(name=>!name.StartsWith("ConnectionStrings",StringComparison.OrdinalIgnoreCase)),"Child cannot inherit database credentials.");
        return Launch(start);
    }
    private static ProcessStartInfo ProcessInfo(string exe,string cwd,string[] args)
    {
        var start=new ProcessStartInfo(exe) { WorkingDirectory=cwd,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
        var allowed=new HashSet<string>(["SystemRoot","WINDIR","PATH","PATHEXT","TEMP","TMP","USERPROFILE","LOCALAPPDATA","APPDATA","COMSPEC","NUMBER_OF_PROCESSORS","PROCESSOR_ARCHITECTURE","PLAYWRIGHT_BROWSERS_PATH"],StringComparer.OrdinalIgnoreCase);
        foreach(var name in start.Environment.Keys.ToArray()) if(!allowed.Contains(name)) start.Environment.Remove(name);
        foreach(var arg in args) start.ArgumentList.Add(arg); return start;
    }
    private static Child Start(string exe,string cwd,string[] args)=>Launch(ProcessInfo(exe,cwd,args));
    private static Child Launch(ProcessStartInfo start) { var p=Process.Start(start)??throw new InvalidOperationException("Cannot start knowledge fixture."); return new(p,p.StandardOutput.ReadToEndAsync(),p.StandardError.ReadToEndAsync()); }
    private static async Task Run(string exe,string cwd,string[] args,int seconds) { await using var child=Start(exe,cwd,args); using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(seconds)); await child.Process.WaitForExitAsync(deadline.Token); Check(child.Process.ExitCode==0,"Knowledge fixture process failed: "+await child.Output+await child.Errors); }
    private static async Task Ready(Child child,string url) { using var client=new HttpClient { Timeout=TimeSpan.FromSeconds(1) }; using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(40)); while(true) { Check(!child.Process.HasExited,"Knowledge child ended before readiness."); try { using var response=await client.GetAsync(url,deadline.Token); if(response.IsSuccessStatusCode)return; } catch(Exception error) when(error is HttpRequestException or OperationCanceledException) { } await Task.Delay(100,deadline.Token); } }
    private static async Task<string> Expect(HttpResponseMessage response,int status) { Check((int)response.StatusCode==status,$"Knowledge HTTP expected {status}, received {(int)response.StatusCode}."); return await response.Content.ReadAsStringAsync(); }
    private static Task StreamStatus(HttpResponseMessage response) { Check(response.StatusCode==HttpStatusCode.OK&&response.Content.Headers.ContentType?.MediaType=="text/event-stream","Knowledge answer must be SSE."); return Task.CompletedTask; }
    private static async Task<(string Type,JsonElement Value)> Event(StreamReader reader)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(180)); string? type=null,data=null;
        while(true) { var line=await reader.ReadLineAsync(timeout.Token); if(line is null)throw new EndOfStreamException("Knowledge stream ended before a terminal event.");
            if(line!.StartsWith("event:",StringComparison.Ordinal))type=line[6..].Trim(); else if(line.StartsWith("data:",StringComparison.Ordinal))data=line[5..].Trim();
            else if(line.Length==0&&type is not null&&data is not null) { using var json=JsonDocument.Parse(data);return(type,json.RootElement.Clone()); } }
    }
    private static async Task<Answer> Collect(StreamReader reader) { for(var i=0;i<20;i++) { var item=await Event(reader); if(item.Type=="complete")return new(item.Value,null); if(item.Type=="error")return new(null,item.Value.GetProperty("code").GetString()); } throw new InvalidOperationException("Knowledge stream exceeded event budget."); }
    private static async Task ExpectDenied(StreamReader reader)
    {
        try
        {
            var answer=await Collect(reader).WaitAsync(TimeSpan.FromSeconds(15));
            Check(answer.Complete is null,"Revocation/deletion cannot deliver complete, even when the changed document is not cited.");
        }
        catch(IOException) { /* The gateway can terminate an already-started SSE response on revocation. */ }
    }
    private sealed class Browser(HttpClient client,string origin,string csrf):IDisposable
    {
        public Guid Tenant { get;private set; } public Guid Run { get;private set; }
        public static async Task<Browser> Login(string origin)
        {
            var client=new HttpClient(new SocketsHttpHandler { AllowAutoRedirect=false,CookieContainer=new(),MaxResponseDrainSize=0 }) { BaseAddress=new Uri(origin),Timeout=TimeSpan.FromSeconds(185) };
            try { using var challenge=await client.GetAsync("/bff/login?returnUrl=/bff/session"); Check(challenge.StatusCode==HttpStatusCode.Redirect,"OIDC challenge required."); using var code=await client.GetAsync(challenge.Headers.Location); using var callback=await client.GetAsync(code.Headers.Location); Check(callback.StatusCode==HttpStatusCode.Redirect,"OIDC callback required."); using var response=await client.GetAsync("/bff/csrf"); using var json=JsonDocument.Parse(await Expect(response,200)); return new(client,origin,json.RootElement.GetProperty("requestToken").GetString()!); } catch {client.Dispose();throw;}
        }
        public async Task SelectTenant(Guid tenant) { using var response=await Send("POST","/bff/tenant",new { tenantId=tenant }); await Expect(response,200); Tenant=tenant; }
        public async Task StartRun() { using var response=await Send("POST",$"/api/platform/v1/tenants/{Tenant}/applications/knowledge/releases/{Version}/runs"); using var json=JsonDocument.Parse(await Expect(response,200)); Run=json.RootElement.GetProperty("runId").GetGuid(); }
        public async Task<HttpResponseMessage> Send(string method,string path,object? body=null) { using var request=Request(method,path);if(body is not null)request.Content=JsonContent.Create(body);return await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead); }
        public async Task<HttpResponseMessage> Bytes(string path,byte[] bytes) { using var request=Request("POST",path);request.Content=new ByteArrayContent(bytes);request.Content.Headers.ContentType=new("application/octet-stream");return await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead); }
        private HttpRequestMessage Request(string method,string path) { var request=new HttpRequestMessage(new HttpMethod(method),path);if(method is "POST" or "PUT" or "DELETE") { request.Headers.Add("Origin",origin);request.Headers.Add(BffEndpoints.CsrfHeaderName,csrf); } if(path.StartsWith("/api/apps/",StringComparison.Ordinal)) { request.Headers.Add("X-Run-ID",Run.ToString("D"));request.Headers.Add("X-Tenant-ID",Tenant.ToString("D"));request.Headers.Add("X-Knowledge-Delegation","browser-forged-knowledge-capability"); } return request; }
        public void Dispose()=>client.Dispose();
    }
    private sealed record Child(Process Process,Task<string> Output,Task<string> Errors):IAsyncDisposable { public async ValueTask DisposeAsync() { if(!Process.HasExited)Process.Kill(entireProcessTree:true);await Process.WaitForExitAsync();Process.Dispose(); } }
    private sealed class Hold { public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);public TaskCompletionSource Cancelled {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously); }
    private sealed class Fixture
    {
        public Guid Owner {get;}=Guid.NewGuid();public Guid Reviewer {get;}=Guid.NewGuid();public Guid Tenant {get;}=Guid.NewGuid();public Guid OtherTenant {get;}=Guid.NewGuid();public Guid Role {get;}=Guid.NewGuid();
        public Guid FactoryA {get;}=Guid.NewGuid();public Guid FactoryB {get;}=Guid.NewGuid();public Guid ReadGrant {get;}=Guid.NewGuid();public Guid ManageGrant {get;}=Guid.NewGuid();
        public string ParserVariable {get;}="ASSEMBLEAI_KNOWLEDGE_PARSER_"+Id().ToUpperInvariant();public string AccessVariable {get;}="ASSEMBLEAI_KNOWLEDGE_ACCESS_"+Id().ToUpperInvariant();
        public string SecretVariable {get;}="ASSEMBLEAI_KNOWLEDGE_SECRET_"+Id().ToUpperInvariant();public string BrokerVariable {get;}="ASSEMBLEAI_KNOWLEDGE_BROKER_"+Id().ToUpperInvariant();public string Queue {get;}="knowledge-fixture-"+Id();
    }
    private sealed record Provider(string Endpoint,string Model,string CredentialVariable,string? ReasoningEffort);
    private sealed record Answer(JsonElement? Complete,string? Error);
    private const string AskPath="/api/apps/knowledge/releases/0.1.0/ask",ReferencesPath="/api/apps/knowledge/releases/0.1.0/references";
    private static string Documents(Guid tenant)=>$"/api/platform/v1/tenants/{tenant}/knowledge/documents";
    private static string Id()=>Guid.NewGuid().ToString("N");
    private static string FreeOrigin() { using var socket=new TcpListener(IPAddress.Loopback,0);socket.Start();return "http://127.0.0.1:"+((IPEndPoint)socket.LocalEndpoint).Port; }
    private static string Workspace() { for(var path=new DirectoryInfo(AppContext.BaseDirectory);path is not null;path=path.Parent)if(File.Exists(Path.Combine(path.FullName,"AssembleAI.slnx")))return path.FullName;throw new InvalidOperationException("Workspace not found."); }
    private static async Task Execute(NpgsqlConnection db,string sql,params object[] values) { await using var command=new NpgsqlCommand(sql,db);foreach(var value in values)command.Parameters.AddWithValue(value);await command.ExecuteNonQueryAsync(); }
    private static void Ok<T>(RegistryResult<T> result)=>Check(result.Error==RegistryError.None,"Knowledge registry transition failed.");
    private static void Check(bool condition,string message) { if(!condition)throw new InvalidOperationException(message); }
}
