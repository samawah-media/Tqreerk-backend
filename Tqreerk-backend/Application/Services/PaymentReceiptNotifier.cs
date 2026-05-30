using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Settings;
using Taqreerk.Domain.Entities;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

/// <summary>
/// Sends a post-payment receipt email via the same <see cref="IEmailSender"/> pipeline as OTP
/// (Microsoft Graph → taqrerk@samawah1.sa on staging/production).
/// </summary>
public sealed class PaymentReceiptNotifier
{
    private readonly TaqreerkDbContext _db;
    private readonly IEmailSender _email;
    private readonly EmailSettings _emailSettings;
    private readonly ILogger<PaymentReceiptNotifier> _logger;

    public PaymentReceiptNotifier(
        TaqreerkDbContext db,
        IEmailSender email,
        IOptions<EmailSettings> emailSettings,
        ILogger<PaymentReceiptNotifier> logger)
    {
        _db = db;
        _email = email;
        _emailSettings = emailSettings.Value;
        _logger = logger;
    }

    public async Task TrySendAsync(
        Payment payment,
        Subscription subscription,
        Plan plan,
        Guid? payerUserId,
        CancellationToken ct = default)
    {
        try
        {
            var payer = await ResolvePayerAsync(subscription, payerUserId, ct);
            if (payer is null || string.IsNullOrWhiteSpace(payer.Email))
            {
                _logger.LogWarning(
                    "Payment {PaymentId} fulfilled but no payer email to send receipt.",
                    payment.Id);
                return;
            }

            var invoice = await EnsureInvoiceAsync(payment, subscription, ct);
            var orgName = subscription.OrganizationId.HasValue
                ? await _db.Organizations.AsNoTracking()
                    .Where(o => o.Id == subscription.OrganizationId)
                    .Select(o => o.NameAr)
                    .FirstOrDefaultAsync(ct)
                : null;

            var subject = $"تأكيد اشتراك تقرير — فاتورة {invoice.InvoiceNumber}";
            var body = BuildHtmlBody(
                payer.FullName,
                orgName,
                plan,
                payment,
                invoice,
                subscription);

            await _email.SendEmailAsync(payer.Email, subject, body, ct);
        }
        catch (Exception ex)
        {
            // Payment already succeeded — never fail fulfillment because email bounced.
            _logger.LogError(ex, "Failed to send payment receipt for payment {PaymentId}", payment.Id);
        }
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

        if (!founderId.HasValue)
            return null;

        return await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == founderId.Value, ct);
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

    private string BuildHtmlBody(
        string fullName,
        string? organizationNameAr,
        Plan plan,
        Payment payment,
        Invoice invoice,
        Subscription subscription)
    {
        var safeName = WebUtility.HtmlEncode(fullName);
        var planName = WebUtility.HtmlEncode(plan.NameAr);
        var orgLine = string.IsNullOrWhiteSpace(organizationNameAr)
            ? ""
            : $"<p><strong>الجهة:</strong> {WebUtility.HtmlEncode(organizationNameAr)}</p>";
        var refLine = string.IsNullOrWhiteSpace(payment.MiserPaymentReference)
            ? ""
            : $"<p><strong>مرجع الدفع:</strong> {WebUtility.HtmlEncode(payment.MiserPaymentReference)}</p>";
        var paidAt = (payment.PaidAt ?? invoice.IssuedAt).ToString("yyyy-MM-dd HH:mm") + " UTC";
        var start = subscription.StartDate.ToString("yyyy-MM-dd");
        var end = subscription.EndDate.ToString("yyyy-MM-dd");
        var dashboardUrl = $"{_emailSettings.AppBaseUrl.TrimEnd('/')}/dashboard";

        return $"""
            <div dir="rtl" style="font-family:Segoe UI,Tahoma,sans-serif;line-height:1.6;color:#0A3034">
              <p>مرحباً {safeName}،</p>
              <p>تم استلام دفعتك وتفعيل اشتراكك بنجاح على منصة <strong>تقرير</strong>.</p>
              <hr style="border:none;border-top:1px solid #e0e0e0;margin:20px 0" />
              <h2 style="color:#28C8A2;margin:0 0 12px">تفاصيل الفاتورة</h2>
              <p><strong>رقم الفاتورة:</strong> {WebUtility.HtmlEncode(invoice.InvoiceNumber)}</p>
              <p><strong>الباقة:</strong> {planName}</p>
              {orgLine}
              <p><strong>المبلغ:</strong> {payment.Amount:N2} {WebUtility.HtmlEncode(payment.Currency)}</p>
              <p><strong>تاريخ الدفع:</strong> {paidAt}</p>
              {refLine}
              <p><strong>فترة الاشتراك:</strong> من {start} إلى {end}</p>
              <hr style="border:none;border-top:1px solid #e0e0e0;margin:20px 0" />
              <p>يمكنك إدارة اشتراكك من <a href="{WebUtility.HtmlEncode(dashboardUrl)}">مساحة العمل</a>.</p>
              <p style="color:#666;font-size:12px">هذه رسالة تأكيد تلقائية — لا حاجة للرد عليها.</p>
            </div>
            """;
    }
}
