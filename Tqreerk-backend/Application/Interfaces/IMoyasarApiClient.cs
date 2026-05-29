namespace Taqreerk.Application.Interfaces;

public interface IMoyasarApiClient
{
    Task<MoyasarPaymentDto?> GetPaymentAsync(string moyasarPaymentId, CancellationToken ct = default);
}

public record MoyasarPaymentDto(
    string Id,
    string Status,
    int Amount,
    string Currency,
    IReadOnlyDictionary<string, string>? Metadata,
    string? SourceToken);
