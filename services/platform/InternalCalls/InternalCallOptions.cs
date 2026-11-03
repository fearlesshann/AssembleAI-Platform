namespace AssembleAI.Platform.InternalCalls;

/// <summary>Deployment-owned key material, never caller-supplied JWKS addresses.</summary>
public sealed class InternalCallOptions
{
    public const string SectionName = "InternalCalls";
    public bool Enabled { get; set; }
    public bool ValidationEndpointEnabled { get; set; }
    public string Issuer { get; set; } = "";
    public string SigningCertificatePath { get; set; } = "";
    public string SigningCertificatePassword { get; set; } = "";
    public string[] ValidationCertificatePaths { get; set; } = [];

    public void Validate(bool development)
    {
        if (!Enabled) { if (ValidationEndpointEnabled) throw InvalidConfiguration(); return; }
        if (string.IsNullOrWhiteSpace(Issuer) || Issuer != Issuer.Trim() || Issuer.Any(char.IsControl)
            || Issuer.Contains('\\') || !Uri.TryCreate(Issuer, UriKind.Absolute, out var issuer)
            || issuer.UserInfo.Length > 0 || issuer.Query.Length > 0 || issuer.Fragment.Length > 0
            || !(issuer.Scheme == Uri.UriSchemeHttps || development && issuer.Scheme == Uri.UriSchemeHttp && issuer.IsLoopback))
            throw InvalidConfiguration();
        ValidateKeyPaths();
    }

    internal void ValidateKeyPaths()
    {
        if (SigningCertificatePath is null || SigningCertificatePassword is null
            || ValidationCertificatePaths is null || ValidationCertificatePaths.Length > 8
            || SigningCertificatePath.Length == 0 && ValidationCertificatePaths.Length == 0
            || SigningCertificatePath.Length > 0 && !Path.IsPathFullyQualified(SigningCertificatePath)
            || ValidationCertificatePaths.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
            throw InvalidConfiguration();
    }

    internal static InvalidOperationException InvalidConfiguration() =>
        new("Internal-call configuration is missing or invalid.");
}
