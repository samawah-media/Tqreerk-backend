using Taqreerk.Domain.Entities;

namespace Taqreerk.Application.Services;

/// Shared plan-capability checks used by controllers and marketing copy.
public static class PlanCapabilities
{
    /// Report-page AI chat and similar interactive AI surfaces.
    /// Free individual plans use <c>AiAccessLevel = "none"</c>.
    public static bool IncludesAiChat(Plan plan)
        => plan.AiAccessLevel is "individual_pro" or "org_basic" or "org_pro";
}
