using Taqreerk.Application.DTOs.Admin;

namespace Taqreerk.Application.Interfaces;

/// Owns the TOTP lifecycle for platform staff. Backed by Otp.NET; secrets
/// + backup codes are encrypted at rest via IEncryptionService.
///
/// Calling order from the SPA's perspective:
///   1. Login (email + password) → returns a "requires2Fa" challenge
///      when the user has 2FA enabled.
///   2. /verify with a TOTP code → returns real auth tokens.
///   3. First-time staff: /setup → /activate before /verify is usable.
///   4. Lost device: SuperAdmin calls /reset → user's secret deleted,
///      next login forces them through /setup again.
public interface ITwoFactorService
{
    /// Generates a fresh TOTP secret + backup codes for the user. Stores
    /// them encrypted with IsEnabled=false; the codes are returned ONCE
    /// here (the only time they're exposed in plain text). Idempotent —
    /// calling /setup again before /activate just rotates the unused
    /// secret; calling it after activation is rejected (use /reset
    /// followed by /setup if a user genuinely needs to start over).
    Task<TwoFactorSetupResponse> StartSetupAsync(Guid userId, CancellationToken ct = default);

    /// Activates the secret produced by StartSetupAsync after the user
    /// proves they can read codes from their authenticator. Throws if no
    /// pending setup exists or the code is wrong.
    Task ActivateAsync(Guid userId, string code, CancellationToken ct = default);

    /// Validates a TOTP code (or single-use backup code) against the
    /// stored secret. Updates LastUsedAt on success and consumes a backup
    /// code if that path was used. Returns false on any mismatch.
    Task<bool> VerifyAsync(Guid userId, string code, CancellationToken ct = default);

    /// Mints a fresh batch of backup codes, invalidating the old set.
    /// Requires 2FA to already be active — calling this on a user who
    /// hasn't completed setup throws.
    Task<TwoFactorBackupCodesResponse> RegenerateBackupCodesAsync(
        Guid userId, CancellationToken ct = default);

    /// Wipes the user's 2FA configuration (secret + backup codes). The
    /// next login will force them back through /setup. Used by the
    /// SuperAdmin "lost device" flow.
    Task ResetAsync(Guid userId, CancellationToken ct = default);

    /// Status snapshot for the staff table + the user's own settings UI.
    Task<TwoFactorStatusDto> GetStatusAsync(Guid userId, CancellationToken ct = default);

    /// True when the user must present a TOTP to finish login. False if
    /// they haven't completed setup yet (in which case login proceeds and
    /// the SPA routes them to the setup wizard).
    Task<bool> RequiresVerificationAsync(Guid userId, CancellationToken ct = default);
}
