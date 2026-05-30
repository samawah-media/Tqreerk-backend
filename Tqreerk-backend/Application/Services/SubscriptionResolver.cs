using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

/// Resolves the active subscription + plan for a user. Prefers a direct
/// user-level subscription; falls back to the user's organization subscription
/// so org members inherit their org's plan limits.
public static class SubscriptionResolver
{
    public static async Task<(Subscription Subscription, Plan Plan)?> TryGetActiveForUserAsync(
        TaqreerkDbContext db, Guid userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var userSub = await db.Subscriptions
            .AsNoTracking()
            .Include(s => s.Plan)
            .Where(s => s.UserId == userId
                        && s.Status == SubscriptionStatus.Active
                        && (s.Plan.AnnualPrice <= 0 || s.EndDate > now))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (userSub?.Plan is not null)
            return (userSub, userSub.Plan);

        var orgId = await db.OrganizationMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => (Guid?)m.OrganizationId)
            .FirstOrDefaultAsync(ct);

        if (orgId is null)
            return null;

        var orgSub = await db.Subscriptions
            .AsNoTracking()
            .Include(s => s.Plan)
            .Where(s => s.OrganizationId == orgId
                        && s.Status == SubscriptionStatus.Active
                        && (s.Plan.AnnualPrice <= 0 || s.EndDate > now))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (orgSub?.Plan is null)
            return null;

        return (orgSub, orgSub.Plan);
    }

    public static async Task<(Subscription Subscription, Plan Plan)> GetActiveForUserAsync(
        TaqreerkDbContext db, Guid userId, CancellationToken ct = default)
    {
        var resolved = await TryGetActiveForUserAsync(db, userId, ct);
        if (resolved is not null)
            return resolved.Value;

        var orgId = await db.OrganizationMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.IsActive)
            .Select(m => (Guid?)m.OrganizationId)
            .FirstOrDefaultAsync(ct);

        if (orgId is not null)
        {
            var awaitingPayment = await db.Subscriptions
                .AsNoTracking()
                .AnyAsync(
                    s => s.OrganizationId == orgId
                      && s.Status == SubscriptionStatus.Inactive
                      && s.PaymentStatus == PaymentStatus.Pending,
                    ct);
            if (awaitingPayment)
            {
                throw new SubscriptionInactiveException(
                    "اشتراك المؤسسة في انتظار الدفع. أكمل الدفع لتفعيل المميزات.");
            }
        }

        throw new InvalidOperationException(
            $"User {userId} has no active subscription. Registration should " +
            "auto-link to the free or org-basic plan.");
    }
}
