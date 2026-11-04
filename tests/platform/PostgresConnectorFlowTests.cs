using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using AssembleAI.Platform.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AssembleAI.Platform.SmokeTests;

/// <summary>Real OIDC, BFF and two distinct database roles in the launcher's disposable cluster.</summary>
public static class PostgresConnectorFlowTests
{
    private const string Source = "factory-db";
    private const string Dataset = "daily-production";
    private const string Injection = "fixture-line' OR TRUE; DROP TABLE forbidden; --";

    public static async Task RunAsync(string connectionString, string runDirectory)
    {
        using var input = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(runDirectory, "test-config.json")));
        var readerConnection = input.RootElement.GetProperty("connectorConnectionString").GetString()!;
        var owner = new NpgsqlConnectionStringBuilder(connectionString);
        var reader = new NpgsqlConnectionStringBuilder(readerConnection);
        Check(reader.Host == owner.Host && reader.Port == owner.Port && reader.Database == owner.Database
            && IPAddress.TryParse(reader.Host, out var address) && IPAddress.IsLoopback(address)
            && reader.Username == "assembleai_connector_reader" && reader.Username != owner.Username,
            "Connector acceptance must use the launcher-owned loopback database and separate read-only role.");
        var f = new Fixture();
        var previous = Environment.GetEnvironmentVariable(f.CredentialVariable);
        Environment.SetEnvironmentVariable(f.CredentialVariable, readerConnection);
        await using var identity = await OidcFixture.StartAsync();
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();
        try
        {
            await Seed(db, f, identity.Authority);
            await CheckReadOnly(readerConnection, f);
            var origin = FreeOrigin();
            await using var platform = PlatformApplication.Build([
                "--environment", "Development", "--urls", origin,
                "--Authentication:Bff:Enabled", "true", "--Authentication:Bff:Authority", identity.Authority,
                "--Authentication:Bff:ClientId", OidcFixture.ClientId,
                "--Authentication:Bff:ClientSecret", OidcFixture.ClientSecret,
                "--Authentication:Bff:PublicOrigin", origin
            ], builder => { builder.Logging.ClearProviders(); builder.Configuration.AddInMemoryCollection(Settings(connectionString, f)); });
            await platform.StartAsync();
            try
            {
                using var browser = await Browser.Login(origin);
                using (var selected = await browser.Send("POST", "/bff/tenant", new { tenantId = f.Tenant })) await Expect(selected, 200);
                var source = $"/api/platform/v1/tenants/{f.Tenant}/connectors/{Source}";
                var configuration = source + "/configuration";
                var query = source + $"/datasets/{Dataset}/query";
                using (var listing = await browser.Send("GET", $"/api/platform/v1/tenants/{f.Tenant}/connectors"))
                {
                    using var json = JsonDocument.Parse(await Expect(listing, 200));
                    var item = json.RootElement.GetProperty("items").EnumerateArray().Single(value => value.GetProperty("sourceId").GetString() == Source);
                    Check(item.GetProperty("canManage").GetBoolean()
                        && item.GetProperty("datasets").EnumerateArray().Any(value => value.GetProperty("datasetId").GetString() == Dataset),
                        "Source and dataset discovery must reflect exact grants.");
                    CheckSafe(json.RootElement.GetRawText(), reader, f);
                }
                using (var unconfigured = await browser.Send("POST", query, Query())) await Expect(unconfigured, 403);
                using (var missingCsrf = await browser.Send("PUT", configuration, Configuration(true, 0), csrf: false)) await Expect(missingCsrf, 403);
                using (var foreign = await browser.Send("PUT", $"/api/platform/v1/tenants/{f.OtherTenant}/connectors/{Source}/configuration", Configuration(true, 0))) await Expect(foreign, 403);
                using (var configured = await browser.Send("PUT", configuration, Configuration(true, 0)))
                {
                    using var json = JsonDocument.Parse(await Expect(configured, 200));
                    Check(json.RootElement.GetProperty("configuration").GetProperty("revision").GetInt64() == 1,
                        "First durable connector configuration must have revision one.");
                }
                using (var stale = await browser.Send("PUT", configuration, Configuration(false, 0))) await Expect(stale, 409);
                using (var invalid = await browser.Send("PUT", configuration, new { enabled = true, maxRows = 50, timeoutSeconds = 3, expectedRevision = 1, connectionString = "forbidden" })) await Expect(invalid, 400);
                using (var tested = await browser.Send("POST", source + "/test", new { }))
                {
                    using var json = JsonDocument.Parse(await Expect(tested, 200));
                    Check(json.RootElement.GetProperty("status").GetString() == "ready", "Read-only source connection test must succeed.");
                }
                await GrantEnabled(db, f, f.TestToolGrant, false);
                using (var denied = await browser.Send("POST", source + "/test", new { })) await Expect(denied, 403);
                await GrantEnabled(db, f, f.TestToolGrant, true);

                var successful = new HashSet<string>(StringComparer.Ordinal);
                using (var result = await browser.Send("POST", query, Query(), spoofedTenant: f.OtherTenant))
                {
                    using var json = JsonDocument.Parse(await Expect(result, 200));
                    var payload = json.RootElement;
                    Check(payload.GetProperty("columns").EnumerateArray().Select(value => value.GetString()).SequenceEqual(["line", "day", "output", "defects"]),
                        "Query columns must expose only ordered deployment aliases.");
                    var rows = payload.GetProperty("rows").EnumerateArray().ToArray();
                    Check(rows.Length == 2 && rows.All(row => row[0].GetString() == "fixture-line-a")
                        && rows.Select(row => row[2].GetInt64()).Order().SequenceEqual([100L, 200L])
                        && rows.Select(row => row[1].GetString()).Order().SequenceEqual(["2026-11-01", "2026-11-02"]),
                        "Real source SQL must enforce tenant and factory intersection before returning rows, ignoring spoofed headers.");
                    successful.Add(payload.GetProperty("requestId").GetString()!);
                }
                using (var aggregate = await browser.Send("POST", query, new {
                    columns = Array.Empty<string>(), filters = Array.Empty<object>(),
                    metrics = new object[] { new { function = "count" }, new { function = "sum", column = "output" },
                        new { function = "avg", column = "defects" }, new { function = "min", column = "output" }, new { function = "max", column = "output" } }, limit = 50 }))
                {
                    using var json = JsonDocument.Parse(await Expect(aggregate, 200));
                    var payload = json.RootElement;
                    Check(payload.GetProperty("columns").EnumerateArray().Select(value => value.GetString()).SequenceEqual(["metric0", "metric1", "metric2", "metric3", "metric4"]),
                        "Metric result aliases must remain stable.");
                    var rows = payload.GetProperty("rows");
                    Check(rows.GetArrayLength() == 1 && rows[0][0].GetInt64() == 2 && rows[0][1].GetDecimal() == 300m
                        && rows[0][2].GetDecimal() == 2.5m && rows[0][3].GetDecimal() == 100m && rows[0][4].GetDecimal() == 200m,
                        "Aggregation must exclude the other factory and foreign tenant before count/sum/avg/min/max.");
                    successful.Add(payload.GetProperty("requestId").GetString()!);
                }
                using (var grouped = await browser.Send("POST", query, new { columns = new[] { "line" }, filters = Array.Empty<object>(), metrics = new[] { new { function = "sum", column = "output" } }, limit = 50 }))
                {
                    using var json = JsonDocument.Parse(await Expect(grouped, 200));
                    Check(json.RootElement.GetProperty("rows").GetArrayLength() == 1
                        && json.RootElement.GetProperty("rows")[0][1].GetDecimal() == 300m,
                        "Grouping must operate only on authorized source rows.");
                }
                using (var filtered = await browser.Send("POST", query, new { columns = new[] { "output" }, filters = new object[] {
                    new { column = "day", @operator = "gte", value = "2026-11-02" },
                    new { column = "output", @operator = "lte", value = 200 } }, metrics = Array.Empty<object>(), limit = 50 }))
                {
                    using var json = JsonDocument.Parse(await Expect(filtered, 200));
                    Check(json.RootElement.GetProperty("rows").GetArrayLength() == 1 && json.RootElement.GetProperty("rows")[0][0].GetInt64() == 200,
                        "Typed date and numeric filters must execute against real PostgreSQL.");
                }
                using (var injection = await browser.Send("POST", query, new { columns = new[] { "output" }, filters = new[] {
                    new { column = "line", @operator = "eq", value = Injection } }, metrics = Array.Empty<object>(), limit = 50 }))
                {
                    using var json = JsonDocument.Parse(await Expect(injection, 200));
                    Check(json.RootElement.GetProperty("rows").GetArrayLength() == 0, "SQL-like filter text must stay a bound value and cannot expand rows.");
                }
                foreach (var invalid in new object[] {
                    new { sql = "SELECT * FROM forbidden" }, new { sql = "UPDATE forbidden SET output=0" },
                    new { columns = new[] { "tenant_id" }, limit = 50 },
                    new { columns = new[] { "output; DROP TABLE forbidden" }, limit = 50 },
                    new { columns = new[] { "output" }, factoryId = f.FactoryB, limit = 50 },
                    new { columns = new[] { "output" }, filters = new[] { new { column = "output", @operator = "sql", value = "1=1" } }, limit = 50 },
                    new { columns = new[] { "output" }, limit = 501 } })
                {
                    using var rejected = await browser.Send("POST", query, invalid);
                    CheckSafe(await Expect(rejected, 400), reader, f);
                }
                using (var unknown = await browser.Send("POST", source + "/datasets/unknown/query", Query())) await Expect(unknown, 403);
                using (var overflow = await browser.Send("POST", query, Query(1))) await Expect(overflow, 413);
                await SourceValueLimits(db, browser, f, query, reader);

                // Source A+B, dataset A and tool B must never be combined into access to either factory.
                await Execute(db, "UPDATE public.grant_organizations SET organization_id=$1 WHERE tenant_id=$2 AND grant_id=$3", f.FactoryB, f.Tenant, f.QueryToolGrant);
                var before = await CountCalls(db, f);
                using (var mismatch = await browser.Send("POST", query, Query())) await Expect(mismatch, 403);
                Check(await CountCalls(db, f) == before, "An empty permission intersection must reject before admitting any source call.");
                await Execute(db, "UPDATE public.grant_organizations SET organization_id=$1 WHERE tenant_id=$2 AND grant_id=$3", f.FactoryA, f.Tenant, f.QueryToolGrant);
                await GrantEnabled(db, f, f.DatasetGrant, false);
                using (var revoked = await browser.Send("POST", query, Query())) await Expect(revoked, 403);
                await GrantEnabled(db, f, f.DatasetGrant, true);

                // A successful connection with a writer must still fail the connector's role guard.
                Environment.SetEnvironmentVariable(f.CredentialVariable, connectionString);
                try { using var writable = await browser.Send("POST", source + "/test", new { }); CheckSafe(await Expect(writable, 503), reader, f); }
                finally { Environment.SetEnvironmentVariable(f.CredentialVariable, readerConnection); }

                await BlockedQuery(connectionString, db, browser, f, query, async () => await GrantEnabled(db, f, f.DatasetGrant, false), 403);
                await GrantEnabled(db, f, f.DatasetGrant, true);
                await BlockedQuery(connectionString, db, browser, f, query, async () => {
                    using var disabled = await browser.Send("PUT", configuration, Configuration(false, 1)); await Expect(disabled, 200);
                }, 403);
                using (var disabled = await browser.Send("POST", query, Query())) await Expect(disabled, 403);
                using (var enabled = await browser.Send("PUT", configuration, Configuration(true, 2))) await Expect(enabled, 200);
                await BlockedQuery(connectionString, db, browser, f, query, () => Task.CompletedTask, 504);
                await CancelBlockedQuery(connectionString, db, browser, f, query);

                // Both writes start from the same durable revision; only one may commit its audit row.
                var concurrent = await Task.WhenAll(browser.Send("PUT", configuration, Configuration(true, 3)), browser.Send("PUT", configuration, Configuration(true, 3)));
                try { Check(concurrent.Select(item => (int)item.StatusCode).Order().SequenceEqual([200, 409]), "Concurrent configuration writes must enforce SQL CAS."); }
                finally { foreach (var response in concurrent) response.Dispose(); }
                using (var calls = await browser.Send("GET", source + "/calls")) CheckSafe(await Expect(calls, 200), reader, f);
                await VerifyAudit(db, f, successful, reader);
                await GrantEnabled(db, f, f.ManageGrant, false);
                using (var denied = await browser.Send("GET", source + "/calls")) await Expect(denied, 403);
                using (var denied = await browser.Send("PUT", configuration, Configuration(false, 4))) await Expect(denied, 403);
                Console.WriteLine("PASS: connectors: real PostgreSQL reader role, OIDC/BFF/CSRF, factory and tenant filtering, aggregate baseline, parameterized filters, permission intersection, live revoke/disable/timeout/disconnect, configuration CAS and metadata-only audit.");
            }
            finally { await platform.StopAsync(); }
        }
        finally
        {
            Environment.SetEnvironmentVariable(f.CredentialVariable, previous);
            await Execute(db, $"DROP TABLE IF EXISTS test_support.{f.Table}");
            await Execute(db, "DELETE FROM public.platform_connector_calls WHERE tenant_id=$1", f.Tenant);
            await Execute(db, "DELETE FROM public.platform_connector_configuration_audit WHERE tenant_id=$1", f.Tenant);
            await Execute(db, "DELETE FROM public.platform_tenants WHERE tenant_id IN ($1,$2)", f.Tenant, f.OtherTenant);
            await Execute(db, "DELETE FROM public.platform_users WHERE user_id=$1", f.User);
        }
    }

    private static async Task Seed(NpgsqlConnection db, Fixture f, string issuer)
    {
        await Execute(db, "INSERT INTO public.platform_users(user_id,issuer,subject,display_name) VALUES($1,$2,$3,'Connector Fixture')", f.User, issuer, OidcFixture.Subject);
        await Execute(db, "INSERT INTO public.platform_tenants(tenant_id,name) VALUES($1,'Connector A'),($2,'Connector B')", f.Tenant, f.OtherTenant);
        await Execute(db, "INSERT INTO public.tenant_memberships(tenant_id,user_id) VALUES($1,$2)", f.Tenant, f.User);
        await Execute(db, "INSERT INTO public.tenant_organizations(tenant_id,organization_id,name,kind) VALUES($1,$2,'Factory A','factory'),($1,$3,'Factory B','factory')", f.Tenant, f.FactoryA, f.FactoryB);
        await Execute(db, "INSERT INTO public.tenant_roles(tenant_id,role_id,name) VALUES($1,$2,'Connector operator')", f.Tenant, f.Role);
        await Execute(db, "INSERT INTO public.tenant_member_roles(tenant_id,user_id,role_id) VALUES($1,$2,$3)", f.Tenant, f.User, f.Role);
        foreach (var (grant, permission, type, id, wide) in new[] {
            (f.ManageGrant,"datasource:manage","datasource",Source,true), (f.TestGrant,"datasource:test","datasource",Source,true),
            (f.TestToolGrant,"tool:invoke","tool","connector.test",true), (f.SourceGrant,"datasource:read","datasource",Source,false),
            (f.DatasetGrant,"dataset:read","dataset",Dataset,false), (f.QueryToolGrant,"tool:invoke","tool","connector.query",false) })
            await Execute(db, "INSERT INTO public.role_permission_grants(tenant_id,grant_id,role_id,permission,resource_type,resource_id,tenant_wide) VALUES($1,$2,$3,$4,$5,$6,$7)",
                f.Tenant, grant, f.Role, permission, type, id, wide);
        foreach (var grant in new[] { f.SourceGrant, f.DatasetGrant, f.QueryToolGrant })
            await Execute(db, "INSERT INTO public.grant_organizations(tenant_id,grant_id,organization_id) VALUES($1,$2,$3)", f.Tenant, grant, f.FactoryA);
        await Execute(db, "INSERT INTO public.grant_organizations(tenant_id,grant_id,organization_id) VALUES($1,$2,$3)", f.Tenant, f.SourceGrant, f.FactoryB);
        await Execute(db, $"CREATE TABLE test_support.{f.Table}(tenant_id uuid NOT NULL,factory_id uuid NOT NULL,line_name text NOT NULL,production_day date NOT NULL,output_count bigint NOT NULL,defect_count numeric NOT NULL)");
        await Execute(db, $"""
            INSERT INTO test_support.{f.Table} VALUES
            ($1,$2,'fixture-line-a','2026-11-01',100,2),($1,$2,'fixture-line-a','2026-11-02',200,3),
            ($1,$3,'fixture-line-b','2026-11-01',900,90),($4,$2,'fixture-foreign','2026-11-01',9999,999)
            """, f.Tenant, f.FactoryA, f.FactoryB, f.OtherTenant);
        await Execute(db, "GRANT USAGE ON SCHEMA test_support TO assembleai_connector_reader");
        await Execute(db, $"GRANT SELECT ON test_support.{f.Table} TO assembleai_connector_reader");
    }

    private static async Task CheckReadOnly(string connection, Fixture f)
    {
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        foreach (var sql in new[] { $"UPDATE test_support.{f.Table} SET output_count=0", $"INSERT INTO test_support.{f.Table} SELECT * FROM test_support.{f.Table}" })
        {
            try { await Execute(db, sql); throw new InvalidOperationException("Fixture reader unexpectedly obtained write permission."); }
            catch (PostgresException error) when (error.SqlState == "42501") { }
        }
    }

    private static async Task SourceValueLimits(NpgsqlConnection db, Browser browser, Fixture f, string path, NpgsqlConnectionStringBuilder reader)
    {
        foreach (var date in new[] { "infinity", "-infinity" })
        {
            await Execute(db, $"UPDATE test_support.{f.Table} SET production_day=$3::date WHERE tenant_id=$1 AND factory_id=$2", f.Tenant, f.FactoryA, date);
            try { using var response = await browser.Send("POST", path, Query()); CheckSafe(await Expect(response, 503), reader, f); }
            finally { await Execute(db, $"UPDATE test_support.{f.Table} SET production_day=CASE output_count WHEN 100 THEN DATE '2026-11-01' ELSE DATE '2026-11-02' END WHERE tenant_id=$1 AND factory_id=$2", f.Tenant, f.FactoryA); }
        }
        foreach (var date in new[] { "0001-01-01", "9999-12-31" })
        {
            // Both literals are fixed fixture constants. Do not seed through DateOnly parameters:
            // the driver's special infinity mapping would otherwise invalidate this regression.
            await Execute(db, $"UPDATE test_support.{f.Table} SET production_day=DATE '{date}' WHERE tenant_id=$1 AND factory_id=$2", f.Tenant, f.FactoryA);
            try
            {
                foreach (var query in new object[] {
                    new { columns = new[] { "day" }, limit = 50 },
                    new { columns = new[] { "day" }, filters = new[] { new { column = "day", @operator = "eq", value = date } }, limit = 50 } })
                {
                    using var response = await browser.Send("POST", path, query);
                    using var body = JsonDocument.Parse(await Expect(response, 200));
                    var rows = body.RootElement.GetProperty("rows");
                    Check(rows.GetArrayLength() == 2 && rows.EnumerateArray().All(row => row[0].GetString() == date),
                        "Finite minimum and maximum dates must retain their original values in projection and equality filtering.");
                }
            }
            finally { await Execute(db, $"UPDATE test_support.{f.Table} SET production_day=CASE output_count WHEN 100 THEN DATE '2026-11-01' ELSE DATE '2026-11-02' END WHERE tenant_id=$1 AND factory_id=$2", f.Tenant, f.FactoryA); }
        }
        await Execute(db, $"UPDATE test_support.{f.Table} SET defect_count='NaN'::numeric WHERE tenant_id=$1 AND factory_id=$2", f.Tenant, f.FactoryA);
        try { using var response = await browser.Send("POST", path, Query()); CheckSafe(await Expect(response, 503), reader, f); }
        finally { await Execute(db, $"UPDATE test_support.{f.Table} SET defect_count=CASE output_count WHEN 100 THEN 2 ELSE 3 END WHERE tenant_id=$1 AND factory_id=$2", f.Tenant, f.FactoryA); }
        foreach (var number in new[] { "1e-100", "0.12345678901234567890123456789123" })
        {
            // Fixed SQL numeric literals preserve precision beyond System.Decimal in the source fixture.
            await Execute(db, $"UPDATE test_support.{f.Table} SET defect_count={number} WHERE tenant_id=$1 AND factory_id=$2", f.Tenant, f.FactoryA);
            try { using var response = await browser.Send("POST", path, Query()); CheckSafe(await Expect(response, 503), reader, f); }
            finally { await Execute(db, $"UPDATE test_support.{f.Table} SET defect_count=CASE output_count WHEN 100 THEN 2 ELSE 3 END WHERE tenant_id=$1 AND factory_id=$2", f.Tenant, f.FactoryA); }
        }

        await Execute(db, $"UPDATE test_support.{f.Table} SET line_name=$3 WHERE tenant_id=$1 AND factory_id=$2", f.Tenant, f.FactoryA, new string('界', 1366));
        try { using var response = await browser.Send("POST", path, Query()); await Expect(response, 413); }
        finally { await Execute(db, $"UPDATE test_support.{f.Table} SET line_name='fixture-line-a' WHERE tenant_id=$1 AND factory_id=$2", f.Tenant, f.FactoryA); }

        // Each cell is exactly 4 KiB, but JSON escaping expands it sixfold. Row count stays below 50.
        await Execute(db, $"INSERT INTO test_support.{f.Table} SELECT $1,$2,$3,'2026-11-03'::date,-1,0 FROM pg_catalog.generate_series(1,48)",
            f.Tenant, f.FactoryA, new string('\u0001', 4096));
        try
        {
            using var response = await browser.Send("POST", path, new { columns = new[] { "line" }, limit = 50 });
            await Expect(response, 413);
        }
        finally { await Execute(db, $"DELETE FROM test_support.{f.Table} WHERE tenant_id=$1 AND output_count=-1", f.Tenant); }

        await Execute(db, $"GRANT UPDATE(output_count) ON test_support.{f.Table} TO assembleai_connector_reader");
        try { using var response = await browser.Send("POST", path, Query()); CheckSafe(await Expect(response, 503), reader, f); }
        finally { await Execute(db, $"REVOKE UPDATE(output_count) ON test_support.{f.Table} FROM assembleai_connector_reader"); }
    }

    private static async Task BlockedQuery(string connection, NpgsqlConnection observer, Browser browser, Fixture f, string path, Func<Task> afterStarted, int expected)
    {
        await using var blocker = new NpgsqlConnection(connection); await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand($"LOCK TABLE test_support.{f.Table} IN ACCESS EXCLUSIVE MODE", blocker, transaction)) await command.ExecuteNonQueryAsync();
        var pending = browser.Send("POST", path, Query());
        try
        {
            await WaitForLock(observer, f, pending);
            await afterStarted();
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(8));
            await Expect(response, expected);
        }
        finally
        {
            await transaction.RollbackAsync();
            if (!pending.IsCompleted) { using var ignored = await pending.WaitAsync(TimeSpan.FromSeconds(8)); }
        }
    }

    private static async Task CancelBlockedQuery(string connection, NpgsqlConnection observer, Browser browser, Fixture f, string path)
    {
        await using var blocker = new NpgsqlConnection(connection); await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand($"LOCK TABLE test_support.{f.Table} IN ACCESS EXCLUSIVE MODE", blocker, transaction)) await command.ExecuteNonQueryAsync();
        using var cancellation = new CancellationTokenSource();
        var pending = browser.Send("POST", path, Query(), cancellationToken: cancellation.Token);
        try
        {
            await WaitForLock(observer, f, pending);
            cancellation.Cancel();
            try { using var unexpected = await pending; throw new InvalidOperationException("Cancelled caller unexpectedly received source results."); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            var cancelled = false;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                await using var command = new NpgsqlCommand("""
                    SELECT NOT EXISTS(SELECT 1 FROM pg_catalog.pg_locks WHERE relation=to_regclass($1) AND NOT granted)
                       AND EXISTS(SELECT 1 FROM public.platform_connector_calls WHERE tenant_id=$2 AND outcome='cancelled' AND finished_at IS NOT NULL)
                    """, observer);
                command.Parameters.AddWithValue("test_support." + f.Table); command.Parameters.AddWithValue(f.Tenant);
                if (await command.ExecuteScalarAsync() is true) { cancelled = true; break; }
                await Task.Delay(50);
            }
            Check(cancelled, "Caller cancellation must release the actual PostgreSQL wait and persist a cancelled audit before the source lock is released.");
        }
        finally { cancellation.Cancel(); await transaction.RollbackAsync(); }
    }

    private static async Task WaitForLock(NpgsqlConnection observer, Fixture f, Task pending)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_locks WHERE relation=to_regclass($1) AND NOT granted)", observer);
            command.Parameters.AddWithValue("test_support." + f.Table);
            if (await command.ExecuteScalarAsync() is true) return;
            if (pending.IsCompleted) break;
            await Task.Delay(50);
        }
        throw new InvalidOperationException("Live invalidation acceptance must first observe the real source query waiting on its table lock.");
    }

    private static async Task VerifyAudit(NpgsqlConnection db, Fixture f, IReadOnlySet<string> successful, NpgsqlConnectionStringBuilder reader)
    {
        await using var command = new NpgsqlCommand("SELECT request_id,user_id,source_id,dataset_id,operation,outcome,row_count,finished_at,elapsed_milliseconds FROM public.platform_connector_calls WHERE tenant_id=$1", db);
        command.Parameters.AddWithValue(f.Tenant);
        var seen = new HashSet<string>(); var outcomes = new HashSet<string>();
        await using (var rows = await command.ExecuteReaderAsync())
        {
            while (await rows.ReadAsync())
            {
                var request = rows.GetString(0); var operation = rows.GetString(4); var outcome = rows.GetString(5);
                Check(rows.GetGuid(1) == f.User && rows.GetString(2) == Source && (operation == "test" ? rows.IsDBNull(3) : rows.GetString(3) == Dataset)
                    && !rows.IsDBNull(7) && rows.GetInt64(8) >= 0 && outcome != "running", "Every admitted call must retain complete source/subject/operation/terminal metadata.");
                if (successful.Contains(request)) Check(outcome == "completed" && rows.GetInt32(6) > 0, "Returned query data requires a committed successful audit row.");
                seen.Add(request); outcomes.Add(outcome);
            }
        }
        Check(successful.All(seen.Contains) && outcomes.IsSupersetOf(["completed", "revoked", "timeout", "cancelled", "upstream_error"]),
            "Durable audit must distinguish successful, revoked, timed out and refused-writer source calls.");
        await using var audit = new NpgsqlCommand("SELECT count(*),max(revision) FROM public.platform_connector_configuration_audit WHERE tenant_id=$1 AND source_id=$2", db);
        audit.Parameters.AddWithValue(f.Tenant); audit.Parameters.AddWithValue(Source);
        await using (var rows = await audit.ExecuteReaderAsync())
        { Check(await rows.ReadAsync() && rows.GetInt64(0) == 4 && rows.GetInt64(1) == 4, "Only successful CAS changes may append configuration audit revisions."); }
        await using var raw = new NpgsqlCommand("SELECT row_to_json(c)::text FROM public.platform_connector_calls c WHERE tenant_id=$1 UNION ALL SELECT row_to_json(a)::text FROM public.platform_connector_configuration_audit a WHERE tenant_id=$1", db);
        raw.Parameters.AddWithValue(f.Tenant);
        await using var records = await raw.ExecuteReaderAsync();
        while (await records.ReadAsync())
        {
            var json = records.GetString(0); CheckSafe(json, reader, f);
            Check(!json.Contains("fixture-line", StringComparison.Ordinal) && !json.Contains("fixture-foreign", StringComparison.Ordinal)
                && !json.Contains(Injection, StringComparison.Ordinal), "Durable connector audit cannot retain source rows or caller filter values.");
        }
    }

    private static Dictionary<string,string?> Settings(string connection, Fixture f)
    {
        var result = new Dictionary<string,string?> {
            ["ConnectionStrings:Platform"] = connection, ["Connectors:Enabled"] = "true",
            [$"Connectors:Sources:{Source}:DisplayName"] = "Manufacturing fixture",
            [$"Connectors:Sources:{Source}:ConnectionEnvironmentVariable"] = f.CredentialVariable,
            [$"Connectors:Sources:{Source}:Enabled"] = "true", [$"Connectors:Sources:{Source}:MaximumRows"] = "50",
            [$"Connectors:Sources:{Source}:TimeoutSeconds"] = "5",
            [$"Connectors:Datasets:{Dataset}:SourceId"] = Source, [$"Connectors:Datasets:{Dataset}:DisplayName"] = "Daily production fixture",
            [$"Connectors:Datasets:{Dataset}:Schema"] = "test_support", [$"Connectors:Datasets:{Dataset}:Table"] = f.Table,
            [$"Connectors:Datasets:{Dataset}:TenantColumn"] = "tenant_id", [$"Connectors:Datasets:{Dataset}:OrganizationColumn"] = "factory_id"
        };
        foreach (var (alias, physical, type) in new[] { ("line","line_name","text"), ("day","production_day","date"), ("output","output_count","integer"), ("defects","defect_count","decimal") })
        { result[$"Connectors:Datasets:{Dataset}:Columns:{alias}:Column"] = physical; result[$"Connectors:Datasets:{Dataset}:Columns:{alias}:Type"] = type; }
        return result;
    }
    private static object Query(int limit = 50) => new { columns = new[] { "line", "day", "output", "defects" }, filters = Array.Empty<object>(), metrics = Array.Empty<object>(), limit };
    private static object Configuration(bool enabled, int revision) => new { enabled, maxRows = 50, timeoutSeconds = 3, expectedRevision = revision };
    private static Task GrantEnabled(NpgsqlConnection db, Fixture f, Guid grant, bool enabled) => Execute(db, "UPDATE public.role_permission_grants SET enabled=$1 WHERE tenant_id=$2 AND grant_id=$3", enabled, f.Tenant, grant);
    private static async Task<long> CountCalls(NpgsqlConnection db, Fixture f) { await using var command = new NpgsqlCommand("SELECT count(*) FROM public.platform_connector_calls WHERE tenant_id=$1", db); command.Parameters.AddWithValue(f.Tenant); return (long)(await command.ExecuteScalarAsync())!; }
    private static void CheckSafe(string body, NpgsqlConnectionStringBuilder reader, Fixture f) => Check(!body.Contains(f.CredentialVariable, StringComparison.Ordinal)
        && !body.Contains(f.Table, StringComparison.Ordinal) && !body.Contains(reader.Password!, StringComparison.Ordinal)
        && !body.Contains("line_name", StringComparison.Ordinal) && !body.Contains("Host=", StringComparison.OrdinalIgnoreCase),
        "Public connector responses and audits must not disclose credentials, environment references, physical identifiers or SQL.");
    private static async Task<string> Expect(HttpResponseMessage response, int status)
    { Check((int)response.StatusCode == status, $"Connector HTTP expected {status}, received {(int)response.StatusCode}."); return await response.Content.ReadAsStringAsync(); }
    private static async Task Execute(NpgsqlConnection db, string sql, params object[] values) { await using var command = new NpgsqlCommand(sql, db); foreach (var value in values) command.Parameters.AddWithValue(value); await command.ExecuteNonQueryAsync(); }
    private static string FreeOrigin() { using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); return "http://127.0.0.1:" + ((IPEndPoint)socket.LocalEndpoint).Port; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Fixture
    {
        public string Table { get; } = "connector_flow_" + Guid.NewGuid().ToString("N");
        public string CredentialVariable { get; } = "ASSEMBLEAI_CONNECTOR_FIXTURE_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        public Guid Tenant { get; } = Guid.NewGuid(); public Guid OtherTenant { get; } = Guid.NewGuid();
        public Guid User { get; } = Guid.NewGuid(); public Guid Role { get; } = Guid.NewGuid();
        public Guid FactoryA { get; } = Guid.NewGuid(); public Guid FactoryB { get; } = Guid.NewGuid();
        public Guid ManageGrant { get; } = Guid.NewGuid(); public Guid TestGrant { get; } = Guid.NewGuid();
        public Guid TestToolGrant { get; } = Guid.NewGuid(); public Guid SourceGrant { get; } = Guid.NewGuid();
        public Guid DatasetGrant { get; } = Guid.NewGuid(); public Guid QueryToolGrant { get; } = Guid.NewGuid();
    }
    private sealed class Browser(HttpClient client, string origin, string token) : IDisposable
    {
        public static async Task<Browser> Login(string origin)
        {
            var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, CookieContainer = new() }) { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(20) };
            try
            {
                using var challenge = await client.GetAsync("/bff/login?returnUrl=/bff/session");
                Check(challenge.StatusCode == HttpStatusCode.Redirect, "Real OIDC challenge is required.");
                using var code = await client.GetAsync(challenge.Headers.Location);
                using var callback = await client.GetAsync(code.Headers.Location);
                Check(callback.StatusCode == HttpStatusCode.Redirect, "Real OIDC code exchange must complete.");
                using var csrf = await client.GetAsync("/bff/csrf");
                using var json = JsonDocument.Parse(await Expect(csrf, 200));
                return new(client, origin, json.RootElement.GetProperty("requestToken").GetString()!);
            }
            catch { client.Dispose(); throw; }
        }
        public async Task<HttpResponseMessage> Send(string method, string path, object? body = null, bool csrf = true, Guid? spoofedTenant = null, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (body is not null) request.Content = JsonContent.Create(body);
            if (method is "POST" or "PUT") { request.Headers.Add("Origin", origin); if (csrf) request.Headers.Add(BffEndpoints.CsrfHeaderName, token); }
            if (spoofedTenant is { } tenant) { request.Headers.Add("X-Tenant-ID", tenant.ToString()); request.Headers.Add("X-Organization-ID", Guid.NewGuid().ToString()); }
            return await client.SendAsync(request, cancellationToken);
        }
        public void Dispose() => client.Dispose();
    }
}
