using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Entities;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class AdminCategoriesService : IAdminCategoriesService
{
    private readonly TaqreerkDbContext _db;
    private readonly IAdminActionLogger _audit;

    public AdminCategoriesService(TaqreerkDbContext db, IAdminActionLogger audit)
    {
        _db = db;
        _audit = audit;
    }

    // ── Sectors ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AdminSectorDto>> ListSectorsAsync(CancellationToken ct = default)
    {
        // One query — pull each sector with its reference count via a
        // navigation Count(). EF translates this to LEFT JOIN + GROUP BY,
        // which on a ~50-row table is well under 10 ms.
        return await _db.Sectors
            .AsNoTracking()
            .OrderBy(s => s.SortOrder).ThenBy(s => s.NameAr)
            .Select(s => new AdminSectorDto(
                s.Id,
                s.NameAr,
                s.NameEn,
                s.Slug,
                s.Description,
                s.IsActive,
                s.SortOrder,
                s.Reports.Count + s.UserInterests.Count))
            .ToListAsync(ct);
    }

    public async Task<AdminSectorDto> CreateSectorAsync(
        Guid actingUserId, CreateSectorRequest req, CancellationToken ct = default)
    {
        var slug = req.Slug.Trim().ToLowerInvariant();
        var slugTaken = await _db.Sectors.AnyAsync(s => s.Slug == slug, ct);
        if (slugTaken)
            throw new InvalidOperationException("A sector with this slug already exists.");

        // New rows go to the end of the order; we don't bother packing the
        // SortOrder here — the next /reorder call will rewrite values.
        var maxOrder = await _db.Sectors.AnyAsync(ct)
            ? await _db.Sectors.MaxAsync(s => s.SortOrder, ct)
            : -1;

        var sector = new Sector
        {
            NameAr = req.NameAr.Trim(),
            NameEn = req.NameEn.Trim(),
            Slug = slug,
            Description = req.Description?.Trim(),
            IsActive = req.IsActive ?? true,
            SortOrder = maxOrder + 1,
        };
        _db.Sectors.Add(sector);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "sector.create",
            targetEntityType: "Sector",
            targetEntityId: sector.Id,
            afterState: new { sector.NameAr, sector.NameEn, sector.Slug, sector.IsActive },
            ct: ct);

        return new AdminSectorDto(
            sector.Id, sector.NameAr, sector.NameEn, sector.Slug,
            sector.Description, sector.IsActive, sector.SortOrder, 0);
    }

    public async Task<AdminSectorDto> UpdateSectorAsync(
        Guid actingUserId, Guid id, UpdateSectorRequest req, CancellationToken ct = default)
    {
        var sector = await _db.Sectors.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new KeyNotFoundException("Sector not found.");

        var before = new { sector.NameAr, sector.NameEn, sector.Slug, sector.IsActive };

        if (req.NameAr is not null) sector.NameAr = req.NameAr.Trim();
        if (req.NameEn is not null) sector.NameEn = req.NameEn.Trim();

        if (req.Slug is not null)
        {
            var newSlug = req.Slug.Trim().ToLowerInvariant();
            if (newSlug != sector.Slug)
            {
                var slugTaken = await _db.Sectors
                    .AnyAsync(s => s.Slug == newSlug && s.Id != id, ct);
                if (slugTaken)
                    throw new InvalidOperationException("A sector with this slug already exists.");
                sector.Slug = newSlug;
            }
        }

        if (req.Description is not null) sector.Description = req.Description.Trim();
        if (req.IsActive.HasValue) sector.IsActive = req.IsActive.Value;

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "sector.update",
            targetEntityType: "Sector",
            targetEntityId: id,
            beforeState: before,
            afterState: new { sector.NameAr, sector.NameEn, sector.Slug, sector.IsActive },
            ct: ct);

        var refCount = await _db.Reports.CountAsync(r => r.SectorId == id, ct)
                     + await _db.UserInterests.CountAsync(u => u.SectorId == id, ct);

        return new AdminSectorDto(
            sector.Id, sector.NameAr, sector.NameEn, sector.Slug,
            sector.Description, sector.IsActive, sector.SortOrder, refCount);
    }

    public async Task DeleteSectorAsync(
        Guid actingUserId, Guid id, CancellationToken ct = default)
    {
        var sector = await _db.Sectors.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new KeyNotFoundException("Sector not found.");

        var reportRefs = await _db.Reports.CountAsync(r => r.SectorId == id, ct);
        var interestRefs = await _db.UserInterests.CountAsync(u => u.SectorId == id, ct);
        if (reportRefs + interestRefs > 0)
            throw new InvalidOperationException(
                $"Cannot delete sector — it is referenced by {reportRefs} report(s) and {interestRefs} user interest(s). Move them to another sector first.");

        _db.Sectors.Remove(sector);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "sector.delete",
            targetEntityType: "Sector",
            targetEntityId: id,
            beforeState: new { sector.NameAr, sector.Slug },
            ct: ct);
    }

    public async Task ReorderSectorsAsync(
        Guid actingUserId, ReorderRequest req, CancellationToken ct = default)
    {
        var sectors = await _db.Sectors.ToListAsync(ct);

        // Sanity-check the reorder. Refusing partial sets keeps the table
        // from drifting if the SPA sent a stale snapshot mid-edit.
        if (req.Ids.Count != sectors.Count)
            throw new InvalidOperationException(
                "Reorder payload must include every existing sector exactly once.");
        var idSet = req.Ids.ToHashSet();
        if (idSet.Count != req.Ids.Count
            || !sectors.All(s => idSet.Contains(s.Id)))
            throw new InvalidOperationException(
                "Reorder payload contains duplicate or unknown sector IDs.");

        var byId = sectors.ToDictionary(s => s.Id);
        for (var i = 0; i < req.Ids.Count; i++)
            byId[req.Ids[i]].SortOrder = i;

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "sector.reorder",
            targetEntityType: "Sector",
            targetEntityId: null,
            afterState: new { Order = req.Ids },
            ct: ct);
    }

    // ── Countries ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AdminCountryDto>> ListCountriesAsync(CancellationToken ct = default)
    {
        return await _db.Countries
            .AsNoTracking()
            .OrderBy(c => c.SortOrder).ThenBy(c => c.NameAr)
            .Select(c => new AdminCountryDto(
                c.Id,
                c.NameAr,
                c.NameEn,
                c.IsoCode,
                c.SortOrder,
                c.Users.Count + c.Organizations.Count + c.Reports.Count))
            .ToListAsync(ct);
    }

    public async Task<AdminCountryDto> CreateCountryAsync(
        Guid actingUserId, CreateCountryRequest req, CancellationToken ct = default)
    {
        var iso = req.IsoCode.Trim().ToUpperInvariant();
        var isoTaken = await _db.Countries.AnyAsync(c => c.IsoCode == iso, ct);
        if (isoTaken)
            throw new InvalidOperationException("A country with this ISO code already exists.");

        var maxOrder = await _db.Countries.AnyAsync(ct)
            ? await _db.Countries.MaxAsync(c => c.SortOrder, ct)
            : -1;

        var country = new Country
        {
            NameAr = req.NameAr.Trim(),
            NameEn = req.NameEn.Trim(),
            IsoCode = iso,
            SortOrder = maxOrder + 1,
        };
        _db.Countries.Add(country);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "country.create",
            targetEntityType: "Country",
            targetEntityId: country.Id,
            afterState: new { country.NameAr, country.NameEn, country.IsoCode },
            ct: ct);

        return new AdminCountryDto(
            country.Id, country.NameAr, country.NameEn, country.IsoCode,
            country.SortOrder, 0);
    }

    public async Task<AdminCountryDto> UpdateCountryAsync(
        Guid actingUserId, Guid id, UpdateCountryRequest req, CancellationToken ct = default)
    {
        var country = await _db.Countries.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new KeyNotFoundException("Country not found.");

        var before = new { country.NameAr, country.NameEn, country.IsoCode };

        if (req.NameAr is not null) country.NameAr = req.NameAr.Trim();
        if (req.NameEn is not null) country.NameEn = req.NameEn.Trim();

        if (req.IsoCode is not null)
        {
            var newIso = req.IsoCode.Trim().ToUpperInvariant();
            if (newIso != country.IsoCode)
            {
                var taken = await _db.Countries
                    .AnyAsync(c => c.IsoCode == newIso && c.Id != id, ct);
                if (taken)
                    throw new InvalidOperationException("A country with this ISO code already exists.");
                country.IsoCode = newIso;
            }
        }

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "country.update",
            targetEntityType: "Country",
            targetEntityId: id,
            beforeState: before,
            afterState: new { country.NameAr, country.NameEn, country.IsoCode },
            ct: ct);

        var refCount = await _db.Users.CountAsync(u => u.CountryId == id, ct)
                     + await _db.Organizations.CountAsync(o => o.CountryId == id, ct)
                     + await _db.Reports.CountAsync(r => r.CountryId == id, ct);

        return new AdminCountryDto(
            country.Id, country.NameAr, country.NameEn, country.IsoCode,
            country.SortOrder, refCount);
    }

    public async Task DeleteCountryAsync(
        Guid actingUserId, Guid id, CancellationToken ct = default)
    {
        var country = await _db.Countries.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new KeyNotFoundException("Country not found.");

        var userRefs = await _db.Users.CountAsync(u => u.CountryId == id, ct);
        var orgRefs = await _db.Organizations.CountAsync(o => o.CountryId == id, ct);
        var reportRefs = await _db.Reports.CountAsync(r => r.CountryId == id, ct);
        if (userRefs + orgRefs + reportRefs > 0)
            throw new InvalidOperationException(
                $"Cannot delete country — it is referenced by {userRefs} user(s), {orgRefs} organization(s), and {reportRefs} report(s).");

        _db.Countries.Remove(country);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "country.delete",
            targetEntityType: "Country",
            targetEntityId: id,
            beforeState: new { country.NameAr, country.IsoCode },
            ct: ct);
    }

    public async Task ReorderCountriesAsync(
        Guid actingUserId, ReorderRequest req, CancellationToken ct = default)
    {
        var countries = await _db.Countries.ToListAsync(ct);

        if (req.Ids.Count != countries.Count)
            throw new InvalidOperationException(
                "Reorder payload must include every existing country exactly once.");
        var idSet = req.Ids.ToHashSet();
        if (idSet.Count != req.Ids.Count
            || !countries.All(c => idSet.Contains(c.Id)))
            throw new InvalidOperationException(
                "Reorder payload contains duplicate or unknown country IDs.");

        var byId = countries.ToDictionary(c => c.Id);
        for (var i = 0; i < req.Ids.Count; i++)
            byId[req.Ids[i]].SortOrder = i;

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "country.reorder",
            targetEntityType: "Country",
            targetEntityId: null,
            afterState: new { Order = req.Ids },
            ct: ct);
    }
}
