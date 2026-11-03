using System.Text.Json;
using AssembleAI.Platform.Authorization;
using Npgsql;
using NpgsqlTypes;

namespace AssembleAI.Platform.Registry;

public sealed class PostgresRegistryStore(IConfiguration configuration, RegistryDeploymentCatalog catalog,
    IAuthorizationStore authorization) : IRegistryStore
{
    public Task<RegistryResult<ApplicationReleaseView>> SaveDraftAsync(SubjectIdentity subject, ApplicationManifest manifest,
        long expectedRevision, string requestId, CancellationToken cancellationToken = default) => Run(async (db, tx) =>
    {
        var actor = await Actor(db, tx, subject, cancellationToken);
        if (actor is null) return Fail<ApplicationReleaseView>(RegistryError.Forbidden);
        if (!ManifestValidator.IsValid(manifest) || expectedRevision < 0)
            return Fail<ApplicationReleaseView>(RegistryError.InvalidManifest);
        await Lock(db, tx, manifest.AppId, cancellationToken);
        var owner = await Owner(db, tx, manifest.AppId, cancellationToken);
        if (!actor.Developer || owner.HasValue && owner != actor.Id)
            return await Reject<ApplicationReleaseView>(db, tx, actor.Id, null, manifest.AppId, manifest.Version, "save_draft", RegistryError.Forbidden, requestId, cancellationToken);
        var previous = owner.HasValue ? await Release(db, tx, manifest.AppId, manifest.Version, owner.Value, cancellationToken) : null;
        if ((previous?.Revision ?? 0) != expectedRevision || previous is not null && previous.Manifest.Status != "draft")
            return await Reject<ApplicationReleaseView>(db, tx, actor.Id, null, manifest.AppId, manifest.Version, "save_draft", RegistryError.Conflict, requestId, cancellationToken);
        if (owner is null)
            await Write(db, tx, "INSERT INTO public.platform_applications(app_id,name,category,owner_user_id) VALUES($1,$2,$3,$4)", cancellationToken,
                manifest.AppId, manifest.Name, manifest.Category, actor.Id);
        if (previous is null)
            await Write(db, tx, "INSERT INTO public.platform_application_releases(app_id,release_version,manifest) VALUES($1,$2,$3::jsonb)", cancellationToken,
                manifest.AppId, manifest.Version, ManifestValidator.Serialize(manifest));
        else
            await Write(db, tx, "UPDATE public.platform_application_releases SET manifest=$3::jsonb,revision=revision+1 WHERE app_id=$1 AND release_version=$2", cancellationToken,
                manifest.AppId, manifest.Version, ManifestValidator.Serialize(manifest));
        await Audit(db, tx, actor.Id, null, manifest.AppId, manifest.Version, "save_draft", "success", requestId, cancellationToken);
        return RegistryResult<ApplicationReleaseView>.Success((await Release(db, tx, manifest.AppId, manifest.Version, actor.Id, cancellationToken))!);
    }, cancellationToken);

    public Task<RegistryResult<ApplicationReleaseView>> TransitionAsync(SubjectIdentity subject, string appId, string version,
        string action, long expectedRevision, string requestId, CancellationToken cancellationToken = default) => Run(async (db, tx) =>
    {
        if (!ManifestValidator.IsAppId(appId) || !ManifestValidator.IsVersion(version)
            || action is not ("submit" or "reject" or "publish" or "disable" or "enable")) return Fail<ApplicationReleaseView>(RegistryError.InvalidManifest);
        var actor = await Actor(db, tx, subject, cancellationToken);
        if (actor is null) return Fail<ApplicationReleaseView>(RegistryError.Forbidden);
        await Lock(db, tx, appId, cancellationToken);
        var owner = await Owner(db, tx, appId, cancellationToken);
        if (!(action == "submit" ? actor.Developer && owner == actor.Id : actor.Reviewer))
            return await Reject<ApplicationReleaseView>(db, tx, actor.Id, null, appId, version, action, RegistryError.Forbidden, requestId, cancellationToken);
        var release = owner.HasValue ? await Release(db, tx, appId, version, owner.Value, cancellationToken) : null;
        if (release is null) return await Reject<ApplicationReleaseView>(db, tx, actor.Id, null, appId, version, action, RegistryError.NotFound, requestId, cancellationToken);
        if (action == "publish" && release.SubmittedBy == actor.Id)
            return await Reject<ApplicationReleaseView>(db, tx, actor.Id, null, appId, version, action, RegistryError.Forbidden, requestId, cancellationToken);
        var next = (release.Manifest.Status, action) switch
        {
            ("draft", "submit") => "pending_review", ("pending_review", "reject") => "draft",
            ("pending_review", "publish") => "published", ("published", "disable") => "disabled",
            ("disabled", "enable") => "published", _ => null
        };
        if (release.Revision != expectedRevision || next is null)
            return await Reject<ApplicationReleaseView>(db, tx, actor.Id, null, appId, version, action, RegistryError.Conflict, requestId, cancellationToken);
        if (next == "published" && !catalog.Allows(release.Manifest))
            return await Reject<ApplicationReleaseView>(db, tx, actor.Id, null, appId, version, action, RegistryError.DeploymentNotAllowed, requestId, cancellationToken);
        await Write(db, tx, """
            UPDATE public.platform_application_releases SET status=$3,revision=revision+1,
                submitted_by=CASE WHEN $4='submit' THEN $5 ELSE submitted_by END,
                published_at=CASE WHEN $3='published' THEN COALESCE(published_at,now()) ELSE published_at END
            WHERE app_id=$1 AND release_version=$2
            """, cancellationToken, appId, version, next, action, actor.Id);
        await Audit(db, tx, actor.Id, null, appId, version, action, "success", requestId, cancellationToken);
        return RegistryResult<ApplicationReleaseView>.Success((await Release(db, tx, appId, version, owner!.Value, cancellationToken))!);
    }, cancellationToken);

    public Task<RegistryResult<ApplicationReleaseView>> ReadReleaseAsync(SubjectIdentity subject, string appId, string version,
        CancellationToken cancellationToken = default) => Run(async (db, tx) =>
    {
        var actor = await Actor(db, tx, subject, cancellationToken);
        var owner = await Owner(db, tx, appId, cancellationToken);
        if (!CanRead(actor, owner)) return Fail<ApplicationReleaseView>(RegistryError.Forbidden);
        var release = owner.HasValue ? await Release(db, tx, appId, version, owner.Value, cancellationToken) : null;
        return release is null ? Fail<ApplicationReleaseView>(RegistryError.NotFound) : RegistryResult<ApplicationReleaseView>.Success(release);
    }, cancellationToken);

    public Task<RegistryResult<IReadOnlyList<ApplicationReleaseView>>> ListReleasesAsync(SubjectIdentity subject, string appId,
        CancellationToken cancellationToken = default) => Run(async (db, tx) =>
    {
        var actor = await Actor(db, tx, subject, cancellationToken);
        var owner = await Owner(db, tx, appId, cancellationToken);
        if (!CanRead(actor, owner)) return Fail<IReadOnlyList<ApplicationReleaseView>>(RegistryError.Forbidden);
        if (owner is null) return Fail<IReadOnlyList<ApplicationReleaseView>>(RegistryError.NotFound);
        await using var command = Command(db, tx, "SELECT manifest,status,revision,submitted_by,published_at FROM public.platform_application_releases WHERE app_id=$1 ORDER BY release_version COLLATE \"C\" LIMIT 101", appId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var releases = new List<ApplicationReleaseView>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (releases.Count == 100) throw new RegistryStoreUnavailableException();
            releases.Add(View(reader, owner.Value));
        }
        return RegistryResult<IReadOnlyList<ApplicationReleaseView>>.Success(releases);
    }, cancellationToken);

    public Task<RegistryResult<TenantApplicationView>> ReadTenantApplicationAsync(SubjectIdentity subject, Guid tenantId,
        string appId, CancellationToken cancellationToken = default) => Run(async (db, tx) =>
    {
        if (await TenantActor(subject, tenantId, appId, cancellationToken) is null) return Fail<TenantApplicationView>(RegistryError.Forbidden);
        var value = await TenantView(db, tx, tenantId, appId, cancellationToken);
        return value is null ? Fail<TenantApplicationView>(RegistryError.NotFound) : RegistryResult<TenantApplicationView>.Success(value);
    }, cancellationToken);

    public Task<RegistryResult<TenantApplicationView>> SetTenantApplicationAsync(SubjectIdentity subject, Guid tenantId,
        string appId, string releaseVersion, bool enabled, long expectedRevision, string requestId,
        CancellationToken cancellationToken = default) => Run(async (db, tx) =>
    {
        var actor = await Actor(db, tx, subject, cancellationToken);
        if (actor is null) return Fail<TenantApplicationView>(RegistryError.Forbidden);
        if (!ManifestValidator.IsAppId(appId) || !ManifestValidator.IsVersion(releaseVersion) || expectedRevision < 0)
            return Fail<TenantApplicationView>(RegistryError.InvalidManifest);
        await Lock(db, tx, appId, cancellationToken);
        if (await TenantActor(subject, tenantId, appId, cancellationToken) is null)
            return await Reject<TenantApplicationView>(db, tx, actor.Id, tenantId, appId, releaseVersion, "set_tenant", RegistryError.Forbidden, requestId, cancellationToken);
        var current = await TenantView(db, tx, tenantId, appId, cancellationToken);
        if ((current?.Revision ?? 0) != expectedRevision)
            return await Reject<TenantApplicationView>(db, tx, actor.Id, tenantId, appId, releaseVersion, "set_tenant", RegistryError.Conflict, requestId, cancellationToken);
        var owner = await Owner(db, tx, appId, cancellationToken);
        var release = owner.HasValue ? await Release(db, tx, appId, releaseVersion, owner.Value, cancellationToken) : null;
        var stoppingCurrent = !enabled && current?.ReleaseVersion == releaseVersion;
        if (release is null || !stoppingCurrent && release.Manifest.Status != "published")
            return await Reject<TenantApplicationView>(db, tx, actor.Id, tenantId, appId, releaseVersion, "set_tenant", RegistryError.Conflict, requestId, cancellationToken);
        if (current is null)
            await Write(db, tx, "INSERT INTO public.tenant_applications(tenant_id,app_id,release_version,enabled) VALUES($1,$2,$3,$4)", cancellationToken, tenantId, appId, releaseVersion, enabled);
        else
            await Write(db, tx, "UPDATE public.tenant_applications SET release_version=$3,enabled=$4,revision=revision+1 WHERE tenant_id=$1 AND app_id=$2", cancellationToken, tenantId, appId, releaseVersion, enabled);
        await Audit(db, tx, actor.Id, tenantId, appId, releaseVersion, "set_tenant", "success", requestId, cancellationToken);
        return RegistryResult<TenantApplicationView>.Success((await TenantView(db, tx, tenantId, appId, cancellationToken))!);
    }, cancellationToken);

    private async Task<AccessSnapshot?> TenantActor(SubjectIdentity subject, Guid tenant, string appId, CancellationToken token)
    {
        var snapshot = await authorization.ReadAccessAsync(subject, tenant, token);
        return PermissionEvaluator.Allows(snapshot, "registry:manage", new(tenant, "application", appId, null)) ? snapshot : null;
    }
    private static bool CanRead(RegistryActor? actor, Guid? owner) => actor is not null && (actor.Reviewer || actor.Developer && owner == actor.Id);
    private static RegistryResult<T> Fail<T>(RegistryError error) => RegistryResult<T>.Failure(error);

    private async Task<T> Run<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var connectionString = configuration.GetConnectionString("Platform");
            if (string.IsNullOrWhiteSpace(connectionString)) throw new RegistryStoreUnavailableException();
            var settings = new NpgsqlConnectionStringBuilder(connectionString) { Timeout = 5, CommandTimeout = 5, IncludeErrorDetail = false };
            await using var db = new NpgsqlConnection(settings.ConnectionString);
            await db.OpenAsync(token);
            await using var tx = await db.BeginTransactionAsync(token);
            var result = await action(db, tx);
            await tx.CommitAsync(token);
            return result;
        }
        catch (Exception error) when (!(error is OperationCanceledException && token.IsCancellationRequested)
            && error is NpgsqlException or IOException or TimeoutException or ArgumentException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            throw new RegistryStoreUnavailableException();
        }
    }
    private static async Task Lock(NpgsqlConnection db, NpgsqlTransaction tx, string appId, CancellationToken token) =>
        await Write(db, tx, "SELECT pg_advisory_xact_lock(162110,hashtext($1))", token, appId);

    private static async Task<RegistryActor?> Actor(NpgsqlConnection db, NpgsqlTransaction tx, SubjectIdentity subject, CancellationToken token)
    {
        await using var command = Command(db, tx, """
            SELECT u.user_id,
                EXISTS(SELECT 1 FROM public.platform_registry_operators r WHERE r.user_id=u.user_id AND r.role='developer' AND r.enabled),
                EXISTS(SELECT 1 FROM public.platform_registry_operators r WHERE r.user_id=u.user_id AND r.role='reviewer' AND r.enabled)
            FROM public.platform_users u WHERE u.issuer=$1 AND u.subject=$2 AND u.enabled
            """, subject.Issuer, subject.Subject);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new(reader.GetGuid(0), reader.GetBoolean(1), reader.GetBoolean(2)) : null;
    }
    private static async Task<Guid?> Owner(NpgsqlConnection db, NpgsqlTransaction tx, string appId, CancellationToken token)
    {
        await using var command = Command(db, tx, "SELECT owner_user_id FROM public.platform_applications WHERE app_id=$1", appId);
        return await command.ExecuteScalarAsync(token) is Guid owner ? owner : null;
    }
    private static async Task<ApplicationReleaseView?> Release(NpgsqlConnection db, NpgsqlTransaction tx, string appId, string version, Guid owner, CancellationToken token)
    {
        await using var command = Command(db, tx, "SELECT manifest,status,revision,submitted_by,published_at FROM public.platform_application_releases WHERE app_id=$1 AND release_version=$2", appId, version);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? View(reader, owner) : null;
    }
    private static ApplicationReleaseView View(NpgsqlDataReader reader, Guid owner) => new(
        Manifest(reader.GetString(0)) with { Status = reader.GetString(1) }, reader.GetInt64(2), owner,
        reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4));
    private static ApplicationManifest Manifest(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!ManifestValidator.TryParse(document.RootElement, out var manifest)) throw new RegistryStoreUnavailableException();
        return manifest!;
    }
    private static async Task<TenantApplicationView?> TenantView(NpgsqlConnection db, NpgsqlTransaction tx, Guid tenantId, string appId, CancellationToken token)
    {
        await using var command = Command(db, tx, """
            SELECT t.release_version,t.enabled,t.revision,r.manifest,r.status
            FROM public.tenant_applications t JOIN public.platform_application_releases r
                ON r.app_id=t.app_id AND r.release_version=t.release_version
            WHERE t.tenant_id=$1 AND t.app_id=$2
            """, tenantId, appId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new(tenantId, appId, reader.GetString(0), reader.GetBoolean(1),
            reader.GetBoolean(1) && reader.GetString(4) == "published", reader.GetInt64(2), Manifest(reader.GetString(3)) with { Status = reader.GetString(4) }) : null;
    }
    private static async Task<RegistryResult<T>> Reject<T>(NpgsqlConnection db, NpgsqlTransaction tx, Guid actor, Guid? tenant,
        string appId, string version, string action, RegistryError error, string requestId, CancellationToken token)
    {
        await Audit(db, tx, actor, tenant, appId, version, action, error.ToString(), requestId, token);
        return Fail<T>(error);
    }
    private static Task Audit(NpgsqlConnection db, NpgsqlTransaction tx, Guid actor, Guid? tenant, string appId,
        string version, string action, string result, string requestId, CancellationToken token) => Write(db, tx,
            "INSERT INTO public.platform_registry_audit(event_id,actor_user_id,tenant_id,app_id,release_version,action,result,request_id) VALUES($1,$2,$3,$4,$5,$6,$7,$8)",
            token, Guid.NewGuid(), actor, tenant, appId, version, action, result, requestId);
    private static async Task Write(NpgsqlConnection db, NpgsqlTransaction tx, string sql, CancellationToken token, params object?[] values)
    {
        await using var command = Command(db, tx, sql, values);
        await command.ExecuteNonQueryAsync(token);
    }
    private static NpgsqlCommand Command(NpgsqlConnection db, NpgsqlTransaction tx, string sql, params object?[] values)
    {
        var command = new NpgsqlCommand(sql, db, tx) { CommandTimeout = 5 };
        foreach (var value in values)
            if (value is null) command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = DBNull.Value });
            else command.Parameters.AddWithValue(value);
        return command;
    }
    private sealed record RegistryActor(Guid Id, bool Developer, bool Reviewer);
}
