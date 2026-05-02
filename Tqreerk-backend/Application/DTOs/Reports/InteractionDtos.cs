using System.ComponentModel.DataAnnotations;

namespace Taqreerk.Application.DTOs.Reports;

/// Body of PUT /api/reports/{id}/rating. Stars are 1..5; reviewers can
/// also pass a short note that's stored alongside the rating.
public record RateReportRequest(
    [Required, Range(1, 5)] int Stars,
    [StringLength(2000)] string? Review = null
);

/// Returned by GET /api/reports/{id}/me — the user-specific bits the
/// public detail page needs to render the right button states. Anonymous
/// callers get 401; the SPA only calls this when there's a token.
public record MyReportInteractionDto(
    bool SavedByMe,
    bool RecommendedByMe,
    /// 1..5 when the caller has rated the report, else null.
    int? MyRating,
    string? MyReview
);

/// Compact aggregate the controller returns after a state-changing call
/// (rate/save/recommend) so the SPA can refresh the badge counts in one
/// round-trip without a follow-up GET.
public record ReportInteractionStateDto(
    Guid ReportId,
    decimal AvgRating,
    int RatingsCount,
    int ViewsCount,
    int RecommendationCount,
    MyReportInteractionDto Mine
);
