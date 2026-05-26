namespace Taqreerk.Application.Interfaces;

public interface IEmailSender
{
    Task SendEmailAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default);
}
