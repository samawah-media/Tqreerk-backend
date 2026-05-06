namespace Taqreerk.Domain.Enums;

/// Lifecycle for a `report_feature_requests` row. Persisted as the
/// enum's string name so additions/reorders are safe.
public enum FeatureRequestStatus
{
    /// Org submitted the request, awaiting admin review.
    Pending = 0,

    /// Admin approved — a `featured_reports` row was auto-created
    /// alongside this transition.
    Approved = 1,

    /// Admin rejected — the org can submit a new request later.
    Rejected = 2,
}
