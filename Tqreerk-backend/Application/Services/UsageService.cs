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
        CancellationToken ct = default)
        => EnsureWithinLimitAndConsumeAsync<object?>(
            userId, actionType, resourceId,
            async ctInner => { await actionFn(ctInner); return null; },
            ct);

    public async Task<TResult> EnsureWithinLimitAndConsumeAsync<TResult>(
        Guid userId,
        UsageActionType actionType,
        Guid? resourceId,
        Func<CancellationToken, Task<TResult>> actionFn,
        CancellationToken ct = default)
    {
        var (subscription, plan) = await GetActiveSubscriptionAsync(userId, ct);
        var (limit, _) = ResolveLimit(plan, actionType);

        // Unlimited path: skip the lock + count, just record and run.
        if (limit < 0)
        {
            return await ExecuteAndRecordAsync(
                userId, subscription, actionType, resourceId, actionFn, ct);
        }

        // Hard 0: plan disallows the action entirely (e.g. free-tier AI).
        // Throw before opening a transaction.
        if (limit == 0)
        {
            throw new UsageLimitExceededException(
                actionType, limit: 0, used: 0, ResetsAt());
        }

        var period = CurrentBillingPeriodStart();

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Advisory lock keyed on (userId, actionType). Two int4 args is
        // the cheapest variant; we hash userId + actionType into them.
        var (lockA, lockB) = AdvisoryLockKey(userId, actionType);
        await _db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0}, {1})",
            new object[] { lockA, lockB }, ct);

        // Count under the lock, AsNoTracking so EF doesn't try to attach
        // existing rows we won't modify.
        var used = await _db.UsageTracking
            .AsNoTracking()
            .CountAsync(
                u => u.UserId == userId
                  && u.ActionType == actionType
                  && u.BillingPeriodStart == period,
                ct);

        if (used >= limit)
        {
            // Release the lock by rolling back. (Commit would also work
            // but rollback is the obvious "we did nothing" signal.)
            await tx.RollbackAsync(ct);
            _logger.LogInformation(
                "[usage] user={UserId} hit cap for {Action} ({Used}/{Limit})",
                userId, actionType, used, limit);
            throw new UsageLimitExceededException(actionType, limit, used, ResetsAt());
        }

        // Run the actual action first. If it throws, the transaction
        // rolls back and we don't burn a count.
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
            ConsumedAt = DateTime.UtcNow,
            BillingPeriodStart = period,
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return result;
    }

    public async Task<UsageSummaryDto> GetMyUsageAsync(Guid userId, CancellationToken ct = default)
    {
        var (subscription, plan) = await GetActiveSubscriptionAsync(userId, ct);
        var period = CurrentBillingPeriodStart();
        var resetsAt = ResetsAt();

        // One round-trip — group by action_type, count, then merge with
        // the static set of action types the plan exposes.
        var grouped = await _db.UsageTracking
            .AsNoTracking()
            .Where(u => u.UserId == userId && u.BillingPeriodStart == period)
            .GroupBy(u => u.ActionType)
            .Select(g => new { Action = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Action, x => x.Count, ct);

        var counters = new List<UsageCounterDto>();
        foreach (UsageActionType action in Enum.GetValues<UsageActionType>())
        {
            var (limit, _) = ResolveLimit(plan, action);
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

    // ── helpers ────────────────────────────────────────────────────────

    private async Task<TResult> ExecuteAndRecordAsync<TResult>(
        Guid userId, Subscription subscription,
        UsageActionType actionType, Guid? resourceId,
        Func<CancellationToken, Task<TResult>> actionFn,
        CancellationToken ct)
    {
        // Unlimited tier still records — useful for analytics + the
        // history endpoint. We don't open a transaction since there's no
        // race condition to guard against when there's no cap.
        var result = await actionFn(ct);
        _db.UsageTracking.Add(new UsageTracking
        {
            UserId = userId,
            OrganizationId = subscription.OrganizationId,
            SubscriptionId = subscription.Id,
            ActionType = actionType,
            ResourceId = resourceId,
            ConsumedAt = DateTime.UtcNow,
            BillingPeriodStart = CurrentBillingPeriodStart(),
        });
        await _db.SaveChangesAsync(ct);
        return result;
    }

    /// Loads the user's currently-active subscription with its plan. If
    /// none exists (which shouldn't happen — registration auto-creates a
    /// free subscription, and a backfill migration covers existing rows),
    /// throw a 500 rather than silently let the action through. Free tier
    /// limits are still limits.
    private async Task<(Subscription, Plan)> GetActiveSubscriptionAsync(
        Guid userId, CancellationToken ct)
    {
        var sub = await _db.Subscriptions
            .Include(s => s.Plan)
            .Where(s => s.UserId == userId
                     && s.Status == SubscriptionStatus.Active)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (sub is null)
        {
            throw new InvalidOperationException(
                $"User {userId} has no active subscription. Registration " +
                "should auto-link to the free plan; check the backfill ran.");
        }

        return (sub, sub.Plan);
    }

    /// Maps an action to the column on `plans` that holds its monthly
    /// cap. Returns the cap (-1 = unlimited, 0 = blocked) and a debug
    /// label.
    private static (int limit, string label) ResolveLimit(Plan plan, UsageActionType action) => action switch
    {
        // Reads + downloads share the per-month cap from the plan file.
        UsageActionType.ReportFullAccess => (plan.IndividualReadsLimit, "reads"),
        UsageActionType.ReportDownload   => (plan.IndividualReadsLimit, "downloads"),
        // Both AI actions count against the same `ai_calls_limit`.
        UsageActionType.AiTranslate => (plan.AiCallsLimit, "ai_translate"),
        UsageActionType.AiCompare   => (plan.AiCallsLimit, "ai_compare"),
        UsageActionType.SaveReport  => (plan.IndividualSavedReportsLimit, "saved"),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    private static DateOnly CurrentBillingPeriodStart()
    {
        var now = DateTime.UtcNow;
        return new DateOnly(now.Year, now.Month, 1);
    }

    /// First instant of the next month (UTC) — when free counters reset.
    private static DateTime ResetsAt()
    {
        var now = DateTime.UtcNow;
        var firstOfNextMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
        return firstOfNextMonth;
    }

    /// Pack (userId, actionType) into two int4 keys for
    /// pg_advisory_xact_lock(int4, int4). The first slot is a stable hash
    /// of the userId; the second carries the actionType ordinal so two
    /// different actions for the same user don't serialize.
    private static (int, int) AdvisoryLockKey(Guid userId, UsageActionType action)
    {
        // GetHashCode on Guid is well-defined and stable within a runtime;
        // we don't need cross-process determinism here — only that the
        // same (userId, action) maps to the same key inside a single DB
        // session window. Belt-and-braces: tag the high bit so we can
        // tell our locks apart from anyone else's advisory locks.
        var slot1 = userId.GetHashCode();
        var slot2 = unchecked((int)0xA0000000) | (int)action;
        return (slot1, slot2);
    }
}
