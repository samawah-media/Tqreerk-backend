using Taqreerk.Application.DTOs.Payments;

namespace Taqreerk.Application.Interfaces;

public interface IPaymentCheckoutService
{
    Task<CheckoutSessionDto> CreateCheckoutAsync(Guid userId, Guid planId, CancellationToken ct = default);

    Task<VerifyPaymentResultDto> VerifyAndFulfillAsync(
        Guid userId,
        string moyasarPaymentId,
        CancellationToken ct = default);

    /// <summary>Idempotent fulfillment from Moyasar webhook payload.</summary>
    Task<bool> HandleWebhookAsync(string eventType, MoyasarPaymentDto payment, CancellationToken ct = default);

    Task<CancelAutoRenewResultDto> CancelAutoRenewAsync(Guid userId, CancellationToken ct = default);

    bool TryVerifyWebhookSignature(string rawBody, string? signatureHeader);
}
