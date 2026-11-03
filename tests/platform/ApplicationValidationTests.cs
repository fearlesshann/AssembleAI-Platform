using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Gateway;
using AssembleAI.Platform.InternalCalls;
using AssembleAI.Platform.Registry;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AssembleAI.Platform.SmokeTests;

public static class ApplicationValidationTests
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "assembleai-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var rsa = RSA.Create(2048);
            using var cert = new CertificateRequest("CN=application-validation", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var pfx = Path.Combine(directory, "test.pfx");
            var password = Guid.NewGuid().ToString("N");
            File.WriteAllBytes(pfx, cert.Export(X509ContentType.Pfx, password));
            var store = new Store();
            var clock = new Clock();
            var builder = WebApplication.CreateBuilder(["--environment", "Development", "--urls", "http://127.0.0.1:0"]);
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InternalCalls:Enabled"] = "true", ["InternalCalls:ValidationEndpointEnabled"] = "true",
                ["InternalCalls:Issuer"] = "https://platform.example/internal",
                ["InternalCalls:SigningCertificatePath"] = pfx, ["InternalCalls:SigningCertificatePassword"] = password,
                ["Registry:FrontendArtifacts:0:AppId"] = store.Manifest.AppId,
                ["Registry:FrontendArtifacts:0:Version"] = "1.0.0",
                ["Registry:FrontendArtifacts:0:Entry"] = store.Manifest.FrontendEntry,
                ["Registry:FrontendArtifacts:0:Sha256"] = store.Manifest.FrontendArtifact.Sha256,
                ["Registry:BackendBindings:0:Service"] = "production-api", ["Registry:BackendBindings:0:Version"] = "1.0.0",
                ["Registry:BackendBindings:0:Audience"] = "assembleai:production", ["Registry:BackendBindings:0:Destination"] = "http://127.0.0.1:1"
            });
            builder.Services.AddSingleton<IAuthorizationStore>(store);
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.AddSingleton<IApplicationCallStore>(store);
            builder.Services.AddSingleton<RegistryDeploymentCatalog>();
            builder.Services.AddSingleton(new GatewayOptions { Operations = [new("production-api", "1.0.0", "POST", "/probe", "production:read")] });
            builder.AddPlatformInternalCalls();
            await using var app = builder.Build();
            app.MapInternalCallEndpoints();
            await app.StartAsync();
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
                var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("iss", "https://id.example"), new Claim("sub", "alice")], "Validated"));
                var service = app.Services.GetRequiredService<InternalCallTokenService>();
                var target = new InternalCallTarget(store.Manifest.AppId, "1.0.0", "assembleai:production", "production:read",
                    new(store.TenantId, "application", store.Manifest.AppId, null), "POST", "/probe");
                var id = Guid.NewGuid().ToString("N");
                var token = await service.IssueAsync(principal, store.TenantId, target, id);
                Check(token is not null, "Fixture must issue a token.");
                const string body = "{\"service\":\"production-api\",\"version\":\"1.0.0\",\"method\":\"POST\",\"path\":\"/probe\"}";
                async Task<HttpResponseMessage> Send(string? bearer = null, string? json = null, string? requestId = null)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/calls/validate");
                    if (bearer is not null) request.Headers.Authorization = new("Bearer", bearer);
                    request.Headers.Add("X-Request-ID", requestId ?? id);
                    request.Headers.Add("X-Internal-User", "forged");
                    request.Headers.Add("Cookie", "identity=forged");
                    request.Content = new StringContent(json ?? body, Encoding.UTF8, "application/json");
                    return await http.SendAsync(request);
                }
                using (var success = await Send(token))
                {
                    Check(success.StatusCode == HttpStatusCode.OK, "Trusted target must validate over HTTP.");
                    using var json = JsonDocument.Parse(await success.Content.ReadAsStringAsync());
                    Check(json.RootElement.GetProperty("userId").GetGuid() == store.UserId
                        && json.RootElement.GetProperty("tenantId").GetGuid() == store.TenantId
                        && json.RootElement.GetProperty("requestId").GetString() == id
                        && json.RootElement.EnumerateObject().Count() == 6, "Return only verified identity and target.");
                    Check(success.Headers.CacheControl?.NoStore == true, "Identity response cannot be cached.");
                }
                using (var missing = await Send()) Check(missing.StatusCode == HttpStatusCode.Unauthorized, "Cookie and user headers cannot authenticate.");
                foreach (var changed in new[] { body.Replace("production-api", "other"), body.Replace("1.0.0", "2.0.0"), body.Replace("POST", "GET"), body.Replace("/probe", "/other") })
                    using (var denied = await Send(token, changed)) Check(denied.StatusCode == HttpStatusCode.Forbidden, "Actual service and operation must match signed routing.");
                foreach (var malformed in new[] { "{}", body.TrimEnd('}') + ",\"tenantId\":\"forged\"}", body.TrimEnd('}') + ",\"path\":\"/probe\"}", body.Replace("/probe", "//probe"), new string('x', 3000) })
                    using (var bad = await Send(token, malformed)) Check(bad.StatusCode == HttpStatusCode.BadRequest, "Reject malformed, unknown, duplicate and oversized fields.");
                using (var wrongId = await Send(token, requestId: Guid.NewGuid().ToString("N"))) Check(wrongId.StatusCode == HttpStatusCode.Forbidden, "Request ID binds the token.");
                var reads = store.AccessReads;
                using (var tampered = await Send(token![..^8] + "AAAAAAAA")) Check(tampered.StatusCode == HttpStatusCode.Forbidden, "Invalid signature must fail closed.");
                Check(store.AccessReads == reads, "Invalid signatures must never read authorization identity.");
                store.Version++;
                using (var revoked = await Send(token)) Check(revoked.StatusCode == HttpStatusCode.Forbidden, "Policy version changes invalidate tokens immediately.");
                store.Version--;
                store.Enabled = false;
                using (var disabled = await Send(token)) Check(disabled.StatusCode == HttpStatusCode.Forbidden, "Tenant/global disable rejects existing capabilities.");
                store.Enabled = true; store.Unavailable = true;
                using (var unavailable = await Send(token)) Check(unavailable.StatusCode == HttpStatusCode.ServiceUnavailable, "Store outage cannot become authorization success.");
                store.Unavailable = false;
                var stateReads = 0;
                store.AfterRead = () => { if (++stateReads == 2) clock.Now = clock.Now.AddSeconds(31); };
                using (var expired = await Send(token)) Check(expired.StatusCode == HttpStatusCode.Forbidden, "A final registry check must not return identity after the signed credential expires.");
            }
            finally { await app.StopAsync(); }
            Console.WriteLine("PASS: application validation: fixed registered targets, strict HTTP body, signed identity, request binding, revocation and fail-closed storage.");
        }
        finally
        {
            Check(Path.GetDirectoryName(Path.GetFullPath(directory)) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), "Own test cleanup only.");
            Directory.Delete(directory, true);
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Store : IAuthorizationStore, IApplicationCallStore
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public Guid TenantId { get; } = Guid.NewGuid();
        public long Version { get; set; } = 1;
        public bool Enabled { get; set; } = true;
        public bool Unavailable { get; set; }
        public int AccessReads { get; private set; }
        public Action? AfterRead { get; set; }
        public ApplicationManifest Manifest { get; } = ManifestTests.Valid();
        public Task<ApplicationManifest?> ReadAsync(Guid tenantId, string appId, string releaseVersion, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Unavailable) throw new ApplicationCallStoreUnavailableException();
            AfterRead?.Invoke();
            return Task.FromResult(Enabled && tenantId == TenantId && appId == Manifest.AppId && releaseVersion == Manifest.Version ? Manifest : null);
        }
        public Task<IReadOnlyList<TenantMembership>> ListTenantsAsync(SubjectIdentity identity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AccessSnapshot?> ReadAccessAsync(SubjectIdentity identity, Guid tenantId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); AccessReads++;
            return Task.FromResult<AccessSnapshot?>(identity == new SubjectIdentity("https://id.example", "alice") && tenantId == TenantId
                ? new(UserId, TenantId, Version, [new("production:read", "application", Manifest.AppId, true, new HashSet<Guid>())], new HashSet<Guid>()) : null);
        }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
