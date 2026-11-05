using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AssembleAI.Platform.SmokeTests;

/// <summary>Explicit opt-in for a launcher-owned, marked PostgreSQL test instance.</summary>
public static class PostgresIntegrationTests
{
    public static async Task RunAsync()
    {
        var phase = "configuration";
        string? password = null;
        try
        {
            var configPath = Environment.GetEnvironmentVariable("ASSEMBLEAI_POSTGRES_TEST_CONFIG");
            Assert(!string.IsNullOrEmpty(configPath), "The isolated PostgreSQL config file is required.");
            var root = FindWorkspace();
            var absolute = Path.GetFullPath(configPath!);
            var runDirectory = Path.GetDirectoryName(absolute)!;
            Assert(string.Equals(Path.GetFileName(absolute), "test-config.json", StringComparison.Ordinal)
                && string.Equals(Path.GetDirectoryName(runDirectory), Path.Combine(root, ".local-data", "postgres-tests"), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(runDirectory).StartsWith("run-", StringComparison.Ordinal),
                "Database verification only accepts a launcher-owned config directory.");
            for (var directory = new DirectoryInfo(runDirectory); directory is not null; directory = directory.Parent)
            {
                Assert(!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) == 0,
                    "Database test paths cannot contain junctions or symbolic links.");
                if (string.Equals(directory.FullName, root, StringComparison.OrdinalIgnoreCase)) break;
            }
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(absolute));
            var config = document.RootElement;
            var runId = config.GetProperty("runId").GetString();
            Assert(Guid.TryParseExact(runId, "N", out _)
                && Path.GetFileName(runDirectory) == "run-" + runId
                && string.Equals(Path.GetFullPath(config.GetProperty("runDirectory").GetString()!), runDirectory, StringComparison.OrdinalIgnoreCase),
                "The run identifier must match its local directory.");
            var connectionString = config.GetProperty("connectionString").GetString()!;
            var settings = new NpgsqlConnectionStringBuilder(connectionString);
            password = settings.Password;
            Assert(settings.Host == "127.0.0.1" && settings.Port is > 0 and <= 65535
                && settings.Database == "assembleai_platform" && settings.Username == "assembleai_platform"
                && !string.IsNullOrEmpty(password), "Only the isolated loopback platform database is accepted.");

            phase = "instance marker and migrations";
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var marker = new NpgsqlCommand("SELECT run_id FROM test_support.instance_marker", connection);
                Assert(string.Equals(await marker.ExecuteScalarAsync() as string, runId, StringComparison.Ordinal),
                    "Database instance marker does not match this test run.");
                await using var role = new NpgsqlCommand("SELECT rolsuper OR rolcreatedb OR rolcreaterole OR rolbypassrls FROM pg_roles WHERE rolname=current_user", connection);
                Assert(await role.ExecuteScalarAsync() is false, "The application role must not have elevated cluster privileges.");
                await using var migrated = new NpgsqlCommand("SELECT array_agg(version ORDER BY version) FROM public.platform_schema_migrations", connection);
                Assert((await migrated.ExecuteScalarAsync() as int[])?.SequenceEqual([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]) == true,
                    "All real core migrations must have completed exactly once.");
                await using var empty = new NpgsqlCommand("SELECT (SELECT count(*) FROM public.platform_users)+(SELECT count(*) FROM public.platform_tenants)+(SELECT count(*) FROM public.platform_bff_sessions)+(SELECT count(*) FROM public.platform_registry_operators)+(SELECT count(*) FROM public.platform_applications)+(SELECT count(*) FROM public.tenant_models)+(SELECT count(*) FROM public.platform_model_configuration_audit)+(SELECT count(*) FROM public.platform_model_calls)", connection);
                Assert(await empty.ExecuteScalarAsync() is 0L, "Core migrations must not seed accounts, tenants, sessions or model configuration/history.");
                await using var emptyConnectors = new NpgsqlCommand("SELECT (SELECT count(*) FROM public.tenant_connectors)+(SELECT count(*) FROM public.platform_connector_configuration_audit)+(SELECT count(*) FROM public.platform_connector_calls)", connection);
                Assert(await emptyConnectors.ExecuteScalarAsync() is 0L, "Connector migrations must not seed configuration or audit.");
                await using var emptyKnowledge = new NpgsqlCommand("SELECT (SELECT count(*) FROM public.knowledge_documents)+(SELECT count(*) FROM public.knowledge_jobs)+(SELECT count(*) FROM public.knowledge_chunks)+(SELECT count(*) FROM public.knowledge_embeddings)+(SELECT count(*) FROM public.platform_knowledge_searches)+(SELECT count(*) FROM public.platform_knowledge_audit)", connection);
                Assert(await emptyKnowledge.ExecuteScalarAsync() is 0L, "Knowledge migration must not seed documents, jobs, chunks or audit.");
                await using var vector = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname='vector')", connection);
                Assert(await vector.ExecuteScalarAsync() is bool present && present == (Environment.GetEnvironmentVariable("ASSEMBLEAI_KNOWLEDGE_ONLY") == "1"),
                    "The explicitly selected acceptance scope must match vector availability.");
            }
            if (Environment.GetEnvironmentVariable("ASSEMBLEAI_KNOWLEDGE_ONLY") == "1")
            {
                phase = "knowledge pgvector indexing and authorization";
                await PostgresKnowledgeIndexTests.RunAsync(connectionString);
                if (Environment.GetEnvironmentVariable("ASSEMBLEAI_KNOWLEDGE_BROWSER_TESTS") == "1")
                {
                    phase = "knowledge native storage, broker, parser, embeddings, model and browser chain";
                    await PostgresKnowledgeFlowTests.RunAsync(connectionString, runDirectory,
                        Environment.GetEnvironmentVariable("ASSEMBLEAI_KNOWLEDGE_REAL_MODEL_TESTS") == "1");
                }
                await VerifyReadiness(connectionString, requireVector: true, "ready");
                await VerifyMissingKnowledgeMigration(connectionString);
                Console.WriteLine(Environment.GetEnvironmentVariable("ASSEMBLEAI_KNOWLEDGE_BROWSER_TESTS") == "1"
                    ? "PASS: focused knowledge real pgvector, native MinIO/RabbitMQ, parser/embedding/application, model, browser and readiness."
                    : "PASS: focused knowledge pgvector indexing, authorization and readiness; application chain not requested.");
                return;
            }
            if (Environment.GetEnvironmentVariable("ASSEMBLEAI_CHATBI_ONLY") == "1")
            {
                Assert(Environment.GetEnvironmentVariable("ASSEMBLEAI_CHATBI_BROWSER_TESTS") == "1", "Focused ChatBI acceptance must be explicitly enabled.");
                phase = "focused ChatBI PostgreSQL/Python/model/connector/browser chain";
                await PostgresChatBiTests.RunAsync(connectionString, runDirectory,
                    Environment.GetEnvironmentVariable("ASSEMBLEAI_CHATBI_REAL_MODEL_TESTS") == "1");
                Console.WriteLine(Environment.GetEnvironmentVariable("ASSEMBLEAI_INTERACTIVE_DEMO") == "1"
                    ? "Local interactive ChatBI ended; this mode is not a contract verification run."
                    : "PASS: focused ChatBI chain with verified instance marker and complete unseeded migrations; broader platform contracts are a separate run.");
                return;
            }
            phase = "authorization store";
            await PostgresAuthorizationTests.RunAsync(connectionString);
            phase = "session store";
            await PostgresSessionTests.RunAsync(connectionString, runDirectory);
            phase = "login, tenant and downstream trust chain";
            await PostgresTrustTests.RunAsync(connectionString, runDirectory);
            phase = "application registry and tenant releases";
            await PostgresRegistryTests.RunAsync(connectionString);
            phase = "registry management queries and scoped audit";
            await PostgresRegistryManagementTests.RunAsync(connectionString);
            phase = "authorized application catalog";
            await PostgresCatalogTests.RunAsync(connectionString);
            phase = "gateway leases and call records";
            await PostgresGatewayStoreTests.RunAsync(connectionString);
            phase = "tenant models and durable model call records";
            await PostgresModelTests.RunAsync(connectionString);
            phase = "knowledge document lifecycle and fenced jobs";
            await PostgresKnowledgeTests.RunAsync(connectionString);
            phase = "tenant connector configuration and durable call records";
            await PostgresConnectorStoreTests.RunAsync(connectionString);
            phase = "read-only data connector PostgreSQL and OIDC HTTP chain";
            await PostgresConnectorFlowTests.RunAsync(connectionString, runDirectory);
            phase = "production gateway PostgreSQL and signed HTTP chain";
            await PostgresGatewayFlowTests.RunAsync(connectionString, runDirectory);
            if (Environment.GetEnvironmentVariable("ASSEMBLEAI_CHATBI_BROWSER_TESTS") == "1")
            {
                phase = "ChatBI PostgreSQL/Python/model/connector/browser chain";
                await PostgresChatBiTests.RunAsync(connectionString, runDirectory,
                    Environment.GetEnvironmentVariable("ASSEMBLEAI_CHATBI_REAL_MODEL_TESTS") == "1");
            }
            if (Environment.GetEnvironmentVariable("ASSEMBLEAI_MODEL_APPLICATION_TESTS") == "1")
            {
                phase = "application model delegation PostgreSQL/Python chain";
                await PostgresModelFlowTests.RunAsync(connectionString, runDirectory,
                    Environment.GetEnvironmentVariable("ASSEMBLEAI_REAL_MODEL_TESTS") == "1");
            }
            if (Environment.GetEnvironmentVariable("ASSEMBLEAI_APPLICATION_BROWSER_TESTS") == "1")
            {
                phase = "application template PostgreSQL/Python/browser chain";
                await PostgresApplicationTests.RunAsync(connectionString, runDirectory);
            }
            if (Environment.GetEnvironmentVariable("ASSEMBLEAI_REGISTRY_BROWSER_TESTS") == "1")
            {
                phase = "registry lifecycle browser and real gateway";
                await PostgresRegistryBrowserTests.RunAsync(connectionString, runDirectory);
            }
            phase = "readiness boundaries";
            await VerifyReadiness(connectionString, requireVector: false, "ready");
            await VerifyReadiness(connectionString, requireVector: true, "unavailable");
            await VerifyMissingKnowledgeMigration(connectionString);
            Console.WriteLine("PASS: PostgreSQL instance marker, non-superuser role, complete/idempotent unseeded core schema and explicit vector readiness boundary.");
        }
        catch (Exception exception)
        {
            var detail = exception is PostgresException postgres ? "SQLSTATE " + postgres.SqlState
                : exception is InvalidOperationException ? exception.Message : exception.GetType().Name;
            if (!string.IsNullOrEmpty(password)) detail = detail.Replace(password, "[redacted]", StringComparison.Ordinal);
            Console.Error.WriteLine($"FAIL: PostgreSQL integration ({phase}): {detail}");
            // Stack frames identify a failed contract without serializing database
            // fields, inner exceptions, credentials or statement parameter values.
            Console.Error.WriteLine(exception.StackTrace);
            Environment.ExitCode = 1;
        }
    }

    private static async Task VerifyMissingKnowledgeMigration(string connectionString)
    {
        // Only the launcher-owned disposable database reaches this point. Restore the ledger on every exit.
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var hide = new NpgsqlCommand("UPDATE public.platform_schema_migrations SET version=-11 WHERE version=11", connection);
        Assert(await hide.ExecuteNonQueryAsync() == 1, "The knowledge migration must exist before checking its readiness boundary.");
        try { await VerifyReadiness(connectionString, requireVector: false, "unavailable"); }
        finally
        {
            await using var restore = new NpgsqlCommand("UPDATE public.platform_schema_migrations SET version=11 WHERE version=-11", connection);
            Assert(await restore.ExecuteNonQueryAsync() == 1, "The isolated migration ledger must be restored.");
        }
    }

    private static async Task VerifyReadiness(string connectionString, bool requireVector, string expected)
    {
        await using var app = PlatformApplication.Build([
            "--environment", "Development", "--urls", "http://127.0.0.1:0",
            "--Authentication:Bff:Enabled", "false", "--ConnectionStrings:Platform", connectionString,
            "--Readiness:RequireVector", requireVector.ToString(), "--Readiness:RedisHost", "",
            "--Readiness:MinioEndpoint", "", "--Readiness:RabbitMqEndpoint", "", "--Readiness:OidcDiscoveryEndpoint", ""
        ], builder => builder.Logging.ClearProviders());
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var response = await client.GetAsync("/health/ready");
            Assert(response.StatusCode == HttpStatusCode.ServiceUnavailable, "Missing non-database dependencies cannot report full readiness.");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert(json.RootElement.GetProperty("checks").GetProperty("dependencies").GetProperty("dependencies")
                .GetProperty("postgres").GetString() == expected, "Database readiness must respect the explicit vector requirement.");
        }
        finally { await app.StopAsync(); }
    }

    private static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AssembleAI.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Run database tests inside the workspace.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
