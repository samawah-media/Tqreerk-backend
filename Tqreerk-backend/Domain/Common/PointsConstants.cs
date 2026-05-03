using Taqreerk.Domain.Enums;

namespace Taqreerk.Domain.Common;

/// Cosmetic-but-real point currency. Every individual gets `WelcomeBalance`
/// on registration; metered actions debit the per-action cost in
/// `Costs`. Points are decoupled from the freemium gates (Feature 5) — a
/// user can run out of points while still under their plan limits, and
/// vice versa. We may merge the two later; until then keep them
/// independent so neither blocks the other.
public static class PointsConstants
{
    /// Initial credit on signup + the value the migration backfills onto
    /// existing individual users.
    public const int WelcomeBalance = 3000;

    public static readonly IReadOnlyDictionary<UsageActionType, int> Costs =
        new Dictionary<UsageActionType, int>
        {
            [UsageActionType.ReportFullAccess] = 1000,
            [UsageActionType.ReportDownload]   = 500,
            [UsageActionType.AiTranslate]      = 500,
            [UsageActionType.AiCompare]        = 800,
            [UsageActionType.SaveReport]       = 100,
        };
}
