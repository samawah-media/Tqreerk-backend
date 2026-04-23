namespace Taqreerk.API.Authorization;

public static class PermissionClaims
{
    public const string PolicyPrefix = "perm:";
    public const string ClaimType = "permissions";
    public const string RoleClaimType = System.Security.Claims.ClaimTypes.Role;
}
