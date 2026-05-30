using Taqreerk.Application.DTOs.Payments;

namespace Taqreerk.Application.Interfaces;

public interface IPaymentCheckoutService
{
    Task<CheckoutSessionDto> CreateCheckoutAsync(
        Guid userId,
        Guid planId,
        string? callbackUrl = null,
        CancellationToken ct = default);

    Task<bool> RegisterCardTokenAsync(
        Guid userId,
        Guid paymentId,
        string moyasarPaymentId,
        string sourceToken,
        CancellationToken ct = default);

    Task<VerifyPaymentResultDto> VerifyAndFulfillAsync(
        Guid userId,
        string moyasarPaymentId,
        string? clientSourceToken = null,
        CancellationToken ct = default);

    /// <summary>Idempotent fulfillment from Moyasar webhook payload.</summary>
    Task<bool> HandleWebhookAsync(string eventType, MoyasarPaymentDto payment, CancellationToken ct = default);

    /// <summary>Webhook / renewal job — fulfill a Moyasar payment already marked paid.</summary>
    Task<bool> FulfillMoyasarPaymentAsync(MoyasarPaymentDto remote, CancellationToken ct = default);

    Task<CancelAutoRenewResultDto> CancelAutoRenewAsync(Guid userId, CancellationToken ct = default);

    bool TryVerifyWebhookSignature(string rawBody, string? signatureHeader);
}
