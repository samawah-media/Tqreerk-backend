using System.ComponentModel.DataAnnotations;

namespace Taqreerk.Application.DTOs.Admin;

/// One row in the Featured kanban. The report fields (Title, Cover,
/// OrgName) are denormalized into the DTO so the kanban renders without
/// a separate fetch per card.
public record FeaturedReportDto(
    Guid Id,
    Guid ReportId,
    string Section,
    int Position,
    DateTime? FeaturedFrom,
    DateTime? FeaturedUntil,
    bool IsActive,
    DateTime CreatedAt,

    // Report snapshot
    string ReportTitleAr,
    string ReportTitleEn,
    string? ReportSlug,
    string? ReportCoverImageUrl,
    string ReportStatus,
    Guid OrganizationId,
    string OrganizationNameAr
);

/// Body of POST /api/admin/featured. Adds a report to a section, optionally
/// with a window. Position is computed server-side (appended to the end);
/// the SPA reorders via /reorder afterwards if needed.
public record CreateFeaturedReportRequest(
    [Required] Guid ReportId,
    [Required] string Section,
    DateTime? FeaturedFrom,
    DateTime? FeaturedUntil,
    bool? IsActive
);

/// Body of PATCH /api/admin/featured/{id}. Every field optional. Section
/// changes treat the row as moved to the new section's tail (Position is
/// re-derived) — to set a specific Position, follow up with /reorder.
public record UpdateFeaturedReportRequest(
    string? Section,
    DateTime? FeaturedFrom,
    DateTime? FeaturedUntil,
    bool? IsActive
);

/// Body of POST /api/admin/featured/sections/{section}/reorder. The Ids
/// array must contain every existing featured-row in that section exactly
/// once — same shape as the categories reorder for consistency.
public record FeaturedReorderRequest(
    [Required, MinLength(1)] IReadOnlyList<Guid> Ids
);
