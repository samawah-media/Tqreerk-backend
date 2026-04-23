using Taqreerk.Application.DTOs.Auth;

namespace Taqreerk.Application.Interfaces;

public interface IAuthService
{
    Task<AuthResult> RegisterIndividualAsync(RegisterIndividualRequest request, CancellationToken ct = default);
    Task<AuthResult> RegisterOrganizationAsync(RegisterOrganizationRequest request, CancellationToken ct = default);
    Task<AuthResult> LoginAsync(LoginRequest request, string? ipAddress, string? deviceInfo, CancellationToken ct = default);
    Task<AuthResult> RefreshAsync(string refreshToken, string? ipAddress, CancellationToken ct = default);
    Task RevokeTokenAsync(string refreshToken, CancellationToken ct = default);

    Task SendVerificationEmailAsync(string email, CancellationToken ct = default);
    Task ConfirmEmailAsync(string token, CancellationToken ct = default);

    Task RequestPasswordResetAsync(string email, string? ipAddress, CancellationToken ct = default);
    Task ResetPasswordAsync(string token, string newPassword, CancellationToken ct = default);
}
