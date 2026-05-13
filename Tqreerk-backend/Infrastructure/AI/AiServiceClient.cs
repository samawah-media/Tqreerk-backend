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

    public async Task<IngestEnqueueResult> IngestAsync(Guid reportId, string fileUrl, CancellationToken ct = default)
    {
        var body = new { report_id = reportId, file_url = fileUrl };
        // Ingest now returns 202 with a job_id and runs in the background. We treat
        // 202 as a normal success and read the body for the job_id.
        var raw = await PostAsync("reports/ingest", body, ct);
        var dto = JsonSerializer.Deserialize<IngestEnqueueResponseDto>(raw, Json)
            ?? throw new InvalidOperationException("ai-service returned empty ingest response");
        if (dto.job_id == Guid.Empty)
            throw new InvalidOperationException("ai-service ingest response did not include a job_id");
        return new IngestEnqueueResult(dto.report_id, dto.job_id, dto.status ?? "Pending");
    }

    public async Task<AiJobStatusSnapshot> GetJobStatusAsync(Guid jobId, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"reports/jobs/{jobId}", ct);
        var content = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "ai-service GET /reports/jobs/{JobId} failed: {Status} {Body}",
                jobId, (int)resp.StatusCode, content);
            throw new InvalidOperationException(
                $"ai-service GET /reports/jobs/{jobId} failed with {(int)resp.StatusCode}: {content}");
        }

        var dto = JsonSerializer.Deserialize<JobStatusResponseDto>(content, Json)
            ?? throw new InvalidOperationException("ai-service returned empty job-status response");

        // output_data comes through as a JsonElement (jsonb on the wire). Flatten to
        // a dictionary so callers can pull "pages_processed" without re-parsing.
        IReadOnlyDictionary<string, object?>? outputData = null;
        if (dto.output_data.HasValue && dto.output_data.Value.ValueKind == JsonValueKind.Object)
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var p in dto.output_data.Value.EnumerateObject())
                dict[p.Name] = JsonElementToObject(p.Value);
            outputData = dict;
        }

        return new AiJobStatusSnapshot(
            dto.job_id, dto.report_id, dto.job_type ?? string.Empty,
            dto.status ?? string.Empty, dto.error_message, outputData);
    }

    public async Task<SummarizeResult> SummarizeAsync(Guid reportId, CancellationToken ct = default)
    {
        var body = new { report_id = reportId };
        var raw = await PostAsync("reports/summarize", body, ct);

        // Re-parse the raw response so we can extract `indicators` and `trends`
        // as raw JSON sub-trees — the strongly-typed DTO would force us to
        // declare every nested field Gemini might emit. The jsonb columns on
        // our side just want the original payload, so passing the raw text
        // through avoids a serialise-then-reshape round-trip.
        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("ai-service summarize response was not an object");

        var dto = JsonSerializer.Deserialize<SummarizeResponseDto>(raw, Json)
            ?? throw new InvalidOperationException("ai-service returned empty summarize response");

        return new SummarizeResult(
            dto.report_id,
            dto.summary ?? string.Empty,
            (IReadOnlyList<string>?)dto.key_findings ?? Array.Empty<string>(),
            (IReadOnlyList<string>?)dto.topics ?? Array.Empty<string>(),
            ExtractRawJsonArray(doc.RootElement, "indicators"),
            ExtractRawJsonArray(doc.RootElement, "trends")
        );
    }

    /// Returns the raw JSON text of the named array property, or "[]" when
    /// it's missing / not an array. Stays queryable by jsonb operators and
    /// never NULL so downstream callers don't need defensive checks.
    private static string ExtractRawJsonArray(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array)
            return "[]";
        return el.GetRawText();
    }

    public async Task<TranslateResult> TranslateAsync(
        Guid reportId, string fileUrl, string outputPrefix, CancellationToken ct = default)
    {
        // Body shape matches the new Python TranslateRequest exactly: just the
        // three fields. The service auto-detects the source language from
        // already-ingested page content and picks the target (Arabic ↔ English),
        // returning both on the response so we still get them.
        var body = new
        {
            report_id = reportId,
            file_url = fileUrl,
            output_prefix = outputPrefix,
        };
        var raw = await PostAsync("reports/translate", body, ct);
        var dto = JsonSerializer.Deserialize<TranslateResponseDto>(raw, Json)
            ?? throw new InvalidOperationException("ai-service returned empty translate response");
        return new TranslateResult(
            dto.report_id,
            dto.target_language ?? string.Empty,
            dto.source_language ?? string.Empty,
            dto.translated_file_url ?? string.Empty
        );
    }

    public async Task<CompareResult> CompareAsync(IReadOnlyList<Guid> reportIds, CancellationToken ct = default)
    {
        // The Python service mounts every endpoint under /api/ai/* — and the
        // configured BaseUrl already ends with /api/ai (see AiServiceSettings),
        // so the relative path here is just `reports/compare`. Same pattern
        // ingest/summarize/translate use; passing /api/ai again here would
        // double-prefix and 404.
        var body = new { report_ids = reportIds };
        var raw = await PostAsync("reports/compare", body, ct);

        // Re-parse the raw response so we can split it into "similarities"
        // (strongly typed) and the qualitative block (raw JSON) without
        // pinning a DTO to Gemini's evolving structured-output schema.
        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("ai-service compare response was not an object");

        var similarities = new List<CompareSimilarityPair>();
        if (doc.RootElement.TryGetProperty("similarities", out var simEl) && simEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in simEl.EnumerateArray())
            {
                // Python service is permissive about which key holds the score;
                // accept the common variants so a back-end rename doesn't break
                // us silently.
                var score = p.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0d;
                var idAStr = p.TryGetProperty("report_id_a", out var a) ? a.GetString() : null;
                var idBStr = p.TryGetProperty("report_id_b", out var b) ? b.GetString() : null;
                if (Guid.TryParse(idAStr, out var idA) && Guid.TryParse(idBStr, out var idB))
                    similarities.Add(new CompareSimilarityPair(idA, idB, score));
            }
        }

        // Qualitative block — wrap whatever Gemini returned (common_topics,
        // key_differences, shared_indicators, etc.) into one JSON object the
        // frontend can render directly. If the AI service changes shape, the
        // wire surface stays stable on our side because we pass the bytes
        // through.
        string qualitativeJson = "{}";
        if (doc.RootElement.TryGetProperty("qualitative", out var qual) && qual.ValueKind == JsonValueKind.Object)
        {
            qualitativeJson = qual.GetRawText();
        }
        else
        {
            // Some Python builds return the qualitative fields at the root.
            // Re-emit only the non-similarities keys so the persisted JSON
            // stays a clean object.
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.NameEquals("similarities") || p.NameEquals("report_ids")) continue;
                    p.WriteTo(w);
                }
                w.WriteEndObject();
            }
            qualitativeJson = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }

        return new CompareResult(reportIds, similarities, qualitativeJson);
    }

    public async Task<IReadOnlyList<BulkJobResult>> BulkIngestAsync(
        IReadOnlyList<BulkIngestItem> items, CancellationToken ct = default)
    {
        // Python expects { "items": [{ "report_id": "...", "file_url": "..." }, ...] }
        var body = new
        {
            items = items.Select(i => new { report_id = i.ReportId, file_url = i.FileUrl }).ToArray(),
        };
        var raw = await PostAsync("reports/bulk/ingest", body, ct);
        return ParseBulkJobsResponse(raw);
    }

    public async Task<IReadOnlyList<BulkJobResult>> BulkSummarizeAsync(
        IReadOnlyList<Guid> reportIds, CancellationToken ct = default)
    {
        var body = new
        {
            items = reportIds.Select(id => new { report_id = id }).ToArray(),
        };
        var raw = await PostAsync("reports/bulk/summarize", body, ct);
        return ParseBulkJobsResponse(raw);
    }

    /// Both /bulk/ingest and /bulk/summarize return the same {"jobs":[{job_id, report_id}, ...]}
    /// shape. Centralised here so a Python-side rename touches one parser instead of two.
    private static IReadOnlyList<BulkJobResult> ParseBulkJobsResponse(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("jobs", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<BulkJobResult>();
        }

        var results = new List<BulkJobResult>(arr.GetArrayLength());
        foreach (var p in arr.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object) continue;
            var jobIdStr = p.TryGetProperty("job_id", out var j) ? j.GetString() : null;
            var reportIdStr = p.TryGetProperty("report_id", out var r) ? r.GetString() : null;
            if (Guid.TryParse(jobIdStr, out var jobId) && Guid.TryParse(reportIdStr, out var reportId))
                results.Add(new BulkJobResult(jobId, reportId));
        }
        return results;
    }

    private async Task<string> PostAsync<TBody>(string path, TBody body, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        _logger.LogInformation("[ai-client] POST {BaseUrl}{Path}", _http.BaseAddress, path);
        using var resp = await _http.PostAsJsonAsync(path, body, Json, ct);
        var content = await resp.Content.ReadAsStringAsync(ct);
        var elapsedMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "[ai-client] POST {Path} -> {Status} ({Elapsed}ms) body={Body}",
                path, (int)resp.StatusCode, elapsedMs, content);
            throw new InvalidOperationException(
                $"ai-service POST /{path} failed with {(int)resp.StatusCode}: {content}");
        }
        _logger.LogInformation(
            "[ai-client] POST {Path} -> {Status} ({Elapsed}ms)",
            path, (int)resp.StatusCode, elapsedMs);
        return content;
    }

    /// Convert a JsonElement to a CLR primitive / nested structure suitable for
    /// stuffing into a Dictionary<string, object?>. Used to flatten the AI
    /// service's output_data payload (which is jsonb on the wire).
    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToObject).ToList(),
        JsonValueKind.Object => el.EnumerateObject().ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
        _ => null,
    };

    // The DTOs use snake_case property names (matching the JSON wire format) and
    // are intentionally permissive — fields stay nullable so an extra/missing
    // field on either end doesn't crash deserialisation.

#pragma warning disable IDE1006 // suppress lowercase naming for wire-DTOs
    private sealed record IngestEnqueueResponseDto(Guid report_id, Guid job_id, string? status);

    private sealed record JobStatusResponseDto(
        Guid job_id,
        Guid? report_id,
        string? job_type,
        string? status,
        string? error_message,
        JsonElement? output_data,
        string? started_at,
        string? completed_at);

    private sealed record SummarizeResponseDto(
        Guid report_id, string? summary, List<string>? key_findings, List<string>? topics);

    private sealed record TranslateResponseDto(
        Guid report_id, string? target_language, string? source_language, string? translated_file_url);
#pragma warning restore IDE1006
}
