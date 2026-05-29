namespace Taqreerk.Application.DTOs.Payments;

public record CreateCheckoutRequestDto(Guid PlanId);

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
    string CallbackUrl);

public record VerifyPaymentRequestDto(string MoyasarPaymentId);

public record VerifyPaymentResultDto(
    bool Success,
    string Status,
    Guid? SubscriptionId,
    string? PlanNameAr);

public record MoyasarPublicConfigDto(string PublishableKey, bool IsConfigured);

public record CancelAutoRenewResultDto(bool AutoRenew);
