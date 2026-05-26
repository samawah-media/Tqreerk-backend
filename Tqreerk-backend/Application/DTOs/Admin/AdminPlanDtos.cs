using System.ComponentModel.DataAnnotations;

namespace Taqreerk.Application.DTOs.Admin;

/// Full plan projection for the admin curation table — every column on
/// `plans` so the admin can see + edit limits, booleans, and tier
/// strings in one place. Mirrors the Plan entity exactly; the editor
/// PATCH below uses the same shape with all fields optional so any
/// subset can be pushed.
public sealed record AdminPlanDto(
    Guid Id,
    string NameAr,
    string NameEn,
    string TargetType,                  // "Individual" | "Organization"
    decimal AnnualPrice,
    string? MiserPriceId,
    int UserLimit,
    bool IsActive,

    // Reads / saves / downloads
    int IndividualReadsLimit,
    int IndividualSavedReportsLimit,
    int IndividualDownloadsLimit,
    int ReportsDownloadLimit,

    // Org-only counters
    int ReportsUploadLimit,
    int FeaturedReportsMonthly,

    // AI counters
    int AiSummarizeLimit,
    int AiKeyFindingsLimit,
    int AiTranslateLimit,
    int AiSimilarSuggestionsLimit,
    int AiCompareLimit,
    int AiCompareMaxReports,

    // Tier labels
    string AiAccessLevel,
    string AdvancedSearchPrecision,
    string OrgPageTier,
    string SupportTier,
    string DashboardTier,
    string NotificationsTier,
    string UpdatesCadence,

    // Boolean feature flags
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
    bool HasSectoralAnalysis,

    // Live rollups for the table — not in the entity, computed at query
    // time so the admin sees "12 active subscriptions" without a second
    // round-trip per row.
    int ActiveSubscriptionsCount,
    DateTime CreatedAt);

/// PATCH body for `/api/admin/plans/{id}`. Every field is optional —
/// null means "leave alone". Field types match `AdminPlanDto` exactly
/// so a round-trip (read, edit one field, write) doesn't have to
/// rebuild the request shape.
public sealed record UpdateAdminPlanRequest(
    [MaxLength(200)] string? NameAr,
    [MaxLength(200)] string? NameEn,
    decimal? AnnualPrice,
    [MaxLength(100)] string? MiserPriceId,
    int? UserLimit,
    bool? IsActive,

    int? IndividualReadsLimit,
    int? IndividualSavedReportsLimit,
    int? IndividualDownloadsLimit,
    int? ReportsDownloadLimit,
    int? ReportsUploadLimit,
    int? FeaturedReportsMonthly,

    int? AiSummarizeLimit,
    int? AiKeyFindingsLimit,
    int? AiTranslateLimit,
    int? AiSimilarSuggestionsLimit,
    int? AiCompareLimit,
    int? AiCompareMaxReports,

    [MaxLength(50)] string? AiAccessLevel,
    [MaxLength(20)] string? AdvancedSearchPrecision,
    [MaxLength(20)] string? OrgPageTier,
    [MaxLength(20)] string? SupportTier,
    [MaxLength(20)] string? DashboardTier,
    [MaxLength(20)] string? NotificationsTier,
    [MaxLength(20)] string? UpdatesCadence,

    bool? HasNotifications,
    bool? HasAdvancedSearch,
    bool? HasInteractions,
    bool? HasExclusiveContent,
    bool? HasTrendAnalysis,
    bool? HasIndicatorExtraction,
    bool? HasSmartRecommendations,
    bool? HasKnowledgeGraph,
    bool? HasSmartAlerts,
    bool? HasOpportunityDiscovery,
    bool? HasSectoralAnalysis);
