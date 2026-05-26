using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.FeatureRequests;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class FeatureRequestsService : IFeatureRequestsService
{
    /// Default editorial window applied when an admin approves a
    /// request — the auto-created FeaturedReport row runs for 30 days
    /// from the approval moment. Admins can shorten/extend through
    /// the /api/admin/featured surface afterwards.
    private static readonly TimeSpan DefaultFeaturedDuration = TimeSpan.FromDays(30);

    private readonly TaqreerkDbContext _db;
    private readonly IFileStorage _files;
    private readonly IUsageService _usage;

    public FeatureRequestsService(TaqreerkDbContext db, IFileStorage files, IUsageService usage)
    {
        _db = db;
        _files = files;
        _usage = usage;
    }

    // ── Org-side ─────────────────────────────────────────────────────

    public async Task<FeatureRequestDto> CreateAsync(
        Guid currentUserId, Guid reportId, CreateFeatureRequest req, CancellationToken ct = default)
    {
        var orgId = await GetCallerOrgIdAsync(currentUserId, ct);

        var report = await _db.Reports
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reportId, ct)
            ?? throw new KeyNotFoundException("Report not found.");

        if (report.OrganizationId != orgId)
            throw new UnauthorizedAccessException("This report belongs to another organization.");

        // Only published reports are eligible — featuring a draft would
        // surface a 404 to public visitors.
        if (report.Status != ReportStatus.Published)
            throw new InvalidOperationException("لا يمكن طلب التمييز إلا للتقارير المنشورة.");

        // Block duplicate Pending requests. The DB has a partial
        // unique index that enforces the same rule, but checking here
        // gives a clean 409 instead of the EF unique-violation surface.
        var hasPending = await _db.ReportFeatureRequests
            .AnyAsync(r => r.ReportId == reportId && r.Status == FeatureRequestStatus.Pending, ct);
        if (hasPending)
            throw new InvalidOperationException("يوجد طلب تمييز قيد المراجعة بالفعل لهذا التقرير.");

        var entity = new ReportFeatureRequest
        {
            ReportId = reportId,
            OrganizationId = orgId,
            RequestedByUserId = currentUserId,
            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
            Status = FeatureRequestStatus.Pending,
        };

        _db.ReportFeatureRequests.Add(entity);
        await _db.SaveChangesAsync(ct);

        return await BuildDtoAsync(entity.Id, ct)
            ?? throw new InvalidOperationException("Feature request disappeared after create.");
    }

    public async Task<FeatureRequestDto?> GetForReportAsync(
        Guid currentUserId, Guid reportId, CancellationToken ct = default)
    {
        var orgId = await GetCallerOrgIdAsync(currentUserId, ct);

        var report = await _db.Reports
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reportId, ct)
            ?? throw new KeyNotFoundException("Report not found.");

        if (report.OrganizationId != orgId)
            throw new UnauthorizedAccessException("This report belongs to another organization.");

        // Most recent first — covers the case where the report has
        // history (rejected once, re-submitted, approved later). The
        // org dashboard only shows the latest entry.
        var latest = await _db.ReportFeatureRequests
            .AsNoTracking()
            .Where(r => r.ReportId == reportId)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => r.Id)
            .FirstOrDefaultAsync(ct);

        return latest == default ? null : await BuildDtoAsync(latest, ct);
    }

    public async Task<IReadOnlyList<FeatureRequestDto>> ListForOrgAsync(
        Guid currentUserId, FeatureRequestStatus? status, CancellationToken ct = default)
    {
        var orgId = await GetCallerOrgIdAsync(currentUserId, ct);

        var query = _db.ReportFeatureRequests
            .AsNoTracking()
            .Where(r => r.OrganizationId == orgId);

        if (status.HasValue) query = query.Where(r => r.Status == status.Value);

        var ids = await query
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => r.Id)
            .ToListAsync(ct);

        return await BuildDtoListAsync(ids, ct);
    }

    // ── Admin-side ───────────────────────────────────────────────────

    public async Task<IReadOnlyList<FeatureRequestDto>> ListForAdminAsync(
        FeatureRequestStatus? status, CancellationToken ct = default)
    {
        var query = _db.ReportFeatureRequests.AsNoTracking().AsQueryable();
        if (status.HasValue) query = query.Where(r => r.Status == status.Value);

        var ids = await query
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => r.Id)
            .ToListAsync(ct);

        return await BuildDtoListAsync(ids, ct);
    }

    public async Task<FeatureRequestDto> ApproveAsync(
        Guid actingAdminUserId, Guid requestId, FeatureRequestDecisionRequest req, CancellationToken ct = default)
    {
        var entity = await LoadPendingAsync(requestId, ct);
        var note = string.IsNullOrWhiteSpace(req.DecisionNote) ? null : req.DecisionNote.Trim();
        var now = DateTime.UtcNow;

        entity.Status = FeatureRequestStatus.Approved;
        entity.ReviewedByUserId = actingAdminUserId;
        entity.ReviewedAt = now;
        entity.DecisionNote = note;

        await _usage.EnsureOrgCanFeatureReportAsync(entity.OrganizationId, ct);

        // Auto-create the editorial pin so the approval ships immediately.
        // Position is the section's current count + 1 — the admin can
        // re-order via the existing reorder endpoint. We don't dedupe
        // against existing FeaturedReport rows for the same report
        // because a duplicate row is harmless (the public list dedupes
        // upstream) and saves a round-trip.
        var section = FeaturedSection.HomepageCarousel;
        var nextPosition = await _db.FeaturedReports
            .Where(f => f.Section == section)
            .CountAsync(ct) + 1;

        _db.FeaturedReports.Add(new FeaturedReport
        {
            ReportId = entity.ReportId,
            Section = section,
            Position = nextPosition,
            FeaturedFrom = now,
            FeaturedUntil = now.Add(DefaultFeaturedDuration),
            IsActive = true,
            CreatedByUserId = actingAdminUserId,
        });

        await _db.SaveChangesAsync(ct);

        return await BuildDtoAsync(entity.Id, ct)
            ?? throw new InvalidOperationException("Feature request disappeared after approve.");
    }

    public async Task<FeatureRequestDto> RejectAsync(
        Guid actingAdminUserId, Guid requestId, FeatureRequestDecisionRequest req, CancellationToken ct = default)
    {
        var entity = await LoadPendingAsync(requestId, ct);

        entity.Status = FeatureRequestStatus.Rejected;
        entity.ReviewedByUserId = actingAdminUserId;
        entity.ReviewedAt = DateTime.UtcNow;
        entity.DecisionNote = string.IsNullOrWhiteSpace(req.DecisionNote) ? null : req.DecisionNote.Trim();

        await _db.SaveChangesAsync(ct);

        return await BuildDtoAsync(entity.Id, ct)
            ?? throw new InvalidOperationException("Feature request disappeared after reject.");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private async Task<ReportFeatureRequest> LoadPendingAsync(Guid requestId, CancellationToken ct)
    {
        var entity = await _db.ReportFeatureRequests
            .FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw new KeyNotFoundException("Feature request not found.");

        if (entity.Status != FeatureRequestStatus.Pending)
            throw new InvalidOperationException("الطلب تمت معالجته مسبقاً ولا يمكن تعديله.");

        return entity;
    }

    private async Task<Guid> GetCallerOrgIdAsync(Guid userId, CancellationToken ct)
    {
        var orgId = await _db.OrganizationMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.IsActive)
            .Select(m => (Guid?)m.OrganizationId)
            .FirstOrDefaultAsync(ct);

        if (!orgId.HasValue)
            throw new InvalidOperationException("Caller is not an active member of any organization.");

        return orgId.Value;
    }

    private async Task<FeatureRequestDto?> BuildDtoAsync(Guid id, CancellationToken ct)
    {
        var list = await BuildDtoListAsync(new[] { id }, ct);
        return list.FirstOrDefault();
    }

    /// Single query that materialises the rich DTO shape — saves N+1
    /// hits when the admin queue has dozens of rows. Joins through the
    /// nav properties so we get titles/names without manual joins.
    /// Cover + logo image keys are resolved to short-lived signed URLs
    /// in a second pass so the queue UI can render them as `<img src>`
    /// without an extra round-trip per row.
    private async Task<IReadOnlyList<FeatureRequestDto>> BuildDtoListAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return Array.Empty<FeatureRequestDto>();

        var rows = await _db.ReportFeatureRequests
            .AsNoTracking()
            .Where(r => ids.Contains(r.Id))
            .Select(r => new
            {
                r.Id,
                r.ReportId,
                ReportTitleAr = r.Report.TitleAr,
                ReportTitleEn = r.Report.TitleEn,
                ReportSlug = r.Report.Slug,
                ReportCoverImageUrl = r.Report.CoverImageUrl,
                r.OrganizationId,
                OrganizationNameAr = r.Organization.NameAr,
                OrganizationLogoUrl = r.Organization.LogoUrl,
                r.RequestedByUserId,
                RequestedByName = r.RequestedByUser.FullName,
                r.Note,
                r.Status,
                r.ReviewedByUserId,
                ReviewedByName = r.ReviewedByUser != null ? r.ReviewedByUser.FullName : null,
                r.ReviewedAt,
                r.DecisionNote,
                r.CreatedAt,
            })
            .ToListAsync(ct);

        // Resolve cover + logo object keys to short-lived signed URLs.
        // De-dupe so a queue with 50 reports from 5 orgs doesn't sign
        // the same logo 50 times.
        var allKeys = rows
            .SelectMany(r => new[] { r.ReportCoverImageUrl, r.OrganizationLogoUrl })
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct()
            .ToList();

        var resolved = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var key in allKeys)
        {
            try { resolved[key!] = await _files.GetReadUrlAsync(key!, ct: ct); }
            catch { resolved[key!] = null; } // best-effort; UI falls back to placeholder
        }

        string? Resolve(string? key)
            => string.IsNullOrWhiteSpace(key)
                ? null
                : resolved.TryGetValue(key!, out var url) ? url : null;

        var dtos = rows.Select(r => new FeatureRequestDto(
            r.Id,
            r.ReportId,
            r.ReportTitleAr,
            r.ReportTitleEn,
            r.ReportSlug,
            Resolve(r.ReportCoverImageUrl),
            r.OrganizationId,
            r.OrganizationNameAr,
            Resolve(r.OrganizationLogoUrl),
            r.RequestedByUserId,
            r.RequestedByName,
            r.Note,
            r.Status,
            r.ReviewedByUserId,
            r.ReviewedByName,
            r.ReviewedAt,
            r.DecisionNote,
            r.CreatedAt));

        // Preserve caller-specified order. The query above doesn't
        // honour `ids` order — re-sort here to keep `ListForOrgAsync`
        // and `ListForAdminAsync` newest-first as advertised.
        var byId = dtos.ToDictionary(r => r.Id);
        return ids.Select(id => byId.TryGetValue(id, out var dto) ? dto : null)
                  .Where(d => d is not null)
                  .Cast<FeatureRequestDto>()
                  .ToList();
    }
}
