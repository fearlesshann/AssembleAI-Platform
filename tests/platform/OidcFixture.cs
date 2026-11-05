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
    private string? interactiveLoginTemplate;
    private readonly Dictionary<string, (byte[] Content, string Type)> loginAssets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Query, DateTimeOffset Expires)> loginForms = new(StringComparer.Ordinal);
    public const string PreviewUsername = "demo";
    public const string PreviewPassword = "AssembleAI-2026!";

    // Explicitly enabled only by the loopback interactive preview, after its
    // server-side setup. Production credentials are handled by the Keycloak theme.
    public void EnableInteractiveLogin(string directory)
    {
        interactiveLoginTemplate = File.ReadAllText(Path.Combine(directory, "login.html"));
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            var type = Path.GetExtension(path) switch { ".css" => "text/css", ".js" => "text/javascript", ".png" => "image/png", ".woff2" => "font/woff2", _ => null };
            if (type is not null) loginAssets.Add(Path.GetFileName(path), (File.ReadAllBytes(path), type));
        }
    }

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
        app.MapPost("/authorize", (Func<HttpContext, Task<IResult>>)fixture.InteractiveLoginAsync);
        app.MapGet("/reference-login/{asset}", (string asset) => fixture.loginAssets.TryGetValue(asset, out var value)
            ? Results.Bytes(value.Content, value.Type) : Results.NotFound());
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

        if (interactiveLoginTemplate is not null && !context.Items.ContainsKey("preview-login-validated"))
            return InteractiveLoginPage(context, context.Request.Cookies["assembleai.preview.account"] ?? "",
                query.ContainsKey("__login_help") ? "本机测试账号：demo；密码：AssembleAI-2026!。仅用于隔离体验环境。" : "");

        string subject;
        if (browserAccounts is not null || (Unique(query["prompt"]) == "login" && !context.Items.ContainsKey("preview-login-validated")))
        {
            var accounts = browserAccounts ?? new Dictionary<string, string> { [Subject] = "Alice Fixture" };
            var selected = Unique(query["__fixture_subject"]);
            if (selected is null)
            {
                var links = accounts.Select(account => "<p><a href=\"" + WebUtility.HtmlEncode(QueryHelpers.AddQueryString(
                    context.Request.Path + context.Request.QueryString, "__fixture_subject", account.Key)) + "\">" + WebUtility.HtmlEncode(account.Value) + "</a></p>");
                return Results.Content("<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><title>OIDC 验收身份</title><h1>选择验收账号</h1>"
                    + string.Join("", links) + "</html>", "text/html; charset=utf-8");
            }
            if (!accounts.ContainsKey(selected)) return ProtocolError("invalid_request");
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

    private IResult InteractiveLoginPage(HttpContext context, string username = "", string error = "", bool remember = false)
    {
        if (interactiveLoginTemplate is null) return Results.NotFound();
        if (!Uri.TryCreate(Unique(context.Request.Query["redirect_uri"]), UriKind.Absolute, out var callback)
            || callback.Scheme != "http" || !callback.IsLoopback || callback.UserInfo.Length != 0
            || callback.AbsolutePath != "/signin-oidc" || callback.Query.Length != 0 || callback.Fragment.Length != 0)
            return ProtocolError("invalid_request");
        foreach (var item in loginForms.Where(item => item.Value.Expires < DateTimeOffset.UtcNow)) loginForms.TryRemove(item.Key, out _);
        if (loginForms.Count >= 128) return Results.StatusCode(429);
        var token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        loginForms[token] = (context.Request.QueryString.Value ?? "", DateTimeOffset.UtcNow.AddMinutes(5));
        context.Response.Headers.CacheControl = "no-store";
        // Chromium applies form-action to the post-login redirect as well. The
        // preview BFF has its own loopback port; allow only its validated origin.
        context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; frame-ancestors 'none'; form-action 'self' "
            + callback.GetLeftPart(UriPartial.Authority) + "; base-uri 'none'";
        context.Response.Cookies.Append("assembleai.preview.login", token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/authorize", MaxAge = TimeSpan.FromMinutes(5) });
        var values = new Dictionary<string, string> {
            ["ASSET_BASE"] = "/reference-login", ["LOGIN_ACTION"] = context.Request.Path + context.Request.QueryString,
            ["USERNAME"] = username, ["ERROR"] = error, ["CSRF_TOKEN"] = token,
            ["FORGOT_HREF"] = QueryHelpers.AddQueryString(context.Request.Path + context.Request.QueryString, "__login_help", "1") };
        var html = interactiveLoginTemplate.Replace("@@ERROR_HIDDEN@@", error.Length == 0 ? "hidden" : "", StringComparison.Ordinal)
            .Replace("@@REMEMBER_CHECKED@@", remember ? "checked" : "", StringComparison.Ordinal).Replace("记住我", "记住账号", StringComparison.Ordinal);
        foreach (var pair in values) html = html.Replace("@@" + pair.Key + "@@", WebUtility.HtmlEncode(pair.Value), StringComparison.Ordinal);
        return Results.Content(html, "text/html; charset=utf-8");
    }

    private async Task<IResult> InteractiveLoginAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (interactiveLoginTemplate is null) return Results.NotFound();
        if (!context.Request.HasFormContentType || context.Request.ContentLength is null or > 8192
            || context.Request.Headers.Origin != Authority) return Results.StatusCode(400);
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var token = Unique(form["form_token"]);
        if (token is null || token != context.Request.Cookies["assembleai.preview.login"]
            || !loginForms.TryRemove(token, out var pending) || pending.Expires < DateTimeOffset.UtcNow
            || pending.Query != context.Request.QueryString.Value) return Results.StatusCode(403);
        var username = Unique(form["username"]) ?? "";
        var password = Unique(form["password"]) ?? "";
        var remember = Unique(form["rememberMe"]) is not null;
        if (username != PreviewUsername || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(password)), SHA256.HashData(Encoding.UTF8.GetBytes(PreviewPassword))))
            return InteractiveLoginPage(context, username.Length <= 256 ? username : "", "账号或密码不正确，请重新输入。", remember);
        if (remember) context.Response.Cookies.Append("assembleai.preview.account", username, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/authorize", MaxAge = TimeSpan.FromHours(4) });
        else context.Response.Cookies.Delete("assembleai.preview.account", new CookieOptions { Path = "/authorize" });
        context.Items["preview-login-validated"] = true;
        return Authorize(context);
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
