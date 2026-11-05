using System.Text.Encodings.Web;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.IdentityManagement;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace AssembleAI.Platform.SmokeTests;

public static class PostgresIdentityResponseBudgetTests
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
    { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static async Task RunAsync(string connectionString)
    {
        await using var db = new NpgsqlConnection(connectionString); await db.OpenAsync();
        var root = Guid.NewGuid(); var tenant = Guid.NewGuid(); var smallRole = Guid.NewGuid();
        var users = new List<Guid> { root };
        var issuer = "https://identity-budget.example/" + root.ToString("N");
        var actor = new SubjectIdentity(issuer, root.ToString());
        var store = new PostgresIdentityManagementStore(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Platform"] = connectionString }).Build());
        static Task<bool> Current(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(true); }
        try
        {
            await Execute(db, "INSERT INTO public.platform_users(user_id,issuer,subject,display_name) VALUES($1,$2,$3,'budget admin')", root, issuer, root.ToString());
            await Execute(db, "INSERT INTO public.platform_identity_administrators(user_id) VALUES($1)", root);
            await Execute(db, "INSERT INTO public.platform_tenants(tenant_id,name) VALUES($1,'budget tenant')", tenant);
            Success(await store.SaveRoleAsync(actor, tenant, null, smallRole, new("editable role", true, [], 0), "budget-small-role", Current, default));

            var longRole = new SaveIdentityRole("large role", true, Grants(unicode: false), 0);
            var unicodeRole = new SaveIdentityRole("Unicode role", true, Grants(unicode: true), 1);
            Assert(JsonSerializer.SerializeToUtf8Bytes(longRole, WireJson).Length < 65536
                && JsonSerializer.SerializeToUtf8Bytes(unicodeRole, WireJson).Length < 65536,
                "Each large role mutation is independently below the public 64KiB request limit.");
            var denied = false;
            for (var index = 0; index < 80; index++)
            {
                var roleId = Guid.NewGuid();
                var before = await AuditCount(db, root);
                var result = await store.SaveRoleAsync(actor, tenant, null, roleId, longRole with { Name = "large role " + index }, "budget-long-role", Current, default);
                if (result.Error == IdentityError.Capacity)
                {
                    denied = true;
                    Assert(await Count(db, "SELECT count(*) FROM public.tenant_roles WHERE tenant_id=$1 AND role_id=$2", tenant, roleId) == 0
                        && await AuditCount(db, root) == before, "Aggregated budget rejection rolls back the role and success audit.");
                    break;
                }
                Success(result);
            }
            Assert(denied, "Eighty individually valid 100-grant roles exceed the 4MiB client snapshot; a mutation must be rejected before that state is committed.");
            var snapshot = (await store.ReadTenantAsync(actor, tenant, null, default)).Value;
            Assert(snapshot is not null && JsonSerializer.SerializeToUtf8Bytes(snapshot, WireJson).Length <= MaximumResponseBytes,
                "The last committed tenant snapshot remains readable within the client response budget.");
            var auditBeforeEdit = await AuditCount(db, root);
            var edit = await store.SaveRoleAsync(actor, tenant, null, smallRole, unicodeRole, "budget-large-edit", Current, default);
            Assert(edit.Error == IdentityError.Capacity, "Editing a small existing role cannot bypass the aggregate response budget.");
            Assert(await Count(db, "SELECT identity_revision FROM public.tenant_roles WHERE tenant_id=$1 AND role_id=$2", tenant, smallRole) == 1
                && await AuditCount(db, root) == auditBeforeEdit, "An over-budget edit rolls back revision, grants and audit.");
            await Execute(db, "DELETE FROM public.tenant_roles WHERE tenant_id=$1 AND role_id<>$2", tenant, smallRole);
            Success(await store.SaveRoleAsync(actor, tenant, null, smallRole, unicodeRole, "budget-unicode-valid", Current, default));
            var unicodeSnapshot = (await store.ReadTenantAsync(actor, tenant, null, default)).Value;
            Assert(unicodeSnapshot is not null && unicodeSnapshot.Roles.Single().Grants.Count == 100
                && JsonSerializer.SerializeToUtf8Bytes(unicodeSnapshot).Length < MaximumResponseBytes,
                "Unicode resource grants survive readback and both escaped and unescaped JSON stay bounded.");

            // 500 entity counts alone cannot bound long Unicode identity profiles.
            var prefix = "https://identity-budget.example/";
            var longIssuer = prefix + new string('界', 2048 - prefix.Length);
            denied = false;
            for (var index = 0; index < 100; index++)
            {
                var id = Guid.NewGuid(); users.Add(id);
                var profile = new SaveIdentityUser(longIssuer, id + new string('组', 476), new string('员', 256), true, false, 0);
                Assert(IdentityManagementValidation.Valid(profile), "Unicode fixture uses valid bounded scalar identity fields.");
                var before = await AuditCount(db, root);
                var result = await store.SaveUserAsync(actor, id, profile, "budget-unicode-user", Current, default);
                if (result.Error == IdentityError.Capacity)
                {
                    denied = true;
                    Assert(await Count(db, "SELECT count(*) FROM public.platform_users WHERE user_id=$1", id) == 0 && await AuditCount(db, root) == before,
                        "Global Unicode budget rejection leaves no user or success audit.");
                    break;
                }
                Success(result);
            }
            Assert(denied, "Global workspace mutations enforce the Unicode-safe aggregate budget below the count cap.");
            var global = (await store.ReadWorkspaceAsync(actor, default)).Value;
            Assert(global is not null && JsonSerializer.SerializeToUtf8Bytes(global).Length <= MaximumResponseBytes
                && JsonSerializer.SerializeToUtf8Bytes(global, WireJson).Length <= MaximumResponseBytes, "Escaped and UTF8 global responses remain below 4MiB.");
            Console.WriteLine("PASS: identity aggregate role/profile response budgets, sub-64KiB mutations, Unicode, edit rollback and readable snapshots below 4MiB.");
        }
        finally
        {
            await Execute(db, "DELETE FROM public.platform_identity_audit WHERE actor_user_id=$1", root);
            await Execute(db, "DELETE FROM public.platform_tenants WHERE tenant_id=$1", tenant);
            await Execute(db, "DELETE FROM public.platform_users WHERE user_id=ANY($1)", users.ToArray());
        }
    }
    private static IdentityGrant[] Grants(bool unicode) => Enumerable.Range(0, 100).Select(index => new IdentityGrant(
        "p" + new string('a', 127), "r" + new string('a', 63),
        unicode ? new string('资', 50) + new string('x', 147) + index.ToString("D3") : new string('x', 253) + index.ToString("D3"), true, [])).ToArray();
    private static Task<long> AuditCount(NpgsqlConnection db, Guid actor) => Count(db, "SELECT count(*) FROM public.platform_identity_audit WHERE actor_user_id=$1", actor);
    private static void Success(IdentityResult<IdentityRevision> result) => Assert(result.Error == IdentityError.None, "Expected an allowed mutation, got " + result.Error);
    private static async Task Execute(NpgsqlConnection db, string sql, params object[] values)
    { await using var command = Command(db, sql, values); await command.ExecuteNonQueryAsync(); }
    private static async Task<long> Count(NpgsqlConnection db, string sql, params object[] values)
    { await using var command = Command(db, sql, values); return (long)(await command.ExecuteScalarAsync())!; }
    private static NpgsqlCommand Command(NpgsqlConnection db, string sql, params object[] values)
    { var command = new NpgsqlCommand(sql, db); foreach (var value in values) command.Parameters.AddWithValue(value); return command; }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
