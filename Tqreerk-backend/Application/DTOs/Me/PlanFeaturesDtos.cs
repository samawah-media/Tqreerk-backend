namespace Taqreerk.Application.DTOs.Me;

/// Compact projection of the caller's active plan, surfaced through
/// `GET /api/me/plan-features` so the SPA can hide / disable controls
/// before the user clicks. Mirrors the Plan entity but flat and snake-
/// case-friendly on the wire.
///
/// `Limits` carry -1 = unlimited, 0 = blocked, N = monthly cap.
/// `Booleans` are pure capability gates (no counter).
/// `Tier` strings are short enums the frontend maps to localised labels.
/// `Usage` snapshots how much of each metered counter the user has
/// burned this month — lets the UI show "3 / 5 reads left" without a
/// second round-trip to /usage/me.
public sealed record PlanFeaturesDto(
    Guid PlanId,
    string PlanNameAr,
    string PlanNameEn,
    string TargetType,                   // "Individual" | "Organization"

    PlanLimitsDto Limits,
    PlanFlagsDto Flags,
    PlanTiersDto Tiers,
    UsageSnapshotDto Usage);

/// Per-action monthly caps. Each field maps 1:1 to a column on `plans`.
public sealed record PlanLimitsDto(
    int IndividualReads,
    int IndividualSavedReports,
    int IndividualDownloads,
    int OrgUserSeats,
    int ReportsUpload,
    int FeaturedReportsMonthly,

    int AiSummarize,
    int AiKeyFindings,
    int AiTranslate,
    int AiSimilarSuggestions,
    int AiCompare,
    int AiCompareMaxReports);

/// Plan capability flags — feature-flag style, no counter behind them.
/// The Pro-only AI capabilities (TrendAnalysis, KnowledgeGraph, etc.)
/// are gated by these — the UI hides their entry points entirely on
/// non-Pro plans.
public sealed record PlanFlagsDto(
    bool HasNotifications,
    bool HasAdvancedSearch,
    bool HasInteractions,
    bool HasExclusiveContent,

    bool HasTrendAnalysis,
    bool HasIndicatorExtraction,
    bool HasSmartRecommendations,
    bool HasKnowledgeGraph,
    bool HasSmartAlerts,
    bool HasOpportunityDiscovery,
    bool HasSectoralAnalysis);

/// Tier labels — short string enums the SPA maps to copy. We intentionally
/// don't enum-encode them on the wire so adding a new tier value (e.g.
/// a future Enterprise dashboard tier) doesn't break older clients.
public sealed record PlanTiersDto(
    string AiAccessLevel,                // "none" | "individual_pro" | "org_basic" | "org_pro"
    string AdvancedSearchPrecision,      // "standard" | "high"
    string OrgPageTier,                  // "basic" | "professional"
    string SupportTier,                  // "email" | "priority"
    string DashboardTier,                // "standard" | "advanced"
    string NotificationsTier,            // "none" | "sector" | "custom"
    string UpdatesCadence);              // "monthly" | "realtime"

/// Snapshot of the caller's current monthly usage. Each entry mirrors a
/// metered action; absence in the dictionary means the user hasn't
/// consumed anything this month for that action.
public sealed record UsageSnapshotDto(
    DateTime PeriodStart,
    DateTime ResetsAt,
    IReadOnlyDictionary<string, int> ConsumedByAction);
