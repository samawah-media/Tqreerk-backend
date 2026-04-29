using Taqreerk.Application.DTOs.Admin;

namespace Taqreerk.Application.Interfaces;

/// Staff management — the SuperAdmin's view of "who can log into the
/// admin app, in what role, and is their 2FA set up". Every write here
/// produces an audit entry.
public interface IStaffService
{
    /// List every user with `is_platform_staff = true`, joined to their
    /// platform-scoped role names + 2FA status. Sorted newest first.
    Task<IReadOnlyList<StaffListItemDto>> ListAsync(CancellationToken ct = default);

    /// Provision a new staff member. Creates the user with the staff flag
    /// set, hashes the password, assigns the requested platform role, and
    /// audit-logs the action under the calling SuperAdmin's id.
    Task<StaffListItemDto> CreateAsync(
        Guid actingUserId, CreateStaffRequest request, CancellationToken ct = default);

    /// Replace the user's platform role. The set of platform roles a user
    /// holds is reduced to exactly { newRole } — we don't support multi-role
    /// staff today. Throws if the target user is not staff.
    Task<StaffListItemDto> UpdateRoleAsync(
        Guid actingUserId, Guid targetUserId, UpdateStaffRoleRequest request, CancellationToken ct = default);

    /// Soft-delete the staff user (User.IsDeleted = true via the global filter)
    /// and clear `is_platform_staff` so a future un-delete doesn't silently
    /// restore admin access. Refuses to delete the last remaining SuperAdmin.
    Task DeleteAsync(Guid actingUserId, Guid targetUserId, CancellationToken ct = default);

    /// Wipe the user's 2FA configuration. Used by the "lost device" flow —
    /// the user is forced through /setup again on their next login. Audit
    /// entry includes the actor (SuperAdmin) and target user id.
    Task ResetTwoFactorAsync(Guid actingUserId, Guid targetUserId, CancellationToken ct = default);
}
