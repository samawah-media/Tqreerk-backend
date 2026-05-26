using System.Security.Claims;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Application.Interfaces;

public interface ITokenService
{
    string GenerateAccessToken(
        User user,
        IReadOnlyList<string> roleNames,
        IReadOnlyList<string> permissionKeys);

    string GenerateRefreshToken();
    ClaimsPrincipal? GetPrincipalFromExpiredToken(string token);

    /// Short-lived signed token a staff user gets at step 1 of 2FA login.
    /// Carries only their user id + an expiry; the /2fa/verify endpoint
    /// accepts it in the body and exchanges it for the real auth tokens.
    /// Expires after `lifetime` (default 5 minutes).
    string GenerateTwoFactorChallengeToken(Guid userId, TimeSpan? lifetime = null);

    /// Validates a challenge token previously produced by
    /// GenerateTwoFactorChallengeToken. Returns the embedded user id, or
    /// null if the token is forged, expired, or for a different purpose.
    Guid? ValidateTwoFactorChallengeToken(string token);
}
