using Taqreerk.Application.Interfaces;

namespace Taqreerk.Application.Services;

/// Default email sender that writes messages to the logger instead of delivering them.
/// Keeps dev/staging quiet when SMTP isn't wired, and makes reset/verify tokens
/// grep-able in container logs during bring-up. Replace with a real SMTP/SES/
/// Postmark implementation when email delivery is needed.
public class LogEmailSender : IEmailSender
{
    private readonly ILogger<LogEmailSender> _logger;

    public LogEmailSender(ILogger<LogEmailSender> logger) => _logger = logger;

    public Task SendEmailAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[EMAIL:DRY-RUN] To={To} Subject={Subject}\nBody:\n{Body}",
            toEmail, subject, htmlBody);
        return Task.CompletedTask;
    }
}
