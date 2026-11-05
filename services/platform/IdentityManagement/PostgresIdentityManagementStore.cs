using System.Text.Json;
using AssembleAI.Platform.Authorization;
using Npgsql;

namespace AssembleAI.Platform.IdentityManagement;

/// <summary>Identity administration is an explicit authority, never inferred from business grants.</summary>
public sealed class PostgresIdentityManagementStore(IConfiguration configuration) : IIdentityManagementStore
{
    public const int MaximumItems = 500;
    // Reads and mutations use the same lock order. External SQL writers are also fenced;
    // an advisory lock alone cannot protect a concurrent DBA revocation.
    private const string Tables = "public.platform_users,public.platform_tenants,public.tenant_memberships,public.tenant_organizations,public.tenant_roles,public.tenant_member_roles,public.role_permission_grants,public.grant_organizations,public.platform_identity_administrators,public.tenant_identity_administrators";

    public Task<IdentityResult<IdentityWorkspace>> ReadWorkspaceAsync(SubjectIdentity subject, CancellationToken token) => Run(false, async (db, ct) =>
    {
        var actor = await Actor(db, subject, ct);
        if (actor is null) return Fail<IdentityWorkspace>(IdentityError.Forbidden);
        if (actor.Global && !await IdentityResponseBudget.FitsGlobalAsync(db, ct)) throw new IdentityManagementUnavailableException();
        var tenants = await Read(db, """
            SELECT t.tenant_id,t.name,t.enabled,t.identity_revision FROM public.platform_tenants t
            WHERE $1 OR (t.enabled AND EXISTS(SELECT 1 FROM public.tenant_memberships m
                JOIN public.tenant_identity_administrators a USING(tenant_id,user_id)
                WHERE m.tenant_id=t.tenant_id AND m.user_id=$2 AND m.enabled AND a.enabled))
            ORDER BY t.name,t.tenant_id LIMIT 501
            """, r => new IdentityTenant(r.GetGuid(0), r.GetString(1), r.GetBoolean(2), r.GetInt64(3)), ct, actor.Global, actor.Id);
        if (!actor.Global && tenants.Count == 0) return Fail<IdentityWorkspace>(IdentityError.Forbidden);
        IReadOnlyList<IdentityUser> users = actor.Global ? await Read(db, """
            SELECT u.user_id,u.issuer,u.subject,u.display_name,u.enabled,
                EXISTS(SELECT 1 FROM public.platform_identity_administrators a WHERE a.user_id=u.user_id AND a.enabled),u.identity_revision
            FROM public.platform_users u ORDER BY u.display_name,u.user_id LIMIT 501
            """, r => new IdentityUser(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetBoolean(5), r.GetInt64(6)), ct) : [];
        var audit = actor.Global ? await Read(db, """
            SELECT event_id,actor_user_id,tenant_id,entity_type,entity_id,action,result,request_id,occurred_at
            FROM public.platform_identity_audit WHERE tenant_id IS NULL ORDER BY occurred_at DESC,event_id DESC LIMIT 51
            """, r => new IdentityAudit(r.GetGuid(0), r.GetGuid(1), null, r.GetString(3), r.GetGuid(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetFieldValue<DateTimeOffset>(8)), ct) : [];
        return IdentityResult<IdentityWorkspace>.Success(new(actor.Id, actor.Global, tenants, users, new(audit.Take(50).ToArray(), audit.Count > 50)));
    }, token);

    public Task<IdentityResult<IdentityTenantWorkspace>> ReadTenantAsync(SubjectIdentity subject, Guid tenant, Guid? selectedTenant, CancellationToken token) => Run(false, async (db, ct) =>
    {
        var actor = await TenantActor(db, subject, tenant, selectedTenant, ct);
        if (actor is null) return Fail<IdentityTenantWorkspace>(IdentityError.Forbidden);
        if (!await IdentityResponseBudget.FitsTenantAsync(db, tenant, ct)) throw new IdentityManagementUnavailableException();
        var tenants = await Read(db, "SELECT tenant_id,name,enabled,identity_revision FROM public.platform_tenants WHERE tenant_id=$1",
            r => new IdentityTenant(r.GetGuid(0), r.GetString(1), r.GetBoolean(2), r.GetInt64(3)), ct, tenant);
        if (tenants.Count == 0) return Fail<IdentityTenantWorkspace>(IdentityError.NotFound);
        var members = await Read(db, """
            SELECT m.user_id,u.display_name,m.enabled,u.enabled,
                EXISTS(SELECT 1 FROM public.tenant_identity_administrators a WHERE a.tenant_id=m.tenant_id AND a.user_id=m.user_id AND a.enabled),
                ARRAY(SELECT mr.role_id FROM public.tenant_member_roles mr WHERE mr.tenant_id=m.tenant_id AND mr.user_id=m.user_id ORDER BY mr.role_id),m.identity_revision
            FROM public.tenant_memberships m JOIN public.platform_users u USING(user_id)
            WHERE m.tenant_id=$1 ORDER BY u.display_name,m.user_id LIMIT 501
            """, r => new IdentityMember(r.GetGuid(0), r.GetString(1), r.GetBoolean(2), r.GetBoolean(3), r.GetBoolean(4), r.GetFieldValue<Guid[]>(5), r.GetInt64(6)), ct, tenant);
        if (members.Any(member => member.RoleIds.Count > 100)) throw new IdentityManagementUnavailableException();
        var organizations = await Read(db, "SELECT organization_id,name,kind,enabled,identity_revision FROM public.tenant_organizations WHERE tenant_id=$1 ORDER BY name,organization_id LIMIT 501",
            r => new IdentityOrganization(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetInt64(4)), ct, tenant);
        var roles = await Read(db, "SELECT role_id,name,enabled,identity_revision FROM public.tenant_roles WHERE tenant_id=$1 ORDER BY name,role_id LIMIT 501",
            r => new IdentityRole(r.GetGuid(0), r.GetString(1), r.GetBoolean(2), r.GetInt64(3), []), ct, tenant);
        var completeRoles = new List<IdentityRole>();
        foreach (var role in roles)
        {
            var grants = await Read(db, """
                SELECT g.permission,g.resource_type,g.resource_id,g.tenant_wide,
                    ARRAY(SELECT o.organization_id FROM public.grant_organizations o WHERE o.tenant_id=g.tenant_id AND o.grant_id=g.grant_id ORDER BY o.organization_id)
                FROM public.role_permission_grants g WHERE g.tenant_id=$1 AND g.role_id=$2 AND g.enabled ORDER BY g.permission,g.resource_type,g.resource_id LIMIT 101
                """, r => new IdentityGrant(r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetFieldValue<Guid[]>(4)), ct, tenant, role.RoleId);
            if (grants.Count > 100 || grants.Any(grant => grant.OrganizationIds.Count > 100)) throw new IdentityManagementUnavailableException();
            completeRoles.Add(role with { Grants = grants });
        }
        var audit = await Read(db, """
            SELECT event_id,actor_user_id,tenant_id,entity_type,entity_id,action,result,request_id,occurred_at
            FROM public.platform_identity_audit WHERE tenant_id=$1 ORDER BY occurred_at DESC,event_id DESC LIMIT 51
            """, r => new IdentityAudit(r.GetGuid(0), r.GetGuid(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.GetString(3), r.GetGuid(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetFieldValue<DateTimeOffset>(8)), ct, tenant);
        return IdentityResult<IdentityTenantWorkspace>.Success(new(actor.Id, actor.Global, tenants[0], members, organizations, completeRoles,
            new(audit.Take(50).ToArray(), audit.Count > 50)));
    }, token);

    public Task<IdentityResult<IdentityRevision>> SaveUserAsync(SubjectIdentity subject, Guid id, SaveIdentityUser value, string requestId, Func<CancellationToken, Task<bool>> currentSession, CancellationToken token) => Run(true, async (db, ct) =>
    {
        if (!await currentSession(ct)) return Fail<IdentityRevision>(IdentityError.Forbidden);
        var actor = await Actor(db, subject, ct);
        if (actor?.Global != true) return Fail<IdentityRevision>(IdentityError.Forbidden);
        if (id == Guid.Empty || !IdentityManagementValidation.Valid(value)) return Fail<IdentityRevision>(IdentityError.Invalid);
        var existing = await Read(db, "SELECT identity_revision,issuer,subject FROM public.platform_users WHERE user_id=$1", r => (Revision: r.GetInt64(0), Issuer: r.GetString(1), Subject: r.GetString(2)), ct, id);
        var revision = existing.Count == 0 ? 0 : existing[0].Revision;
        if (revision != value.ExpectedRevision) return Fail<IdentityRevision>(IdentityError.Conflict);
        if (revision == 0 && await Count(db, "SELECT count(*) FROM public.platform_users", ct) >= MaximumItems)
            return Fail<IdentityRevision>(IdentityError.Capacity);
        if (existing.Count > 0 && (existing[0].Issuer != value.Issuer || existing[0].Subject != value.Subject)) return Fail<IdentityRevision>(IdentityError.Invalid);
        if (await Exists(db, "SELECT EXISTS(SELECT 1 FROM public.platform_users WHERE issuer=$1 AND subject=$2 AND user_id<>$3)", ct, value.Issuer, value.Subject, id)) return Fail<IdentityRevision>(IdentityError.Conflict);
        if ((!value.Enabled || !value.PlatformAdministrator) && await Exists(db, """
            SELECT EXISTS(SELECT 1 FROM public.platform_identity_administrators a JOIN public.platform_users u USING(user_id) WHERE a.user_id=$1 AND a.enabled AND u.enabled)
                AND NOT EXISTS(SELECT 1 FROM public.platform_identity_administrators a JOIN public.platform_users u USING(user_id) WHERE a.user_id<>$1 AND a.enabled AND u.enabled)
            """, ct, id)) return Fail<IdentityRevision>(IdentityError.Conflict);
        await Write(db, """
            INSERT INTO public.platform_users(user_id,issuer,subject,display_name,enabled) VALUES($1,$2,$3,$4,$5)
            ON CONFLICT(user_id) DO UPDATE SET display_name=EXCLUDED.display_name,enabled=EXCLUDED.enabled,identity_revision=platform_users.identity_revision+1
            """, ct, id, value.Issuer, value.Subject, value.DisplayName, value.Enabled);
        await Write(db, "INSERT INTO public.platform_identity_administrators(user_id,enabled) VALUES($1,$2) ON CONFLICT(user_id) DO UPDATE SET enabled=EXCLUDED.enabled", ct, id, value.PlatformAdministrator);
        if (!await IdentityResponseBudget.FitsGlobalAsync(db, ct)) throw new IdentityCapacityExceededException();
        await Audit(db, actor.Id, null, "user", id, revision == 0 ? "create" : "update", requestId, ct);
        return IdentityResult<IdentityRevision>.Success(new(revision + 1));
    }, token);

    public Task<IdentityResult<IdentityRevision>> SaveTenantAsync(SubjectIdentity subject, Guid id, SaveIdentityTenant value, string requestId, Func<CancellationToken, Task<bool>> currentSession, CancellationToken token) => Run(true, async (db, ct) =>
    {
        if (!await currentSession(ct)) return Fail<IdentityRevision>(IdentityError.Forbidden);
        var actor = await Actor(db, subject, ct);
        if (actor?.Global != true) return Fail<IdentityRevision>(IdentityError.Forbidden);
        if (id == Guid.Empty || !IdentityManagementValidation.Valid(value)) return Fail<IdentityRevision>(IdentityError.Invalid);
        var revision = await Revision(db, "platform_tenants", "tenant_id", id, null, ct);
        if (revision != value.ExpectedRevision) return Fail<IdentityRevision>(IdentityError.Conflict);
        if (revision == 0 && await Count(db, "SELECT count(*) FROM public.platform_tenants", ct) >= MaximumItems)
            return Fail<IdentityRevision>(IdentityError.Capacity);
        await Write(db, """
            INSERT INTO public.platform_tenants(tenant_id,name,enabled) VALUES($1,$2,$3)
            ON CONFLICT(tenant_id) DO UPDATE SET name=EXCLUDED.name,enabled=EXCLUDED.enabled,identity_revision=platform_tenants.identity_revision+1
            """, ct, id, value.Name, value.Enabled);
        if (!await IdentityResponseBudget.FitsGlobalAsync(db, ct)) throw new IdentityCapacityExceededException();
        await Audit(db, actor.Id, id, "tenant", id, revision == 0 ? "create" : "update", requestId, ct);
        return IdentityResult<IdentityRevision>.Success(new(revision + 1));
    }, token);

    public Task<IdentityResult<IdentityRevision>> SaveMemberAsync(SubjectIdentity subject, Guid tenant, Guid? selectedTenant, Guid id, SaveIdentityMember value, string requestId, Func<CancellationToken, Task<bool>> currentSession, CancellationToken token) => Run(true, async (db, ct) =>
    {
        if (!await currentSession(ct)) return Fail<IdentityRevision>(IdentityError.Forbidden);
        var actor = await TenantActor(db, subject, tenant, selectedTenant, ct);
        if (actor is null) return Fail<IdentityRevision>(IdentityError.Forbidden);
        if (id == Guid.Empty || !IdentityManagementValidation.Valid(value)) return Fail<IdentityRevision>(IdentityError.Invalid);
        var revision = await Revision(db, "tenant_memberships", "user_id", id, tenant, ct);
        var administrator = await Exists(db, "SELECT EXISTS(SELECT 1 FROM public.tenant_identity_administrators WHERE tenant_id=$1 AND user_id=$2 AND enabled)", ct, tenant, id);
        if (!actor.Global && value.Administrator != administrator) return Fail<IdentityRevision>(IdentityError.Forbidden);
        if (revision != value.ExpectedRevision) return Fail<IdentityRevision>(IdentityError.Conflict);
        if (revision == 0 && await Count(db, "SELECT count(*) FROM public.tenant_memberships WHERE tenant_id=$1", ct, tenant) >= MaximumItems)
            return Fail<IdentityRevision>(IdentityError.Capacity);
        if (!await Exists(db, "SELECT EXISTS(SELECT 1 FROM public.platform_users WHERE user_id=$1)", ct, id)
            || !await Exists(db, "SELECT EXISTS(SELECT 1 FROM public.platform_tenants WHERE tenant_id=$1)", ct, tenant)) return Fail<IdentityRevision>(IdentityError.Invalid);
        if (await Count(db, "SELECT count(*) FROM public.tenant_roles WHERE tenant_id=$1 AND role_id=ANY($2)", ct, tenant, value.RoleIds.ToArray()) != value.RoleIds.Count)
            return Fail<IdentityRevision>(IdentityError.Invalid);
        await Write(db, """
            INSERT INTO public.tenant_memberships(tenant_id,user_id,enabled) VALUES($1,$2,$3)
            ON CONFLICT(tenant_id,user_id) DO UPDATE SET enabled=EXCLUDED.enabled,identity_revision=tenant_memberships.identity_revision+1
            """, ct, tenant, id, value.Enabled);
        if (actor.Global)
            await Write(db, "INSERT INTO public.tenant_identity_administrators(tenant_id,user_id,enabled) VALUES($1,$2,$3) ON CONFLICT(tenant_id,user_id) DO UPDATE SET enabled=EXCLUDED.enabled", ct, tenant, id, value.Administrator);
        await Write(db, "DELETE FROM public.tenant_member_roles WHERE tenant_id=$1 AND user_id=$2", ct, tenant, id);
        foreach (var role in value.RoleIds) await Write(db, "INSERT INTO public.tenant_member_roles(tenant_id,user_id,role_id) VALUES($1,$2,$3)", ct, tenant, id, role);
        if (!await IdentityResponseBudget.FitsTenantAsync(db, tenant, ct)) throw new IdentityCapacityExceededException();
        await Audit(db, actor.Id, tenant, "member", id, revision == 0 ? "create" : "update", requestId, ct);
        return IdentityResult<IdentityRevision>.Success(new(revision + 1));
    }, token);

    public Task<IdentityResult<IdentityRevision>> SaveOrganizationAsync(SubjectIdentity subject, Guid tenant, Guid? selectedTenant, Guid id, SaveIdentityOrganization value, string requestId, Func<CancellationToken, Task<bool>> currentSession, CancellationToken token) => Run(true, async (db, ct) =>
    {
        if (!await currentSession(ct)) return Fail<IdentityRevision>(IdentityError.Forbidden);
        var actor = await TenantActor(db, subject, tenant, selectedTenant, ct);
        if (actor is null) return Fail<IdentityRevision>(IdentityError.Forbidden);
        if (id == Guid.Empty || !IdentityManagementValidation.Valid(value)) return Fail<IdentityRevision>(IdentityError.Invalid);
        var revision = await Revision(db, "tenant_organizations", "organization_id", id, tenant, ct);
        if (revision != value.ExpectedRevision) return Fail<IdentityRevision>(IdentityError.Conflict);
        if (revision == 0 && await Count(db, "SELECT count(*) FROM public.tenant_organizations WHERE tenant_id=$1", ct, tenant) >= MaximumItems)
            return Fail<IdentityRevision>(IdentityError.Capacity);
        if (!await Exists(db, "SELECT EXISTS(SELECT 1 FROM public.platform_tenants WHERE tenant_id=$1)", ct, tenant)) return Fail<IdentityRevision>(IdentityError.NotFound);
        await Write(db, """
            INSERT INTO public.tenant_organizations(tenant_id,organization_id,name,kind,enabled) VALUES($1,$2,$3,$4,$5)
            ON CONFLICT(tenant_id,organization_id) DO UPDATE SET name=EXCLUDED.name,kind=EXCLUDED.kind,enabled=EXCLUDED.enabled,identity_revision=tenant_organizations.identity_revision+1
            """, ct, tenant, id, value.Name, value.Kind, value.Enabled);
        if (!await IdentityResponseBudget.FitsTenantAsync(db, tenant, ct)) throw new IdentityCapacityExceededException();
        await Audit(db, actor.Id, tenant, "organization", id, revision == 0 ? "create" : "update", requestId, ct);
        return IdentityResult<IdentityRevision>.Success(new(revision + 1));
    }, token);

    public Task<IdentityResult<IdentityRevision>> SaveRoleAsync(SubjectIdentity subject, Guid tenant, Guid? selectedTenant, Guid id, SaveIdentityRole value, string requestId, Func<CancellationToken, Task<bool>> currentSession, CancellationToken token) => Run(true, async (db, ct) =>
    {
        if (!await currentSession(ct)) return Fail<IdentityRevision>(IdentityError.Forbidden);
        var actor = await TenantActor(db, subject, tenant, selectedTenant, ct);
        if (actor is null) return Fail<IdentityRevision>(IdentityError.Forbidden);
        if (id == Guid.Empty || !IdentityManagementValidation.Valid(value)) return Fail<IdentityRevision>(IdentityError.Invalid);
        var revision = await Revision(db, "tenant_roles", "role_id", id, tenant, ct);
        if (revision != value.ExpectedRevision) return Fail<IdentityRevision>(IdentityError.Conflict);
        if (revision == 0 && await Count(db, "SELECT count(*) FROM public.tenant_roles WHERE tenant_id=$1", ct, tenant) >= MaximumItems)
            return Fail<IdentityRevision>(IdentityError.Capacity);
        if (!await Exists(db, "SELECT EXISTS(SELECT 1 FROM public.platform_tenants WHERE tenant_id=$1)", ct, tenant)) return Fail<IdentityRevision>(IdentityError.NotFound);
        if (await Exists(db, "SELECT EXISTS(SELECT 1 FROM public.tenant_roles WHERE tenant_id=$1 AND name=$2 AND role_id<>$3)", ct, tenant, value.Name, id)) return Fail<IdentityRevision>(IdentityError.Conflict);
        var organizations = value.Grants.SelectMany(grant => grant.OrganizationIds).Distinct().ToArray();
        if (await Count(db, "SELECT count(*) FROM public.tenant_organizations WHERE tenant_id=$1 AND organization_id=ANY($2) AND enabled", ct, tenant, organizations) != organizations.Length)
            return Fail<IdentityRevision>(IdentityError.Invalid);
        await Write(db, """
            INSERT INTO public.tenant_roles(tenant_id,role_id,name,enabled) VALUES($1,$2,$3,$4)
            ON CONFLICT(tenant_id,role_id) DO UPDATE SET name=EXCLUDED.name,enabled=EXCLUDED.enabled,identity_revision=tenant_roles.identity_revision+1
            """, ct, tenant, id, value.Name, value.Enabled);
        await Write(db, "DELETE FROM public.role_permission_grants WHERE tenant_id=$1 AND role_id=$2", ct, tenant, id);
        foreach (var grant in value.Grants)
        {
            var grantId = Guid.NewGuid();
            await Write(db, "INSERT INTO public.role_permission_grants(tenant_id,grant_id,role_id,permission,resource_type,resource_id,tenant_wide) VALUES($1,$2,$3,$4,$5,$6,$7)",
                ct, tenant, grantId, id, grant.Permission, grant.ResourceType, grant.ResourceId, grant.TenantWide);
            foreach (var organization in grant.OrganizationIds)
                await Write(db, "INSERT INTO public.grant_organizations(tenant_id,grant_id,organization_id) VALUES($1,$2,$3)", ct, tenant, grantId, organization);
        }
        if (!await IdentityResponseBudget.FitsTenantAsync(db, tenant, ct)) throw new IdentityCapacityExceededException();
        await Audit(db, actor.Id, tenant, "role", id, revision == 0 ? "create" : "update", requestId, ct);
        return IdentityResult<IdentityRevision>.Success(new(revision + 1));
    }, token);

    private static async Task<IdentityActor?> Actor(NpgsqlConnection db, SubjectIdentity subject, CancellationToken ct)
    {
        var actors = await Read(db, """
            SELECT u.user_id,EXISTS(SELECT 1 FROM public.platform_identity_administrators a WHERE a.user_id=u.user_id AND a.enabled)
            FROM public.platform_users u WHERE u.issuer=$1 AND u.subject=$2 AND u.enabled
            """, r => new IdentityActor(r.GetGuid(0), r.GetBoolean(1)), ct, subject.Issuer, subject.Subject);
        return actors.FirstOrDefault();
    }
    private static async Task<IdentityActor?> TenantActor(NpgsqlConnection db, SubjectIdentity subject, Guid tenant, Guid? selected, CancellationToken ct)
    {
        if (tenant == Guid.Empty) return null;
        var actor = await Actor(db, subject, ct);
        if (actor?.Global == true) return actor;
        if (actor is null || selected != tenant) return null;
        return await Exists(db, """
            SELECT EXISTS(SELECT 1 FROM public.tenant_identity_administrators a
                JOIN public.tenant_memberships m USING(tenant_id,user_id) JOIN public.platform_tenants t USING(tenant_id)
                WHERE a.tenant_id=$1 AND a.user_id=$2 AND a.enabled AND m.enabled AND t.enabled)
            """, ct, tenant, actor.Id) ? actor : null;
    }
    private async Task<IdentityResult<T>> Run<T>(bool write, Func<NpgsqlConnection, CancellationToken, Task<IdentityResult<T>>> operation, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var ct = timeout.Token;
        try
        {
            var value = configuration.GetConnectionString("Platform");
            if (string.IsNullOrWhiteSpace(value)) throw new IdentityManagementUnavailableException();
            var options = new NpgsqlConnectionStringBuilder(value) { Timeout = 3, CommandTimeout = 3, IncludeErrorDetail = false };
            await using var db = new NpgsqlConnection(options.ConnectionString);
            await db.OpenAsync(ct);
            await using var tx = await db.BeginTransactionAsync(ct);
            await Write(db, "SET LOCAL lock_timeout='2s'; SET LOCAL statement_timeout='3s'", ct);
            await Write(db, "LOCK TABLE " + Tables + (write ? " IN SHARE ROW EXCLUSIVE MODE" : " IN SHARE MODE"), ct);
            var result = await operation(db, ct);
            await tx.CommitAsync(ct);
            return result;
        }
        // The transaction has been disposed (rolled back) before this catch returns
        // Capacity; an edit's rows, authorization epochs and audit all stay unchanged.
        catch (IdentityCapacityExceededException) { return IdentityResult<T>.Failure(IdentityError.Capacity); }
        catch (Exception error) when (!token.IsCancellationRequested && error is NpgsqlException or IOException or TimeoutException or InvalidOperationException or ArgumentException or OperationCanceledException or JsonException)
        { throw new IdentityManagementUnavailableException(); }
    }
    private static NpgsqlCommand Command(NpgsqlConnection db, string sql, params object[] values)
    {
        var command = new NpgsqlCommand(sql, db) { CommandTimeout = 3 };
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return command;
    }
    private static async Task<IReadOnlyList<T>> Read<T>(NpgsqlConnection db, string sql, Func<NpgsqlDataReader,T> map, CancellationToken ct, params object[] values)
    {
        await using var command = Command(db, sql, values);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var results = new List<T>();
        while (await reader.ReadAsync(ct))
        {
            if (results.Count == MaximumItems) throw new IdentityManagementUnavailableException();
            results.Add(map(reader));
        }
        return results;
    }
    private static async Task Write(NpgsqlConnection db, string sql, CancellationToken ct, params object[] values)
    { await using var command = Command(db, sql, values); await command.ExecuteNonQueryAsync(ct); }
    private static async Task<bool> Exists(NpgsqlConnection db, string sql, CancellationToken ct, params object[] values)
    { await using var command = Command(db, sql, values); return await command.ExecuteScalarAsync(ct) is true; }
    private static async Task<long> Count(NpgsqlConnection db, string sql, CancellationToken ct, params object[] values)
    { await using var command = Command(db, sql, values); return (long)(await command.ExecuteScalarAsync(ct))!; }
    private static async Task<long> Revision(NpgsqlConnection db, string table, string key, Guid id, Guid? tenant, CancellationToken ct)
    {
        // table/key are internal constants, never identifiers from the request.
        await using var command = Command(db, $"SELECT identity_revision FROM public.{table} WHERE {key}=$1" + (tenant.HasValue ? " AND tenant_id=$2" : ""), tenant.HasValue ? [id, tenant.Value] : [id]);
        return await command.ExecuteScalarAsync(ct) is long revision ? revision : 0;
    }
    private static Task Audit(NpgsqlConnection db, Guid actor, Guid? tenant, string type, Guid id, string action, string requestId, CancellationToken ct) =>
        Write(db, "INSERT INTO public.platform_identity_audit(event_id,actor_user_id,tenant_id,entity_type,entity_id,action,result,request_id) VALUES($1,$2,$3,$4,$5,$6,'success',$7)",
            ct, Guid.NewGuid(), actor, tenant.HasValue ? tenant.Value : DBNull.Value, type, id, action, requestId);
    private static IdentityResult<T> Fail<T>(IdentityError error) => IdentityResult<T>.Failure(error);
    private sealed record IdentityActor(Guid Id, bool Global);
}
