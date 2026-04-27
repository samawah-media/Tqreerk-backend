using Taqreerk.Application.DTOs.Reports;

namespace Taqreerk.Application.Interfaces;

/// Public (anonymous-readable) view over the reports table. All methods filter
/// to Status=Published + soft-delete already handled by the global query
/// filter. Never exposes uploader PII.
public interface IPublicReportService
{
    Task<PagedResult<PublicReportListItemDto>> ListAsync(
        PublicReportListRequest req,
        CancellationToken ct = default);

    /// Look up by slug — the canonical public identifier. Throws KeyNotFound
    /// if the report is missing or not published.
    Task<PublicReportDetailDto> GetBySlugAsync(string slug, CancellationToken ct = default);

    /// Curated featured reports (IsFeatured=true). Capped at `take`.
    Task<IReadOnlyList<PublicReportListItemDto>> GetFeaturedAsync(int take = 5, CancellationToken ct = default);

    /// Most-viewed reports in the last 7 days. Falls back to all-time views
    /// when no recent activity exists. Capped at `take`.
    Task<IReadOnlyList<PublicReportListItemDto>> GetTrendingAsync(int take = 5, CancellationToken ct = default);

    /// Newest published reports. Capped at `take`.
    Task<IReadOnlyList<PublicReportListItemDto>> GetRecentAsync(int take = 8, CancellationToken ct = default);
}
