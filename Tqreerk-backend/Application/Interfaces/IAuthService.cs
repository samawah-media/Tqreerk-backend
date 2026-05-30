using Taqreerk.Application.DTOs.Auth;

namespace Taqreerk.Application.Interfaces;

public interface IAuthService
{
    Task<string> RegisterIndividualAsync(RegisterIndividualRequest request, CancellationToken ct = default);
    Task<string> RegisterOrganizationAsync(RegisterOrganizationRequest request, CancellationToken ct = default);
    Task<AuthResult> LoginAsync(LoginRequest request, string? ipAddress, string? deviceInfo, CancellationToken ct = default);

    /// Returns either a full token pair OR a 2FA challenge when the user
    /// is platform staff with 2FA enabled. The controller picks which to
    /// serialize based on which property is non-null.
    Task<LoginOutcome> LoginWithTwoFactorAsync(
        LoginRequest request, string? ipAddress, string? deviceInfo, CancellationToken ct = default);

    /// Step 2 of 2FA login: exchange the challenge token + TOTP code for
    /// real tokens. Throws if the challenge is invalid/expired or the
    /// code is wrong.
    Task<AuthResult> CompleteTwoFactorLoginAsync(
        string challengeToken, string code, string? ipAddress, string? deviceInfo, CancellationToken ct = default);
    Task<AuthResult> RefreshAsync(string refreshToken, string? ipAddress, CancellationToken ct = default);
    Task RevokeTokenAsync(string refreshToken, CancellationToken ct = default);

    Task SendVerificationEmailAsync(string email, CancellationToken ct = default);
    Task ConfirmEmailAsync(string token, CancellationToken ct = default);

    Task SendEmailOtpAsync(string email, CancellationToken ct = default);
    Task<AuthResult> VerifyEmailOtpAsync(
        string email,
        string code,
        string? ipAddress,
        string? deviceInfo,
        Guid? organizationPlanId = null,
        CancellationToken ct = default);

    Task RequestPasswordResetAsync(string email, string? ipAddress, CancellationToken ct = default);
    Task SendPasswordResetOtpAsync(string email, string? ipAddress, CancellationToken ct = default);
    Task<string> VerifyPasswordResetOtpAsync(string email, string code, CancellationToken ct = default);
    Task ResetPasswordAsync(string token, string newPassword, CancellationToken ct = default);

    Task<IReadOnlyList<SessionDto>> GetActiveSessionsAsync(Guid userId, string? currentRefreshToken, CancellationToken ct = default);
    Task RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken ct = default);
    Task RevokeAllSessionsAsync(Guid userId, CancellationToken ct = default);
}
