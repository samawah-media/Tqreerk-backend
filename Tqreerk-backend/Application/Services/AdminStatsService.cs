using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class AdminStatsService : IAdminStatsService
{
    private const int TopN = 10;
    private const int OrgsTopN = 10;
    private const int RecentRejectionsN = 10;
    private const int TimeseriesDays = 30;
    private const int MinRatingsForLeaderboard = 5;

    private readonly TaqreerkDbContext _db;

    public AdminStatsService(TaqreerkDbContext db)
    {
        _db = db;
    }

    public async Task<AdminStatsOverviewDto> GetOverviewAsync(CancellationToken ct = default)
    {
        // The whole rollup is cheap (everything indexed), but EF can't share
        // a connection across parallel queries by default — so we issue them
        // sequentially. Total wall-time on a moderately-populated DB is
        // single-digit ms.
        var thirtyDaysAgo = DateTime.UtcNow.AddDays(-TimeseriesDays);
        var maoCutoff = DateTime.UtcNow.AddDays(-30);

        // ── KPIs ─────────────────────────────────────────────────────
        var publishedReports = await _db.Reports
            .CountAsync(r => r.Status == ReportStatus.Published, ct);
        var pendingReviews = await _db.Reports
            .CountAsync(r => r.Status == ReportStatus.PendingReview, ct);
        var underReview = await _db.Reports
            .CountAsync(r => r.Status == ReportStatus.UnderReview, ct);

        var totalOrganizations = await _db.Organizations
            .CountAsync(o => o.Status == OrganizationStatus.Active, ct);
        var partnerOrganizations = await _db.Organizations
            .CountAsync(o => o.IsPartner, ct);

        var totalUsers = await _db.Users
            .CountAsync(u => !u.IsPlatformStaff, ct);

        // MAU proxy: distinct user_id in report_views in the last 30 days.
        // Cheaper than DAU/WAU windows, and the index on (UserId, ViewedAt)
        // keeps it fast even as views grow.
        var monthlyActiveUsers = await _db.ReportViews
            .Where(v => v.UserId != null && v.ViewedAt >= maoCutoff)
            .Select(v => v.UserId!.Value)
            .Distinct()
            .CountAsync(ct);

        var totalViews = await _db.Reports.SumAsync(r => (long)r.ViewsCount, ct);
        var totalDownloads = await _db.Reports.SumAsync(r => (long)r.DownloadsCount, ct);

        // Reviews. AverageAsync over an empty source throws InvalidOperationException;
        // gate on a count so the caller gets a clean null instead.
        var reviewedCount = await _db.ReportReviews
            .CountAsync(r => r.ReviewDurationSeconds != null, ct);
        double? avgReviewSeconds = reviewedCount > 0
            ? await _db.ReportReviews
                .Where(r => r.ReviewDurationSeconds != null)
                .AverageAsync(r => (double)r.ReviewDurationSeconds!.Value, ct)
            : null;

        var totalDecisions = await _db.ReportReviews.CountAsync(ct);
        var returnCount = await _db.ReportReviews
            .CountAsync(r => r.Decision == ReviewDecision.ReturnedForEdit, ct);
        double? returnRate = totalDecisions > 0
            ? (double)returnCount / totalDecisions
            : null;

        // ── Top sectors / countries / orgs (by published-report count) ──
        // EF Core 8's GROUP BY translator can't construct positional records
        // inline — project to an anonymous type first, then map to the DTO
        // in memory. The shape stays identical.
        var topSectors = (await _db.Reports
            .Where(r => r.Status == ReportStatus.Published && r.SectorId != null)
            .GroupBy(r => r.Sector!.NameAr)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(TopN)
            .ToListAsync(ct))
            .Select(x => new NamedCountDto(x.Name, x.Count))
            .ToList();

        var topCountries = (await _db.Reports
            .Where(r => r.Status == ReportStatus.Published && r.CountryId != null)
            .GroupBy(r => r.Country!.NameAr)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(TopN)
            .ToListAsync(ct))
            .Select(x => new NamedCountDto(x.Name, x.Count))
            .ToList();

        var topOrganizations = (await _db.Reports
            .Where(r => r.Status == ReportStatus.Published)
            .GroupBy(r => r.Organization.NameAr)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(OrgsTopN)
            .ToListAsync(ct))
            .Select(x => new NamedCountDto(x.Name, x.Count))
            .ToList();

        // ── Top reports by metric ─────────────────────────────────────
        // Same anonymous-type-then-map pattern as the GROUP BYs above —
        // works whether or not EF can translate the positional record ctor.
        var mostViewed = (await _db.Reports
            .Where(r => r.Status == ReportStatus.Published)
            .OrderByDescending(r => r.ViewsCount)
            .Take(TopN)
            .Select(r => new
            {
                r.Id,
                r.TitleAr,
                r.TitleEn,
                OrgName = r.Organization.NameAr,
                Metric = (long)r.ViewsCount,
            })
            .ToListAsync(ct))
            .Select(x => new TopReportDto(x.Id, x.TitleAr, x.TitleEn, x.OrgName, x.Metric, "views"))
            .ToList();

        var mostDownloaded = (await _db.Reports
            .Where(r => r.Status == ReportStatus.Published)
            .OrderByDescending(r => r.DownloadsCount)
            .Take(TopN)
            .Select(r => new
            {
                r.Id,
                r.TitleAr,
                r.TitleEn,
                OrgName = r.Organization.NameAr,
                Metric = (long)r.DownloadsCount,
            })
            .ToListAsync(ct))
            .Select(x => new TopReportDto(x.Id, x.TitleAr, x.TitleEn, x.OrgName, x.Metric, "downloads"))
            .ToList();

        // Highest rated needs a minimum sample size — single 5-star rating
        // shouldn't dominate the leaderboard. The metric is the average
        // rating × 100 (rounded to long) so the payload stays integer-friendly.
        var highestRated = (await _db.Reports
            .Where(r => r.Status == ReportStatus.Published
                     && r.RatingsCount >= MinRatingsForLeaderboard)
            .OrderByDescending(r => r.AvgRating)
            .ThenByDescending(r => r.RatingsCount)
            .Take(TopN)
            .Select(r => new
            {
                r.Id,
                r.TitleAr,
                r.TitleEn,
                OrgName = r.Organization.NameAr,
                Rating = r.AvgRating,
            })
            .ToListAsync(ct))
            .Select(x => new TopReportDto(x.Id, x.TitleAr, x.TitleEn, x.OrgName, (long)(x.Rating * 100m), "rating"))
            .ToList();

        // ── Timeseries (last 30 days, gap-filled) ─────────────────────
        var uploadsRaw = await _db.Reports
            .Where(r => r.CreatedAt >= thirtyDaysAgo)
            .GroupBy(r => r.CreatedAt.Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var uploadsTimeseries = FillDailyGaps(uploadsRaw.ToDictionary(r => r.Date, r => r.Count));

        var registrationsRaw = await _db.Users
            .Where(u => u.CreatedAt >= thirtyDaysAgo && !u.IsPlatformStaff)
            .GroupBy(u => u.CreatedAt.Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var registrationsTimeseries = FillDailyGaps(
            registrationsRaw.ToDictionary(r => r.Date, r => r.Count));

        // ── Breakdowns ────────────────────────────────────────────────
        var statusGroups = await _db.Reports
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var reportStatusBreakdown = statusGroups
            .Select(g => new NamedCountDto(g.Status.ToString(), g.Count))
            .OrderByDescending(x => x.Count)
            .ToList();

        // userType is derived (same logic as AdminUsersService): individual /
        // orgMember / staff. Three counts is cheaper than a CASE-WHEN GROUP BY.
        var staffCount = await _db.Users.CountAsync(u => u.IsPlatformStaff, ct);
        var orgMemberCount = await _db.Users.CountAsync(
            u => !u.IsPlatformStaff && u.OrganizationMemberships.Any(), ct);
        var individualCount = await _db.Users.CountAsync(
            u => !u.IsPlatformStaff && !u.OrganizationMemberships.Any(), ct);
        var userTypeBreakdown = new List<NamedCountDto>
        {
            new("individual", individualCount),
            new("orgMember", orgMemberCount),
            new("staff", staffCount),
        };

        var decisionGroups = await _db.ReportReviews
            .GroupBy(r => r.Decision)
            .Select(g => new { Decision = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var reviewDecisionBreakdown = decisionGroups
            .Select(g => new NamedCountDto(g.Decision.ToString(), g.Count))
            .ToList();

        // ── Recent rejections ─────────────────────────────────────────
        var recentRejections = (await _db.ReportReviews
            .Where(r => r.Decision == ReviewDecision.Rejected)
            .OrderByDescending(r => r.ReviewedAt)
            .Take(RecentRejectionsN)
            .Select(r => new
            {
                r.ReportId,
                ReportTitleAr = r.Report.TitleAr,
                ReportTitleEn = r.Report.TitleEn,
                OrgName = r.Report.Organization.NameAr,
                r.ReviewNotes,
                r.ReviewedAt,
            })
            .ToListAsync(ct))
            .Select(x => new RejectionNoteDto(x.ReportId, x.ReportTitleAr, x.ReportTitleEn, x.OrgName, x.ReviewNotes, x.ReviewedAt))
            .ToList();

        return new AdminStatsOverviewDto(
            publishedReports,
            pendingReviews,
            underReview,
            totalOrganizations,
            partnerOrganizations,
            totalUsers,
            monthlyActiveUsers,
            totalViews,
            totalDownloads,
            avgReviewSeconds,
            returnRate,
            topSectors,
            topCountries,
            topOrganizations,
            mostViewed,
            mostDownloaded,
            highestRated,
            uploadsTimeseries,
            registrationsTimeseries,
            reportStatusBreakdown,
            userTypeBreakdown,
            reviewDecisionBreakdown,
            recentRejections);
    }

    /// Walk the last N days and emit one bucket per day, using 0 where
    /// the source dictionary has no entry. Keeps charts continuous so a
    /// quiet day doesn't disappear from the line.
    private static List<TimeseriesPointDto> FillDailyGaps(IDictionary<DateTime, int> raw)
    {
        var result = new List<TimeseriesPointDto>(TimeseriesDays);
        var today = DateTime.UtcNow.Date;
        for (var i = TimeseriesDays - 1; i >= 0; i--)
        {
            var d = today.AddDays(-i);
            raw.TryGetValue(d, out var count);
            result.Add(new TimeseriesPointDto(d, count));
        }
        return result;
    }
}
