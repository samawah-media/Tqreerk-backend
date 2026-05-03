using Taqreerk.Domain.Enums;

namespace Taqreerk.Application.DTOs.Me;

/// One row in the individual dashboard's "saved files" grid. Mirrors
/// the public ReportCard payload so the dashboard can render the same
/// rich card the user sees in the library — title, slug (for
/// navigation), cover, organisation, sector, country, year, page
/// count, view count, and the save timestamp for ordering.
public sealed record MySavedReportDto(
    Guid Id,
    string Title,
    string Slug,
    string? CoverImageUrl,
    string? SectorNameAr,
    string? CountryNameAr,
    int? PublicationYear,
    int? PageCount,
    int ViewsCount,
    string? OrganizationNameAr,
    string? OrganizationLogoUrl,
    DateTime SavedAt);

/// One row in the dashboard's "النشاط الأخير" feed. Derived from
/// usage_tracking — only actions whose ResourceId joins to a published
/// report appear with a title; everything else is summarized by action
/// type alone.
public sealed record MyActivityItemDto(
    Guid Id,
    UsageActionType ActionType,
    Guid? ResourceId,
    string? ReportTitle,
    string? ReportSlug,
    DateTime OccurredAt);
