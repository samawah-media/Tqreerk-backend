using Taqreerk.Application.DTOs.Reports;

namespace Taqreerk.Application.Interfaces;

/// Lightweight value object wrapping a multipart file upload. Keeps the
/// service signatures small as we add optional companion files (cover image
/// today, infographics tomorrow). All fields are required when the wrapper
/// itself is non-null; pass null to omit the file entirely.
public record UploadedFile(Stream Content, string OriginalFileName, string ContentType);

public interface IReportService
{
    /// Create a new report owned by the caller's organization. The main PDF is
    /// required; an optional cover image (PNG/JPEG/WEBP) is stored alongside
    /// it and surfaced as the card thumbnail.
    Task<ReportDetailDto> CreateAsync(
        Guid currentUserId,
        CreateReportRequest req,
        UploadedFile reportFile,
        UploadedFile? coverImage,
        CancellationToken ct = default);

    /// List the caller's org's reports (paginated, optional text filter).
    Task<PagedResult<ReportListItemDto>> ListMineAsync(
        Guid currentUserId,
        int page,
        int pageSize,
        string? query,
        CancellationToken ct = default);

    /// Get a single report. Caller must belong to the owning org (until the
    /// public detail endpoint lands in PR 3).
    Task<ReportDetailDto> GetAsync(
        Guid currentUserId,
        Guid reportId,
        CancellationToken ct = default);

    /// Soft-delete a report. Caller must belong to the owning org and have
    /// the upload role (founder for now; full RBAC in a later PR).
    Task DeleteAsync(
        Guid currentUserId,
        Guid reportId,
        CancellationToken ct = default);

    /// Re-submit a report that was returned for edit. Replaces the PDF
    /// (and optionally the cover) with the new upload, flips status back
    /// to PendingReview, and bumps SubmittedForReviewAt so the queue
    /// re-orders. Only valid when the report is currently in
    /// ReturnedForEdit; any other status throws.
    Task<ReportDetailDto> ResubmitAsync(
        Guid currentUserId,
        Guid reportId,
        UploadedFile reportFile,
        UploadedFile? coverImage,
        CancellationToken ct = default);
}
