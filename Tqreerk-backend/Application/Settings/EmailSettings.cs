namespace Taqreerk.Application.Settings;

public class EmailSettings
{
    public const string Section = "EmailSettings";

    public string FromAddress { get; set; } = "no-reply@taqreerk.com";
    public string FromName { get; set; } = "Taqreerk";
    public string AppBaseUrl { get; set; } = "https://taqreerk.com";

    public int VerificationTokenHoursValid { get; set; } = 24;
    public int PasswordResetTokenMinutesValid { get; set; } = 60;

    // SMTP configuration. When SmtpHost is empty, the LogEmailSender (dry-run) is used.
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseStartTls { get; set; } = true;
    public string SmtpUsername { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;

    // Microsoft Graph API configuration. When GraphTenantId is set, GraphEmailSender is used (takes priority over SMTP).
    public string GraphTenantId { get; set; } = string.Empty;
    public string GraphClientId { get; set; } = string.Empty;
    public string GraphClientSecret { get; set; } = string.Empty;
    public string GraphSenderEmail { get; set; } = string.Empty;
}
