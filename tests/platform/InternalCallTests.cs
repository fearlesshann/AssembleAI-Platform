using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.InternalCalls;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace AssembleAI.Platform.SmokeTests;

public static class InternalCallTests
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(FindWorkspace(), ".local-data", "internal-call-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var rsa = RSA.Create(2048);
            using var certificate = Certificate(rsa);
            var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var pfx = Path.Combine(directory, "signer.pfx");
            var cer = Path.Combine(directory, "signer.cer");
            File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pfx, password));
            File.WriteAllBytes(cer, certificate.Export(X509ContentType.Cert));
            var options = Options(pfx, password);
            options.Validate(development: false);
            using var keys = new InternalCallKeys(options);
            var clock = new FixedClock();
            var store = new AccessStore();
            var service = new InternalCallTokenService(options, keys, store, clock);
            var principal = Principal();
            var target = Target(store);
            const string requestId = "internal-contract-request";
            var token = await service.IssueAsync(principal, store.TenantId, target, requestId);
            Assert(token is not null, "A trusted member with the exact permission must receive an internal token.");
            var verified = await service.ValidateAsync(token!, target, requestId);
            Assert(verified is not null && verified.UserId == store.UserId && verified.TenantId == store.TenantId
                && verified.AuthorizationVersion == 7 && verified.RequestId == requestId,
                "The internal token must recover only the verified call identity and policy version.");
            Assert(store.Reads == 2, "Issuance and consumption must independently read current authorization.");
            using var body = Payload(token!);
            Assert(body.RootElement.GetProperty("exp").GetInt64() - body.RootElement.GetProperty("iat").GetInt64() == 30,
                "The internal credential lifetime must be exactly 30 seconds.");
            Assert(!token!.Contains("fixture-access-token", StringComparison.Ordinal), "OIDC access tokens must never become internal call credentials.");

            var publicOptions = Options("", "", [cer]);
            publicOptions.Validate(false);
            using var publicKeys = new InternalCallKeys(publicOptions);
            var publicService = new InternalCallTokenService(publicOptions, publicKeys, store, clock);
            Assert(await publicService.ValidateAsync(token, target, requestId) is not null,
                "A consumer with only a public certificate must validate without the signing private key.");
            Assert(await publicService.IssueAsync(principal, store.TenantId, target, requestId) is null,
                "A public-key-only consumer must not issue credentials.");
            await VerifyRejectedInputs(service, principal, store, target, token, requestId);
            await VerifyTokenAttacks(service, rsa, keys, body.RootElement, target, requestId, clock);
            await VerifyFreshAuthorization(service, store, target, token, requestId);
            await VerifyRotation(directory, pfx, password, cer, token, target, store, clock, requestId);
            await VerifyConfigurationAndJwks(options, keys, directory, password, target, store, clock);
            Console.WriteLine("PASS: internal calls: RS256/public-only verification, exact targets, strict JWT shape/time, current authorization, key rotation and public JWKS boundaries.");
        }
        finally
        {
            var target = Path.GetFullPath(directory);
            var parent = Path.GetFullPath(Path.Combine(FindWorkspace(), ".local-data", "internal-call-tests"));
            Assert(Path.GetDirectoryName(target) == parent, "Internal-call fixture cleanup must stay inside its owned root.");
            Directory.Delete(target, recursive: true);
        }
    }

    private static async Task VerifyRejectedInputs(InternalCallTokenService service, ClaimsPrincipal principal,
        AccessStore store, InternalCallTarget target, string token, string requestId)
    {
        Assert(await service.IssueAsync(new ClaimsPrincipal(new ClaimsIdentity()), store.TenantId, target, requestId) is null,
            "Anonymous callers cannot issue internal credentials.");
        Assert(await service.IssueAsync(principal, Guid.NewGuid(), target, requestId) is null,
            "The selected tenant must equal the trusted resource tenant.");
        Assert(await service.IssueAsync(principal, store.TenantId, target with { Permission = "dataset:write" }, requestId) is null,
            "An unrelated permission cannot be signed.");
        Assert(await service.IssueAsync(principal, store.TenantId, target with { Resource = target.Resource with { OrganizationId = Guid.NewGuid() } }, requestId) is null,
            "A different organization cannot inherit the grant.");
        foreach (var changed in new[]
        {
            target with { AppId = "other-app" }, target with { ReleaseVersion = "2.0.0" },
            target with { Audience = "other-audience" }, target with { Permission = "dataset:write" },
            target with { Method = "POST" }, target with { Path = "/another" },
            target with { Resource = target.Resource with { Id = "another" } },
            target with { Resource = target.Resource with { Type = "another" } },
            target with { Resource = target.Resource with { TenantId = Guid.NewGuid() } },
            target with { Resource = target.Resource with { OrganizationId = Guid.NewGuid() } }
        }) Assert(await service.ValidateAsync(token, changed, requestId) is null,
            "A credential must bind every server-defined target dimension.");
        Assert(await service.ValidateAsync(token, target, "different-request") is null, "A different request identifier must be rejected.");
        foreach (var bad in new[] { "", "not-a-jwt", token + ".extra", new string('x', 20000) })
            Assert(await service.ValidateAsync(bad, target, requestId) is null, "Malformed or oversized credentials must fail closed.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ExpectCancelled(() => service.IssueAsync(principal, store.TenantId, target, requestId, cancelled.Token));
        await ExpectCancelled(() => service.ValidateAsync(token, target, requestId, cancelled.Token));
    }

    private static async Task VerifyTokenAttacks(InternalCallTokenService service, RSA signer, InternalCallKeys keys,
        JsonElement original, InternalCallTarget target, string requestId, FixedClock clock)
    {
        using var jwks = JsonDocument.Parse(keys.GetJwksJson());
        var kid = jwks.RootElement.GetProperty("keys")[0].GetProperty("kid").GetString()!;
        var claims = JsonSerializer.Deserialize<Dictionary<string, object?>>(original.GetRawText())!;
        var header = new Dictionary<string, object?> { ["alg"] = "RS256", ["typ"] = "assembleai-internal+jwt", ["kid"] = kid };
        foreach (var (name, value) in new (string, object?)[]
        {
            ("iss", "https://untrusted.example"), ("aud", new[] { target.Audience, "second" }),
            ("aud", "wrong"), ("sub", Guid.NewGuid().ToString("D")), ("authz_ver", 0),
            ("authz_ver", 7.1), ("authz_ver", "7"), ("oidc_sub", "unregistered"),
            ("iat", clock.Now.ToUnixTimeSeconds() + 1), ("nbf", clock.Now.ToUnixTimeSeconds() + 1),
            ("exp", clock.Now.ToUnixTimeSeconds() + 31), ("exp", clock.Now.ToUnixTimeSeconds()),
            ("jti", ""), ("tenant_id", Guid.Empty.ToString("D"))
        })
        {
            var changed = new Dictionary<string, object?>(claims) { [name] = value };
            Assert(await service.ValidateAsync(Sign(signer, header, JsonSerializer.Serialize(changed)), target, requestId) is null,
                "Signed but invalid token claim must be rejected: " + name);
        }
        foreach (var (name, value) in new (string, object?)[]
        {
            ("alg", "HS256"), ("alg", "none"), ("typ", "JWT"), ("kid", "unknown"),
            ("jku", "https://attacker.example/jwks"), ("x5u", "https://attacker.example/cert"), ("crit", new[] { "custom" })
        })
        {
            var changed = new Dictionary<string, object?>(header) { [name] = value };
            Assert(await service.ValidateAsync(Sign(signer, changed, original.GetRawText()), target, requestId) is null,
                "An untrusted token header must be rejected: " + name);
        }
        var duplicate = original.GetRawText().TrimEnd('}') + ",\"aud\":\"" + target.Audience + "\"}";
        Assert(await service.ValidateAsync(Sign(signer, header, duplicate), target, requestId) is null,
            "Duplicate JSON claim names must not be resolved differently by token components.");
        var duplicateHeader = JsonSerializer.Serialize(header).TrimEnd('}') + ",\"kid\":\"" + kid + "\"}";
        Assert(await service.ValidateAsync(SignRaw(signer, duplicateHeader, original.GetRawText()), target, requestId) is null,
            "Duplicate JOSE header names must be rejected.");
        using var attacker = RSA.Create(2048);
        Assert(await service.ValidateAsync(Sign(attacker, header, original.GetRawText()), target, requestId) is null,
            "A matching kid does not make an attacker's signature valid.");
        var token = Sign(signer, header, original.GetRawText());
        clock.Now = clock.Now.AddSeconds(30);
        Assert(await service.ValidateAsync(token, target, requestId) is null, "The credential must expire at 30 seconds with zero grace.");
        clock.Now = clock.Now.AddSeconds(-30);
    }

    private static async Task VerifyFreshAuthorization(InternalCallTokenService service, AccessStore store,
        InternalCallTarget target, string token, string requestId)
    {
        store.Version++;
        Assert(await service.ValidateAsync(token, target, requestId) is null, "A committed policy version change invalidates old credentials.");
        store.Version--;
        store.Enabled = false;
        Assert(await service.ValidateAsync(token, target, requestId) is null, "Membership revocation must reject a still signed credential.");
        store.Enabled = true;
        store.GrantEnabled = false;
        Assert(await service.ValidateAsync(token, target, requestId) is null, "The recipient must recheck exact permission scope.");
        store.GrantEnabled = true;
        store.Unavailable = true;
        try { await service.ValidateAsync(token, target, requestId); throw new InvalidOperationException("Authorization failure must not become success or a stale cache hit."); }
        catch (AuthorizationStoreUnavailableException exception) { Assert(exception.InnerException is null, "Store failures should retain their safe boundary."); }
        finally { store.Unavailable = false; }
    }

    private static async Task VerifyRotation(string directory, string oldPfx, string password, string oldCer,
        string oldToken, InternalCallTarget target, AccessStore store, FixedClock clock, string requestId)
    {
        using var rsa = RSA.Create(2048);
        using var certificate = Certificate(rsa);
        var newPfx = Path.Combine(directory, "rotated.pfx");
        File.WriteAllBytes(newPfx, certificate.Export(X509ContentType.Pfx, password));
        var rotatingOptions = Options(newPfx, password, [oldCer]);
        using var rotatingKeys = new InternalCallKeys(rotatingOptions);
        var rotating = new InternalCallTokenService(rotatingOptions, rotatingKeys, store, clock);
        Assert(await rotating.ValidateAsync(oldToken, target, requestId) is not null,
            "The overlap keyset must still validate the prior signing key.");
        var newToken = await rotating.IssueAsync(Principal(), store.TenantId, target, requestId);
        Assert(newToken is not null && await rotating.ValidateAsync(newToken, target, requestId) is not null,
            "The new active key must sign and validate during overlap.");
        var retiredOptions = Options(newPfx, password);
        using var retiredKeys = new InternalCallKeys(retiredOptions);
        var retired = new InternalCallTokenService(retiredOptions, retiredKeys, store, clock);
        Assert(await retired.ValidateAsync(oldToken, target, requestId) is null,
            "Rebuilding the consumer keyset without the retired key must reject its unexpired credential.");
    }

    private static async Task VerifyConfigurationAndJwks(InternalCallOptions options, InternalCallKeys keys,
        string directory, string password, InternalCallTarget target, AccessStore store, FixedClock clock)
    {
        using var document = JsonDocument.Parse(keys.GetJwksJson());
        var key = document.RootElement.GetProperty("keys")[0];
        Assert(key.GetProperty("kty").GetString() == "RSA" && key.GetProperty("alg").GetString() == "RS256"
            && key.GetProperty("use").GetString() == "sig" && !key.TryGetProperty("d", out _) && !key.TryGetProperty("p", out _),
            "JWKS must contain only RSA public verification material.");
        foreach (var issuer in new[] { "http://external.example", "https://user:pass@example.com", "https://example.com/?secret=x", "https://example.com/#fragment", " https://example.com" })
        {
            var bad = Options(options.SigningCertificatePath, password); bad.Issuer = issuer;
            ExpectInvalid(() => bad.Validate(false));
        }
        var missing = Options(Path.Combine(directory, "missing-secret-path.pfx"), password);
        ExpectInvalid(() => { using var unused = new InternalCallKeys(missing); });
        using var weak = RSA.Create(1024);
        using var weakCertificate = Certificate(weak);
        var weakPath = Path.Combine(directory, "weak.pfx");
        File.WriteAllBytes(weakPath, weakCertificate.Export(X509ContentType.Pfx, password));
        ExpectInvalid(() => { using var unused = new InternalCallKeys(Options(weakPath, password)); });

        var builder = WebApplication.CreateBuilder(["--environment", "Development", "--urls", "http://127.0.0.1:0"]);
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InternalCalls:Enabled"] = "true", ["InternalCalls:Issuer"] = options.Issuer,
            ["InternalCalls:SigningCertificatePath"] = options.SigningCertificatePath,
            ["InternalCalls:SigningCertificatePassword"] = password
        });
        builder.Services.AddSingleton<IAuthorizationStore>(store);
        builder.AddPlatformInternalCalls();
        await using var app = builder.Build();
        app.MapInternalCallEndpoints();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using (var validation = await client.PostAsync("/internal/calls/validate", null))
                Assert(validation.StatusCode == HttpStatusCode.NotFound, "Remote application validation must remain opt-in even when signing is enabled.");
            using var response = await client.GetAsync("/.well-known/assembleai-jwks.json");
            Assert(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true,
                "The configured public JWKS endpoint must respond with no-store.");
            Assert(await response.Content.ReadAsStringAsync() == keys.GetJwksJson(), "The endpoint must expose only the configured public keyset.");
            using var mint = await client.PostAsync("/internal/token", null);
            Assert(mint.StatusCode == HttpStatusCode.NotFound, "No browser token minting endpoint may be installed.");
        }
        finally { await app.StopAsync(); }
        var disabledBuilder = WebApplication.CreateBuilder(["--environment", "Production", "--urls", "http://127.0.0.1:0"]);
        disabledBuilder.Logging.ClearProviders();
        disabledBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["InternalCalls:Enabled"] = "false" });
        disabledBuilder.AddPlatformInternalCalls();
        await using var disabled = disabledBuilder.Build();
        disabled.MapInternalCallEndpoints();
        await disabled.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(disabled.Urls.Single()) };
            using var response = await client.GetAsync("/.well-known/assembleai-jwks.json");
            Assert(response.StatusCode == HttpStatusCode.NotFound, "Disabled internal calls must not publish a keyset.");
        }
        finally { await disabled.StopAsync(); }
    }

    private static InternalCallOptions Options(string pfx, string password, string[]? previous = null) => new()
    {
        Enabled = true, Issuer = "https://platform.example/internal", SigningCertificatePath = pfx,
        SigningCertificatePassword = password, ValidationCertificatePaths = previous ?? []
    };
    private static ClaimsPrincipal Principal() => new(new ClaimsIdentity([new Claim("iss", "https://identity.example"), new Claim("sub", "alice")], "TestValidated"));
    private static InternalCallTarget Target(AccessStore store) => new("reports", "1.0.0", "assembleai:reports", "dataset:read",
        new(store.TenantId, "dataset", "production", store.OrganizationId), "GET", "/reports/production");
    private static X509Certificate2 Certificate(RSA rsa) => new CertificateRequest("CN=internal-call-contract", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
        .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
    private static JsonDocument Payload(string token) => JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.Split('.')[1]));
    private static string Sign(RSA key, Dictionary<string, object?> header, string body) => SignRaw(key, JsonSerializer.Serialize(header), body);
    private static string SignRaw(RSA key, string header, string body)
    {
        var input = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(header)) + "." + Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(body));
        return input + "." + Base64UrlEncoder.Encode(key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
    private static void ExpectInvalid(Action action)
    {
        try { action(); throw new InvalidOperationException("Invalid internal-call configuration was accepted."); }
        catch (InvalidOperationException exception) when (exception.Message == "Internal-call configuration is missing or invalid.") { }
    }
    private static async Task ExpectCancelled(Func<Task> action)
    {
        try { await action(); throw new InvalidOperationException("Cancellation must propagate."); }
        catch (OperationCanceledException) { }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string FindWorkspace()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "AssembleAI.slnx"))) return current.FullName;
        throw new InvalidOperationException("Run internal-call tests inside the workspace.");
    }
    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class AccessStore : IAuthorizationStore
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public Guid TenantId { get; } = Guid.NewGuid();
        public Guid OrganizationId { get; } = Guid.NewGuid();
        public long Version { get; set; } = 7;
        public bool Enabled { get; set; } = true;
        public bool GrantEnabled { get; set; } = true;
        public bool Unavailable { get; set; }
        public int Reads { get; private set; }
        public Task<IReadOnlyList<TenantMembership>> ListTenantsAsync(SubjectIdentity identity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AccessSnapshot?> ReadAccessAsync(SubjectIdentity identity, Guid tenantId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads++;
            if (Unavailable) throw new AuthorizationStoreUnavailableException();
            return Task.FromResult<AccessSnapshot?>(!Enabled || tenantId != TenantId || identity != new SubjectIdentity("https://identity.example", "alice") ? null
                : new(UserId, TenantId, Version, GrantEnabled ? [new("dataset:read", "dataset", "production", false, new HashSet<Guid> { OrganizationId })] : [], new HashSet<Guid> { OrganizationId }));
        }
    }
}
