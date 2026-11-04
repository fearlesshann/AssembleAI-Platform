using System.Data;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace AssembleAI.Platform.Connectors;

public sealed class PostgresConnectorClient(ConnectorCatalog catalog) : IConnectorClient
{
    private const int MaximumCellBytes = 4096;
    private const int MaximumResultBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task TestAsync(ConnectorSource source, IReadOnlyList<ConnectorDataset> datasets, int timeoutSeconds, CancellationToken token)
    {
        using var budget = Budget(source, timeoutSeconds, token);
        try
        {
            var controlled = ControlledDatasets(source);
            if (datasets is null || datasets.Count != controlled.Count || datasets.Any(dataset => !controlled.Contains(dataset)))
                throw new ConnectorUnavailableException();
            await using var connection = Connection(source, timeoutSeconds);
            await connection.OpenAsync(budget.Token);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, budget.Token);
            await PrepareAsync(connection, transaction, controlled, timeoutSeconds, budget.Token);
            budget.Token.ThrowIfCancellationRequested();
            await transaction.CommitAsync(budget.Token);
        }
        catch (PostgresException error) when (error.SqlState is "57014" or "55P03")
        { throw new OperationCanceledException("The connector operation timed out.", budget.Token); }
        catch (Exception error) when (SafeFailure(error)) { throw new ConnectorUnavailableException(); }
    }

    public async Task<ConnectorResult> QueryAsync(ConnectorSource source, ConnectorDataset dataset, ConnectorQuery query,
        Guid tenantId, IReadOnlySet<Guid> organizations, int timeoutSeconds, CancellationToken token)
    {
        using var budget = Budget(source, timeoutSeconds, token);
        try
        {
            var controlled = ControlledDatasets(source);
            if (!controlled.Contains(dataset) || query is null || query.Limit > source.MaximumRows)
                throw new ConnectorUnavailableException();
            var compiled = ConnectorQueryCompiler.Compile(dataset, query, tenantId, organizations);
            await using var connection = Connection(source, timeoutSeconds);
            await connection.OpenAsync(budget.Token);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, budget.Token);
            await PrepareAsync(connection, transaction, controlled, timeoutSeconds, budget.Token);
            await using var command = new NpgsqlCommand(compiled.Sql, connection, transaction) { CommandTimeout = timeoutSeconds };
            foreach (var parameter in compiled.Parameters)
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = parameter.Type, Value = parameter.Value });
            var rows = new List<object?[]>();
            // Include JSON escaping, names and punctuation. Reserve space for the HTTP request-id envelope.
            var resultBytes = JsonSerializer.SerializeToUtf8Bytes(new { columns = compiled.Columns, rows = Array.Empty<object?[]>() }).Length + 128;
            var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, budget.Token);
            var readingFailed = false;
            try
            {
                if (reader.FieldCount != compiled.Columns.Count) throw new ConnectorUnavailableException();
                for (var index = 0; index < reader.FieldCount; index++)
                    if (!Compatible(reader.GetPostgresType(index).OID, compiled.Types[index])) throw new ConnectorUnavailableException();
                while (await reader.ReadAsync(budget.Token))
                {
                    if (rows.Count == compiled.Limit) throw new ConnectorLimitException();
                    var row = new object?[reader.FieldCount];
                    for (var index = 0; index < row.Length; index++)
                    {
                        budget.Token.ThrowIfCancellationRequested();
                        if (await reader.IsDBNullAsync(index, budget.Token)) continue;
                        row[index] = await ReadCellAsync(command, reader, index, compiled.Types[index], budget.Token);
                    }
                    resultBytes += JsonSerializer.SerializeToUtf8Bytes(row).Length + (rows.Count == 0 ? 0 : 1);
                    if (resultBytes > MaximumResultBytes) throw new ConnectorLimitException();
                    rows.Add(row);
                }
            }
            catch
            {
                // Reader disposal otherwise drains unread rows. Cancel first, preserving the original failure (especially 413).
                readingFailed = true;
                Abort(command);
                throw;
            }
            finally
            {
                try { await reader.DisposeAsync(); }
                catch (Exception error) when (readingFailed && (SafeFailure(error) || error is OperationCanceledException)) { }
            }
            budget.Token.ThrowIfCancellationRequested();
            await transaction.CommitAsync(budget.Token);
            return new(compiled.Columns, rows);
        }
        catch (PostgresException error) when (error.SqlState is "57014" or "55P03")
        { throw new OperationCanceledException("The connector operation timed out.", budget.Token); }
        catch (Exception error) when (SafeFailure(error)) { throw new ConnectorUnavailableException(); }
    }

    private IReadOnlyList<ConnectorDataset> ControlledDatasets(ConnectorSource source)
    {
        if (!catalog.Options.Enabled || !ConnectorCatalog.ValidSource(source) || !source.Enabled)
            throw new ConnectorUnavailableException();
        var sourceId = catalog.Options.Sources.SingleOrDefault(item => ReferenceEquals(item.Value, source)).Key;
        if (sourceId is null) throw new ConnectorUnavailableException();
        var datasets = catalog.Options.Datasets.Values.Where(dataset => dataset.SourceId == sourceId).ToArray();
        if (datasets.Any(dataset => !ConnectorCatalog.ValidDataset(dataset))) throw new ConnectorUnavailableException();
        return datasets;
    }

    private static CancellationTokenSource Budget(ConnectorSource source, int seconds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ConnectorCatalog.ValidSource(source) || seconds < 1 || seconds > source.TimeoutSeconds || seconds > 30)
            throw new ConnectorUnavailableException();
        var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(seconds));
        return budget;
    }

    private static NpgsqlConnection Connection(ConnectorSource source, int timeoutSeconds)
    {
        var secret = Environment.GetEnvironmentVariable(source.ConnectionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 8192) throw new ConnectorUnavailableException();
        var builder = new NpgsqlConnectionStringBuilder(secret) {
            Pooling = false, Multiplexing = false, Enlist = false, Timeout = timeoutSeconds,
            CommandTimeout = timeoutSeconds, IncludeErrorDetail = false, LogParameters = false,
            ApplicationName = "AssembleAI read-only connector",
            Options = "-c default_transaction_read_only=on -c search_path=pg_catalog"
        };
        return new NpgsqlConnection(builder.ConnectionString);
    }

    private static async Task PrepareAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        IReadOnlyList<ConnectorDataset> datasets, int seconds, CancellationToken token)
    {
        // Set transaction properties before the first query. Only this fixed command contains multiple statements.
        await using (var settings = new NpgsqlCommand("SET TRANSACTION READ ONLY; SET LOCAL search_path = pg_catalog; SET LOCAL row_security = on", connection, transaction)
            { CommandTimeout = seconds })
            await settings.ExecuteNonQueryAsync(token);
        await using (var timeouts = new NpgsqlCommand("SELECT pg_catalog.set_config('statement_timeout', $1, true), pg_catalog.set_config('lock_timeout', $1, true)", connection, transaction)
            { CommandTimeout = seconds })
        {
            timeouts.Parameters.AddWithValue((seconds * 1000).ToString(System.Globalization.CultureInfo.InvariantCulture));
            await timeouts.ExecuteNonQueryAsync(token);
        }
        await using (var role = new NpgsqlCommand("SELECT rolsuper OR rolcreatedb OR rolcreaterole OR rolbypassrls OR rolreplication FROM pg_catalog.pg_roles WHERE rolname = CURRENT_USER", connection, transaction)
            { CommandTimeout = seconds })
            if (await role.ExecuteScalarAsync(token) is not false) throw new ConnectorUnavailableException();
        foreach (var dataset in datasets)
        {
            // Validate every relation controlled by this source, including ones not used by the current query.
            uint relation;
            await using (var permissions = new NpgsqlCommand("""
                SELECT c.oid, pg_catalog.has_table_privilege(CURRENT_USER, c.oid, 'SELECT'),
                  pg_catalog.has_table_privilege(CURRENT_USER, c.oid, 'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')
                    OR pg_catalog.has_any_column_privilege(CURRENT_USER, c.oid, 'INSERT,UPDATE,REFERENCES')
                FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = $1 AND c.relname = $2 AND c.relkind IN ('r','p','v','m')
                """, connection, transaction) { CommandTimeout = seconds })
            {
                permissions.Parameters.AddWithValue(dataset.Schema); permissions.Parameters.AddWithValue(dataset.Table);
                await using var reader = await permissions.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token) || !reader.GetBoolean(1) || reader.GetBoolean(2)) throw new ConnectorUnavailableException();
                relation = reader.GetFieldValue<uint>(0);
                if (await reader.ReadAsync(token)) throw new ConnectorUnavailableException();
            }
            var columns = new Dictionary<string, uint>(StringComparer.Ordinal);
            await using (var schema = new NpgsqlCommand("SELECT attname, atttypid FROM pg_catalog.pg_attribute WHERE attrelid = $1 AND attnum > 0 AND NOT attisdropped", connection, transaction)
                { CommandTimeout = seconds })
            {
                schema.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Oid, relation);
                await using var reader = await schema.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) columns.Add(reader.GetString(0), reader.GetFieldValue<uint>(1));
            }
            if (!columns.TryGetValue(dataset.TenantColumn, out var tenantType) || tenantType != 2950
                || !columns.TryGetValue(dataset.OrganizationColumn, out var scopeType) || scopeType != 2950)
                throw new ConnectorUnavailableException();
            foreach (var column in dataset.Columns.Values)
                if (!columns.TryGetValue(column.Column, out var type) || !Compatible(type, column.Type)) throw new ConnectorUnavailableException();
        }
    }

    private static async Task<object> ReadCellAsync(NpgsqlCommand command, NpgsqlDataReader reader, int index, string type, CancellationToken token)
    {
        switch (type)
        {
            case "text":
                var text = reader.GetTextReader(index);
                var textFailed = false;
                try
                {
                    // One extra character distinguishes an exact-size value from an incomplete read.
                    var buffer = new char[MaximumCellBytes + 1]; var length = 0;
                    while (length < buffer.Length)
                    {
                        var read = await text.ReadAsync(buffer.AsMemory(length, buffer.Length - length), token);
                        if (read == 0) break;
                        length += read;
                    }
                    if (length > MaximumCellBytes) throw new ConnectorLimitException();
                    var value = new string(buffer, 0, length);
                    if (StrictUtf8.GetByteCount(value) > MaximumCellBytes) throw new ConnectorLimitException();
                    return value;
                }
                catch
                {
                    textFailed = true;
                    Abort(command);
                    throw;
                }
                finally
                {
                    try { text.Dispose(); }
                    catch (Exception error) when (textFailed && (SafeFailure(error) || error is OperationCanceledException)) { }
                }
            case "integer":
                return reader.GetPostgresType(index).OID switch {
                    21 => (long)reader.GetInt16(index), 23 => (long)reader.GetInt32(index), 20 => reader.GetInt64(index),
                    _ => throw new ConnectorUnavailableException()
                };
            case "decimal": return reader.GetDecimal(index);
            case "date":
                var days = reader.GetFieldValue<int>(index);
                if (days is int.MinValue or int.MaxValue) throw new ConnectorUnavailableException();
                return new DateOnly(2000, 1, 1).AddDays(days);
            default: throw new ConnectorUnavailableException();
        }
    }

    // Built-in OIDs deliberately exclude domains, user-defined casts, arrays, NaN/Infinity-capable floats, and JSON.
    private static bool Compatible(uint oid, string type) => type switch {
        "text" => oid is 25 or 1042 or 1043, "integer" => oid is 20 or 21 or 23,
        "decimal" => oid == 1700, "date" => oid == 1082, _ => false
    };
    private static void Abort(NpgsqlCommand command)
    {
        try { command.Cancel(); }
        catch (Exception error) when (SafeFailure(error) || error is OperationCanceledException) { }
    }
    private static bool SafeFailure(Exception error) => error is NpgsqlException or ArgumentException or InvalidOperationException
        or InvalidCastException or OverflowException or IOException or JsonException;
}
