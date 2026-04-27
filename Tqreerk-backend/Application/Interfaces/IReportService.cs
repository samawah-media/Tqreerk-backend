using Taqreerk.Application.DTOs.Reports;

namespace Taqreerk.Application.Interfaces;

public interface IReportService
{
    /// Create a new report owned by the caller's organization. The PDF is
    /// uploaded to GCS; metadata is persisted; status starts at Draft.
    Task<ReportDetailDto> CreateAsync(
        Guid currentUserId,
        CreateReportRequest req,
        Stream fileContent,
        string originalFileName,
        string contentType,
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
}
