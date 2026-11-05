using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AssembleAI.Platform.Audit;

public sealed record AuditQuery(string Scope, DateTimeOffset From, DateTimeOffset To, string? Source, string? Status,
    string? AppId, string? RequestId, int PageSize, string? Cursor)
{
    public static readonly string[] Sources = ["gateway", "model", "connector", "knowledge-search", "registry", "identity",
        "model-configuration", "connector-configuration", "knowledge-document", "audit-export"];
    public static string ParseScope(IQueryCollection query)
    {
        var scope = query.TryGetValue("scope", out var value) && value.Count == 1 ? value[0] : query.ContainsKey("scope") ? null : "tenant";
        return scope is "tenant" or "global" ? scope : throw new AuditRequestException(400);
    }
    public static AuditQuery Parse(IQueryCollection query, bool export = false)
    {
        var allowed = new[] { "scope", "from", "to", "source", "status", "appId", "requestId", "pageSize", "cursor" };
        if (query.Any(pair => !allowed.Contains(pair.Key, StringComparer.Ordinal) || pair.Value.Count != 1 || string.IsNullOrEmpty(pair.Value[0]))
            || export && (query.ContainsKey("pageSize") || query.ContainsKey("cursor"))) throw new AuditRequestException(400);
        string? Read(string name) => query.TryGetValue(name, out var value) ? value[0] : null;
        static DateTimeOffset Date(string? value)
        {
            if (value is null || value.Length > 35 || !value.EndsWith('Z')
                || !DateTimeOffset.TryParseExact(value, ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)) throw new AuditRequestException(400);
            return parsed;
        }
        var from = Date(Read("from")); var to = Date(Read("to"));
        if (from >= to || to - from > TimeSpan.FromDays(31)) throw new AuditRequestException(400);
        var source = Read("source"); var status = Read("status"); var app = Read("appId"); var request = Read("requestId");
        if (source is not null && !Sources.Contains(source, StringComparer.Ordinal)
            || status is not null && status is not ("success" or "failure" or "unfinished")
            || app is not null && !Regex.IsMatch(app, "\\A[a-z][a-z0-9-]{0,63}\\z")
            || request is not null && !Regex.IsMatch(request, "\\A[A-Za-z0-9._:-]{1,128}\\z")) throw new AuditRequestException(400);
        var page = 50;
        if (Read("pageSize") is { } size && (!int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out page) || page is < 1 or > 100)) throw new AuditRequestException(400);
        var cursor = Read("cursor"); if (cursor is { Length: > 8192 }) throw new AuditRequestException(400);
        return new(ParseScope(query), from, to, source, status, app, request, page, cursor);
    }
    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Scope, From, To, Source, Status, AppId, RequestId, PageSize }))));
}
