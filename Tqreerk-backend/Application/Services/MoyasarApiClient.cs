using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Settings;

namespace Taqreerk.Application.Services;

public class MoyasarApiClient : IMoyasarApiClient
{
    private readonly HttpClient _http;
    private readonly MoyasarSettings _settings;

    public MoyasarApiClient(HttpClient http, IOptions<MoyasarSettings> settings)
    {
        _http = http;
        _settings = settings.Value;
        _http.BaseAddress = new Uri("https://api.moyasar.com/v1/");
    }

    public async Task<MoyasarPaymentDto?> GetPaymentAsync(string moyasarPaymentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.SecretKey))
            return null;

        using var req = new HttpRequestMessage(HttpMethod.Get, $"payments/{moyasarPaymentId}");
        req.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{_settings.SecretKey}:")));

        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            return null;

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return ParsePayment(doc.RootElement);
    }

    public async Task<MoyasarPaymentDto?> CreateTokenPaymentAsync(
        Guid paymentId,
        int amountHalalas,
        string currency,
        string description,
        string callbackUrl,
        string cardToken,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.SecretKey))
            return null;

        var payload = new Dictionary<string, object?>
        {
            ["given_id"] = paymentId.ToString(),
            ["amount"] = amountHalalas,
            ["currency"] = currency,
            ["description"] = description,
            ["callback_url"] = callbackUrl,
            ["source"] = new Dictionary<string, string>
            {
                ["type"] = "token",
                ["token"] = cardToken,
            },
            ["metadata"] = metadata,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "payments")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_settings.SecretKey}:")));

        using var res = await _http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            return ParsePayment(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<MoyasarPaymentDto> RefundPaymentAsync(
        string moyasarPaymentId,
        int? amountHalalas = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.SecretKey))
            throw new InvalidOperationException("Moyasar SecretKey is not configured.");

        if (string.IsNullOrWhiteSpace(moyasarPaymentId))
            throw new ArgumentException("Moyasar payment id is required.", nameof(moyasarPaymentId));

        using var req = new HttpRequestMessage(HttpMethod.Post, $"payments/{moyasarPaymentId}/refund");
        if (amountHalalas is > 0)
        {
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { amount = amountHalalas.Value }),
                Encoding.UTF8,
                "application/json");
        }

        req.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_settings.SecretKey}:")));

        using var res = await _http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Moyasar refund failed ({(int)res.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var parsed = ParsePayment(doc.RootElement)
            ?? throw new InvalidOperationException("Moyasar refund returned an unparseable payload.");

        return parsed;
    }

    internal static MoyasarPaymentDto? ParsePayment(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(id))
            return null;

        var status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
        var amount = root.TryGetProperty("amount", out var am) ? am.GetInt32() : 0;
        var currency = root.TryGetProperty("currency", out var cur) ? cur.GetString() ?? "SAR" : "SAR";

        Dictionary<string, string>? metadata = null;
        if (root.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in meta.EnumerateObject())
            {
                metadata[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => prop.Value.GetRawText(),
                };
            }
        }

        var sourceToken = ExtractSourceToken(root);

        return new MoyasarPaymentDto(id, status, amount, currency, metadata, sourceToken);
    }

    /// <summary>
    /// Card token for recurring charges (token_xxx). Present when the payment form
    /// used credit_card.save_card and tokenization is enabled on the Moyasar account.
    /// STC Pay payments do not produce a card token.
    /// </summary>
    internal static string? ExtractSourceToken(JsonElement root)
    {
        if (!root.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object)
            return null;

        var fromTokenField = ReadTokenString(source, "token");
        if (fromTokenField is not null)
            return fromTokenField;

        var sourceType = source.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
            ? typeEl.GetString()
            : null;

        // Recurring charge source: { type: "token", token: "token_xxx" } or id holds token_xxx.
        if (string.Equals(sourceType, "token", StringComparison.OrdinalIgnoreCase))
        {
            var nested = ReadTokenString(source, "id");
            if (nested is not null)
                return nested;
        }

        // After save_card on creditcard payment, token may appear only on source.id.
        if (string.Equals(sourceType, "creditcard", StringComparison.OrdinalIgnoreCase))
        {
            var idAsToken = ReadTokenString(source, "id");
            if (idAsToken is not null)
                return idAsToken;
        }

        return null;
    }

    private static string? ReadTokenString(JsonElement source, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var el) || el.ValueKind != JsonValueKind.String)
            return null;

        var value = el.GetString();
        return IsMoyasarCardToken(value) ? value : null;
    }

    private static bool IsMoyasarCardToken(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.StartsWith("token_", StringComparison.Ordinal);
}
