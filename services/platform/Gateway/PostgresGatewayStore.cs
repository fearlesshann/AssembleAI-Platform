using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Registry;
using Npgsql;

namespace AssembleAI.Platform.Gateway;

/// <summary>Durable leases and call records; every admission rechecks current entry permissions.</summary>
public sealed class PostgresGatewayStore(IConfiguration configuration, IAuthorizationStore authorization) : IGatewayStore
{
    public Task<ApplicationManifest?> ResolveStartAsync(SubjectIdentity subject, Guid tenantId, string appId, string version,
        CancellationToken token = default) => Run(async (db, tx, budget) =>
    {
        var resolved = await Resolve(db, tx, subject, tenantId, appId, version, budget);
        return resolved?.Manifest;
    }, token);

    public Task<GatewayRuntime?> CreateRunAsync(SubjectIdentity subject, string sessionKey, Guid tenantId, string appId,
        string version, TimeSpan lifetime, TimeSpan drainWindow, CancellationToken token = default) => Run(async (db, tx, budget) =>
    {
        if (!ValidSessionKey(sessionKey) || lifetime.TotalSeconds is < 1 or > 86400
            || drainWindow.TotalSeconds is < 1 or > 3600 || lifetime.TotalSeconds != Math.Truncate(lifetime.TotalSeconds)
            || drainWindow.TotalSeconds != Math.Truncate(drainWindow.TotalSeconds)) return null;
        if (!ManifestValidator.IsAppId(appId) || !ManifestValidator.IsVersion(version)) return null;
        // Same first lock as registry release/pointer changes. User lock is always second.
        await Write(db, tx, "SELECT pg_advisory_xact_lock(162110,hashtext($1))", budget, appId);
        var resolved = await Resolve(db, tx, subject, tenantId, appId, version, budget);
        if (resolved is null) return null;
        await Write(db, tx, "SELECT pg_advisory_xact_lock(162111,hashtext($1))", budget, resolved.Access.UserId.ToString("D"));
        // Bounded, subject-owned cleanup; call history deliberately has no lease FK.
        await Write(db, tx, """
            WITH expired AS (
                SELECT run_id FROM public.platform_gateway_runs WHERE user_id=$1
                    AND (expires_at<=now() OR revoked_at IS NOT NULL OR drain_until<=now())
                ORDER BY expires_at LIMIT 100 FOR UPDATE SKIP LOCKED
            ) DELETE FROM public.platform_gateway_runs r USING expired e WHERE r.run_id=e.run_id
            """, budget, resolved.Access.UserId);
        await using (var count = Command(db, tx, "SELECT count(*) FROM public.platform_gateway_runs WHERE user_id=$1 AND expires_at>now() AND revoked_at IS NULL AND (drain_until IS NULL OR drain_until>now())", resolved.Access.UserId))
            if ((long)(await count.ExecuteScalarAsync(budget))! >= 100) return null;
        var id = Guid.NewGuid();
        await using var command = Command(db, tx, """
            INSERT INTO public.platform_gateway_runs(run_id,user_id,tenant_id,app_id,release_version,session_hash,expires_at,drain_seconds)
            VALUES($1,$2,$3,$4,$5,$6,now()+$7*interval '1 second',$8) RETURNING expires_at
            """, id, resolved.Access.UserId, tenantId, appId, version, Hash(sessionKey), (int)lifetime.TotalSeconds, (int)drainWindow.TotalSeconds);
        var expires = (DateTime)(await command.ExecuteScalarAsync(budget))!;
        return new GatewayRuntime(id, resolved.Access.UserId, tenantId, appId, version, resolved.Manifest, new DateTimeOffset(expires), null);
    }, token);

    public Task<GatewayRuntime?> ReadRunAsync(SubjectIdentity subject, string sessionKey, Guid runId,
        CancellationToken token = default) => Run(async (db, tx, budget) =>
    {
        if (!ValidSessionKey(sessionKey) || runId == Guid.Empty) return null;
        GatewayRuntime? run;
        await using (var command = Command(db, tx, """
            SELECT r.run_id,r.user_id,r.tenant_id,r.app_id,r.release_version,p.manifest,r.expires_at,r.drain_until
            FROM public.platform_gateway_runs r
            JOIN public.platform_users u ON u.user_id=r.user_id AND u.enabled
            JOIN public.tenant_applications t ON t.tenant_id=r.tenant_id AND t.app_id=r.app_id AND t.enabled
            JOIN public.platform_application_releases p ON p.app_id=r.app_id AND p.release_version=r.release_version AND p.status='published'
            WHERE r.run_id=$1 AND r.session_hash=$2 AND u.issuer=$3 AND u.subject=$4
                AND r.revoked_at IS NULL AND r.expires_at>now() AND (r.drain_until IS NULL OR r.drain_until>now())
                AND (t.release_version=r.release_version OR r.drain_until IS NOT NULL)
            """, runId, Hash(sessionKey), subject.Issuer, subject.Subject))
        await using (var reader = await command.ExecuteReaderAsync(budget))
        {
            if (!await reader.ReadAsync(budget)) return null;
            run = new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4),
                Manifest(reader.GetString(5)), reader.GetFieldValue<DateTimeOffset>(6), reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7));
        }
        var access = await authorization.ReadAccessAsync(subject, run.TenantId, budget);
        return access?.UserId == run.UserId && Allows(access, run.Manifest, run.TenantId) ? run : null;
    }, token);

    public Task StartCallAsync(GatewayRuntime runtime, string requestId, string method, string path, CancellationToken token = default) =>
        Run(async (db, tx, budget) =>
        {
            if (!ValidRequestId(requestId) || method.Length is < 1 or > 16 || !method.All(c => c is >= 'A' and <= 'Z')
                || path.Length is < 1 or > 2048 || path[0] != '/' || path.StartsWith("//", StringComparison.Ordinal)
                || path.Any(char.IsControl) || path.Contains('?') || path.Contains('#') || path.Contains('\\')) throw new GatewayStoreUnavailableException();
            // Metadata is copied from the actual bound record, never an arbitrary supplied DTO.
            var count = await Write(db, tx, """
                INSERT INTO public.platform_gateway_calls(request_id,run_id,user_id,tenant_id,app_id,release_version,method,path)
                SELECT $1,r.run_id,r.user_id,r.tenant_id,r.app_id,r.release_version,$2,$3
                FROM public.platform_gateway_runs r
                JOIN public.tenant_applications t ON t.tenant_id=r.tenant_id AND t.app_id=r.app_id AND t.enabled
                JOIN public.platform_application_releases p ON p.app_id=r.app_id AND p.release_version=r.release_version AND p.status='published'
                WHERE r.run_id=$4 AND r.user_id=$5 AND r.tenant_id=$6 AND r.app_id=$7 AND r.release_version=$8
                    AND r.revoked_at IS NULL AND r.expires_at>now() AND (r.drain_until IS NULL OR r.drain_until>now())
                    AND (t.release_version=r.release_version OR r.drain_until IS NOT NULL)
                """, budget, requestId, method, path, runtime.RunId, runtime.UserId, runtime.TenantId, runtime.AppId, runtime.ReleaseVersion);
            if (count != 1) throw new GatewayStoreUnavailableException();
            return true;
        }, token);

    public Task FinishCallAsync(string requestId, int httpStatus, string outcome, long elapsedMilliseconds,
        CancellationToken token = default) => Run(async (db, tx, budget) =>
    {
        if (!ValidRequestId(requestId) || httpStatus != 0 && httpStatus is < 100 or > 599 || elapsedMilliseconds < 0
            || outcome.Length is < 1 or > 64 || outcome == "running" || outcome[0] is < 'a' or > 'z'
            || !outcome.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')) throw new GatewayStoreUnavailableException();
        var count = await Write(db, tx, """
            UPDATE public.platform_gateway_calls SET finished_at=now(),http_status=$2,outcome=$3,elapsed_milliseconds=$4
            WHERE request_id=$1 AND finished_at IS NULL AND outcome='running'
            """, budget, requestId, httpStatus, outcome, elapsedMilliseconds);
        if (count != 1) throw new GatewayStoreUnavailableException();
        return true;
    }, token);

    private async Task<Resolved?> Resolve(NpgsqlConnection db, NpgsqlTransaction tx, SubjectIdentity subject, Guid tenant,
        string appId, string version, CancellationToken token)
    {
        if (tenant == Guid.Empty || !ManifestValidator.IsAppId(appId) || !ManifestValidator.IsVersion(version)) return null;
        ApplicationManifest manifest;
        await using (var command = Command(db, tx, """
            SELECT p.manifest FROM public.tenant_applications t
            JOIN public.platform_application_releases p ON p.app_id=t.app_id AND p.release_version=t.release_version
            WHERE t.tenant_id=$1 AND t.app_id=$2 AND t.release_version=$3 AND t.enabled AND p.status='published'
            """, tenant, appId, version))
        {
            if (await command.ExecuteScalarAsync(token) is not string json) return null;
            manifest = Manifest(json);
        }
        var access = await authorization.ReadAccessAsync(subject, tenant, token);
        return Allows(access, manifest, tenant) ? new(manifest, access!) : null;
    }
    private static bool Allows(AccessSnapshot? access, ApplicationManifest manifest, Guid tenant) =>
        manifest.Permissions.All(permission => PermissionEvaluator.Allows(access, permission, new(tenant, "application", manifest.AppId, null)));
    private static ApplicationManifest Manifest(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!ManifestValidator.TryParse(document.RootElement, out var manifest)) throw new GatewayStoreUnavailableException();
        return manifest! with { Status = "published" };
    }
    private static bool ValidSessionKey(string value) => value is { Length: >= 1 and <= 512 } && !value.Any(char.IsControl);
    private static bool ValidRequestId(string value) => value is { Length: 32 } && value.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9');
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private async Task<T> Run<T>(Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var value = configuration.GetConnectionString("Platform");
            if (string.IsNullOrWhiteSpace(value)) throw new GatewayStoreUnavailableException();
            var settings = new NpgsqlConnectionStringBuilder(value) { Timeout = 3, CommandTimeout = 3, IncludeErrorDetail = false };
            await using var db = new NpgsqlConnection(settings.ConnectionString);
            await db.OpenAsync(budget.Token);
            await using var tx = await db.BeginTransactionAsync(budget.Token);
            var result = await action(db, tx, budget.Token);
            await tx.CommitAsync(budget.Token);
            return result;
        }
        catch (Exception error) when (!(error is OperationCanceledException && token.IsCancellationRequested)
            && error is NpgsqlException or IOException or TimeoutException or ArgumentException or InvalidOperationException or JsonException or OperationCanceledException)
        {
            throw new GatewayStoreUnavailableException();
        }
    }
    private static NpgsqlCommand Command(NpgsqlConnection db, NpgsqlTransaction tx, string sql, params object[] values)
    {
        var command = new NpgsqlCommand(sql, db, tx) { CommandTimeout = 3 };
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return command;
    }
    private static async Task<int> Write(NpgsqlConnection db, NpgsqlTransaction tx, string sql, CancellationToken token, params object[] values)
    {
        await using var command = Command(db, tx, sql, values);
        return await command.ExecuteNonQueryAsync(token);
    }
    private sealed record Resolved(ApplicationManifest Manifest, AccessSnapshot Access);
}
