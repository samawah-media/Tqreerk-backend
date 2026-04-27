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

    Task SendEmailOtpAsync(string email, CancellationToken ct = default);
    Task<AuthResult> VerifyEmailOtpAsync(string email, string code, string? ipAddress, string? deviceInfo, CancellationToken ct = default);

    Task RequestPasswordResetAsync(string email, string? ipAddress, CancellationToken ct = default);
    Task ResetPasswordAsync(string token, string newPassword, CancellationToken ct = default);

    Task<IReadOnlyList<SessionDto>> GetActiveSessionsAsync(Guid userId, string? currentRefreshToken, CancellationToken ct = default);
    Task RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken ct = default);
    Task RevokeAllSessionsAsync(Guid userId, CancellationToken ct = default);
}
