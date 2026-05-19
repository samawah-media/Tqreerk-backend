namespace Taqreerk.Application.DTOs.Admin;

/// One big payload behind GET /api/admin/stats/overview. Everything the
/// admin Dashboard page needs comes from one round-trip — no per-card
/// fetches, no n+1 traffic on poll.
///
/// Anything tagged with "needs upstream tracking" in the comments is
/// stubbed (zero / empty array) until the relevant data source lands —
/// see Feature 9 doc for the full deferred list.
public record AdminStatsOverviewDto(
    // ── KPIs ─────────────────────────────────────────────────────────
    int PublishedReports,
    int PendingReviews,
    int UnderReview,
    int TotalOrganizations,
    int PartnerOrganizations,
    int TotalUsers,
    /// Distinct user_id in report_views over the last 30 days. The cheapest
    /// proxy for "active users" we have without a sessions table.
    int MonthlyActiveUsers,
    long TotalViews,
    long TotalDownloads,
    /// Average review duration in seconds across all completed reviews.
    /// Null when no reviews are recorded yet.
    double? AvgReviewSeconds,
    /// (returns / total decisions). Null when there are no decisions yet.
    double? ReturnRate,

    // ── Top-N lists ──────────────────────────────────────────────────
    IReadOnlyList<NamedCountDto> TopSectors,
    IReadOnlyList<NamedCountDto> TopCountries,
    IReadOnlyList<NamedCountDto> TopOrganizations,

    IReadOnlyList<TopReportDto> MostViewedReports,
    IReadOnlyList<TopReportDto> MostDownloadedReports,
    IReadOnlyList<TopReportDto> HighestRatedReports,

    // ── Timeseries (daily buckets, last 30 days, gap-filled) ─────────
    IReadOnlyList<TimeseriesPointDto> UploadsTimeseries,
    IReadOnlyList<TimeseriesPointDto> RegistrationsTimeseries,

    // ── Breakdowns ───────────────────────────────────────────────────
    IReadOnlyList<NamedCountDto> ReportStatusBreakdown,
    IReadOnlyList<NamedCountDto> UserTypeBreakdown,
    IReadOnlyList<NamedCountDto> ReviewDecisionBreakdown,

    /// Last 10 rejection notes — gives the admin a sense of why reports
    /// get rejected without us having to do NLP/clustering.
    IReadOnlyList<RejectionNoteDto> RecentRejections
);

public record NamedCountDto(string Name, int Count);

public record TopReportDto(
    Guid Id,
    string TitleAr,
    string TitleEn,
    string OrganizationNameAr,
    long Metric,
    /// "views" | "downloads" | "rating" — lets the SPA show units.
    string MetricKind
);

public record TimeseriesPointDto(DateTime Date, int Count);

public record RejectionNoteDto(
    Guid ReportId,
    string ReportTitleAr,
    string ReportTitleEn,
    string OrganizationNameAr,
    string? Notes,
    DateTime ReviewedAt
);
