using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using AssembleAI.Platform.Identity;
using AssembleAI.Platform.Identity.Sessions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssembleAI.Platform.SmokeTests;

public static class BffTests
{
    public static async Task RunAsync()
    {
        await VerifyDisabled();
        VerifyInvalidConfiguration();
        await using var provider = await OidcFixture.StartAsync();
        var origin = FreeOrigin();
        var clock = new BffClock();
        await using var app = PlatformApplication.Build([
            "--environment", "Development", "--urls", origin,
            "--Authentication:Bff:Enabled", "true",
            "--Authentication:Bff:Authority", provider.Authority,
            "--Authentication:Bff:ClientId", OidcFixture.ClientId,
            "--Authentication:Bff:ClientSecret", OidcFixture.ClientSecret,
            "--Authentication:Bff:PublicOrigin", origin
        ], builder =>
        {
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<TimeProvider>(clock);
        });
        await app.StartAsync();
        try
        {
            await VerifyLoginAndLogout(app, provider, origin);
            await VerifyConcurrentLogin(provider, origin);
            foreach (var fault in Enum.GetValues<OidcFault>().Where(value => value != OidcFault.None))
                await VerifyInvalidToken(provider, origin, fault);
            await VerifyStateAndCorrelation(provider, origin);
            await VerifyStorageFailure(app, origin);
            await VerifySessionExpiry(provider, origin, clock);
            await VerifyInteractiveLogin(provider, origin);
        }
        finally { await app.StopAsync(); }
        Console.WriteLine("PASS: BFF HTTP OIDC code/S256, JWT/state/nonce/correlation validation, opaque cookies, CSRF/Origin, revocation, storage failures and secure configuration.");
    }

    private static async Task VerifyDisabled()
    {
        await using var app = PlatformApplication.Build([
            "--environment", "Development", "--urls", "http://127.0.0.1:0",
            "--Authentication:Bff:Enabled", "false"
        ], builder => builder.Logging.ClearProviders());
        await app.StartAsync();
        try
        {
            using var client = Client(app.Urls.Single(), new CookieContainer());
            await Expect(client, "/bff/login", HttpStatusCode.ServiceUnavailable);
            await Expect(client, "/bff/session", HttpStatusCode.Unauthorized);
            await Expect(client, "/bff/logout", HttpStatusCode.MethodNotAllowed);
        }
        finally { await app.StopAsync(); }
    }

    private static void VerifyInvalidConfiguration()
    {
        foreach (var (environment, authority, origin) in new[]
        {
            ("Production", "http://127.0.0.1:8080/realms/test", "https://platform.example.test"),
            ("Production", "https://identity.example.test/realms/test", "http://localhost:5100"),
            ("Production", "https://identity.example.test/realms/test", "https://platform.example.test"),
            ("Development", "http://identity.example.test/realms/test", "http://localhost:5100"),
            ("Development", "http://localhost:8080/realms/test", "http://localhost:5100/path"),
            ("Development", "http://localhost:8080/realms/test?secret=hidden", "http://localhost:5100")
        })
        {
            WebApplication? unexpected = null;
            try
            {
                unexpected = PlatformApplication.Build([
                    "--environment", environment, "--Authentication:Bff:Enabled", "true",
                    "--Authentication:Bff:Authority", authority,
                    "--Authentication:Bff:PublicOrigin", origin,
                    "--Authentication:Bff:ClientId", "test",
                    "--Authentication:Bff:ClientSecret", "hidden",
                    "--ConnectionStrings:Platform", "",
                    "--Authentication:Bff:DataProtectionKeyPath", "",
                    "--Authentication:Bff:DataProtectionCertificatePath", ""
                ], builder => builder.Logging.ClearProviders());
            }
            catch (InvalidOperationException exception)
            {
                Assert(!exception.ToString().Contains("hidden", StringComparison.Ordinal), "Configuration failures must not disclose credentials.");
                continue;
            }
            unexpected.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new InvalidOperationException("Insecure or incomplete BFF configuration must be rejected at startup.");
        }
    }

    private static async Task VerifyLoginAndLogout(WebApplication app, OidcFixture provider, string origin)
    {
        var cookies = new CookieContainer();
        using var client = Client(origin, cookies);
        await Expect(client, "/bff/session", HttpStatusCode.Unauthorized);
        await Expect(client, "/api/platform/v1/tenants", HttpStatusCode.Unauthorized);
        foreach (var path in new[] { "//evil.example", "/\\evil.example", "/%2fevil.example", "/%255cevil.example", "/\nheader" })
            await Expect(client, "/bff/login?returnUrl=" + Uri.EscapeDataString(path), HttpStatusCode.BadRequest);
        await Expect(client, "/bff/login?returnUrl=/&returnUrl=/other", HttpStatusCode.BadRequest);
        foreach (var prompt in new[] { "", "none", "login&prompt=login" })
            await Expect(client, "/bff/login?prompt=" + prompt, HttpStatusCode.BadRequest);
        using (var reauthentication = await client.GetAsync("/bff/login?prompt=login&returnUrl=/"))
            Assert(reauthentication.StatusCode == HttpStatusCode.Redirect
                && QueryHelpers.ParseQuery(reauthentication.Headers.Location!.Query)["prompt"] == "login",
                "Explicit login must request credentials from the identity provider, including after local logout.");
        await Expect(client, "/bff/csrf", HttpStatusCode.OK);
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/bff/login"))
        {
            request.Headers.Host = "localhost:1";
            request.Headers.Add("X-Forwarded-Host", "evil.example");
            request.Headers.Add("X-Forwarded-Proto", "https");
            using var response = await client.SendAsync(request);
            Assert(response.StatusCode == HttpStatusCode.Redirect
                && QueryHelpers.ParseQuery(response.Headers.Location!.Query)["redirect_uri"] == origin + "/signin-oidc",
                "Host and forwarded headers cannot change the configured callback.");
        }

        var callback = await StartLogin(client, provider, origin);
        using (var response = await client.GetAsync(callback))
        {
            Assert(response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location?.OriginalString == "/workspace/integration",
                "Validated callback must establish a session and use the local return path.");
            AssertNoStore(response);
            AssertNoToken(response.Headers.ToString(), provider);
        }
        var browserCookie = cookies.GetCookies(new Uri(origin))["assembleai-session"];
        Assert(browserCookie is { HttpOnly: true } && browserCookie.Expires == DateTime.MinValue,
            "Browser must receive an HttpOnly session cookie without a persistent expiry.");
        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(BffAuthentication.CookieScheme);
        var referenceTicket = options.TicketDataFormat.Unprotect(browserCookie!.Value);
        Assert(referenceTicket is not null && referenceTicket.Principal.Claims.Count() == 1
            && !referenceTicket.Properties.GetTokens().Any(), "Decrypted browser cookie must contain only a server-session reference, never tokens.");
        var sessionKey = referenceTicket!.Principal.Claims.Single().Value;
        var store = app.Services.GetRequiredService<ITicketStore>();
        var serverTicket = await store.RetrieveAsync(sessionKey);
        Assert(serverTicket?.Properties.GetTokenValue("access_token") == OidcFixture.AccessToken
            && serverTicket.Properties.GetTokenValue("refresh_token") == OidcFixture.RefreshToken,
            "OIDC tokens must be kept only in the server ticket.");
        Assert(serverTicket!.Properties.ExpiresUtc - serverTicket.Properties.IssuedUtc == TimeSpan.FromHours(8)
            && !options.SlidingExpiration, "Session lifetime must be eight hours without sliding renewal.");
        using (var response = await client.GetAsync("/bff/session"))
        {
            Assert(response.StatusCode == HttpStatusCode.OK, "A validated cookie must authenticate the session endpoint.");
            AssertNoStore(response);
            var body = await response.Content.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);
            Assert(json.RootElement.GetProperty("iss").GetString() == provider.Authority
                && json.RootElement.GetProperty("sub").GetString() == OidcFixture.Subject,
                "Session must expose the original validated issuer and subject.");
            AssertNoToken(body + response.Headers, provider);
        }
        await Expect(client, "/bff/logout", HttpStatusCode.MethodNotAllowed);
        await Expect(client, "/signout-oidc", HttpStatusCode.NotFound, noStore: false);
        await Expect(client, "/bff/session", HttpStatusCode.OK);

        string token;
        using (var response = await client.GetAsync("/bff/csrf"))
        {
            Assert(response.StatusCode == HttpStatusCode.OK, "Authenticated CSRF bootstrap must work.");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert(json.RootElement.GetProperty("headerName").GetString() == "X-CSRF-TOKEN", "CSRF header is part of the API contract.");
            token = json.RootElement.GetProperty("requestToken").GetString()!;
        }
        foreach (var (requestOrigin, requestToken) in new (string?, string?)[]
        {
            (null, token), ("https://evil.example", token), (origin, null), (origin, "invalid")
        })
        {
            using var response = await Logout(client, requestOrigin, requestToken);
            Assert(response.StatusCode == HttpStatusCode.Forbidden, "Logout requires both same Origin and valid CSRF token.");
            await Expect(client, "/bff/session", HttpStatusCode.OK);
        }
        var oldCookie = browserCookie.Value;
        using (var response = await Logout(client, origin, token))
        {
            Assert(response.StatusCode == HttpStatusCode.NoContent, "Verified POST logout must succeed.");
            AssertNoStore(response);
            Assert(response.Headers.Location is null, "Local logout must not leak id_token_hint in a redirect.");
        }
        Assert(await store.RetrieveAsync(sessionKey) is null, "Logout must delete the server session.");
        await Expect(client, "/bff/session", HttpStatusCode.Unauthorized);
        using var replay = Client(origin, new CookieContainer());
        replay.DefaultRequestHeaders.Add("Cookie", "assembleai-session=" + oldCookie);
        await Expect(replay, "/bff/session", HttpStatusCode.Unauthorized);
    }

    private static async Task VerifyInvalidToken(OidcFixture provider, string origin, OidcFault fault)
    {
        using var client = Client(origin, new CookieContainer());
        provider.NextFault = fault;
        var callback = await StartLogin(client, provider, origin);
        using var response = await client.GetAsync(callback);
        Assert(response.StatusCode == HttpStatusCode.BadRequest, $"OIDC {fault} failure must be rejected, got {response.StatusCode}.");
        AssertNoToken(await response.Content.ReadAsStringAsync(), provider);
        await Expect(client, "/bff/session", HttpStatusCode.Unauthorized);
    }

    // V27: an existing reference cannot be rebound to the identity from another login.
    private static async Task VerifyConcurrentLogin(OidcFixture provider, string origin)
    {
        var cookies = new CookieContainer();
        using var client = Client(origin, cookies);
        var firstCallback = await StartLogin(client, provider, origin);
        provider.NextSubject = "bob";
        var secondCallback = await StartLogin(client, provider, origin);
        using var firstResponse = await client.GetAsync(firstCallback);
        Assert(firstResponse.StatusCode == HttpStatusCode.Redirect, "The first concurrent login must succeed.");
        var oldCookie = cookies.GetCookies(new Uri(origin))["assembleai-session"]!.Value;
        using var secondResponse = await client.GetAsync(secondCallback);
        Assert(secondResponse.StatusCode == HttpStatusCode.Conflict,
            "A later OIDC callback must not replace the identity behind an existing session reference.");
        await Expect(client, "/bff/login", HttpStatusCode.Conflict);
        using var replay = Client(origin, new CookieContainer());
        replay.DefaultRequestHeaders.Add("Cookie", "assembleai-session=" + oldCookie);
        using var response = await replay.GetAsync("/bff/session");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert(response.StatusCode == HttpStatusCode.OK
            && json.RootElement.GetProperty("sub").GetString() == OidcFixture.Subject,
            "An old Cookie must never acquire the identity from a different account's login.");
    }

    private static async Task VerifyStateAndCorrelation(OidcFixture provider, string origin)
    {
        foreach (var remove in new[] { "state", "correlation", "nonce" })
        {
            var cookies = new CookieContainer();
            using var client = Client(origin, cookies);
            var callback = await StartLogin(client, provider, origin);
            if (remove == "state")
            {
                var query = QueryHelpers.ParseQuery(callback.Query);
                callback = new Uri(QueryHelpers.AddQueryString(origin + "/signin-oidc", new Dictionary<string, string?>
                {
                    ["code"] = query["code"], ["state"] = "tampered"
                }));
            }
            else
            {
                foreach (Cookie cookie in cookies.GetCookies(callback))
                    if (cookie.Name.Contains(remove, StringComparison.OrdinalIgnoreCase)) cookie.Expired = true;
            }
            using var response = await client.GetAsync(callback);
            Assert(response.StatusCode == HttpStatusCode.BadRequest, $"Missing or invalid {remove} must reject login.");
            await Expect(client, "/bff/session", HttpStatusCode.Unauthorized);
        }
    }

    private static async Task VerifyStorageFailure(WebApplication app, string origin)
    {
        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(BffAuthentication.CookieScheme);
        var originalStore = options.SessionStore;
        try
        {
            options.SessionStore = new UnavailableStore();
            var reference = new AuthenticationTicket(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
                new System.Security.Claims.Claim("Microsoft.AspNetCore.Authentication.Cookies-SessionId", "test-reference")
            ], BffAuthentication.CookieScheme)), new AuthenticationProperties(), BffAuthentication.CookieScheme);
            using var client = Client(origin, new CookieContainer());
            client.DefaultRequestHeaders.Add("Cookie", "assembleai-session=" + options.TicketDataFormat.Protect(reference));
            await Expect(client, "/bff/session", HttpStatusCode.ServiceUnavailable);
            await Expect(client, "/api/platform/v1/tenants", HttpStatusCode.ServiceUnavailable);
        }
        finally { options.SessionStore = originalStore; }
    }

    private static async Task VerifySessionExpiry(OidcFixture provider, string origin, BffClock clock)
    {
        using var client = Client(origin, new CookieContainer());
        var callback = await StartLogin(client, provider, origin);
        using var response = await client.GetAsync(callback);
        Assert(response.StatusCode == HttpStatusCode.Redirect, "Expiry check requires a validated login.");
        await Expect(client, "/bff/session", HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromHours(8));
        await Expect(client, "/bff/session", HttpStatusCode.Unauthorized);
    }

    private static async Task<Uri> StartLogin(HttpClient client, OidcFixture provider, string origin)
    {
        using var challenge = await client.GetAsync("/bff/login?returnUrl=/workspace/integration");
        Assert(challenge.StatusCode == HttpStatusCode.Redirect, "Explicit login must challenge OIDC.");
        AssertNoStore(challenge);
        var authorization = challenge.Headers.Location!;
        var query = QueryHelpers.ParseQuery(authorization.Query);
        Assert(query["response_type"] == "code" && query["code_challenge_method"] == "S256"
            && query["code_challenge"].ToString().Length > 30 && query["redirect_uri"] == origin + "/signin-oidc"
            && !query.ContainsKey("client_secret"), "Login must use S256 code flow with fixed callback and no browser client secret.");
        AssertNoToken(authorization.ToString(), provider);
        using var authorizationResponse = await client.GetAsync(authorization);
        Assert(authorizationResponse.StatusCode == HttpStatusCode.Redirect, "OIDC fixture must issue a single-use authorization code.");
        return authorizationResponse.Headers.Location!;
    }

    private static async Task VerifyInteractiveLogin(OidcFixture provider, string origin)
    {
        var workspace = new DirectoryInfo(AppContext.BaseDirectory);
        while (workspace is not null && !File.Exists(Path.Combine(workspace.FullName, "SPEC.md"))) workspace = workspace.Parent;
        Assert(workspace is not null, "Interactive identity tests require the repository reference assets.");
        provider.EnableInteractiveLogin(Path.Combine(workspace!.FullName, "apps", "portal", "public", "reference-login"));
        var cookies = new CookieContainer();
        using var client = Client(origin, cookies);
        using var challenge = await client.GetAsync("/bff/login?returnUrl=/&prompt=login");
        var authorization = challenge.Headers.Location!;
        using var page = await client.GetAsync(authorization);
        Assert(page.StatusCode == HttpStatusCode.OK, "Interactive preview must show the original login page.");
        Assert(page.Headers.GetValues("Content-Security-Policy").Single().Contains("form-action 'self' " + origin + ";", StringComparison.Ordinal),
            "The preview form CSP allows its validated BFF callback origin so the browser can complete the code flow.");
        var html = await page.Content.ReadAsStringAsync();
        Assert(html.Contains("login-screen", StringComparison.Ordinal) && html.Contains("name=\"password\"", StringComparison.Ordinal), "The complete original form is served by the identity provider.");
        string Token(string value) => Regex.Match(value, "name=\"form_token\" value=\"([^\"]+)\"").Groups[1].Value;
        var token = Token(html);
        Assert(token.Length >= 32, "Original login form has a server-bound CSRF token.");
        async Task<HttpResponseMessage> Submit(HttpClient caller, string formToken, string password, string requestOrigin)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, authorization) {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> {
                    ["username"] = OidcFixture.PreviewUsername, ["password"] = password,
                    ["rememberMe"] = "on", ["form_token"] = formToken }) };
            request.Headers.Add("Origin", requestOrigin);
            return await caller.SendAsync(request);
        }
        using (var deniedOrigin = await Submit(client, token, OidcFixture.PreviewPassword, "https://other.invalid"))
            Assert(deniedOrigin.StatusCode == HttpStatusCode.BadRequest, "Login rejects a foreign Origin before credential validation.");
        using (var stranger = Client(origin, new CookieContainer()))
        using (var deniedCookie = await Submit(stranger, token, OidcFixture.PreviewPassword, provider.Authority))
            Assert(deniedCookie.StatusCode == HttpStatusCode.Forbidden, "A copied form token without the initiating cookie cannot log in.");
        var exchangedBefore = provider.SuccessfulTokenRequests;
        using (var wrong = await Submit(client, token, "wrong-local-preview-password", provider.Authority))
        {
            var errorPage = await wrong.Content.ReadAsStringAsync();
            Assert(wrong.StatusCode == HttpStatusCode.OK && errorPage.Contains("账号或密码不正确", StringComparison.Ordinal)
                && !errorPage.Contains("wrong-local-preview-password", StringComparison.Ordinal), "Wrong credentials show the original error state without echoing the password.");
            token = Token(errorPage);
        }
        Assert(provider.SuccessfulTokenRequests == exchangedBefore, "Invalid credentials cannot issue or exchange an authorization code.");
        await Expect(client, "/bff/session", HttpStatusCode.Unauthorized);
        using (var accepted = await Submit(client, token, OidcFixture.PreviewPassword, provider.Authority))
        {
            Assert(accepted.StatusCode == HttpStatusCode.Redirect, "Valid credentials continue the real OIDC code flow.");
            using var callback = await client.GetAsync(accepted.Headers.Location);
            Assert(callback.StatusCode == HttpStatusCode.Redirect, "PKCE callback establishes the BFF session.");
        }
        await Expect(client, "/bff/session", HttpStatusCode.OK);
        using (var replay = await Submit(client, token, OidcFixture.PreviewPassword, provider.Authority))
            Assert(replay.StatusCode == HttpStatusCode.Forbidden, "Login form submission tokens are single use.");
        Assert(cookies.GetCookies(new Uri(provider.Authority + "/authorize"))["assembleai.preview.account"] is { HttpOnly: true, Value: OidcFixture.PreviewUsername },
            "Remember account stores only the username, never a password or authenticated session.");
        using var csrf = await client.GetAsync("/bff/csrf");
        using var document = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());
        using var logout = await Logout(client, origin, document.RootElement.GetProperty("requestToken").GetString());
        Assert(logout.StatusCode == HttpStatusCode.NoContent, "Local logout succeeds after interactive login.");
        await Expect(client, "/bff/session", HttpStatusCode.Unauthorized);
        using var afterLogout = await client.GetAsync("/bff/login?prompt=login");
        using var loginAgain = await client.GetAsync(afterLogout.Headers.Location);
        Assert(loginAgain.StatusCode == HttpStatusCode.OK, "Local logout returns to the credential form without signing in again.");
        await Expect(client, "/bff/session", HttpStatusCode.Unauthorized);
    }

    private static async Task<HttpResponseMessage> Logout(HttpClient client, string? origin, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bff/logout");
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (token is not null) request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request);
    }

    private static async Task Expect(HttpClient client, string path, HttpStatusCode status, bool noStore = true)
    {
        using var response = await client.GetAsync(path);
        Assert(response.StatusCode == status, $"{path}: expected {status}, got {response.StatusCode}.");
        if (noStore) AssertNoStore(response);
    }

    private static HttpClient Client(string origin, CookieContainer cookies) => new(new HttpClientHandler
    {
        AllowAutoRedirect = false, CookieContainer = cookies
    }) { BaseAddress = new Uri(origin), Timeout = TimeSpan.FromSeconds(15) };

    private static string FreeOrigin()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void AssertNoStore(HttpResponseMessage response) => Assert(response.Headers.CacheControl?.NoStore == true,
        "Authentication and authorization responses must forbid caching.");

    private static void AssertNoToken(string value, OidcFixture provider) => Assert(
        !value.Contains(OidcFixture.AccessToken, StringComparison.Ordinal)
        && !value.Contains(OidcFixture.RefreshToken, StringComparison.Ordinal)
        && (string.IsNullOrEmpty(provider.LastIdToken) || !value.Contains(provider.LastIdToken, StringComparison.Ordinal)),
        "Browser responses and redirects must never contain OIDC tokens.");

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class UnavailableStore : ITicketStore
    {
        public Task<string> StoreAsync(AuthenticationTicket ticket) => throw new BffSessionStoreException();
        public Task RenewAsync(string key, AuthenticationTicket ticket) => throw new BffSessionStoreException();
        public Task<AuthenticationTicket?> RetrieveAsync(string key) => throw new BffSessionStoreException();
        public Task RemoveAsync(string key) => throw new BffSessionStoreException();
    }

    private sealed class BffClock : TimeProvider
    {
        private TimeSpan offset;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.Add(offset);
        public void Advance(TimeSpan duration) => offset += duration;
    }
}
