namespace AssembleAI.Platform.Identity;

/// <summary>Explicit deployment settings. Secrets must come from external configuration.</summary>
public sealed class BffOptions
{
    public const string SectionName = "Authentication:Bff";

    public bool Enabled { get; set; }
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string PublicOrigin { get; set; } = "";
    public string DataProtectionKeyPath { get; set; } = "";
    public string DataProtectionCertificatePath { get; set; } = "";
    public string DataProtectionCertificatePassword { get; set; } = "";

    internal void Validate(bool development, IConfiguration configuration)
    {
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret))
            throw InvalidConfiguration();
        if (!TryValidateAddress(Authority, development, out _)
            || !TryValidateAddress(PublicOrigin, development, out var origin)
            || origin.AbsolutePath != "/" || HasOriginPath(PublicOrigin))
            throw InvalidConfiguration();

        // Origin headers have no trailing slash. All callback and Origin checks use this
        // fixed deployment value, never a Host or forwarded header from the request.
        PublicOrigin = origin.GetLeftPart(UriPartial.Authority);
        if (!development && (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Platform"))
            || !Path.IsPathFullyQualified(DataProtectionKeyPath)
            || !Path.IsPathFullyQualified(DataProtectionCertificatePath)))
            throw InvalidConfiguration();
    }

    private static bool TryValidateAddress(string value, bool development, out Uri address)
    {
        address = null!;
        return !string.IsNullOrWhiteSpace(value)
            && string.Equals(value, value.Trim(), StringComparison.Ordinal)
            && !value.Contains('\\')
            && Uri.TryCreate(value, UriKind.Absolute, out address!)
            && string.IsNullOrEmpty(address.UserInfo)
            && string.IsNullOrEmpty(address.Query)
            && string.IsNullOrEmpty(address.Fragment)
            && (address.Scheme == Uri.UriSchemeHttps
                || (development && address.Scheme == Uri.UriSchemeHttp && address.IsLoopback));
    }

    private static bool HasOriginPath(string value)
    {
        // Inspect the original text too: Uri canonicalizes /path/.. to /.
        var slash = value.IndexOf('/', value.IndexOf("://", StringComparison.Ordinal) + 3);
        return slash >= 0 && slash != value.Length - 1;
    }

    internal static InvalidOperationException InvalidConfiguration() =>
        new("BFF authentication configuration is missing or invalid.");
}
