using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class AdminFeaturedService : IAdminFeaturedService
{
    private readonly TaqreerkDbContext _db;
    private readonly IAdminActionLogger _audit;

    public AdminFeaturedService(TaqreerkDbContext db, IAdminActionLogger audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<IReadOnlyList<FeaturedReportDto>> ListAsync(CancellationToken ct = default)
    {
        return await _db.FeaturedReports
            .AsNoTracking()
            .OrderBy(f => f.Section).ThenBy(f => f.Position)
            .Select(f => new FeaturedReportDto(
                f.Id,
                f.ReportId,
                f.Section.ToString(),
                f.Position,
                f.FeaturedFrom,
                f.FeaturedUntil,
                f.IsActive,
                f.CreatedAt,
                f.Report.Title,
                f.Report.Slug,
                f.Report.CoverImageUrl,
                f.Report.Status.ToString(),
                f.Report.OrganizationId,
                f.Report.Organization.NameAr))
            .ToListAsync(ct);
    }

    public async Task<FeaturedReportDto> CreateAsync(
        Guid actingUserId, CreateFeaturedReportRequest req, CancellationToken ct = default)
    {
        if (!Enum.TryParse<FeaturedSection>(req.Section, ignoreCase: true, out var section))
            throw new InvalidOperationException("Unknown featured section.");

        var report = await _db.Reports
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == req.ReportId, ct)
            ?? throw new KeyNotFoundException("Report not found.");

        if (report.Status != ReportStatus.Published)
            throw new InvalidOperationException(
                "Only published reports can be featured. Approve and wait for AI processing first.");

        // Refuse duplicate placement in the same section. A report can be
        // featured in multiple *different* sections at once (e.g. hero +
        // sector_top), but not twice in the same section.
        var alreadyHere = await _db.FeaturedReports
            .AnyAsync(f => f.ReportId == req.ReportId && f.Section == section, ct);
        if (alreadyHere)
            throw new InvalidOperationException(
                "This report is already featured in that section.");

        // Validate scheduling: from must be before until when both set.
        if (req.FeaturedFrom is { } from
            && req.FeaturedUntil is { } until
            && from >= until)
            throw new InvalidOperationException(
                "FeaturedFrom must be earlier than FeaturedUntil.");

        // Append to the end of the section. We don't pack here; /reorder
        // will rewrite the values when the editor drags things around.
        var maxPos = await _db.FeaturedReports.AnyAsync(f => f.Section == section, ct)
            ? await _db.FeaturedReports.Where(f => f.Section == section).MaxAsync(f => f.Position, ct)
            : -1;

        var entity = new FeaturedReport
        {
            ReportId = req.ReportId,
            Section = section,
            Position = maxPos + 1,
            FeaturedFrom = req.FeaturedFrom,
            FeaturedUntil = req.FeaturedUntil,
            IsActive = req.IsActive ?? true,
            CreatedByUserId = actingUserId,
        };
        _db.FeaturedReports.Add(entity);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "featured.create",
            targetEntityType: "FeaturedReport",
            targetEntityId: entity.Id,
            afterState: new
            {
                entity.ReportId,
                Section = section.ToString(),
                entity.Position,
                entity.FeaturedFrom,
                entity.FeaturedUntil,
                entity.IsActive,
            },
            ct: ct);

        return await BuildDtoAsync(entity.Id, ct)
            ?? throw new InvalidOperationException("Saved row not found on reload.");
    }

    public async Task<FeaturedReportDto> UpdateAsync(
        Guid actingUserId, Guid id, UpdateFeaturedReportRequest req, CancellationToken ct = default)
    {
        var entity = await _db.FeaturedReports.FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw new KeyNotFoundException("Featured row not found.");

        var before = new
        {
            entity.ReportId,
            Section = entity.Section.ToString(),
            entity.Position,
            entity.FeaturedFrom,
            entity.FeaturedUntil,
            entity.IsActive,
        };

        if (req.Section is not null)
        {
            if (!Enum.TryParse<FeaturedSection>(req.Section, ignoreCase: true, out var newSection))
                throw new InvalidOperationException("Unknown featured section.");
            if (newSection != entity.Section)
            {
                // Re-tail in the new section. Don't bother packing the old
                // section's positions — they're sparse but ordered, and the
                // SPA will trigger /reorder on next drag.
                var maxPos = await _db.FeaturedReports.AnyAsync(f => f.Section == newSection, ct)
                    ? await _db.FeaturedReports.Where(f => f.Section == newSection).MaxAsync(f => f.Position, ct)
                    : -1;
                entity.Section = newSection;
                entity.Position = maxPos + 1;
            }
        }

        if (req.FeaturedFrom.HasValue) entity.FeaturedFrom = req.FeaturedFrom.Value;
        if (req.FeaturedUntil.HasValue) entity.FeaturedUntil = req.FeaturedUntil.Value;

        if (entity.FeaturedFrom is { } from
            && entity.FeaturedUntil is { } until
            && from >= until)
            throw new InvalidOperationException(
                "FeaturedFrom must be earlier than FeaturedUntil.");

        if (req.IsActive.HasValue) entity.IsActive = req.IsActive.Value;

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "featured.update",
            targetEntityType: "FeaturedReport",
            targetEntityId: id,
            beforeState: before,
            afterState: new
            {
                entity.ReportId,
                Section = entity.Section.ToString(),
                entity.Position,
                entity.FeaturedFrom,
                entity.FeaturedUntil,
                entity.IsActive,
            },
            ct: ct);

        return (await BuildDtoAsync(id, ct))!;
    }

    public async Task DeleteAsync(Guid actingUserId, Guid id, CancellationToken ct = default)
    {
        var entity = await _db.FeaturedReports.FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw new KeyNotFoundException("Featured row not found.");

        _db.FeaturedReports.Remove(entity);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "featured.delete",
            targetEntityType: "FeaturedReport",
            targetEntityId: id,
            beforeState: new
            {
                entity.ReportId,
                Section = entity.Section.ToString(),
                entity.Position,
            },
            ct: ct);
    }

    public async Task ReorderSectionAsync(
        Guid actingUserId, string section, FeaturedReorderRequest req, CancellationToken ct = default)
    {
        if (!Enum.TryParse<FeaturedSection>(section, ignoreCase: true, out var sec))
            throw new InvalidOperationException("Unknown featured section.");

        var rows = await _db.FeaturedReports
            .Where(f => f.Section == sec)
            .ToListAsync(ct);

        if (req.Ids.Count != rows.Count)
            throw new InvalidOperationException(
                "Reorder payload must include every featured row in this section exactly once.");

        var idSet = req.Ids.ToHashSet();
        if (idSet.Count != req.Ids.Count
            || !rows.All(r => idSet.Contains(r.Id)))
            throw new InvalidOperationException(
                "Reorder payload contains duplicate or unknown row IDs.");

        var byId = rows.ToDictionary(r => r.Id);
        for (var i = 0; i < req.Ids.Count; i++)
            byId[req.Ids[i]].Position = i;

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "featured.reorder",
            targetEntityType: "FeaturedReport",
            targetEntityId: null,
            afterState: new { Section = sec.ToString(), Order = req.Ids },
            ct: ct);
    }

    private async Task<FeaturedReportDto?> BuildDtoAsync(Guid id, CancellationToken ct)
    {
        return await _db.FeaturedReports
            .AsNoTracking()
            .Where(f => f.Id == id)
            .Select(f => new FeaturedReportDto(
                f.Id,
                f.ReportId,
                f.Section.ToString(),
                f.Position,
                f.FeaturedFrom,
                f.FeaturedUntil,
                f.IsActive,
                f.CreatedAt,
                f.Report.Title,
                f.Report.Slug,
                f.Report.CoverImageUrl,
                f.Report.Status.ToString(),
                f.Report.OrganizationId,
                f.Report.Organization.NameAr))
            .FirstOrDefaultAsync(ct);
    }
}
