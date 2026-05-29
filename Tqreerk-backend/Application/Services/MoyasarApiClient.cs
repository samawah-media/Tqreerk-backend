using System.Net.Http.Headers;
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

        string? token = null;
        if (root.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object)
        {
            if (source.TryGetProperty("token", out var tok) && tok.ValueKind == JsonValueKind.String)
                token = tok.GetString();
        }

        return new MoyasarPaymentDto(id, status, amount, currency, metadata, token);
    }
}
