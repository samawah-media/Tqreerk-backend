namespace Taqreerk.Application.DTOs.Payments;

public record CreateCheckoutRequestDto(
    Guid PlanId,
    /// <summary>SPA callback after 3DS, e.g. {origin}/plans/payment/callback</summary>
    string? CallbackUrl = null);

public record RegisterCardTokenRequestDto(
    Guid PaymentId,
    string MoyasarPaymentId,
    string SourceToken);

public record CheckoutSessionDto(
    Guid PaymentId,
    Guid SubscriptionId,
    Guid PlanId,
    string PlanNameAr,
    string PlanNameEn,
    int AmountHalalas,
    string Currency,
    string Description,
    string PublishableKey,
    string CallbackUrl,
    /// <summary>True when the user already has an active paid subscription for this plan.</summary>
    bool AlreadyActive = false);

public record VerifyPaymentRequestDto(
    string MoyasarPaymentId,
    /// <summary>From Moyasar form on_completed (source.token). Optional fallback when API omits token.</summary>
    string? SourceToken = null);

public record VerifyPaymentResultDto(
    bool Success,
    string Status,
    Guid? SubscriptionId,
    string? PlanNameAr,
    /// <summary>True when moyasarToken is stored on the subscription after this call.</summary>
    bool CardTokenSaved);

public record MoyasarPublicConfigDto(string PublishableKey, bool IsConfigured);

public record CancelAutoRenewResultDto(bool AutoRenew, DateTime SubscriptionEndDate);
