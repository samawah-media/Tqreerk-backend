using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Common;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class SubscriptionProvisioningService : ISubscriptionProvisioningService
{
    private readonly TaqreerkDbContext _db;

    public SubscriptionProvisioningService(TaqreerkDbContext db)
    {
        _db = db;
    }

    public Task EnsureIndividualFreeAsync(Guid userId, CancellationToken ct = default)
        => EnsureSubscriptionAsync(
            userId: userId,
            organizationId: null,
            planId: PlanIds.IndividualFree,
            status: SubscriptionStatus.Active,
            paymentStatus: PaymentStatus.Paid,
            ct);

    public Task EnsureOrganizationPlanAsync(
        Guid organizationId,
        Guid planId,
        bool awaitingPayment = true,
        CancellationToken ct = default)
        => EnsureSubscriptionAsync(
            userId: null,
            organizationId: organizationId,
            planId: planId,
            status: awaitingPayment ? SubscriptionStatus.Inactive : SubscriptionStatus.Active,
            paymentStatus: awaitingPayment ? PaymentStatus.Pending : PaymentStatus.Paid,
            ct);

    private async Task EnsureSubscriptionAsync(
        Guid? userId,
        Guid? organizationId,
        Guid planId,
        SubscriptionStatus status,
        PaymentStatus paymentStatus,
        CancellationToken ct)
    {
        var exists = await _db.Subscriptions.AnyAsync(s =>
            (userId.HasValue
                ? s.UserId == userId
                : s.OrganizationId == organizationId),
            ct);

        if (exists)
            return;

        var plan = await _db.Plans
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == planId && p.IsActive, ct);

        if (plan is null)
        {
            throw new InvalidOperationException(
                $"Plan {planId} is missing or inactive.");
        }

        if (organizationId.HasValue && plan.TargetType != PlanTargetType.Organization)
        {
            throw new InvalidOperationException(
                $"Plan {planId} is not an organization plan.");
        }

        if (userId.HasValue && plan.TargetType != PlanTargetType.Individual)
        {
            throw new InvalidOperationException(
                $"Plan {planId} is not an individual plan.");
        }

        var now = DateTime.UtcNow;
        _db.Subscriptions.Add(new Subscription
        {
            UserId = userId,
            OrganizationId = organizationId,
            PlanId = planId,
            Status = status,
            PaymentStatus = paymentStatus,
            StartDate = now,
            EndDate = now.AddYears(1),
        });

        await _db.SaveChangesAsync(ct);
    }
}
