using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Reports;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;
using Taqreerk.Infrastructure.Storage;

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

        var q = ApplyFilters(PublishedQuery(), req, except: null);

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
                r.CoverImageBaseKey,
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
            .Select(c => new { c.Summary, c.KeyFindings, c.Topics, c.Indicators })
            .FirstOrDefaultAsync(ct);

        var fileUrl = await ResolveFileUrlAsync(row.FileUrl, ct);
        var (coverUrl, coverVariants) = await ResolveCoverAsync(row.CoverImageUrl, row.CoverImageBaseKey, ct);

        // Two cheap COUNTs to round out the header. Both are well-indexed
        // and their absence would force the SPA to fan out an extra
        // request per badge, which dwarfs the cost of computing them here.
        var commentCount = await _db.ReportComments
            .AsNoTracking()
            .CountAsync(c => c.ReportId == row.Id, ct);
        var recommendationCount = await _db.ReportRecommendations
            .AsNoTracking()
            .CountAsync(r => r.ReportId == row.Id, ct);

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
            coverVariants,
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
            ParseJsonStringArray(ai?.Summary),
            ParseJsonStringArray(ai?.KeyFindings),
            ParseJsonStringArray(ai?.Topics),
            ai?.Indicators,
            commentCount,
            recommendationCount,
            row.CreatedAt
        );
    }

    public async Task<IReadOnlyList<PublicReportListItemDto>> GetRelatedAsync(
        string slug, int take = 3, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 12);

        // Pull the source report's id + facets so we can match siblings.
        // We deliberately tolerate missing/unpublished slugs (returning
        // an empty list) to keep the public detail page resilient when
        // a report is unpublished mid-render.
        var src = await PublishedQuery()
            .Where(r => r.Slug == slug)
            .Select(r => new { r.Id, r.SectorId, r.CountryId })
            .FirstOrDefaultAsync(ct);
        if (src is null) return Array.Empty<PublicReportListItemDto>();

        var q = PublishedQuery().Where(r => r.Id != src.Id);

        // Match by sector first (tightest signal), then country, then any.
        // The frontend takes the first N regardless of which bucket they
        // come from — we just need to backfill cleanly.
        if (src.SectorId.HasValue)
        {
            var bySector = q.Where(r => r.SectorId == src.SectorId);
            var rows = await ProjectListAsync(
                bySector.OrderByDescending(r => r.ViewsCount).Take(take), ct);
            if (rows.Count >= take) return rows;

            // Fall through and top up from the country-only bucket
            // (or recent if neither). Use a list to keep stable order.
            var seen = rows.Select(x => x.Id).ToHashSet();
            var remaining = take - rows.Count;
            var fallback = await ProjectListAsync(
                q.Where(r => !seen.Contains(r.Id))
                 .OrderByDescending(r => r.ViewsCount)
                 .Take(remaining), ct);
            rows.AddRange(fallback);
            return rows;
        }

        // No sector on the source — fall back to view count overall.
        return await ProjectListAsync(
            q.OrderByDescending(r => r.ViewsCount).Take(take), ct);
    }

    public async Task<IReadOnlyList<PublicReportListItemDto>> GetFeaturedAsync(
        int take = 5, string? section = null, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 20);

        // Fall-through order: explicit section → HomepageHero → HomepageCarousel.
        // The hero rarely holds more than 1–3 picks, so when the caller
        // didn't ask for a specific column we top up from the carousel.
        var sections = new List<FeaturedSection>();
        if (section is not null
            && Enum.TryParse<FeaturedSection>(section, ignoreCase: true, out var parsed))
        {
            sections.Add(parsed);
        }
        else
        {
            sections.Add(FeaturedSection.HomepageHero);
            sections.Add(FeaturedSection.HomepageCarousel);
        }

        var now = DateTime.UtcNow;

        // Pull featured-report IDs in the requested section order, ordered
        // within each section by Position. Distinct() handles the rare case
        // of the same report being pinned to both fall-through sections.
        var featuredIds = new List<Guid>();
        foreach (var sec in sections)
        {
            if (featuredIds.Count >= take) break;

            var remaining = take - featuredIds.Count;
            var ids = await _db.FeaturedReports
                .AsNoTracking()
                .Where(f => f.Section == sec
                         && f.IsActive
                         && (f.FeaturedFrom == null || f.FeaturedFrom <= now)
                         && (f.FeaturedUntil == null || f.FeaturedUntil > now)
                         && f.Report.Status == ReportStatus.Published
                         && f.Report.DeletedAt == null)
                .OrderBy(f => f.Position)
                .Select(f => f.ReportId)
                .Take(remaining)
                .ToListAsync(ct);

            foreach (var id in ids)
                if (!featuredIds.Contains(id))
                    featuredIds.Add(id);
        }

        if (featuredIds.Count == 0)
            return Array.Empty<PublicReportListItemDto>();

        // Project against the published query, then re-sort to match the
        // featured order (EF can't ORDER BY an in-memory list).
        var rows = await ProjectListAsync(
            PublishedQuery().Where(r => featuredIds.Contains(r.Id)), ct);

        return rows
            .OrderBy(r => featuredIds.IndexOf(r.Id))
            .ToList();
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

    public async Task<PublicStatsOverviewDto> GetPublicStatsAsync(CancellationToken ct = default)
    {
        // Three independent indexed COUNTs — published reports, active orgs,
        // individual non-staff users. Each is well under 10 ms even with
        // millions of rows; serializing them keeps the EF connection happy
        // (parallel queries on a shared DbContext aren't supported).
        var publishedReports = await _db.Reports
            .AsNoTracking()
            .CountAsync(r => r.Status == ReportStatus.Published, ct);

        var activeOrgs = await _db.Organizations
            .AsNoTracking()
            .CountAsync(o => o.Status == OrganizationStatus.Active, ct);

        // "Individual readers" approximation: non-staff users that aren't a
        // member of any organization. Mirrors the userType derivation in
        // AdminUsersService so the two surfaces stay aligned.
        var individualReaders = await _db.Users
            .AsNoTracking()
            .CountAsync(u => !u.IsPlatformStaff && !u.OrganizationMemberships.Any(), ct);

        return new PublicStatsOverviewDto(publishedReports, activeOrgs, individualReaders);
    }

    public async Task<PublicReportFacetsDto> GetFacetsAsync(
        PublicReportListRequest req, CancellationToken ct = default)
    {
        // Counts respect every filter EXCEPT the dimension being computed.
        // Otherwise picking sector X drops every other sector to 0 and the
        // user gets stuck. Each facet runs as a single GROUP BY on an
        // already-filtered subquery — fast at our scale.

        // Sectors: filter by everything except sector selection.
        var sectorRows = await ApplyFilters(PublishedQuery(), req, except: FacetDim.Sectors)
            .Where(r => r.SectorId != null)
            .GroupBy(r => new { r.SectorId, r.Sector!.NameAr, r.Sector!.NameEn })
            .Select(g => new { Id = g.Key.SectorId!.Value, NameAr = g.Key.NameAr, NameEn = g.Key.NameEn, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync(ct);

        var countryRows = await ApplyFilters(PublishedQuery(), req, except: FacetDim.Countries)
            .Where(r => r.CountryId != null)
            .GroupBy(r => new { r.CountryId, r.Country!.NameAr, r.Country!.NameEn })
            .Select(g => new { Id = g.Key.CountryId!.Value, NameAr = g.Key.NameAr, NameEn = g.Key.NameEn, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync(ct);

        // Top 20 orgs to keep the sidebar list bounded. The org-search
        // input on the frontend filters this list locally.
        var orgRows = await ApplyFilters(PublishedQuery(), req, except: FacetDim.Organizations)
            .GroupBy(r => new { r.OrganizationId, r.Organization.NameAr, r.Organization.NameEn })
            .Select(g => new { Id = g.Key.OrganizationId, NameAr = g.Key.NameAr, NameEn = g.Key.NameEn, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(20)
            .ToListAsync(ct);

        // Languages are short codes (ar / en); we surface them with the
        // raw key as both Id and Name and let the SPA localize.
        var languageRows = await ApplyFilters(PublishedQuery(), req, except: FacetDim.Language)
            .GroupBy(r => r.OriginalLanguage)
            .Select(g => new { Lang = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync(ct);

        return new PublicReportFacetsDto(
            Sectors:       sectorRows.Select(x => new FacetItemDto(x.Id.ToString(), x.NameAr, x.NameEn ?? x.NameAr, x.Count)).ToList(),
            Countries:     countryRows.Select(x => new FacetItemDto(x.Id.ToString(), x.NameAr, x.NameEn ?? x.NameAr, x.Count)).ToList(),
            Organizations: orgRows.Select(x => new FacetItemDto(x.Id.ToString(), x.NameAr, x.NameEn ?? x.NameAr, x.Count)).ToList(),
            Languages:     languageRows.Select(x => new FacetItemDto(x.Lang, x.Lang, x.Lang, x.Count)).ToList());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// Names the facet currently being computed so ApplyFilters can skip
    /// the corresponding clause. Plain enum is enough — we don't need
    /// flags here, faceting is one dimension at a time.
    private enum FacetDim { Sectors, Countries, Organizations, Language }

    /// Compose every filter the public-list endpoint understands. `except`
    /// lets the facet pass skip the clause for the dimension it's about
    /// to GROUP BY. Sort + paging are NOT applied here — those are list-
    /// only concerns.
    private static IQueryable<Report> ApplyFilters(
        IQueryable<Report> q, PublicReportListRequest req, FacetDim? except)
    {
        if (!string.IsNullOrWhiteSpace(req.Q))
        {
            var like = $"%{req.Q.Trim()}%";
            q = q.Where(r =>
                EF.Functions.ILike(r.Title, like)
                || (r.Description != null && EF.Functions.ILike(r.Description, like)));
        }

        if (except != FacetDim.Sectors && req.Sectors is { Length: > 0 })
            q = q.Where(r => r.SectorId.HasValue && req.Sectors.Contains(r.SectorId.Value));

        if (except != FacetDim.Countries && req.Countries is { Length: > 0 })
            q = q.Where(r => r.CountryId.HasValue && req.Countries.Contains(r.CountryId.Value));

        if (except != FacetDim.Organizations && req.Organizations is { Length: > 0 })
            q = q.Where(r => req.Organizations.Contains(r.OrganizationId));

        if (req.YearFrom.HasValue)
            q = q.Where(r => r.PublicationYear >= req.YearFrom.Value);

        if (req.YearTo.HasValue)
            q = q.Where(r => r.PublicationYear <= req.YearTo.Value);

        if (req.PageCountMin.HasValue)
            q = q.Where(r => r.PageCount != null && r.PageCount >= req.PageCountMin.Value);

        if (req.PageCountMax.HasValue)
            q = q.Where(r => r.PageCount != null && r.PageCount <= req.PageCountMax.Value);

        if (except != FacetDim.Language && !string.IsNullOrWhiteSpace(req.Language))
            q = q.Where(r => r.OriginalLanguage == req.Language);

        return q;
    }

    private IQueryable<Report> PublishedQuery() =>
        _db.Reports
           .AsNoTracking()
           .Where(r => r.Status == ReportStatus.Published);

    private async Task<List<PublicReportListItemDto>> ProjectListAsync(
        IQueryable<Report> query,
        CancellationToken ct)
    {
        // Project to a flat shape first so we don't carry nav properties
        // through the second pass; then resolve cover URLs (variants vs
        // legacy signed) per row.
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
                r.CoverImageBaseKey,
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
            var (coverUrl, coverVariants) = await ResolveCoverAsync(r.CoverImageUrl, r.CoverImageBaseKey, ct);
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
                coverUrl,
                coverVariants,
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

    /// <summary>
    /// Pick the cover representation for a report:
    ///   • If <paramref name="coverImageBaseKey"/> is set the report has the
    ///     three-variant set on public GCS — emit signature-free URLs that
    ///     the browser will cache for a year, and surface the medium URL on
    ///     the legacy <c>coverImageUrl</c> field too so older clients keep
    ///     working with no DTO change.
    ///   • Otherwise fall back to signing the legacy single image.
    /// </summary>
    private async Task<(string? CoverImageUrl, CoverImagesDto? CoverImages)> ResolveCoverAsync(
        string? coverImageUrl, string? coverImageBaseKey, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(coverImageBaseKey))
        {
            var thumb = _files.GetPublicUrl($"{coverImageBaseKey}/{CoverImageVariants.ThumbName}");
            var medium = _files.GetPublicUrl($"{coverImageBaseKey}/{CoverImageVariants.MediumName}");
            var full = _files.GetPublicUrl($"{coverImageBaseKey}/{CoverImageVariants.FullName}");
            return (medium, new CoverImagesDto(thumb, medium, full));
        }
        return (await ResolveFileUrlAsync(coverImageUrl, ct), null);
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
