using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<PaymentCheckoutService> _logger;

    public PaymentCheckoutService(
        TaqreerkDbContext db,
        IMoyasarApiClient moyasar,
        ISubscriptionProvisioningService provisioning,
        PaymentReceiptNotifier receipts,
        IOptions<MoyasarSettings> settings,
        ILogger<PaymentCheckoutService> logger)
    {
        _db = db;
        _moyasar = moyasar;
        _provisioning = provisioning;
        _receipts = receipts;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<CheckoutSessionDto> CreateCheckoutAsync(
        Guid userId,
        Guid planId,
        string? callbackUrl = null,
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
            CallbackUrl: ResolveCallbackUrl(callbackUrl));
    }

    public async Task<bool> RegisterCardTokenAsync(
        Guid userId,
        Guid paymentId,
        string moyasarPaymentId,
        string sourceToken,
        CancellationToken ct = default)
    {
        var token = FirstNonEmpty(sourceToken);
        if (token is null)
            return false;

        var payment = await _db.Payments
            .FirstOrDefaultAsync(p => p.Id == paymentId, ct);
        if (payment is null)
            return false;

        var subscription = await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.Id == payment.SubscriptionId, ct);
        if (subscription is null
            || !await UserOwnsSubscriptionAsync(userId, subscription, ct))
        {
            return false;
        }

        var addons = SubscriptionAddons.Parse(subscription.AddonsJson);
        subscription.AddonsJson = SubscriptionAddons.Serialize(
            addons with { MoyasarToken = token });
        payment.MiserPaymentReference = moyasarPaymentId;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Registered moyasarToken for payment {PaymentId} (Moyasar {MoyasarId}).",
            paymentId,
            moyasarPaymentId);
        return true;
    }

    public async Task<VerifyPaymentResultDto> VerifyAndFulfillAsync(
        Guid userId,
        string moyasarPaymentId,
        string? clientSourceToken = null,
        CancellationToken ct = default)
    {
        var remote = await FetchPaymentWithTokenRetriesAsync(moyasarPaymentId, ct)
            ?? throw new InvalidOperationException("تعذّر التحقق من الدفع.");

        var fulfilled = await TryFulfillAsync(userId, remote, clientSourceToken, ct);
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

        var cardTokenSaved = false;
        if (fulfilled)
        {
            var subId = await GetSubscriptionIdForUserAsync(userId, ct);
            if (subId.HasValue)
            {
                var json = await _db.Subscriptions.AsNoTracking()
                    .Where(s => s.Id == subId.Value)
                    .Select(s => s.AddonsJson)
                    .FirstOrDefaultAsync(ct);
                cardTokenSaved = !string.IsNullOrWhiteSpace(
                    SubscriptionAddons.Parse(json).MoyasarToken);
            }
        }

        return new VerifyPaymentResultDto(
            Success: fulfilled,
            Status: remote.Status,
            SubscriptionId: fulfilled ? await GetSubscriptionIdForUserAsync(userId, ct) : null,
            PlanNameAr: planNameAr,
            CardTokenSaved: cardTokenSaved);
    }

    public async Task<bool> HandleWebhookAsync(
        string eventType,
        MoyasarPaymentDto payment,
        CancellationToken ct = default)
    {
        if (!IsPaidWebhookEvent(eventType) && !IsFailedWebhookEvent(eventType))
        {
            return false;
        }

        if (IsFailedWebhookEvent(eventType))
        {
            await MarkPaymentFailedAsync(payment, ct);
            return true;
        }

        return await TryFulfillAsync(userId: null, payment, clientSourceToken: null, ct);
    }

    private async Task<MoyasarPaymentDto?> FetchPaymentWithTokenRetriesAsync(
        string moyasarPaymentId,
        CancellationToken ct)
    {
        MoyasarPaymentDto? last = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            last = await _moyasar.GetPaymentAsync(moyasarPaymentId, ct);
            if (last is null)
                return null;

            if (!string.IsNullOrWhiteSpace(last.SourceToken)
                || !string.Equals(last.Status, "paid", StringComparison.OrdinalIgnoreCase))
            {
                return last;
            }

            if (attempt < 3)
                await Task.Delay(400 * (attempt + 1), ct);
        }

        return last;
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
        return new CancelAutoRenewResultDto(false, sub.EndDate);
    }

    public Task<bool> FulfillMoyasarPaymentAsync(MoyasarPaymentDto remote, CancellationToken ct = default)
        => TryFulfillAsync(userId: null, remote, clientSourceToken: null, ct);

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
        string? clientSourceToken,
        CancellationToken ct)
    {
        if (!string.Equals(remote.Status, "paid", StringComparison.OrdinalIgnoreCase))
            return false;

        var payment = await ResolvePaymentFromMetadataAsync(remote, ct);
        if (payment is null)
            return false;

        var subscription = await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.Id == payment.SubscriptionId, ct);
        if (subscription is null)
            return false;

        if (userId.HasValue && !await UserOwnsSubscriptionAsync(userId.Value, subscription, ct))
            return false;

        var addons = SubscriptionAddons.Parse(subscription.AddonsJson);

        if (payment.Status == PaymentStatus.Paid)
        {
            await TryPersistCardTokenAsync(
                subscription,
                addons,
                remote,
                clientSourceToken,
                payment.Id,
                ct);

            // Webhook may fulfill before SPA verify — still send receipt once.
            var paidPlanId = addons.PendingPlanId ?? subscription.PlanId;
            var paidPlan = await _db.Plans.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == paidPlanId && p.IsActive, ct);
            if (paidPlan is not null)
            {
                await _receipts.TrySendSuccessAsync(
                    payment, subscription, paidPlan, userId, ct);
            }

            return true;
        }
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

        if (IsRenewalPayment(remote))
            return await FulfillRenewalAsync(
                payment,
                subscription,
                addons,
                remote,
                clientSourceToken,
                targetPlan,
                userId,
                ct);

        var now = DateTime.UtcNow;
        payment.Status = PaymentStatus.Paid;
        payment.PaidAt = now;
        payment.MiserPaymentReference = remote.Id;

        subscription.PlanId = targetPlanId;
        subscription.Status = SubscriptionStatus.Active;
        subscription.PaymentStatus = PaymentStatus.Paid;
        subscription.StartDate = now;
        subscription.EndDate = now.AddYears(1);
        var cardToken = ResolveCardToken(remote, clientSourceToken, addons.MoyasarToken);
        LogMissingCardTokenIfNeeded(payment.Id, cardToken);

        subscription.AddonsJson = SubscriptionAddons.Serialize(
            addons with
            {
                AutoRenew = true,
                PendingPlanId = null,
                MoyasarToken = cardToken,
            });

        await _db.SaveChangesAsync(ct);

        await _receipts.TrySendSuccessAsync(payment, subscription, targetPlan, userId, ct);
        return true;
    }

    private async Task<bool> FulfillRenewalAsync(
        Payment payment,
        Subscription subscription,
        SubscriptionAddons.State addons,
        MoyasarPaymentDto remote,
        string? clientSourceToken,
        Plan plan,
        Guid? userId,
        CancellationToken ct)
    {
        if (subscription.Status != SubscriptionStatus.Active
            || subscription.PaymentStatus != PaymentStatus.Paid)
        {
            throw new InvalidOperationException("تجديد غير صالح لهذا الاشتراك.");
        }

        var now = DateTime.UtcNow;
        payment.Status = PaymentStatus.Paid;
        payment.PaidAt = now;
        payment.MiserPaymentReference = remote.Id;

        var cardToken = ResolveCardToken(remote, clientSourceToken, addons.MoyasarToken);
        LogMissingCardTokenIfNeeded(payment.Id, cardToken);

        var periodBase = subscription.EndDate > now ? subscription.EndDate : now;
        subscription.EndDate = periodBase.AddYears(1);
        subscription.AddonsJson = SubscriptionAddons.Serialize(
            addons with
            {
                AutoRenew = true,
                PendingPlanId = null,
                MoyasarToken = cardToken,
                LastRenewalAttemptUtc = now,
            });

        await _db.SaveChangesAsync(ct);
        await _receipts.TrySendSuccessAsync(payment, subscription, plan, userId, ct);
        return true;
    }

    private static bool IsRenewalPayment(MoyasarPaymentDto remote)
        => remote.Metadata is not null
           && remote.Metadata.TryGetValue("renewal", out var flag)
           && string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);

    private async Task<bool> TryPersistCardTokenAsync(
        Subscription subscription,
        SubscriptionAddons.State addons,
        MoyasarPaymentDto remote,
        string? clientSourceToken,
        Guid paymentId,
        CancellationToken ct)
    {
        var cardToken = ResolveCardToken(remote, clientSourceToken, addons.MoyasarToken);
        if (string.IsNullOrWhiteSpace(cardToken)
            || string.Equals(cardToken, addons.MoyasarToken, StringComparison.Ordinal))
        {
            LogMissingCardTokenIfNeeded(paymentId, cardToken ?? addons.MoyasarToken);
            return true;
        }

        subscription.AddonsJson = SubscriptionAddons.Serialize(
            addons with { MoyasarToken = cardToken });
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Updated moyasarToken on subscription {SubscriptionId} after payment {PaymentId}.",
            subscription.Id,
            paymentId);
        return true;
    }

    private static string? ResolveCardToken(
        MoyasarPaymentDto remote,
        string? clientSourceToken,
        string? existingToken)
        => FirstNonEmpty(remote.SourceToken, clientSourceToken, existingToken);

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (string.IsNullOrWhiteSpace(v))
                continue;

            var trimmed = v.Trim();
            if (trimmed.StartsWith("token_", StringComparison.Ordinal))
                return trimmed;
        }

        return null;
    }

    private void LogMissingCardTokenIfNeeded(Guid paymentId, string? cardToken)
    {
        if (!string.IsNullOrWhiteSpace(cardToken))
            return;

        _logger.LogWarning(
            "Payment {PaymentId} fulfilled without moyasarToken. Pay with card (not STC), use save_card on the form, and enable Tokenization on the Moyasar merchant account.",
            paymentId);
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
        var wasUpgradePending = false;
        var isRenewal = IsRenewalPayment(remote);
        Plan? failedPlan = null;

        if (sub is not null)
        {
            var addons = SubscriptionAddons.Parse(sub.AddonsJson);
            wasUpgradePending = addons.PendingPlanId.HasValue;
            var failedPlanId = addons.PendingPlanId ?? sub.PlanId;
            failedPlan = await _db.Plans.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == failedPlanId && p.IsActive, ct);

            if (isRenewal)
            {
                sub.AddonsJson = SubscriptionAddons.Serialize(
                    addons with { LastRenewalAttemptUtc = DateTime.UtcNow });
            }
            else if (wasUpgradePending)
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

        if (sub is not null && failedPlan is not null)
        {
            await _receipts.TrySendFailedAsync(
                payment,
                sub,
                failedPlan,
                wasUpgradePending,
                isRenewalAttempt: isRenewal,
                payerUserId: null,
                ct);
        }
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

    private string ResolveCallbackUrl(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return _settings.FrontendCallbackUrl;

        if (!Uri.TryCreate(requested.Trim(), UriKind.Absolute, out var uri))
            return _settings.FrontendCallbackUrl;

        var isLocal = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase);
        var isHttps = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
        var isLocalHttp = isLocal && uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase);
        if (!isHttps && !isLocalHttp)
            return _settings.FrontendCallbackUrl;

        if (!uri.AbsolutePath.Contains("payment/callback", StringComparison.OrdinalIgnoreCase))
            return _settings.FrontendCallbackUrl;

        return uri.GetLeftPart(UriPartial.Path);
    }

    private static bool IsPaidWebhookEvent(string eventType)
        => string.Equals(eventType, "payment_paid", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailedWebhookEvent(string eventType)
        => string.Equals(eventType, "payment_failed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(eventType, "payment_faild", StringComparison.OrdinalIgnoreCase);
}
