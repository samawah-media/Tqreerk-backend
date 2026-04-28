using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

/// One-to-one with User for platform staff. Holds the TOTP secret and
/// single-use backup codes encrypted at rest. The row exists from the
/// moment a staff member starts the setup wizard; `IsEnabled = false`
/// until they prove they can read codes from their authenticator app.
///
/// We use BaseEntity (not soft-deletable) on purpose: when an admin
/// resets a user's 2FA we delete the row outright so the user is forced
/// through setup again on next login. Audit history lives in
/// `admin_action_logs`.
public class Admin2faSecret : BaseEntity
{
    /// FK to User.Id. Marked unique in EF config — a user has at most one
    /// 2FA secret. Re-running setup overwrites the existing row in place.
    public Guid UserId { get; set; }

    /// Base32-encoded TOTP secret, encrypted via IDataProtector before
    /// being persisted. Never read or logged in plain text outside the
    /// service that owns the encryption key.
    public string EncryptedSecret { get; set; } = string.Empty;

    /// JSON array of backup codes (encrypted same way). Each code is
    /// single-use; consumed codes are removed from the array on the next
    /// SaveChanges. Caller mints a fresh batch via the regenerate
    /// endpoint when running low.
    public string EncryptedBackupCodes { get; set; } = string.Empty;

    /// True once the user submits a valid TOTP from their authenticator
    /// during setup. Login enforces 2FA only when this flag is set.
    public bool IsEnabled { get; set; }

    public DateTime? LastUsedAt { get; set; }

    public User User { get; set; } = null!;
}
