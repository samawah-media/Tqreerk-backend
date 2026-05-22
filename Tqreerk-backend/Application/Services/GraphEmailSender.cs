using Azure.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.SendMail;
using Taqreerk.Application.Interfaces;
using AppEmailSettings = Taqreerk.Application.Settings.EmailSettings;

namespace Taqreerk.Application.Services;

/// Sends email via Microsoft Graph API using Client Credentials (no MFA / SMTP AUTH needed).
/// Requires an Entra ID App Registration with Mail.Send Application permission + admin consent.
public class GraphEmailSender : IEmailSender
{
    private readonly AppEmailSettings _settings;
    private readonly ILogger<GraphEmailSender> _logger;

    public GraphEmailSender(IOptions<AppEmailSettings> settings, ILogger<GraphEmailSender> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task SendEmailAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
    {
        var credential = new ClientSecretCredential(
            _settings.GraphTenantId,
            _settings.GraphClientId,
            _settings.GraphClientSecret);

        var graphClient = new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);

        var message = new Message
        {
            Subject = subject,
            Body = new ItemBody { ContentType = BodyType.Html, Content = htmlBody },
            ToRecipients =
            [
                new Recipient { EmailAddress = new EmailAddress { Address = toEmail } }
            ],
            From = new Recipient
            {
                EmailAddress = new EmailAddress
                {
                    Address = _settings.GraphSenderEmail,
                    Name = _settings.FromName,
                }
            },
        };

        var requestBody = new SendMailPostRequestBody { Message = message, SaveToSentItems = false };

        try
        {
            await graphClient.Users[_settings.GraphSenderEmail].SendMail.PostAsync(requestBody, cancellationToken: ct);
            _logger.LogInformation("Graph email sent to {To} (subject: {Subject})", toEmail, subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Graph email to {To} (subject: {Subject})", toEmail, subject);
            throw;
        }
    }
}
