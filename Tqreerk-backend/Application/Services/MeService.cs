using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Me;
using Taqreerk.Application.Interfaces;
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
