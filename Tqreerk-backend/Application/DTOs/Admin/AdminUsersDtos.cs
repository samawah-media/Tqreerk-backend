using System.ComponentModel.DataAnnotations;

namespace Taqreerk.Application.DTOs.Admin;

/// Row in the admin users table. Lean — the heavy joins (org memberships,
/// uploaded-report counts) live on the detail endpoint so the list query
/// scales with user count and not with related entities.
public record AdminUserListItemDto(
    Guid Id,
    string FullName,
    string Email,
    /// "individual" | "orgMember" | "staff". Derived in the service from
    /// IsPlatformStaff + presence of org memberships, not stored on the
    /// row, so a user transitioning categories doesn't need a backfill.
    string UserType,
    string Status,
    bool EmailVerified,
    string? CountryNameAr,
    DateTime CreatedAt
);

/// Filters for GET /api/admin/users. Mirrors the plan's filter list. Free-
/// text Q searches name + email; everything else is enum/Guid/bool.
public record AdminUsersListRequest
{
    public string? Q { get; init; }

    /// One of "individual", "orgMember", "staff". Anything else is ignored
    /// — keeps unknown values from breaking pagination.
    public string? UserType { get; init; }

    public string? Status { get; init; }
    public Guid? CountryId { get; init; }

    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}

/// Detail surface for /api/admin/users/{id}. Includes derived counts and
/// the list of org memberships (rendered as a tab on the detail page).
public record AdminUserDetailDto(
    Guid Id,
    string FullName,
    string Email,
    string? Phone,
    string UserType,
    string Status,
    bool EmailVerified,
    bool PhoneVerified,
    bool IsPlatformStaff,
    string PreferredLanguage,
    string? JobTitle,
    Guid? CountryId,
    string? CountryNameAr,
    DateTime CreatedAt,
    DateTime? LockoutEndsAt,
    /// Reports the user has uploaded across all orgs. Counts every status
    /// (drafts, rejected, etc.) — gives the admin a sense of activity.
    int UploadedReportsCount,
    IReadOnlyList<AdminUserOrgMembershipDto> Organizations
);

/// One row in the user's "Organizations" tab. Joined to the role + org
/// status so the admin can spot members of suspended orgs at a glance.
public record AdminUserOrgMembershipDto(
    Guid OrganizationId,
    string NameAr,
    string Slug,
    string OrganizationStatus,
    string RoleName,
    DateTime JoinedAt,
    bool IsActive
);

/// Body of POST /api/admin/users/{id}/ban. The reason ends up on the audit
/// row so the admin team can see why an action was taken later.
public record BanUserRequest(
    [Required, StringLength(2000, MinimumLength = 5)] string Reason
);

/// Row in the user's "Reports uploaded" tab. Same shape as the org-detail
/// reports list — kept separate to avoid the two pages diverging if either
/// gains a column the other doesn't need.
public record AdminUserReportItemDto(
    Guid Id,
    string Title,
    string Status,
    Guid OrganizationId,
    string OrganizationNameAr,
    DateTime CreatedAt,
    DateTime? PublishedAt
);
