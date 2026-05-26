namespace Taqreerk.Domain.Common;

/// Derives UI-facing tier labels from canonical plan ids.
public static class PlanTierHelper
{
    /// <summary>
    /// "free" | "annual" | "org_basic" | "org_pro" | "unknown"
    /// </summary>
    public static string ResolveTier(Guid planId) => planId switch
    {
        _ when planId == PlanIds.IndividualFree => "free",
        _ when planId == PlanIds.IndividualAnnual => "annual",
        _ when planId == PlanIds.OrganizationBasic => "org_basic",
        _ when planId == PlanIds.OrganizationProfessional => "org_pro",
        _ => "unknown",
    };

    /// True when the plan unlocks paid AI / interaction features in the editor.
    public static bool HasPaidEditorAccess(string tier) =>
        tier is "annual" or "org_basic" or "org_pro";
}
