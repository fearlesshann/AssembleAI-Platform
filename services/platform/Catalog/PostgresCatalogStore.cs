using System.Data;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Registry;
using Npgsql;

namespace AssembleAI.Platform.Catalog;

public sealed class PostgresCatalogStore(IConfiguration configuration, IAuthorizationStore authorization,
    RegistryDeploymentCatalog catalog) : ICatalogStore
{
    public const int MaximumVisibleApplications = 500;

    public async Task<CatalogResult<CatalogListing>> ListAsync(SubjectIdentity subject, Guid tenantId, CancellationToken token = default)
    {
        var result = await Read(subject, tenantId, null, token);
        if (!result.IsMember) return new(false, null);
        var items = result.Value!.OrderBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.AppId, StringComparer.Ordinal).ToArray();
        return new(true, new(items, items.Select(item => item.Category).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
    }

    public async Task<CatalogResult<CatalogApplication>> ReadAsync(SubjectIdentity subject, Guid tenantId, string appId, CancellationToken token = default)
    {
        var result = await Read(subject, tenantId, appId, token);
        return new(result.IsMember, result.Value?.SingleOrDefault());
    }

    private async Task<CatalogResult<List<CatalogApplication>>> Read(SubjectIdentity subject, Guid tenantId, string? appId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var access = await authorization.ReadAccessAsync(subject, tenantId, budget.Token);
            if (access is null || access.TenantId != tenantId || access.UserId == Guid.Empty || access.AuthorizationVersion < 1)
                return new(false, null);
            if (appId is not null && !ManifestValidator.IsAppId(appId)) return new(true, []);
            // Entry resources have no organization. PermissionEvaluator therefore
            // accepts only tenant-wide grants for this exact application resource.
            // Index these tuples, preserving permission/resource/scope together.
            var grants = access.Grants.Where(grant => grant.TenantWide && grant.ResourceType == "application"
                    && ManifestValidator.IsAppId(grant.ResourceId) && (appId is null || grant.ResourceId == appId))
                .GroupBy(grant => grant.ResourceId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Select(grant => grant.Permission).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
            if (grants.Count == 0) return new(true, []);
            var value = configuration.GetConnectionString("Platform");
            if (string.IsNullOrWhiteSpace(value)) throw new CatalogStoreUnavailableException();
            var settings = new NpgsqlConnectionStringBuilder(value) { Timeout = 3, CommandTimeout = 3, IncludeErrorDetail = false };
            await using var db = new NpgsqlConnection(settings.ConnectionString);
            await db.OpenAsync(budget.Token);
            await using var command = new NpgsqlCommand("""
                SELECT p.manifest
                FROM public.tenant_applications t
                JOIN public.platform_application_releases p ON p.app_id=t.app_id AND p.release_version=t.release_version
                WHERE t.tenant_id=$1 AND t.app_id=ANY($2) AND t.enabled AND p.status='published'
                ORDER BY t.app_id COLLATE "C"
                """, db) { CommandTimeout = 3 };
            command.Parameters.AddWithValue(tenantId);
            command.Parameters.AddWithValue(grants.Keys.ToArray());
            // Stream rows; retain at most 501 display DTOs, never materialize the
            // tenant's full manifests. Over-capacity is an explicit failure.
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, budget.Token);
            var items = new List<CatalogApplication>();
            while (await reader.ReadAsync(budget.Token))
            {
                budget.Token.ThrowIfCancellationRequested();
                using var json = JsonDocument.Parse(reader.GetString(0));
                if (!ManifestValidator.TryParse(json.RootElement, out var manifest)) throw new CatalogStoreUnavailableException();
                if (!grants.TryGetValue(manifest!.AppId, out var permissions)
                    || !manifest.Permissions.All(permissions.Contains) || !catalog.Allows(manifest)) continue;
                items.Add(new(manifest.AppId, manifest.Name, manifest.Version, manifest.Category, manifest.Description));
                if (items.Count > MaximumVisibleApplications) throw new CatalogStoreUnavailableException();
            }
            budget.Token.ThrowIfCancellationRequested();
            return new(true, items);
        }
        catch (Exception error) when (!(error is OperationCanceledException && token.IsCancellationRequested)
            && error is NpgsqlException or IOException or TimeoutException or ArgumentException or InvalidOperationException or JsonException or OperationCanceledException)
        { throw new CatalogStoreUnavailableException(); }
    }
}
