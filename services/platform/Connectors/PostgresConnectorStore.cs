using Npgsql;
using NpgsqlTypes;

namespace AssembleAI.Platform.Connectors;

/// <summary>Revisioned policies and metadata-only audit; never stores credentials, SQL values or results.</summary>
public sealed class PostgresConnectorStore(IConfiguration configuration) : IConnectorStore
{
    public Task<TenantConnectorConfiguration?> ReadConfigurationAsync(Guid tenantId, string sourceId, CancellationToken token) =>
        Run<TenantConnectorConfiguration?>(async (db, tx, budget) =>
        {
            ValidateIdentity(tenantId, sourceId);
            await using var command = Command(db, tx, "SELECT enabled,max_rows,timeout_seconds,revision FROM public.tenant_connectors WHERE tenant_id=$1 AND source_id=$2", tenantId, sourceId);
            await using var reader = await command.ExecuteReaderAsync(budget);
            return await reader.ReadAsync(budget) ? new(tenantId, sourceId, reader.GetBoolean(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3)) : null;
        }, token);

    public Task<TenantConnectorConfiguration?> SaveConfigurationAsync(Guid tenantId, string sourceId, bool enabled, int maxRows,
        int timeoutSeconds, long expectedRevision, Guid actorUserId, string requestId, CancellationToken token) =>
        Run<TenantConnectorConfiguration?>(async (db, tx, budget) =>
        {
            ValidateIdentity(tenantId, sourceId);
            if (maxRows is < 1 or > 500 || timeoutSeconds is < 1 or > 30 || expectedRevision is < 0 or long.MaxValue
                || actorUserId == Guid.Empty || !ValidRequestId(requestId)) throw new ConnectorUnavailableException();
            // Retain active identity rows until this configuration/audit transaction
            // commits; a concurrent disable cannot slip between validation and write.
            await using (var actor = Command(db, tx, """
                SELECT u.user_id FROM public.platform_users u
                JOIN public.tenant_memberships member ON member.user_id=u.user_id AND member.tenant_id=$1 AND member.enabled
                JOIN public.platform_tenants t ON t.tenant_id=member.tenant_id AND t.enabled
                WHERE u.user_id=$2 AND u.enabled FOR SHARE OF u,member,t
                """, tenantId, actorUserId))
                if (await actor.ExecuteScalarAsync(budget) is not Guid) throw new ConnectorUnavailableException();
            long revision;
            if (expectedRevision == 0)
            {
                await using var command = Command(db, tx, """
                    INSERT INTO public.tenant_connectors(tenant_id,source_id,enabled,max_rows,timeout_seconds)
                    VALUES($1,$2,$3,$4,$5) ON CONFLICT(tenant_id,source_id) DO NOTHING RETURNING revision
                    """, tenantId, sourceId, enabled, maxRows, timeoutSeconds);
                if (await command.ExecuteScalarAsync(budget) is not long inserted) return null;
                revision = inserted;
            }
            else
            {
                await using var command = Command(db, tx, """
                    UPDATE public.tenant_connectors SET enabled=$3,max_rows=$4,timeout_seconds=$5,revision=revision+1
                    WHERE tenant_id=$1 AND source_id=$2 AND revision=$6 RETURNING revision
                    """, tenantId, sourceId, enabled, maxRows, timeoutSeconds, expectedRevision);
                if (await command.ExecuteScalarAsync(budget) is not long updated) return null;
                revision = updated;
            }
            await Write(db, tx, """
                INSERT INTO public.platform_connector_configuration_audit(event_id,actor_user_id,tenant_id,source_id,request_id,revision,enabled,max_rows,timeout_seconds)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9)
                """, budget, Guid.NewGuid(), actorUserId, tenantId, sourceId, requestId, revision, enabled, maxRows, timeoutSeconds);
            return new(tenantId, sourceId, enabled, maxRows, timeoutSeconds, revision);
        }, token);

    public Task<Guid> BeginCallAsync(Guid tenantId, Guid userId, string sourceId, string? datasetId, string operation, string requestId, CancellationToken token) =>
        Run(async (db, tx, budget) =>
        {
            ValidateIdentity(tenantId, sourceId);
            if (userId == Guid.Empty || !ValidRequestId(requestId)
                || operation is not ("query" or "test") || (operation == "query" ? !ValidId(datasetId) : datasetId is not null))
                throw new ConnectorUnavailableException();
            var callId = Guid.NewGuid();
            await using var command = Command(db, tx, """
                INSERT INTO public.platform_connector_calls(call_id,tenant_id,user_id,source_id,request_id,operation,dataset_id)
                SELECT $1,c.tenant_id,u.user_id,c.source_id,$5,$6,$7
                FROM public.tenant_connectors c
                JOIN public.platform_tenants t ON t.tenant_id=c.tenant_id AND t.enabled
                JOIN public.tenant_memberships member ON member.tenant_id=c.tenant_id AND member.user_id=$3 AND member.enabled
                JOIN public.platform_users u ON u.user_id=member.user_id AND u.enabled
                WHERE c.tenant_id=$2 AND c.source_id=$4 AND c.enabled
                """, callId, tenantId, userId, sourceId, requestId, operation);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Varchar, Value = (object?)datasetId ?? DBNull.Value });
            if (await command.ExecuteNonQueryAsync(budget) != 1) throw new ConnectorUnavailableException();
            return callId;
        }, token);

    public Task CompleteCallAsync(Guid callId, string outcome, int? rowCount, long elapsedMilliseconds, CancellationToken token) =>
        Run(async (db, tx, budget) =>
        {
            if (callId == Guid.Empty || outcome is not ("completed" or "cancelled" or "revoked" or "timeout" or "upstream_error" or "failed")
                || elapsedMilliseconds < 0 || rowCount < 0 || outcome != "completed" && rowCount is not null)
                throw new ConnectorUnavailableException();
            await using var command = Command(db, tx, """
                UPDATE public.platform_connector_calls SET finished_at=now(),outcome=$2,elapsed_milliseconds=$3,row_count=$4
                WHERE call_id=$1 AND outcome='running' AND finished_at IS NULL
                    AND ($2<>'completed' OR (operation='query' AND $4 IS NOT NULL) OR (operation='test' AND $4 IS NULL))
                """, callId, outcome, elapsedMilliseconds);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = (object?)rowCount ?? DBNull.Value });
            if (await command.ExecuteNonQueryAsync(budget) != 1) throw new ConnectorUnavailableException();
            return true;
        }, token);

    public Task<IReadOnlyList<ConnectorCallRecord>> ListCallsAsync(Guid tenantId, string sourceId, CancellationToken token) =>
        Run<IReadOnlyList<ConnectorCallRecord>>(async (db, tx, budget) =>
        {
            ValidateIdentity(tenantId, sourceId);
            var rows = new List<ConnectorCallRecord>();
            await using var command = Command(db, tx, """
                SELECT call_id,user_id,dataset_id,operation,request_id,outcome,row_count,elapsed_milliseconds,started_at
                FROM public.platform_connector_calls WHERE tenant_id=$1 AND source_id=$2
                ORDER BY started_at DESC,call_id DESC LIMIT 50
                """, tenantId, sourceId);
            await using var reader = await command.ExecuteReaderAsync(budget);
            while (await reader.ReadAsync(budget))
                rows.Add(new(reader.GetGuid(0), tenantId, reader.GetGuid(1), sourceId, reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    reader.IsDBNull(7) ? 0 : reader.GetInt64(7), reader.GetFieldValue<DateTimeOffset>(8)));
            return rows;
        }, token);

    private static bool ValidId(string? value) => value is { Length: >= 1 and <= 64 } && value[0] is >= 'a' and <= 'z'
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');
    private static bool ValidRequestId(string value) => value is { Length: 32 } && value.All(character => character is >= 'a' and <= 'f' or >= '0' and <= '9');
    private static void ValidateIdentity(Guid tenantId, string sourceId)
    { if (tenantId == Guid.Empty || !ValidId(sourceId)) throw new ConnectorUnavailableException(); }

    private async Task<T> Run<T>(Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var value = configuration.GetConnectionString("Platform");
            if (string.IsNullOrWhiteSpace(value)) throw new ConnectorUnavailableException();
            var settings = new NpgsqlConnectionStringBuilder(value) { Timeout = 3, CommandTimeout = 3, IncludeErrorDetail = false };
            await using var db = new NpgsqlConnection(settings.ConnectionString);
            await db.OpenAsync(budget.Token);
            await using var tx = await db.BeginTransactionAsync(budget.Token);
            var result = await action(db, tx, budget.Token);
            await tx.CommitAsync(budget.Token);
            return result;
        }
        catch (Exception error) when (!(error is OperationCanceledException && token.IsCancellationRequested)
            && error is NpgsqlException or IOException or TimeoutException or ArgumentException or InvalidOperationException or OperationCanceledException)
        { throw new ConnectorUnavailableException(); }
    }
    private static NpgsqlCommand Command(NpgsqlConnection db, NpgsqlTransaction tx, string sql, params object[] values)
    { var command = new NpgsqlCommand(sql, db, tx) { CommandTimeout = 3 }; foreach (var value in values) command.Parameters.AddWithValue(value); return command; }
    private static async Task<int> Write(NpgsqlConnection db, NpgsqlTransaction tx, string sql, CancellationToken token, params object[] values)
    { await using var command = Command(db, tx, sql, values); return await command.ExecuteNonQueryAsync(token); }
}
