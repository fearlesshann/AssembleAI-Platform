using AssembleAI.Platform.Models;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace AssembleAI.Platform.SmokeTests;

// Called only after the isolated PostgreSQL dispatcher checks its instance marker.
public static class PostgresModelTests
{
    public static async Task RunAsync(string connectionString)
    {
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        var model = "model-" + Guid.NewGuid().ToString("N");
        var secondModel = "second-" + Guid.NewGuid().ToString("N");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Platform"] = connectionString
        }).Build();
        var store = new PostgresModelStore(configuration);
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();
        try
        {
            await Execute(db, "INSERT INTO public.platform_users(user_id,issuer,subject,display_name) VALUES($1,'https://model-fixture.example',$2,'Model fixture')", user, user.ToString());
            await Execute(db, "INSERT INTO public.platform_tenants(tenant_id,name) VALUES($1,'Model A'),($2,'Model B')", tenant, otherTenant);
            await Execute(db, "INSERT INTO public.tenant_memberships(tenant_id,user_id) VALUES($1,$3),($2,$3)", tenant, otherTenant, user);

            Assert(await store.ReadConfigurationAsync(tenant, model, default) is null, "Missing tenant configuration cannot imply model enablement.");
            var request = Id();
            var created = await store.SaveConfigurationAsync(tenant, model, true, 512, 0, user, request, default);
            Assert(created == new TenantModelConfiguration(tenant, model, true, 512, 1), "Initial model configuration must persist at revision one.");
            Assert(await new PostgresModelStore(configuration).ReadConfigurationAsync(tenant, model, default) == created, "Configuration must survive store recreation.");
            Assert(await store.ReadConfigurationAsync(otherTenant, model, default) is null, "Configuration reads must remain tenant scoped.");
            Assert(await Scalar(db, "SELECT count(*) FROM public.platform_model_configuration_audit WHERE tenant_id=$1 AND model_id=$2 AND actor_user_id=$3 AND request_id=$4 AND revision=1 AND enabled AND max_output_tokens=512", tenant, model, user, request) is 1L,
                "Configuration writes need attributed request and revision audit metadata.");
            Assert(await store.SaveConfigurationAsync(tenant, model, false, 256, 0, user, Id(), default) is null, "Create cannot overwrite an existing configuration.");
            Assert(await store.SaveConfigurationAsync(tenant, secondModel, true, 128, 1, user, Id(), default) is null, "Update cannot create a configuration when its expected revision is missing.");
            var creates = await Task.WhenAll(
                store.SaveConfigurationAsync(tenant, secondModel, true, 128, 0, user, Id(), default),
                store.SaveConfigurationAsync(tenant, secondModel, false, 256, 0, user, Id(), default));
            Assert(creates.Count(value => value is not null) == 1 && creates.Single(value => value is not null)!.Revision == 1,
                "Concurrent first saves must create exactly one configuration without overwriting it.");
            var updates = await Task.WhenAll(
                store.SaveConfigurationAsync(tenant, model, true, 256, 1, user, Id(), default),
                store.SaveConfigurationAsync(tenant, model, true, 1024, 1, user, Id(), default));
            Assert(updates.Count(value => value is not null) == 1 && updates.Single(value => value is not null)!.Revision == 2,
                "Concurrent editors must not overwrite the same revision.");
            Assert(await Scalar(db, "SELECT count(*) FROM public.platform_model_configuration_audit WHERE tenant_id=$1 AND model_id=$2", tenant, model) is 2L,
                "Conflicting edits must not create successful management audit events.");
            await VerifyManagementAuditRollback(db, store, tenant, user, model);
            await VerifyQueuedSaveRechecksAccess(connectionString, db, store, tenant, user, model);

            var callRequest = Id();
            var call = await store.BeginCallAsync(tenant, user, model, callRequest, default);
            var running = (await store.ListCallsAsync(tenant, model, default)).Single();
            Assert(running.CallId == call && running.UserId == user && running.TenantId == tenant && running.ModelId == model
                && running.RequestId == callRequest && running.Outcome == "running" && running.Usage is null,
                "Admission must persist an attributed running call before upstream execution, without invented usage.");
            Assert((await store.ListCallsAsync(otherTenant, model, default)).Count == 0
                && (await store.ListCallsAsync(tenant, secondModel, default)).Count == 0, "Call history must be scoped by both tenant and model.");
            await store.CompleteCallAsync(call, "completed", 37, new(11, 7, 18), default);
            var completed = (await store.ListCallsAsync(tenant, model, default)).Single();
            Assert(completed.Outcome == "completed" && completed.ElapsedMilliseconds == 37 && completed.Usage == new ModelUsage(11, 7, 18),
                "Completion must retain exact provider usage and elapsed time.");
            await Unavailable(() => store.CompleteCallAsync(call, "failed", 99, null, default));
            Assert((await store.ListCallsAsync(tenant, model, default)).Single() == completed, "A terminal record must not be overwritten.");
            foreach (var outcome in new[] { "cancelled", "revoked", "timeout", "upstream_error", "failed" })
            {
                var id = await store.BeginCallAsync(tenant, user, model, Id(), default);
                await store.CompleteCallAsync(id, outcome, 5, null, default);
                Assert((await store.ListCallsAsync(tenant, model, default)).Single(row => row.CallId == id) is { Usage: null, Outcome: var actual } && actual == outcome,
                    "Interrupted calls must keep their bounded outcome and unknown usage.");
            }
            var invalid = await store.BeginCallAsync(tenant, user, model, Id(), default);
            await Unavailable(() => store.CompleteCallAsync(invalid, "provider response text", 5, null, default));
            await Unavailable(() => store.CompleteCallAsync(invalid, "failed", -1, null, default));
            await Unavailable(() => store.CompleteCallAsync(invalid, "completed", 5, new(-1, 1, 0), default));
            Assert((await store.ListCallsAsync(tenant, model, default)).Single(row => row.CallId == invalid).Outcome == "running",
                "Invalid terminal metadata must not mutate the pending record.");
            await store.CompleteCallAsync(invalid, "completed", 0, new(0, 0, 0), default);
            Assert((await store.ListCallsAsync(tenant, model, default)).Single(row => row.CallId == invalid).Usage == new ModelUsage(0, 0, 0),
                "Explicit provider zero usage is distinct from absent usage.");
            var exactUsage = await store.BeginCallAsync(tenant, user, model, Id(), default);
            await store.CompleteCallAsync(exactUsage, "completed", 5, new(11, 7, 20), default);
            Assert((await store.ListCallsAsync(tenant, model, default)).Single(row => row.CallId == exactUsage).Usage == new ModelUsage(11, 7, 20),
                "Storage must preserve provider-reported totals rather than calculate or replace them.");
            await VerifyCallAuditFailure(db, store, tenant, user, model);

            Assert(completed.Source is null, "Direct model calls must not invent an application source.");
            var source = new ModelCallSource("model-sample", "1.0.0", Guid.NewGuid(), Id());
            var delegated = await store.BeginCallAsync(tenant, user, model, Id(), default, source);
            Assert((await store.ListCallsAsync(tenant, model, default)).Single(row => row.CallId == delegated).Source == source,
                "Delegated model admission must atomically retain its server-captured application and run source.");
            await Unavailable(() => store.BeginCallAsync(tenant, user, model, Id(), default, source with { RunId = Guid.Empty }));
            try
            {
                await Execute(db, "UPDATE public.platform_model_calls SET app_id='partial-source' WHERE call_id=$1", call);
                throw new InvalidOperationException("A partial application source passed the database check.");
            }
            catch (PostgresException error) when (error.SqlState == "23514") { }

            await store.SaveConfigurationAsync(tenant, model, false, 512, 2, user, Id(), default);
            await Unavailable(() => store.BeginCallAsync(tenant, user, model, Id(), default));
            await Unavailable(() => store.BeginCallAsync(otherTenant, user, model, Id(), default));
            await store.SaveConfigurationAsync(tenant, model, true, 512, 3, user, Id(), default);
            await Execute(db, "UPDATE public.tenant_memberships SET enabled=false WHERE tenant_id=$1 AND user_id=$2", tenant, user);
            await Unavailable(() => store.BeginCallAsync(tenant, user, model, Id(), default));
            await Execute(db, "UPDATE public.tenant_memberships SET enabled=true WHERE tenant_id=$1 AND user_id=$2", tenant, user);

            await Execute(db, "UPDATE public.platform_model_calls SET started_at=now()-interval '1 day' WHERE tenant_id=$1 AND model_id=$2", tenant, model);
            for (var index = 0; index < 51; index++) await store.BeginCallAsync(tenant, user, model, Id(), default);
            var recent = await store.ListCallsAsync(tenant, model, default);
            Assert(recent.Count == 50 && recent.All(row => row.CallId != call)
                && recent.Zip(recent.Skip(1), (left, right) => left.StartedAt >= right.StartedAt).All(value => value),
                "History must return only the newest fifty calls in descending order.");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await store.ReadConfigurationAsync(tenant, model, cancelled.Token); throw new InvalidOperationException("Cancellation was ignored."); }
            catch (OperationCanceledException) { }
            var invalidSettings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Platform"] = "Password=private-model-secret;InvalidOption=true"
            }).Build();
            await Unavailable(() => new PostgresModelStore(invalidSettings).ReadConfigurationAsync(tenant, model, default));

            await Execute(db, "DELETE FROM public.platform_tenants WHERE tenant_id=$1", tenant);
            Assert(await store.ReadConfigurationAsync(tenant, model, default) is null && (await store.ListCallsAsync(tenant, model, default)).Count == 50,
                "Deleting mutable tenant configuration must retain historical call attribution.");
        }
        finally
        {
            await Execute(db, "DELETE FROM public.platform_model_calls WHERE tenant_id IN ($1,$2)", tenant, otherTenant);
            await Execute(db, "DELETE FROM public.platform_model_configuration_audit WHERE tenant_id IN ($1,$2)", tenant, otherTenant);
            await Execute(db, "DELETE FROM public.platform_tenants WHERE tenant_id IN ($1,$2)", tenant, otherTenant);
            await Execute(db, "DELETE FROM public.platform_users WHERE user_id=$1", user);
        }
        Console.WriteLine("PASS: PostgreSQL models: tenant configuration, optimistic revision, atomic management audit, durable call admission, exact nullable usage, terminal outcomes and bounded tenant/model history.");
    }

    private static async Task VerifyManagementAuditRollback(NpgsqlConnection db, IModelStore store, Guid tenant, Guid user, string model)
    {
        var before = await store.ReadConfigurationAsync(tenant, model, default);
        await Execute(db, "CREATE FUNCTION test_support.fail_model_configuration_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private-model-secret' USING ERRCODE='23514'; END $$");
        try
        {
            // Identifier is generated by this fixture, not supplied by a request.
            await Execute(db, $"CREATE TRIGGER fixture_model_configuration_audit BEFORE INSERT ON public.platform_model_configuration_audit FOR EACH ROW WHEN (NEW.model_id='{model}') EXECUTE FUNCTION test_support.fail_model_configuration_audit()");
            try
            {
                await Unavailable(() => store.SaveConfigurationAsync(tenant, model, false, 128, 2, user, Id(), default));
                Assert(await store.ReadConfigurationAsync(tenant, model, default) == before, "An audit failure must roll back the configuration write.");
            }
            finally { await Execute(db, "DROP TRIGGER fixture_model_configuration_audit ON public.platform_model_configuration_audit"); }
        }
        finally { await Execute(db, "DROP FUNCTION test_support.fail_model_configuration_audit()"); }
    }

    private static async Task VerifyCallAuditFailure(NpgsqlConnection db, IModelStore store, Guid tenant, Guid user, string model)
    {
        var pending = await store.BeginCallAsync(tenant, user, model, Id(), default);
        await Execute(db, "CREATE FUNCTION test_support.fail_model_call() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private-model-secret' USING ERRCODE='23514'; END $$");
        try
        {
            await Execute(db, $"CREATE TRIGGER fixture_model_call BEFORE INSERT OR UPDATE ON public.platform_model_calls FOR EACH ROW WHEN (NEW.model_id='{model}') EXECUTE FUNCTION test_support.fail_model_call()");
            try
            {
                var request = Id();
                await Unavailable(() => store.BeginCallAsync(tenant, user, model, request, default));
                Assert(await Scalar(db, "SELECT count(*) FROM public.platform_model_calls WHERE request_id=$1", request) is 0L,
                    "A rejected call admission must not leave a durable record or report success.");
                await Unavailable(() => store.CompleteCallAsync(pending, "completed", 10, new(1, 1, 2), default));
                Assert((await store.ListCallsAsync(tenant, model, default)).Single(row => row.CallId == pending).Outcome == "running",
                    "A failed completion write must retain the unfinished record.");
            }
            finally { await Execute(db, "DROP TRIGGER fixture_model_call ON public.platform_model_calls"); }
        }
        finally { await Execute(db, "DROP FUNCTION test_support.fail_model_call()"); }
    }

    private static async Task VerifyQueuedSaveRechecksAccess(string connection, NpgsqlConnection db, IModelStore store, Guid tenant, Guid user, string model)
    {
        var before = await store.ReadConfigurationAsync(tenant, model, default);
        var audited = await Scalar(db, "SELECT count(*) FROM public.platform_model_configuration_audit WHERE tenant_id=$1 AND model_id=$2", tenant, model);
        await using var blocker = new NpgsqlConnection(connection);
        await blocker.OpenAsync();
        await using var tx = await blocker.BeginTransactionAsync();
        await Execute(blocker, "LOCK TABLE public.tenant_models IN ACCESS EXCLUSIVE MODE");
        var allowed = true; var checks = 0;
        var save = store.SaveConfigurationAsync(tenant, model, false, 128, before!.Revision, user, Id(), default,
            _ => { Interlocked.Increment(ref checks); return Task.FromResult(allowed); });
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!(bool)(await Scalar(db, "SELECT EXISTS(SELECT 1 FROM pg_locks WHERE relation='public.tenant_models'::regclass AND NOT granted)"))!)
            await Task.Delay(10, wait.Token);
        Assert(checks == 0, "Model live-access check must follow lock acquisition, not precede the database wait.");
        allowed = false;
        await tx.RollbackAsync();
        try { await save; throw new InvalidOperationException("A model save queued before revocation must be denied."); }
        catch (ModelConfigurationAccessDeniedException) { }
        Assert(checks == 1 && await store.ReadConfigurationAsync(tenant, model, default) == before
            && Equals(audited, await Scalar(db, "SELECT count(*) FROM public.platform_model_configuration_audit WHERE tenant_id=$1 AND model_id=$2", tenant, model)),
            "Revoked queued model save cannot mutate configuration or append successful audit.");
    }

    private static string Id() => Guid.NewGuid().ToString("N");
    private static async Task Unavailable(Func<Task> action)
    {
        try { await action(); throw new InvalidOperationException("Expected a safe model storage failure."); }
        catch (ModelStoreUnavailableException error)
        {
            Assert(error.InnerException is null && !error.ToString().Contains("private-model-secret", StringComparison.Ordinal),
                "Storage errors must not expose connection strings or PostgreSQL details.");
        }
    }
    private static async Task Execute(NpgsqlConnection db, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, db);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<object?> Scalar(NpgsqlConnection db, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, db);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return await command.ExecuteScalarAsync();
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
