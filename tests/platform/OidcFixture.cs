using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AssembleAI.Platform.SmokeTests;

public enum OidcFault
{
    None,
    Issuer,
    Audience,
    Signature,
    Nonce,
    Expired
}

/// <summary>
/// Test-only OIDC server. It exercises the real discovery, code exchange, PKCE and JWT
/// validation handlers; it is not a Keycloak acceptance test or a production login path.
/// </summary>
public sealed class OidcFixture : IAsyncDisposable
{
    public const string ClientId = "bff-test";
    public const string ClientSecret = "test-secret";
    public const string Subject = "alice";
    public const string AccessToken = "fixture-access-token";
    public const string RefreshToken = "fixture-refresh-token";
    private const string KeyId = "fixture-signing-key";

    private readonly WebApplication app;
    private readonly RSA signingRsa = RSA.Create(2048);
    private readonly RSA untrustedRsa = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, AuthorizationCode> codes = new(StringComparer.Ordinal);
    private int nextFault;
    private string nextSubject = Subject;
    private int successfulTokenRequests;
    private IReadOnlyDictionary<string, string>? browserAccounts;

    private OidcFixture(WebApplication app) => this.app = app;

    public string Authority { get; private set; } = "";
    public string? LastIdToken { get; private set; }
    public int SuccessfulTokenRequests => Volatile.Read(ref successfulTokenRequests);
    public string NextSubject
    {
        get => Volatile.Read(ref nextSubject);
        set => Volatile.Write(ref nextSubject, value);
    }
    public OidcFault NextFault
    {
        get => (OidcFault)Volatile.Read(ref nextFault);
        set => Volatile.Write(ref nextFault, (int)value);
    }

    public static async Task<OidcFixture> StartAsync(IReadOnlyDictionary<string, string>? browserAccounts = null)
    {
        var builder = WebApplication.CreateBuilder(["--environment", "Development"]);
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        var fixture = new OidcFixture(app);
        fixture.browserAccounts = browserAccounts is null ? null : new Dictionary<string, string>(browserAccounts, StringComparer.Ordinal);
        app.MapGet("/.well-known/openid-configuration", fixture.Discovery);
        app.MapGet("/jwks", fixture.Jwks);
        app.MapGet("/authorize", fixture.Authorize);
        app.MapPost("/token", (Func<HttpContext, Task<IResult>>)fixture.TokenAsync);
        try
        {
            await app.StartAsync();
            fixture.Authority = app.Urls.Single().TrimEnd('/');
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    private IResult Discovery() => Results.Json(new
    {
        issuer = Authority,
        authorization_endpoint = Authority + "/authorize",
        token_endpoint = Authority + "/token",
        jwks_uri = Authority + "/jwks",
        response_types_supported = new[] { "code" },
        response_modes_supported = new[] { "query" },
        subject_types_supported = new[] { "public" },
        id_token_signing_alg_values_supported = new[] { "RS256" },
        token_endpoint_auth_methods_supported = new[] { "client_secret_post" },
        grant_types_supported = new[] { "authorization_code" },
        code_challenge_methods_supported = new[] { "S256" },
        scopes_supported = new[] { "openid", "profile" }
    });

    private IResult Jwks()
    {
        var key = signingRsa.ExportParameters(false);
        return Results.Json(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA", use = "sig", kid = KeyId, alg = "RS256",
                    n = Base64UrlEncoder.Encode(key.Modulus!),
                    e = Base64UrlEncoder.Encode(key.Exponent!)
                }
            }
        });
    }

    private IResult Authorize(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var query = context.Request.Query;
        var redirect = Unique(query["redirect_uri"]);
        var nonce = Unique(query["nonce"]);
        var state = Unique(query["state"]);
        var challenge = Unique(query["code_challenge"]);
        if (Unique(query["client_id"]) != ClientId
            || Unique(query["response_type"]) != "code"
            || Unique(query["code_challenge_method"]) != "S256"
            || (query.ContainsKey("response_mode") && Unique(query["response_mode"]) != "query")
            || nonce is null || state is null || !IsChallenge(challenge)
            || !Uri.TryCreate(redirect, UriKind.Absolute, out var callback)
            || callback.Scheme != "http" || !callback.IsLoopback
            || callback.AbsolutePath != "/signin-oidc"
            || callback.Query.Length != 0 || callback.Fragment.Length != 0 || callback.UserInfo.Length != 0
            || !(Unique(query["scope"]) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("openid"))
            return ProtocolError("invalid_request");

        string subject;
        if (browserAccounts is not null)
        {
            var selected = Unique(query["__fixture_subject"]);
            if (selected is null)
            {
                var links = browserAccounts.Select(account => "<p><a href=\"" + WebUtility.HtmlEncode(QueryHelpers.AddQueryString(
                    context.Request.Path + context.Request.QueryString, "__fixture_subject", account.Key)) + "\">" + WebUtility.HtmlEncode(account.Value) + "</a></p>");
                return Results.Content("<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><title>OIDC 验收身份</title><h1>选择验收账号</h1>"
                    + string.Join("", links) + "</html>", "text/html; charset=utf-8");
            }
            if (!browserAccounts.ContainsKey(selected)) return ProtocolError("invalid_request");
            subject = selected;
        }
        else subject = Interlocked.Exchange(ref nextSubject, Subject);
        var code = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var fault = (OidcFault)Interlocked.Exchange(ref nextFault, (int)OidcFault.None);
        codes[code] = new(redirect!, nonce, challenge!, fault,
            subject, DateTimeOffset.UtcNow.AddMinutes(2));
        return Results.Redirect(QueryHelpers.AddQueryString(redirect!, new Dictionary<string, string?>
        {
            ["code"] = code,
            ["state"] = state
        }));
    }

    private async Task<IResult> TokenAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        if (!context.Request.HasFormContentType) return ProtocolError("invalid_request");
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (Unique(form["client_id"]) != ClientId || Unique(form["client_secret"]) != ClientSecret)
            return ProtocolError("invalid_client");
        var code = Unique(form["code"]);
        if (Unique(form["grant_type"]) != "authorization_code" || code is null
            || !codes.TryRemove(code, out var authorization))
            return ProtocolError("invalid_grant");

        var verifier = Unique(form["code_verifier"]);
        if (authorization.ExpiresAt <= DateTimeOffset.UtcNow
            || Unique(form["redirect_uri"]) != authorization.RedirectUri
            || verifier is null || verifier.Length is < 43 or > 128
            || !StringComparer.Ordinal.Equals(authorization.Challenge,
                Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))))
            return ProtocolError("invalid_grant");

        var now = DateTime.UtcNow;
        var expired = authorization.Fault == OidcFault.Expired;
        var key = new RsaSecurityKey(authorization.Fault == OidcFault.Signature ? untrustedRsa : signingRsa)
        {
            KeyId = KeyId
        };
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = authorization.Fault == OidcFault.Issuer ? Authority + "/wrong-issuer" : Authority,
            Audience = authorization.Fault == OidcFault.Audience ? "another-client" : ClientId,
            IssuedAt = expired ? now.AddHours(-2) : now,
            NotBefore = expired ? now.AddHours(-2) : now.AddSeconds(-5),
            Expires = expired ? now.AddHours(-1) : now.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = authorization.SubjectName,
                ["name"] = browserAccounts is not null && browserAccounts.TryGetValue(authorization.SubjectName, out var displayName) ? displayName : "Alice Fixture",
                ["nonce"] = authorization.Fault == OidcFault.Nonce ? "incorrect-nonce" : authorization.Nonce
            }
        });
        LastIdToken = token;
        Interlocked.Increment(ref successfulTokenRequests);
        return Results.Json(new
        {
            access_token = AccessToken,
            refresh_token = RefreshToken,
            id_token = token,
            token_type = "Bearer",
            expires_in = 300
        });
    }

    private static string? Unique(StringValues value) => value.Count == 1 && !string.IsNullOrWhiteSpace(value[0])
        ? value[0] : null;

    private static bool IsChallenge(string? value) => value is { Length: 43 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static IResult ProtocolError(string error) => Results.Json(new { error }, statusCode: 400);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
        finally
        {
            codes.Clear();
            signingRsa.Dispose();
            untrustedRsa.Dispose();
        }
    }

    private sealed record AuthorizationCode(
        string RedirectUri, string Nonce, string Challenge, OidcFault Fault, string SubjectName, DateTimeOffset ExpiresAt);
}
