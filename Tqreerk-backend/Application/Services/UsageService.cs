using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Usage;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

/// Implements the per-user freemium gate (see IUsageService).
///
/// Concurrency: the count-then-insert path is wrapped in a DB transaction
/// AND a Postgres advisory lock keyed by (userId, actionType). Without
/// that lock, a user opening the same report in two tabs could pass two
/// "remaining = 1" checks before either insert lands. The advisory lock
/// is transaction-scoped (pg_advisory_xact_lock) so it's released when
/// the transaction commits or rolls back — no cleanup needed.
public class UsageService : IUsageService
{
    private const decimal DownloadPercentageOfDb = 0.10m;

    private readonly TaqreerkDbContext _db;
    private readonly ILogger<UsageService> _logger;

    public UsageService(TaqreerkDbContext db, ILogger<UsageService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task EnsureWithinLimitAndConsumeAsync(
        Guid userId,
        UsageActionType actionType,
        Guid? resourceId,
        Func<CancellationToken, Task> actionFn,
        CancellationToken ct = default,
        string? metadata = null,
        bool idempotentPerResource = false)
        => EnsureWithinLimitAndConsumeAsync<object?>(
            userId, actionType, resourceId,
            async ctInner => { await actionFn(ctInner); return null; },
            ct, metadata, idempotentPerResource);

    public async Task<TResult> EnsureWithinLimitAndConsumeAsync<TResult>(
        Guid userId,
        UsageActionType actionType,
        Guid? resourceId,
        Func<CancellationToken, Task<TResult>> actionFn,
        CancellationToken ct = default,
        string? metadata = null,
        bool idempotentPerResource = false)
    {
        var (subscription, plan) = await GetActiveSubscriptionAsync(userId, ct);
        var period = CurrentBillingPeriodStart();

        if (idempotentPerResource && resourceId.HasValue)
        {
            var alreadyConsumed = await _db.UsageTracking
                .AsNoTracking()
                .AnyAsync(
                    u => u.UserId == userId
                      && u.ActionType == actionType
                      && u.BillingPeriodStart == period
                      && u.ResourceId == resourceId,
                    ct);
            if (alreadyConsumed)
                return await actionFn(ct);
        }

        var limit = await ResolveLimitAsync(plan, actionType, ct);

        if (limit < 0)
        {
            return await ExecuteAndRecordAsync(
                userId, subscription, actionType, resourceId, actionFn, ct, metadata);
        }

        if (limit == 0)
        {
            throw new UsageLimitExceededException(
                actionType, limit: 0, used: 0, ResetsAt());
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var (lockA, lockB) = AdvisoryLockKey(userId, actionType);
        await _db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0}, {1})",
            new object[] { lockA, lockB }, ct);

        var used = await _db.UsageTracking
            .AsNoTracking()
            .CountAsync(
                u => u.UserId == userId
                  && u.ActionType == actionType
                  && u.BillingPeriodStart == period,
                ct);

        if (used >= limit)
        {
            await tx.RollbackAsync(ct);
            _logger.LogInformation(
                "[usage] user={UserId} hit cap for {Action} ({Used}/{Limit})",
                userId, actionType, used, limit);
            throw new UsageLimitExceededException(actionType, limit, used, ResetsAt());
        }

        TResult result;
        try
        {
            result = await actionFn(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }

        _db.UsageTracking.Add(new UsageTracking
        {
            UserId = userId,
            OrganizationId = subscription.OrganizationId,
            SubscriptionId = subscription.Id,
            ActionType = actionType,
            ResourceId = resourceId,
            Metadata = metadata,
            ConsumedAt = DateTime.UtcNow,
            BillingPeriodStart = period,
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return result;
    }

    public async Task RecordUsageAsync(
        Guid userId,
        UsageActionType actionType,
        Guid? resourceId,
        string? metadata = null,
        CancellationToken ct = default)
    {
        try
        {
            var (subscription, _) = await GetActiveSubscriptionAsync(userId, ct);
            _db.UsageTracking.Add(new UsageTracking
            {
                UserId = userId,
                OrganizationId = subscription.OrganizationId,
                SubscriptionId = subscription.Id,
                ActionType = actionType,
                ResourceId = resourceId,
                Metadata = metadata,
                ConsumedAt = DateTime.UtcNow,
                BillingPeriodStart = CurrentBillingPeriodStart(),
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[usage] failed to record {Action} for user={UserId}",
                actionType, userId);
        }
    }

    public async Task<UsageSummaryDto> GetMyUsageAsync(Guid userId, CancellationToken ct = default)
    {
        var (subscription, plan) = await GetActiveSubscriptionAsync(userId, ct);
        var period = CurrentBillingPeriodStart();
        var resetsAt = ResetsAt();

        var grouped = await _db.UsageTracking
            .AsNoTracking()
            .Where(u => u.UserId == userId && u.BillingPeriodStart == period)
            .GroupBy(u => u.ActionType)
            .Select(g => new { Action = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Action, x => x.Count, ct);

        var counters = new List<UsageCounterDto>();
        foreach (UsageActionType action in Enum.GetValues<UsageActionType>())
        {
            var limit = await ResolveLimitAsync(plan, action, ct);
            var used = grouped.GetValueOrDefault(action, 0);
            var unlimited = limit < 0;
            var remaining = unlimited ? int.MaxValue : Math.Max(0, limit - used);
            counters.Add(new UsageCounterDto(
                ActionType: action,
                Limit: limit,
                Used: used,
                Remaining: remaining,
                IsUnlimited: unlimited,
                IsExceeded: !unlimited && used >= limit));
        }

        return new UsageSummaryDto(
            SubscriptionId: subscription.Id,
            PlanId: plan.Id,
            PlanNameAr: plan.NameAr,
            PlanNameEn: plan.NameEn,
            BillingPeriodStart: period,
            ResetsAt: resetsAt,
            Counters: counters);
    }

    public async Task<UsageHistoryPageDto> GetMyHistoryAsync(
        Guid userId, int page, int pageSize, CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var query = _db.UsageTracking
            .AsNoTracking()
            .Where(u => u.UserId == userId);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(u => u.ConsumedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new UsageHistoryItemDto(
                u.Id, u.ActionType, u.ResourceId, u.ConsumedAt))
            .ToListAsync(ct);

        return new UsageHistoryPageDto(items, page, pageSize, total);
    }

    public async Task EnsureOrgCanAddMemberAsync(Guid organizationId, CancellationToken ct = default)
    {
        var plan = await GetActiveOrgPlanAsync(organizationId, ct);
        if (plan.UserLimit < 0) return;

        var activeMembers = await _db.OrganizationMembers
            .CountAsync(m => m.OrganizationId == organizationId && m.IsActive, ct);
        var pendingInvites = await _db.OrganizationInvitations
            .CountAsync(
                i => i.OrganizationId == organizationId
                  && i.Status == InvitationStatus.Pending,
                ct);

        if (activeMembers + pendingInvites >= plan.UserLimit)
        {
            throw new InvalidOperationException(
                $"وصلت مؤسستك إلى الحد الأقصى للمقاعد ({plan.UserLimit}). قم بترقية الباقة لإضافة أعضاء جدد.");
        }
    }

    public async Task EnsureOrgCanAcceptMemberAsync(Guid organizationId, CancellationToken ct = default)
    {
        var plan = await GetActiveOrgPlanAsync(organizationId, ct);
        if (plan.UserLimit < 0) return;

        var activeMembers = await _db.OrganizationMembers
            .CountAsync(m => m.OrganizationId == organizationId && m.IsActive, ct);

        if (activeMembers >= plan.UserLimit)
        {
            throw new InvalidOperationException(
                $"وصلت مؤسستك إلى الحد الأقصى للمقاعد ({plan.UserLimit}).");
        }
    }

    public async Task EnsureOrgCanUploadReportAsync(Guid organizationId, CancellationToken ct = default)
    {
        var plan = await GetActiveOrgPlanAsync(organizationId, ct);
        if (plan.ReportsUploadLimit < 0) return;

        var yearStart = new DateTime(DateTime.UtcNow.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var uploadedThisYear = await _db.Reports
            .CountAsync(
                r => r.OrganizationId == organizationId && r.CreatedAt >= yearStart,
                ct);

        if (uploadedThisYear >= plan.ReportsUploadLimit)
        {
            throw new InvalidOperationException(
                $"وصلت مؤسستك إلى حد رفع التقارير السنوي ({plan.ReportsUploadLimit}). قم بترقية الباقة لرفع المزيد.");
        }
    }

    public async Task EnsureOrgCanFeatureReportAsync(Guid organizationId, CancellationToken ct = default)
    {
        var plan = await GetActiveOrgPlanAsync(organizationId, ct);
        if (plan.FeaturedReportsMonthly < 0) return;

        var period = CurrentBillingPeriodStart();
        var periodStartUtc = period.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var periodEndUtc = periodStartUtc.AddMonths(1);

        var featuredThisMonth = await (
            from f in _db.FeaturedReports
            join r in _db.Reports on f.ReportId equals r.Id
            where f.CreatedAt >= periodStartUtc
               && f.CreatedAt < periodEndUtc
               && r.OrganizationId == organizationId
            select f.Id
        ).CountAsync(ct);

        if (featuredThisMonth >= plan.FeaturedReportsMonthly)
        {
            throw new InvalidOperationException(
                $"وصلت مؤسستك إلى حد التمييز الشهري ({plan.FeaturedReportsMonthly}). قم بترقية الباقة أو انتظر الشهر القادم.");
        }
    }

    // ── helpers ────────────────────────────────────────────────────────

    private async Task<TResult> ExecuteAndRecordAsync<TResult>(
        Guid userId, Subscription subscription,
        UsageActionType actionType, Guid? resourceId,
        Func<CancellationToken, Task<TResult>> actionFn,
        CancellationToken ct,
        string? metadata = null)
    {
        var result = await actionFn(ct);
        _db.UsageTracking.Add(new UsageTracking
        {
            UserId = userId,
            OrganizationId = subscription.OrganizationId,
            SubscriptionId = subscription.Id,
            ActionType = actionType,
            ResourceId = resourceId,
            Metadata = metadata,
            ConsumedAt = DateTime.UtcNow,
            BillingPeriodStart = CurrentBillingPeriodStart(),
        });
        await _db.SaveChangesAsync(ct);
        return result;
    }

    private Task<(Subscription, Plan)> GetActiveSubscriptionAsync(
        Guid userId, CancellationToken ct)
        => SubscriptionResolver.GetActiveForUserAsync(_db, userId, ct);

    private async Task<Plan> GetActiveOrgPlanAsync(Guid organizationId, CancellationToken ct)
    {
        var sub = await _db.Subscriptions
            .AsNoTracking()
            .Include(s => s.Plan)
            .Where(s => s.OrganizationId == organizationId && s.Status == SubscriptionStatus.Active)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (sub?.Plan is null)
        {
            throw new InvalidOperationException(
                "لا يوجد اشتراك نشط لهذه المؤسسة.");
        }

        return sub.Plan;
    }

    /// Maps an action to the plan column cap. -1 = unlimited, 0 = blocked.
    /// Download caps of -1 mean floor(10% * published_reports).
    /// Org members never consume IndividualReadsLimit (always unlimited).
    private async Task<int> ResolveLimitAsync(
        Plan plan, UsageActionType action, CancellationToken ct)
    {
        if (plan.TargetType == PlanTargetType.Organization
            && action == UsageActionType.ReportFullAccess)
        {
            return -1;
        }

        if (action == UsageActionType.ReportDownload)
        {
            var raw = plan.TargetType == PlanTargetType.Organization
                ? plan.ReportsDownloadLimit
                : plan.IndividualDownloadsLimit;
            return await ResolveDownloadLimitAsync(raw, ct);
        }

        return action switch
        {
            UsageActionType.ReportFullAccess => plan.IndividualReadsLimit,
            UsageActionType.SaveReport => plan.IndividualSavedReportsLimit,
            UsageActionType.AiSummarize => plan.AiSummarizeLimit,
            UsageActionType.AiKeyFindings => plan.AiKeyFindingsLimit,
            UsageActionType.AiTranslate => plan.AiTranslateLimit,
            UsageActionType.AiSimilarSuggestions => plan.AiSimilarSuggestionsLimit,
            UsageActionType.AiCompare => plan.AiCompareLimit,
            UsageActionType.AiChat => PlanCapabilities.ResolveAiChatLimit(plan),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
    }

    private async Task<int> ResolveDownloadLimitAsync(int rawLimit, CancellationToken ct)
    {
        if (rawLimit == 0) return 0;
        if (rawLimit != -1) return rawLimit;

        var published = await CountPublishedReportsAsync(ct);
        return (int)Math.Floor(published * DownloadPercentageOfDb);
    }

    private Task<int> CountPublishedReportsAsync(CancellationToken ct)
        => _db.Reports.CountAsync(r => r.Status == ReportStatus.Published, ct);

    private static DateOnly CurrentBillingPeriodStart()
    {
        var now = DateTime.UtcNow;
        return new DateOnly(now.Year, now.Month, 1);
    }

    private static DateTime ResetsAt()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
    }

    private static (int, int) AdvisoryLockKey(Guid userId, UsageActionType action)
    {
        var slot1 = userId.GetHashCode();
        var slot2 = unchecked((int)0xA0000000) | (int)action;
        return (slot1, slot2);
    }
}
