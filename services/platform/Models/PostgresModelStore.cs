using Npgsql;
using NpgsqlTypes;

namespace AssembleAI.Platform.Models;

/// <summary>Bounded PostgreSQL operations; provider configuration and model content are never persisted.</summary>
public sealed class PostgresModelStore(IConfiguration configuration) : IModelStore
{
    public Task<TenantModelConfiguration?> ReadConfigurationAsync(Guid tenantId, string modelId, CancellationToken token) =>
        Run<TenantModelConfiguration?>(async (db, tx, budget) =>
        {
            ValidateIdentity(tenantId, modelId);
            await using var command = Command(db, tx, "SELECT enabled,max_output_tokens,revision FROM public.tenant_models WHERE tenant_id=$1 AND model_id=$2", tenantId, modelId);
            await using var reader = await command.ExecuteReaderAsync(budget);
            return await reader.ReadAsync(budget) ? new(tenantId, modelId, reader.GetBoolean(0), reader.GetInt32(1), reader.GetInt64(2)) : null;
        }, token);

    public Task<TenantModelConfiguration?> SaveConfigurationAsync(Guid tenantId, string modelId, bool enabled, int maxOutputTokens,
        long expectedRevision, Guid actorUserId, string requestId, CancellationToken token) =>
        Run<TenantModelConfiguration?>(async (db, tx, budget) =>
        {
            ValidateIdentity(tenantId, modelId);
            if (maxOutputTokens <= 0 || expectedRevision is < 0 or long.MaxValue || actorUserId == Guid.Empty || !ValidRequestId(requestId))
                throw new ModelStoreUnavailableException();
            long revision;
            if (expectedRevision == 0)
            {
                await using var command = Command(db, tx, """
                    INSERT INTO public.tenant_models(tenant_id,model_id,enabled,max_output_tokens)
                    VALUES($1,$2,$3,$4) ON CONFLICT(tenant_id,model_id) DO NOTHING RETURNING revision
                    """, tenantId, modelId, enabled, maxOutputTokens);
                if (await command.ExecuteScalarAsync(budget) is not long inserted) return null;
                revision = inserted;
            }
            else
            {
                await using var command = Command(db, tx, """
                    UPDATE public.tenant_models SET enabled=$3,max_output_tokens=$4,revision=revision+1
                    WHERE tenant_id=$1 AND model_id=$2 AND revision=$5 RETURNING revision
                    """, tenantId, modelId, enabled, maxOutputTokens, expectedRevision);
                if (await command.ExecuteScalarAsync(budget) is not long updated) return null;
                revision = updated;
            }
            await Write(db, tx, """
                INSERT INTO public.platform_model_configuration_audit(event_id,actor_user_id,tenant_id,model_id,request_id,revision,enabled,max_output_tokens)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8)
                """, budget, Guid.NewGuid(), actorUserId, tenantId, modelId, requestId, revision, enabled, maxOutputTokens);
            return new(tenantId, modelId, enabled, maxOutputTokens, revision);
        }, token);

    public Task<Guid> BeginCallAsync(Guid tenantId, Guid userId, string modelId, string requestId, CancellationToken token) =>
        Run(async (db, tx, budget) =>
        {
            ValidateIdentity(tenantId, modelId);
            if (userId == Guid.Empty || !ValidRequestId(requestId)) throw new ModelStoreUnavailableException();
            var callId = Guid.NewGuid();
            var written = await Write(db, tx, """
                INSERT INTO public.platform_model_calls(call_id,tenant_id,user_id,model_id,request_id)
                SELECT $1,m.tenant_id,u.user_id,m.model_id,$5
                FROM public.tenant_models m
                JOIN public.platform_tenants t ON t.tenant_id=m.tenant_id AND t.enabled
                JOIN public.tenant_memberships member ON member.tenant_id=m.tenant_id AND member.user_id=$3 AND member.enabled
                JOIN public.platform_users u ON u.user_id=member.user_id AND u.enabled
                WHERE m.tenant_id=$2 AND m.model_id=$4 AND m.enabled
                """, budget, callId, tenantId, userId, modelId, requestId);
            if (written != 1) throw new ModelStoreUnavailableException();
            return callId;
        }, token);

    public Task CompleteCallAsync(Guid callId, string outcome, long elapsedMilliseconds, ModelUsage? usage, CancellationToken token) =>
        Run(async (db, tx, budget) =>
        {
            if (callId == Guid.Empty || outcome is not ("completed" or "cancelled" or "revoked" or "timeout" or "upstream_error" or "failed")
                || elapsedMilliseconds < 0 || usage is { PromptTokens: < 0 } or { CompletionTokens: < 0 } or { TotalTokens: < 0 })
                throw new ModelStoreUnavailableException();
            await using var command = Command(db, tx, """
                UPDATE public.platform_model_calls
                SET finished_at=now(),outcome=$2,elapsed_milliseconds=$3,prompt_tokens=$4,completion_tokens=$5,total_tokens=$6
                WHERE call_id=$1 AND outcome='running' AND finished_at IS NULL
                """, callId, outcome, elapsedMilliseconds);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)usage?.PromptTokens ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)usage?.CompletionTokens ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)usage?.TotalTokens ?? DBNull.Value });
            if (await command.ExecuteNonQueryAsync(budget) != 1) throw new ModelStoreUnavailableException();
            return true;
        }, token);

    public Task<IReadOnlyList<ModelCallRecord>> ListCallsAsync(Guid tenantId, string modelId, CancellationToken token) =>
        Run<IReadOnlyList<ModelCallRecord>>(async (db, tx, budget) =>
        {
            ValidateIdentity(tenantId, modelId);
            var records = new List<ModelCallRecord>();
            await using var command = Command(db, tx, """
                SELECT call_id,user_id,request_id,outcome,elapsed_milliseconds,prompt_tokens,completion_tokens,total_tokens,started_at
                FROM public.platform_model_calls WHERE tenant_id=$1 AND model_id=$2
                ORDER BY started_at DESC,call_id DESC LIMIT 50
                """, tenantId, modelId);
            await using var reader = await command.ExecuteReaderAsync(budget);
            while (await reader.ReadAsync(budget))
                records.Add(new(reader.GetGuid(0), tenantId, reader.GetGuid(1), modelId, reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : new(reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7)), reader.GetFieldValue<DateTimeOffset>(8)));
            return records;
        }, token);

    private static void ValidateIdentity(Guid tenantId, string modelId)
    {
        if (tenantId == Guid.Empty || modelId is not { Length: >= 1 and <= 64 } || modelId[0] is < 'a' or > 'z'
            || !modelId.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-'))
            throw new ModelStoreUnavailableException();
    }
    private static bool ValidRequestId(string value) => value is { Length: 32 } && value.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9');

    private async Task<T> Run<T>(Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var value = configuration.GetConnectionString("Platform");
            if (string.IsNullOrWhiteSpace(value)) throw new ModelStoreUnavailableException();
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
        {
            throw new ModelStoreUnavailableException();
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
}
