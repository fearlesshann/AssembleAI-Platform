using System.Text.Json;
using AssembleAI.Platform.Registry;
using Npgsql;

namespace AssembleAI.Platform.InternalCalls;

public sealed class PostgresApplicationCallStore(IConfiguration configuration) : IApplicationCallStore
{
    public async Task<ApplicationManifest?> ReadAsync(Guid tenantId, string appId, string releaseVersion, CancellationToken token)
    {
        try
        {
            var value = configuration.GetConnectionString("Platform");
            if (string.IsNullOrWhiteSpace(value)) throw new ApplicationCallStoreUnavailableException();
            var settings = new NpgsqlConnectionStringBuilder(value) { Timeout = 3, CommandTimeout = 3, IncludeErrorDetail = false };
            await using var db = new NpgsqlConnection(settings.ConnectionString);
            await db.OpenAsync(token);
            await using var command = new NpgsqlCommand("""
                SELECT r.manifest FROM public.platform_application_releases r
                JOIN public.tenant_applications t ON t.app_id=r.app_id
                WHERE t.tenant_id=$1 AND r.app_id=$2 AND r.release_version=$3 AND t.enabled AND r.status='published'
                """, db) { CommandTimeout = 3 };
            command.Parameters.AddWithValue(tenantId);
            command.Parameters.AddWithValue(appId);
            command.Parameters.AddWithValue(releaseVersion);
            var result = await command.ExecuteScalarAsync(token);
            if (result is null) return null;
            using var json = JsonDocument.Parse((string)result);
            if (!ManifestValidator.TryParse(json.RootElement, out var manifest)) throw new ApplicationCallStoreUnavailableException();
            return manifest;
        }
        catch (Exception error) when (error is NpgsqlException or IOException or TimeoutException or ArgumentException or InvalidOperationException or JsonException)
        { throw new ApplicationCallStoreUnavailableException(); }
    }
}
