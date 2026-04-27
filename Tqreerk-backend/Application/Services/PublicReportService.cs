using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Reports;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class PublicReportService : IPublicReportService
{
    private readonly TaqreerkDbContext _db;
    private readonly IFileStorage _files;

    public PublicReportService(TaqreerkDbContext db, IFileStorage files)
    {
        _db = db;
        _files = files;
    }

    public async Task<PagedResult<PublicReportListItemDto>> ListAsync(
        PublicReportListRequest req,
        CancellationToken ct = default)
    {
        var page = Math.Max(1, req.Page);
        var pageSize = Math.Clamp(req.PageSize, 1, 50);

        var q = PublishedQuery();

        if (!string.IsNullOrWhiteSpace(req.Q))
        {
            var like = $"%{req.Q.Trim()}%";
            q = q.Where(r =>
                EF.Functions.ILike(r.Title, like)
                || (r.Description != null && EF.Functions.ILike(r.Description, like)));
        }

        if (req.Sectors is { Length: > 0 })
            q = q.Where(r => r.SectorId.HasValue && req.Sectors.Contains(r.SectorId.Value));

        if (req.Countries is { Length: > 0 })
            q = q.Where(r => r.CountryId.HasValue && req.Countries.Contains(r.CountryId.Value));

        if (req.YearFrom.HasValue)
            q = q.Where(r => r.PublicationYear >= req.YearFrom.Value);

        if (req.YearTo.HasValue)
            q = q.Where(r => r.PublicationYear <= req.YearTo.Value);

        if (!string.IsNullOrWhiteSpace(req.Language))
            q = q.Where(r => r.OriginalLanguage == req.Language);

        // Sort: relevance is a no-op for now (we'd need ts_rank against the
        // search vector). The other modes map to plain ORDER BY clauses.
        q = (req.Sort ?? "newest") switch
        {
            "popular" or "most_viewed" => q.OrderByDescending(r => r.ViewsCount),
            "rating" or "highest_rated" => q.OrderByDescending(r => r.AvgRating),
            "oldest" => q.OrderBy(r => r.CreatedAt),
            _ => q.OrderByDescending(r => r.CreatedAt),
        };

        var total = await q.CountAsync(ct);
        var rows = await ProjectListAsync(
            q.Skip((page - 1) * pageSize).Take(pageSize), ct);

        return new PagedResult<PublicReportListItemDto>(rows, total, page, pageSize);
    }

    public async Task<PublicReportDetailDto> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
            throw new KeyNotFoundException("Report not found.");

        // Pull a single row + its Arabic AI content (the source-language summary).
        // We project to an anonymous type rather than the entity so EF Core can
        // build a single SQL with the joins it needs and we don't accidentally
        // load nav properties we wouldn't expose.
        var row = await PublishedQuery()
            .Where(r => r.Slug == slug)
            .Select(r => new
            {
                r.Id,
                r.Slug,
                r.Title,
                r.Description,
                r.ReportType,
                r.OriginalLanguage,
                r.PublicationYear,
                r.PublicationDate,
                r.PageCount,
                r.FileUrl,
                r.CoverImageUrl,
                r.ViewsCount,
                r.DownloadsCount,
                r.AvgRating,
                r.RatingsCount,
                r.IsFeatured,
                r.OrganizationId,
                OrgNameAr = r.Organization.NameAr,
                OrgNameEn = r.Organization.NameEn,
                r.SectorId,
                SectorNameAr = r.Sector != null ? r.Sector.NameAr : null,
                r.CountryId,
                CountryNameAr = r.Country != null ? r.Country.NameAr : null,
                r.CreatedAt,
            })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Report not found.");

        // Public detail surfaces the AI summary in the source language only.
        // Translations have their own viewer flow; surfacing them here would
        // double-render content unnecessarily.
        var ai = await _db.ReportAiContents
            .AsNoTracking()
            .Where(c => c.ReportId == row.Id && c.Language == row.OriginalLanguage)
            .Select(c => new { c.Summary, c.KeyFindings, c.Indicators })
            .FirstOrDefaultAsync(ct);

        var fileUrl = await ResolveFileUrlAsync(row.FileUrl, ct);
        var coverUrl = await ResolveFileUrlAsync(row.CoverImageUrl, ct);

        return new PublicReportDetailDto(
            row.Id,
            row.Slug,
            row.Title,
            row.Description,
            row.ReportType,
            row.OriginalLanguage,
            row.PublicationYear,
            row.PublicationDate,
            row.PageCount,
            fileUrl,
            coverUrl,
            row.ViewsCount,
            row.DownloadsCount,
            row.AvgRating,
            row.RatingsCount,
            row.IsFeatured,
            row.OrganizationId,
            row.OrgNameAr,
            row.OrgNameEn,
            row.SectorId,
            row.SectorNameAr,
            row.CountryId,
            row.CountryNameAr,
            ai?.Summary,
            ParseJsonStringArray(ai?.KeyFindings),
            ParseJsonStringArray(ai?.Indicators),
            row.CreatedAt
        );
    }

    public async Task<IReadOnlyList<PublicReportListItemDto>> GetFeaturedAsync(int take = 5, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 20);
        var q = PublishedQuery()
            .Where(r => r.IsFeatured)
            .OrderByDescending(r => r.CreatedAt)
            .Take(take);
        return await ProjectListAsync(q, ct);
    }

    public async Task<IReadOnlyList<PublicReportListItemDto>> GetTrendingAsync(int take = 5, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 20);
        // "Trending" = views in the last 7 days. ReportView rows are written
        // each time someone opens a report; aggregating here is fine while the
        // table is small. When it grows we'll precompute via a snapshot job.
        var since = DateTime.UtcNow.AddDays(-7);

        var trendingIds = await _db.ReportViews
            .AsNoTracking()
            .Where(v => v.ViewedAt >= since)
            .GroupBy(v => v.ReportId)
            .Select(g => new { ReportId = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(take)
            .Select(x => x.ReportId)
            .ToListAsync(ct);

        if (trendingIds.Count == 0)
        {
            // No recent views — fall back to all-time most-viewed so the slot
            // never renders empty on a fresh deployment.
            var fallback = PublishedQuery()
                .OrderByDescending(r => r.ViewsCount)
                .Take(take);
            return await ProjectListAsync(fallback, ct);
        }

        // Preserve the trending order returned above. EF can't sort by an
        // arbitrary in-memory list directly; we re-sort after materialising.
        var rows = await ProjectListAsync(
            PublishedQuery().Where(r => trendingIds.Contains(r.Id)), ct);

        return rows
            .OrderBy(r => trendingIds.IndexOf(r.Id))
            .ToList();
    }

    public async Task<IReadOnlyList<PublicReportListItemDto>> GetRecentAsync(int take = 8, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 20);
        var q = PublishedQuery()
            .OrderByDescending(r => r.CreatedAt)
            .Take(take);
        return await ProjectListAsync(q, ct);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private IQueryable<Report> PublishedQuery() =>
        _db.Reports
           .AsNoTracking()
           .Where(r => r.Status == ReportStatus.Published);

    private async Task<List<PublicReportListItemDto>> ProjectListAsync(
        IQueryable<Report> query,
        CancellationToken ct)
    {
        // Project to a flat shape first so we don't carry nav properties
        // through the second pass; then sign each cover image URL.
        var raw = await query
            .Select(r => new
            {
                r.Id,
                r.Slug,
                r.Title,
                r.Description,
                r.ReportType,
                r.OriginalLanguage,
                r.PublicationYear,
                r.PageCount,
                r.ViewsCount,
                r.DownloadsCount,
                r.AvgRating,
                r.RatingsCount,
                r.IsFeatured,
                r.CoverImageUrl,
                r.OrganizationId,
                OrgNameAr = r.Organization.NameAr,
                OrgNameEn = r.Organization.NameEn,
                r.SectorId,
                SectorNameAr = r.Sector != null ? r.Sector.NameAr : null,
                r.CountryId,
                CountryNameAr = r.Country != null ? r.Country.NameAr : null,
                r.CreatedAt,
            })
            .ToListAsync(ct);

        var dtos = new List<PublicReportListItemDto>(raw.Count);
        foreach (var r in raw)
        {
            dtos.Add(new PublicReportListItemDto(
                r.Id,
                r.Slug,
                r.Title,
                r.Description,
                r.ReportType,
                r.OriginalLanguage,
                r.PublicationYear,
                r.PageCount,
                r.ViewsCount,
                r.DownloadsCount,
                r.AvgRating,
                r.RatingsCount,
                r.IsFeatured,
                await ResolveFileUrlAsync(r.CoverImageUrl, ct),
                r.OrganizationId,
                r.OrgNameAr,
                r.OrgNameEn,
                r.SectorId,
                r.SectorNameAr,
                r.CountryId,
                r.CountryNameAr,
                r.CreatedAt
            ));
        }
        return dtos;
    }

    /// Convert the stored object key into a signed HTTPS URL the browser can
    /// open. Best-effort: signing failures fall back to null so the page still
    /// renders without the file link.
    private async Task<string?> ResolveFileUrlAsync(string? objectKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(objectKey)) return null;
        try { return await _files.GetReadUrlAsync(objectKey, ct: ct); }
        catch { return null; }
    }

    private static IReadOnlyList<string> ParseJsonStringArray(string? jsonb)
    {
        if (string.IsNullOrWhiteSpace(jsonb)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(jsonb) ?? new List<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
