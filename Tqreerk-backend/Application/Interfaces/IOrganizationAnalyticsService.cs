using Taqreerk.Application.DTOs.Analytics;

namespace Taqreerk.Application.Interfaces;

/// Read-only analytics surface for the org dashboard's "تحليلات" tab.
/// Pulls from ReportView + ReportRating against the caller's org —
/// org membership is resolved per-call via OrganizationMembers, same
/// pattern as the existing ReportService / DashboardService.
public interface IOrganizationAnalyticsService
{
    /// Org-wide rollup for the [from, to] window: totals, daily views
    /// time series, and the top-performing reports. `from` and `to` are
    /// inclusive day boundaries — the service normalises to UTC and
    /// expands to a full day on the upper bound.
    Task<OrganizationAnalyticsDto> GetOrganizationAnalyticsAsync(
        Guid currentUserId, DateTime from, DateTime to, CancellationToken ct = default);

    /// Drilldown for a single report. Caller must own the report (via
    /// the org membership) — KeyNotFound otherwise so the service stays
    /// silent on cross-org probing.
    Task<ReportAnalyticsDto> GetReportAnalyticsAsync(
        Guid currentUserId, Guid reportId, DateTime from, DateTime to, CancellationToken ct = default);
}
