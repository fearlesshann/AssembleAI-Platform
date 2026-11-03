using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AssembleAI.Platform.SmokeTests;

/// <summary>Real Production key persistence, without starting HTTP or a database connection.</summary>
public static class BffKeyPersistenceTests
{
    private const string Purpose = "AssembleAI.Tests.BffKeyPersistence.v1";

    public static async Task RunAsync()
    {
        var workspace = FindWorkspace();
        var temporary = Path.Combine(workspace, ".local-data", "bff-key-persistence-" + Guid.NewGuid().ToString("N"));
        ValidateTemporaryPath(workspace, temporary);
        Assert(!Directory.Exists(temporary), "Key persistence tests must never reuse an existing directory.");
        Directory.CreateDirectory(temporary);
        try
        {
            var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var clientSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var keyDirectory = Path.Combine(temporary, "keys");
            var certificatePath = Path.Combine(temporary, "primary.pfx");
            using var primary = CreateRsaCertificate();
            File.WriteAllBytes(certificatePath, primary.Export(X509ContentType.Pfx, password));

            var plaintext = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            string ciphertext;
            await using (var first = BuildProduction(keyDirectory, certificatePath, password, clientSecret))
            {
                var protector = first.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);
                ciphertext = protector.Protect(plaintext);
                Assert(ciphertext != plaintext, "Production protection must produce protected data.");
            }

            var keyFiles = Directory.GetFiles(keyDirectory, "key-*.xml");
            Assert(keyFiles.Length > 0, "Production DataProtection must persist its key ring to the configured directory.");
            foreach (var keyFile in keyFiles)
            {
                var document = XDocument.Load(keyFile);
                Assert(document.Descendants().Any(element => element.Name.LocalName == "encryptedSecret"),
                    "Persisted Production key material must be encrypted with the configured certificate.");
                Assert(!document.Descendants().Any(element => element.Name.LocalName == "masterKey"),
                    "Persisted Production key XML must not contain a plaintext master key.");
            }

            // Rebuild the entire application and provider, rather than reusing an
            // in-memory key ring. The only shared state is the persisted keys/PFX.
            await using (var restarted = BuildProduction(keyDirectory, certificatePath, password, clientSecret))
            {
                var protector = restarted.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);
                Assert(protector.Unprotect(ciphertext) == plaintext,
                    "A new application instance must recover data using the same persisted keys and certificate.");
            }

            var wrongCertificatePath = Path.Combine(temporary, "different.pfx");
            using var different = CreateRsaCertificate();
            File.WriteAllBytes(wrongCertificatePath, different.Export(X509ContentType.Pfx, password));
            await using (var wrongCertificate = BuildProduction(keyDirectory, wrongCertificatePath, password, clientSecret))
            {
                var protector = wrongCertificate.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);
                var rejected = false;
                try { _ = protector.Unprotect(ciphertext); }
                catch (CryptographicException) { rejected = true; }
                Assert(rejected, "A different certificate must not decrypt existing Production key material.");
            }

            using var publicOnly = X509CertificateLoader.LoadCertificate(primary.Export(X509ContentType.Cert));
            var publicOnlyPath = Path.Combine(temporary, "public-only.pfx");
            File.WriteAllBytes(publicOnlyPath, publicOnly.Export(X509ContentType.Pfx, password));
            await VerifyRejectedCertificate(keyDirectory, publicOnlyPath, password, clientSecret);

            using var ecKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var ecRequest = new CertificateRequest("CN=AssembleAI key persistence test", ecKey, HashAlgorithmName.SHA256);
            using var ecCertificate = ecRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            var ecPath = Path.Combine(temporary, "ecdsa.pfx");
            File.WriteAllBytes(ecPath, ecCertificate.Export(X509ContentType.Pfx, password));
            await VerifyRejectedCertificate(keyDirectory, ecPath, password, clientSecret);
            await VerifyRejectedCertificate(keyDirectory, Path.Combine(temporary, "missing.pfx"), password, clientSecret);
        }
        finally
        {
            // Revalidate the absolute target immediately before recursive cleanup.
            // Only the fresh directory owned by this test may be removed.
            ValidateTemporaryPath(workspace, temporary);
            Directory.Delete(temporary, recursive: true);
        }
        Console.WriteLine("PASS: Production BFF keys: encrypted persistence, recovery after application restart, wrong-certificate rejection and safe invalid-certificate failures.");
    }

    private static WebApplication BuildProduction(string keyDirectory, string certificatePath, string password, string clientSecret) =>
        PlatformApplication.Build([
            "--environment", "Production",
            "--Authentication:Bff:Enabled", "true",
            "--Authentication:Bff:Authority", "https://identity.example.test/realms/test",
            "--Authentication:Bff:PublicOrigin", "https://platform.example.test",
            "--Authentication:Bff:ClientId", "key-persistence-test",
            "--Authentication:Bff:ClientSecret", clientSecret,
            "--Authentication:Bff:DataProtectionKeyPath", keyDirectory,
            "--Authentication:Bff:DataProtectionCertificatePath", certificatePath,
            "--Authentication:Bff:DataProtectionCertificatePassword", password,
            "--ConnectionStrings:Platform", "Host=127.0.0.1;Port=1;Database=unused;Username=unused",
            "--DevelopmentSample:Enabled", "false"
        ], builder => builder.Logging.ClearProviders());

    private static async Task VerifyRejectedCertificate(string keyDirectory, string certificatePath, string password, string clientSecret)
    {
        try
        {
            await using var unexpected = BuildProduction(keyDirectory, certificatePath, password, clientSecret);
        }
        catch (InvalidOperationException exception)
        {
            var failure = exception.ToString();
            Assert(!failure.Contains(password, StringComparison.Ordinal)
                && !failure.Contains(clientSecret, StringComparison.Ordinal)
                && !failure.Contains(certificatePath, StringComparison.Ordinal),
                "Certificate configuration errors must not disclose credentials or certificate paths.");
            return;
        }
        throw new InvalidOperationException("Production BFF must reject a missing certificate or one without an RSA private key.");
    }

    private static X509Certificate2 CreateRsaCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=AssembleAI key persistence test", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature, critical: true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static string FindWorkspace()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AssembleAI.slnx"))) return directory.FullName;
        throw new InvalidOperationException("The key persistence test must run inside the repository workspace.");
    }

    private static void ValidateTemporaryPath(string workspace, string temporary)
    {
        var root = Path.GetFullPath(workspace);
        var absolute = Path.GetFullPath(temporary);
        var localRoot = Path.Combine(root, ".local-data");
        const string prefix = "bff-key-persistence-";
        Assert(string.Equals(Path.GetDirectoryName(absolute), localRoot, StringComparison.OrdinalIgnoreCase)
            && absolute.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(absolute).StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(Path.GetFileName(absolute)[prefix.Length..], "N", out _),
            "Refusing a key-test path outside its dedicated workspace directory.");
        for (var directory = new DirectoryInfo(absolute); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Key persistence test paths must not contain symbolic links or junctions.");
            if (string.Equals(directory.FullName, root, StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
