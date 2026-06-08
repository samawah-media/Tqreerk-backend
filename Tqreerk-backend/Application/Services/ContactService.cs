using System.Net;
using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Contact;
using Taqreerk.Application.Interfaces;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class ContactService : IContactService
{
    private const string SupportEmailKey = "support_email";
    private const string DefaultSupportEmail = "support@taqreerk.com";

    private readonly TaqreerkDbContext _db;
    private readonly IEmailSender _email;

    public ContactService(TaqreerkDbContext db, IEmailSender email)
    {
        _db = db;
        _email = email;
    }

    public async Task<SubmitContactResponse> SubmitAsync(
        SubmitContactRequest req, CancellationToken ct = default)
    {
        var fullName = req.FullName.Trim();
        var email = req.Email.Trim().ToLowerInvariant();
        var phone = string.IsNullOrWhiteSpace(req.Phone) ? null : req.Phone.Trim();
        var message = req.Message.Trim();

        if (fullName.Length == 0)
            throw new ArgumentException("الاسم مطلوب.");
        if (message.Length < 10)
            throw new ArgumentException("الرسالة قصيرة جداً — يرجى كتابة 10 أحرف على الأقل.");

        var supportEmail = await _db.SystemSettings
            .AsNoTracking()
            .Where(s => s.Key == SupportEmailKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(supportEmail))
            supportEmail = DefaultSupportEmail;

        var typeLabel = req.Type switch
        {
            "suggestion" => "اقتراح",
            "complaint" => "شكوى",
            _ => "استفسار",
        };

        var encodedName = WebUtility.HtmlEncode(fullName);
        var encodedEmail = WebUtility.HtmlEncode(email);
        var encodedPhone = WebUtility.HtmlEncode(phone ?? "—");
        var encodedMessage = WebUtility.HtmlEncode(message).Replace("\n", "<br/>", StringComparison.Ordinal);

        var supportSubject = $"[تقريرك] {typeLabel} — {fullName}";
        var supportBody =
            "<div dir=\"rtl\" style=\"font-family:Arial,sans-serif;line-height:1.6\">" +
            "<h2>طلب تواصل جديد</h2>" +
            "<p><strong>النوع:</strong> " + typeLabel + "</p>" +
            "<p><strong>الاسم:</strong> " + encodedName + "</p>" +
            "<p><strong>البريد:</strong> " + encodedEmail + "</p>" +
            "<p><strong>الهاتف:</strong> " + encodedPhone + "</p>" +
            "<p><strong>الرسالة:</strong></p>" +
            "<p>" + encodedMessage + "</p>" +
            "</div>";

        await _email.SendEmailAsync(supportEmail, supportSubject, supportBody, ct);

        var ackBody =
            "<div dir=\"rtl\" style=\"font-family:Arial,sans-serif;line-height:1.6\">" +
            "<p>مرحباً " + encodedName + "،</p>" +
            "<p>تم استلام رسالتك بنجاح. سيقوم فريق الدعم بمراجعتها والرد عليك في أقرب وقت.</p>" +
            "<p>مع تحيات فريق تقريرك</p>" +
            "</div>";

        await _email.SendEmailAsync(
            email,
            "تم استلام رسالتك — تقريرك",
            ackBody,
            ct);

        return new SubmitContactResponse(
            "تم إرسال رسالتك بنجاح. سيقوم فريق الدعم بالرد عليك قريباً.");
    }
}
