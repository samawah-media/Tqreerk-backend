using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Entities;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class AdminPartnersService : IAdminPartnersService
{
    private readonly TaqreerkDbContext _db;
    private readonly IAdminActionLogger _audit;
    private readonly IFileStorage _files;

    public AdminPartnersService(TaqreerkDbContext db, IAdminActionLogger audit, IFileStorage files)
    {
        _db = db;
        _audit = audit;
        _files = files;
    }

    public async Task<IReadOnlyList<AdminPartnerDto>> ListAsync(CancellationToken ct = default)
    {
        return await _db.Partners
            .AsNoTracking()
            .OrderBy(p => p.SortOrder).ThenBy(p => p.NameAr)
            .Select(p => new AdminPartnerDto(
                p.Id, p.NameAr, p.NameEn,
                p.LogoUrl != null ? _files.GetPublicUrl(p.LogoUrl) : null,
                p.WebsiteUrl, p.IsActive, p.SortOrder))
            .ToListAsync(ct);
    }

    public async Task<AdminPartnerDto> CreateAsync(
        Guid actingUserId, CreatePartnerRequest req, IFormFile? logo, CancellationToken ct = default)
    {
        var maxOrder = await _db.Partners.AnyAsync(ct)
            ? await _db.Partners.MaxAsync(p => p.SortOrder, ct)
            : -1;

        var partner = new Partner
        {
            NameAr = req.NameAr.Trim(),
            NameEn = req.NameEn.Trim(),
            WebsiteUrl = req.WebsiteUrl?.Trim(),
            IsActive = req.IsActive ?? true,
            SortOrder = maxOrder + 1,
        };

        if (logo is not null)
            partner.LogoUrl = await UploadLogoAsync(logo, ct);

        _db.Partners.Add(partner);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "partner.create",
            targetEntityType: "Partner",
            targetEntityId: partner.Id,
            afterState: new { partner.NameAr, partner.NameEn, partner.IsActive },
            ct: ct);

        return ToDto(partner);
    }

    public async Task<AdminPartnerDto> UpdateAsync(
        Guid actingUserId, Guid id, UpdatePartnerRequest req, IFormFile? logo, CancellationToken ct = default)
    {
        var partner = await _db.Partners.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new KeyNotFoundException("Partner not found.");

        var before = new { partner.NameAr, partner.NameEn, partner.IsActive };

        if (req.NameAr is not null) partner.NameAr = req.NameAr.Trim();
        if (req.NameEn is not null) partner.NameEn = req.NameEn.Trim();
        if (req.WebsiteUrl is not null) partner.WebsiteUrl = req.WebsiteUrl.Trim();
        if (req.IsActive.HasValue) partner.IsActive = req.IsActive.Value;

        if (logo is not null)
        {
            var oldKey = partner.LogoUrl;
            partner.LogoUrl = await UploadLogoAsync(logo, ct);
            if (oldKey is not null)
            {
                try { await _files.DeleteAsync(oldKey, ct); } catch { /* old logo gone already */ }
            }
        }

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "partner.update",
            targetEntityType: "Partner",
            targetEntityId: id,
            beforeState: before,
            afterState: new { partner.NameAr, partner.NameEn, partner.IsActive },
            ct: ct);

        return ToDto(partner);
    }

    public async Task DeleteAsync(Guid actingUserId, Guid id, CancellationToken ct = default)
    {
        var partner = await _db.Partners.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new KeyNotFoundException("Partner not found.");

        var logoKey = partner.LogoUrl;

        _db.Partners.Remove(partner);
        await _db.SaveChangesAsync(ct);

        if (logoKey is not null)
        {
            try { await _files.DeleteAsync(logoKey, ct); } catch { /* best-effort */ }
        }

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "partner.delete",
            targetEntityType: "Partner",
            targetEntityId: id,
            beforeState: new { partner.NameAr },
            ct: ct);
    }

    public async Task ReorderAsync(Guid actingUserId, ReorderRequest req, CancellationToken ct = default)
    {
        var partners = await _db.Partners.ToListAsync(ct);

        if (req.Ids.Count != partners.Count)
            throw new InvalidOperationException(
                "Reorder payload must include every existing partner exactly once.");
        var idSet = req.Ids.ToHashSet();
        if (idSet.Count != req.Ids.Count || !partners.All(p => idSet.Contains(p.Id)))
            throw new InvalidOperationException(
                "Reorder payload contains duplicate or unknown partner IDs.");

        var byId = partners.ToDictionary(p => p.Id);
        for (var i = 0; i < req.Ids.Count; i++)
            byId[req.Ids[i]].SortOrder = i;

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "partner.reorder",
            targetEntityType: "Partner",
            targetEntityId: null,
            afterState: new { Order = req.Ids },
            ct: ct);
    }

    private async Task<string> UploadLogoAsync(IFormFile logo, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await logo.CopyToAsync(ms, ct);
        ms.Position = 0;
        var folder = $"public/partners/{Guid.NewGuid():N}";
        var stored = await _files.UploadPublicAsync(ms, logo.FileName, logo.ContentType, folder, ct);
        return stored.ObjectKey;
    }

    private AdminPartnerDto ToDto(Partner p) => new(
        p.Id, p.NameAr, p.NameEn,
        p.LogoUrl != null ? _files.GetPublicUrl(p.LogoUrl) : null,
        p.WebsiteUrl, p.IsActive, p.SortOrder);
}
