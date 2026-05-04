using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Me;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class MeService : IMeService
{
    private readonly TaqreerkDbContext _db;
    private readonly IFileStorage _files;

    public MeService(TaqreerkDbContext db, IFileStorage files)
    {
        _db = db;
        _files = files;
    }

    public async Task<IReadOnlyList<MySavedReportDto>> ListSavedReportsAsync(
        Guid userId, int take = 20, CancellationToken ct = default)
    {
        if (take < 1) take = 1;
        if (take > 100) take = 100;

        // Newest saves first. Soft-deleted reports are filtered by the
        // global query filter on Report — they won't appear here even if
        // the saved_reports row still exists.
        var rows = await _db.SavedReports
            .AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.SavedAt)
            .Take(take)
            .Select(s => new
            {
                s.Report.Id,
                s.Report.Title,
                s.Report.Slug,
                s.Report.CoverImageUrl,
                SectorNameAr = s.Report.Sector != null ? s.Report.Sector.NameAr : null,
                CountryNameAr = s.Report.Country != null ? s.Report.Country.NameAr : null,
                s.Report.PublicationYear,
                s.Report.PageCount,
                s.Report.ViewsCount,
                OrganizationNameAr = s.Report.Organization != null ? s.Report.Organization.NameAr : null,
                OrganizationLogoUrl = s.Report.Organization != null ? s.Report.Organization.LogoUrl : null,
                s.SavedAt,
            })
            .ToListAsync(ct);

        // Resolve cover + logo object keys to short-lived signed HTTPS
        // URLs the browser can render. Best-effort: a sign failure on
        // one row drops that single image to null and the card falls
        // back to the gradient placeholder.
        var dtos = new List<MySavedReportDto>(rows.Count);
        foreach (var r in rows)
        {
            var cover = await ResolveAsync(r.CoverImageUrl, ct);
            var logo = await ResolveAsync(r.OrganizationLogoUrl, ct);
            dtos.Add(new MySavedReportDto(
                r.Id, r.Title, r.Slug, cover,
                r.SectorNameAr, r.CountryNameAr,
                r.PublicationYear, r.PageCount, r.ViewsCount,
                r.OrganizationNameAr, logo,
                r.SavedAt));
        }
        return dtos;
    }

    private async Task<string?> ResolveAsync(string? objectKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(objectKey)) return null;
        try { return await _files.GetReadUrlAsync(objectKey, ct: ct); }
        catch { return null; }
    }

    public async Task<IReadOnlyList<MySavedReportDto>> ListRecommendationsAsync(
        Guid userId, int take = 20, CancellationToken ct = default)
    {
        if (take < 1) take = 1;
        if (take > 50) take = 50;

        // Pull the user's sector interests up front. No interests → no
        // recommendations: returning empty here keeps the SQL plan simple
        // and lets the frontend render its empty state without a special
        // "no interests" signal — the empty list is the signal.
        var interestSectorIds = await _db.UserInterests
            .AsNoTracking()
            .Where(i => i.UserId == userId && i.SectorId != null)
            .Select(i => i.SectorId!.Value)
            .Distinct()
            .ToListAsync(ct);

        if (interestSectorIds.Count == 0)
            return Array.Empty<MySavedReportDto>();

        // Reports the user has already saved are excluded — they're
        // already in /my/library and don't need re-recommending. We
        // intentionally DO NOT exclude viewed reports: a quick read
        // doesn't mean the user wants the report off their feed, and
        // surfacing it again is a useful nudge to save for later.
        var savedReportIds = _db.SavedReports
            .AsNoTracking()
            .Where(s => s.UserId == userId)
            .Select(s => s.ReportId);

        var rows = await _db.Reports
            .AsNoTracking()
            .Where(r =>
                r.Status == ReportStatus.Published &&
                r.SectorId != null &&
                interestSectorIds.Contains(r.SectorId.Value) &&
                !savedReportIds.Contains(r.Id))
            // Highest-rated first, then most-popular as the tiebreaker.
            // Reports with no ratings yet (AvgRating = 0) drop to the
            // bottom — that's intentional, we'd rather surface vetted
            // content first.
            .OrderByDescending(r => r.AvgRating)
            .ThenByDescending(r => r.ViewsCount)
            .Take(take)
            .Select(r => new
            {
                r.Id,
                r.Title,
                r.Slug,
                r.CoverImageUrl,
                SectorNameAr = r.Sector != null ? r.Sector.NameAr : null,
                CountryNameAr = r.Country != null ? r.Country.NameAr : null,
                r.PublicationYear,
                r.PageCount,
                r.ViewsCount,
                OrganizationNameAr = r.Organization != null ? r.Organization.NameAr : null,
                OrganizationLogoUrl = r.Organization != null ? r.Organization.LogoUrl : null,
                // Reuse MySavedReportDto for the response shape — there's
                // no SavedAt on a never-saved report, so we fall back to
                // PublishedAt (or CreatedAt) for the timestamp slot. The
                // dashboard sort isn't by this column so the surrogate is
                // harmless.
                Stamp = r.PublishedAt ?? r.CreatedAt,
            })
            .ToListAsync(ct);

        var dtos = new List<MySavedReportDto>(rows.Count);
        foreach (var r in rows)
        {
            var cover = await ResolveAsync(r.CoverImageUrl, ct);
            var logo = await ResolveAsync(r.OrganizationLogoUrl, ct);
            dtos.Add(new MySavedReportDto(
                r.Id, r.Title, r.Slug, cover,
                r.SectorNameAr, r.CountryNameAr,
                r.PublicationYear, r.PageCount, r.ViewsCount,
                r.OrganizationNameAr, logo,
                r.Stamp));
        }
        return dtos;
    }

    public async Task<IReadOnlyList<MyActivityItemDto>> ListActivityAsync(
        Guid userId, int take = 10, CancellationToken ct = default)
    {
        if (take < 1) take = 1;
        if (take > 50) take = 50;

        // Left-join with reports so rows whose ResourceId no longer
        // resolves (deleted/unpublished report) still render — the
        // frontend falls back to the action type alone.
        var query =
            from u in _db.UsageTracking.AsNoTracking()
            where u.UserId == userId
            orderby u.ConsumedAt descending
            join r in _db.Reports.AsNoTracking()
                on u.ResourceId equals (Guid?)r.Id into rj
            from r in rj.DefaultIfEmpty()
            select new MyActivityItemDto(
                u.Id,
                u.ActionType,
                u.ResourceId,
                r != null ? r.Title : null,
                r != null ? r.Slug : null,
                u.ConsumedAt);

        return await query.Take(take).ToListAsync(ct);
    }
}
