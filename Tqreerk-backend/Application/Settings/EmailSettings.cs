namespace Taqreerk.Application.Settings;

public class EmailSettings
{
    public const string Section = "EmailSettings";

    public string FromAddress { get; set; } = "no-reply@taqreerk.com";
    public string FromName { get; set; } = "Taqreerk";
    public string AppBaseUrl { get; set; } = "https://taqreerk.com";

    public int VerificationTokenHoursValid { get; set; } = 24;
    public int PasswordResetTokenMinutesValid { get; set; } = 60;
}
