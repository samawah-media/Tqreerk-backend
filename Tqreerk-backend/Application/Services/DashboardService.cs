using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Taqreerk.Application.DTOs.Dashboard;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class DashboardService : IDashboardService
{
    private static readonly TimeSpan StatsCacheLifetime = TimeSpan.FromMinutes(5);
    private const int MaxRecentActivity = 50;

    private readonly TaqreerkDbContext _db;
    private readonly IMemoryCache _cache;

    public DashboardService(TaqreerkDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<OrganizationStatsDto> GetOrganizationStatsAsync(Guid userId, CancellationToken ct = default)
    {
        var orgId = await ResolveOrgIdAsync(userId, ct);
        var cacheKey = $"org-stats:{orgId}";

        if (_cache.TryGetValue(cacheKey, out OrganizationStatsDto? cached) && cached is not null)
            return cached;

        // Reports counts come from the reports table; view/download/rating
        // totals are sums of the denormalized counters on each report (kept
        // in sync by the report-view/-rating writers).
        var reportRows = await _db.Reports
            .AsNoTracking()
            .Where(r => r.OrganizationId == orgId)
            .Select(r => new { r.Status, r.ViewsCount, r.DownloadsCount, r.AvgRating, r.RatingsCount })
            .ToListAsync(ct);

        var totalReports = reportRows.Count;
        var publishedReports = reportRows.Count(r => r.Status == ReportStatus.Published);
        var totalViews = reportRows.Sum(r => (long)r.ViewsCount);
        var totalDownloads = reportRows.Sum(r => (long)r.DownloadsCount);
        var totalRatings = reportRows.Sum(r => r.RatingsCount);
        // Weighted average across reports (avg per report × ratings count) — falls
        // back to 0 when no ratings exist anywhere yet.
        var weightedSum = reportRows.Sum(r => r.AvgRating * r.RatingsCount);
        var averageRating = totalRatings > 0 ? Math.Round(weightedSum / totalRatings, 2) : 0m;

        var teamMembers = await _db.OrganizationMembers
            .AsNoTracking()
            .CountAsync(m => m.OrganizationId == orgId, ct);

        var dto = new OrganizationStatsDto(
            totalReports, publishedReports, totalViews, totalDownloads,
            averageRating, totalRatings, teamMembers);

        _cache.Set(cacheKey, dto, StatsCacheLifetime);
        return dto;
    }

    public async Task<IReadOnlyList<RecentActivityDto>> GetRecentActivityAsync(Guid userId, int take = 10, CancellationToken ct = default)
    {
        if (take <= 0) take = 10;
        if (take > MaxRecentActivity) take = MaxRecentActivity;

        var orgId = await ResolveOrgIdAsync(userId, ct);

        var rows = await _db.AuditLogs
            .AsNoTracking()
            .Where(a => a.OrganizationId == orgId)
            .OrderByDescending(a => a.CreatedAt)
            .Take(take)
            .Select(a => new
            {
                a.Id,
                a.EventType,
                a.EntityType,
                a.EntityId,
                a.UserId,
                a.CreatedAt,
            })
            .ToListAsync(ct);

        if (rows.Count == 0) return [];

        // Hydrate actor names in a single follow-up query so the list isn't N+1.
        var actorIds = rows.Where(r => r.UserId.HasValue).Select(r => r.UserId!.Value).Distinct().ToList();
        var actors = actorIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Users.AsNoTracking()
                .Where(u => actorIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FullName })
                .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

        return rows
            .Select(r => new RecentActivityDto(
                r.Id,
                r.EventType,
                r.EntityType,
                r.EntityId,
                r.UserId.HasValue && actors.TryGetValue(r.UserId.Value, out var name) ? name : null,
                r.CreatedAt))
            .ToList();
    }

    private async Task<Guid> ResolveOrgIdAsync(Guid userId, CancellationToken ct)
    {
        var orgId = await _db.OrganizationMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.OrganizationId)
            .FirstOrDefaultAsync(ct);

        if (orgId == Guid.Empty)
            throw new KeyNotFoundException("No organization is associated with this user.");

        return orgId;
    }
}
