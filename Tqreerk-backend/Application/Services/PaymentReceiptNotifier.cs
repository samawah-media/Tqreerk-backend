using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Settings;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

/// <summary>
/// Payment outcome emails (success receipt + failure notice) via <see cref="IEmailSender"/>.
/// </summary>
public sealed class PaymentReceiptNotifier
{
    private const string ReceiptEmailSentMarker = "receipt-email:sent";
    private static readonly TimeSpan FailureEmailCacheTtl = TimeSpan.FromDays(7);

    private readonly TaqreerkDbContext _db;
    private readonly IEmailSender _email;
    private readonly EmailSettings _emailSettings;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PaymentReceiptNotifier> _logger;

    public PaymentReceiptNotifier(
        TaqreerkDbContext db,
        IEmailSender email,
        IOptions<EmailSettings> emailSettings,
        IMemoryCache cache,
        ILogger<PaymentReceiptNotifier> logger)
    {
        _db = db;
        _email = email;
        _emailSettings = emailSettings.Value;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>Success: invoice + subscription confirmation (idempotent).</summary>
    public Task TrySendAsync(
        Payment payment,
        Subscription subscription,
        Plan plan,
        Guid? payerUserId,
        CancellationToken ct = default)
        => TrySendSuccessAsync(payment, subscription, plan, payerUserId, ct);

    /// <summary>
    /// Creates the invoice row for a paid payment (always), then sends the receipt email when possible.
    /// </summary>
    public async Task<Invoice?> EnsureInvoiceAndTrySendReceiptAsync(
        Payment payment,
        Subscription subscription,
        Plan plan,
        Guid? payerUserId,
        CancellationToken ct = default)
    {
        if (payment.Status != PaymentStatus.Paid)
        {
            _logger.LogWarning(
                "Skipped invoice for payment {PaymentId}: status is {Status}, expected Paid.",
                payment.Id,
                payment.Status);
            return null;
        }

        try
        {
            var invoice = await EnsureInvoiceAsync(payment, subscription, ct);
            _logger.LogInformation(
                "Invoice {InvoiceNumber} ensured for payment {PaymentId}.",
                invoice.InvoiceNumber,
                payment.Id);

            if (string.Equals(invoice.PdfUrl, ReceiptEmailSentMarker, StringComparison.Ordinal))
            {
                _logger.LogDebug(
                    "Receipt email already sent for payment {PaymentId}.",
                    payment.Id);
                return invoice;
            }

            var payer = await ResolvePayerAsync(subscription, payerUserId, ct);
            if (payer is null || string.IsNullOrWhiteSpace(payer.Email))
            {
                _logger.LogWarning(
                    "Invoice {InvoiceNumber} created for payment {PaymentId} but no payer email — receipt not sent.",
                    invoice.InvoiceNumber,
                    payment.Id);
                return invoice;
            }

            var (orgNameAr, orgNameEn) = await ResolveOrgNamesAsync(subscription, ct);
            var subject =
                $"تأكيد اشتراك تقرير | Taqreerk subscription confirmed — {invoice.InvoiceNumber}";
            var body = BuildSuccessHtmlBody(
                payer.FullName,
                orgNameAr,
                orgNameEn,
                plan,
                payment,
                invoice,
                subscription);

            await _email.SendEmailAsync(payer.Email, subject, body, ct);

            invoice.PdfUrl = ReceiptEmailSentMarker;
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Sent payment receipt for {PaymentId} to {Email} (invoice {InvoiceNumber}).",
                payment.Id,
                payer.Email,
                invoice.InvoiceNumber);
            return invoice;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Invoice/receipt failed for payment {PaymentId}",
                payment.Id);
            return null;
        }
    }

    public Task TrySendSuccessAsync(
        Payment payment,
        Subscription subscription,
        Plan plan,
        Guid? payerUserId,
        CancellationToken ct = default)
        => EnsureInvoiceAndTrySendReceiptAsync(payment, subscription, plan, payerUserId, ct);

    /// <summary>Failure: payment declined / not completed (idempotent per payment).</summary>
    public async Task TrySendFailedAsync(
        Payment payment,
        Subscription subscription,
        Plan plan,
        bool wasUpgradeAttempt,
        bool isRenewalAttempt = false,
        Guid? payerUserId = null,
        CancellationToken ct = default)
    {
        var cacheKey = $"payment-failed-email:{payment.Id}";
        if (_cache.TryGetValue(cacheKey, out _))
        {
            _logger.LogDebug(
                "Failure email already sent for payment {PaymentId}.",
                payment.Id);
            return;
        }

        try
        {
            var payer = await ResolvePayerAsync(subscription, payerUserId, ct);
            if (payer is null || string.IsNullOrWhiteSpace(payer.Email))
            {
                _logger.LogWarning(
                    "Payment {PaymentId} failed but no payer email — failure notice not sent (userId={UserId}, org={OrgId}).",
                    payment.Id,
                    payerUserId,
                    subscription.OrganizationId);
                return;
            }

            var (orgNameAr, orgNameEn) = await ResolveOrgNamesAsync(subscription, ct);
            var subject = isRenewalAttempt
                ? "لم يتم تجديد الاشتراك تلقائياً | Taqreerk auto-renewal failed"
                : wasUpgradeAttempt
                    ? "لم يتم ترقية الباقة | Taqreerk plan upgrade not completed"
                    : "لم يتم إتمام الدفع | Taqreerk payment not completed";
            var body = BuildFailureHtmlBody(
                payer.FullName,
                orgNameAr,
                orgNameEn,
                plan,
                payment,
                wasUpgradeAttempt,
                isRenewalAttempt,
                subscription.EndDate);

            await _email.SendEmailAsync(payer.Email, subject, body, ct);
            _cache.Set(cacheKey, true, FailureEmailCacheTtl);

            _logger.LogInformation(
                "Sent payment failure email for payment {PaymentId} to {Email}.",
                payment.Id,
                payer.Email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send payment failure email for {PaymentId}", payment.Id);
        }
    }

    private async Task<(string? Ar, string? En)> ResolveOrgNamesAsync(
        Subscription subscription,
        CancellationToken ct)
    {
        if (!subscription.OrganizationId.HasValue)
            return (null, null);

        var org = await _db.Organizations.AsNoTracking()
            .Where(o => o.Id == subscription.OrganizationId)
            .Select(o => new { o.NameAr, o.NameEn })
            .FirstOrDefaultAsync(ct);
        return (org?.NameAr, org?.NameEn);
    }

    private async Task<User?> ResolvePayerAsync(
        Subscription subscription,
        Guid? payerUserId,
        CancellationToken ct)
    {
        if (payerUserId.HasValue)
        {
            return await _db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == payerUserId.Value, ct);
        }

        if (subscription.UserId.HasValue)
        {
            return await _db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == subscription.UserId.Value, ct);
        }

        if (!subscription.OrganizationId.HasValue)
            return null;

        var founderId = await _db.Organizations.AsNoTracking()
            .Where(o => o.Id == subscription.OrganizationId)
            .Select(o => o.CreatedByUserId)
            .FirstOrDefaultAsync(ct);

        if (!founderId.HasValue)
        {
            founderId = await _db.OrganizationMembers.AsNoTracking()
                .Where(m => m.OrganizationId == subscription.OrganizationId && m.IsActive)
                .OrderBy(m => m.JoinedAt)
                .Select(m => (Guid?)m.UserId)
                .FirstOrDefaultAsync(ct);
        }

        if (founderId.HasValue)
        {
            var founder = await _db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == founderId.Value, ct);
            if (founder is not null && !string.IsNullOrWhiteSpace(founder.Email))
                return founder;
        }

        return await _db.OrganizationMembers.AsNoTracking()
            .Where(m => m.OrganizationId == subscription.OrganizationId && m.IsActive)
            .Join(
                _db.Users.AsNoTracking(),
                m => m.UserId,
                u => u.Id,
                (_, u) => u)
            .Where(u => u.Email != null && u.Email != "")
            .OrderBy(u => u.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<Invoice> EnsureInvoiceAsync(
        Payment payment,
        Subscription subscription,
        CancellationToken ct)
    {
        var existing = await _db.Invoices
            .FirstOrDefaultAsync(i => i.PaymentId == payment.Id, ct);
        if (existing is not null)
            return existing;

        var invoice = new Invoice
        {
            PaymentId = payment.Id,
            SubscriptionId = subscription.Id,
            InvoiceNumber = await GenerateInvoiceNumberAsync(ct),
            Amount = payment.Amount,
            IssuedAt = payment.PaidAt ?? DateTime.UtcNow,
        };
        _db.Invoices.Add(invoice);
        await _db.SaveChangesAsync(ct);
        return invoice;
    }

    private async Task<string> GenerateInvoiceNumberAsync(CancellationToken ct)
    {
        var year = DateTime.UtcNow.Year;
        var prefix = $"INV-{year}-";
        var count = await _db.Invoices.CountAsync(
            i => i.InvoiceNumber.StartsWith(prefix), ct);
        return $"{prefix}{(count + 1):D6}";
    }

    private string BuildSuccessHtmlBody(
        string fullName,
        string? organizationNameAr,
        string? organizationNameEn,
        Plan plan,
        Payment payment,
        Invoice invoice,
        Subscription subscription)
    {
        var safeName = WebUtility.HtmlEncode(fullName);
        var planNameAr = WebUtility.HtmlEncode(plan.NameAr);
        var planNameEn = WebUtility.HtmlEncode(
            string.IsNullOrWhiteSpace(plan.NameEn) ? plan.NameAr : plan.NameEn);
        var invoiceNo = WebUtility.HtmlEncode(invoice.InvoiceNumber);
        var currency = WebUtility.HtmlEncode(payment.Currency);
        var amount = payment.Amount.ToString("N2");
        var paymentRef = WebUtility.HtmlEncode(payment.MiserPaymentReference ?? "");
        var paidAt = (payment.PaidAt ?? invoice.IssuedAt).ToString("yyyy-MM-dd HH:mm") + " UTC";
        var start = subscription.StartDate.ToString("yyyy-MM-dd");
        var end = subscription.EndDate.ToString("yyyy-MM-dd");
        var dashboardUrl = WebUtility.HtmlEncode($"{_emailSettings.AppBaseUrl.TrimEnd('/')}/dashboard");

        var orgLineAr = OrgLineAr(organizationNameAr);
        var orgLineEn = OrgLineEn(organizationNameAr, organizationNameEn);
        var refLineAr = RefLineAr(payment.MiserPaymentReference, paymentRef);
        var refLineEn = RefLineEn(payment.MiserPaymentReference, paymentRef);

        return WrapEmail($"""
            <div dir="rtl" style="margin-bottom:28px">
              <p>مرحباً {safeName}،</p>
              <p>تم استلام دفعتك وتفعيل اشتراكك بنجاح على منصة <strong>تقرير</strong>.</p>
              <hr style="border:none;border-top:1px solid #e0e0e0;margin:20px 0" />
              <h2 style="color:#28C8A2;margin:0 0 12px">تفاصيل الفاتورة</h2>
              <p><strong>رقم الفاتورة:</strong> {invoiceNo}</p>
              <p><strong>الباقة:</strong> {planNameAr}</p>
              {orgLineAr}
              <p><strong>المبلغ:</strong> {amount} {currency}</p>
              <p><strong>تاريخ الدفع:</strong> {paidAt}</p>
              {refLineAr}
              <p><strong>فترة الاشتراك:</strong> من {start} إلى {end}</p>
              <p>يمكنك إدارة اشتراكك من <a href="{dashboardUrl}">مساحة العمل</a>.</p>
            </div>
            <hr style="border:none;border-top:2px solid #28C8A2;margin:24px 0" />
            <div dir="ltr" style="margin-top:28px">
              <p>Hello {safeName},</p>
              <p>Your payment has been received and your <strong>Taqreerk</strong> subscription is now active.</p>
              <hr style="border:none;border-top:1px solid #e0e0e0;margin:20px 0" />
              <h2 style="color:#28C8A2;margin:0 0 12px">Invoice details</h2>
              <p><strong>Invoice number:</strong> {invoiceNo}</p>
              <p><strong>Plan:</strong> {planNameEn}</p>
              {orgLineEn}
              <p><strong>Amount:</strong> {amount} {currency}</p>
              <p><strong>Paid at:</strong> {paidAt}</p>
              {refLineEn}
              <p><strong>Subscription period:</strong> {start} to {end}</p>
              <p>Manage your subscription from your <a href="{dashboardUrl}">workspace</a>.</p>
            </div>
            """);
    }

    private string BuildFailureHtmlBody(
        string fullName,
        string? organizationNameAr,
        string? organizationNameEn,
        Plan plan,
        Payment payment,
        bool wasUpgradeAttempt,
        bool isRenewalAttempt,
        DateTime subscriptionEndDate)
    {
        var safeName = WebUtility.HtmlEncode(fullName);
        var planNameAr = WebUtility.HtmlEncode(plan.NameAr);
        var planNameEn = WebUtility.HtmlEncode(
            string.IsNullOrWhiteSpace(plan.NameEn) ? plan.NameAr : plan.NameEn);
        var currency = WebUtility.HtmlEncode(payment.Currency);
        var amount = payment.Amount.ToString("N2");
        var paymentRef = WebUtility.HtmlEncode(payment.MiserPaymentReference ?? "");
        var plansUrl = WebUtility.HtmlEncode($"{_emailSettings.AppBaseUrl.TrimEnd('/')}/plans");
        var checkoutUrl = WebUtility.HtmlEncode(
            $"{_emailSettings.AppBaseUrl.TrimEnd('/')}/plans/checkout?planId={plan.Id}");

        var orgLineAr = OrgLineAr(organizationNameAr);
        var orgLineEn = OrgLineEn(organizationNameAr, organizationNameEn);
        var refLineAr = RefLineAr(payment.MiserPaymentReference, paymentRef);
        var refLineEn = RefLineEn(payment.MiserPaymentReference, paymentRef);

        var endDate = subscriptionEndDate.ToString("yyyy-MM-dd");
        var statusAr = isRenewalAttempt
            ? $"لم نتمكن من تجديد اشتراكك تلقائياً. اشتراكك الحالي ساري حتى {endDate} (UTC) ما لم تُلغِ التجديد التلقائي."
            : wasUpgradeAttempt
                ? "لم تكتمل عملية ترقية الباقة."
                : "لم تكتمل عملية الدفع ولم يتم تفعيل الاشتراك.";
        var statusEn = isRenewalAttempt
            ? $"We could not auto-renew your subscription. Your current plan remains active until {endDate} (UTC) unless you cancelled auto-renew."
            : wasUpgradeAttempt
                ? "Your plan upgrade payment did not complete."
                : "Your payment did not complete and your subscription was not activated.";
        var actionAr = isRenewalAttempt
            ? $"حدّث بطاقة الدفع من <a href=\"{checkoutUrl}\">صفحة الدفع</a> أو <a href=\"{plansUrl}\">الباقات</a>، أو ألغِ التجديد التلقائي من الإعدادات."
            : wasUpgradeAttempt
                ? $"اشتراكك الحالي ما زال فعّالاً. يمكنك إعادة المحاولة من <a href=\"{checkoutUrl}\">صفحة الدفع</a> أو <a href=\"{plansUrl}\">الباقات</a>."
                : $"لم يتم خصم المبلغ بنجاح. يمكنك إعادة المحاولة من <a href=\"{checkoutUrl}\">صفحة الدفع</a> أو <a href=\"{plansUrl}\">الباقات</a>.";
        var actionEn = isRenewalAttempt
            ? $"Update your card via <a href=\"{checkoutUrl}\">checkout</a> or <a href=\"{plansUrl}\">plans</a>, or turn off auto-renew in settings."
            : wasUpgradeAttempt
                ? $"Your current subscription is unchanged. Try again from the <a href=\"{checkoutUrl}\">checkout page</a> or <a href=\"{plansUrl}\">plans</a>."
                : $"No successful charge was recorded. Try again from the <a href=\"{checkoutUrl}\">checkout page</a> or <a href=\"{plansUrl}\">plans</a>.";

        return WrapEmail($"""
            <div dir="rtl" style="margin-bottom:28px">
              <p>مرحباً {safeName}،</p>
              <p style="color:#c0392b"><strong>{statusAr}</strong></p>
              <hr style="border:none;border-top:1px solid #e0e0e0;margin:20px 0" />
              <h2 style="color:#c0392b;margin:0 0 12px">تفاصيل المحاولة</h2>
              <p><strong>الباقة:</strong> {planNameAr}</p>
              {orgLineAr}
              <p><strong>المبلغ:</strong> {amount} {currency}</p>
              {refLineAr}
              <p>{actionAr}</p>
            </div>
            <hr style="border:none;border-top:2px solid #e0e0e0;margin:24px 0" />
            <div dir="ltr" style="margin-top:28px">
              <p>Hello {safeName},</p>
              <p style="color:#c0392b"><strong>{statusEn}</strong></p>
              <hr style="border:none;border-top:1px solid #e0e0e0;margin:20px 0" />
              <h2 style="color:#c0392b;margin:0 0 12px">Attempt details</h2>
              <p><strong>Plan:</strong> {planNameEn}</p>
              {orgLineEn}
              <p><strong>Amount:</strong> {amount} {currency}</p>
              {refLineEn}
              <p>{actionEn}</p>
            </div>
            """);
    }

    private static string WrapEmail(string inner)
        => $"""
            <div style="font-family:Segoe UI,Tahoma,sans-serif;line-height:1.6;color:#0A3034;max-width:560px">
              {inner}
              <p dir="ltr" style="color:#666;font-size:12px;margin-top:24px">
                This is an automated message — please do not reply.<br />
                <span dir="rtl">رسالة تلقائية — لا حاجة للرد عليها.</span>
              </p>
            </div>
            """;

    private static string OrgLineAr(string? organizationNameAr)
        => string.IsNullOrWhiteSpace(organizationNameAr)
            ? ""
            : $"<p><strong>الجهة:</strong> {WebUtility.HtmlEncode(organizationNameAr)}</p>";

    private static string OrgLineEn(string? organizationNameAr, string? organizationNameEn)
        => string.IsNullOrWhiteSpace(organizationNameEn)
            ? (string.IsNullOrWhiteSpace(organizationNameAr)
                ? ""
                : $"<p><strong>Organization:</strong> {WebUtility.HtmlEncode(organizationNameAr)}</p>")
            : $"<p><strong>Organization:</strong> {WebUtility.HtmlEncode(organizationNameEn)}</p>";

    private static string RefLineAr(string? reference, string encoded)
        => string.IsNullOrWhiteSpace(reference)
            ? ""
            : $"<p><strong>مرجع الدفع:</strong> {encoded}</p>";

    private static string RefLineEn(string? reference, string encoded)
        => string.IsNullOrWhiteSpace(reference)
            ? ""
            : $"<p><strong>Payment reference:</strong> {encoded}</p>";
}
