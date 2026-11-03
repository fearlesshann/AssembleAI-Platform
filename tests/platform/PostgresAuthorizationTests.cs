using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AssembleAI.Platform.SmokeTests;

/// <summary>Only called after the integration dispatcher verifies the isolated database's run marker.</summary>
public static class PostgresAuthorizationTests
{
    public static async Task RunAsync(string connectionString)
    {
        var rows = new Fixture();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Platform"] = connectionString
        }).Build();
        var store = new PostgresAuthorizationStore(configuration);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        try
        {
            await Seed(connection, rows);
            await VerifyScopeAndIdentity(store, rows);
            await VerifyCompositeKeys(connection, rows);
            await VerifyHttp(connectionString, rows);
            await VerifyRollbackAndRevocation(connection, store, rows);
            await VerifyCancellation(store, rows);
        }
        finally
        {
            // Delete only this fixture's random IDs; no schema reset or shared-row truncation.
            await Execute(connection, "DELETE FROM public.platform_tenants WHERE tenant_id IN ($1, $2)",
                [rows.TenantA, rows.TenantB]);
            await Execute(connection, "DELETE FROM public.platform_users WHERE user_id IN ($1, $2)",
                [rows.UserA, rows.UserB]);
        }
        Console.WriteLine("PASS: PostgreSQL authorization: real snapshots, issuer/tenant/factory isolation, composite foreign keys, transactional versions, revocation, HTTP boundaries and cancellation.");
    }

    private static async Task Seed(NpgsqlConnection connection, Fixture f)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, """
            INSERT INTO public.platform_users(user_id, issuer, subject, display_name)
            VALUES ($1, $2, $3, 'Fixture Alice'), ($4, $5, $3, 'Fixture Other Issuer')
            """, [f.UserA, f.IssuerA, f.Subject, f.UserB, f.IssuerB], transaction);
        await Execute(connection, """
            INSERT INTO public.platform_tenants(tenant_id, name) VALUES ($1, 'Fixture A'), ($2, 'Fixture B')
            """, [f.TenantA, f.TenantB], transaction);
        await Execute(connection, """
            INSERT INTO public.tenant_memberships(tenant_id, user_id) VALUES ($1, $2), ($3, $4)
            """, [f.TenantA, f.UserA, f.TenantB, f.UserB], transaction);
        await Execute(connection, """
            INSERT INTO public.tenant_organizations(tenant_id, organization_id, name, kind)
            VALUES ($1, $2, 'Factory A1', 'factory'), ($1, $3, 'Factory A2', 'factory'),
                ($1, $4, 'Unlisted factory', 'factory'), ($5, $6, 'Factory B1', 'factory'),
                ($5, $7, 'Factory B2', 'factory')
            """, [f.TenantA, f.FactoryA1, f.FactoryA2, f.UnlistedFactory, f.TenantB, f.FactoryB1, f.FactoryB2], transaction);
        await Execute(connection, """
            INSERT INTO public.tenant_roles(tenant_id, role_id, name)
            VALUES ($1, $2, 'Production'), ($1, $3, 'Quality'), ($4, $5, 'Production'), ($4, $6, 'Quality')
            """, [f.TenantA, f.RoleA1, f.RoleA2, f.TenantB, f.RoleB1, f.RoleB2], transaction);
        await Execute(connection, """
            INSERT INTO public.tenant_member_roles(tenant_id, user_id, role_id)
            VALUES ($1, $2, $3), ($1, $2, $4), ($5, $6, $7), ($5, $6, $8)
            """, [f.TenantA, f.UserA, f.RoleA1, f.RoleA2, f.TenantB, f.UserB, f.RoleB1, f.RoleB2], transaction);
        await Execute(connection, """
            INSERT INTO public.role_permission_grants(tenant_id, grant_id, role_id, permission, resource_type, resource_id)
            VALUES ($1, $2, $3, 'production:read', 'dataset', 'production'),
                ($1, $4, $5, 'quality:read', 'dataset', 'quality'),
                ($6, $7, $8, 'production:read', 'dataset', 'production'),
                ($6, $9, $10, 'quality:read', 'dataset', 'quality')
            """, [f.TenantA, f.GrantA1, f.RoleA1, f.GrantA2, f.RoleA2,
                f.TenantB, f.GrantB1, f.RoleB1, f.GrantB2, f.RoleB2], transaction);
        await Execute(connection, """
            INSERT INTO public.grant_organizations(tenant_id, grant_id, organization_id)
            VALUES ($1, $2, $3), ($1, $4, $5), ($6, $7, $8), ($6, $9, $10)
            """, [f.TenantA, f.GrantA1, f.FactoryA1, f.GrantA2, f.FactoryA2,
                f.TenantB, f.GrantB1, f.FactoryB1, f.GrantB2, f.FactoryB2], transaction);
        await transaction.CommitAsync();
    }

    private static async Task VerifyScopeAndIdentity(PostgresAuthorizationStore store, Fixture f)
    {
        var tenants = await store.ListTenantsAsync(f.Alice);
        var otherTenants = await store.ListTenantsAsync(f.OtherIssuer);
        Assert(tenants.Count == 1 && tenants[0].TenantId == f.TenantA, "V4: SQL must list only this user's memberships.");
        Assert(otherTenants.Count == 1 && otherTenants[0].TenantId == f.TenantB,
            "V14: equal subjects at different issuers must resolve different users.");
        Assert((await store.ListTenantsAsync(new(f.IssuerA, "unregistered"))).Count == 0,
            "V6: an unregistered subject must not receive a default membership.");
        Assert(await store.ReadAccessAsync(f.Alice, f.TenantB) is null
            && await store.ReadAccessAsync(f.Alice, Guid.NewGuid()) is null, "V4: foreign and unknown tenants must return no access.");

        var access = await store.ReadAccessAsync(f.Alice, f.TenantA);
        Assert(access is not null && access.UserId == f.UserA && access.TenantId == f.TenantA
            && access.Grants.Count == 2 && access.EnabledOrganizationIds.SetEquals([f.FactoryA1, f.FactoryA2, f.UnlistedFactory]),
            "SQL must map real UUID arrays, bound grants and internal organization validation data.");
        Assert(access!.Grants.Single(grant => grant.Permission == "production:read").OrganizationIds.SetEquals([f.FactoryA1]),
            "V5: a grant must carry only its own organization array.");
        Assert(PermissionEvaluator.Allows(access, "production:read", f.ProductionA), "V6: the matching factory grant must work.");
        Assert(!PermissionEvaluator.Allows(access, "production:read", f.ProductionA with { OrganizationId = f.FactoryA2 })
            && !PermissionEvaluator.Allows(access, "production:read", f.ProductionA with { TenantId = f.TenantB })
            && !PermissionEvaluator.Allows(access, "production:read", f.ProductionA with { OrganizationId = f.FactoryB1 }),
            "V4/V5/V7: real SQL grants must not combine unrelated scopes or cross tenant boundaries.");
        var other = await store.ReadAccessAsync(f.OtherIssuer, f.TenantB);
        Assert(PermissionEvaluator.Allows(other, "production:read", new(f.TenantB, "dataset", "production", f.FactoryB1))
            && !PermissionEvaluator.Allows(other, "production:read", new(f.TenantB, "dataset", "production", f.FactoryB2)),
            "V5: the second tenant must independently enforce its two factory scopes.");
        Assert(await store.ReadAccessAsync(new(f.IssuerA, f.Subject + "' OR true --"), f.TenantA) is null,
            "V14: SQL-looking subject text must remain a parameter, never expand identity lookup.");
    }

    private static async Task VerifyCompositeKeys(NpgsqlConnection connection, Fixture f)
    {
        await ExpectForeignKey(connection, "INSERT INTO public.tenant_member_roles VALUES ($1, $2, $3)",
            [f.TenantA, f.UserA, f.RoleB1]);
        await ExpectForeignKey(connection, "INSERT INTO public.tenant_member_roles VALUES ($1, $2, $3)",
            [f.TenantB, f.UserA, f.RoleB1]);
        await ExpectForeignKey(connection, "INSERT INTO public.grant_organizations VALUES ($1, $2, $3)",
            [f.TenantA, f.GrantA1, f.FactoryB1]);
    }

    private static async Task ExpectForeignKey(NpgsqlConnection connection, string sql, object[] values)
    {
        try { await Execute(connection, sql, values); }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ForeignKeyViolation) { return; }
        throw new InvalidOperationException("V4: a cross-tenant relationship must fail its composite foreign key.");
    }

    private static async Task VerifyRollbackAndRevocation(NpgsqlConnection connection, PostgresAuthorizationStore store, Fixture f)
    {
        var initial = (await store.ReadAccessAsync(f.Alice, f.TenantA))!;
        var otherVersion = await Version(connection, f.TenantB);
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await Execute(connection, "UPDATE public.role_permission_grants SET enabled=false WHERE tenant_id=$1 AND grant_id=$2",
                [f.TenantA, f.GrantA1], transaction);
            var concurrent = await store.ReadAccessAsync(f.Alice, f.TenantA);
            Assert(concurrent?.AuthorizationVersion == initial.AuthorizationVersion
                && PermissionEvaluator.Allows(concurrent, "production:read", f.ProductionA),
                "Uncommitted revocation and its version increment must not escape to another query snapshot.");
            await transaction.RollbackAsync();
        }
        var afterRollback = await store.ReadAccessAsync(f.Alice, f.TenantA);
        Assert(afterRollback?.AuthorizationVersion == initial.AuthorizationVersion
            && PermissionEvaluator.Allows(afterRollback, "production:read", f.ProductionA),
            "Rolled-back revocation must preserve grants and authorization version.");

        (string Sql, object[] Keys, bool MembershipRemains)[] changes = [
            ("UPDATE public.role_permission_grants SET enabled=$1 WHERE tenant_id=$2 AND grant_id=$3", [f.TenantA, f.GrantA1], true),
            ("UPDATE public.tenant_organizations SET enabled=$1 WHERE tenant_id=$2 AND organization_id=$3", [f.TenantA, f.FactoryA1], true),
            ("UPDATE public.tenant_roles SET enabled=$1 WHERE tenant_id=$2 AND role_id=$3", [f.TenantA, f.RoleA1], true),
            ("UPDATE public.tenant_memberships SET enabled=$1 WHERE tenant_id=$2 AND user_id=$3", [f.TenantA, f.UserA], false),
            ("UPDATE public.platform_users SET enabled=$1 WHERE user_id=$2", [f.UserA], false),
            ("UPDATE public.platform_tenants SET enabled=$1 WHERE tenant_id=$2", [f.TenantA], false)
        ];
        foreach (var change in changes)
        {
            var before = await Version(connection, f.TenantA);
            Assert(await Execute(connection, change.Sql, [false, .. change.Keys]) == 1, "The revocation must change one fixture row.");
            try
            {
                var revoked = await store.ReadAccessAsync(f.Alice, f.TenantA);
                Assert((revoked is not null) == change.MembershipRemains
                    && !PermissionEvaluator.Allows(revoked, "production:read", f.ProductionA),
                    "V25: each committed grant, organization, role, membership, user or tenant revocation must affect a fresh query.");
                Assert(await Version(connection, f.TenantA) > before, "Committed authorization changes must advance the tenant version.");
                if (!change.MembershipRemains)
                    Assert((await store.ListTenantsAsync(f.Alice)).Count == 0, "Disabled membership chains must disappear from tenant lists.");
            }
            finally { await Execute(connection, change.Sql, [true, .. change.Keys]); }
            Assert(PermissionEvaluator.Allows(await store.ReadAccessAsync(f.Alice, f.TenantA), "production:read", f.ProductionA),
                "Restoring the explicit fixture grant chain must restore access on a fresh query.");
        }
        Assert(await Version(connection, f.TenantB) == otherVersion,
            "V4: another tenant's authorization version must not change when only tenant A is modified.");
    }

    private static async Task VerifyHttp(string connectionString, Fixture f)
    {
        await using var app = PlatformApplication.Build([
            "--environment", "Development", "--urls", "http://127.0.0.1:0", "--Authentication:Bff:Enabled", "false"
        ], builder =>
        {
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Platform"] = connectionString });
            builder.Services.AddSingleton(f);
            builder.Services.AddAuthentication("PostgresFixture")
                .AddScheme<AuthenticationSchemeOptions, FixtureAuthenticationHandler>("PostgresFixture", _ => { });
        });
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
            await ExpectHttp(client, "/api/platform/v1/tenants", HttpStatusCode.Unauthorized);
            client.DefaultRequestHeaders.Authorization = new("PostgresFixture", "fixture");
            using (var list = JsonDocument.Parse(await ExpectHttp(client, "/api/platform/v1/tenants", HttpStatusCode.OK)))
            {
                var items = list.RootElement.GetProperty("items");
                Assert(items.GetArrayLength() == 1 && items[0].GetProperty("tenantId").GetGuid() == f.TenantA,
                    "V4: the real HTTP endpoint must expose only the real database membership.");
            }
            var body = await ExpectHttp(client, $"/api/platform/v1/tenants/{f.TenantA}/access", HttpStatusCode.OK);
            Assert(!body.Contains(f.UnlistedFactory.ToString(), StringComparison.Ordinal)
                && !body.Contains("enabledOrganizationIds", StringComparison.Ordinal),
                "V4/V25: SQL's internal organization validation set must not leak through the HTTP projection.");
            await ExpectHttp(client, $"/api/platform/v1/tenants/{f.TenantB}/access", HttpStatusCode.Forbidden);
            var missingDatabase = "missing_fixture_" + Guid.NewGuid().ToString("N");
            app.Configuration["ConnectionStrings:Platform"] = new NpgsqlConnectionStringBuilder(connectionString)
                { Database = missingDatabase }.ConnectionString;
            var failure = await ExpectHttp(client, "/api/platform/v1/tenants", HttpStatusCode.ServiceUnavailable);
            Assert(!failure.Contains(missingDatabase, StringComparison.Ordinal), "Real database failures must not expose connection details.");
        }
        finally { await app.StopAsync(); }
    }

    private static async Task<string> ExpectHttp(HttpClient client, string path, HttpStatusCode expected)
    {
        using var response = await client.GetAsync(path);
        Assert(response.StatusCode == expected, "The database-backed HTTP endpoint returned an unexpected status.");
        Assert(response.Headers.CacheControl?.NoStore == true, "Authorization HTTP responses must not be cached.");
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task VerifyCancellation(PostgresAuthorizationStore store, Fixture f)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Func<Task>[] reads = [() => store.ListTenantsAsync(f.Alice, cancellation.Token),
            () => store.ReadAccessAsync(f.Alice, f.TenantA, cancellation.Token)];
        foreach (var read in reads)
        {
            try { await read(); }
            catch (OperationCanceledException) { continue; }
            throw new InvalidOperationException("Cancelled authorization reads must propagate cancellation.");
        }
    }

    private static async Task<int> Execute(NpgsqlConnection connection, string sql, object[] values, NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 5 };
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> Version(NpgsqlConnection connection, Guid tenant)
    {
        await using var command = new NpgsqlCommand("SELECT authorization_version FROM public.platform_tenants WHERE tenant_id=$1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = tenant });
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture
    {
        public Guid UserA { get; } = Guid.NewGuid(); public Guid UserB { get; } = Guid.NewGuid();
        public Guid TenantA { get; } = Guid.NewGuid(); public Guid TenantB { get; } = Guid.NewGuid();
        public Guid FactoryA1 { get; } = Guid.NewGuid(); public Guid FactoryA2 { get; } = Guid.NewGuid();
        public Guid FactoryB1 { get; } = Guid.NewGuid(); public Guid FactoryB2 { get; } = Guid.NewGuid();
        public Guid UnlistedFactory { get; } = Guid.NewGuid();
        public Guid RoleA1 { get; } = Guid.NewGuid(); public Guid RoleA2 { get; } = Guid.NewGuid();
        public Guid RoleB1 { get; } = Guid.NewGuid(); public Guid RoleB2 { get; } = Guid.NewGuid();
        public Guid GrantA1 { get; } = Guid.NewGuid(); public Guid GrantA2 { get; } = Guid.NewGuid();
        public Guid GrantB1 { get; } = Guid.NewGuid(); public Guid GrantB2 { get; } = Guid.NewGuid();
        public string IssuerA { get; } = "https://postgres-fixture.example/" + Guid.NewGuid().ToString("N");
        public string IssuerB { get; } = "https://other-fixture.example/" + Guid.NewGuid().ToString("N");
        public string Subject { get; } = "shared-subject";
        public SubjectIdentity Alice => new(IssuerA, Subject);
        public SubjectIdentity OtherIssuer => new(IssuerB, Subject);
        public ProtectedResource ProductionA => new(TenantA, "dataset", "production", FactoryA1);
    }

    // This authentication scheme exists only in this test assembly and only in this test app.
    private sealed class FixtureAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder, Fixture fixture) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.Authorization != "PostgresFixture fixture") return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("iss", fixture.IssuerA), new Claim("sub", fixture.Subject)
            ], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
