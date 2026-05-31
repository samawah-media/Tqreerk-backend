using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Settings;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

/// <summary>
/// Charges saved Moyasar tokens for annual subscription renewals (Hangfire-driven).
/// </summary>
public sealed class SubscriptionRenewalService
{
    private const string RenewalPaymentMethod = "moyasar-renewal";

    private readonly TaqreerkDbContext _db;
    private readonly IMoyasarApiClient _moyasar;
    private readonly IPaymentCheckoutService _checkout;
    private readonly PaymentReceiptNotifier _receipts;
    private readonly MoyasarSettings _settings;
    private readonly ILogger<SubscriptionRenewalService> _logger;

    public SubscriptionRenewalService(
        TaqreerkDbContext db,
        IMoyasarApiClient moyasar,
        IPaymentCheckoutService checkout,
        PaymentReceiptNotifier receipts,
        IOptions<MoyasarSettings> settings,
        ILogger<SubscriptionRenewalService> logger)
    {
        _db = db;
        _moyasar = moyasar;
        _checkout = checkout;
        _receipts = receipts;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task ProcessDueRenewalsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.SecretKey))
        {
            _logger.LogWarning("Subscription renewal skipped: Moyasar SecretKey not configured.");
            return;
        }

        var now = DateTime.UtcNow;
        var renewBy = now.AddDays(_settings.RenewalLeadDays);

        // Auto-renew only before EndDate (within lead window). No retries after expiry.
        var candidates = await _db.Subscriptions
            .AsNoTracking()
            .Include(s => s.Plan)
            .Where(s =>
                s.Status == SubscriptionStatus.Active
                && s.PaymentStatus == PaymentStatus.Paid
                && s.Plan.AnnualPrice > 0
                && s.EndDate > now
                && s.EndDate <= renewBy)
            .ToListAsync(ct);

        _logger.LogInformation(
            "Subscription renewal scan: {Count} candidate(s) with EndDate in (now, now+{LeadDays}d].",
            candidates.Count,
            _settings.RenewalLeadDays);

        foreach (var snapshot in candidates)
        {
            try
            {
                await RenewOneAsync(snapshot.Id, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Subscription renewal failed for subscription {SubscriptionId}.",
                    snapshot.Id);
            }
        }

        // Catch-up: send failure emails for renewal charges that failed but were never notified.
        await NotifyUnsentRenewalFailureEmailsAsync(ct);
    }

    private async Task NotifyUnsentRenewalFailureEmailsAsync(CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-30);
        var failedPayments = await _db.Payments
            .AsNoTracking()
            .Include(p => p.Subscription!)
            .ThenInclude(s => s!.Plan)
            .Where(p =>
                p.PaymentMethod == RenewalPaymentMethod
                && p.Status == PaymentStatus.Failed
                && p.CreatedAt >= since)
            .OrderByDescending(p => p.CreatedAt)
            .Take(50)
            .ToListAsync(ct);

        foreach (var payment in failedPayments)
        {
            var subscription = payment.Subscription;
            var plan = subscription?.Plan;
            if (subscription is null || plan is null)
                continue;

            await _receipts.TrySendFailedAsync(
                payment,
                subscription,
                plan,
                wasUpgradeAttempt: false,
                isRenewalAttempt: true,
                moyasarStatus: payment.MiserPaymentReference is null ? "failed" : null,
                attemptedAtUtc: payment.CreatedAt,
                ct: ct);
        }
    }

    private async Task RenewOneAsync(Guid subscriptionId, CancellationToken ct)
    {
        var subscription = await _db.Subscriptions
            .Include(s => s.Plan)
            .FirstOrDefaultAsync(s => s.Id == subscriptionId, ct);
        if (subscription?.Plan is null)
        {
            _logger.LogWarning("Renewal skipped: subscription {SubscriptionId} not found.", subscriptionId);
            return;
        }

        var addons = SubscriptionAddons.Parse(subscription.AddonsJson);
        if (!addons.AutoRenew)
        {
            _logger.LogDebug(
                "Renewal skipped for {SubscriptionId}: auto-renew is off.",
                subscriptionId);
            return;
        }

        if (string.IsNullOrWhiteSpace(addons.MoyasarToken))
        {
            _logger.LogWarning(
                "Renewal skipped for {SubscriptionId}: no Moyasar card token saved.",
                subscriptionId);
            return;
        }

        var now = DateTime.UtcNow;
        if (addons.LastRenewalAttemptUtc.HasValue
            && addons.LastRenewalAttemptUtc.Value > now.AddHours(-20))
        {
            var lastStatus = await _db.Payments
                .AsNoTracking()
                .Where(p => p.SubscriptionId == subscription.Id
                            && p.PaymentMethod == RenewalPaymentMethod)
                .OrderByDescending(p => p.CreatedAt)
                .Select(p => (PaymentStatus?)p.Status)
                .FirstOrDefaultAsync(ct);

            if (lastStatus is not PaymentStatus.Failed)
            {
                _logger.LogDebug(
                    "Renewal skipped for {SubscriptionId}: last attempt at {At} (cooldown 20h, last status {Status}).",
                    subscriptionId,
                    addons.LastRenewalAttemptUtc,
                    lastStatus);
                return;
            }
        }

        await ExpireStalePendingRenewalPaymentsAsync(subscription.Id, now, ct);

        var hasOpenPayment = await _db.Payments.AnyAsync(
            p => p.SubscriptionId == subscription.Id
                 && p.PaymentMethod == RenewalPaymentMethod
                 && p.Status == PaymentStatus.Pending
                 && p.CreatedAt > now.AddDays(-3),
            ct);
        if (hasOpenPayment)
        {
            _logger.LogDebug(
                "Renewal skipped for {SubscriptionId}: pending renewal payment still open.",
                subscriptionId);
            return;
        }

        var plan = subscription.Plan;
        var payment = new Payment
        {
            SubscriptionId = subscription.Id,
            Amount = plan.AnnualPrice,
            Currency = "SAR",
            Status = PaymentStatus.Pending,
            PaymentMethod = RenewalPaymentMethod,
        };
        _db.Payments.Add(payment);

        subscription.AddonsJson = SubscriptionAddons.Serialize(
            addons with { LastRenewalAttemptUtc = now });
        await _db.SaveChangesAsync(ct);

        var description = subscription.OrganizationId.HasValue
            ? $"تجديد اشتراك مؤسسي — {plan.NameAr}"
            : $"تجديد اشتراك سنوي — {plan.NameAr}";

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["paymentId"] = payment.Id.ToString(),
            ["subscriptionId"] = subscription.Id.ToString(),
            ["planId"] = plan.Id.ToString(),
            ["renewal"] = "true",
        };

        MoyasarPaymentDto? remote;
        try
        {
            remote = await _moyasar.CreateTokenPaymentAsync(
                payment.Id,
                ToHalalas(plan.AnnualPrice),
                "SAR",
                description,
                _settings.FrontendCallbackUrl,
                addons.MoyasarToken!,
                metadata,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Moyasar token charge threw for subscription {SubscriptionId}, payment {PaymentId}.",
                subscription.Id,
                payment.Id);
            await FinalizeRenewalFailureAsync(
                payment,
                subscription,
                plan,
                now,
                moyasarStatus: "gateway_error",
                moyasarPaymentId: null,
                ct);
            return;
        }

        if (remote is null)
        {
            _logger.LogWarning(
                "Moyasar token charge returned no payment for subscription {SubscriptionId}, payment {PaymentId}.",
                subscription.Id,
                payment.Id);
            await FinalizeRenewalFailureAsync(
                payment,
                subscription,
                plan,
                now,
                moyasarStatus: "no_response",
                moyasarPaymentId: null,
                ct);
            return;
        }

        if (string.Equals(remote.Status, "paid", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var fulfilled = await _checkout.FulfillMoyasarPaymentAsync(remote, ct);
                _logger.LogInformation(
                    "Renewal {PaymentId} for subscription {SubscriptionId}: Moyasar={MoyasarId}, fulfilled={Fulfilled}.",
                    payment.Id,
                    subscription.Id,
                    remote.Id,
                    fulfilled);

                if (!fulfilled)
                {
                    await FinalizeRenewalFailureAsync(
                        payment,
                        subscription,
                        plan,
                        now,
                        moyasarStatus: "fulfill_failed",
                        moyasarPaymentId: remote.Id,
                        ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Renewal fulfill threw for subscription {SubscriptionId}, payment {PaymentId}.",
                    subscription.Id,
                    payment.Id);
                await FinalizeRenewalFailureAsync(
                    payment,
                    subscription,
                    plan,
                    now,
                    moyasarStatus: "fulfill_exception",
                    moyasarPaymentId: remote.Id,
                    ct);
            }

            return;
        }

        await FinalizeRenewalFailureAsync(
            payment,
            subscription,
            plan,
            now,
            moyasarStatus: remote.Status,
            moyasarPaymentId: remote.Id,
            ct);

        _logger.LogWarning(
            "Renewal charge not paid for subscription {SubscriptionId}: status={Status}.",
            subscription.Id,
            remote.Status);
    }

    private async Task FinalizeRenewalFailureAsync(
        Payment payment,
        Subscription subscription,
        Plan plan,
        DateTime attemptedAtUtc,
        string? moyasarStatus,
        string? moyasarPaymentId,
        CancellationToken ct)
    {
        if (payment.Status != PaymentStatus.Failed)
        {
            payment.Status = PaymentStatus.Failed;
            if (!string.IsNullOrWhiteSpace(moyasarPaymentId))
                payment.MiserPaymentReference = moyasarPaymentId;
            await _db.SaveChangesAsync(ct);
        }

        await _receipts.TrySendFailedAsync(
            payment,
            subscription,
            plan,
            wasUpgradeAttempt: false,
            isRenewalAttempt: true,
            moyasarStatus: moyasarStatus,
            attemptedAtUtc: attemptedAtUtc,
            ct: ct);
    }

    private async Task ExpireStalePendingRenewalPaymentsAsync(
        Guid subscriptionId,
        DateTime now,
        CancellationToken ct)
    {
        var stale = await _db.Payments
            .Where(p =>
                p.SubscriptionId == subscriptionId
                && p.PaymentMethod == RenewalPaymentMethod
                && p.Status == PaymentStatus.Pending
                && p.CreatedAt < now.AddHours(-1))
            .ToListAsync(ct);

        if (stale.Count == 0)
            return;

        foreach (var p in stale)
            p.Status = PaymentStatus.Failed;

        await _db.SaveChangesAsync(ct);
        _logger.LogWarning(
            "Marked {Count} stale pending renewal payment(s) as failed for subscription {SubscriptionId}.",
            stale.Count,
            subscriptionId);
    }

    private static int ToHalalas(decimal amountSar)
        => (int)Math.Round(amountSar * 100m, MidpointRounding.AwayFromZero);
}
