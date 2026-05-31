using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.DTOs.Reports;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Common;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class AdminSubscriptionsService : IAdminSubscriptionsService
{
    private const int MaxPageSize = 100;

    private readonly TaqreerkDbContext _db;
    private readonly IMoyasarApiClient _moyasar;
    private readonly IAdminActionLogger _audit;

    public AdminSubscriptionsService(
        TaqreerkDbContext db,
        IMoyasarApiClient moyasar,
        IAdminActionLogger audit)
    {
        _db = db;
        _moyasar = moyasar;
        _audit = audit;
    }

    public async Task<PagedResult<AdminSubscriptionListItemDto>> ListAsync(
        AdminSubscriptionsListRequest req,
        CancellationToken ct = default)
    {
        var page = Math.Max(1, req.Page);
        var pageSize = Math.Clamp(req.PageSize, 1, MaxPageSize);

        var q = _db.Subscriptions.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(req.Status)
            && Enum.TryParse<SubscriptionStatus>(req.Status, ignoreCase: true, out var status))
            q = q.Where(s => s.Status == status);

        if (!string.IsNullOrWhiteSpace(req.PaymentStatus)
            && Enum.TryParse<PaymentStatus>(req.PaymentStatus, ignoreCase: true, out var payStatus))
            q = q.Where(s => s.PaymentStatus == payStatus);

        if (!string.IsNullOrWhiteSpace(req.SubscriberType))
        {
            q = req.SubscriberType.ToLowerInvariant() switch
            {
                "individual" => q.Where(s => s.UserId != null),
                "organization" => q.Where(s => s.OrganizationId != null),
                _ => q,
            };
        }

        if (!string.IsNullOrWhiteSpace(req.Q))
        {
            var term = req.Q.Trim().ToLower();
            q = q.Where(s =>
                (s.User != null && (s.User.FullName.ToLower().Contains(term)
                                    || s.User.Email.ToLower().Contains(term)))
                || (s.Organization != null && (s.Organization.NameAr.ToLower().Contains(term)
                                               || s.Organization.NameEn.ToLower().Contains(term))));
        }

        var total = await q.CountAsync(ct);

        var rows = await q
            .OrderByDescending(s => s.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new
            {
                s.Id,
                s.UserId,
                s.OrganizationId,
                UserName = s.User != null ? s.User.FullName : null,
                UserEmail = s.User != null ? s.User.Email : null,
                OrgNameAr = s.Organization != null ? s.Organization.NameAr : null,
                OrgNameEn = s.Organization != null ? s.Organization.NameEn : null,
                s.PlanId,
                PlanNameAr = s.Plan.NameAr,
                PlanNameEn = s.Plan.NameEn,
                Status = s.Status.ToString(),
                PaymentStatus = s.PaymentStatus.ToString(),
                s.StartDate,
                s.EndDate,
                s.AddonsJson,
                s.CreatedAt,
                LastPaid = s.Payments
                    .Where(p => p.Status == PaymentStatus.Paid)
                    .OrderByDescending(p => p.PaidAt ?? p.CreatedAt)
                    .Select(p => new
                    {
                        p.Id,
                        p.Amount,
                        p.PaidAt,
                        p.MiserPaymentReference,
                    })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        var items = rows.Select(r =>
        {
            var addons = SubscriptionAddons.Parse(r.AddonsJson);
            var isOrg = r.OrganizationId.HasValue;
            return new AdminSubscriptionListItemDto(
                r.Id,
                isOrg ? "organization" : "individual",
                isOrg ? (r.OrgNameAr ?? r.OrgNameEn ?? "—") : (r.UserName ?? "—"),
                isOrg ? null : r.UserEmail,
                r.PlanId,
                r.PlanNameAr,
                r.PlanNameEn,
                r.Status,
                r.PaymentStatus,
                r.StartDate,
                r.EndDate,
                addons.AutoRenew,
                r.LastPaid?.Amount,
                r.LastPaid?.PaidAt,
                r.LastPaid?.MiserPaymentReference,
                r.LastPaid?.Id,
                r.CreatedAt);
        }).ToList();

        return new PagedResult<AdminSubscriptionListItemDto>(items, total, page, pageSize);
    }

    public async Task<AdminSubscriptionDetailDto> GetAsync(Guid subscriptionId, CancellationToken ct = default)
    {
        var row = await _db.Subscriptions
            .AsNoTracking()
            .Where(s => s.Id == subscriptionId)
            .Select(s => new
            {
                s.Id,
                s.UserId,
                s.OrganizationId,
                UserName = s.User != null ? s.User.FullName : null,
                UserEmail = s.User != null ? s.User.Email : null,
                OrgNameAr = s.Organization != null ? s.Organization.NameAr : null,
                OrgNameEn = s.Organization != null ? s.Organization.NameEn : null,
                s.PlanId,
                PlanNameAr = s.Plan.NameAr,
                PlanNameEn = s.Plan.NameEn,
                Status = s.Status.ToString(),
                PaymentStatus = s.PaymentStatus.ToString(),
                s.StartDate,
                s.EndDate,
                s.AddonsJson,
                Payments = s.Payments
                    .OrderByDescending(p => p.CreatedAt)
                    .Select(p => new
                    {
                        p.Id,
                        p.Amount,
                        p.Currency,
                        Status = p.Status.ToString(),
                        p.PaymentMethod,
                        p.PaidAt,
                        p.MiserPaymentReference,
                        p.CreatedAt,
                    })
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Subscription not found.");

        var addons = SubscriptionAddons.Parse(row.AddonsJson);
        var isOrg = row.OrganizationId.HasValue;

        var payments = row.Payments
            .Select(p => new AdminSubscriptionPaymentDto(
                p.Id,
                p.Amount,
                p.Currency,
                p.Status,
                p.PaymentMethod,
                p.PaidAt,
                p.MiserPaymentReference,
                p.Status == nameof(PaymentStatus.Paid)
                    && !string.IsNullOrWhiteSpace(p.MiserPaymentReference),
                p.CreatedAt))
            .ToList();

        return new AdminSubscriptionDetailDto(
            row.Id,
            isOrg ? "organization" : "individual",
            isOrg ? (row.OrgNameAr ?? row.OrgNameEn ?? "—") : (row.UserName ?? "—"),
            isOrg ? null : row.UserEmail,
            row.UserId,
            row.OrganizationId,
            row.PlanId,
            row.PlanNameAr,
            row.PlanNameEn,
            row.Status,
            row.PaymentStatus,
            row.StartDate,
            row.EndDate,
            addons.AutoRenew,
            !string.IsNullOrWhiteSpace(addons.MoyasarToken),
            payments);
    }

    public async Task<RefundSubscriptionPaymentResultDto> RefundPaymentAsync(
        Guid actingAdminUserId,
        Guid paymentId,
        RefundSubscriptionPaymentRequest req,
        string? ipAddress,
        CancellationToken ct = default)
    {
        var payment = await _db.Payments
            .Include(p => p.Subscription)
            .ThenInclude(s => s!.Plan)
            .FirstOrDefaultAsync(p => p.Id == paymentId, ct)
            ?? throw new KeyNotFoundException("Payment not found.");

        if (payment.Status != PaymentStatus.Paid)
            throw new InvalidOperationException("Only paid payments can be refunded.");

        if (string.IsNullOrWhiteSpace(payment.MiserPaymentReference))
            throw new InvalidOperationException("This payment has no Moyasar reference and cannot be refunded via the gateway.");

        var subscription = payment.Subscription
            ?? throw new InvalidOperationException("Subscription not found for this payment.");

        var totalHalalas = ToHalalas(payment.Amount);
        if (totalHalalas <= 0)
            throw new InvalidOperationException("Payment amount is invalid for refund.");

        var (refundHalalas, isFullRefund) = ResolveRefundAmount(req.AmountHalalas, totalHalalas);

        var beforePayment = SnapshotPayment(payment);
        var beforeSubscription = SnapshotSubscription(subscription);

        var remote = await _moyasar.RefundPaymentAsync(
            payment.MiserPaymentReference,
            isFullRefund ? null : refundHalalas,
            ct);

        if (!string.Equals(remote.Status, "refunded", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(remote.Status, "paid", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Moyasar returned unexpected status after refund: {remote.Status}");
        }

        payment.Status = isFullRefund
            ? PaymentStatus.Refunded
            : PaymentStatus.PartiallyRefunded;
        ApplySubscriptionAfterRefund(subscription, payment, remote, isFullRefund);

        await _db.SaveChangesAsync(ct);

        var auditAction = isFullRefund ? "payment.refund" : "payment.refund.partial";
        await _audit.LogAsync(
            actingAdminUserId,
            auditAction,
            "Payment",
            payment.Id,
            req.Reason,
            beforeState: new { Payment = beforePayment, Subscription = beforeSubscription },
            afterState: new
            {
                Payment = SnapshotPayment(payment),
                Subscription = SnapshotSubscription(subscription),
                Moyasar = new { remote.Id, remote.Status, remote.Amount },
                Refund = new { refundHalalas, isFullRefund },
            },
            ct);

        return new RefundSubscriptionPaymentResultDto(
            payment.Id,
            subscription.Id,
            remote.Id,
            remote.Status,
            refundHalalas / 100m,
            isFullRefund,
            subscription.Status.ToString(),
            subscription.PaymentStatus.ToString());
    }

    private static int ToHalalas(decimal amountSar)
        => (int)Math.Round(amountSar * 100m, MidpointRounding.AwayFromZero);

    private static (int RefundHalalas, bool IsFullRefund) ResolveRefundAmount(int? amountHalalas, int totalHalalas)
    {
        if (amountHalalas is null or <= 0)
            return (totalHalalas, true);

        if (amountHalalas > totalHalalas)
            throw new InvalidOperationException(
                $"Refund amount cannot exceed the payment total ({totalHalalas} halalas).");

        var isFull = amountHalalas >= totalHalalas;
        return (isFull ? totalHalalas : amountHalalas.Value, isFull);
    }

    private static void ApplySubscriptionAfterRefund(
        Subscription subscription,
        Payment refundedPayment,
        MoyasarPaymentDto remote,
        bool isFullRefund)
    {
        var refundedPayStatus = isFullRefund
            ? PaymentStatus.Refunded
            : PaymentStatus.PartiallyRefunded;

        var addons = SubscriptionAddons.Parse(subscription.AddonsJson);
        subscription.AddonsJson = SubscriptionAddons.Serialize(
            addons with
            {
                AutoRenew = false,
                MoyasarToken = null,
                PendingPlanId = null,
            });

        if (subscription.OrganizationId.HasValue)
        {
            // Org must re-pay on the same plan before any platform access.
            // Payment row keeps Refunded/PartiallyRefunded; subscription awaits checkout.
            subscription.Status = SubscriptionStatus.Inactive;
            subscription.PaymentStatus = PaymentStatus.Pending;
            return;
        }

        subscription.PaymentStatus = refundedPayStatus;

        // Individual: cancel paid tier. Renewal payments roll back the extended year.
        if (remote.Metadata is not null
            && remote.Metadata.TryGetValue("renewal", out var renewalFlag)
            && string.Equals(renewalFlag, "true", StringComparison.OrdinalIgnoreCase))
        {
            var rolled = subscription.EndDate.AddYears(-1);
            subscription.EndDate = rolled > subscription.StartDate ? rolled : DateTime.UtcNow;
            subscription.PlanId = PlanIds.IndividualFree;
            subscription.Status = SubscriptionStatus.Inactive;
            return;
        }

        subscription.PlanId = PlanIds.IndividualFree;
        subscription.Status = SubscriptionStatus.Inactive;
    }

    private static object SnapshotPayment(Payment p) => new
    {
        p.Id,
        Status = p.Status.ToString(),
        p.Amount,
        p.MiserPaymentReference,
        p.PaidAt,
    };

    private static object SnapshotSubscription(Subscription s) => new
    {
        s.Id,
        s.PlanId,
        Status = s.Status.ToString(),
        PaymentStatus = s.PaymentStatus.ToString(),
        s.StartDate,
        s.EndDate,
        s.AddonsJson,
    };
}
