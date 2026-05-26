using Taqreerk.Domain.Common;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Domain.Entities;

/// Subscription plan blueprint. Two ladders + a free tier:
///   Individual / Free          → 5 reads, no AI, no save
///   Individual / Annual        → unlimited reads + AI counters + save
///   Organization / Basic       → 3 seats, basic AI bundle, 2 promo slots
///   Organization / Professional → 10 seats, full AI bundle, 5 promo slots
///
/// Limits convention:
///   -1 = unlimited
///    0 = blocked
///   N  = monthly cap (per user for individuals, per org for orgs)
///
/// See PLANS.md (frontend repo) for the canonical mapping between the
/// PDF spec and these columns.
public class Plan : BaseEntity
{
    public string NameAr { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public PlanTargetType TargetType { get; set; }
    public decimal AnnualPrice { get; set; }
    public string? MiserPriceId { get; set; }
    public int UserLimit { get; set; }

    // ── Reads / downloads / save ──────────────────────────────────────────
    /// Per-month full-access reads for individuals. Org members read
    /// freely off their org seat — this column is N/A for org plans
    /// (set to -1 for clarity, but UsageService never consults it for
    /// org members).
    public int IndividualReadsLimit { get; set; }

    /// Per-month "save report" cap for individuals. Free = 0 disables
    /// the save action entirely.
    public int IndividualSavedReportsLimit { get; set; }

    /// Per-month download cap. The PDF specifies "10% of database" for
    /// every paid tier — implementation reads this column raw and
    /// `UsageService` does the percentage math when -1.
    public int ReportsDownloadLimit { get; set; }

    /// Per-month download cap for individuals. Same percentage rule
    /// (-1 means "10% cap" computed at gate time).
    public int IndividualDownloadsLimit { get; set; }

    // ── AI counters — one per metered AI action ───────────────────────────
    /// "Quick summary" / Key Insights extraction.
    public int AiSummarizeLimit { get; set; }

    /// Topic + keyword extraction.
    public int AiKeyFindingsLimit { get; set; }

    /// Translation (ar↔en).
    public int AiTranslateLimit { get; set; }

    /// "Reports you might also like" — content-based recommendations.
    public int AiSimilarSuggestionsLimit { get; set; }

    /// Pairwise + multi-report comparisons.
    public int AiCompareLimit { get; set; }

    /// Hard cap on how many reports a single comparison can include.
    /// Free = 0 blocks; Annual / Basic = 2; Professional = 5.
    public int AiCompareMaxReports { get; set; }

    // ── Org-only counters ────────────────────────────────────────────────
    /// Yearly cap on how many reports the org can upload. -1 unlimited.
    public int ReportsUploadLimit { get; set; }

    /// Per-month featured-report slots (homepage carousel + sector top
    /// + country top combined). Counted on `featured_reports` rows
    /// created during the period.
    public int FeaturedReportsMonthly { get; set; }

    // ── Tier labels — string enums ────────────────────────────────────────
    /// AI feature bundle: "none" / "individual_pro" / "org_basic" / "org_pro".
    public string AiAccessLevel { get; set; } = "none";

    /// Search precision: "standard" / "high".
    public string AdvancedSearchPrecision { get; set; } = "standard";

    /// Org page surface: "basic" / "professional".
    public string OrgPageTier { get; set; } = "basic";

    /// Support channel: "email" / "priority".
    public string SupportTier { get; set; } = "email";

    /// Dashboard tier: "standard" / "advanced".
    public string DashboardTier { get; set; } = "standard";

    /// Notifications tier: "none" / "sector" / "custom".
    public string NotificationsTier { get; set; } = "none";

    /// How fresh the report feed is for the user: "monthly" / "realtime".
    public string UpdatesCadence { get; set; } = "monthly";

    // ── Boolean feature flags ────────────────────────────────────────────
    /// Lets the user receive any notifications at all (free tier off).
    public bool HasNotifications { get; set; }

    /// Advanced search filters (date ranges, multi-sector, etc.) —
    /// distinct from the precision tier above which controls ranking.
    public bool HasAdvancedSearch { get; set; }

    /// Save / rate / recommend interactions on reports.
    public bool HasInteractions { get; set; }

    /// Org-only — access to admin-flagged exclusive reports.
    public bool HasExclusiveContent { get; set; }

    // ── Pro-only AI features ─────────────────────────────────────────────
    // These are unmetered booleans (decision 2026-05-07) — once a plan
    // turns the flag on, the feature is unlimited within the plan.
    // Counter-style limits would punish the very users we want using
    // these capabilities.
    public bool HasTrendAnalysis { get; set; }
    public bool HasIndicatorExtraction { get; set; }
    public bool HasSmartRecommendations { get; set; }
    public bool HasKnowledgeGraph { get; set; }
    public bool HasSmartAlerts { get; set; }
    public bool HasOpportunityDiscovery { get; set; }
    public bool HasSectoralAnalysis { get; set; }

    public bool IsActive { get; set; } = true;

    public ICollection<Subscription> Subscriptions { get; set; } = [];
}
