using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class AdminPlansService : IAdminPlansService
{
    private readonly TaqreerkDbContext _db;
    private readonly IAdminActionLogger _audit;

    public AdminPlansService(TaqreerkDbContext db, IAdminActionLogger audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<IReadOnlyList<AdminPlanDto>> ListAsync(CancellationToken ct = default)
    {
        // Sort by target type first (so Individual rows group together),
        // then ascending price so Free comes before Annual / Basic comes
        // before Professional. The active counts come from a single
        // grouped query keyed by PlanId — no N+1 even when the catalogue
        // grows.
        var plans = await _db.Plans
            .AsNoTracking()
            .OrderBy(p => p.TargetType)
            .ThenBy(p => p.AnnualPrice)
            .ToListAsync(ct);

        if (plans.Count == 0) return Array.Empty<AdminPlanDto>();

        var planIds = plans.Select(p => p.Id).ToList();
        var activeCounts = await _db.Subscriptions
            .AsNoTracking()
            .Where(s => planIds.Contains(s.PlanId) && s.Status == SubscriptionStatus.Active)
            .GroupBy(s => s.PlanId)
            .Select(g => new { PlanId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.PlanId, g => g.Count, ct);

        return plans
            .Select(p => ToDto(p, activeCounts.GetValueOrDefault(p.Id, 0)))
            .ToList();
    }

    public async Task<AdminPlanDto> GetAsync(Guid planId, CancellationToken ct = default)
    {
        var plan = await _db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId, ct)
            ?? throw new KeyNotFoundException("Plan not found.");

        var activeCount = await _db.Subscriptions
            .AsNoTracking()
            .CountAsync(s => s.PlanId == planId && s.Status == SubscriptionStatus.Active, ct);

        return ToDto(plan, activeCount);
    }

    public async Task<AdminPlanDto> UpdateAsync(
        Guid actingAdminUserId,
        Guid planId,
        UpdateAdminPlanRequest req,
        string? ipAddress,
        CancellationToken ct = default)
    {
        var plan = await _db.Plans.FirstOrDefaultAsync(p => p.Id == planId, ct)
            ?? throw new KeyNotFoundException("Plan not found.");

        // Snapshot the before-state so the audit row carries a clean
        // diff. We mirror the entity rather than the DTO to skip the
        // ToString() / count rollups that don't matter for the audit.
        var beforeState = SnapshotForAudit(plan);

        // Partial update — null on each field means "leave alone". We
        // skip the trim logic on tier strings: the API DTO already caps
        // length, and an empty string from the editor is a deliberate
        // "clear me" signal we honour by writing it through.
        if (req.NameAr is not null) plan.NameAr = req.NameAr.Trim();
        if (req.NameEn is not null) plan.NameEn = req.NameEn.Trim();
        if (req.AnnualPrice.HasValue) plan.AnnualPrice = req.AnnualPrice.Value;
        if (req.MiserPriceId is not null)
            plan.MiserPriceId = string.IsNullOrWhiteSpace(req.MiserPriceId) ? null : req.MiserPriceId.Trim();
        if (req.UserLimit.HasValue) plan.UserLimit = req.UserLimit.Value;
        if (req.IsActive.HasValue) plan.IsActive = req.IsActive.Value;

        if (req.IndividualReadsLimit.HasValue) plan.IndividualReadsLimit = req.IndividualReadsLimit.Value;
        if (req.IndividualSavedReportsLimit.HasValue) plan.IndividualSavedReportsLimit = req.IndividualSavedReportsLimit.Value;
        if (req.IndividualDownloadsLimit.HasValue) plan.IndividualDownloadsLimit = req.IndividualDownloadsLimit.Value;
        if (req.ReportsDownloadLimit.HasValue) plan.ReportsDownloadLimit = req.ReportsDownloadLimit.Value;
        if (req.ReportsUploadLimit.HasValue) plan.ReportsUploadLimit = req.ReportsUploadLimit.Value;
        if (req.FeaturedReportsMonthly.HasValue) plan.FeaturedReportsMonthly = req.FeaturedReportsMonthly.Value;

        if (req.AiSummarizeLimit.HasValue) plan.AiSummarizeLimit = req.AiSummarizeLimit.Value;
        if (req.AiKeyFindingsLimit.HasValue) plan.AiKeyFindingsLimit = req.AiKeyFindingsLimit.Value;
        if (req.AiTranslateLimit.HasValue) plan.AiTranslateLimit = req.AiTranslateLimit.Value;
        if (req.AiSimilarSuggestionsLimit.HasValue) plan.AiSimilarSuggestionsLimit = req.AiSimilarSuggestionsLimit.Value;
        if (req.AiCompareLimit.HasValue) plan.AiCompareLimit = req.AiCompareLimit.Value;
        if (req.AiCompareMaxReports.HasValue) plan.AiCompareMaxReports = req.AiCompareMaxReports.Value;

        if (req.AiAccessLevel is not null) plan.AiAccessLevel = req.AiAccessLevel.Trim();
        if (req.AdvancedSearchPrecision is not null) plan.AdvancedSearchPrecision = req.AdvancedSearchPrecision.Trim();
        if (req.OrgPageTier is not null) plan.OrgPageTier = req.OrgPageTier.Trim();
        if (req.SupportTier is not null) plan.SupportTier = req.SupportTier.Trim();
        if (req.DashboardTier is not null) plan.DashboardTier = req.DashboardTier.Trim();
        if (req.NotificationsTier is not null) plan.NotificationsTier = req.NotificationsTier.Trim();
        if (req.UpdatesCadence is not null) plan.UpdatesCadence = req.UpdatesCadence.Trim();

        if (req.HasNotifications.HasValue) plan.HasNotifications = req.HasNotifications.Value;
        if (req.HasAdvancedSearch.HasValue) plan.HasAdvancedSearch = req.HasAdvancedSearch.Value;
        if (req.HasInteractions.HasValue) plan.HasInteractions = req.HasInteractions.Value;
        if (req.HasExclusiveContent.HasValue) plan.HasExclusiveContent = req.HasExclusiveContent.Value;
        if (req.HasTrendAnalysis.HasValue) plan.HasTrendAnalysis = req.HasTrendAnalysis.Value;
        if (req.HasIndicatorExtraction.HasValue) plan.HasIndicatorExtraction = req.HasIndicatorExtraction.Value;
        if (req.HasSmartRecommendations.HasValue) plan.HasSmartRecommendations = req.HasSmartRecommendations.Value;
        if (req.HasKnowledgeGraph.HasValue) plan.HasKnowledgeGraph = req.HasKnowledgeGraph.Value;
        if (req.HasSmartAlerts.HasValue) plan.HasSmartAlerts = req.HasSmartAlerts.Value;
        if (req.HasOpportunityDiscovery.HasValue) plan.HasOpportunityDiscovery = req.HasOpportunityDiscovery.Value;
        if (req.HasSectoralAnalysis.HasValue) plan.HasSectoralAnalysis = req.HasSectoralAnalysis.Value;

        await _db.SaveChangesAsync(ct);

        // IAdminActionLogger pulls IP / UA off the current HttpContext
        // itself, so the diff is the only thing we hand it. The
        // ipAddress parameter on this method stays so a future
        // non-HTTP caller (background job, CLI tool) can record an
        // attribution without an HttpContext, but for now we just
        // discard it.
        _ = ipAddress;
        await _audit.LogAsync(
            adminUserId: actingAdminUserId,
            actionType: "plan.update",
            targetEntityType: "Plan",
            targetEntityId: plan.Id,
            beforeState: beforeState,
            afterState: SnapshotForAudit(plan),
            ct: ct);

        var activeCount = await _db.Subscriptions
            .AsNoTracking()
            .CountAsync(s => s.PlanId == plan.Id && s.Status == SubscriptionStatus.Active, ct);

        return ToDto(plan, activeCount);
    }

    /// Compact projection used as the audit row's beforeState/afterState.
    /// Mirrors every editable column so the diff stays meaningful even
    /// for boolean-only changes (the audit log otherwise can't tell a
    /// pricing tweak apart from a feature flag flip).
    private static object SnapshotForAudit(Domain.Entities.Plan plan) => new
    {
        plan.NameAr,
        plan.NameEn,
        plan.AnnualPrice,
        plan.MiserPriceId,
        plan.UserLimit,
        plan.IsActive,
        plan.IndividualReadsLimit,
        plan.IndividualSavedReportsLimit,
        plan.IndividualDownloadsLimit,
        plan.ReportsDownloadLimit,
        plan.ReportsUploadLimit,
        plan.FeaturedReportsMonthly,
        plan.AiSummarizeLimit,
        plan.AiKeyFindingsLimit,
        plan.AiTranslateLimit,
        plan.AiSimilarSuggestionsLimit,
        plan.AiCompareLimit,
        plan.AiCompareMaxReports,
        plan.AiAccessLevel,
        plan.AdvancedSearchPrecision,
        plan.OrgPageTier,
        plan.SupportTier,
        plan.DashboardTier,
        plan.NotificationsTier,
        plan.UpdatesCadence,
        plan.HasNotifications,
        plan.HasAdvancedSearch,
        plan.HasInteractions,
        plan.HasExclusiveContent,
        plan.HasTrendAnalysis,
        plan.HasIndicatorExtraction,
        plan.HasSmartRecommendations,
        plan.HasKnowledgeGraph,
        plan.HasSmartAlerts,
        plan.HasOpportunityDiscovery,
        plan.HasSectoralAnalysis,
    };

    private static AdminPlanDto ToDto(Domain.Entities.Plan p, int activeSubscriptionsCount) => new(
        p.Id,
        p.NameAr,
        p.NameEn,
        p.TargetType.ToString(),
        p.AnnualPrice,
        p.MiserPriceId,
        p.UserLimit,
        p.IsActive,
        p.IndividualReadsLimit,
        p.IndividualSavedReportsLimit,
        p.IndividualDownloadsLimit,
        p.ReportsDownloadLimit,
        p.ReportsUploadLimit,
        p.FeaturedReportsMonthly,
        p.AiSummarizeLimit,
        p.AiKeyFindingsLimit,
        p.AiTranslateLimit,
        p.AiSimilarSuggestionsLimit,
        p.AiCompareLimit,
        p.AiCompareMaxReports,
        p.AiAccessLevel,
        p.AdvancedSearchPrecision,
        p.OrgPageTier,
        p.SupportTier,
        p.DashboardTier,
        p.NotificationsTier,
        p.UpdatesCadence,
        p.HasNotifications,
        p.HasAdvancedSearch,
        p.HasInteractions,
        p.HasExclusiveContent,
        p.HasTrendAnalysis,
        p.HasIndicatorExtraction,
        p.HasSmartRecommendations,
        p.HasKnowledgeGraph,
        p.HasSmartAlerts,
        p.HasOpportunityDiscovery,
        p.HasSectoralAnalysis,
        activeSubscriptionsCount,
        p.CreatedAt);
}
