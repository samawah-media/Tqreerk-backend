namespace Taqreerk.Application.Settings;

public class MoyasarSettings
{
    public const string Section = "Moyasar";

    /// <summary>pk_test_* or pk_live_* — safe for the SPA.</summary>
    public string PublishableKey { get; set; } = string.Empty;

    /// <summary>sk_test_* or sk_live_* — server only.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Webhook signing secret from Moyasar dashboard.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// SPA route Moyasar redirects to after 3DS (must include ?id= from Moyasar).
    /// Example: http://localhost:5173/plans/payment/callback
    /// </summary>
    public string FrontendCallbackUrl { get; set; } = "http://localhost:5173/plans/payment/callback";

    /// <summary>Start auto-renewal this many days before EndDate (UTC).</summary>
    public int RenewalLeadDays { get; set; } = 5;

    /// <summary>Unused — subscriptions end exactly at EndDate. Kept for config compatibility.</summary>
    public int RenewalGraceDaysAfterExpiry { get; set; } = 0;
}
