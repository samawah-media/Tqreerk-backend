using Taqreerk.Application.DTOs.Usage;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Application.Interfaces;

/// Per-user freemium gate. Counts metered actions in the current billing
/// month and refuses an action when the user's plan cap is hit.
///
/// Distinct from <see cref="IQuotaService"/>: that one throttles AI jobs
/// per-org on a rolling 24h window; this one enforces *plan-level* limits
/// per-user on a calendar-month window.
public interface IUsageService
{
    /// Verify the caller is within the plan limit for this action and, if
    /// so, atomically write a usage_tracking row in the same transaction.
    /// Throws <see cref="UsageLimitExceededException"/> when the cap is hit.
    ///
    /// `resourceId` is stored on the row (e.g. report id) so the row is
    /// useful as an audit trail. Pass null if the action is not tied to a
    /// single resource. The `actionFn` is invoked AFTER the limit check
    /// passes and BEFORE the transaction commits — its return value is
    /// returned to the caller. If `actionFn` throws, the usage row is
    /// rolled back together with whatever it did so we don't burn a count
    /// on a failed action.
    Task<TResult> EnsureWithinLimitAndConsumeAsync<TResult>(
        Guid userId,
        UsageActionType actionType,
        Guid? resourceId,
        Func<CancellationToken, Task<TResult>> actionFn,
        CancellationToken ct = default);

    /// Same as the generic overload but for void actions.
    Task EnsureWithinLimitAndConsumeAsync(
        Guid userId,
        UsageActionType actionType,
        Guid? resourceId,
        Func<CancellationToken, Task> actionFn,
        CancellationToken ct = default);

    /// Read-only check for the user's current month consumption against
    /// their plan limits — used by GET /api/usage/me to render the
    /// dashboard widget. Does not consume.
    Task<UsageSummaryDto> GetMyUsageAsync(Guid userId, CancellationToken ct = default);

    /// Paged history of usage_tracking rows for the given user (newest
    /// first). For GET /api/usage/me/history.
    Task<UsageHistoryPageDto> GetMyHistoryAsync(
        Guid userId,
        int page,
        int pageSize,
        CancellationToken ct = default);
}

/// Thrown when a user has hit the cap for an action under their current
/// plan. The middleware translates this into HTTP 403 with a structured
/// body the SPA uses to render the upgrade prompt — body shape:
///   { status, title, code = "USAGE_LIMIT_EXCEEDED",
///     actionType, limit, used, resetsAt, traceId }
public sealed class UsageLimitExceededException : Exception
{
    public UsageActionType ActionType { get; }
    public int Limit { get; }
    public int Used { get; }

    /// First day of the next billing month (UTC). Frontend renders this
    /// as "resets in X days" so users know when they regain access.
    public DateTime ResetsAt { get; }

    public UsageLimitExceededException(
        UsageActionType actionType, int limit, int used, DateTime resetsAt)
        : base($"Monthly limit reached for {actionType} ({used}/{limit}).")
    {
        ActionType = actionType;
        Limit = limit;
        Used = used;
        ResetsAt = resetsAt;
    }
}

/// Thrown when an action requires a plan feature flag the caller doesn't
/// have (e.g. Pro-only Trend Analysis on a Basic org plan). Distinct from
/// UsageLimitExceededException because the user can't "wait it out" —
/// they need to upgrade to access the feature at all.
///
/// Middleware translates to 403 with body:
///   { status, title, code = "AI_FEATURE_NOT_AVAILABLE",
///     featureName, currentPlanName, traceId }
public sealed class PlanFeatureNotAvailableException : Exception
{
    /// Plan column name (e.g. "HasKnowledgeGraph"). Frontend uses this
    /// to look up a localised label for the upsell modal.
    public string FeatureName { get; }

    /// The Arabic name of the user's current plan. Helps the modal
    /// say "your باقة أساسية doesn't include this — upgrade to …".
    public string CurrentPlanName { get; }

    public PlanFeatureNotAvailableException(string featureName, string currentPlanName)
        : base($"Feature '{featureName}' is not available on plan '{currentPlanName}'.")
    {
        FeatureName = featureName;
        CurrentPlanName = currentPlanName;
    }
}
