using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Compare;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class CompareService : ICompareService
{
    private const int MinReports = 2;
    private const int MaxReports = 4;

    private readonly TaqreerkDbContext _db;
    private readonly IAiServiceClient _ai;
    private readonly IFileStorage _files;

    public CompareService(TaqreerkDbContext db, IAiServiceClient ai, IFileStorage files)
    {
        _db = db;
        _ai = ai;
        _files = files;
    }

    public async Task<ComparisonResultDto> CompareAsync(
        Guid userId, IReadOnlyList<Guid> reportIds, CancellationToken ct = default)
    {
        if (reportIds is null || reportIds.Count < MinReports || reportIds.Count > MaxReports)
            throw new ArgumentException($"يجب اختيار من {MinReports} إلى {MaxReports} تقارير للمقارنة.");

        // De-dupe + sort canonical so the cache key is order-insensitive.
        // Two users picking the same reports in different orders share one
        // ReportComparison row.
        var distinctIds = reportIds.Distinct().OrderBy(id => id).ToList();
        if (distinctIds.Count < MinReports)
            throw new ArgumentException("يجب اختيار تقريرين مختلفين على الأقل.");

        // Validate that every report exists, is published, and has been
        // summarized (the AI service can't compare without a summary).
        // Soft-deleted reports get filtered out automatically by the
        // global query filter on Report.
        var reports = await _db.Reports
            .AsNoTracking()
            .Where(r => distinctIds.Contains(r.Id))
            .Select(r => new
            {
                r.Id,
                r.Title,
                r.Slug,
                r.CoverImageUrl,
                r.Status,
                r.PublicationYear,
                OrganizationNameAr = r.Organization != null ? r.Organization.NameAr : null,
                SectorNameAr = r.Sector != null ? r.Sector.NameAr : null,
            })
            .ToListAsync(ct);

        if (reports.Count != distinctIds.Count)
            throw new KeyNotFoundException("بعض التقارير المختارة غير موجودة أو غير متاحة.");

        var notPublished = reports.Where(r => r.Status != ReportStatus.Published).ToList();
        if (notPublished.Count > 0)
            throw new InvalidOperationException("لا يمكن مقارنة تقارير غير منشورة.");

        // Cache lookup. We persist the canonical sorted id array as JSON
        // and key on it — same user, same sorted ids → one row reused.
        // The cache key is also hashed for the metadata column so we can
        // index it later if the table grows; for now we just match on
        // the JSON body via a string equality check.
        var canonicalJson = JsonSerializer.Serialize(distinctIds);

        var cached = await _db.ReportComparisons
            .AsNoTracking()
            .Where(c => c.UserId == userId && c.ReportIds == canonicalJson && c.ComparisonResult != null)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (cached is not null)
        {
            return await BuildDtoAsync(cached, reports, distinctIds, ct);
        }

        // Cache miss → call the Python ai-service. The freemium gate
        // (applied at the controller via [EnforceUsageLimit]) has
        // already verified the cap; the consume side fires after a
        // 2xx response, so an AI failure here doesn't burn the user's
        // monthly allowance.
        var aiResult = await _ai.CompareAsync(distinctIds, ct);

        // Persist. The ComparisonResult column carries the full
        // qualitative JSON; SimilarityScore stores the maximum pair
        // score for quick "how similar were they overall" lookups.
        var maxScore = aiResult.Similarities.Count == 0 ? (decimal?)null
            : (decimal?)aiResult.Similarities.Max(s => s.Score);

        var entity = new ReportComparison
        {
            UserId = userId,
            ReportIds = canonicalJson,
            ComparisonResult = BuildPersistedJson(aiResult),
            SimilarityScore = maxScore,
        };
        _db.ReportComparisons.Add(entity);
        await _db.SaveChangesAsync(ct);

        return await BuildDtoAsync(entity, reports, distinctIds, ct);
    }

    public async Task<IReadOnlyList<ComparisonListItemDto>> ListMineAsync(
        Guid userId, int take = 20, CancellationToken ct = default)
    {
        if (take < 1) take = 1;
        if (take > 50) take = 50;

        var rows = await _db.ReportComparisons
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.CreatedAt)
            .Take(take)
            .Select(c => new { c.Id, c.CreatedAt, c.ReportIds })
            .ToListAsync(ct);

        if (rows.Count == 0) return Array.Empty<ComparisonListItemDto>();

        // Hydrate report titles in one round-trip across all comparisons
        // in the page. We expand the JSON arrays to a flat distinct id
        // set, query reports.Title once, then map back.
        var allIds = new HashSet<Guid>();
        var perRowIds = new Dictionary<Guid, List<Guid>>();
        foreach (var row in rows)
        {
            var ids = TryParseIds(row.ReportIds) ?? new List<Guid>();
            perRowIds[row.Id] = ids;
            foreach (var id in ids) allIds.Add(id);
        }

        var titles = await _db.Reports
            .AsNoTracking()
            .Where(r => allIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Title })
            .ToDictionaryAsync(r => r.Id, r => r.Title, ct);

        return rows.Select(row =>
        {
            var ids = perRowIds[row.Id];
            var rowTitles = ids
                .Select(id => titles.TryGetValue(id, out var t) ? t : "(تقرير محذوف)")
                .ToList();
            return new ComparisonListItemDto(row.Id, row.CreatedAt, ids.Count, rowTitles);
        }).ToList();
    }

    public async Task<ComparisonResultDto> GetMineAsync(
        Guid userId, Guid comparisonId, CancellationToken ct = default)
    {
        var entity = await _db.ReportComparisons
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == comparisonId && c.UserId == userId, ct)
            ?? throw new KeyNotFoundException("Comparison not found.");

        var ids = TryParseIds(entity.ReportIds) ?? new List<Guid>();

        var reports = await _db.Reports
            .AsNoTracking()
            .Where(r => ids.Contains(r.Id))
            .Select(r => new
            {
                r.Id,
                r.Title,
                r.Slug,
                r.CoverImageUrl,
                r.Status,
                r.PublicationYear,
                OrganizationNameAr = r.Organization != null ? r.Organization.NameAr : null,
                SectorNameAr = r.Sector != null ? r.Sector.NameAr : null,
            })
            .ToListAsync(ct);

        return await BuildDtoAsync(entity, reports.Cast<dynamic>().ToList(), ids, ct);
    }

    private async Task<ComparisonResultDto> BuildDtoAsync(
        ReportComparison entity,
        IReadOnlyList<dynamic> reportRows,
        IReadOnlyList<Guid> sortedIds,
        CancellationToken ct)
    {
        // Re-parse the persisted blob so similarities + qualitative come
        // out in the rich shape the frontend wants.
        var parsed = ParsePersistedJson(entity.ComparisonResult);

        // Hydrate per-report metadata (summary + key findings) from the
        // AI content cache. Falls back to empty when a report has no
        // ai_content row yet — shouldn't happen post-publish but stays
        // defensive.
        var byReport = reportRows.ToDictionary(r => (Guid)r.Id, r => r);

        // Pull AI summaries / key findings in one go. We prefer Arabic
        // content; English is a fallback for English-language reports.
        var aiContent = _db.ReportAiContents
            .AsNoTracking()
            .Where(c => sortedIds.Contains(c.ReportId))
            .OrderBy(c => c.Language == "ar" ? 0 : 1)
            .ToList()
            .GroupBy(c => c.ReportId)
            .ToDictionary(g => g.Key, g => g.First());

        // Resolve cover image keys to short-lived signed URLs. The DB
        // stores GCS object keys (e.g. "taqreerk-uploads-dev/covers/..");
        // raw keys won't load in an <img src> on the frontend. Best-effort —
        // a sign failure leaves coverImageUrl null and the UI shows the
        // placeholder.
        var coverKeys = reportRows
            .Select(r => (string?)r.CoverImageUrl)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct()
            .ToList();
        var resolvedCovers = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var key in coverKeys)
        {
            try { resolvedCovers[key!] = await _files.GetReadUrlAsync(key!, ct: ct); }
            catch { resolvedCovers[key!] = null; }
        }

        var reportDtos = sortedIds
            .Where(id => byReport.ContainsKey(id))
            .Select(id =>
            {
                var r = byReport[id];
                var content = aiContent.TryGetValue(id, out var c) ? c : null;
                IReadOnlyList<string> findings = Array.Empty<string>();
                if (!string.IsNullOrWhiteSpace(content?.KeyFindings))
                {
                    try
                    {
                        var arr = JsonSerializer.Deserialize<List<string>>(content!.KeyFindings!);
                        if (arr is not null) findings = arr;
                    }
                    catch { /* malformed JSON — treat as empty */ }
                }

                var rawCover = (string?)r.CoverImageUrl;
                var coverUrl = !string.IsNullOrWhiteSpace(rawCover) && resolvedCovers.TryGetValue(rawCover!, out var url)
                    ? url
                    : null;

                return new ComparedReportDto(
                    (Guid)r.Id,
                    (string)r.Title,
                    (string)r.Slug,
                    coverUrl,
                    (string?)r.OrganizationNameAr,
                    (int?)r.PublicationYear,
                    (string?)r.SectorNameAr,
                    content?.Summary,
                    findings);
            })
            .ToList();

        return new ComparisonResultDto(
            entity.Id,
            entity.CreatedAt,
            reportDtos,
            parsed.Similarities,
            parsed.QualitativeJson);
    }

    /// Persist similarities + qualitative as a single JSON blob so we
    /// can pull them out together on cache hit. Shape mirrors what we
    /// return on the wire so the round-trip is cheap.
    private static string BuildPersistedJson(CompareResult ai)
    {
        var sims = ai.Similarities
            .Select(s => new SimilarityPairDto(s.ReportIdA, s.ReportIdB, s.Score))
            .ToList();
        var payload = new
        {
            similarities = sims,
            qualitative = ai.QualitativeJson, // already a JSON object as text
        };
        return JsonSerializer.Serialize(payload);
    }

    private static (IReadOnlyList<SimilarityPairDto> Similarities, string QualitativeJson) ParsePersistedJson(string? blob)
    {
        if (string.IsNullOrWhiteSpace(blob))
            return (Array.Empty<SimilarityPairDto>(), "{}");

        try
        {
            using var doc = JsonDocument.Parse(blob);
            var sims = new List<SimilarityPairDto>();
            if (doc.RootElement.TryGetProperty("similarities", out var simEl) && simEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in simEl.EnumerateArray())
                {
                    var dto = JsonSerializer.Deserialize<SimilarityPairDto>(p.GetRawText());
                    if (dto is not null) sims.Add(dto);
                }
            }

            string qualitative = "{}";
            if (doc.RootElement.TryGetProperty("qualitative", out var qual))
            {
                // Persisted as a string holding a JSON object — parse out
                // the embedded JSON so the wire payload stays a JSON
                // object (not a string).
                qualitative = qual.ValueKind == JsonValueKind.String
                    ? qual.GetString() ?? "{}"
                    : qual.GetRawText();
            }

            return (sims, qualitative);
        }
        catch
        {
            return (Array.Empty<SimilarityPairDto>(), "{}");
        }
    }

    private static List<Guid>? TryParseIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<List<Guid>>(json!); }
        catch { return null; }
    }
}
