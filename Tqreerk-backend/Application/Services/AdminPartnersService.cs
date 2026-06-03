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

    public async Task<IReadOnlyList<AdminPartnerCategoryDto>> ListCategoriesAsync(CancellationToken ct = default)
    {
        return await _db.PartnerCategories
            .AsNoTracking()
            .OrderBy(c => c.SortOrder).ThenBy(c => c.NameAr)
            .Select(c => new AdminPartnerCategoryDto(
                c.Id, c.NameAr, c.NameEn, c.IsActive, c.SortOrder,
                c.Partners.Count))
            .ToListAsync(ct);
    }

    public async Task<AdminPartnerCategoryDto> CreateCategoryAsync(
        Guid actingUserId, CreatePartnerCategoryRequest req, CancellationToken ct = default)
    {
        var maxOrder = await _db.PartnerCategories.AnyAsync(ct)
            ? await _db.PartnerCategories.MaxAsync(c => c.SortOrder, ct)
            : -1;

        var category = new PartnerCategory
        {
            NameAr = req.NameAr.Trim(),
            NameEn = req.NameEn.Trim(),
            IsActive = req.IsActive ?? true,
            SortOrder = maxOrder + 1,
        };

        _db.PartnerCategories.Add(category);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "partner_category.create",
            targetEntityType: "PartnerCategory",
            targetEntityId: category.Id,
            afterState: new { category.NameAr, category.NameEn, category.IsActive },
            ct: ct);

        return ToCategoryDto(category, 0);
    }

    public async Task<AdminPartnerCategoryDto> UpdateCategoryAsync(
        Guid actingUserId, Guid id, UpdatePartnerCategoryRequest req, CancellationToken ct = default)
    {
        var category = await _db.PartnerCategories
            .Include(c => c.Partners)
            .FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new KeyNotFoundException("Partner category not found.");

        var before = new { category.NameAr, category.NameEn, category.IsActive };

        if (req.NameAr is not null) category.NameAr = req.NameAr.Trim();
        if (req.NameEn is not null) category.NameEn = req.NameEn.Trim();
        if (req.IsActive.HasValue) category.IsActive = req.IsActive.Value;

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "partner_category.update",
            targetEntityType: "PartnerCategory",
            targetEntityId: id,
            beforeState: before,
            afterState: new { category.NameAr, category.NameEn, category.IsActive },
            ct: ct);

        return ToCategoryDto(category, category.Partners.Count);
    }

    public async Task DeleteCategoryAsync(Guid actingUserId, Guid id, CancellationToken ct = default)
    {
        var category = await _db.PartnerCategories
            .Include(c => c.Partners)
            .FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new KeyNotFoundException("Partner category not found.");

        if (category.Partners.Count > 0)
            throw new InvalidOperationException("Cannot delete a category that still has partners.");

        _db.PartnerCategories.Remove(category);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "partner_category.delete",
            targetEntityType: "PartnerCategory",
            targetEntityId: id,
            beforeState: new { category.NameAr },
            ct: ct);
    }

    public async Task ReorderCategoriesAsync(Guid actingUserId, ReorderRequest req, CancellationToken ct = default)
    {
        var categories = await _db.PartnerCategories.ToListAsync(ct);

        if (req.Ids.Count != categories.Count)
            throw new InvalidOperationException(
                "Reorder payload must include every existing partner category exactly once.");

        var idSet = req.Ids.ToHashSet();
        if (idSet.Count != req.Ids.Count || !categories.All(c => idSet.Contains(c.Id)))
            throw new InvalidOperationException(
                "Reorder payload contains duplicate or unknown partner category IDs.");

        var byId = categories.ToDictionary(c => c.Id);
        for (var i = 0; i < req.Ids.Count; i++)
            byId[req.Ids[i]].SortOrder = i;

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "partner_category.reorder",
            targetEntityType: "PartnerCategory",
            targetEntityId: null,
            afterState: new { Order = req.Ids },
            ct: ct);
    }

    public async Task<IReadOnlyList<AdminPartnerDto>> ListAsync(CancellationToken ct = default)
    {
        return await _db.Partners
            .AsNoTracking()
            .Include(p => p.Category)
            .OrderBy(p => p.Category.SortOrder)
            .ThenBy(p => p.SortOrder)
            .ThenBy(p => p.NameAr)
            .Select(p => new AdminPartnerDto(
                p.Id, p.CategoryId, p.Category.NameAr, p.Category.NameEn,
                p.NameAr, p.NameEn,
                p.LogoUrl != null ? _files.GetPublicUrl(p.LogoUrl) : null,
                p.WebsiteUrl, p.IsActive, p.SortOrder))
            .ToListAsync(ct);
    }

    public async Task<AdminPartnerDto> CreateAsync(
        Guid actingUserId, CreatePartnerRequest req, IFormFile? logo, CancellationToken ct = default)
    {
        var categoryExists = await _db.PartnerCategories.AnyAsync(c => c.Id == req.CategoryId, ct);
        if (!categoryExists)
            throw new KeyNotFoundException("Partner category not found.");

        var maxOrder = await _db.Partners
            .Where(p => p.CategoryId == req.CategoryId)
            .Select(p => (int?)p.SortOrder)
            .MaxAsync(ct) ?? -1;

        var partner = new Partner
        {
            CategoryId = req.CategoryId,
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

        await _db.Entry(partner).Reference(p => p.Category).LoadAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "partner.create",
            targetEntityType: "Partner",
            targetEntityId: partner.Id,
            afterState: new { partner.NameAr, partner.NameEn, partner.IsActive, partner.CategoryId },
            ct: ct);

        return ToDto(partner);
    }

    public async Task<AdminPartnerDto> UpdateAsync(
        Guid actingUserId, Guid id, UpdatePartnerRequest req, IFormFile? logo, CancellationToken ct = default)
    {
        var partner = await _db.Partners
            .Include(p => p.Category)
            .FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new KeyNotFoundException("Partner not found.");

        var before = new { partner.NameAr, partner.NameEn, partner.IsActive, partner.CategoryId };

        if (req.CategoryId.HasValue)
        {
            var categoryExists = await _db.PartnerCategories
                .AnyAsync(c => c.Id == req.CategoryId.Value, ct);
            if (!categoryExists)
                throw new KeyNotFoundException("Partner category not found.");

            if (partner.CategoryId != req.CategoryId.Value)
            {
                var maxOrder = await _db.Partners
                    .Where(p => p.CategoryId == req.CategoryId.Value)
                    .Select(p => (int?)p.SortOrder)
                    .MaxAsync(ct) ?? -1;
                partner.CategoryId = req.CategoryId.Value;
                partner.SortOrder = maxOrder + 1;
            }
        }

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

        if (req.CategoryId.HasValue && partner.CategoryId != before.CategoryId)
            await _db.Entry(partner).Reference(p => p.Category).LoadAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "partner.update",
            targetEntityType: "Partner",
            targetEntityId: id,
            beforeState: before,
            afterState: new { partner.NameAr, partner.NameEn, partner.IsActive, partner.CategoryId },
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

    public async Task ReorderAsync(Guid actingUserId, PartnerReorderRequest req, CancellationToken ct = default)
    {
        var partners = await _db.Partners
            .Where(p => p.CategoryId == req.CategoryId)
            .ToListAsync(ct);

        if (req.Ids.Count != partners.Count)
            throw new InvalidOperationException(
                "Reorder payload must include every partner in the category exactly once.");

        var idSet = req.Ids.ToHashSet();
        if (idSet.Count != req.Ids.Count || !partners.All(p => idSet.Contains(p.Id)))
            throw new InvalidOperationException(
                "Reorder payload contains duplicate or unknown partner IDs for this category.");

        var byId = partners.ToDictionary(p => p.Id);
        for (var i = 0; i < req.Ids.Count; i++)
            byId[req.Ids[i]].SortOrder = i;

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "partner.reorder",
            targetEntityType: "Partner",
            targetEntityId: null,
            afterState: new { req.CategoryId, Order = req.Ids },
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

    private AdminPartnerCategoryDto ToCategoryDto(PartnerCategory c, int partnerCount) => new(
        c.Id, c.NameAr, c.NameEn, c.IsActive, c.SortOrder, partnerCount);

    private AdminPartnerDto ToDto(Partner p) => new(
        p.Id, p.CategoryId, p.Category.NameAr, p.Category.NameEn,
        p.NameAr, p.NameEn,
        p.LogoUrl != null ? _files.GetPublicUrl(p.LogoUrl) : null,
        p.WebsiteUrl, p.IsActive, p.SortOrder);
}
