using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Taqreerk.API.Authorization;

/// <summary>
/// Dynamically builds authorization policies for any "perm:&lt;page&gt;:&lt;action&gt;"
/// name so we don't have to pre-register a policy for every permission key.
/// Falls back to the default provider for everything else.
/// </summary>
public class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback;

    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    {
        _fallback = new DefaultAuthorizationPolicyProvider(options);
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(PermissionClaims.PolicyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var permission = policyName[PermissionClaims.PolicyPrefix.Length..];
            var policy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(permission))
                .Build();
            return Task.FromResult<AuthorizationPolicy?>(policy);
        }

        return _fallback.GetPolicyAsync(policyName);
    }
}
