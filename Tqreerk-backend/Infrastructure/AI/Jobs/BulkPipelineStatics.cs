using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Taqreerk.Application.Settings;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Infrastructure.AI.Jobs;

/// <summary>
/// Pure static helpers shared by <see cref="BulkUploadItemJob"/> and
/// <see cref="BulkAdvanceItemJob"/>. No state, no DI dependencies.
/// </summary>
internal static class BulkPipelineStatics
{
    internal static string ToGcsUri(string? objectKey, FileStorageSettings storage)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
            throw new InvalidOperationException("Report has no FileUrl after upload.");
        if (objectKey.StartsWith("gs://", StringComparison.OrdinalIgnoreCase)) return objectKey;
        if (string.IsNullOrWhiteSpace(storage.GcsBucketName))
            throw new InvalidOperationException("GcsBucketName is not configured.");
        return $"gs://{storage.GcsBucketName}/{objectKey.TrimStart('/')}";
    }

    internal static string ToSlug(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var sb = new System.Text.StringBuilder(input.Length);
        foreach (var c in input.Trim().Normalize(System.Text.NormalizationForm.FormC))
            sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        var slug = sb.ToString().ToLowerInvariant();
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        if (slug.Length > 80) slug = slug[..80].TrimEnd('-');
        return slug;
    }

    internal static async Task<string> GenerateUniqueSlugAsync(
        TaqreerkDbContext db, string? title, CancellationToken ct)
    {
        var baseSlug = ToSlug(title ?? string.Empty);
        if (string.IsNullOrEmpty(baseSlug)) baseSlug = "report";
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var candidate = $"{baseSlug}-{Guid.NewGuid().ToString("N")[..6]}";
            var taken = await db.Reports.IgnoreQueryFilters()
                .AnyAsync(r => r.Slug == candidate, ct);
            if (!taken) return candidate;
        }
        return $"report-{Guid.NewGuid():N}"[..24];
    }

    internal static int? TryReadInt(string? raw, string key)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty(key, out var prop)) return null;
            return prop.ValueKind switch
            {
                JsonValueKind.Number when prop.TryGetInt32(out var i) => i,
                JsonValueKind.Number => (int)prop.GetDouble(),
                JsonValueKind.String when int.TryParse(prop.GetString(), out var p) => p,
                _ => null,
            };
        }
        catch (JsonException) { return null; }
    }

    internal static async Task CopySummaryToContentAsync(
        TaqreerkDbContext db, Report report, Guid jobId, string? rawOutput, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawOutput)) return;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(rawOutput); }
        catch (JsonException) { return; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            var summaryItems = ExtractStringArray(doc.RootElement, "summary");
            if (summaryItems.Count == 0) return;
            var summary     = JsonSerializer.Serialize(summaryItems);
            var keyFindings = JsonSerializer.Serialize(ExtractStringArray(doc.RootElement, "key_findings"));
            var topics      = JsonSerializer.Serialize(ExtractStringArray(doc.RootElement, "topics"));
            var indicators  = ExtractRawJsonArray(doc.RootElement, "indicators");
            var lang        = string.IsNullOrWhiteSpace(report.OriginalLanguage) ? "ar" : report.OriginalLanguage;
            var existing    = await db.ReportAiContents
                .FirstOrDefaultAsync(c => c.ReportId == report.Id && c.Language == lang, ct);
            if (existing is null)
            {
                db.ReportAiContents.Add(new ReportAiContent
                {
                    ReportId    = report.Id,
                    Language    = lang,
                    AiJobId     = jobId,
                    Summary     = summary,
                    KeyFindings = keyFindings,
                    Topics      = topics,
                    Indicators  = indicators,
                    GeneratedAt = DateTime.UtcNow,
                });
            }
            else
            {
                existing.Summary     = summary;
                existing.KeyFindings = keyFindings;
                existing.Topics      = topics;
                existing.Indicators  = indicators;
                existing.GeneratedAt = DateTime.UtcNow;
                existing.AiJobId     = jobId;
            }
        }
    }

    private static List<string> ExtractStringArray(JsonElement root, string key)
    {
        var list = new List<string>();
        if (!root.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
        return list;
    }

    private static string ExtractRawJsonArray(JsonElement root, string key)
        => root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Array
            ? el.GetRawText() : "[]";
}
