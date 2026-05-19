using System.ComponentModel.DataAnnotations;

namespace Taqreerk.Application.DTOs.Admin;

/// Row in the admin organizations table. Lean on purpose — counts and
/// rollups are populated lazily on the detail page so the list query
/// stays fast as the org count grows.
public record AdminOrganizationListItemDto(
    Guid Id,
    string NameAr,
    string NameEn,
    string Slug,
    string Type,
    string Status,
    bool IsVerified,
    bool IsPartner,
    bool TranslationEnabled,
    string? CountryCode,
    string? CountryNameAr,
    string? City,
    string? LogoUrl,
    DateTime CreatedAt
);

/// Filters for GET /api/admin/organizations. Status / Type are validated
/// against the matching enums in the service; the SPA picks from a fixed
/// list so unknown values are a developer error, not a user one.
public record AdminOrganizationsListRequest
{
    public string? Q { get; init; }
    public string? Status { get; init; }
    public string? Type { get; init; }
    public Guid? CountryId { get; init; }
    public bool? IsPartner { get; init; }
    public bool? IsVerified { get; init; }

    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}

/// Detail surface for /api/admin/organizations/{id}. Mirrors the list row
/// + the editable fields the PATCH endpoint accepts + a few pre-computed
/// counts for the header strip.
public record AdminOrganizationDetailDto(
    Guid Id,
    string NameAr,
    string NameEn,
    string Slug,
    string Type,
    string Status,
    bool IsVerified,
    bool IsPartner,
    bool TranslationEnabled,
    string? SectorScope,
    Guid? CountryId,
    string? CountryNameAr,
    string? City,
    string? Phone,
    string? WebsiteUrl,
    string? LogoUrl,
    string? Description,
    DateTime CreatedAt,
    /// Total members regardless of role.
    int MemberCount,
    /// Reports the org has uploaded — any status, even drafts.
    int ReportCount,
    /// Subset of ReportCount that are visible in the public library.
    int PublishedReportCount
);

/// Body of PATCH /api/admin/organizations/{id}. Every field is optional;
/// only set ones get applied. Validators enforce length so a runaway
/// payload can't bloat the row.
public record UpdateAdminOrganizationRequest(
    [StringLength(200)] string? NameAr,
    [StringLength(200)] string? NameEn,
    [StringLength(50)] string? Type,
    [StringLength(200)] string? SectorScope,
    Guid? CountryId,
    [StringLength(100)] string? City,
    [StringLength(50)] string? Phone,
    [StringLength(500)] string? WebsiteUrl,
    [StringLength(2000)] string? Description,
    bool? IsPartner,
    bool? TranslationEnabled
);

/// Body of POST /api/admin/organizations/{id}/suspend. Reason is required
/// and shown in the audit log + (eventually) the rejection email/notification
/// to the org members. Bounds match the audit log column width.
public record SuspendOrganizationRequest(
    [Required, StringLength(2000, MinimumLength = 5)] string Reason
);

/// Row in the org-detail "Reports" tab. Same fields as the org dashboard,
/// just without permission filtering since admin sees everything.
public record AdminOrganizationReportItemDto(
    Guid Id,
    string TitleAr,
    string TitleEn,
    string Status,
    string? Slug,
    DateTime CreatedAt,
    DateTime? PublishedAt,
    int ViewCount
);

/// Row in the org-detail "Members" tab. Joined to the user table so we
/// can show identity + role + activity in one row.
public record AdminOrganizationMemberDto(
    Guid UserId,
    string FullName,
    string Email,
    string RoleName,
    DateTime JoinedAt,
    bool IsActive
);
