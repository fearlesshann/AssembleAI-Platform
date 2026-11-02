using System.Collections.Frozen;
using Npgsql;
using NpgsqlTypes;

namespace AssembleAI.Platform.Authorization;

/// <summary>
/// Reads each decision from one PostgreSQL statement snapshot. No positive or negative
/// authorization cache is retained. Revocations committed before the statement starts
/// are visible; a previously issued decision is not retroactively cancelled.
/// </summary>
public sealed class PostgresAuthorizationStore(IConfiguration configuration) : IAuthorizationStore
{
    private const string ListTenantsSql = """
        SELECT t.tenant_id, t.name
        FROM public.platform_users u
        JOIN public.tenant_memberships m ON m.user_id = u.user_id AND m.enabled
        JOIN public.platform_tenants t ON t.tenant_id = m.tenant_id AND t.enabled
        WHERE u.issuer = $1 AND u.subject = $2 AND u.enabled
        ORDER BY t.name COLLATE "C", t.tenant_id
        """;

    private const string ReadAccessSql = """
        WITH access AS (
            SELECT u.user_id, t.tenant_id, t.authorization_version
            FROM public.platform_users u
            JOIN public.tenant_memberships m ON m.user_id = u.user_id AND m.enabled
            JOIN public.platform_tenants t ON t.tenant_id = m.tenant_id AND t.enabled
            WHERE u.issuer = $1 AND u.subject = $2 AND u.enabled AND t.tenant_id = $3
        ), enabled_organizations AS (
            SELECT array_agg(o.organization_id ORDER BY o.organization_id) AS ids
            FROM public.tenant_organizations o
            JOIN access a ON a.tenant_id = o.tenant_id
            WHERE o.enabled
        )
        SELECT a.user_id, a.tenant_id, a.authorization_version,
            COALESCE(e.ids, '{}'::uuid[]), g.grant_id,
            g.permission, g.resource_type, g.resource_id, g.tenant_wide,
            COALESCE(scope.ids, '{}'::uuid[])
        FROM access a
        CROSS JOIN enabled_organizations e
        LEFT JOIN LATERAL (
            SELECT g.grant_id, g.permission, g.resource_type, g.resource_id, g.tenant_wide
            FROM public.tenant_member_roles mr
            JOIN public.tenant_roles r ON r.tenant_id = mr.tenant_id AND r.role_id = mr.role_id AND r.enabled
            JOIN public.role_permission_grants g ON g.tenant_id = r.tenant_id AND g.role_id = r.role_id AND g.enabled
            WHERE mr.tenant_id = a.tenant_id AND mr.user_id = a.user_id
        ) g ON true
        LEFT JOIN LATERAL (
            SELECT array_agg(o.organization_id ORDER BY o.organization_id) AS ids
            FROM public.grant_organizations go
            JOIN public.tenant_organizations o
                ON o.tenant_id = go.tenant_id AND o.organization_id = go.organization_id AND o.enabled
            WHERE go.tenant_id = a.tenant_id AND go.grant_id = g.grant_id
        ) scope ON true
        ORDER BY g.grant_id
        """;

    public async Task<IReadOnlyList<TenantMembership>> ListTenantsAsync(
        SubjectIdentity identity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValid(identity)) return [];

        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = CreateCommand(ListTenantsSql, connection, identity);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var memberships = new List<TenantMembership>();
            while (await reader.ReadAsync(cancellationToken))
                memberships.Add(new TenantMembership(reader.GetGuid(0), reader.GetString(1)));
            return memberships.AsReadOnly();
        }
        catch (Exception exception) when (IsStoreFailure(exception, cancellationToken))
        {
            throw new AuthorizationStoreUnavailableException(exception);
        }
    }

    public async Task<AccessSnapshot?> ReadAccessAsync(
        SubjectIdentity identity, Guid tenantId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValid(identity) || tenantId == Guid.Empty) return null;

        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = CreateCommand(ReadAccessSql, connection, identity);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = tenantId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;

            var userId = reader.GetGuid(0);
            var version = reader.GetInt64(2);
            var enabledOrganizations = reader.GetFieldValue<Guid[]>(3).ToFrozenSet();
            var grants = new List<PermissionGrant>();
            do
            {
                // A current member with no roles still has a snapshot, with no grants.
                if (!reader.IsDBNull(4))
                    grants.Add(new PermissionGrant(reader.GetString(5), reader.GetString(6), reader.GetString(7),
                        reader.GetBoolean(8), reader.GetFieldValue<Guid[]>(9).ToFrozenSet()));
            } while (await reader.ReadAsync(cancellationToken));

            return new AccessSnapshot(userId, tenantId, version, grants.AsReadOnly(), enabledOrganizations);
        }
        catch (Exception exception) when (IsStoreFailure(exception, cancellationToken))
        {
            throw new AuthorizationStoreUnavailableException(exception);
        }
    }

    private NpgsqlConnection CreateConnection()
    {
        var connectionString = configuration.GetConnectionString("Platform");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new AuthorizationStoreUnavailableException();
        var options = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Timeout = 5,
            CommandTimeout = 5,
            IncludeErrorDetail = false
        };
        return new NpgsqlConnection(options.ConnectionString);
    }

    private static NpgsqlCommand CreateCommand(string sql, NpgsqlConnection connection, SubjectIdentity identity)
    {
        var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 5 };
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = identity.Issuer });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = identity.Subject });
        return command;
    }

    private static bool IsValid(SubjectIdentity identity) => identity is not null
        && !string.IsNullOrWhiteSpace(identity.Issuer) && identity.Issuer.Length <= 2048
        && !string.IsNullOrWhiteSpace(identity.Subject) && identity.Subject.Length <= 512;

    private static bool IsStoreFailure(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && exception is NpgsqlException or TimeoutException or ArgumentException
            or InvalidOperationException or IOException or OperationCanceledException;
}
