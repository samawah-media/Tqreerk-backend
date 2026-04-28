using System.ComponentModel.DataAnnotations;

namespace Taqreerk.Application.DTOs.Reports;

/// Summary row for the org's reports list (and the public library, eventually).
public record ReportListItemDto(
    Guid Id,
    string Title,
    string Slug,
    string Status,
    string? ReportType,
    string OriginalLanguage,
    int? PublicationYear,
    int? PageCount,
    int ViewsCount,
    int DownloadsCount,
    decimal AvgRating,
    bool IsFeatured,
    string? CoverImageUrl,
    Guid? SectorId,
    string? SectorNameAr,
    Guid? CountryId,
    string? CountryNameAr,
    DateTime CreatedAt
);

/// Detail view for a single report. Includes the signed read URL when the
/// caller has access — null otherwise.
public record ReportDetailDto(
    Guid Id,
    Guid OrganizationId,
    string OrganizationNameAr,
    Guid UploadedByUserId,
    string Title,
    string Slug,
    string? Description,
    string? ReportType,
    string OriginalLanguage,
    int? PublicationYear,
    DateOnly? PublicationDate,
    int? PageCount,
    string? FileUrl,
    string? CoverImageUrl,
    int ViewsCount,
    int DownloadsCount,
    decimal AvgRating,
    int RatingsCount,
    bool IsFeatured,
    string Status,
    string SourceType,
    Guid? SectorId,
    string? SectorNameAr,
    Guid? CountryId,
    string? CountryNameAr,
    /// Latest review decision the report received (Approved / Rejected /
    /// ReturnedForEdit). Null when nobody has reviewed it yet — the org
    /// dashboard uses this to render the review-notes banner.
    string? LatestReviewDecision,
    string? LatestReviewNotes,
    DateTime? LatestReviewedAt,
    DateTime CreatedAt
);

/// Multipart form for creating a report. The PDF itself is sent as a separate
/// IFormFile in the controller; this DTO carries the metadata fields.
public record CreateReportRequest(
    [Required, MaxLength(500)] string Title,
    [MaxLength(5000)] string? Description,
    [MaxLength(100)] string? ReportType,
    [MaxLength(5)] string? OriginalLanguage,
    int? PublicationYear,
    DateOnly? PublicationDate,
    Guid? SectorId,
    Guid? CountryId
);

public record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Total,
    int Page,
    int PageSize
);

/// Snapshot of where a report is in the AI pipeline. Polled by the frontend
/// every few seconds while `OverallStatus` is Processing.
public record ReportAiStatusDto(
    Guid ReportId,
    string OverallStatus,
    IReadOnlyList<AiJobStatusDto> Jobs,
    ReportAiContentDto? ArabicContent,
    ReportAiContentDto? EnglishContent,
    IReadOnlyList<TranslationStatusDto> Translations
);

public record AiJobStatusDto(
    Guid Id,
    string JobType,
    string Status,
    string? ErrorMessage,
    DateTime? StartedAt,
    DateTime? CompletedAt
);

public record ReportAiContentDto(
    string Language,
    string? Summary,
    IReadOnlyList<string> KeyFindings,
    IReadOnlyList<string> Topics,
    DateTime? GeneratedAt
);

public record TranslationStatusDto(
    string Language,
    string Status,
    string? TranslatedFileUrl,
    DateTime? TranslatedAt
);
