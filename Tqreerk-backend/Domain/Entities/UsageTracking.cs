using Taqreerk.Domain.Common;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Domain.Entities;

/// One row per metered action a user performs (full-access read, download,
/// AI translate/compare, save). The freemium gate counts rows in the
/// current billing month — `BillingPeriodStart` is denormalized to the
/// first day of the action's month so the count query is a single
/// indexed equality lookup instead of a date-range scan.
///
/// Inserts go through IUsageService and are protected by an advisory lock
/// to keep two parallel requests from each squeezing past the limit.
public class UsageTracking : BaseEntity
{
    public Guid UserId { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid SubscriptionId { get; set; }

    public UsageActionType ActionType { get; set; }

    /// The report (or other resource) the action was taken on. Nullable
    /// because some actions (e.g. AiCompare on a fresh selection) aren't
    /// tied to a single resource.
    public Guid? ResourceId { get; set; }

    /// Free-form context (e.g. compare {reportIds: [...]}).
    public string? Metadata { get; set; }

    public DateTime ConsumedAt { get; set; } = DateTime.UtcNow;

    /// First day (UTC) of the billing month this row counts against.
    /// Indexed alongside (UserId, ActionType) to make the cap-check query
    /// a single seek.
    public DateOnly BillingPeriodStart { get; set; }

    public User User { get; set; } = null!;
    public Organization? Organization { get; set; }
    public Subscription Subscription { get; set; } = null!;
}
