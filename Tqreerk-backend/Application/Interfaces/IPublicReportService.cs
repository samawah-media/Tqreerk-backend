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

    /// Curated featured reports. Reads from the `featured_reports` table
    /// (the admin Curation page), filtered to active rows whose schedule
    /// window covers "now". When `section` is null, falls back through
    /// HomepageHero → HomepageCarousel so the homepage hero never renders
    /// empty just because the editor only filled the carousel column.
    Task<IReadOnlyList<PublicReportListItemDto>> GetFeaturedAsync(
        int take = 5, string? section = null, CancellationToken ct = default);

    /// Most-viewed reports in the last 7 days. Falls back to all-time views
    /// when no recent activity exists. Capped at `take`.
    Task<IReadOnlyList<PublicReportListItemDto>> GetTrendingAsync(int take = 5, CancellationToken ct = default);

    /// Newest published reports. Capped at `take`.
    Task<IReadOnlyList<PublicReportListItemDto>> GetRecentAsync(int take = 8, CancellationToken ct = default);

    /// Hero-strip rollup for the public Landing page: how many published
    /// reports, how many active organizations, how many individual users
    /// have signed up. Anonymous-readable, cached at the controller.
    Task<PublicStatsOverviewDto> GetPublicStatsAsync(CancellationToken ct = default);

    /// Per-facet counts for the library sidebar. Counts respect every
    /// active filter EXCEPT the facet being computed, so picking a
    /// sector doesn't zero out the rest of the sector list.
    Task<PublicReportFacetsDto> GetFacetsAsync(
        PublicReportListRequest req, CancellationToken ct = default);

    /// "More like this" picks for the public report page footer. Matches
    /// by sector first, falls back to most-viewed overall — never
    /// returns the source report itself.
    Task<IReadOnlyList<PublicReportListItemDto>> GetRelatedAsync(
        string slug, int take = 3, CancellationToken ct = default);
}
