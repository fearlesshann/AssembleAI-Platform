using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace AssembleAI.Platform.InternalCalls;

/// <summary>Static deployment keyset; construct a new instance to rotate or revoke keys.</summary>
public sealed class InternalCallKeys : IDisposable
{
    private readonly Dictionary<string, RsaSecurityKey> validationKeys = new(StringComparer.Ordinal);
    private readonly List<RSA> ownedKeys = [];
    private readonly List<X509Certificate2> ownedCertificates = [];
    private readonly string jwks;
    internal SigningCredentials? SigningCredentials { get; }

    public InternalCallKeys(InternalCallOptions options)
    {
        try
        {
            options.ValidateKeyPaths();
            if (options.SigningCertificatePath.Length > 0)
            {
                var certificate = X509CertificateLoader.LoadPkcs12FromFile(options.SigningCertificatePath,
                    options.SigningCertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
                ownedCertificates.Add(certificate);
                var privateKey = certificate.GetRSAPrivateKey();
                if (privateKey is not null) ownedKeys.Add(privateKey);
                if (!certificate.HasPrivateKey || privateKey is null || privateKey.KeySize < 2048)
                    throw InternalCallOptions.InvalidConfiguration();
                // CNG ephemeral PFX keys can sign while deliberately refusing private
                // export. Retain the certificate/key handles instead of copying secrets.
                var kid = AddPublicKey(privateKey);
                SigningCredentials = new SigningCredentials(SecurityKey(privateKey, kid), SecurityAlgorithms.RsaSha256);
            }
            foreach (var path in options.ValidationCertificatePaths)
            {
                using var certificate = X509CertificateLoader.LoadCertificateFromFile(path);
                using var publicKey = certificate.GetRSAPublicKey();
                if (publicKey is null || publicKey.KeySize < 2048) throw InternalCallOptions.InvalidConfiguration();
                AddPublicKey(publicKey);
            }
            jwks = JsonSerializer.Serialize(new
            {
                keys = validationKeys.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
                {
                    var parameters = pair.Value.Rsa.ExportParameters(false);
                    return new { kty = "RSA", use = "sig", alg = "RS256", kid = pair.Key,
                        n = Base64UrlEncoder.Encode(parameters.Modulus!), e = Base64UrlEncoder.Encode(parameters.Exponent!) };
                }).ToArray()
            });
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException)
        {
            Dispose();
            // Never expose file paths, PFX passwords or cryptographic provider details.
            throw InternalCallOptions.InvalidConfiguration();
        }
    }

    public string GetJwksJson() => jwks;
    internal SecurityKey? FindValidationKey(string kid) => validationKeys.GetValueOrDefault(kid);

    private string AddPublicKey(RSA key)
    {
        var kid = Base64UrlEncoder.Encode(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
        if (!validationKeys.ContainsKey(kid))
        {
            var publicKey = RSA.Create();
            ownedKeys.Add(publicKey);
            publicKey.ImportParameters(key.ExportParameters(false));
            validationKeys.Add(kid, SecurityKey(publicKey, kid));
        }
        return kid;
    }

    private static RsaSecurityKey SecurityKey(RSA key, string kid) => new(key)
    {
        KeyId = kid,
        // A static keyset owns its RSA handles. Do not leave a global cached
        // signature provider holding disposed handles after keyset replacement.
        CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
    };

    public void Dispose()
    {
        foreach (var key in ownedKeys) key.Dispose();
        ownedKeys.Clear();
        foreach (var certificate in ownedCertificates) certificate.Dispose();
        ownedCertificates.Clear();
    }
}
