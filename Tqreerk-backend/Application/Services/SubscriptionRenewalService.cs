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
            "Subscription renewal scan: {Count} candidate(s) in window.",
            candidates.Count);

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
    }

    private async Task RenewOneAsync(Guid subscriptionId, CancellationToken ct)
    {
        var subscription = await _db.Subscriptions
            .Include(s => s.Plan)
            .FirstOrDefaultAsync(s => s.Id == subscriptionId, ct);
        if (subscription?.Plan is null)
            return;

        var addons = SubscriptionAddons.Parse(subscription.AddonsJson);
        if (!addons.AutoRenew || string.IsNullOrWhiteSpace(addons.MoyasarToken))
            return;

        var now = DateTime.UtcNow;
        if (addons.LastRenewalAttemptUtc.HasValue
            && addons.LastRenewalAttemptUtc.Value > now.AddHours(-20))
        {
            return;
        }

        var hasOpenPayment = await _db.Payments.AnyAsync(
            p => p.SubscriptionId == subscription.Id
                 && p.Status == PaymentStatus.Pending
                 && p.CreatedAt > now.AddDays(-3),
            ct);
        if (hasOpenPayment)
            return;

        var plan = subscription.Plan;
        var payment = new Payment
        {
            SubscriptionId = subscription.Id,
            Amount = plan.AnnualPrice,
            Currency = "SAR",
            Status = PaymentStatus.Pending,
            PaymentMethod = "moyasar-renewal",
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

        var remote = await _moyasar.CreateTokenPaymentAsync(
            payment.Id,
            ToHalalas(plan.AnnualPrice),
            "SAR",
            description,
            _settings.FrontendCallbackUrl,
            addons.MoyasarToken!,
            metadata,
            ct);

        if (remote is null)
        {
            _logger.LogWarning(
                "Moyasar token charge returned no response for subscription {SubscriptionId}, payment {PaymentId}.",
                subscription.Id,
                payment.Id);
            payment.Status = PaymentStatus.Failed;
            await _db.SaveChangesAsync(ct);
            await _receipts.TrySendFailedAsync(
                payment,
                subscription,
                plan,
                wasUpgradeAttempt: false,
                isRenewalAttempt: true,
                payerUserId: null,
                ct: ct);
            return;
        }

        if (string.Equals(remote.Status, "paid", StringComparison.OrdinalIgnoreCase))
        {
            var fulfilled = await _checkout.FulfillMoyasarPaymentAsync(remote, ct);
            _logger.LogInformation(
                "Renewal {PaymentId} for subscription {SubscriptionId}: Moyasar={MoyasarId}, fulfilled={Fulfilled}.",
                payment.Id,
                subscription.Id,
                remote.Id,
                fulfilled);
            return;
        }

        payment.Status = PaymentStatus.Failed;
        payment.MiserPaymentReference = remote.Id;
        await _db.SaveChangesAsync(ct);

        await _receipts.TrySendFailedAsync(
            payment, subscription, plan, wasUpgradeAttempt: false, isRenewalAttempt: true, ct: ct);

        _logger.LogWarning(
            "Renewal charge not paid for subscription {SubscriptionId}: status={Status}.",
            subscription.Id,
            remote.Status);
    }

    private static int ToHalalas(decimal amountSar)
        => (int)Math.Round(amountSar * 100m, MidpointRounding.AwayFromZero);
}
