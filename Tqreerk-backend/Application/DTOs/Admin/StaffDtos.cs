using System.ComponentModel.DataAnnotations;

namespace Taqreerk.Application.DTOs.Admin;

/// Row in the staff table at /admin/staff. Includes 2FA status so the
/// SuperAdmin can spot accounts that haven't completed setup yet.
public record StaffListItemDto(
    Guid Id,
    string FullName,
    string Email,
    /// Platform-scoped role names the user holds, e.g. ["SuperAdmin"]
    /// or ["Admin", "ContentReviewer"]. Empty list means staff flag is
    /// on but no platform role is assigned — show as "—" in the UI.
    IReadOnlyList<string> Roles,
    bool TwoFactorConfigured,
    bool TwoFactorEnabled,
    DateTime? TwoFactorLastUsedAt,
    DateTime CreatedAt
);

/// Body of POST /api/admin/staff. The password is set by the SuperAdmin —
/// the new staff member can rotate it themselves once they log in. 2FA
/// will be required on their first login (they go through /setup before
/// a real session is issued).
public record CreateStaffRequest(
    [Required, EmailAddress, StringLength(255)] string Email,
    [Required, StringLength(150, MinimumLength = 2)] string FullName,
    [Required, StringLength(128, MinimumLength = 8)] string Password,
    /// One of "SuperAdmin", "Admin", "ContentReviewer". Validated server-side
    /// against the platform-scoped roles seeded in RbacSeedData.
    [Required] string RoleName
);

/// Body of PATCH /api/admin/staff/{id}/role. Only the role assignment is
/// editable here; identity fields (email/name) are not on purpose — staff
/// rotate their own profile via the regular user endpoints.
public record UpdateStaffRoleRequest(
    [Required] string RoleName
);
