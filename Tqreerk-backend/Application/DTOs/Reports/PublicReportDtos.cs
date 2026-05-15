namespace Taqreerk.Application.DTOs.Reports;

/// <summary>
/// Set of three width-bounded WebP variants emitted by the cover-image
/// pipeline. URLs are public, signature-free, and served from GCS with a
/// 1-year immutable Cache-Control — the frontend should plug them straight
/// into a <c>&lt;img srcset="..."&gt;</c>.
///
/// Null when a report has no cover, OR when it was uploaded before the
/// variants pipeline shipped (older rows only have <c>CoverImageUrl</c>).
/// </summary>
public record CoverImagesDto(string Thumb, string Medium, string Full);

/// Public summary for the library / homepage carousels. Excludes uploader PII
/// (no email, no IP, no internal status fields). Slug is the canonical
/// identifier the public site uses in URLs — never the GUID.
public record PublicReportListItemDto(
    Guid Id,
    string Slug,
    string Title,
    string? Description,
    string? ReportType,
    string OriginalLanguage,
    int? PublicationYear,
    int? PageCount,
    int ViewsCount,
    int DownloadsCount,
    decimal AvgRating,
    int RatingsCount,
    bool IsFeatured,
    /// Legacy single-image URL. For new reports this is the medium-variant
    /// URL (back-compat alias) and <see cref="CoverImages"/> carries the
    /// full set; for older reports it's a signed URL to a single uploaded
    /// image and <see cref="CoverImages"/> is null.
    string? CoverImageUrl,
    /// Three-variant set for srcset rendering. Null for legacy uploads.
    CoverImagesDto? CoverImages,
    Guid OrganizationId,
    string OrganizationNameAr,
    string OrganizationNameEn,
    Guid? SectorId,
    string? SectorNameAr,
    Guid? CountryId,
    string? CountryNameAr,
    DateTime CreatedAt
);

/// Detail shape for /api/public/reports/{slug}. Same fields as the list item
/// plus the signed file URL + AI summary/topics so the public report page can
/// render the preview content. Translations are not included here — those are
/// surfaced separately if/when we add a public translation viewer.
public record PublicReportDetailDto(
    Guid Id,
    string Slug,
    string Title,
    string? Description,
    string? ReportType,
    string OriginalLanguage,
    int? PublicationYear,
    DateOnly? PublicationDate,
    int? PageCount,
    string? FileUrl,
    string? CoverImageUrl,
    /// Three-variant set for srcset rendering on the detail-page hero.
    /// Null for legacy uploads.
    CoverImagesDto? CoverImages,
    int ViewsCount,
    int DownloadsCount,
    decimal AvgRating,
    int RatingsCount,
    bool IsFeatured,
    Guid OrganizationId,
    string OrganizationNameAr,
    string OrganizationNameEn,
    Guid? SectorId,
    string? SectorNameAr,
    Guid? CountryId,
    string? CountryNameAr,
    string? Summary,
    IReadOnlyList<string> KeyFindings,
    IReadOnlyList<string> Topics,
    /// Total number of (non-deleted) comments. Cheap COUNT — included in
    /// the detail payload so the SPA doesn't have to fan out to fetch it
    /// before rendering the comments header badge.
    int CommentCount,
    /// Total number of users who have recommended this report. Powers
    /// the "Heart" reaction count in the public page header.
    int RecommendationCount,
    DateTime CreatedAt
);

/// Query parameters for the public list. All optional. Filter values use
/// arrays so multiple sectors/countries/organizations can be selected at once.
public record PublicReportListRequest(
    string? Q = null,
    Guid[]? Sectors = null,
    Guid[]? Countries = null,
    Guid[]? Organizations = null,
    int? YearFrom = null,
    int? YearTo = null,
    /// Inclusive page-count bucket bounds. The library sidebar exposes
    /// them as radio buttons ("سريعة < 10" / "متوسطة 10-50" / "عميقة > 50")
    /// — both fields can be null which means "no upper/lower bound".
    int? PageCountMin = null,
    int? PageCountMax = null,
    string? Language = null,
    string? Sort = null,
    int Page = 1,
    int PageSize = 12
);

/// Per-facet count rows the sidebar uses to render the chip badges.
/// Counts respect every active filter EXCEPT the facet being computed,
/// otherwise picking a sector would zero out the rest of the sector list
/// the moment you click it.
public record PublicReportFacetsDto(
    IReadOnlyList<FacetItemDto> Sectors,
    IReadOnlyList<FacetItemDto> Countries,
    IReadOnlyList<FacetItemDto> Organizations,
    IReadOnlyList<FacetItemDto> Languages
);

public record FacetItemDto(string Id, string NameAr, string NameEn, int Count);
