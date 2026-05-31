namespace Taqreerk.Application.Interfaces;

public interface IMoyasarApiClient
{
    Task<MoyasarPaymentDto?> GetPaymentAsync(string moyasarPaymentId, CancellationToken ct = default);

    /// <summary>Charge a saved card token (annual renewal). Uses paymentId as Moyasar given_id.</summary>
    Task<MoyasarPaymentDto?> CreateTokenPaymentAsync(
        Guid paymentId,
        int amountHalalas,
        string currency,
        string description,
        string callbackUrl,
        string cardToken,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken ct = default);

    /// <summary>Refund a captured/paid Moyasar payment. Omit <paramref name="amountHalalas"/>
    /// for a full refund.</summary>
    Task<MoyasarPaymentDto> RefundPaymentAsync(
        string moyasarPaymentId,
        int? amountHalalas = null,
        CancellationToken ct = default);
}

public record MoyasarPaymentDto(
    string Id,
    string Status,
    int Amount,
    string Currency,
    IReadOnlyDictionary<string, string>? Metadata,
    string? SourceToken);
