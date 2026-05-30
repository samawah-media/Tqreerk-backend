using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taqreerk.Application.DTOs.Payments;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Settings;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class PaymentCheckoutService : IPaymentCheckoutService
{
    private readonly TaqreerkDbContext _db;
    private readonly IMoyasarApiClient _moyasar;
    private readonly ISubscriptionProvisioningService _provisioning;
    private readonly PaymentReceiptNotifier _receipts;
    private readonly MoyasarSettings _settings;

    public PaymentCheckoutService(
        TaqreerkDbContext db,
        IMoyasarApiClient moyasar,
        ISubscriptionProvisioningService provisioning,
        PaymentReceiptNotifier receipts,
        IOptions<MoyasarSettings> settings)
    {
        _db = db;
        _moyasar = moyasar;
        _provisioning = provisioning;
        _receipts = receipts;
        _settings = settings.Value;
    }

    public async Task<CheckoutSessionDto> CreateCheckoutAsync(
        Guid userId,
        Guid planId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.PublishableKey)
            || string.IsNullOrWhiteSpace(_settings.SecretKey))
        {
            throw new InvalidOperationException(
                "بوابة الدفع غير مهيأة. أضف مفاتيح Moyasar في الإعدادات.");
        }

        var plan = await _db.Plans
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == planId && p.IsActive, ct)
            ?? throw new KeyNotFoundException("الباقة غير موجودة.");

        if (plan.AnnualPrice <= 0)
            throw new InvalidOperationException("هذه الباقة مجانية ولا تتطلب دفعاً.");

        var (subscription, isOrg, isFounder) = await ResolveWritableSubscriptionAsync(userId, plan, ct);

        var addons = SubscriptionAddons.Parse(subscription.AddonsJson);
        var hasActivePaid =
            subscription.Status == SubscriptionStatus.Active
            && subscription.PaymentStatus == PaymentStatus.Paid;

        if (hasActivePaid && subscription.PlanId == planId)
            throw new InvalidOperationException("أنت مشترك بالفعل في هذه الباقة.");

        if (hasActivePaid)
        {
            // Upgrade: keep current plan/features until Moyasar confirms payment.
            subscription.AddonsJson = SubscriptionAddons.Serialize(
                addons with { AutoRenew = true, PendingPlanId = planId });
        }
        else
        {
            // Org (or other) first activation — not active yet; row tracks checkout target.
            subscription.PlanId = planId;
            subscription.Status = SubscriptionStatus.Inactive;
            subscription.PaymentStatus = PaymentStatus.Pending;
            subscription.AddonsJson = SubscriptionAddons.Serialize(
                addons with { AutoRenew = true, PendingPlanId = null });
        }

        var amountHalalas = ToHalalas(plan.AnnualPrice);
        var payment = new Payment
        {
            SubscriptionId = subscription.Id,
            Amount = plan.AnnualPrice,
            Currency = "SAR",
            Status = PaymentStatus.Pending,
            PaymentMethod = "moyasar",
        };
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync(ct);

        var description = isOrg
            ? $"اشتراك مؤسسي — {plan.NameAr}"
            : $"اشتراك سنوي — {plan.NameAr}";

        return new CheckoutSessionDto(
            PaymentId: payment.Id,
            SubscriptionId: subscription.Id,
            PlanId: plan.Id,
            PlanNameAr: plan.NameAr,
            PlanNameEn: plan.NameEn,
            AmountHalalas: amountHalalas,
            Currency: "SAR",
            Description: description,
            PublishableKey: _settings.PublishableKey,
            CallbackUrl: _settings.FrontendCallbackUrl);
    }

    public async Task<VerifyPaymentResultDto> VerifyAndFulfillAsync(
        Guid userId,
        string moyasarPaymentId,
        CancellationToken ct = default)
    {
        var remote = await _moyasar.GetPaymentAsync(moyasarPaymentId, ct)
            ?? throw new InvalidOperationException("تعذّر التحقق من الدفع.");

        var fulfilled = await TryFulfillAsync(userId, remote, ct);
        string? planNameAr = null;
        if (fulfilled)
        {
            var subId = await GetSubscriptionIdForUserAsync(userId, ct);
            if (subId.HasValue)
            {
                planNameAr = await _db.Subscriptions.AsNoTracking()
                    .Where(s => s.Id == subId.Value)
                    .Select(s => s.Plan.NameAr)
                    .FirstOrDefaultAsync(ct);
            }
        }

        return new VerifyPaymentResultDto(
            Success: fulfilled,
            Status: remote.Status,
            SubscriptionId: fulfilled ? await GetSubscriptionIdForUserAsync(userId, ct) : null,
            PlanNameAr: planNameAr);
    }

    public async Task<bool> HandleWebhookAsync(
        string eventType,
        MoyasarPaymentDto payment,
        CancellationToken ct = default)
    {
        if (!string.Equals(eventType, "payment_paid", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(eventType, "payment_failed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(eventType, "payment_failed", StringComparison.OrdinalIgnoreCase))
        {
            await MarkPaymentFailedAsync(payment, ct);
            return true;
        }

        return await TryFulfillAsync(userId: null, payment, ct);
    }

    public async Task<CancelAutoRenewResultDto> CancelAutoRenewAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        var sub = await GetWritableSubscriptionForUserAsync(userId, ct);
        if (sub is null)
            throw new KeyNotFoundException("لا يوجد اشتراك.");

        var addons = SubscriptionAddons.Parse(sub.AddonsJson);
        sub.AddonsJson = SubscriptionAddons.Serialize(addons with { AutoRenew = false });
        await _db.SaveChangesAsync(ct);
        return new CancelAutoRenewResultDto(false);
    }

    public bool TryVerifyWebhookSignature(string rawBody, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(_settings.WebhookSecret))
            return true;

        if (string.IsNullOrWhiteSpace(signatureHeader))
            return false;

        var computed = ComputeHmac(rawBody, _settings.WebhookSecret);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(signatureHeader.Trim()));
    }

    private async Task<bool> TryFulfillAsync(
        Guid? userId,
        MoyasarPaymentDto remote,
        CancellationToken ct)
    {
        if (!string.Equals(remote.Status, "paid", StringComparison.OrdinalIgnoreCase))
            return false;

        var payment = await ResolvePaymentFromMetadataAsync(remote, ct);
        if (payment is null)
            return false;

        if (payment.Status == PaymentStatus.Paid)
            return true;

        var subscription = await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.Id == payment.SubscriptionId, ct);
        if (subscription is null)
            return false;

        if (userId.HasValue && !await UserOwnsSubscriptionAsync(userId.Value, subscription, ct))
            return false;

        var addons = SubscriptionAddons.Parse(subscription.AddonsJson);
        var targetPlanId = addons.PendingPlanId ?? subscription.PlanId;
        var targetPlan = await _db.Plans
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == targetPlanId && p.IsActive, ct);
        if (targetPlan is null)
            return false;

        var expectedHalalas = ToHalalas(targetPlan.AnnualPrice);
        if (remote.Amount != expectedHalalas
            || payment.Amount != targetPlan.AnnualPrice
            || !string.Equals(remote.Currency, payment.Currency, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("مبلغ الدفع لا يطابق الطلب.");
        }

        var now = DateTime.UtcNow;
        payment.Status = PaymentStatus.Paid;
        payment.PaidAt = now;
        payment.MiserPaymentReference = remote.Id;

        subscription.PlanId = targetPlanId;
        subscription.Status = SubscriptionStatus.Active;
        subscription.PaymentStatus = PaymentStatus.Paid;
        subscription.StartDate = now;
        subscription.EndDate = now.AddYears(1);
        subscription.AddonsJson = SubscriptionAddons.Serialize(
            addons with
            {
                AutoRenew = true,
                PendingPlanId = null,
                MoyasarToken = remote.SourceToken ?? addons.MoyasarToken,
            });

        await _db.SaveChangesAsync(ct);

        await _receipts.TrySendAsync(payment, subscription, targetPlan, userId, ct);
        return true;
    }

    private async Task MarkPaymentFailedAsync(MoyasarPaymentDto remote, CancellationToken ct)
    {
        var payment = await ResolvePaymentFromMetadataAsync(remote, ct);
        if (payment is null || payment.Status == PaymentStatus.Paid)
            return;

        payment.Status = PaymentStatus.Failed;
        payment.MiserPaymentReference = remote.Id;

        var sub = await _db.Subscriptions.FirstOrDefaultAsync(
            s => s.Id == payment.SubscriptionId, ct);
        if (sub is not null)
        {
            var addons = SubscriptionAddons.Parse(sub.AddonsJson);
            var wasUpgradePending = addons.PendingPlanId.HasValue;

            if (wasUpgradePending)
            {
                sub.AddonsJson = SubscriptionAddons.Serialize(
                    addons with { PendingPlanId = null });
            }
            else if (sub.PaymentStatus != PaymentStatus.Paid)
            {
                sub.PaymentStatus = PaymentStatus.Failed;
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task<Payment?> ResolvePaymentFromMetadataAsync(
        MoyasarPaymentDto remote,
        CancellationToken ct)
    {
        if (remote.Metadata is not null
            && remote.Metadata.TryGetValue("paymentId", out var pid)
            && Guid.TryParse(pid, out var paymentId))
        {
            return await _db.Payments
                .FirstOrDefaultAsync(p => p.Id == paymentId, ct);
        }

        return await _db.Payments
            .FirstOrDefaultAsync(p => p.MiserPaymentReference == remote.Id, ct);
    }

    private async Task<(Subscription Subscription, bool IsOrg, bool IsFounder)>
        ResolveWritableSubscriptionAsync(
            Guid userId,
            Plan plan,
            CancellationToken ct)
    {
        var membership = await _db.OrganizationMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.IsActive)
            .Select(m => new { m.OrganizationId })
            .FirstOrDefaultAsync(ct);

        if (plan.TargetType == PlanTargetType.Organization)
        {
            if (membership is null)
                throw new InvalidOperationException("الدفع للمؤسسات متاح لأعضاء المؤسسة فقط.");

            var founderId = await ResolveFounderIdAsync(membership.OrganizationId, ct);
            var isFounder = founderId == userId;
            if (!isFounder)
                throw new UnauthorizedAccessException("فقط مؤسس المؤسسة يمكنه إتمام الدفع.");

            await _provisioning.EnsureOrganizationPlanAsync(
                membership.OrganizationId,
                plan.Id,
                awaitingPayment: true,
                ct);

            var sub = await _db.Subscriptions
                .FirstAsync(s => s.OrganizationId == membership.OrganizationId, ct);
            return (sub, true, true);
        }

        if (membership is not null)
            throw new InvalidOperationException("استخدم حساباً فردياً لشراء باقة الأفراد.");

        await _provisioning.EnsureIndividualFreeAsync(userId, ct);

        var userSub = await _db.Subscriptions
            .FirstAsync(s => s.UserId == userId, ct);
        return (userSub, false, false);
    }

    private async Task<Subscription?> GetWritableSubscriptionForUserAsync(
        Guid userId,
        CancellationToken ct)
    {
        var orgId = await _db.OrganizationMembers
            .Where(m => m.UserId == userId && m.IsActive)
            .Select(m => (Guid?)m.OrganizationId)
            .FirstOrDefaultAsync(ct);

        if (orgId.HasValue)
        {
            return await _db.Subscriptions
                .FirstOrDefaultAsync(s => s.OrganizationId == orgId, ct);
        }

        return await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.UserId == userId, ct);
    }

    private async Task<bool> UserOwnsSubscriptionAsync(
        Guid userId,
        Subscription subscription,
        CancellationToken ct)
    {
        if (subscription.UserId == userId)
            return true;

        if (!subscription.OrganizationId.HasValue)
            return false;

        var founderId = await ResolveFounderIdAsync(subscription.OrganizationId.Value, ct);
        return founderId == userId;
    }

    private async Task<Guid?> GetSubscriptionIdForUserAsync(Guid userId, CancellationToken ct)
    {
        var sub = await GetWritableSubscriptionForUserAsync(userId, ct);
        return sub?.Id;
    }

    private async Task<Guid?> ResolveFounderIdAsync(Guid orgId, CancellationToken ct)
    {
        var explicitFounder = await _db.Organizations
            .AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => o.CreatedByUserId)
            .FirstOrDefaultAsync(ct);

        if (explicitFounder.HasValue)
            return explicitFounder;

        return await _db.OrganizationMembers
            .AsNoTracking()
            .Where(m => m.OrganizationId == orgId && m.IsActive)
            .OrderBy(m => m.JoinedAt)
            .ThenBy(m => m.Id)
            .Select(m => (Guid?)m.UserId)
            .FirstOrDefaultAsync(ct);
    }

    private static int ToHalalas(decimal amountSar)
        => (int)Math.Round(amountSar * 100m, MidpointRounding.AwayFromZero);

    private static string ComputeHmac(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
