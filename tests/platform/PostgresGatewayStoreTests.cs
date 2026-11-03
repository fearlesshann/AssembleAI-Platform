using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Gateway;
using AssembleAI.Platform.Registry;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace AssembleAI.Platform.SmokeTests;

// The isolated PostgreSQL dispatcher validates the database marker before invoking this test.
public static class PostgresGatewayStoreTests
{
    public static async Task RunAsync(string connectionString)
    {
        var f = new Fixture();
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();
        try
        {
            await Seed(db, f);
            var first = ManifestTests.Valid(f.App) with { Permissions = ["production:read", "reports:use"] };
            var second = first with { Version = "2.0.0", FrontendEntry = $"/apps/{f.App}/2.0.0/", FrontendArtifact = new("2.0.0", new string('a', 64)) };
            var values = ManifestTests.CatalogSettings(first);
            foreach (var pair in ManifestTests.CatalogSettings(second).Where(p => p.Key.StartsWith("Registry:FrontendArtifacts:0:", StringComparison.Ordinal)))
                values[pair.Key.Replace(":0:", ":1:")] = pair.Value;
            values["ConnectionStrings:Platform"] = connectionString;
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var authorization = new PostgresAuthorizationStore(configuration);
            var registry = new PostgresRegistryStore(configuration, new(configuration), authorization);
            var store = new PostgresGatewayStore(configuration, authorization);
            var subject = new SubjectIdentity(f.Issuer, f.Owner.ToString());
            var reviewer = new SubjectIdentity(f.Issuer, f.Reviewer.ToString());
            foreach (var manifest in new[] { first, second })
            {
                Ok(await registry.SaveDraftAsync(subject, manifest, 0, Id()));
                Ok(await registry.TransitionAsync(subject, f.App, manifest.Version, "submit", 1, Id()));
                Ok(await registry.TransitionAsync(reviewer, f.App, manifest.Version, "publish", 2, Id()));
            }
            Ok(await registry.SetTenantApplicationAsync(subject, f.Tenant, f.App, "1.0.0", true, 0, Id()));
            Assert(await store.ResolveStartAsync(subject, f.Tenant, f.App, "1.0.0") is not null, "Current enabled release must resolve.");
            Assert(await store.ResolveStartAsync(subject, f.Tenant, f.App, "2.0.0") is null, "A published inactive release must not start.");
            Assert(await store.CreateRunAsync(subject, "session-a", f.Tenant, f.App, "2.0.0", TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(2)) is null, "Guessing an old/new release cannot create a run.");
            var run = await Create(store, subject, f);
            Assert(await store.ReadRunAsync(subject, "session-a", run.RunId) is not null, "A bound live lease must load.");
            Assert(await store.ReadRunAsync(subject, "session-b", run.RunId) is null, "A separate session cannot steal a run.");
            Assert(await store.ReadRunAsync(new(f.Issuer, f.Other.ToString()), "session-a", run.RunId) is null, "A different authorized subject cannot steal a run.");
            Assert(await store.ReadRunAsync(new(f.Issuer + "/other", f.Owner.ToString()), "session-a", run.RunId) is null, "Issuer is part of runtime ownership.");
            Assert(await store.ResolveStartAsync(subject, f.OtherTenant, f.App, "1.0.0") is null, "An unrelated tenant cannot resolve another tenant's activation.");
            Assert(await Scalar(db, "SELECT session_hash FROM public.platform_gateway_runs WHERE run_id=$1", run.RunId) is string hash
                && hash.Length == 64 && hash != "session-a", "Runtime storage must not retain the BFF session reference.");

            Ok(await registry.SetTenantApplicationAsync(subject, f.Tenant, f.App, "2.0.0", true, 1, Id()));
            var draining = await store.ReadRunAsync(subject, "session-a", run.RunId);
            Assert(draining?.DrainUntil is not null && draining.DrainUntil <= draining.ExpiresAt, "Upgrade must set a bounded drain deadline.");
            var originalDrain = draining!.DrainUntil;
            var runB = await Create(store, subject, f, "2.0.0");
            Ok(await registry.SetTenantApplicationAsync(subject, f.Tenant, f.App, "1.0.0", true, 2, Id()));
            Assert((await store.ReadRunAsync(subject, "session-a", run.RunId))?.DrainUntil == originalDrain, "Rollback must not reset or extend a draining lease.");
            await Execute(db, "UPDATE public.platform_gateway_runs SET drain_until=created_at WHERE run_id=$1", run.RunId);
            Assert(await store.ReadRunAsync(subject, "session-a", run.RunId) is null, "A drained lease must remain invalid even when its version becomes current again.");
            Ok(await registry.SetTenantApplicationAsync(subject, f.Tenant, f.App, "2.0.0", true, 3, Id()));
            Assert(await store.ReadRunAsync(subject, "session-a", run.RunId) is null, "Further pointer flips must not revive a drained lease.");
            Ok(await registry.TransitionAsync(reviewer, f.App, "2.0.0", "disable", 3, Id()));
            Assert(await store.ReadRunAsync(subject, "session-a", runB.RunId) is null, "Global stop must revoke existing runs.");
            Ok(await registry.TransitionAsync(reviewer, f.App, "2.0.0", "enable", 4, Id()));
            Assert(await store.ReadRunAsync(subject, "session-a", runB.RunId) is null, "Global reenable must not revive stopped runs.");
            var tenantStopped = await Create(store, subject, f, "2.0.0");
            Ok(await registry.SetTenantApplicationAsync(subject, f.Tenant, f.App, "2.0.0", false, 4, Id()));
            Ok(await registry.SetTenantApplicationAsync(subject, f.Tenant, f.App, "2.0.0", true, 5, Id()));
            Assert(await store.ReadRunAsync(subject, "session-a", tenantStopped.RunId) is null, "Tenant reenable must not revive stopped runs.");

            var authorized = await Create(store, subject, f, "2.0.0");
            await Execute(db, "UPDATE public.role_permission_grants SET enabled=false WHERE tenant_id=$1 AND grant_id=$2", f.Tenant, f.SecondGrant);
            Assert(await store.ResolveStartAsync(subject, f.Tenant, f.App, "2.0.0") is null
                && await store.ReadRunAsync(subject, "session-a", authorized.RunId) is null, "Every manifest permission must remain granted; the first permission alone is insufficient.");
            await Execute(db, "UPDATE public.role_permission_grants SET enabled=true WHERE tenant_id=$1 AND grant_id=$2", f.Tenant, f.SecondGrant);
            var call = Id();
            await store.StartCallAsync(authorized, call, "POST", "/ask");
            Assert(await Scalar(db, "SELECT outcome FROM public.platform_gateway_calls WHERE request_id=$1", call) is "running", "Call admission must be durable before forwarding.");
            await store.FinishCallAsync(call, 200, "transport_completed", 12);
            Assert(await Scalar(db, "SELECT outcome FROM public.platform_gateway_calls WHERE request_id=$1", call) is "transport_completed", "Transport completion must persist without claiming business success.");
            await Unavailable(() => store.FinishCallAsync(call, 500, "error", 13));
            Assert(await Scalar(db, "SELECT http_status FROM public.platform_gateway_calls WHERE request_id=$1", call) is 200, "A terminal log cannot be overwritten by retry.");
            await VerifyAuditFailure(db, store, authorized, f.App);
            await Execute(db, "UPDATE public.platform_gateway_runs SET created_at=now()-interval '1 hour',expires_at=now()-interval '1 second',drain_until=NULL WHERE run_id=$1", authorized.RunId);
            Assert(await store.ReadRunAsync(subject, "session-a", authorized.RunId) is null, "Expired runs must fail closed.");

            // The per-user bound spans all active runs, including parallel creates.
            await Execute(db, "UPDATE public.platform_gateway_runs SET revoked_at=now() WHERE user_id=$1", f.Owner);
            for (var index = 0; index < 98; index++) await Create(store, subject, f, "2.0.0");
            var created = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => store.CreateRunAsync(subject, "session-a", f.Tenant, f.App, "2.0.0", TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(2))));
            Assert(created.Count(value => value is not null) == 2, "Concurrent creation must enforce the shared per-user run bound.");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await store.ReadRunAsync(subject, "session-a", Guid.NewGuid(), cancelled.Token); throw new InvalidOperationException("Cancellation was ignored."); }
            catch (OperationCanceledException) { }
        }
        finally
        {
            await Execute(db, "DELETE FROM public.platform_gateway_calls WHERE app_id=$1", f.App);
            await Execute(db, "DELETE FROM public.platform_applications WHERE app_id=$1", f.App);
            await Execute(db, "DELETE FROM public.platform_registry_audit WHERE app_id=$1", f.App);
            await Execute(db, "DELETE FROM public.platform_tenants WHERE tenant_id IN ($1,$2)", f.Tenant, f.OtherTenant);
            await Execute(db, "DELETE FROM public.platform_users WHERE user_id IN ($1,$2,$3)", f.Owner, f.Reviewer, f.Other);
        }
        Console.WriteLine("PASS: PostgreSQL gateway leases: current version admission, session/subject isolation, irreversible drain/stop, all-permission revocation, bounded concurrency and durable call lifecycle.");
    }

    private static async Task VerifyAuditFailure(NpgsqlConnection db, IGatewayStore store, GatewayRuntime run, string app)
    {
        var pending = Id();
        await store.StartCallAsync(run, pending, "GET", "/stream");
        await Execute(db, "CREATE FUNCTION test_support.fail_gateway_call() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture call failure' USING ERRCODE='23514'; END $$");
        try
        {
            // The app identifier is a locally generated prefix and UUID, never request input.
            await Execute(db, $"CREATE TRIGGER fixture_gateway_call BEFORE INSERT OR UPDATE ON public.platform_gateway_calls FOR EACH ROW WHEN (NEW.app_id='{app}') EXECUTE FUNCTION test_support.fail_gateway_call()");
            try
            {
                var refused = Id();
                await Unavailable(() => store.StartCallAsync(run, refused, "GET", "/stream"));
                Assert(await Scalar(db, "SELECT count(*) FROM public.platform_gateway_calls WHERE request_id=$1", refused) is 0L, "Failed admission must not leave a call row.");
                await Unavailable(() => store.FinishCallAsync(pending, 499, "cancelled", 10));
                Assert(await Scalar(db, "SELECT outcome FROM public.platform_gateway_calls WHERE request_id=$1", pending) is "running", "Failed finish must retain the pending record rather than claim completion.");
            }
            finally { await Execute(db, "DROP TRIGGER fixture_gateway_call ON public.platform_gateway_calls"); }
        }
        finally { await Execute(db, "DROP FUNCTION test_support.fail_gateway_call()"); }
    }
    private static async Task Seed(NpgsqlConnection db, Fixture f)
    {
        foreach (var user in new[] { f.Owner, f.Reviewer, f.Other })
            await Execute(db, "INSERT INTO public.platform_users(user_id,issuer,subject,display_name) VALUES($1,$2,$3,'Gateway fixture')", user, f.Issuer, user.ToString());
        await Execute(db, "INSERT INTO public.platform_registry_operators(user_id,role) VALUES($1,'developer'),($2,'reviewer')", f.Owner, f.Reviewer);
        await Execute(db, "INSERT INTO public.platform_tenants(tenant_id,name) VALUES($1,'Gateway A'),($2,'Gateway B')", f.Tenant, f.OtherTenant);
        await Execute(db, "INSERT INTO public.tenant_memberships(tenant_id,user_id) VALUES($1,$3),($1,$4),($2,$3)", f.Tenant, f.OtherTenant, f.Owner, f.Other);
        await Execute(db, "INSERT INTO public.tenant_roles(tenant_id,role_id,name) VALUES($1,$2,'Gateway role')", f.Tenant, f.Role);
        await Execute(db, "INSERT INTO public.tenant_member_roles(tenant_id,user_id,role_id) VALUES($1,$2,$4),($1,$3,$4)", f.Tenant, f.Owner, f.Other, f.Role);
        foreach (var (grant, permission) in new[] { (f.FirstGrant, "production:read"), (f.SecondGrant, "reports:use"), (f.ManageGrant, "registry:manage") })
            await Execute(db, "INSERT INTO public.role_permission_grants(tenant_id,grant_id,role_id,permission,resource_type,resource_id,tenant_wide) VALUES($1,$2,$3,$4,'application',$5,true)", f.Tenant, grant, f.Role, permission, f.App);
    }
    private static async Task<GatewayRuntime> Create(IGatewayStore store, SubjectIdentity subject, Fixture f, string version = "1.0.0") =>
        await store.CreateRunAsync(subject, "session-a", f.Tenant, f.App, version, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(2))
        ?? throw new InvalidOperationException("Expected an authorized runtime lease.");
    private static string Id() => Guid.NewGuid().ToString("N");
    private static void Ok<T>(RegistryResult<T> result) => Assert(result.Error == RegistryError.None, "Registry setup or transition failed.");
    private static async Task Unavailable(Func<Task> action)
    {
        try { await action(); throw new InvalidOperationException("Expected a safe storage failure."); }
        catch (GatewayStoreUnavailableException e) { Assert(e.InnerException is null, "Storage failure must not retain raw database error details."); }
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
    private sealed class Fixture
    {
        public string App { get; } = "gateway-" + Guid.NewGuid().ToString("N");
        public string Issuer { get; } = "https://gateway-fixture.example/" + Guid.NewGuid().ToString("N");
        public Guid Owner { get; } = Guid.NewGuid();
        public Guid Reviewer { get; } = Guid.NewGuid();
        public Guid Other { get; } = Guid.NewGuid();
        public Guid Tenant { get; } = Guid.NewGuid();
        public Guid OtherTenant { get; } = Guid.NewGuid();
        public Guid Role { get; } = Guid.NewGuid();
        public Guid FirstGrant { get; } = Guid.NewGuid();
        public Guid SecondGrant { get; } = Guid.NewGuid();
        public Guid ManageGrant { get; } = Guid.NewGuid();
    }
}
