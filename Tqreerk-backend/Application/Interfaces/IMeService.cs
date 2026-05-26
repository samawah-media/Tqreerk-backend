using Taqreerk.Application.DTOs.Me;

namespace Taqreerk.Application.Interfaces;

/// Caller-scoped reads for the individual dashboard. The service is the
/// glue between the public report library and the dashboard widgets —
/// it does not own write semantics; saves are mutated via
/// IReportInteractionsService and activity rows are written by
/// IUsageService.
public interface IMeService
{
    Task<IReadOnlyList<MySavedReportDto>> ListSavedReportsAsync(
        Guid userId, int take = 20, CancellationToken ct = default);

    Task<IReadOnlyList<MyActivityItemDto>> ListActivityAsync(
        Guid userId, int take = 10, CancellationToken ct = default);

    /// Personalised recommendations: published reports from the sectors
    /// the user marked as interesting (user_interests), excluding ones
    /// they've already viewed. Sorted by avg_rating desc, then views_count
    /// desc. Returns an empty list when the user has no interests yet.
    Task<IReadOnlyList<MySavedReportDto>> ListRecommendationsAsync(
        Guid userId, int take = 20, CancellationToken ct = default);

    /// Compact projection of the caller's active plan + this-month
    /// usage snapshot. Drives the SPA's pre-emptive gating (hide /
    /// disable controls before the user clicks) so the upsell
    /// modals only fire for true edge cases (race conditions, stale
    /// caches).
    Task<PlanFeaturesDto> GetPlanFeaturesAsync(
        Guid userId, CancellationToken ct = default);

    /// Active subscription, or the org's pending-payment row for founders.
    /// Null when the caller has no subscription history.
    Task<MySubscriptionDto?> GetSubscriptionAsync(
        Guid userId, CancellationToken ct = default);
}
