namespace Taqreerk.Application.DTOs.Admin;

/// Lightweight summary of a bulk-import job — used by the admin "my recent
/// imports" list. Keeps the row tight; the per-item breakdown is fetched
/// separately when the admin opens a job.
public record BulkImportJobSummaryDto(
    Guid Id,
    DateTime CreatedAt,
    /// "Pending" | "Processing" | "Completed" | "Failed"
    string Status,
    int TotalCount,
    int CompletedCount,
    int FailedCount,
    string? SourceFileName,
    string? ErrorMessage,
    DateTime? StartedAt,
    DateTime? CompletedAt
);

/// Per-row state for the live progress UI. Includes the source-cell
/// snapshot so the row stays renderable even before the Report row exists.
public record BulkImportItemDto(
    Guid Id,
    int RowIndex,
    /// "Pending" | "Uploading" | "Ingesting" | "Summarizing" | "Completed" | "Failed"
    string Stage,
    string Title,
    string TitleEn,
    string FileUrl,
    string? Source,
    string? Authors,
    string? OriginalLanguage,
    int? PublicationYear,
    string? SectorNameAr,
    string? CountryNameAr,
    string? ReportType,
    Guid? ReportId,
    string? ReportSlug,
    string? ErrorMessage,
    DateTime? StartedAt,
    DateTime? CompletedAt
);

/// Full job detail returned by GET /api/admin/bulk-imports/{id}.
public record BulkImportJobDetailDto(
    Guid Id,
    DateTime CreatedAt,
    string Status,
    int TotalCount,
    int CompletedCount,
    int FailedCount,
    string? SourceFileName,
    string? ErrorMessage,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    IReadOnlyList<BulkImportItemDto> Items
);
