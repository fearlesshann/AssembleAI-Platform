using System.Security.Claims;
using AssembleAI.Platform.Authorization;

namespace AssembleAI.Platform.Identity;

/// <summary>Reads claims only after an authentication handler has validated the identity.</summary>
public static class TrustedSubject
{
    public static SubjectIdentity? Read(ClaimsPrincipal principal)
    {
        // Do not combine an issuer from one identity with a subject from another.
        var identities = principal.Identities.Where(identity => identity.IsAuthenticated).ToArray();
        if (identities.Length != 1) return null;
        var issuers = identities[0].FindAll("iss").ToArray();
        var subjects = identities[0].FindAll("sub").ToArray();
        if (issuers.Length != 1 || subjects.Length != 1
            || string.IsNullOrWhiteSpace(issuers[0].Value)
            || string.IsNullOrWhiteSpace(subjects[0].Value)) return null;
        return new SubjectIdentity(issuers[0].Value, subjects[0].Value);
    }
}
