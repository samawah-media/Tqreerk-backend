using Microsoft.AspNetCore.Authorization;

namespace Taqreerk.API.Authorization;

/// <summary>
/// Shortcut for [Authorize(Policy = "perm:&lt;page&gt;:&lt;action&gt;")].
/// Example: [RequirePermission("reports:view")]
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public class RequirePermissionAttribute : AuthorizeAttribute
{
    public RequirePermissionAttribute(string permission)
        : base(policy: PermissionClaims.PolicyPrefix + permission)
    {
    }
}
