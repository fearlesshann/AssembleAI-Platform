using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AssembleAI.Platform.Identity.Sessions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace AssembleAI.Platform.Identity;

public static class BffAuthentication
{
    public const string CookieScheme = "PlatformSession";
    public const string OidcScheme = "PlatformOidc";
    internal static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);

    public static WebApplicationBuilder AddPlatformBff(this WebApplicationBuilder builder)
    {
        var settings = builder.Configuration.GetSection(BffOptions.SectionName).Get<BffOptions>() ?? new();
        var development = builder.Environment.IsDevelopment();
        settings.Validate(development, builder.Configuration);
        builder.Services.AddSingleton(settings);
        builder.Services.TryAddSingleton(TimeProvider.System);

        if (development)
        {
            // Both the disabled shell and development's memory sessions are process-local.
            // Key-ring warmup must not read the developer's unrelated DPAPI profile.
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.Configure<KeyManagementOptions>(options =>
                options.XmlRepository = new DevelopmentKeyRepository());
        }

        if (!settings.Enabled) return builder;

        if (development)
            builder.Services.AddSingleton<ITicketStore, MemoryTicketStore>();
        else
        {
            ConfigurePersistentKeys(builder, settings);
            builder.Services.AddSingleton<ITicketStore, PostgresTicketStore>();
        }

        var securePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = BffEndpoints.CsrfHeaderName;
            options.Cookie.Name = development ? "assembleai-csrf" : "__Host-assembleai-csrf";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = securePolicy;
            options.Cookie.Path = "/";
        });
        builder.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = CookieScheme;
            // Only the explicit login endpoint challenges OIDC. API challenges stay 401.
            options.DefaultChallengeScheme = CookieScheme;
            options.DefaultForbidScheme = CookieScheme;
            options.DefaultSignInScheme = CookieScheme;
            options.DefaultSignOutScheme = CookieScheme;
        })
        .AddCookie(CookieScheme, options =>
        {
            options.Cookie.Name = development ? "assembleai-session" : "__Host-assembleai-session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = securePolicy;
            options.Cookie.Path = "/";
            options.ExpireTimeSpan = SessionLifetime;
            options.SlidingExpiration = false;
            options.Events = new CookieAuthenticationEvents
            {
                OnRedirectToLogin = context => BffEndpoints.Problem(context.HttpContext,
                    StatusCodes.Status401Unauthorized, "Authentication required.", "authentication_required"),
                OnRedirectToAccessDenied = context => BffEndpoints.Problem(context.HttpContext,
                    StatusCodes.Status403Forbidden, "Access denied.", "access_denied"),
                OnSigningIn = context =>
                {
                    var now = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
                    context.Properties.IssuedUtc = now;
                    context.Properties.ExpiresUtc = now.Add(SessionLifetime);
                    context.Properties.AllowRefresh = false;
                    context.Properties.IsPersistent = false;
                    return Task.CompletedTask;
                }
            };
        })
        .AddOpenIdConnect(OidcScheme, options =>
        {
            options.Authority = settings.Authority;
            options.ClientId = settings.ClientId;
            options.ClientSecret = settings.ClientSecret;
            options.SignInScheme = CookieScheme;
            options.CallbackPath = "/signin-oidc";
            // Framework defaults include a GET remote sign-out endpoint. This BFF only
            // supports the local POST endpoint protected by Origin and antiforgery.
            options.RemoteSignOutPath = PathString.Empty;
            options.SignedOutCallbackPath = PathString.Empty;
            options.RequireHttpsMetadata = !development || new Uri(settings.Authority).Scheme == Uri.UriSchemeHttps;
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.ResponseMode = OpenIdConnectResponseMode.Query;
            options.UsePkce = true;
            options.MapInboundClaims = false;
            // The framework's default claim actions otherwise delete the raw issuer
            // after TokenValidated, which would invalidate our (iss, sub) subject.
            options.ClaimActions.Remove("iss");
            options.SaveTokens = true;
            options.GetClaimsFromUserInfoEndpoint = false;
            options.UseTokenLifetime = false;
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.ProtocolValidator.RequireNonce = true;
            // The handler protects state and checks its correlation cookie. Keep that
            // built-in validation; never replace it with browser-supplied identity data.
            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
            options.CorrelationCookie.SecurePolicy = securePolicy;
            options.NonceCookie.SameSite = SameSiteMode.Lax;
            options.NonceCookie.SecurePolicy = securePolicy;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = settings.Authority,
                ValidateAudience = true,
                ValidAudience = settings.ClientId,
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero,
                NameClaimType = "name"
            };
            options.Events = new OpenIdConnectEvents
            {
                OnRedirectToIdentityProvider = context =>
                {
                    context.ProtocolMessage.RedirectUri = settings.PublicOrigin + "/signin-oidc";
                    return Task.CompletedTask;
                },
                OnTokenValidated = context =>
                {
                    var subject = context.Principal is null ? null : TrustedSubject.Read(context.Principal);
                    if (subject is null || !string.Equals(subject.Issuer, settings.Authority, StringComparison.Ordinal))
                        context.Fail("The validated identity is invalid.");
                    return Task.CompletedTask;
                },
                OnTicketReceived = async context =>
                {
                    // Remote handlers run before HttpContext.User is populated. An
                    // overlapping login callback must not renew an existing session
                    // key with another identity, even when both flows began anonymously.
                    if ((await context.HttpContext.AuthenticateAsync(CookieScheme)).Succeeded)
                    {
                        context.HandleResponse();
                        await BffEndpoints.Problem(context.HttpContext, StatusCodes.Status409Conflict,
                            "A session already exists. Sign out before signing in again.", "session_already_exists");
                    }
                },
                OnRemoteFailure = context =>
                {
                    context.HandleResponse();
                    return BffEndpoints.Problem(context.HttpContext, StatusCodes.Status400BadRequest,
                        "Sign-in could not be completed.", "sign_in_failed");
                }
            };
        });
        // SaveTokens must never result in a self-contained browser ticket. Resolve the
        // required store after all regular Cookie options have been configured.
        builder.Services.AddOptions<CookieAuthenticationOptions>(CookieScheme)
            .Configure<TimeProvider>((options, clock) => options.TimeProvider = clock)
            .PostConfigure<ITicketStore>((options, store) => options.SessionStore = store);
        return builder;
    }

    private static void ConfigurePersistentKeys(WebApplicationBuilder builder, BffOptions settings)
    {
        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(settings.DataProtectionCertificatePath,
                settings.DataProtectionCertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
            using var privateKey = certificate.GetRSAPrivateKey();
            if (!certificate.HasPrivateKey || privateKey is null)
            {
                certificate.Dispose();
                throw BffOptions.InvalidConfiguration();
            }
            Directory.CreateDirectory(settings.DataProtectionKeyPath);
            builder.Services.AddSingleton(certificate);
            builder.Services.AddDataProtection().SetApplicationName("AssembleAI.Platform")
                .PersistKeysToFileSystem(new DirectoryInfo(settings.DataProtectionKeyPath))
                .ProtectKeysWithCertificate(certificate);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Configuration errors deliberately omit paths, certificate passwords and
            // underlying provider messages, which may contain deployment secrets.
            throw BffOptions.InvalidConfiguration();
        }
    }
}
