namespace Taqreerk.Domain.Common;

/// Stable IDs for the four canonical plans (see PLANS.md / PLANS_SEED.sql).
/// Code paths that auto-assign subscriptions reference these directly so we
/// don't depend on a name lookup that could break if names are edited in admin.
public static class PlanIds
{
    /// Individual freemium — auto-assigned on individual registration.
    public static readonly Guid IndividualFree = new("70000000-0000-0000-0000-000000000001");

    /// Individual paid annual tier.
    public static readonly Guid IndividualAnnual = new("70000000-0000-0000-0000-000000000010");

    /// Organization basic — auto-assigned when an org is created.
    public static readonly Guid OrganizationBasic = new("70000000-0000-0000-0000-000000000020");

    /// Organization professional tier.
    public static readonly Guid OrganizationProfessional = new("70000000-0000-0000-0000-000000000030");
}
