namespace Taqreerk.Application.DTOs.Admin;

/// Lightweight rollup for the admin topbar. Refreshed by the SPA every
/// 30 seconds so the badges stay roughly current — not authoritative,
/// just enough to draw attention to the queue.
///
/// Add new counters here as features land (alerts, AI failures, etc.);
/// keep the shape flat so the SPA doesn't have to dig.
public record AdminQuickStatsDto(
    /// Reports waiting in the review queue (Status = PendingReview).
    /// Drives the red badge on the "التقارير" sidebar entry.
    int PendingReviews,

    /// Reports currently held by a reviewer (Status = UnderReview).
    /// Surfaces in-flight work the SuperAdmin can keep an eye on.
    int UnderReview,

    /// Organizations created in the last 7 days. Approximates "new sign-ups
    /// you may need to look at" — the real "needs approval" count comes
    /// from OrganizationStatus.PendingReview once Feature 4 ships.
    int NewOrganizationsLast7d
);
