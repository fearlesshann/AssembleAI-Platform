using Npgsql;

namespace AssembleAI.Platform.IdentityManagement;

/// <summary>
/// A conservative bound shared by management reads and transactional mutations.
/// JSON escaping uses at most six output bytes per input UTF8 byte (ASCII controls
/// are the worst ratio; supplementary Unicode uses at most twelve per four).
/// Each DTO's fixed property names, punctuation, UUIDs, booleans and safe revision
/// total less than 512 bytes; a UUID array entry occupies at most 39, rounded to 40.
/// Fifty audit DTOs with all varchar fields at their Unicode limits use less than
/// 300 KiB; reserve 512 KiB so the audit tail cannot strand a previously valid page.
/// Member display names and the tenant name reserve their full 256-scalar limit:
/// global user/tenant edits therefore cannot invalidate a tenant's budget.
/// </summary>
public static class IdentityResponseBudget
{
    public const long MaximumResponseBytes = 4 * 1024 * 1024;
    public const long EnvelopeBytes = 512;
    public const long AuditReserveBytes = 512 * 1024;
    public const long EntityOverheadBytes = 512;
    public const long MaximumDisplayNameBytes = 256 * 4 * 6;
    public const long UuidArrayEntryBytes = 40;

    // The bootstrap transaction uses the same expression before it commits.
    public const string GlobalEstimateSql = """
        SELECT 512::bigint + 524288
            + COALESCE((SELECT SUM(512::bigint + 6::bigint *
                (octet_length(issuer) + octet_length(subject) + octet_length(display_name)))
                FROM public.platform_users), 0)
            + COALESCE((SELECT SUM(512::bigint + 6::bigint * octet_length(name))
                FROM public.platform_tenants), 0)
        """;
    private const string TenantEstimateSql = """
        SELECT 512::bigint + 524288 + 512 + 6144
            + (SELECT count(*) * (512::bigint + 6144) FROM public.tenant_memberships WHERE tenant_id=$1)
            + (SELECT count(*) * 40::bigint FROM public.tenant_member_roles WHERE tenant_id=$1)
            + COALESCE((SELECT SUM(512::bigint + 6::bigint * (octet_length(name) + octet_length(kind)))
                FROM public.tenant_organizations WHERE tenant_id=$1), 0)
            + COALESCE((SELECT SUM(512::bigint + 6::bigint * octet_length(name))
                FROM public.tenant_roles WHERE tenant_id=$1), 0)
            + COALESCE((SELECT SUM(512::bigint + 6::bigint *
                (octet_length(permission) + octet_length(resource_type) + octet_length(resource_id)))
                FROM public.role_permission_grants WHERE tenant_id=$1 AND enabled), 0)
            + (SELECT count(*) * 40::bigint FROM public.grant_organizations o
                JOIN public.role_permission_grants g USING(tenant_id,grant_id) WHERE o.tenant_id=$1 AND g.enabled)
        """;

    public static Task<bool> FitsGlobalAsync(NpgsqlConnection db, CancellationToken token) => Fits(db, GlobalEstimateSql, null, token);
    public static Task<bool> FitsTenantAsync(NpgsqlConnection db, Guid tenant, CancellationToken token) => Fits(db, TenantEstimateSql, tenant, token);
    private static async Task<bool> Fits(NpgsqlConnection db, string sql, Guid? tenant, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, db) { CommandTimeout = 3 };
        if (tenant.HasValue) command.Parameters.AddWithValue(tenant.Value);
        // PostgreSQL SUM(bigint) is numeric; decimal preserves this bounded estimate exactly.
        return Convert.ToDecimal(await command.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture) <= MaximumResponseBytes;
    }
}

internal sealed class IdentityCapacityExceededException : Exception;
