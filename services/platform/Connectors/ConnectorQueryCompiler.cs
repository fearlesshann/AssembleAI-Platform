using System.Globalization;
using System.Text;
using System.Text.Json;
using NpgsqlTypes;

namespace AssembleAI.Platform.Connectors;

public sealed record ConnectorSqlParameter(NpgsqlDbType Type, object Value);
public sealed record CompiledConnectorQuery(string Sql, IReadOnlyList<ConnectorSqlParameter> Parameters,
    IReadOnlyList<string> Columns, IReadOnlyList<string> Types, int Limit);

public static class ConnectorQueryCompiler
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static ConnectorQuery? Parse(JsonElement body, ConnectorDataset dataset, int maximumRows)
    {
        try { return ParseCore(body, dataset, maximumRows); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException) { return null; }
    }

    private static ConnectorQuery? ParseCore(JsonElement body, ConnectorDataset dataset, int maximumRows)
    {
        if (!ConnectorCatalog.ValidDataset(dataset) || maximumRows is < 1 or > 500
            || !Fields(body, ["columns", "filters", "metrics", "limit"])) return null;
        var columns = new List<string>(); var filters = new List<ConnectorFilter>(); var metrics = new List<ConnectorMetric>();
        var limit = maximumRows;
        if (body.TryGetProperty("limit", out var requestedLimit)
            && (requestedLimit.ValueKind != JsonValueKind.Number || !requestedLimit.TryGetInt32(out limit) || limit < 1 || limit > maximumRows)) return null;
        if (body.TryGetProperty("columns", out var projected))
        {
            if (projected.ValueKind != JsonValueKind.Array || projected.GetArrayLength() > 16) return null;
            foreach (var item in projected.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } column
                    || !dataset.Columns.ContainsKey(column) || columns.Contains(column, StringComparer.Ordinal)) return null;
                columns.Add(column);
            }
        }
        if (body.TryGetProperty("filters", out var requestedFilters))
        {
            if (requestedFilters.ValueKind != JsonValueKind.Array || requestedFilters.GetArrayLength() > 16) return null;
            foreach (var item in requestedFilters.EnumerateArray())
            {
                if (!Fields(item, ["column", "operator", "value"]) || Text(item, "column") is not { } column
                    || !dataset.Columns.TryGetValue(column, out var definition) || Text(item, "operator") is not ("eq" or "gte" or "lte")
                    || !item.TryGetProperty("value", out var value) || !TryValue(value, definition.Type, out _)) return null;
                filters.Add(new(column, Text(item, "operator")!, value.Clone()));
            }
        }
        if (body.TryGetProperty("metrics", out var requestedMetrics))
        {
            if (requestedMetrics.ValueKind != JsonValueKind.Array || requestedMetrics.GetArrayLength() > 8) return null;
            foreach (var item in requestedMetrics.EnumerateArray())
            {
                if (!Fields(item, ["function", "column"])) return null;
                var function = Text(item, "function"); var column = Text(item, "column");
                if (function == "count")
                {
                    if (item.TryGetProperty("column", out _)) return null;
                }
                else if (function is not ("sum" or "avg" or "min" or "max") || column is null
                    || !dataset.Columns.TryGetValue(column, out var definition) || definition.Type is not ("integer" or "decimal")) return null;
                if (columns.Contains("metric" + metrics.Count, StringComparer.Ordinal)) return null;
                metrics.Add(new(function!, column));
            }
        }
        return columns.Count == 0 && metrics.Count == 0 ? null : new(columns.ToArray(), filters.ToArray(), metrics.ToArray(), limit);
    }

    public static CompiledConnectorQuery Compile(ConnectorDataset dataset, ConnectorQuery query,
        Guid tenantId, IReadOnlySet<Guid> organizations)
    {
        if (!ConnectorCatalog.ValidDataset(dataset) || query is null || query.Columns is null || query.Filters is null || query.Metrics is null
            || query.Columns.Count > 16 || query.Filters.Count > 16 || query.Metrics.Count > 8 || query.Filters.Any(x => x is null)
            || query.Metrics.Any(x => x is null) || tenantId == Guid.Empty || organizations is null || organizations.Count == 0
            || organizations.Count > 10000 || organizations.Contains(Guid.Empty)) throw Invalid();
        // Apply exactly the same validation to programmatic callers as to HTTP JSON.
        ConnectorQuery? validated;
        try
        {
            var body = JsonSerializer.SerializeToElement(new {
                columns = query.Columns, filters = query.Filters,
                metrics = query.Metrics.Select(metric => metric.Column is null
                    ? new Dictionary<string, object?> { ["function"] = metric.Function }
                    : new Dictionary<string, object?> { ["function"] = metric.Function, ["column"] = metric.Column }),
                limit = query.Limit
            }, JsonOptions);
            validated = Parse(body, dataset, 500);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { throw Invalid(); }
        if (validated is null) throw Invalid();
        query = validated;
        var parameters = new List<ConnectorSqlParameter> { new(NpgsqlDbType.Uuid, tenantId), new(NpgsqlDbType.Array | NpgsqlDbType.Uuid, organizations.Order().ToArray()) };
        var columns = query.Columns.ToList();
        var types = query.Columns.Select(column => dataset.Columns[column].Type).ToList();
        var expressions = query.Columns.Select(column => Quote(dataset.Columns[column].Column) + " AS " + Quote(column)).ToList();
        for (var index = 0; index < query.Metrics.Count; index++)
        {
            var metric = query.Metrics[index]; var alias = "metric" + index;
            // Normalize numeric aggregate results while preserving native source-column semantics.
            var expression = metric.Function == "count" ? "pg_catalog.count(*)" : "pg_catalog." + metric.Function + "(" + Quote(dataset.Columns[metric.Column!].Column) + ")::pg_catalog.numeric";
            expressions.Add(expression + " AS " + Quote(alias)); columns.Add(alias); types.Add(metric.Function == "count" ? "integer" : "decimal");
        }
        var predicates = new List<string> { Quote(dataset.TenantColumn) + " = $1", Quote(dataset.OrganizationColumn) + " = ANY($2)" };
        foreach (var filter in query.Filters)
        {
            var definition = dataset.Columns[filter.Column];
            if (!TryValue(filter.Value, definition.Type, out var value)) throw Invalid();
            // Npgsql maps DateOnly.Min/MaxValue to PostgreSQL infinities by default. Raw day offsets preserve finite boundaries.
            parameters.Add(new(DbType(definition.Type), definition.Type == "date"
                ? ((DateOnly)value!).DayNumber - new DateOnly(2000, 1, 1).DayNumber : value!));
            var operation = filter.Operator switch { "eq" => "=", "gte" => ">=", "lte" => "<=", _ => throw Invalid() };
            predicates.Add(Quote(definition.Column) + " " + operation + " $" + parameters.Count);
        }
        var sql = "SELECT " + string.Join(", ", expressions) + " FROM " + Quote(dataset.Schema) + "." + Quote(dataset.Table)
            + " WHERE " + string.Join(" AND ", predicates);
        if (query.Metrics.Count > 0 && query.Columns.Count > 0)
            sql += " GROUP BY " + string.Join(", ", query.Columns.Select(column => Quote(dataset.Columns[column].Column)));
        if (query.Columns.Count > 0)
            sql += " ORDER BY " + string.Join(", ", query.Columns.Select(column => Quote(dataset.Columns[column].Column)));
        parameters.Add(new(NpgsqlDbType.Integer, query.Limit + 1));
        sql += " LIMIT $" + parameters.Count;
        return new(sql, parameters.ToArray(), columns.ToArray(), types.ToArray(), query.Limit);
    }

    internal static string Quote(string identifier) => "\"" + identifier + "\"";
    private static bool Fields(JsonElement element, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) return false;
        return true;
    }
    private static string? Text(JsonElement element, string property) => element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool TryValue(JsonElement value, string type, out object? result)
    {
        result = null;
        switch (type)
        {
            case "integer" when value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer): result = integer; return true;
            case "decimal" when value.ValueKind == JsonValueKind.Number && ExactDecimal(value, out var number): result = number; return true;
            case "date" when value.ValueKind == JsonValueKind.String
                && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date): result = date; return true;
            case "text" when value.ValueKind == JsonValueKind.String:
                var text = value.GetString()!;
                try { if (StrictUtf8.GetByteCount(text) <= 4096 && !text.Contains('\0')) { result = text; return true; } }
                catch (EncoderFallbackException) { }
                return false;
            default: return false;
        }
    }
    private static bool ExactDecimal(JsonElement value, out decimal result)
    {
        result = 0;
        var raw = value.GetRawText();
        if (raw.Length > 16384 || !value.TryGetDecimal(out result)) return false;
        // Decimal parsing can round or underflow without reporting failure. Compare normalized coefficient/exponent pairs.
        var original = NormalizeNumber(raw);
        return original is not null && original == NormalizeNumber(result.ToString(CultureInfo.InvariantCulture));
    }
    private static string? NormalizeNumber(string number)
    {
        var negative = number[0] == '-';
        var exponentIndex = number.IndexOfAny(['e', 'E']);
        long exponent = 0;
        if (exponentIndex >= 0 && (!int.TryParse(number.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var suppliedExponent) || Math.Abs((long)suppliedExponent) > 100000)) return null;
        if (exponentIndex >= 0) exponent = int.Parse(number.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var mantissa = number.AsSpan(negative ? 1 : 0, (exponentIndex >= 0 ? exponentIndex : number.Length) - (negative ? 1 : 0));
        var decimalPoint = mantissa.IndexOf('.');
        if (decimalPoint >= 0) exponent -= mantissa.Length - decimalPoint - 1;
        var digits = mantissa.ToString().Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) return "0";
        var significantLength = digits.Length;
        while (significantLength > 0 && digits[significantLength - 1] == '0') { significantLength--; exponent++; }
        return (negative ? "-" : "") + digits[..significantLength] + "e" + exponent.ToString(CultureInfo.InvariantCulture);
    }
    private static NpgsqlDbType DbType(string type) => type switch {
        "text" => NpgsqlDbType.Text, "integer" => NpgsqlDbType.Bigint, "decimal" => NpgsqlDbType.Numeric, "date" => NpgsqlDbType.Date,
        _ => throw Invalid()
    };
    private static InvalidOperationException Invalid() => new("Connector query is invalid.");
}
