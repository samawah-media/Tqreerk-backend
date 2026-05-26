using Microsoft.AspNetCore.Authorization;

namespace Taqreerk.API.Authorization;

public class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return Task.CompletedTask;

        var has = context.User.Claims
            .Any(c => c.Type == PermissionClaims.ClaimType &&
                      string.Equals(c.Value, requirement.Permission, StringComparison.OrdinalIgnoreCase));

        if (has)
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
