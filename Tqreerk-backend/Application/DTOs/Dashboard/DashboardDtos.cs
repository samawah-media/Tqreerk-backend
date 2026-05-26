namespace Taqreerk.Application.DTOs.Dashboard;

/// Top-line numbers for the org dashboard. All four are *current totals* —
/// not deltas. The frontend can compute changes by storing yesterday's snapshot
/// if needed; we don't keep historical aggregates server-side yet.
public record OrganizationStatsDto(
    int TotalReports,
    int PublishedReports,
    long TotalViews,
    long TotalDownloads,
    decimal AverageRating,
    int TotalRatings,
    int TeamMembers
);

/// One audit-log row, suitable for a "Recent activity" feed.
public record RecentActivityDto(
    Guid Id,
    string EventType,
    string EntityType,
    Guid? EntityId,
    string? ActorName,
    DateTime CreatedAt
);
