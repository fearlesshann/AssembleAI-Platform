using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using NpgsqlTypes;

namespace AssembleAI.Platform.Identity.Sessions;

/// <summary>Shared sessions; PostgreSQL contains only protected ticket bytes and absolute expiry.</summary>
public sealed class PostgresTicketStore(
    IConfiguration configuration, IDataProtectionProvider provider, TimeProvider clock) : ITicketStore
{
    private const string ProtectionPurpose = "AssembleAI.Platform.Identity.BffSessionTicket.v1";
    private const string StoreSql = """
        WITH expired AS (
            SELECT session_key FROM public.platform_bff_sessions
            WHERE expires_at <= $4 ORDER BY expires_at LIMIT 128 FOR UPDATE SKIP LOCKED
        ), removed AS (
            DELETE FROM public.platform_bff_sessions s USING expired e WHERE s.session_key = e.session_key
        )
        INSERT INTO public.platform_bff_sessions(session_key, protected_ticket, expires_at) VALUES ($1, $2, $3)
        """;

    private const string RenewSql = """
        UPDATE public.platform_bff_sessions
        SET protected_ticket = $2, expires_at = LEAST(expires_at, $3)
        WHERE session_key = $1 AND expires_at > $4
        """;

    public Task<string> StoreAsync(AuthenticationTicket ticket) => StoreAsync(ticket, CancellationToken.None);

    public Task<string> StoreAsync(AuthenticationTicket ticket, HttpContext context, CancellationToken cancellationToken = default) =>
        StoreAsync(ticket, cancellationToken);

    public async Task<string> StoreAsync(AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var connection = CreateConnection();
            var now = clock.GetUtcNow();
            var key = SessionTicket.NewKey();
            var snapshot = SessionTicket.Copy(ticket, now);
            var protectedBytes = Protector(key).Protect(snapshot.Bytes);
            await connection.OpenAsync(cancellationToken);
            await using var command = WriteCommand(StoreSql, connection, key, protectedBytes, snapshot.ExpiresUtc, now);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return key;
        }
        catch (Exception exception) when (IsStoreFailure(exception, cancellationToken))
        {
            throw new BffSessionStoreException();
        }
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket) => RenewAsync(key, ticket, CancellationToken.None);

    public Task RenewAsync(string key, AuthenticationTicket ticket, HttpContext context, CancellationToken cancellationToken = default) =>
        RenewAsync(key, ticket, cancellationToken);

    public async Task RenewAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var connection = CreateConnection();
            var now = clock.GetUtcNow();
            var snapshot = SessionTicket.Copy(ticket, now);
            var protectedBytes = Protector(key).Protect(snapshot.Bytes);
            await connection.OpenAsync(cancellationToken);
            await using var command = WriteCommand(RenewSql, connection, key, protectedBytes, snapshot.ExpiresUtc, clock.GetUtcNow());
            // A single conditional UPDATE cannot insert a missing row or extend its deadline.
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception exception) when (IsStoreFailure(exception, cancellationToken))
        {
            throw new BffSessionStoreException();
        }
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key) => RetrieveAsync(key, CancellationToken.None);

    public async Task<AuthenticationTicket?> RetrieveAsync(string key, HttpContext context, CancellationToken cancellationToken = default)
    {
        context.Features.Set<LiveBffSessionReference>(null);
        var ticket = await RetrieveAsync(key, cancellationToken);
        if (ticket is not null) context.Features.Set(new LiveBffSessionReference(key));
        return ticket;
    }

    public async Task<AuthenticationTicket?> RetrieveAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("""
                SELECT protected_ticket, expires_at FROM public.platform_bff_sessions
                WHERE session_key = $1 AND expires_at > $2
                """, connection) { CommandTimeout = 5 };
            Add(command, NpgsqlDbType.Text, key);
            Add(command, NpgsqlDbType.TimestampTz, clock.GetUtcNow());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            var protectedBytes = reader.GetFieldValue<byte[]>(0);
            var expires = reader.GetFieldValue<DateTimeOffset>(1);
            if (expires <= clock.GetUtcNow()) return null;
            return Unprotect(key, protectedBytes, expires);
        }
        catch (Exception exception) when (IsStoreFailure(exception, cancellationToken))
        {
            throw new BffSessionStoreException();
        }
    }

    public Task RemoveAsync(string key) => RemoveAsync(key, CancellationToken.None);

    public Task RemoveAsync(string key, HttpContext context, CancellationToken cancellationToken = default) =>
        RemoveAsync(key, cancellationToken);

    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                "DELETE FROM public.platform_bff_sessions WHERE session_key = $1", connection) { CommandTimeout = 5 };
            Add(command, NpgsqlDbType.Text, key);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception exception) when (IsStoreFailure(exception, cancellationToken))
        {
            throw new BffSessionStoreException();
        }
    }

    private IDataProtector Protector(string key) => provider.CreateProtector(ProtectionPurpose, key);

    private AuthenticationTicket? Unprotect(string key, byte[] protectedBytes, DateTimeOffset databaseExpiry)
    {
        try
        {
            var ticket = TicketSerializer.Default.Deserialize(Protector(key).Unprotect(protectedBytes));
            if (ticket?.Properties.ExpiresUtc is not { } ticketExpiry) return null;
            var expires = ticketExpiry < databaseExpiry ? ticketExpiry : databaseExpiry;
            if (expires <= clock.GetUtcNow()) return null;
            ticket.Properties.ExpiresUtc = expires;
            return ticket;
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException
            or FormatException or InvalidOperationException or IOException)
        {
            // Wrong keys, tampering and unknown ticket formats fail authentication without logging contents.
            return null;
        }
    }

    private NpgsqlConnection CreateConnection()
    {
        var connectionString = configuration.GetConnectionString("Platform");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new BffSessionStoreException();
        var options = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Timeout = 5,
            CommandTimeout = 5,
            IncludeErrorDetail = false
        };
        return new NpgsqlConnection(options.ConnectionString);
    }

    private static NpgsqlCommand WriteCommand(string sql, NpgsqlConnection connection, string key,
        byte[] protectedBytes, DateTimeOffset expires, DateTimeOffset now)
    {
        var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 5 };
        Add(command, NpgsqlDbType.Text, key);
        Add(command, NpgsqlDbType.Bytea, protectedBytes);
        Add(command, NpgsqlDbType.TimestampTz, expires);
        Add(command, NpgsqlDbType.TimestampTz, now);
        return command;
    }

    private static void Add(NpgsqlCommand command, NpgsqlDbType type, object value) =>
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = type, Value = value });

    private static bool IsStoreFailure(Exception exception, CancellationToken cancellationToken) =>
        !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        && exception is NpgsqlException or TimeoutException
            or ArgumentException or InvalidOperationException or IOException or CryptographicException
            or OperationCanceledException;
}
