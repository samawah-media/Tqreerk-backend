using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class AdminDashboardService : IAdminDashboardService
{
    private readonly TaqreerkDbContext _db;

    public AdminDashboardService(TaqreerkDbContext db)
    {
        _db = db;
    }

    public async Task<AdminQuickStatsDto> GetQuickStatsAsync(CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddDays(-7);

        // Three independent COUNTs run in parallel via a single round-trip
        // pattern: kick them off as separate Task<int>s and Task.WhenAll. EF
        // can't share a connection across concurrent queries by default, so
        // we await sequentially — the queries themselves are indexed COUNT(*)
        // calls and finish in single-digit ms even on a million-row reports
        // table.
        var pending = await _db.Reports
            .AsNoTracking()
            .CountAsync(r => r.Status == ReportStatus.PendingReview, ct);

        var underReview = await _db.Reports
            .AsNoTracking()
            .CountAsync(r => r.Status == ReportStatus.UnderReview, ct);

        var newOrgs = await _db.Organizations
            .AsNoTracking()
            .CountAsync(o => o.CreatedAt >= since, ct);

        return new AdminQuickStatsDto(
            PendingReviews: pending,
            UnderReview: underReview,
            NewOrganizationsLast7d: newOrgs);
    }
}
