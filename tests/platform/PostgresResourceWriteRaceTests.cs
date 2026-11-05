using Npgsql;
using System.Net;
using System.Text.Json;
using static AssembleAI.Platform.SmokeTests.IdentityManagementFixture;

namespace AssembleAI.Platform.SmokeTests;

internal static class PostgresResourceWriteRaceTests
{
    public static async Task RunAsync(string connection, NpgsqlConnection observer, OidcFixture oidc,
        string origin, IdentityManagementFixture fixture)
    {
        foreach (var model in new[] { true, false })
        {
            var table = model ? "tenant_models" : "tenant_connectors";
            var audit = model ? "platform_model_configuration_audit" : "platform_connector_configuration_audit";
            var idColumn = model ? "model_id" : "source_id";
            var resource = model ? "assistant" : "factory-db";
            var permission = model ? "model:manage" : "datasource:manage";
            var path = $"/api/platform/v1/tenants/{fixture.Tenant}/{(model ? "models" : "connectors")}/{resource}/configuration";
            // No successful API write is necessary for the revocation races; the final
            // row-lock case uses an explicitly seeded row removed before UI acceptance.
            foreach (var reason in new[] { "logout", "tenant", "permission", "rowlogout", "tenantrowlogout", "auditlogout" })
            {
                var row = reason == "rowlogout";
                if (row)
                    await Execute(observer, model
                        ? "INSERT INTO public.tenant_models(tenant_id,model_id,enabled,max_output_tokens) VALUES($1,$2,true,32)"
                        : "INSERT INTO public.tenant_connectors(tenant_id,source_id,enabled,max_rows,timeout_seconds) VALUES($1,$2,true,10,1)", fixture.Tenant, resource);
                using var session = await Login(origin, fixture.Admin);
                await session.Select(fixture.Tenant);
                await using var blocker = new NpgsqlConnection(connection); await blocker.OpenAsync();
                await using var transaction = await blocker.BeginTransactionAsync();
                var relation = reason == "permission" ? "platform_users" : reason == "auditlogout" ? audit : table;
                if (row)
                    await Execute(blocker, $"SELECT revision FROM public.{table} WHERE tenant_id=$1 AND {idColumn}=$2 FOR UPDATE", fixture.Tenant, resource);
                else if (reason == "tenantrowlogout")
                    await Execute(blocker, "SELECT tenant_id FROM public.platform_tenants WHERE tenant_id=$1 FOR UPDATE", fixture.Tenant);
                else await Execute(blocker, $"LOCK TABLE public.{relation} IN {(reason is "permission" or "auditlogout" ? "SHARE ROW EXCLUSIVE" : "ACCESS EXCLUSIVE")} MODE");
                object body = model ? new { enabled = false, maxOutputTokens = 32, expectedRevision = row ? 1 : 0 }
                    : new { enabled = false, maxRows = 10, timeoutSeconds = 1, expectedRevision = row ? 1 : 0 };
                var pending = session.Status("PUT", path, body);
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
                    while (!await Scalar<bool>(observer,
                        "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid<>$1 AND $1=ANY(pg_blocking_pids(pid)))", blocker.ProcessID))
                        await Task.Delay(10, deadline.Token);
                    if (reason.EndsWith("logout", StringComparison.Ordinal)) await session.Call("POST", "/bff/logout", expected: 204);
                    else if (reason == "tenant") await session.Select(fixture.OtherTenant);
                    else await Execute(blocker, "UPDATE public.role_permission_grants SET enabled=false WHERE tenant_id=$1 AND permission=$2", fixture.Tenant, permission);
                    await transaction.CommitAsync();
                }
                catch { await transaction.RollbackAsync(); throw; }
                var status = await pending;
                IdentityManagementFixture.Assert(status == 403, $"Queued {table} configuration must reject committed {reason} before mutation; expected 403, got {status}.");
                IdentityManagementFixture.Assert(await Scalar<long>(observer, $"SELECT count(*) FROM public.{audit} WHERE tenant_id=$1", fixture.Tenant) == 0,
                    "Revoked resource writes cannot append successful audit records.");
                if (row)
                {
                    IdentityManagementFixture.Assert(await Scalar<long>(observer, $"SELECT revision FROM public.{table} WHERE tenant_id=$1 AND {idColumn}=$2", fixture.Tenant, resource) == 1,
                        "A row-lock wait followed by logout cannot increment the revision.");
                    await Execute(observer, $"DELETE FROM public.{table} WHERE tenant_id=$1 AND {idColumn}=$2", fixture.Tenant, resource);
                }
                else IdentityManagementFixture.Assert(await Scalar<long>(observer, $"SELECT count(*) FROM public.{table} WHERE tenant_id=$1", fixture.Tenant) == 0,
                    "A revoked queued create cannot persist configuration.");
                if (reason == "permission")
                    await Execute(observer, "UPDATE public.role_permission_grants SET enabled=true WHERE tenant_id=$1 AND permission=$2", fixture.Tenant, permission);
            }
        }
        Console.WriteLine("PASS: model/source queued writes recheck real BFF logout, tenant switch and permission revocation after configuration/identity/FK/audit table or row waits; no configuration or audit mutation.");
    }

    private static async Task<IdentityBrowser> Login(string origin, Guid user)
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, CookieContainer = new() })
            { BaseAddress = new(origin), Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            using var challenge = await client.GetAsync("/bff/login?returnUrl=/bff/session");
            IdentityManagementFixture.Assert(challenge.StatusCode == HttpStatusCode.Redirect, "OIDC challenge required.");
            using var code = await client.GetAsync(challenge.Headers.Location!.ToString() + "&__fixture_subject=" + user);
            IdentityManagementFixture.Assert(code.StatusCode == HttpStatusCode.Redirect, "Fixture account choice must issue a code.");
            using var callback = await client.GetAsync(code.Headers.Location);
            IdentityManagementFixture.Assert(callback.StatusCode == HttpStatusCode.Redirect, "Validated callback required.");
            using var csrf = await client.GetAsync("/bff/csrf");
            using var document = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());
            return new IdentityBrowser(client, origin, document.RootElement.GetProperty("requestToken").GetString()!);
        }
        catch { client.Dispose(); throw; }
    }
}
