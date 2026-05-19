namespace Taqreerk.Application.DTOs.Analytics;

/// Top-level org analytics payload for the date range the caller passed.
/// All counts are computed against ReportView / ReportRating rows whose
/// timestamp falls inside [from, to] (inclusive). The `Totals` block is
/// the org-wide rollup; `Series` powers the line chart; `TopReports`
/// drives the leaderboard table.
public sealed record OrganizationAnalyticsDto(
    DateTime From,
    DateTime To,
    AnalyticsTotalsDto Totals,
    IReadOnlyList<DailyViewsPointDto> Series,
    IReadOnlyList<ReportLeaderboardItemDto> TopReports);

public sealed record AnalyticsTotalsDto(
    int PublishedReports,
    long TotalViews,
    int TotalRatings,
    decimal AverageRating);

/// One day on the views chart. Ranges with a long span still return one
/// row per day — the frontend can downsample for display.
public sealed record DailyViewsPointDto(
    DateOnly Date,
    long Views);

/// One row in the per-report performance table. Ordered by Views desc on
/// the way out so the top-performing reports float to the top of the
/// leaderboard.
public sealed record ReportLeaderboardItemDto(
    Guid ReportId,
    string TitleAr,
    string TitleEn,
    string Slug,
    string? CoverImageUrl,
    long Views,
    int Ratings,
    decimal AverageRating);

/// Drilldown payload for a single report — same shape as the org-wide
/// report but scoped to one report id. Powers the modal that opens when
/// the user clicks a row in the leaderboard.
public sealed record ReportAnalyticsDto(
    Guid ReportId,
    string TitleAr,
    string TitleEn,
    DateTime From,
    DateTime To,
    long TotalViews,
    int TotalRatings,
    decimal AverageRating,
    IReadOnlyList<DailyViewsPointDto> Series);
