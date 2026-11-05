using AssembleAI.Platform.Connectors;
using AssembleAI.Platform.Models;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace AssembleAI.Platform.SmokeTests;

// Only invoked after the isolated PostgreSQL dispatcher verifies its instance marker.
public static class PostgresConnectorStoreTests
{
    public static async Task RunAsync(string connectionString)
    {
        var tenant = Guid.NewGuid(); var otherTenant = Guid.NewGuid(); var user = Guid.NewGuid();
        var source = "source-" + Id(); var otherSource = "source-" + Id(); var dataset = "dataset-" + Id();
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:Platform"] = connectionString }).Build();
        var store = new PostgresConnectorStore(settings);
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();
        try
        {
            await Execute(db, "INSERT INTO public.platform_users(user_id,issuer,subject,display_name) VALUES($1,'https://connector-fixture.example',$2,'Connector fixture')", user, user.ToString());
            await Execute(db, "INSERT INTO public.platform_tenants(tenant_id,name) VALUES($1,'Connector A'),($2,'Connector B')", tenant, otherTenant);
            await Execute(db, "INSERT INTO public.tenant_memberships(tenant_id,user_id) VALUES($1,$3),($2,$3)", tenant, otherTenant, user);
            Assert(await store.ReadConfigurationAsync(tenant, source, default) is null, "Missing source configuration must not imply enablement.");
            var request = Id();
            var first = await store.SaveConfigurationAsync(tenant, source, true, 100, 10, 0, user, request, default);
            Assert(first == new TenantConnectorConfiguration(tenant, source, true, 100, 10, 1), "Create must persist revision one and exact limits.");
            Assert(await new PostgresConnectorStore(settings).ReadConfigurationAsync(tenant, source, default) == first, "Configuration must survive store recreation.");
            Assert(await store.ReadConfigurationAsync(otherTenant, source, default) is null, "Source configuration must remain tenant scoped.");
            Assert(await Scalar(db, "SELECT count(*) FROM public.platform_connector_configuration_audit WHERE actor_user_id=$1 AND tenant_id=$2 AND source_id=$3 AND request_id=$4 AND revision=1 AND enabled AND max_rows=100 AND timeout_seconds=10", user, tenant, source, request) is 1L,
                "Configuration audit must retain the exact actor, source, request and applied limits.");
            Assert(await store.SaveConfigurationAsync(tenant, source, false, 50, 5, 0, user, Id(), default) is null, "Create cannot overwrite an existing source.");
            Assert(await store.SaveConfigurationAsync(tenant, otherSource, true, 100, 10, 1, user, Id(), default) is null, "Update cannot create a missing source.");
            var created = await Task.WhenAll(store.SaveConfigurationAsync(tenant, otherSource, true, 100, 10, 0, user, Id(), default),
                store.SaveConfigurationAsync(tenant, otherSource, true, 50, 5, 0, user, Id(), default));
            Assert(created.Count(item => item is not null) == 1, "Concurrent create must have exactly one winner.");
            var edited = await Task.WhenAll(store.SaveConfigurationAsync(tenant, source, true, 50, 5, 1, user, Id(), default),
                store.SaveConfigurationAsync(tenant, source, true, 80, 8, 1, user, Id(), default));
            Assert(edited.Count(item => item is not null) == 1 && edited.Single(item => item is not null)!.Revision == 2, "CAS must prevent lost updates.");
            Assert(await Scalar(db, "SELECT count(*) FROM public.platform_connector_configuration_audit WHERE tenant_id=$1 AND source_id=$2", tenant, source) is 2L, "Conflicts must not create successful audit records.");
            await AuditFailures(db, store, tenant, user, source, dataset);
            await VerifyQueuedSaveRechecksAccess(connectionString, db, store, tenant, user, source);

            var callRequest = Id();
            var call = await store.BeginCallAsync(tenant, user, source, dataset, "query", callRequest, default);
            var pending = (await store.ListCallsAsync(tenant, source, default)).Single(row => row.CallId == call);
            Assert(pending.UserId == user && pending.TenantId == tenant && pending.SourceId == source && pending.DatasetId == dataset
                && pending.Operation == "query" && pending.RequestId == callRequest && pending.Outcome == "running" && pending.RowCount is null,
                "Admission must persist attributed metadata without inventing a result count.");
            Assert(pending.Source is null, "Direct BFF calls must retain an empty application source.");
            var origin = new ModelCallSource("chatbi", "1.0.0", Guid.NewGuid(), Id());
            var delegated = await store.BeginCallAsync(tenant, user, source, dataset, "query", Id(), default, origin);
            Assert((await store.ListCallsAsync(tenant, source, default)).Single(row => row.CallId == delegated).Source == origin,
                "Delegated calls must persist and reconstruct the exact trusted application/run source.");
            await store.CompleteCallAsync(delegated, "completed", 1, 1, default);
            foreach (var invalidSource in new[] { origin with { AppId = "INVALID" }, origin with { ReleaseVersion = "../1" },
                origin with { RunId = Guid.Empty }, origin with { GatewayRequestId = "invalid" } })
                await Unavailable(() => store.BeginCallAsync(tenant, user, source, dataset, "query", Id(), default, invalidSource));
            try { await Execute(db, "UPDATE public.platform_connector_calls SET app_id='chatbi' WHERE call_id=$1", call); throw new InvalidOperationException("Partial source must fail SQL CHECK."); }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.CheckViolation) { }
            Assert((await store.ListCallsAsync(otherTenant, source, default)).Count == 0 && (await store.ListCallsAsync(tenant, otherSource, default)).Count == 0,
                "Call history must be scoped by both tenant and source.");
            await store.CompleteCallAsync(call, "completed", 0, 17, default);
            var done = (await store.ListCallsAsync(tenant, source, default)).Single(row => row.CallId == call);
            Assert(done is { Outcome: "completed", RowCount: 0, ElapsedMilliseconds: 17 }, "A completed empty query must retain an explicit zero.");
            await Unavailable(() => store.CompleteCallAsync(call, "failed", null, 99, default));
            Assert((await store.ListCallsAsync(tenant, source, default)).Single(row => row.CallId == call) == done, "Terminal records cannot be overwritten.");
            foreach (var outcome in new[] { "cancelled", "revoked", "timeout", "upstream_error", "failed" })
            {
                var id = await store.BeginCallAsync(tenant, user, source, dataset, "query", Id(), default);
                await store.CompleteCallAsync(id, outcome, null, 1, default);
                Assert((await store.ListCallsAsync(tenant, source, default)).Single(row => row.CallId == id).Outcome == outcome, "Safe outcomes must persist without result values.");
            }
            var test = await store.BeginCallAsync(tenant, user, source, null, "test", Id(), default);
            await Unavailable(() => store.CompleteCallAsync(test, "completed", 0, 0, default));
            await store.CompleteCallAsync(test, "completed", null, 0, default);
            Assert((await store.ListCallsAsync(tenant, source, default)).Single(row => row.CallId == test) is { DatasetId: null, RowCount: null }, "Connectivity checks must not claim dataset rows.");
            var invalid = await store.BeginCallAsync(tenant, user, source, dataset, "query", Id(), default);
            await Unavailable(() => store.CompleteCallAsync(invalid, "completed", null, 1, default));
            await Unavailable(() => store.CompleteCallAsync(invalid, "completed", -1, 1, default));
            await Unavailable(() => store.CompleteCallAsync(invalid, "failed", 1, 1, default));
            await Unavailable(() => store.CompleteCallAsync(invalid, "untrusted database detail", null, 1, default));
            await Unavailable(() => store.CompleteCallAsync(invalid, "failed", null, -1, default));
            Assert((await store.ListCallsAsync(tenant, source, default)).Single(row => row.CallId == invalid).Outcome == "running", "Invalid completion must preserve pending state.");
            await store.CompleteCallAsync(invalid, "completed", 5, 8, default);
            await Unavailable(() => store.BeginCallAsync(tenant, user, source, dataset, "query", callRequest, default));
            await Unavailable(() => store.BeginCallAsync(tenant, user, source, null, "query", Id(), default));
            await Unavailable(() => store.BeginCallAsync(tenant, user, source, dataset, "test", Id(), default));
            await Unavailable(() => store.BeginCallAsync(tenant, user, source, dataset, "write", Id(), default));
            await Unavailable(() => store.BeginCallAsync(tenant, user, source, "invalid/dataset", "query", Id(), default));
            await Unavailable(() => store.BeginCallAsync(tenant, Guid.NewGuid(), source, dataset, "query", Id(), default));
            await Unavailable(() => store.BeginCallAsync(otherTenant, user, source, dataset, "query", Id(), default));
            foreach (var (rows, seconds, revision) in new[] { (0, 1, 2L), (501, 1, 2L), (1, 0, 2L), (1, 31, 2L), (1, 1, -1L), (1, 1, long.MaxValue) })
                await Unavailable(() => store.SaveConfigurationAsync(tenant, source, true, rows, seconds, revision, user, Id(), default));
            foreach (var badSource in new[] { "", "A", "a/b", "a' OR true", new string('a', 65) })
            {
                await Unavailable(() => store.ReadConfigurationAsync(tenant, badSource, default));
                await Unavailable(() => store.ListCallsAsync(tenant, badSource, default));
            }
            await Unavailable(() => store.ReadConfigurationAsync(Guid.Empty, source, default));
            await Unavailable(() => store.SaveConfigurationAsync(tenant, source, true, 1, 1, 2, user, "bad", default));
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try { await store.ReadConfigurationAsync(tenant, source, cancelled.Token); throw new InvalidOperationException("Expected caller cancellation."); }
                catch (OperationCanceledException) { }
            }
            await Preconditions(db, store, tenant, user, source, dataset);
            for (var index = 0; index < 52; index++)
            {
                var id = await store.BeginCallAsync(tenant, user, source, null, "test", Id(), default);
                await store.CompleteCallAsync(id, "completed", null, index, default);
            }
            var latest = await store.ListCallsAsync(tenant, source, default);
            Assert(latest.Count == 50 && latest[0].ElapsedMilliseconds == 51 && latest[^1].ElapsedMilliseconds == 2,
                "History returns precisely the most recent fifty records.");
            await SqlConstraints(db, tenant, source);
            var broken = new PostgresConnectorStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Platform"] = "Host=private-connector-secret;InvalidSetting=private-connector-secret" }).Build());
            await Unavailable(() => broken.ReadConfigurationAsync(tenant, source, default));
            Console.WriteLine("PASS: PostgreSQL connector store: scoped revisioned policies, attributed atomic audit, active admission, strict query/test metadata, terminal immutability and bounded history.");
        }
        finally
        {
            await Execute(db, "DELETE FROM public.platform_connector_calls WHERE tenant_id IN ($1,$2)", tenant, otherTenant);
            await Execute(db, "DELETE FROM public.platform_connector_configuration_audit WHERE tenant_id IN ($1,$2)", tenant, otherTenant);
            await Execute(db, "DELETE FROM public.platform_tenants WHERE tenant_id IN ($1,$2)", tenant, otherTenant);
            await Execute(db, "DELETE FROM public.platform_users WHERE user_id=$1", user);
        }
    }

    private static async Task Preconditions(NpgsqlConnection db, PostgresConnectorStore store, Guid tenant, Guid user, string source, string dataset)
    {
        foreach (var (sql, values) in new (string, object[])[] {
            ("UPDATE public.platform_users SET enabled=false WHERE user_id=$1", [user]),
            ("UPDATE public.platform_tenants SET enabled=false WHERE tenant_id=$1", [tenant]),
            ("UPDATE public.tenant_memberships SET enabled=false WHERE user_id=$1 AND tenant_id=$2", [user,tenant]),
            ("UPDATE public.tenant_connectors SET enabled=false WHERE tenant_id=$1 AND source_id=$2", [tenant,source]) })
        {
            await Execute(db, sql, values);
            try
            {
                await Unavailable(() => store.BeginCallAsync(tenant, user, source, dataset, "query", Id(), default));
                if (!sql.Contains("tenant_connectors", StringComparison.Ordinal))
                    await Unavailable(() => store.SaveConfigurationAsync(tenant, source, true, 100, 10, 2, user, Id(), default));
            }
            finally
            {
                await Execute(db, sql.Replace("enabled=false", "enabled=true", StringComparison.Ordinal), values);
            }
        }
    }

    private static async Task AuditFailures(NpgsqlConnection db, PostgresConnectorStore store, Guid tenant, Guid user, string source, string dataset)
    {
        await Execute(db, "CREATE FUNCTION test_support.fail_connector_write() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private-connector-secret'; END $$");
        try
        {
            await Execute(db, "CREATE TRIGGER fixture_connector_config BEFORE INSERT ON public.platform_connector_configuration_audit FOR EACH ROW EXECUTE FUNCTION test_support.fail_connector_write()");
            try
            {
                var before = await store.ReadConfigurationAsync(tenant, source, default);
                await Unavailable(() => store.SaveConfigurationAsync(tenant, source, false, 2, 1, 2, user, Id(), default));
                Assert(await store.ReadConfigurationAsync(tenant, source, default) == before, "Failed configuration audit must roll back the configuration mutation.");
            }
            finally { await Execute(db, "DROP TRIGGER fixture_connector_config ON public.platform_connector_configuration_audit"); }
            var pending = await store.BeginCallAsync(tenant, user, source, dataset, "query", Id(), default);
            await Execute(db, "CREATE TRIGGER fixture_connector_calls BEFORE INSERT OR UPDATE ON public.platform_connector_calls FOR EACH ROW EXECUTE FUNCTION test_support.fail_connector_write()");
            try
            {
                var request = Id();
                await Unavailable(() => store.BeginCallAsync(tenant, user, source, dataset, "query", request, default));
                Assert(await Scalar(db, "SELECT count(*) FROM public.platform_connector_calls WHERE request_id=$1", request) is 0L, "Failed admission cannot leave a running record.");
                await Unavailable(() => store.CompleteCallAsync(pending, "completed", 1, 1, default));
                Assert((await store.ListCallsAsync(tenant, source, default)).Single(row => row.CallId == pending).Outcome == "running", "Failed completion preserves unfinished audit.");
            }
            finally { await Execute(db, "DROP TRIGGER fixture_connector_calls ON public.platform_connector_calls"); }
        }
        finally { await Execute(db, "DROP FUNCTION test_support.fail_connector_write()"); }
    }

    private static async Task SqlConstraints(NpgsqlConnection db, Guid tenant, string source)
    {
        foreach (var sql in new[] {
            "UPDATE public.tenant_connectors SET max_rows=501 WHERE tenant_id=$1 AND source_id=$2",
            "UPDATE public.tenant_connectors SET timeout_seconds=0 WHERE tenant_id=$1 AND source_id=$2",
            "UPDATE public.platform_connector_calls SET dataset_id=NULL WHERE tenant_id=$1 AND source_id=$2 AND operation='query'",
            "UPDATE public.platform_connector_calls SET row_count=0 WHERE tenant_id=$1 AND source_id=$2 AND operation='test'",
            "UPDATE public.platform_connector_calls SET outcome='other' WHERE tenant_id=$1 AND source_id=$2" })
        {
            try { await Execute(db, sql, tenant, source); throw new InvalidOperationException("Expected PostgreSQL CHECK rejection."); }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.CheckViolation) { }
        }
    }
    private static async Task VerifyQueuedSaveRechecksAccess(string connection, NpgsqlConnection db, IConnectorStore store, Guid tenant, Guid user, string source)
    {
        var before = await store.ReadConfigurationAsync(tenant, source, default);
        var audited = await Scalar(db, "SELECT count(*) FROM public.platform_connector_configuration_audit WHERE tenant_id=$1 AND source_id=$2", tenant, source);
        await using var blocker = new NpgsqlConnection(connection);
        await blocker.OpenAsync();
        await using var tx = await blocker.BeginTransactionAsync();
        await Execute(blocker, "LOCK TABLE public.tenant_connectors IN ACCESS EXCLUSIVE MODE");
        var allowed = true; var checks = 0;
        var save = store.SaveConfigurationAsync(tenant, source, false, 10, 1, before!.Revision, user, Id(), default,
            _ => { Interlocked.Increment(ref checks); return Task.FromResult(allowed); });
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!(bool)(await Scalar(db, "SELECT EXISTS(SELECT 1 FROM pg_locks WHERE relation='public.tenant_connectors'::regclass AND NOT granted)"))!)
            await Task.Delay(10, wait.Token);
        Assert(checks == 0, "Connector live-access check must follow lock acquisition, not precede the database wait.");
        allowed = false;
        await tx.RollbackAsync();
        try { await save; throw new InvalidOperationException("A connector save queued before revocation must be denied."); }
        catch (ConnectorConfigurationAccessDeniedException) { }
        Assert(checks == 1 && await store.ReadConfigurationAsync(tenant, source, default) == before
            && Equals(audited, await Scalar(db, "SELECT count(*) FROM public.platform_connector_configuration_audit WHERE tenant_id=$1 AND source_id=$2", tenant, source)),
            "Revoked queued connector save cannot mutate configuration or append successful audit.");
    }

    private static string Id() => Guid.NewGuid().ToString("N");
    private static async Task Unavailable(Func<Task> action)
    {
        try { await action(); throw new InvalidOperationException("Expected safe connector storage failure."); }
        catch (ConnectorUnavailableException error)
        { Assert(error.InnerException is null && !error.ToString().Contains("private-connector-secret", StringComparison.Ordinal), "Storage failures must not expose credentials or SQL diagnostics."); }
    }
    private static async Task Execute(NpgsqlConnection db, string sql, params object[] values)
    { await using var command = new NpgsqlCommand(sql, db); foreach (var value in values) command.Parameters.AddWithValue(value); await command.ExecuteNonQueryAsync(); }
    private static async Task<object?> Scalar(NpgsqlConnection db, string sql, params object[] values)
    { await using var command = new NpgsqlCommand(sql, db); foreach (var value in values) command.Parameters.AddWithValue(value); return await command.ExecuteScalarAsync(); }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
