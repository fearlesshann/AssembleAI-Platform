using System.Text.Json;
using AssembleAI.Platform.Connectors;
using Microsoft.Extensions.Configuration;
using NpgsqlTypes;

namespace AssembleAI.Platform.SmokeTests;

public static class ConnectorCompilerTests
{
    public static async Task RunAsync()
    {
        CatalogValidation();
        StrictParsing();
        ScopedCompilation();
        await ClientBoundary();
        Console.WriteLine("PASS: connector catalog, strict typed queries, parameterized tenant/organization predicates before aggregation, direct-client validation.");
    }

    private static void CatalogValidation()
    {
        var empty = new ConnectorCatalog(new ConfigurationBuilder().Build(), TimeProvider.System);
        Assert(!empty.Options.Enabled && empty.FindSource("missing") is null && empty.FindDataset("missing") is null,
            "Connectors must be disabled and empty by default.");
        var catalog = Catalog();
        Assert(catalog.FindSource("manufacturing") is { MaximumRows: 500 } && catalog.FindDataset("production") is not null,
            "Declared source and dataset aliases must resolve exactly.");
        Assert(catalog.FindSource("Manufacturing") is null && catalog.FindDataset("../production") is null,
            "Aliases must be exact and safe.");
        foreach (var mutation in new Dictionary<string, string?>[] {
            new() { ["Connectors:Sources:manufacturing:MaximumRows"] = "501" },
            new() { ["Connectors:Sources:manufacturing:TimeoutSeconds"] = "31" },
            new() { ["Connectors:Sources:manufacturing:DisplayName"] = " " },
            new() { ["Connectors:Sources:manufacturing:ConnectionEnvironmentVariable"] = "secret=value" },
            new() { ["Connectors:Datasets:production:Table"] = "production;drop table x" },
            new() { ["Connectors:Datasets:production:Schema"] = "制造" },
            new() { ["Connectors:Datasets:production:SourceId"] = "missing" },
            new() { ["Connectors:Datasets:production:Columns:quantity:Type"] = "jsonb" },
            new() { ["Connectors:Datasets:production:Columns:quantity:Column"] = "tenant_id" }
        }) ExpectInvalid(() => Catalog(mutation), "Invalid deployment configuration must fail closed.");
        var tooMany = new Dictionary<string, string?>();
        for (var index = 0; index < 33; index++)
            tooMany[$"Connectors:Datasets:production:Columns:c{index}:Column"] = "c" + index;
        ExpectInvalid(() => Catalog(tooMany), "Dataset column counts must be bounded.");
    }

    private static void StrictParsing()
    {
        var dataset = Dataset();
        Assert(Parse("""{"columns":["line"],"filters":[{"column":"quantity","operator":"gte","value":10}],"limit":20}""", dataset) is { Limit: 20 },
            "Declared projections and typed filters must be accepted.");
        Assert(Parse("""{"metrics":[{"function":"count"}]}""", dataset) is { Limit: 500 }, "Count without a column must be allowed.");
        Assert(Parse("""{"columns":["line"],"metrics":[{"function":"sum","column":"quantity"},{"function":"avg","column":"cost"}]}""", dataset) is not null,
            "Grouped numeric aggregations must be supported.");
        foreach (var number in new[] { "0.1000", "1e-2", "-0.000", "1e-28", "79228162514264337593543950335" })
            Assert(Parse("{\"columns\":[\"line\"],\"filters\":[{\"column\":\"cost\",\"operator\":\"eq\",\"value\":" + number + "}]}", dataset) is not null,
                "Exact decimal representations, harmless trailing zeroes and scientific notation must remain supported.");
        foreach (var json in new[] {
            "null", "[]", "{}", """{"columns":["line"],"sql":"select 1"}""",
            """{"columns":["line"],"columns":["quantity"]}""", """{"columns":["line","line"]}""",
            """{"columns":["tenant_id"]}""", """{"columns":["line;drop table x"]}""",
            """{"columns":["line"],"limit":0}""", """{"columns":["line"],"limit":501}""",
            """{"columns":["line"],"limit":"10"}""", """{"columns":["line"],"filters":null}""",
            """{"columns":["line"],"filters":[{"column":"quantity","operator":"eq","value":"1"}]}""",
            """{"columns":["line"],"filters":[{"column":"quantity","operator":"eq","value":1.5}]}""",
            """{"columns":["line"],"filters":[{"column":"quantity","operator":"eq","value":9223372036854775808}]}""",
            """{"columns":["line"],"filters":[{"column":"cost","operator":"eq","value":0.12345678901234567890123456789123}]}""",
            """{"columns":["line"],"filters":[{"column":"cost","operator":"eq","value":1e-100}]}""",
            """{"columns":["line"],"filters":[{"column":"line","operator":"eq","value":null}]}""",
            """{"columns":["line"],"filters":[{"column":"line","operator":"eq","value":"\uD800"}]}""",
            """{"columns":["line"],"filters":[{"column":"day","operator":"eq","value":"2026-02-30"}]}""",
            """{"columns":["line"],"filters":[{"column":"line","operator":"eq","value":"x","value":"y"}]}""",
            """{"columns":["line"],"filters":[{"column":"line","operator":"like","value":"%"}]}""",
            """{"metrics":[{"function":"count","column":"quantity"}]}""",
            """{"metrics":[{"function":"sum","column":"line"}]}""",
            """{"metrics":[{"function":"max","column":"day"}]}""",
            """{"metrics":[{"function":"pg_sleep","column":"quantity"}]}""",
            """{"metrics":[{"function":"count","extra":true}]}"""
        }) Assert(Parse(json, dataset) is null, "Malformed, untyped or non-whitelisted query must be rejected: " + json);
        Assert(Parse("""{"columns":["line"],"limit":11}""", dataset, 10) is null, "Tenant row limits must be enforced at parsing.");
        Assert(Parse("""{"columns":["line"]}""", dataset, 0) is null, "Invalid maximum rows must fail closed.");
        using var root = JsonDocument.Parse("""{"columns":["day"],"filters":[{"column":"day","operator":"eq","value":"2026-11-04"}]}""");
        var retained = ConnectorQueryCompiler.Parse(root.RootElement, dataset, 500)!;
        root.Dispose();
        Assert(retained.Filters[0].Value.GetString() == "2026-11-04", "Parsed query values must survive request document disposal.");
    }

    private static void ScopedCompilation()
    {
        var tenant = Guid.NewGuid(); var factory = Guid.NewGuid();
        var query = Parse("""{"columns":["line"],"filters":[{"column":"line","operator":"eq","value":"x'; DELETE FROM production; --"}],"metrics":[{"function":"sum","column":"quantity"},{"function":"count"}],"limit":25}""", Dataset())!;
        var compiled = ConnectorQueryCompiler.Compile(Dataset(), query, tenant, new HashSet<Guid> { factory });
        Assert(compiled.Sql.Contains("\"tenant_id\" = $1", StringComparison.Ordinal)
            && compiled.Sql.Contains("\"factory_id\" = ANY($2)", StringComparison.Ordinal), "Every SQL query must bind its trusted tenant and organization scope.");
        Assert(compiled.Sql.IndexOf(" WHERE ", StringComparison.Ordinal) < compiled.Sql.IndexOf(" GROUP BY ", StringComparison.Ordinal)
            && !compiled.Sql.Contains("DELETE", StringComparison.Ordinal), "Authorization filtering must precede aggregation and filter values must never become SQL.");
        Assert(compiled.Parameters[0].Type == NpgsqlDbType.Uuid && Equals(compiled.Parameters[0].Value, tenant)
            && compiled.Parameters[1].Type == (NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            && ((Guid[])compiled.Parameters[1].Value).SequenceEqual([factory]), "Tenant and organizations must use typed PostgreSQL parameters.");
        Assert(compiled.Columns.SequenceEqual(["line", "metric0", "metric1"]) && compiled.Types.SequenceEqual(["text", "decimal", "integer"]),
            "Results must expose predictable logical aliases and bounded supported types.");
        Assert(Equals(compiled.Parameters[^1].Value, 26), "SQL must fetch one additional row to detect an incomplete result.");
        foreach (var date in new[] { "0001-01-01", "9999-12-31" })
        {
            var dateQuery = Parse("{\"columns\":[\"day\"],\"filters\":[{\"column\":\"day\",\"operator\":\"eq\",\"value\":\"" + date + "\"}]}", Dataset())!;
            var dateSql = ConnectorQueryCompiler.Compile(Dataset(), dateQuery, tenant, new HashSet<Guid> { factory });
            Assert(dateSql.Parameters[2].Type == NpgsqlDbType.Date && dateSql.Parameters[2].Value is int days
                && new DateOnly(2000, 1, 1).AddDays(days).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) == date,
                "Finite date boundary parameters must use raw PostgreSQL days instead of Npgsql infinity conversions.");
        }
        foreach (var organizations in new IReadOnlySet<Guid>[] { new HashSet<Guid>(), new HashSet<Guid> { Guid.Empty } })
            ExpectInvalid(() => ConnectorQueryCompiler.Compile(Dataset(), query, tenant, organizations), "Empty or invalid scope must never compile.");
        ExpectInvalid(() => ConnectorQueryCompiler.Compile(Dataset(), query, Guid.Empty, new HashSet<Guid> { factory }), "An empty tenant must never compile.");
        ExpectInvalid(() => ConnectorQueryCompiler.Compile(Dataset(), query with { Columns = ["tenant_id"] }, tenant, new HashSet<Guid> { factory }),
            "Direct callers cannot bypass projection validation.");
        ExpectInvalid(() => ConnectorQueryCompiler.Compile(Dataset(), query with { Limit = 501 }, tenant, new HashSet<Guid> { factory }),
            "Direct callers cannot bypass row budgets.");
    }

    private static async Task ClientBoundary()
    {
        var client = new PostgresConnectorClient(Catalog());
        var source = Catalog().FindSource("manufacturing")!;
        var query = Parse("""{"columns":["line"]}""", Dataset())!;
        try { await client.QueryAsync(source, Dataset(), query, Guid.NewGuid(), new HashSet<Guid>(), 10, default); }
        catch (ConnectorUnavailableException error) { Assert(error.InnerException is null, "Safe connector errors cannot include sensitive driver errors."); return; }
        throw new InvalidOperationException("Client must reject an empty scope before opening any connection.");
    }

    private static ConnectorQuery? Parse(string json, ConnectorDataset dataset, int maximumRows = 500)
    {
        using var document = JsonDocument.Parse(json);
        return ConnectorQueryCompiler.Parse(document.RootElement, dataset, maximumRows);
    }

    internal static ConnectorDataset Dataset() => new() {
        SourceId = "manufacturing", Schema = "manufacturing", Table = "production_daily",
        Columns = new() {
            ["line"] = new() { Column = "line_name", Type = "text" },
            ["quantity"] = new() { Column = "produced_count", Type = "integer" },
            ["cost"] = new() { Column = "unit_cost", Type = "decimal" },
            ["day"] = new() { Column = "production_date", Type = "date" }
        }
    };

    private static ConnectorCatalog Catalog(Dictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?> {
            ["Connectors:Enabled"] = "true", ["Connectors:Sources:manufacturing:Enabled"] = "true",
            ["Connectors:Sources:manufacturing:DisplayName"] = "Manufacturing test data",
            ["Connectors:Sources:manufacturing:ConnectionEnvironmentVariable"] = "ASSEMBLEAI_TEST_CONNECTOR",
            ["Connectors:Datasets:production:SourceId"] = "manufacturing",
            ["Connectors:Datasets:production:DisplayName"] = "Production test data",
            ["Connectors:Datasets:production:Schema"] = "manufacturing",
            ["Connectors:Datasets:production:Table"] = "production_daily",
            ["Connectors:Datasets:production:Columns:quantity:Column"] = "produced_count",
            ["Connectors:Datasets:production:Columns:quantity:Type"] = "integer"
        };
        if (overrides is not null) foreach (var item in overrides) values[item.Key] = item.Value;
        return new ConnectorCatalog(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), TimeProvider.System);
    }

    private static void ExpectInvalid(Action action, string message)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
