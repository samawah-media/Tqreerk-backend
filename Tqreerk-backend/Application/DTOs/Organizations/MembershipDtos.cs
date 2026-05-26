using System.ComponentModel.DataAnnotations;

namespace Taqreerk.Application.DTOs.Organizations;

public record OrganizationMemberDto(
    Guid UserId,
    string FullName,
    string Email,
    bool IsActive,
    bool IsFounder,
    bool IsCurrentUser,
    Guid RoleId,
    string RoleName,
    DateTime JoinedAt
);

/// PATCH body for /api/organizations/me/members/{userId}/role.
/// `RoleName` is one of the seeded org-scoped role names — admin / editor / viewer.
public record ChangeMemberRoleRequest(
    [Required, MaxLength(40)] string RoleName
);

public record OrganizationInvitationDto(
    Guid Id,
    string Email,
    string Status,
    DateTime ExpiresAt,
    DateTime CreatedAt,
    string? InvitedByName
);

public record CreateInvitationRequest(
    [Required, EmailAddress, MaxLength(255)] string Email
);

/// Public (anonymous-readable) summary of an invitation for the accept page.
/// Reveals only the org name and inviter — never anything sensitive.
public record InvitationPreviewDto(
    string OrganizationNameAr,
    string OrganizationNameEn,
    string InvitedEmail,
    string? InvitedByName,
    DateTime ExpiresAt,
    bool IsExpired,
    string Status
);

public record AcceptInvitationRequest(
    [Required] string Token
);
