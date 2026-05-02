using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.DTOs.Reports;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class AdminOrganizationsService : IAdminOrganizationsService
{
    private const int MaxPageSize = 100;

    private readonly TaqreerkDbContext _db;
    private readonly IAdminActionLogger _audit;

    public AdminOrganizationsService(TaqreerkDbContext db, IAdminActionLogger audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<PagedResult<AdminOrganizationListItemDto>> ListAsync(
        AdminOrganizationsListRequest req, CancellationToken ct = default)
    {
        var page = Math.Max(1, req.Page);
        var pageSize = Math.Clamp(req.PageSize, 1, MaxPageSize);

        var q = _db.Organizations.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(req.Q))
        {
            // Case-insensitive contains across name + slug. EF.Functions.ILike
            // would map to ILIKE, but plain Contains + ToLower keeps the call
            // sites consistent with existing search code in the codebase.
            var qLower = req.Q.Trim().ToLower();
            q = q.Where(o =>
                o.NameAr.ToLower().Contains(qLower) ||
                o.NameEn.ToLower().Contains(qLower) ||
                o.Slug.ToLower().Contains(qLower));
        }

        if (!string.IsNullOrWhiteSpace(req.Status)
            && Enum.TryParse<OrganizationStatus>(req.Status, ignoreCase: true, out var status))
            q = q.Where(o => o.Status == status);

        if (!string.IsNullOrWhiteSpace(req.Type)
            && Enum.TryParse<OrganizationType>(req.Type, ignoreCase: true, out var type))
            q = q.Where(o => o.Type == type);

        if (req.CountryId is { } cid)
            q = q.Where(o => o.CountryId == cid);

        if (req.IsPartner is { } partner)
            q = q.Where(o => o.IsPartner == partner);

        if (req.IsVerified is { } verified)
            q = q.Where(o => o.IsVerified == verified);

        var total = await q.CountAsync(ct);

        var rows = await q
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new AdminOrganizationListItemDto(
                o.Id,
                o.NameAr,
                o.NameEn,
                o.Slug,
                o.Type.ToString(),
                o.Status.ToString(),
                o.IsVerified,
                o.IsPartner,
                o.TranslationEnabled,
                o.Country != null ? o.Country.IsoCode : null,
                o.Country != null ? o.Country.NameAr : null,
                o.City,
                o.LogoUrl,
                o.CreatedAt))
            .ToListAsync(ct);

        return new PagedResult<AdminOrganizationListItemDto>(rows, total, page, pageSize);
    }

    public async Task<AdminOrganizationDetailDto> GetAsync(Guid orgId, CancellationToken ct = default)
        => await BuildDetailAsync(orgId, ct)
           ?? throw new KeyNotFoundException("Organization not found.");

    public async Task<AdminOrganizationDetailDto> UpdateAsync(
        Guid actingUserId, Guid orgId, UpdateAdminOrganizationRequest req, CancellationToken ct = default)
    {
        var org = await _db.Organizations.FirstOrDefaultAsync(o => o.Id == orgId, ct)
            ?? throw new KeyNotFoundException("Organization not found.");

        // Track only the fields that actually changed so the audit row stays
        // small and readable. The "before" snapshot mirrors the same shape.
        var before = new
        {
            org.NameAr, org.NameEn, Type = org.Type.ToString(), org.SectorScope,
            org.CountryId, org.City, org.Phone, org.WebsiteUrl, org.Description,
            org.IsPartner, org.TranslationEnabled
        };

        if (req.NameAr is not null) org.NameAr = req.NameAr.Trim();
        if (req.NameEn is not null) org.NameEn = req.NameEn.Trim();
        if (req.Type is not null && Enum.TryParse<OrganizationType>(req.Type, ignoreCase: true, out var newType))
            org.Type = newType;
        if (req.SectorScope is not null) org.SectorScope = req.SectorScope.Trim();
        if (req.CountryId.HasValue) org.CountryId = req.CountryId.Value;
        if (req.City is not null) org.City = req.City.Trim();
        if (req.Phone is not null) org.Phone = req.Phone.Trim();
        if (req.WebsiteUrl is not null) org.WebsiteUrl = req.WebsiteUrl.Trim();
        if (req.Description is not null) org.Description = req.Description.Trim();
        if (req.IsPartner.HasValue) org.IsPartner = req.IsPartner.Value;
        if (req.TranslationEnabled.HasValue) org.TranslationEnabled = req.TranslationEnabled.Value;

        org.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "organization.update",
            targetEntityType: "Organization",
            targetEntityId: orgId,
            beforeState: before,
            afterState: new
            {
                org.NameAr, org.NameEn, Type = org.Type.ToString(), org.SectorScope,
                org.CountryId, org.City, org.Phone, org.WebsiteUrl, org.Description,
                org.IsPartner, org.TranslationEnabled
            },
            ct: ct);

        return (await BuildDetailAsync(orgId, ct))!;
    }

    public async Task<AdminOrganizationDetailDto> SetVerifiedAsync(
        Guid actingUserId, Guid orgId, bool verified, CancellationToken ct = default)
    {
        var org = await _db.Organizations.FirstOrDefaultAsync(o => o.Id == orgId, ct)
            ?? throw new KeyNotFoundException("Organization not found.");

        var wasVerified = org.IsVerified;
        org.IsVerified = verified;
        org.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: verified ? "organization.verify" : "organization.unverify",
            targetEntityType: "Organization",
            targetEntityId: orgId,
            beforeState: new { IsVerified = wasVerified },
            afterState: new { IsVerified = verified },
            ct: ct);

        return (await BuildDetailAsync(orgId, ct))!;
    }

    public async Task<AdminOrganizationDetailDto> SuspendAsync(
        Guid actingUserId, Guid orgId, SuspendOrganizationRequest req, CancellationToken ct = default)
    {
        var org = await _db.Organizations.FirstOrDefaultAsync(o => o.Id == orgId, ct)
            ?? throw new KeyNotFoundException("Organization not found.");

        var prev = org.Status;
        org.Status = OrganizationStatus.Suspended;
        org.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "organization.suspend",
            targetEntityType: "Organization",
            targetEntityId: orgId,
            reason: req.Reason,
            beforeState: new { Status = prev.ToString() },
            afterState: new { Status = org.Status.ToString() },
            ct: ct);

        return (await BuildDetailAsync(orgId, ct))!;
    }

    public async Task<AdminOrganizationDetailDto> ReactivateAsync(
        Guid actingUserId, Guid orgId, CancellationToken ct = default)
    {
        var org = await _db.Organizations.FirstOrDefaultAsync(o => o.Id == orgId, ct)
            ?? throw new KeyNotFoundException("Organization not found.");

        var prev = org.Status;
        org.Status = OrganizationStatus.Active;
        org.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "organization.reactivate",
            targetEntityType: "Organization",
            targetEntityId: orgId,
            beforeState: new { Status = prev.ToString() },
            afterState: new { Status = org.Status.ToString() },
            ct: ct);

        return (await BuildDetailAsync(orgId, ct))!;
    }

    public async Task DeleteAsync(Guid actingUserId, Guid orgId, CancellationToken ct = default)
    {
        var org = await _db.Organizations.FirstOrDefaultAsync(o => o.Id == orgId, ct)
            ?? throw new KeyNotFoundException("Organization not found.");

        // Refuse if there are published reports — deleting the org would orphan
        // them in the public library. The admin needs to archive them first
        // (org-side flow) or use a future bulk-archive admin tool.
        var publishedCount = await _db.Reports
            .AsNoTracking()
            .CountAsync(r => r.OrganizationId == orgId && r.Status == ReportStatus.Published, ct);
        if (publishedCount > 0)
            throw new InvalidOperationException(
                "Cannot delete an organization that has published reports. Archive them first.");

        org.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "organization.delete",
            targetEntityType: "Organization",
            targetEntityId: orgId,
            beforeState: new { org.NameAr, org.Slug, Status = org.Status.ToString() },
            ct: ct);
    }

    public async Task<PagedResult<AdminOrganizationReportItemDto>> ListReportsAsync(
        Guid orgId, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var q = _db.Reports.AsNoTracking().Where(r => r.OrganizationId == orgId);

        var total = await q.CountAsync(ct);

        var rows = await q
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new AdminOrganizationReportItemDto(
                r.Id,
                r.Title,
                r.Status.ToString(),
                r.Slug,
                r.CreatedAt,
                r.PublishedAt,
                r.ViewsCount))
            .ToListAsync(ct);

        return new PagedResult<AdminOrganizationReportItemDto>(rows, total, page, pageSize);
    }

    public async Task<IReadOnlyList<AdminOrganizationMemberDto>> ListMembersAsync(
        Guid orgId, CancellationToken ct = default)
    {
        return await _db.OrganizationMembers
            .AsNoTracking()
            .Where(m => m.OrganizationId == orgId)
            .OrderBy(m => m.JoinedAt)
            .Select(m => new AdminOrganizationMemberDto(
                m.UserId,
                m.User.FullName,
                m.User.Email,
                m.Role.Name,
                m.JoinedAt,
                m.IsActive))
            .ToListAsync(ct);
    }

    private async Task<AdminOrganizationDetailDto?> BuildDetailAsync(Guid orgId, CancellationToken ct)
    {
        var detail = await _db.Organizations
            .AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => new
            {
                o.Id, o.NameAr, o.NameEn, o.Slug,
                Type = o.Type.ToString(),
                Status = o.Status.ToString(),
                o.IsVerified, o.IsPartner, o.TranslationEnabled, o.SectorScope,
                o.CountryId,
                CountryNameAr = o.Country != null ? o.Country.NameAr : null,
                o.City, o.Phone, o.WebsiteUrl, o.LogoUrl, o.Description, o.CreatedAt,
            })
            .FirstOrDefaultAsync(ct);
        if (detail is null) return null;

        var memberCount = await _db.OrganizationMembers
            .CountAsync(m => m.OrganizationId == orgId, ct);
        var reportCount = await _db.Reports
            .CountAsync(r => r.OrganizationId == orgId, ct);
        var publishedCount = await _db.Reports
            .CountAsync(r => r.OrganizationId == orgId && r.Status == ReportStatus.Published, ct);

        return new AdminOrganizationDetailDto(
            detail.Id, detail.NameAr, detail.NameEn, detail.Slug,
            detail.Type, detail.Status, detail.IsVerified, detail.IsPartner,
            detail.TranslationEnabled,
            detail.SectorScope, detail.CountryId, detail.CountryNameAr,
            detail.City, detail.Phone, detail.WebsiteUrl, detail.LogoUrl, detail.Description,
            detail.CreatedAt, memberCount, reportCount, publishedCount);
    }
}
