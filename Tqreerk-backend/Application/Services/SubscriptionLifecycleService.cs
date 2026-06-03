using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Taqreerk.Domain.Common;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

/// <summary>
/// Applies subscription state when a paid period ends: individuals revert to the
/// free tier; organization subscriptions are marked expired (payment required).
/// Expiration is exact when EndDate passes (no grace period).
/// </summary>
public static class SubscriptionLifecycleService
{
    public sealed record ExpirationBatchResult(
        int OrganizationsExpired,
        int IndividualsDowngraded);

    public static async Task<ExpirationBatchResult> ProcessAllExpiredSubscriptionsAsync(
        TaqreerkDbContext db,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var candidates = await db.Subscriptions
            .Include(s => s.Plan)
            .Where(s =>
                s.Status == SubscriptionStatus.Active
                && s.PaymentStatus == PaymentStatus.Paid
                && s.Plan.AnnualPrice > 0)
            .ToListAsync(ct);

        var orgs = 0;
        var individuals = 0;

        foreach (var sub in candidates)
        {
            if (sub.Plan is null || !ShouldApplyExpiration(sub, sub.Plan, now))
                continue;

            if (sub.OrganizationId.HasValue)
            {
                if (sub.Status == SubscriptionStatus.Expired)
                    continue;

                ApplyOrganizationExpiration(sub);
                orgs++;
                continue;
            }

            if (!sub.UserId.HasValue)
                continue;

            ApplyIndividualExpiration(sub);
            individuals++;
        }

        if (orgs > 0 || individuals > 0)
            await db.SaveChangesAsync(ct);

        logger?.LogInformation(
            "Subscription expiration batch: {OrgCount} organization(s) expired, {IndividualCount} individual(s) downgraded to free.",
            orgs,
            individuals);

        return new ExpirationBatchResult(orgs, individuals);
    }

    public static async Task ApplyExpirationTransitionsForUserAsync(
        TaqreerkDbContext db,
        Guid userId,
        CancellationToken ct = default)
    {
        var orgId = await db.OrganizationMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.IsActive)
            .Select(m => (Guid?)m.OrganizationId)
            .FirstOrDefaultAsync(ct);

        var changed = false;
        if (orgId.HasValue)
        {
            changed = await TryApplyOrganizationExpirationAsync(db, orgId.Value, ct);
        }
        else
        {
            changed = await TryApplyIndividualExpirationAsync(db, userId, ct);
            changed = await TryEnsureIndividualFreeAccessAsync(db, userId, ct) || changed;
        }

        if (changed)
            await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Ensures an individual (non-org) user has an active free-tier row after expiry,
    /// refund, or legacy Inactive states. Returns true when the row was updated.
    /// </summary>
    public static async Task<bool> TryEnsureIndividualFreeAccessAsync(
        TaqreerkDbContext db,
        Guid userId,
        CancellationToken ct = default)
    {
        var hasActiveOrg = await db.OrganizationMembers
            .AnyAsync(m => m.UserId == userId && m.IsActive, ct);
        if (hasActiveOrg)
            return false;

        var sub = await db.Subscriptions
            .Include(s => s.Plan)
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (sub is null)
            return false;

        var now = DateTime.UtcNow;
        if (sub.Plan is not null
            && sub.Status == SubscriptionStatus.Active
            && sub.PaymentStatus == PaymentStatus.Paid
            && (sub.Plan.AnnualPrice <= 0 || sub.EndDate > now))
        {
            return false;
        }

        ApplyIndividualExpiration(sub);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static async Task<bool> TryApplyIndividualExpirationAsync(
        TaqreerkDbContext db,
        Guid userId,
        CancellationToken ct)
    {
        var sub = await db.Subscriptions
            .Include(s => s.Plan)
            .FirstOrDefaultAsync(s => s.UserId == userId, ct);

        if (sub?.Plan is null || !ShouldApplyExpiration(sub, sub.Plan, DateTime.UtcNow))
            return false;

        ApplyIndividualExpiration(sub);
        return true;
    }

    private static async Task<bool> TryApplyOrganizationExpirationAsync(
        TaqreerkDbContext db,
        Guid organizationId,
        CancellationToken ct)
    {
        var sub = await db.Subscriptions
            .Include(s => s.Plan)
            .Where(s => s.OrganizationId == organizationId)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (sub?.Plan is null
            || !ShouldApplyExpiration(sub, sub.Plan, DateTime.UtcNow)
            || sub.Status == SubscriptionStatus.Expired)
        {
            return false;
        }

        ApplyOrganizationExpiration(sub);
        return true;
    }

    public static void ApplyIndividualExpiration(Subscription sub)
    {
        var addons = SubscriptionAddons.Parse(sub.AddonsJson);
        sub.PlanId = PlanIds.IndividualFree;
        sub.Status = SubscriptionStatus.Active;
        sub.PaymentStatus = PaymentStatus.Paid;
        sub.AddonsJson = SubscriptionAddons.Serialize(
            addons with
            {
                AutoRenew = false,
                MoyasarToken = null,
                PendingPlanId = null,
            });
    }

    private static void ApplyOrganizationExpiration(Subscription sub)
        => sub.Status = SubscriptionStatus.Expired;

    public static bool ShouldApplyExpiration(Subscription sub, Plan plan, DateTime now)
        => plan.AnnualPrice > 0
           && sub.PaymentStatus == PaymentStatus.Paid
           && sub.Status == SubscriptionStatus.Active
           && sub.EndDate <= now;

    public static bool IsExpiredPaidSubscription(Subscription sub, Plan plan)
        => ShouldApplyExpiration(sub, plan, DateTime.UtcNow);

    public static bool RequiresOrganizationRenewal(Subscription sub, Plan plan, bool isActive)
        => sub.OrganizationId.HasValue
           && plan.AnnualPrice > 0
           && sub.PaymentStatus == PaymentStatus.Paid
           && (!isActive
               || sub.Status == SubscriptionStatus.Expired
               || sub.EndDate <= DateTime.UtcNow);

    /// <summary>Org must complete checkout before platform access (signup, post-refund, etc.).</summary>
    public static bool OrganizationAwaitingCheckout(Subscription sub)
        => sub.OrganizationId.HasValue
           && sub.Status != SubscriptionStatus.Active
           && sub.PaymentStatus is PaymentStatus.Pending
               or PaymentStatus.Refunded
               or PaymentStatus.PartiallyRefunded;
}
