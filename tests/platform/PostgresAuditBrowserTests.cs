using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Npgsql;
using static AssembleAI.Platform.SmokeTests.IdentityManagementFixture;

namespace AssembleAI.Platform.SmokeTests;

// Only the launcher-verified disposable PostgreSQL instance reaches this fixture.
public static class PostgresAuditBrowserTests
{
    private const string Api = "/api/platform/v1/audit";
    public static async Task RunAsync(string connectionString, string runDirectory, bool browser)
    {
        var f = new IdentityManagementFixture();
        var stamp = DateTimeOffset.UtcNow.AddMinutes(-2);
        var correlation = Guid.NewGuid().ToString("N");
        var hidden = Guid.NewGuid(); var allowed = Guid.NewGuid();
        await using var oidc = await OidcFixture.StartAsync(new Dictionary<string, string> {
            [f.Admin.ToString()] = "审计管理员", [f.Member.ToString()] = "模型审计员",
            [f.Root.ToString()] = "平台身份管理员", [f.Developer.ToString()] = "发布审核员" });
        await using var db = new NpgsqlConnection(connectionString); await db.OpenAsync();
        try
        {
            await f.Seed(db, oidc.Authority);
            await Execute(db, "INSERT INTO public.tenant_memberships(tenant_id,user_id) VALUES($1,$2)", f.Tenant, f.Root);
            await Grant(db, f.Tenant, f.Admin, [
                ("model:manage", "model", "assistant"), ("datasource:manage", "datasource", "factory-db"),
                ("gateway:observe", "application", "sample"), ("registry:manage", "application", "sample"),
                ("audit:read", "audit", "knowledge"), ("audit:export", "audit", "events")]);
            await Grant(db, f.Tenant, f.Member, [("model:manage", "model", "assistant")]);
            await Seed(db, f, stamp, correlation, allowed, hidden);
            var origin = FreeOrigin(); var root = Workspace();
            await using var app = Application(connectionString, oidc, origin);
            using var files = browser ? new PhysicalFileProvider(Path.Combine(root, "apps/portal/dist")) : null;
            if (files is not null)
            {
                app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
                app.MapGet("/", () => Results.File(Path.Combine(files.Root, "index.html"), "text/html"));
                app.MapGet("/manage/{**path}", () => Results.File(Path.Combine(files.Root, "index.html"), "text/html"));
            }
            await app.StartAsync();
            try
            {
                await Contracts(db, oidc, origin, f, stamp, correlation, allowed, hidden);
                await RevokeWhileReading(connectionString, db, origin, f, stamp);
                if (browser)
                {
                    var config = Path.Combine(runDirectory, "audit-browser.json");
                    await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new { origin, tenantId = f.Tenant,
                        otherTenantId = f.OtherTenant, correlation, allowedId = allowed.ToString("N"), hiddenId = hidden.ToString("N") }));
                    var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ASSEMBLEAI_NODE") ?? "node") {
                        WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true };
                    start.ArgumentList.Add(Path.Combine(root, "tests/frontend/audit-postgres-browser.mjs")); start.ArgumentList.Add(config);
                    foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("ASSEMBLEAI_", StringComparison.OrdinalIgnoreCase)
                        || key.StartsWith("ConnectionStrings", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
                    using var process = Process.Start(start) ?? throw new InvalidOperationException("Audit browser could not start.");
                    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(180));
                    try { await process.WaitForExitAsync(budget.Token); }
                    catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
                    var transcript = await stdout + await stderr; Console.Write(transcript.Length > 20000 ? transcript[..20000] : transcript);
                    Check(process.ExitCode == 0, "Audit built-page verification failed.");
                }
            }
            finally { await app.StopAsync(); }
        }
        finally
        {
            foreach (var table in new[] { "platform_gateway_calls", "platform_model_calls", "platform_connector_calls", "platform_knowledge_searches",
                "platform_model_configuration_audit", "platform_connector_configuration_audit", "platform_knowledge_audit", "platform_audit_exports" })
                await Execute(db, $"DELETE FROM public.{table} WHERE tenant_id=ANY($1)", new[] { f.Tenant, f.OtherTenant });
            await Execute(db, "DELETE FROM public.platform_registry_audit WHERE actor_user_id=ANY($1)", new[] { f.Admin, f.Developer });
            await f.Cleanup(db);
        }
        Console.WriteLine("PASS: unified audit real OIDC/BFF/PostgreSQL resource/global scope, stable pagination, correlation, safe metadata, export bounds and revocation" + (browser ? ", plus built browser pages." : "."));
    }
    private static async Task Grant(NpgsqlConnection db, Guid tenant, Guid user, (string Permission, string Type, string Id)[] grants)
    {
        var role = Guid.NewGuid();
        await Execute(db, "INSERT INTO public.tenant_roles(tenant_id,role_id,name) VALUES($1,$2,$3)", tenant, role, "审计验收角色-" + user.ToString("N"));
        await Execute(db, "INSERT INTO public.tenant_member_roles(tenant_id,user_id,role_id) VALUES($1,$2,$3)", tenant, user, role);
        foreach (var grant in grants)
            await Execute(db, "INSERT INTO public.role_permission_grants(tenant_id,grant_id,role_id,permission,resource_type,resource_id,tenant_wide) VALUES($1,$2,$3,$4,$5,$6,true)",
                tenant, Guid.NewGuid(), role, grant.Permission, grant.Type, grant.Id);
    }
    private static async Task Seed(NpgsqlConnection db, IdentityManagementFixture f, DateTimeOffset stamp, string request, Guid allowed, Guid hidden)
    {
        await Execute(db, """
            INSERT INTO public.platform_model_calls(call_id,tenant_id,user_id,model_id,request_id,started_at,finished_at,outcome,elapsed_milliseconds)
            SELECT gen_random_uuid(),$1,$2,'assistant',replace(gen_random_uuid()::text,'-',''),$3,$3,'completed',12 FROM generate_series(1,55)
            """, f.Tenant, f.Admin, stamp);
        await Execute(db, "INSERT INTO public.platform_model_calls(call_id,tenant_id,user_id,model_id,request_id,started_at) VALUES($1,$2,$3,'assistant',$4,$5)", Guid.NewGuid(), f.Tenant, f.Admin, Guid.NewGuid().ToString("N"), stamp);
        await Execute(db, "INSERT INTO public.platform_model_calls(call_id,tenant_id,user_id,model_id,request_id,started_at,finished_at,outcome,elapsed_milliseconds) VALUES($1,$2,$3,'assistant',$4,$5,$5,'completed',12)", Guid.NewGuid(), f.Tenant, f.Admin, Guid.NewGuid().ToString("N"), stamp.AddDays(-2));
        foreach (var (tenant, id, model) in new[] { (f.Tenant, allowed, "assistant"), (f.Tenant, hidden, "private-model"), (f.OtherTenant, Guid.NewGuid(), "assistant") })
            await Execute(db, "INSERT INTO public.platform_model_calls(call_id,tenant_id,user_id,model_id,request_id,started_at,finished_at,outcome,elapsed_milliseconds,app_id,release_version,run_id,gateway_request_id) VALUES($1,$2,$3,$4,$5,$6,$6,'completed',10,'sample','1.0.0',$7,$8)", id, tenant, f.Admin, model, Guid.NewGuid().ToString("N"), stamp, Guid.NewGuid(), request);
        await Execute(db, "INSERT INTO public.platform_gateway_calls(request_id,run_id,user_id,tenant_id,app_id,release_version,method,path,started_at,finished_at,http_status,outcome,elapsed_milliseconds) VALUES($1,$2,$3,$4,'sample','1.0.0','GET','/private?token=private-marker',$5,$5,200,'transport_completed',30)", request, Guid.NewGuid(), f.Admin, f.Tenant, stamp);
        await Execute(db, "INSERT INTO public.platform_connector_calls(call_id,tenant_id,user_id,source_id,dataset_id,operation,request_id,started_at,finished_at,outcome,row_count,elapsed_milliseconds,app_id,release_version,run_id,gateway_request_id) VALUES($1,$2,$3,'factory-db','production','query',$4,$5,$5,'completed',2,15,'sample','1.0.0',$6,$7)", Guid.NewGuid(), f.Tenant, f.Admin, Guid.NewGuid().ToString("N"), stamp, Guid.NewGuid(), request);
        await Execute(db, "INSERT INTO public.platform_knowledge_searches(call_id,tenant_id,user_id,request_id,authorization_version,profile,outcome,hit_count,elapsed_milliseconds,started_at,finished_at) VALUES($1,$2,$3,$4,1,'e5-small-dd476dd0-384-v1','failed',0,5,$5,$5)", Guid.NewGuid(), f.Tenant, f.Admin, Guid.NewGuid().ToString("N"), stamp);
        await Execute(db, "INSERT INTO public.platform_model_configuration_audit(event_id,actor_user_id,tenant_id,model_id,request_id,revision,enabled,max_output_tokens,occurred_at) VALUES($1,$2,$3,'assistant',$4,1,true,512,$5)", Guid.NewGuid(), f.Admin, f.Tenant, Guid.NewGuid().ToString("N"), stamp);
        await Execute(db, "INSERT INTO public.platform_connector_configuration_audit(event_id,actor_user_id,tenant_id,source_id,request_id,revision,enabled,max_rows,timeout_seconds,occurred_at) VALUES($1,$2,$3,'factory-db',$4,1,true,50,5,$5)", Guid.NewGuid(), f.Admin, f.Tenant, Guid.NewGuid().ToString("N"), stamp);
        await Execute(db, "INSERT INTO public.platform_knowledge_audit(event_id,tenant_id,document_id,actor_user_id,action,revision,request_id,occurred_at) VALUES($1,$2,$3,$4,'delete',2,$5,$6)", Guid.NewGuid(), f.Tenant, Guid.NewGuid(), f.Admin, Guid.NewGuid().ToString("N"), stamp);
        foreach (var global in new[] { false, true })
        {
            await using var registry = new NpgsqlCommand("INSERT INTO public.platform_registry_audit(event_id,actor_user_id,tenant_id,app_id,release_version,action,result,request_id,occurred_at) VALUES($1,$2,$3,'sample','1.0.0','set_tenant','success',$4,$5)", db);
            registry.Parameters.AddWithValue(Guid.NewGuid()); registry.Parameters.AddWithValue(global ? f.Developer : f.Admin);
            registry.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid, Value = global ? DBNull.Value : f.Tenant });
            registry.Parameters.AddWithValue(Guid.NewGuid().ToString("N")); registry.Parameters.AddWithValue(stamp); await registry.ExecuteNonQueryAsync();
            await using var identity = new NpgsqlCommand("INSERT INTO public.platform_identity_audit(event_id,actor_user_id,tenant_id,entity_type,entity_id,action,result,request_id,occurred_at) VALUES($1,$2,$3,'role',$4,'update','success',$5,$6)", db);
            identity.Parameters.AddWithValue(Guid.NewGuid()); identity.Parameters.AddWithValue(global ? f.Root : f.Admin);
            identity.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid, Value = global ? DBNull.Value : f.Tenant });
            identity.Parameters.AddWithValue(Guid.NewGuid()); identity.Parameters.AddWithValue(Guid.NewGuid().ToString("N")); identity.Parameters.AddWithValue(stamp); await identity.ExecuteNonQueryAsync();
        }
    }
    private static string Query(DateTimeOffset stamp, string source = "", string extra = "", string scope = "tenant", int size = 17) =>
        "?scope=" + scope + "&from=" + Uri.EscapeDataString(stamp.AddMinutes(-1).UtcDateTime.ToString("O"))
        + "&to=" + Uri.EscapeDataString(stamp.AddMinutes(1).UtcDateTime.ToString("O")) + "&pageSize=" + size
        + (source.Length > 0 ? "&source=" + source : "") + extra;
    private static async Task Contracts(NpgsqlConnection db, OidcFixture oidc, string origin, IdentityManagementFixture f, DateTimeOffset stamp, string request, Guid allowed, Guid hidden)
    {
        using var manager = await Login(origin, f.Admin); await manager.Select(f.Tenant);
        using var limited = await Login(origin, f.Member); await limited.Select(f.Tenant);
        using var root = await Login(origin, f.Root);
        var workspace = await manager.Call("GET", Api + "/workspace?scope=tenant");
        Check(workspace.GetProperty("canExport").GetBoolean(), "Explicit export grant must be discoverable.");
        var sources = workspace.GetProperty("sources").EnumerateArray().Select(x => x.GetString()).ToHashSet();
        Check(sources.Count == 10, "Manager must have all ten authorized metadata sources.");
        var query = Query(stamp); var first = await manager.Call("GET", Api + "/events" + query);
        var next = first.GetProperty("nextCursor").GetString(); Check(next is not null, "Seeded same-time history must paginate.");
        var ids = new HashSet<string>(); var page = first; var rounds = 0;
        do
        {
            foreach (var item in page.GetProperty("items").EnumerateArray())
            {
                Check(item.GetProperty("tenantId").GetGuid() == f.Tenant && item.GetProperty("resourceId").GetString() != "private-model", "Historical metadata must preserve tenant and exact-resource isolation.");
                Check(ids.Add(item.GetProperty("source").GetString() + "/" + item.GetProperty("id").GetString()), "Same-time keyset pages cannot repeat a row.");
                Check(!item.GetRawText().Contains("private-marker") && !item.TryGetProperty("path", out _), "Audit metadata cannot include gateway path/query secrets.");
            }
            var cursor = page.GetProperty("nextCursor").GetString(); if (cursor is null) break;
            page = await manager.Call("GET", Api + "/events" + query + "&cursor=" + Uri.EscapeDataString(cursor));
        } while (++rounds < 20);
        Check(ids.Count == 65, "All authorized rows across nine seeded sources must be enumerated exactly once; older records stay outside the time window.");
        await limited.Call("GET", Api + "/events" + query + "&cursor=" + Uri.EscapeDataString(next!), expected: 400);
        var midpoint = next!.Length / 2;
        var tampered = next[..midpoint] + (next[midpoint] == 'a' ? "b" : "a") + next[(midpoint + 1)..];
        await manager.Call("GET", Api + "/events" + query + "&cursor=" + Uri.EscapeDataString(tampered), expected: 400);
        var linked = await manager.Call("GET", Api + "/events" + Query(stamp, extra: "&requestId=" + request));
        Check(linked.GetProperty("items").GetArrayLength() == 3, "Request correlation combines gateway and authorized delegated calls only.");
        Check((await manager.Call("GET", Api + "/events" + Query(stamp, extra: "&status=failure"))).GetProperty("items").GetArrayLength() == 1, "Failure filtering returns the failed search metadata only.");
        Check((await manager.Call("GET", Api + "/events" + Query(stamp, extra: "&status=unfinished"))).GetProperty("items").GetArrayLength() == 1, "Unfinished filtering must not claim a running record completed.");
        Check((await manager.Call("GET", Api + "/events" + Query(stamp, extra: "&appId=sample"))).GetProperty("items").GetArrayLength() == 4, "Application filtering preserves authorized call and registry history.");
        await manager.Call("GET", Api + "/events/model/" + hidden.ToString("N") + Query(stamp), expected: 404);
        var detail = await manager.Call("GET", Api + "/events/model/" + allowed.ToString("N") + Query(stamp));
        Check(detail.GetProperty("gatewayRequestId").GetString() == request, "Authorized detail retains safe gateway correlation.");
        var own = await limited.Call("GET", Api + "/workspace?scope=tenant");
        Check(!own.GetProperty("canExport").GetBoolean() && own.GetProperty("sources").GetArrayLength() == 2, "Resource manager cannot inherit broader audit or export rights.");
        await limited.Call("GET", Api + "/events" + Query(stamp, "connector"), expected: 403);
        await limited.Call("POST", Api + "/export" + Query(stamp).Replace("&pageSize=17", ""), new { }, expected: 403);
        await manager.Call("POST", Api + "/export" + Query(stamp).Replace("&pageSize=17", ""), new { }, expected: 403, csrf: false);
        await root.Call("GET", Api + "/workspace?scope=tenant", expected: 403);
        var global = await root.Call("GET", Api + "/events" + Query(stamp, scope: "global"));
        Check(global.GetProperty("items").GetArrayLength() == 1 && global.GetProperty("items").EnumerateArray().All(x => x.GetProperty("source").GetString() == "identity" && x.GetProperty("tenantId").ValueKind == JsonValueKind.Null), "Global identity administration cannot expose tenant/business audit.");
        using var reviewer = await Login(origin, f.Developer);
        var releaseHistory = await reviewer.Call("GET", Api + "/events" + Query(stamp, scope: "global"));
        Check(releaseHistory.GetProperty("items").GetArrayLength() == 1 && releaseHistory.GetProperty("items")[0].GetProperty("source").GetString() == "registry", "Global reviewer sees release history without identity or business audit.");
        await root.Select(f.Tenant); await root.Call("GET", Api + "/events" + Query(stamp, "model"), expected: 403);
        await manager.Call("GET", Api + "/events?scope=tenant", expected: 400);
        await manager.Call("GET", Api + "/events" + query + "&unknown=value", expected: 400);
        await manager.Call("GET", Api + "/events" + query + "&pageSize=2", expected: 400);
        await manager.Select(f.OtherTenant);
        Check((await manager.Call("GET", Api + "/events" + query)).GetProperty("items").GetArrayLength() == 0, "Selecting another tenant clears historical visibility.");
        await manager.Select(f.Tenant);
        await Execute(db, "UPDATE public.role_permission_grants SET enabled=false WHERE tenant_id=$1 AND permission='model:manage'", f.Tenant);
        Check((await limited.Call("GET", Api + "/events" + query)).GetProperty("items").GetArrayLength() == 0, "Revocation removes previously visible history immediately.");
        await manager.Call("GET", Api + "/events/model/" + allowed.ToString("N") + query, expected: 404);
        await Execute(db, "UPDATE public.role_permission_grants SET enabled=true WHERE tenant_id=$1 AND permission='model:manage'", f.Tenant);
        await Execute(db, "INSERT INTO public.platform_model_calls(call_id,tenant_id,user_id,model_id,request_id,started_at,finished_at,outcome,elapsed_milliseconds) SELECT gen_random_uuid(),$1,$2,'assistant',replace(gen_random_uuid()::text,'-',''),$3,$3,'completed',1 FROM generate_series(1,1005)", f.Tenant, f.Admin, stamp);
        var exported = await manager.Call("POST", Api + "/export" + Query(stamp, "model").Replace("&pageSize=17", ""), new { });
        Check(exported.GetProperty("items").GetArrayLength() == 1000 && exported.GetProperty("truncated").GetBoolean(), "Export must bound the entire authorized result and explicitly disclose truncation.");
        Check(await Scalar<long>(db, "SELECT count(*) FROM public.platform_audit_exports WHERE tenant_id=$1 AND actor_user_id=$2", f.Tenant, f.Admin) == 1, "Successful export must have durable attributable audit.");
        var exportHistory = await manager.Call("GET", Api + "/events" + Query(DateTimeOffset.UtcNow, "audit-export"));
        Check(exportHistory.GetProperty("items").GetArrayLength() == 1
            && exportHistory.GetProperty("items")[0].GetProperty("details").GetProperty("recordCount").GetString() == "1000"
            && exportHistory.GetProperty("items")[0].GetProperty("details").GetProperty("truncated").GetString() == "true",
            "The export audit source exposes safe count and truncation metadata for the authorized operation.");
        await limited.Call("GET", Api + "/events" + Query(DateTimeOffset.UtcNow, "audit-export"), expected: 403);
        var failureTrigger = "audit_failure_" + Guid.NewGuid().ToString("N");
        await Execute(db, $"CREATE FUNCTION test_support.{failureTrigger}() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RAISE EXCEPTION ''private-audit-database-error''; END;'");
        await Execute(db, $"CREATE TRIGGER {failureTrigger} BEFORE INSERT ON public.platform_audit_exports FOR EACH ROW EXECUTE FUNCTION test_support.{failureTrigger}()");
        try
        {
            var failure = await manager.Call("POST", Api + "/export" + Query(stamp, "model").Replace("&pageSize=17", ""), new { }, expected: 503);
            Check(!failure.TryGetProperty("items", out _) && !failure.GetRawText().Contains("private-audit-database-error"), "Export audit failure must withhold records and raw database errors.");
            Check(await Scalar<long>(db, "SELECT count(*) FROM public.platform_audit_exports WHERE tenant_id=$1", f.Tenant) == 1, "Failed export cannot append a successful audit event.");
        }
        finally { await Execute(db, $"DROP TRIGGER {failureTrigger} ON public.platform_audit_exports; DROP FUNCTION test_support.{failureTrigger}()"); }
        await Execute(db, "DELETE FROM public.platform_model_calls WHERE tenant_id=$1 AND model_id='assistant' AND elapsed_milliseconds=1", f.Tenant);
    }
    private static async Task<IdentityBrowser> Login(string origin, Guid user)
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, CookieContainer = new() }) { BaseAddress = new(origin), Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            using var challenge = await client.GetAsync("/bff/login?returnUrl=/bff/session");
            using var code = await client.GetAsync(challenge.Headers.Location!.ToString() + "&__fixture_subject=" + user);
            using var callback = await client.GetAsync(code.Headers.Location); Check(callback.StatusCode == HttpStatusCode.Redirect, "Validated OIDC callback is required.");
            using var csrf = await client.GetAsync("/bff/csrf"); using var json = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());
            return new(client, origin, json.RootElement.GetProperty("requestToken").GetString()!);
        }
        catch { client.Dispose(); throw; }
    }
    private static async Task RevokeWhileReading(string connection, NpgsqlConnection db, string origin, IdentityManagementFixture f, DateTimeOffset stamp)
    {
        foreach (var reason in new[] { "logout", "tenant", "permission", "exportlogout" })
        {
            using var session = await Login(origin, f.Admin); await session.Select(f.Tenant);
            var exports = await Scalar<long>(db, "SELECT count(*) FROM public.platform_audit_exports WHERE tenant_id=$1", f.Tenant);
            var exporting = reason == "exportlogout";
            await using var blocker = new NpgsqlConnection(connection); await blocker.OpenAsync();
            await using var transaction = await blocker.BeginTransactionAsync();
            await Execute(blocker, exporting ? "LOCK TABLE public.platform_audit_exports IN SHARE ROW EXCLUSIVE MODE" : "LOCK TABLE public.platform_model_calls IN ACCESS EXCLUSIVE MODE");
            var pending = exporting
                ? session.Call("POST", Api + "/export" + Query(stamp, "model").Replace("&pageSize=17", ""), new { }, expected: 403)
                : session.Call("GET", Api + "/events" + Query(stamp, "model"), expected: 403);
            try
            {
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                while (!await Scalar<bool>(db, "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid<>$1 AND $1=ANY(pg_blocking_pids(pid)))", blocker.ProcessID))
                    await Task.Delay(10, budget.Token);
                if (reason is "logout" or "exportlogout") await session.Call("POST", "/bff/logout", expected: 204);
                else if (reason == "tenant") await session.Select(f.OtherTenant);
                else await Execute(db, "UPDATE public.role_permission_grants SET enabled=false WHERE tenant_id=$1 AND permission='model:manage'", f.Tenant);
                await transaction.CommitAsync();
                var denied = await pending;
                Check(!denied.TryGetProperty("items", out _), "Late revocation cannot deliver audit rows or export content.");
                Check(await Scalar<long>(db, "SELECT count(*) FROM public.platform_audit_exports WHERE tenant_id=$1", f.Tenant) == exports, "Revoked export cannot persist a successful export audit.");
            }
            finally
            {
                if (reason == "permission") await Execute(db, "UPDATE public.role_permission_grants SET enabled=true WHERE tenant_id=$1 AND permission='model:manage'", f.Tenant);
            }
        }
        Console.WriteLine("PASS: audit real database waits followed by logout, tenant switch or role revocation withhold rows; export lock/logout rolls back its success audit.");
    }
    private static void Check(bool value, string message) => IdentityManagementFixture.Assert(value, message);
}
