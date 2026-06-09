using Taqreerk.Domain.Entities;

namespace Taqreerk.Application.Services;

/// Shared plan-capability checks used by controllers and marketing copy.
public static class PlanCapabilities
{
    /// Report-page AI chat and similar interactive AI surfaces.
    /// Free individual plans use <c>AiAccessLevel = "trial"</c> (one message/month).
    public static bool IncludesAiChat(Plan plan)
        => plan.AiAccessLevel is "individual_pro" or "org_basic" or "org_pro" or "trial";

    /// Monthly cap for <see cref="UsageActionType.AiChat"/>.
    /// Paid tiers are unlimited; trial tier gets one message.
    public static int ResolveAiChatLimit(Plan plan)
        => plan.AiAccessLevel switch
        {
            "individual_pro" or "org_basic" or "org_pro" => -1,
            "trial" => 1,
            _ => 0,
        };
}
