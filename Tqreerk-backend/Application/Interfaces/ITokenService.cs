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
}
