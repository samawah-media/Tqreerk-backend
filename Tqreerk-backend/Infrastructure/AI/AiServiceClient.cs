using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Settings;

namespace Taqreerk.Infrastructure.AI;

public class AiServiceClient : IAiServiceClient
{
    private readonly HttpClient _http;
    private readonly ILogger<AiServiceClient> _logger;

    /// snake_case property names match the FastAPI Pydantic models.
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AiServiceClient(HttpClient http, IOptions<AiServiceSettings> settings, ILogger<AiServiceClient> logger)
    {
        _http = http;
        _logger = logger;

        var baseUrl = settings.Value.BaseUrl?.TrimEnd('/')
            ?? throw new InvalidOperationException("AiService:BaseUrl is required.");
        // BaseAddress expects a trailing slash so relative routes resolve correctly.
        _http.BaseAddress = new Uri(baseUrl + "/");
        _http.Timeout = TimeSpan.FromSeconds(settings.Value.TimeoutSeconds);
    }

    public async Task<IngestResult> IngestAsync(Guid reportId, string fileUrl, CancellationToken ct = default)
    {
        var body = new { report_id = reportId, file_url = fileUrl };
        var raw = await PostAsync("reports/ingest", body, ct);
        var dto = JsonSerializer.Deserialize<IngestResponseDto>(raw, Json)
            ?? throw new InvalidOperationException("ai-service returned empty ingest response");
        return new IngestResult(dto.report_id, dto.pages_processed, dto.status ?? "ok");
    }

    public async Task<SummarizeResult> SummarizeAsync(Guid reportId, CancellationToken ct = default)
    {
        var body = new { report_id = reportId };
        var raw = await PostAsync("reports/summarize", body, ct);
        var dto = JsonSerializer.Deserialize<SummarizeResponseDto>(raw, Json)
            ?? throw new InvalidOperationException("ai-service returned empty summarize response");
        return new SummarizeResult(
            dto.report_id,
            dto.summary ?? string.Empty,
            (IReadOnlyList<string>?)dto.key_findings ?? Array.Empty<string>(),
            (IReadOnlyList<string>?)dto.topics ?? Array.Empty<string>()
        );
    }

    public async Task<TranslateResult> TranslateAsync(
        Guid reportId, string fileUrl, string outputPrefix,
        string targetLanguage, string sourceLanguage, CancellationToken ct = default)
    {
        var body = new
        {
            report_id = reportId,
            file_url = fileUrl,
            output_prefix = outputPrefix,
            target_language = targetLanguage,
            source_language = sourceLanguage,
        };
        var raw = await PostAsync("reports/translate", body, ct);
        var dto = JsonSerializer.Deserialize<TranslateResponseDto>(raw, Json)
            ?? throw new InvalidOperationException("ai-service returned empty translate response");
        return new TranslateResult(
            dto.report_id,
            dto.target_language ?? targetLanguage,
            dto.source_language ?? sourceLanguage,
            dto.translated_file_url ?? string.Empty
        );
    }

    private async Task<string> PostAsync<TBody>(string path, TBody body, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync(path, body, Json, ct);
        var content = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "ai-service POST {Path} failed: {Status} {Body}",
                path, (int)resp.StatusCode, content);
            throw new InvalidOperationException(
                $"ai-service POST /{path} failed with {(int)resp.StatusCode}: {content}");
        }
        return content;
    }

    // The DTOs use snake_case property names (matching the JSON wire format) and
    // are intentionally permissive — fields stay nullable so an extra/missing
    // field on either end doesn't crash deserialisation.

#pragma warning disable IDE1006 // suppress lowercase naming for wire-DTOs
    private sealed record IngestResponseDto(Guid report_id, int pages_processed, string? status);

    private sealed record SummarizeResponseDto(
        Guid report_id, string? summary, List<string>? key_findings, List<string>? topics);

    private sealed record TranslateResponseDto(
        Guid report_id, string? target_language, string? source_language, string? translated_file_url);
#pragma warning restore IDE1006
}
