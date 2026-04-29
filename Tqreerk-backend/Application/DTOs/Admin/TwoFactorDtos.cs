using System.ComponentModel.DataAnnotations;

namespace Taqreerk.Application.DTOs.Admin;

/// Returned by /api/admin/auth/2fa/setup. The QR code is rendered by the
/// SPA from the otpauth URI; the raw secret is included so the user can
/// type it manually if they can't scan. Backup codes are returned ONCE
/// here — there's no other endpoint that exposes them in plain text.
public record TwoFactorSetupResponse(
    string OtpAuthUri,
    string Secret,
    IReadOnlyList<string> BackupCodes
);

/// Body of /api/admin/auth/2fa/activate (after setup, to enable 2FA).
/// The verify-step (login step 2) needs the challenge token too — see
/// TwoFactorVerifyRequest below.
public record TwoFactorCodeRequest(
    [Required, StringLength(10, MinimumLength = 6)] string Code
);

/// Body of /api/admin/auth/2fa/verify. Carries the challenge token issued
/// by /api/auth/login when 2FA is required, plus the TOTP (or backup) code
/// from the user's authenticator. Anonymous endpoint — the challenge token
/// is the auth.
public record TwoFactorVerifyRequest(
    [Required] string ChallengeToken,
    [Required, StringLength(10, MinimumLength = 6)] string Code
);

/// Returned by the regenerate-backup-codes endpoint. Only the new batch
/// is shown — the old codes are invalidated atomically.
public record TwoFactorBackupCodesResponse(
    IReadOnlyList<string> BackupCodes
);

/// Response shape for /api/admin/auth/2fa/status (used by the staff
/// table + the admin SPA's own badge). Doesn't expose the secret.
public record TwoFactorStatusDto(
    bool IsConfigured,
    bool IsEnabled,
    DateTime? LastUsedAt
);
