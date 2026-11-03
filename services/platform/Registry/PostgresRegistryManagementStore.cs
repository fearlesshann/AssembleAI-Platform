using System.Text.Json;
using AssembleAI.Platform.Authorization;
using Npgsql;

namespace AssembleAI.Platform.Registry;

/// <summary>Bounded management reads. Global operators never inherit tenant audit access.</summary>
public sealed class PostgresRegistryManagementStore(IConfiguration configuration, IAuthorizationStore authorization) : IRegistryManagementStore
{
    public const int MaximumApplications = 500;
    public const int MaximumReleases = 100;
    public const int AuditPageSize = 50;

    public Task<RegistryResult<RegistryWorkspace>> ReadWorkspaceAsync(SubjectIdentity subject, CancellationToken token) => Run(async (db, ct) =>
    {
        var actor = await ReadActor(db, subject, ct);
        if (actor is null) return Denied<RegistryWorkspace>();
        var items = new List<RegistryWorkspaceApplication>();
        await using (var command = Command(db, """
            SELECT app_id,name,category,owner_user_id FROM public.platform_applications
            WHERE $1 OR ($2 AND owner_user_id=$3) ORDER BY app_id COLLATE "C" LIMIT 501
            """, actor.Reviewer, actor.Developer, actor.Id))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                if (items.Count == MaximumApplications) throw new RegistryStoreUnavailableException();
                var owner = reader.GetGuid(3);
                items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), owner,
                    actor.Developer && owner == actor.Id, actor.Reviewer));
            }
        if (actor != await ReadActor(db, subject, ct)) return Denied<RegistryWorkspace>();
        return RegistryResult<RegistryWorkspace>.Success(new(actor.Id, actor.Developer, actor.Reviewer, items));
    }, token);

    public Task<RegistryResult<TenantRegistryWorkspace>> ReadTenantWorkspaceAsync(SubjectIdentity subject, Guid tenantId, CancellationToken token) => Run(async (db, ct) =>
    {
        var access = await authorization.ReadAccessAsync(subject, tenantId, ct);
        if (!ValidAccess(access, tenantId)) return Denied<TenantRegistryWorkspace>();
        var ids = access!.Grants.Where(grant => grant.Permission == "registry:manage" && grant.ResourceType == "application"
                && grant.TenantWide && ManifestValidator.IsAppId(grant.ResourceId))
            .Select(grant => grant.ResourceId).Distinct(StringComparer.Ordinal).ToArray();
        var items = new List<TenantRegistryApplication>();
        if (ids.Length > 0)
        {
            await using var command = Command(db, """
                SELECT a.app_id,COALESCE(r.manifest->>'name',p.manifest->>'name'),
                    COALESCE(r.manifest->>'category',p.manifest->>'category'),t.release_version,t.enabled,t.revision,r.status
                FROM public.platform_applications a
                LEFT JOIN public.tenant_applications t ON t.app_id=a.app_id AND t.tenant_id=$1
                LEFT JOIN public.platform_application_releases r ON r.app_id=t.app_id AND r.release_version=t.release_version
                LEFT JOIN LATERAL (SELECT manifest FROM public.platform_application_releases
                    WHERE app_id=a.app_id AND status='published' ORDER BY release_version COLLATE "C" DESC LIMIT 1) p ON true
                WHERE a.app_id=ANY($2) AND (t.app_id IS NOT NULL OR p.manifest IS NOT NULL)
                ORDER BY a.app_id COLLATE "C" LIMIT 501
                """, tenantId, ids);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (items.Count == MaximumApplications) throw new RegistryStoreUnavailableException();
                TenantRegistryPointer? current = reader.IsDBNull(3) ? null : new(reader.GetString(3), reader.GetBoolean(4),
                    reader.GetBoolean(4) && reader.GetString(6) == "published", reader.GetInt64(5));
                items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), current));
            }
        }
        if (!await SameAccess(subject, access, ct)) return Denied<TenantRegistryWorkspace>();
        return RegistryResult<TenantRegistryWorkspace>.Success(new(access.UserId, tenantId, items));
    }, token);

    public Task<RegistryResult<IReadOnlyList<ApplicationReleaseView>>> ReadTenantReleasesAsync(SubjectIdentity subject, Guid tenantId,
        string appId, CancellationToken token) => Run(async (db, ct) =>
    {
        var access = await TenantActor(subject, tenantId, appId, ct);
        if (access is null) return Denied<IReadOnlyList<ApplicationReleaseView>>();
        var owner = await Owner(db, appId, ct);
        if (owner is null) return RegistryResult<IReadOnlyList<ApplicationReleaseView>>.Failure(RegistryError.NotFound);
        var items = new List<ApplicationReleaseView>();
        await using (var command = Command(db, """
            SELECT manifest,revision,submitted_by,published_at FROM public.platform_application_releases
            WHERE app_id=$1 AND status='published' ORDER BY release_version COLLATE "C" LIMIT 101
            """, appId))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                if (items.Count == MaximumReleases) throw new RegistryStoreUnavailableException();
                using var json = JsonDocument.Parse(reader.GetString(0));
                if (!ManifestValidator.TryParse(json.RootElement, out var manifest)) throw new RegistryStoreUnavailableException();
                items.Add(new(manifest! with { Status = "published" }, reader.GetInt64(1), owner.Value,
                    reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3)));
            }
        if (!await SameAccess(subject, access, ct)) return Denied<IReadOnlyList<ApplicationReleaseView>>();
        return RegistryResult<IReadOnlyList<ApplicationReleaseView>>.Success(items);
    }, token);

    public Task<RegistryResult<RegistryAuditPage>> ReadAuditAsync(SubjectIdentity subject, Guid? tenantId, string appId,
        CancellationToken token) => Run(async (db, ct) =>
    {
        Actor? actor = null;
        AccessSnapshot? access = null;
        if (tenantId is { } tenant)
        {
            access = await TenantActor(subject, tenant, appId, ct);
            if (access is null) return Denied<RegistryAuditPage>();
        }
        else
        {
            actor = await ReadActor(db, subject, ct);
            var owner = await Owner(db, appId, ct);
            if (actor is null || !(actor.Reviewer || actor.Developer && owner == actor.Id)) return Denied<RegistryAuditPage>();
            if (owner is null) return RegistryResult<RegistryAuditPage>.Failure(RegistryError.NotFound);
        }
        var items = new List<RegistryAuditItem>();
        var scope = tenantId.HasValue ? "tenant_id=$2" : "tenant_id IS NULL";
        // Only this fixed server-selected fragment is interpolated. All input is parameterized.
        await using (var command = Command(db, $"""
            SELECT event_id,actor_user_id,tenant_id,app_id,release_version,action,result,request_id,occurred_at
            FROM public.platform_registry_audit WHERE app_id=$1 AND {scope}
            ORDER BY occurred_at DESC,event_id DESC LIMIT 51
            """, tenantId.HasValue ? [appId, tenantId.Value] : [appId]))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) items.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetFieldValue<DateTimeOffset>(8)));
        if (tenantId.HasValue ? !await SameAccess(subject, access!, ct) : actor != await ReadActor(db, subject, ct)) return Denied<RegistryAuditPage>();
        var hasMore = items.Count > AuditPageSize;
        if (hasMore) items.RemoveAt(AuditPageSize);
        return RegistryResult<RegistryAuditPage>.Success(new(items, hasMore));
    }, token);

    private async Task<AccessSnapshot?> TenantActor(SubjectIdentity subject, Guid tenant, string appId, CancellationToken token)
    {
        var access = await authorization.ReadAccessAsync(subject, tenant, token);
        return ValidAccess(access, tenant) && PermissionEvaluator.Allows(access, "registry:manage", new(tenant, "application", appId, null)) ? access : null;
    }
    private async Task<bool> SameAccess(SubjectIdentity subject, AccessSnapshot before, CancellationToken token)
    {
        var after = await authorization.ReadAccessAsync(subject, before.TenantId, token);
        return ValidAccess(after, before.TenantId) && after!.UserId == before.UserId && after.AuthorizationVersion == before.AuthorizationVersion;
    }
    private static bool ValidAccess(AccessSnapshot? access, Guid tenant) => tenant != Guid.Empty && access is not null
        && access.TenantId == tenant && access.UserId != Guid.Empty && access.AuthorizationVersion > 0;
    private static async Task<Actor?> ReadActor(NpgsqlConnection db, SubjectIdentity subject, CancellationToken token)
    {
        await using var command = Command(db, """
            SELECT u.user_id,
                EXISTS(SELECT 1 FROM public.platform_registry_operators r WHERE r.user_id=u.user_id AND r.role='developer' AND r.enabled),
                EXISTS(SELECT 1 FROM public.platform_registry_operators r WHERE r.user_id=u.user_id AND r.role='reviewer' AND r.enabled)
            FROM public.platform_users u WHERE u.issuer=$1 AND u.subject=$2 AND u.enabled
            """, subject.Issuer, subject.Subject);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new(reader.GetGuid(0), reader.GetBoolean(1), reader.GetBoolean(2)) : null;
    }
    private static async Task<Guid?> Owner(NpgsqlConnection db, string appId, CancellationToken token)
    {
        await using var command = Command(db, "SELECT owner_user_id FROM public.platform_applications WHERE app_id=$1", appId);
        return await command.ExecuteScalarAsync(token) is Guid id ? id : null;
    }
    private async Task<T> Run<T>(Func<NpgsqlConnection, CancellationToken, Task<T>> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var value = configuration.GetConnectionString("Platform");
            if (string.IsNullOrWhiteSpace(value)) throw new RegistryStoreUnavailableException();
            var settings = new NpgsqlConnectionStringBuilder(value) { Timeout = 3, CommandTimeout = 3, IncludeErrorDetail = false };
            await using var db = new NpgsqlConnection(settings.ConnectionString);
            await db.OpenAsync(budget.Token);
            var result = await action(db, budget.Token);
            budget.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception error) when (!(error is OperationCanceledException && token.IsCancellationRequested)
            && error is NpgsqlException or IOException or TimeoutException or ArgumentException or InvalidOperationException or JsonException or OperationCanceledException)
        { throw new RegistryStoreUnavailableException(); }
    }
    private static RegistryResult<T> Denied<T>() => RegistryResult<T>.Failure(RegistryError.Forbidden);
    private static NpgsqlCommand Command(NpgsqlConnection db, string sql, params object[] values)
    {
        var command = new NpgsqlCommand(sql, db) { CommandTimeout = 3 };
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return command;
    }
    private sealed record Actor(Guid Id, bool Developer, bool Reviewer);
}
