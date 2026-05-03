namespace Taqreerk.Domain.Common;

/// Stable IDs for seeded plans. Code paths that assume "the free plan
/// exists" (e.g. RegisterIndividualAsync, the backfill migration) reference
/// these directly so we don't depend on a name lookup that could break if
/// names get edited from the admin UI.
public static class PlanIds
{
    /// Individual freemium tier — auto-assigned on registration.
    public static readonly Guid IndividualFree     = new("70000000-0000-0000-0000-000000000001");
    public static readonly Guid IndividualBasic    = new("70000000-0000-0000-0000-000000000002");
    public static readonly Guid IndividualPremium  = new("70000000-0000-0000-0000-000000000003");
}
