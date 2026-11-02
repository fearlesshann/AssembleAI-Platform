using AssembleAI.Platform.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace AssembleAI.Platform.Identity;

public sealed record PlatformPermissionRequirement(string Permission) : IAuthorizationRequirement;

/// <summary>The resource's ownership must come from a server-side resource lookup.</summary>
public sealed class PlatformPermissionHandler(
    IAuthorizationStore store,
    IHttpContextAccessor httpContextAccessor) : AuthorizationHandler<PlatformPermissionRequirement, ProtectedResource>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PlatformPermissionRequirement requirement,
        ProtectedResource resource)
    {
        var subject = TrustedSubject.Read(context.User);
        if (subject is null) return;
        var cancellationToken = httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;
        var snapshot = await store.ReadAccessAsync(subject, resource.TenantId, cancellationToken);
        if (snapshot is not null && PermissionEvaluator.Allows(snapshot, requirement.Permission, resource))
            context.Succeed(requirement);
    }
}
