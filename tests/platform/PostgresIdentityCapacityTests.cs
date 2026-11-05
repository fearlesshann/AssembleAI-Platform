using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.IdentityManagement;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace AssembleAI.Platform.SmokeTests;

// The dispatcher verifies the disposable instance marker before invoking this class.
public static class PostgresIdentityCapacityTests
{
    public static async Task RunAsync(string connectionString)
    {
        await using var db = new NpgsqlConnection(connectionString); await db.OpenAsync();
        var issuer = "https://identity-capacity.example/" + Guid.NewGuid().ToString("N");
        var actor = Guid.NewGuid(); var tenant = Guid.NewGuid();
        var users = new List<Guid> { actor }; var tenants = new List<Guid> { tenant };
        var subject = new SubjectIdentity(issuer, actor.ToString());
        var store = new PostgresIdentityManagementStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Platform"] = connectionString }).Build());
        static Task<bool> Current(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(true); }
        try
        {
            await Execute(db, "INSERT INTO public.platform_users(user_id,issuer,subject,display_name) VALUES($1,$2,$3,'capacity admin')", actor, issuer, actor.ToString());
            await Execute(db, "INSERT INTO public.platform_identity_administrators(user_id) VALUES($1)", actor);
            await Execute(db, "INSERT INTO public.platform_tenants(tenant_id,name) VALUES($1,'capacity tenant')", tenant);

            // The 500th record is accepted. The next create cannot strand bounded reads at 503.
            var fillUsers = NewIds(499 - await Count(db, "SELECT count(*) FROM public.platform_users")); users.AddRange(fillUsers);
            await Execute(db, "INSERT INTO public.platform_users(user_id,issuer,subject,display_name) SELECT x,$1,x::text,'capacity user' FROM unnest($2::uuid[]) x", issuer, fillUsers);
            var lastUser = Guid.NewGuid(); users.Add(lastUser);
            var userValue = new SaveIdentityUser(issuer, lastUser.ToString(), "last capacity user", true, false, 0);
            Success(await store.SaveUserAsync(subject, lastUser, userValue, "capacity-user-500", Current, default), "500th user");
            var rejectedUser = Guid.NewGuid(); users.Add(rejectedUser);
            await Capacity(db, actor, "platform_users", null, () => store.SaveUserAsync(subject, rejectedUser,
                userValue with { Subject = rejectedUser.ToString() }, "capacity-user-501", Current, default));
            Success(await store.SaveUserAsync(subject, lastUser, userValue with { Enabled = false, ExpectedRevision = 1 }, "capacity-user-edit", Current, default), "User edits remain available at capacity");
            await Capacity(db, actor, "platform_users", null, () => store.SaveUserAsync(subject, rejectedUser,
                userValue with { Subject = rejectedUser.ToString() }, "capacity-disabled-user-still-counts", Current, default));
            Assert((await store.ReadWorkspaceAsync(subject, default)).Value?.Users.Count == 500, "Full user directory remains readable.");

            var fillTenants = NewIds(499 - await Count(db, "SELECT count(*) FROM public.platform_tenants")); tenants.AddRange(fillTenants);
            await Execute(db, "INSERT INTO public.platform_tenants(tenant_id,name) SELECT x,'capacity tenant' FROM unnest($1::uuid[]) x", fillTenants);
            var lastTenant = Guid.NewGuid(); tenants.Add(lastTenant);
            Success(await store.SaveTenantAsync(subject, lastTenant, new("last tenant", true, 0), "capacity-tenant-500", Current, default), "500th tenant");
            var rejectedTenant = Guid.NewGuid(); tenants.Add(rejectedTenant);
            await Capacity(db, actor, "platform_tenants", null, () => store.SaveTenantAsync(subject, rejectedTenant,
                new("too many tenants", true, 0), "capacity-tenant-501", Current, default));
            Success(await store.SaveTenantAsync(subject, lastTenant, new("disabled at capacity", false, 1), "capacity-tenant-edit", Current, default), "Tenant edits remain available at capacity");
            await Capacity(db, actor, "platform_tenants", null, () => store.SaveTenantAsync(subject, rejectedTenant,
                new("too many tenants", true, 0), "capacity-disabled-tenant-still-counts", Current, default));
            Assert((await store.ReadWorkspaceAsync(subject, default)).Value?.Tenants.Count == 500, "Full tenant directory remains readable.");

            var organizations = NewIds(499);
            await Execute(db, "INSERT INTO public.tenant_organizations(tenant_id,organization_id,name,kind) SELECT $1,x,'capacity factory','factory' FROM unnest($2::uuid[]) x", tenant, organizations);
            var lastOrg = Guid.NewGuid();
            Success(await store.SaveOrganizationAsync(subject, tenant, null, lastOrg, new("last factory", "factory", true, 0), "capacity-org-500", Current, default), "500th organization");
            await Capacity(db, actor, "tenant_organizations", tenant, () => store.SaveOrganizationAsync(subject, tenant, null, Guid.NewGuid(),
                new("too many factories", "factory", true, 0), "capacity-org-501", Current, default));
            Success(await store.SaveOrganizationAsync(subject, tenant, null, lastOrg, new("disabled factory", "factory", false, 1), "capacity-org-edit", Current, default), "Organization edits remain available at capacity");
            await Capacity(db, actor, "tenant_organizations", tenant, () => store.SaveOrganizationAsync(subject, tenant, null, Guid.NewGuid(),
                new("too many factories", "factory", true, 0), "capacity-disabled-org-still-counts", Current, default));
            Assert((await store.ReadTenantAsync(subject, tenant, null, default)).Value?.Organizations.Count == 500, "Full organization directory remains readable.");
            // Count boundaries are independent of the separate aggregate response budget.
            await Execute(db, "DELETE FROM public.tenant_organizations WHERE tenant_id=$1", tenant);

            var roles = NewIds(499);
            await Execute(db, "INSERT INTO public.tenant_roles(tenant_id,role_id,name) SELECT $1,x,x::text FROM unnest($2::uuid[]) x", tenant, roles);
            var lastRole = Guid.NewGuid();
            Success(await store.SaveRoleAsync(subject, tenant, null, lastRole, new("last role", true, [], 0), "capacity-role-500", Current, default), "500th role");
            await Capacity(db, actor, "tenant_roles", tenant, () => store.SaveRoleAsync(subject, tenant, null, Guid.NewGuid(),
                new("too many roles", true, [], 0), "capacity-role-501", Current, default));
            Success(await store.SaveRoleAsync(subject, tenant, null, lastRole, new("disabled role", false, [], 1), "capacity-role-edit", Current, default), "Role edits remain available at capacity");
            await Capacity(db, actor, "tenant_roles", tenant, () => store.SaveRoleAsync(subject, tenant, null, Guid.NewGuid(),
                new("too many roles", true, [], 0), "capacity-disabled-role-still-counts", Current, default));
            Assert((await store.ReadTenantAsync(subject, tenant, null, default)).Value?.Roles.Count == 500, "Full role directory remains readable.");
            await Execute(db, "DELETE FROM public.tenant_roles WHERE tenant_id=$1", tenant);

            // An external provisioning path can predate the directory cap; the member cap
            // must still reject a valid 501st user, independently of global user creation.
            var memberIds = await ReadIds(db, "SELECT user_id FROM public.platform_users ORDER BY user_id");
            await Execute(db, "INSERT INTO public.tenant_memberships(tenant_id,user_id) SELECT $1,x FROM unnest($2::uuid[]) x", tenant, memberIds.Take(499).ToArray());
            var lastMember = memberIds[499];
            Success(await store.SaveMemberAsync(subject, tenant, null, lastMember, new(true, false, [], 0), "capacity-member-500", Current, default), "500th member");
            var externalUser = Guid.NewGuid(); users.Add(externalUser);
            await Execute(db, "INSERT INTO public.platform_users(user_id,issuer,subject,display_name) VALUES($1,$2,$3,'external user')", externalUser, issuer, externalUser.ToString());
            await Capacity(db, actor, "tenant_memberships", tenant, () => store.SaveMemberAsync(subject, tenant, null, externalUser,
                new(true, false, [], 0), "capacity-member-501", Current, default));
            Success(await store.SaveMemberAsync(subject, tenant, null, lastMember, new(false, false, [], 1), "capacity-member-edit", Current, default), "Member edits remain available at capacity");
            await Capacity(db, actor, "tenant_memberships", tenant, () => store.SaveMemberAsync(subject, tenant, null, externalUser,
                new(true, false, [], 0), "capacity-disabled-member-still-counts", Current, default));
            Assert((await store.ReadTenantAsync(subject, tenant, null, default)).Value?.Members.Count == 500, "Full member directory remains readable.");
            Console.WriteLine("PASS: identity user/tenant/member/organization/role 500th create, 501st rejection, no audit/write, disabled capacity and existing edits.");
        }
        finally
        {
            await Execute(db, "DELETE FROM public.platform_identity_audit WHERE actor_user_id=$1", actor);
            await Execute(db, "DELETE FROM public.platform_tenants WHERE tenant_id=ANY($1)", tenants.ToArray());
            await Execute(db, "DELETE FROM public.platform_users WHERE user_id=ANY($1)", users.ToArray());
        }
    }
    private static async Task Capacity(NpgsqlConnection db, Guid actor, string table, Guid? tenant, Func<Task<IdentityResult<IdentityRevision>>> create)
    {
        var audit = await Count(db, "SELECT count(*) FROM public.platform_identity_audit WHERE actor_user_id=$1", actor);
        var result = await create();
        Assert(result.Error.ToString() == "Capacity", table + ": the 501st create must return Capacity, got " + result.Error);
        Assert(await Count(db, "SELECT count(*) FROM public.platform_identity_audit WHERE actor_user_id=$1", actor) == audit, table + ": capacity rejection cannot persist a success audit.");
        Assert(await Count(db, "SELECT count(*) FROM public." + table + (tenant.HasValue ? " WHERE tenant_id=$1" : ""), tenant.HasValue ? [tenant.Value] : []) == 500,
            table + ": capacity rejection cannot insert a record.");
    }
    private static Guid[] NewIds(long count)
    { Assert(count is >= 0 and <= 500, "Fixture must not overwrite existing capacity."); return Enumerable.Range(0, (int)count).Select(_ => Guid.NewGuid()).ToArray(); }
    private static void Success(IdentityResult<IdentityRevision> result, string action) => Assert(result.Error == IdentityError.None, action + ": " + result.Error);
    private static async Task Execute(NpgsqlConnection db, string sql, params object[] values)
    { await using var command = Command(db, sql, values); await command.ExecuteNonQueryAsync(); }
    private static async Task<long> Count(NpgsqlConnection db, string sql, params object[] values)
    { await using var command = Command(db, sql, values); return (long)(await command.ExecuteScalarAsync())!; }
    private static async Task<Guid[]> ReadIds(NpgsqlConnection db, string sql)
    { await using var command = Command(db, sql); await using var reader = await command.ExecuteReaderAsync(); var ids = new List<Guid>(); while (await reader.ReadAsync()) ids.Add(reader.GetGuid(0)); return ids.ToArray(); }
    private static NpgsqlCommand Command(NpgsqlConnection db, string sql, params object[] values)
    { var command = new NpgsqlCommand(sql, db); foreach (var value in values) command.Parameters.AddWithValue(value); return command; }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
