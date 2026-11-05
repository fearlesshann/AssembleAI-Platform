using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Npgsql;
using static AssembleAI.Platform.SmokeTests.IdentityManagementFixture;

namespace AssembleAI.Platform.SmokeTests;

/// <summary>Built pages, real OIDC/BFF, PostgreSQL and a loopback model protocol fixture.</summary>
public static class PostgresResourceBrowserTests
{
    public static async Task RunAsync(string connectionString, string runDirectory)
    {
        var f = new IdentityManagementFixture();
        var table = "resource_browser_" + Guid.NewGuid().ToString("N");
        var credential = "ASSEMBLEAI_RESOURCE_FIXTURE_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(runDirectory, "test-config.json")));
        var readerConnection = config.RootElement.GetProperty("connectorConnectionString").GetString()!;
        var owner = new NpgsqlConnectionStringBuilder(connectionString);
        var reader = new NpgsqlConnectionStringBuilder(readerConnection);
        IdentityManagementFixture.Assert(reader.Host == owner.Host && reader.Port == owner.Port && reader.Database == owner.Database
            && reader.Username == "assembleai_connector_reader" && reader.Username != owner.Username,
            "Resource browser requires the disposable cluster's distinct read-only role.");
        Environment.SetEnvironmentVariable(credential, readerConnection);
        await using var oidc = await OidcFixture.StartAsync(new Dictionary<string, string>
        {
            [f.Admin.ToString()] = "资源管理员", [f.Member.ToString()] = "仅配置管理员",
            [f.Root.ToString()] = "平台身份管理员", [f.Developer.ToString()] = "普通成员"
        });
        await using var db = new NpgsqlConnection(connectionString); await db.OpenAsync();
        var builder = WebApplication.CreateBuilder(["--environment", "Development", "--urls", "http://127.0.0.1:0"]);
        builder.Logging.ClearProviders();
        await using var provider = builder.Build();
        var upstreamCalls = 0;
        provider.MapPost("/v1/chat/completions", async (HttpContext context) => {
            using var input = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            IdentityManagementFixture.Assert(input.RootElement.GetProperty("model").GetString() == "fixture-model"
                && !input.RootElement.GetProperty("stream").GetBoolean()
                && !context.Request.Headers.ContainsKey("Cookie") && !context.Request.Headers.ContainsKey("Origin"),
                "Resource UI calls must use only the configured model and stripped provider credentials.");
            Interlocked.Increment(ref upstreamCalls);
            await context.Response.WriteAsJsonAsync(new {
                choices = new[] { new { index = 0, message = new { role = "assistant", content = "模型连接正常。" }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 5, completion_tokens = 4, total_tokens = 9 }
            });
        });
        await provider.StartAsync();
        try
        {
            await f.Seed(db, oidc.Authority);
            await Execute(db, "INSERT INTO public.tenant_memberships(tenant_id,user_id) VALUES($1,$2),($1,$3)", f.Tenant, f.Root, f.Developer);
            foreach (var (user, full) in new[] { (f.Admin, true), (f.Member, false) })
            {
                var role = Guid.NewGuid();
                await Execute(db, "INSERT INTO public.tenant_roles(tenant_id,role_id,name) VALUES($1,$2,$3)", f.Tenant, role, full ? "资源操作员" : "仅配置员");
                await Execute(db, "INSERT INTO public.tenant_member_roles(tenant_id,user_id,role_id) VALUES($1,$2,$3)", f.Tenant, user, role);
                foreach (var (permission, type, id) in new[] {
                    ("model:manage", "model", "assistant"), ("datasource:manage", "datasource", "factory-db"),
                    ("model:invoke", "model", "assistant"), ("datasource:test", "datasource", "factory-db"),
                    ("tool:invoke", "tool", "connector.test") })
                {
                    if (!full && !permission.EndsWith(":manage", StringComparison.Ordinal)) continue;
                    await Execute(db, "INSERT INTO public.role_permission_grants(tenant_id,grant_id,role_id,permission,resource_type,resource_id,tenant_wide) VALUES($1,$2,$3,$4,$5,$6,true)",
                        f.Tenant, Guid.NewGuid(), role, permission, type, id);
                }
            }
            await Execute(db, $"CREATE TABLE test_support.{table}(tenant_id uuid NOT NULL,factory_id uuid NOT NULL,line_name text NOT NULL,output_count bigint NOT NULL)");
            await Execute(db, $"INSERT INTO test_support.{table} VALUES($1,$2,'synthetic-line',100)", f.Tenant, Guid.NewGuid());
            await Execute(db, "GRANT USAGE ON SCHEMA test_support TO assembleai_connector_reader");
            await Execute(db, $"GRANT SELECT ON test_support.{table} TO assembleai_connector_reader");
            var origin = FreeOrigin(); var root = Workspace();
            var values = new Dictionary<string, string?> {
                ["ConnectionStrings:Platform"] = connectionString,
                ["Models:Enabled"] = "true", ["Models:CallSeconds"] = "30",
                ["Models:Providers:fixture:Endpoint"] = provider.Urls.Single() + "/v1/chat/completions",
                ["Models:Providers:fixture:AllowInsecureHttp"] = "true",
                ["Models:Catalog:assistant:Provider"] = "fixture", ["Models:Catalog:assistant:UpstreamModel"] = "fixture-model",
                ["Models:Catalog:assistant:DisplayName"] = "验收模型", ["Models:Catalog:assistant:Enabled"] = "true",
                ["Models:Catalog:assistant:MaximumOutputTokens"] = "1024",
                ["Connectors:Enabled"] = "true", ["Connectors:Sources:factory-db:DisplayName"] = "制造测试数据源",
                ["Connectors:Sources:factory-db:ConnectionEnvironmentVariable"] = credential,
                ["Connectors:Sources:factory-db:Enabled"] = "true", ["Connectors:Sources:factory-db:MaximumRows"] = "50",
                ["Connectors:Sources:factory-db:TimeoutSeconds"] = "5",
                ["Connectors:Datasets:daily-production:SourceId"] = "factory-db",
                ["Connectors:Datasets:daily-production:DisplayName"] = "每日产量",
                ["Connectors:Datasets:daily-production:Schema"] = "test_support", ["Connectors:Datasets:daily-production:Table"] = table,
                ["Connectors:Datasets:daily-production:TenantColumn"] = "tenant_id",
                ["Connectors:Datasets:daily-production:OrganizationColumn"] = "factory_id",
                ["Connectors:Datasets:daily-production:Columns:line:Column"] = "line_name",
                ["Connectors:Datasets:daily-production:Columns:line:Type"] = "text",
                ["Connectors:Datasets:daily-production:Columns:output:Column"] = "output_count",
                ["Connectors:Datasets:daily-production:Columns:output:Type"] = "integer"
            };
            await using var app = PlatformApplication.Build([
                "--environment", "Development", "--urls", origin, "--Authentication:Bff:Enabled", "true",
                "--Authentication:Bff:Authority", oidc.Authority, "--Authentication:Bff:ClientId", OidcFixture.ClientId,
                "--Authentication:Bff:ClientSecret", OidcFixture.ClientSecret, "--Authentication:Bff:PublicOrigin", origin,
                "--InternalCalls:Enabled", "false", "--Gateway:Enabled", "false"
            ], b => { b.Logging.ClearProviders(); b.Configuration.AddInMemoryCollection(values); });
            using var files = new PhysicalFileProvider(Path.Combine(root, "apps/portal/dist"));
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
            app.MapGet("/", () => Results.File(Path.Combine(files.Root, "index.html"), "text/html"));
            app.MapGet("/manage/{**path}", () => Results.File(Path.Combine(files.Root, "index.html"), "text/html"));
            await app.StartAsync();
            try
            {
                await PostgresResourceWriteRaceTests.RunAsync(connectionString, db, oidc, origin, f);
                var path = Path.Combine(runDirectory, "resource-browser.json");
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { origin, tenantId = f.Tenant, otherTenantId = f.OtherTenant }));
                var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ASSEMBLEAI_NODE") ?? "node") {
                    WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                start.ArgumentList.Add(Path.Combine(root, "tests/frontend/resource-postgres-browser.mjs")); start.ArgumentList.Add(path);
                foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("ASSEMBLEAI_", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("ConnectionStrings", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Resource browser could not start.");
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(180));
                try { await process.WaitForExitAsync(budget.Token); }
                catch (OperationCanceledException) {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(); throw new InvalidOperationException("Resource browser exceeded its time budget.");
                }
                var transcript = await output + await error;
                Console.Write(transcript.Length > 20000 ? transcript[..20000] : transcript);
                IdentityManagementFixture.Assert(process.ExitCode == 0, "Resource real browser verification failed.");
                IdentityManagementFixture.Assert(upstreamCalls == 1, "Only the authorized, explicitly submitted model test may contact the provider.");
                foreach (var tableName in new[] { "platform_model_calls", "platform_connector_calls" })
                    IdentityManagementFixture.Assert(await Scalar<long>(db, $"SELECT count(*) FROM public.{tableName} WHERE tenant_id=$1 AND user_id=$2 AND outcome='completed'", f.Tenant, f.Admin) >= 1,
                        "Real UI tests must have attributable persisted call records.");
                foreach (var tableName in new[] { "platform_model_configuration_audit", "platform_connector_configuration_audit" })
                    IdentityManagementFixture.Assert(await Scalar<long>(db, $"SELECT count(*) FROM public.{tableName} WHERE tenant_id=$1 AND actor_user_id=$2", f.Tenant, f.Admin) >= 1,
                        "Real UI configuration writes must persist atomic audit records.");
            }
            finally { await app.StopAsync(); }
        }
        finally
        {
            Environment.SetEnvironmentVariable(credential, null);
            await provider.StopAsync();
            await Execute(db, $"DROP TABLE IF EXISTS test_support.{table}");
            foreach (var tableName in new[] { "platform_model_configuration_audit", "platform_model_calls", "platform_connector_configuration_audit", "platform_connector_calls" })
                await Execute(db, $"DELETE FROM public.{tableName} WHERE tenant_id=$1", f.Tenant);
            await f.Cleanup(db);
        }
        Console.WriteLine("PASS: resource management built pages, actual OIDC/BFF/PG configuration/CAS/audit, local model protocol and read-only source test, permissions, themes/mobile and logout.");
    }
}
