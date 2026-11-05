using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace AssembleAI.Platform;

internal sealed class DependencyReadinessCheck(
    IConfiguration configuration,
    IHttpClientFactory clientFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var checks = await Task.WhenAll(
            Check("postgres", CheckPostgres, cancellationToken),
            Check("redis", CheckRedis, cancellationToken),
            Check("minio", token => CheckHttp("Readiness:MinioEndpoint", token), cancellationToken),
            Check("rabbitmq", CheckRabbitMq, cancellationToken),
            Check("oidc", CheckOidc, cancellationToken));
        var data = checks.ToDictionary(item => item.Name, item => (object)item.Status);
        return checks.All(item => item.Status == "ready")
            ? HealthCheckResult.Healthy("Configured dependencies responded successfully.", data)
            : HealthCheckResult.Unhealthy("A dependency is unavailable or not configured.", data: data);
    }

    private static async Task<(string Name, string Status)> Check(
        string name, Func<CancellationToken, Task> action, CancellationToken requestToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await action(timeout.Token);
            return (name, "ready");
        }
        catch (InvalidOperationException)
        {
            return (name, "not_configured_or_invalid");
        }
        catch (Exception) when (!requestToken.IsCancellationRequested)
        {
            // Health output must never contain connection strings, credentials or exception text.
            return (name, "unavailable");
        }
    }

    private async Task CheckPostgres(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("Platform");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Postgres connection is not configured.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) = 12 FROM public.platform_schema_migrations WHERE version BETWEEN 1 AND 12", connection);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw new IOException("Platform migration has not been applied.");

        if (configuration.GetValue("Readiness:RequireVector", true))
        {
            await using var vector = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'vector' AND extversion = '0.8.7')", connection);
            if (await vector.ExecuteScalarAsync(cancellationToken) is not true)
                throw new IOException("Required knowledge-base extension has not been initialized.");
        }
    }

    private async Task CheckRedis(CancellationToken cancellationToken)
    {
        var host = Required("Readiness:RedisHost");
        var port = configuration.GetValue<int>("Readiness:RedisPort");
        if (port is < 1 or > 65535) throw new InvalidOperationException("Redis port is invalid.");
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var password = configuration["Readiness:RedisPassword"];
        if (!string.IsNullOrEmpty(password))
        {
            await WriteResp(stream, ["AUTH", password], cancellationToken);
            if (await reader.ReadLineAsync(cancellationToken) != "+OK")
                throw new IOException("Redis authentication failed.");
        }
        await WriteResp(stream, ["PING"], cancellationToken);
        if (await reader.ReadLineAsync(cancellationToken) != "+PONG")
            throw new IOException("Redis did not respond to PING.");
    }

    private static Task WriteResp(Stream stream, string[] parts, CancellationToken cancellationToken)
    {
        var command = new StringBuilder($"*{parts.Length}\r\n");
        foreach (var part in parts)
            command.Append('$').Append(Encoding.UTF8.GetByteCount(part)).Append("\r\n").Append(part).Append("\r\n");
        return stream.WriteAsync(Encoding.UTF8.GetBytes(command.ToString()), cancellationToken).AsTask();
    }

    private async Task CheckHttp(string configKey, CancellationToken cancellationToken)
    {
        using var response = await clientFactory.CreateClient("readiness")
            .GetAsync(RequiredHttpEndpoint(configKey), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task CheckRabbitMq(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RequiredHttpEndpoint("Readiness:RabbitMqEndpoint"));
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{Required("Readiness:RabbitMqUser")}:{Required("Readiness:RabbitMqPassword")}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        using var response = await clientFactory.CreateClient("readiness").SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task CheckOidc(CancellationToken cancellationToken)
    {
        using var response = await clientFactory.CreateClient("readiness")
            .GetAsync(RequiredHttpEndpoint("Readiness:OidcDiscoveryEndpoint"), cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        foreach (var field in new[] { "issuer", "authorization_endpoint", "token_endpoint", "jwks_uri" })
        {
            if (!document.RootElement.TryGetProperty(field, out var value)
                || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https"))
                throw new IOException("Invalid OIDC discovery document.");
        }
    }

    private Uri RequiredHttpEndpoint(string name)
    {
        if (!Uri.TryCreate(Required(name), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            throw new InvalidOperationException("Invalid readiness endpoint.");
        return uri;
    }

    private string Required(string name) => !string.IsNullOrWhiteSpace(configuration[name])
        ? configuration[name]!
        : throw new InvalidOperationException("Readiness configuration is incomplete.");
}
