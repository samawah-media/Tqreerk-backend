using Taqreerk.Domain.Enums;

namespace Taqreerk.Application.DTOs.Me;

/// One row in the individual dashboard's "saved files" grid. Mirrors
/// the public ReportCard payload so the dashboard can render the same
/// rich card the user sees in the library — title, slug (for
/// navigation), cover, organisation, sector, country, year, page
/// count, view count, and the save timestamp for ordering.
public sealed record MySavedReportDto(
    Guid Id,
    string TitleAr,
    string TitleEn,
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
///
/// `Metadata` is the raw JSON stored on the usage row. The frontend uses
/// it to render richer copy:
///   - AiCompare: `{ reportIds: [guid, ...] }` — the additional report
///     IDs in the comparison set (the primary is already in ResourceId).
///     `RelatedReports` carries the title/slug for those extras so the
///     UI can say "قارنت X بـ Y" without a second round-trip.
///   - AiTranslate: `{ targetLanguage: "en" }` — render "ترجمت X إلى الإنجليزية".
public sealed record MyActivityItemDto(
    Guid Id,
    UsageActionType ActionType,
    Guid? ResourceId,
    string? ReportTitleAr,
    string? ReportTitleEn,
    string? ReportSlug,
    DateTime OccurredAt,
    string? Metadata,
    IReadOnlyList<ActivityRelatedReportDto> RelatedReports);

/// Lightweight title/slug pair for reports referenced from a usage row's
/// Metadata but NOT the primary ResourceId — e.g. the additional reports
/// in an AiCompare set. Resolved by joining metadata.reportIds against
/// the reports table.
public sealed record ActivityRelatedReportDto(
    Guid Id,
    string TitleAr,
    string TitleEn,
    string Slug);
