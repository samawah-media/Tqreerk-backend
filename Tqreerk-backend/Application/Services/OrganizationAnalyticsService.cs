using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Analytics;
using Taqreerk.Application.DTOs.Reports;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;
using Taqreerk.Infrastructure.Storage;

namespace Taqreerk.Application.Services;

public class OrganizationAnalyticsService : IOrganizationAnalyticsService
{
    /// Cap on the leaderboard shown in the org analytics tab. The frontend
    /// renders these as a table; 25 keeps it scannable without paging.
    private const int LeaderboardSize = 25;

    private readonly TaqreerkDbContext _db;
    private readonly IFileStorage _files;
    private readonly ILogger<OrganizationAnalyticsService> _logger;

    public OrganizationAnalyticsService(
        TaqreerkDbContext db,
        IFileStorage files,
        ILogger<OrganizationAnalyticsService> logger)
    {
        _db = db;
        _files = files;
        _logger = logger;
    }

    public async Task<OrganizationAnalyticsDto> GetOrganizationAnalyticsAsync(
        Guid currentUserId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var orgId = await GetCallerOrgIdAsync(currentUserId, ct);
        var (fromUtc, toUtc) = NormalizeRange(from, to);

        // Org's report ids — re-used by every query below. Pulling them
        // up front lets EF push down the filter on report_views without
        // a join into reports just to filter by org.
        var orgReportIds = await _db.Reports
            .AsNoTracking()
            .Where(r => r.OrganizationId == orgId)
            .Select(r => r.Id)
            .ToListAsync(ct);

        // Published count is the published-NOW count, not "was published
        // during the range" — the latter would need a separate audit
        // log. For the analytics widget it's the count the org expects.
        var publishedReports = await _db.Reports
            .AsNoTracking()
            .CountAsync(r => r.OrganizationId == orgId && r.Status == ReportStatus.Published, ct);

        var totalViews = await _db.ReportViews
            .AsNoTracking()
            .Where(v => orgReportIds.Contains(v.ReportId)
                     && v.ViewedAt >= fromUtc && v.ViewedAt <= toUtc)
            .LongCountAsync(ct);

        // Ratings haven't carried CreatedAt-style timestamps consistently
        // before — BaseEntity sets CreatedAt on insert, so we filter by
        // that for the in-range counter. AverageRating is computed over
        // the same window so it reflects the same audience.
        var ratingsInRange = _db.ReportRatings
            .AsNoTracking()
            .Where(r => orgReportIds.Contains(r.ReportId)
                     && r.CreatedAt >= fromUtc && r.CreatedAt <= toUtc);

        var totalRatings = await ratingsInRange.CountAsync(ct);
        var averageRating = totalRatings == 0
            ? 0m
            : Math.Round((decimal)await ratingsInRange.AverageAsync(r => (double)r.Rating, ct), 2);

        // Daily views series — one row per day with views=0 days filled
        // in client-side after we materialise the bucketed counts.
        var bucketed = await _db.ReportViews
            .AsNoTracking()
            .Where(v => orgReportIds.Contains(v.ReportId)
                     && v.ViewedAt >= fromUtc && v.ViewedAt <= toUtc)
            .GroupBy(v => v.ViewedAt.Date)
            .Select(g => new { Date = g.Key, Count = (long)g.Count() })
            .ToListAsync(ct);

        var series = ExpandDaily(bucketed.ToDictionary(b => DateOnly.FromDateTime(b.Date), b => b.Count),
                                 DateOnly.FromDateTime(fromUtc),
                                 DateOnly.FromDateTime(toUtc));

        // Leaderboard — top reports by views in-range. We fetch a few
        // more rows than we need on the views side and then enrich with
        // the title/cover/rating in a single follow-up query so we're
        // not joining reports into the views aggregation.
        var leaderboardCounts = await _db.ReportViews
            .AsNoTracking()
            .Where(v => orgReportIds.Contains(v.ReportId)
                     && v.ViewedAt >= fromUtc && v.ViewedAt <= toUtc)
            .GroupBy(v => v.ReportId)
            .Select(g => new { ReportId = g.Key, Views = (long)g.Count() })
            .OrderByDescending(x => x.Views)
            .Take(LeaderboardSize)
            .ToListAsync(ct);

        var topIds = leaderboardCounts.Select(x => x.ReportId).ToList();
        var ratingAggs = await _db.ReportRatings
            .AsNoTracking()
            .Where(r => topIds.Contains(r.ReportId)
                     && r.CreatedAt >= fromUtc && r.CreatedAt <= toUtc)
            .GroupBy(r => r.ReportId)
            .Select(g => new
            {
                ReportId = g.Key,
                Count = g.Count(),
                Avg = g.Average(r => (double)r.Rating),
            })
            .ToListAsync(ct);

        var ratingByReport = ratingAggs.ToDictionary(r => r.ReportId);

        var reportMeta = await _db.Reports
            .AsNoTracking()
            .Where(r => topIds.Contains(r.Id))
            .Select(r => new { r.Id, r.TitleAr, r.TitleEn, r.Slug, r.CoverImageUrl, r.CoverImageBaseKey })
            .ToListAsync(ct);

        var metaByReport = reportMeta.ToDictionary(r => r.Id);

        var topReports = new List<ReportLeaderboardItemDto>(leaderboardCounts.Count);
        foreach (var x in leaderboardCounts)
        {
            if (!metaByReport.TryGetValue(x.ReportId, out var meta))
                continue;

            ratingByReport.TryGetValue(x.ReportId, out var rating);
            var (coverUrl, coverVariants) = ResolveCover(meta.CoverImageUrl, meta.CoverImageBaseKey);
            topReports.Add(new ReportLeaderboardItemDto(
                meta.Id, meta.TitleAr, meta.TitleEn, meta.Slug, coverUrl, coverVariants,
                x.Views,
                rating?.Count ?? 0,
                rating is null ? 0m : Math.Round((decimal)rating.Avg, 2)));
        }

        return new OrganizationAnalyticsDto(
            fromUtc, toUtc,
            new AnalyticsTotalsDto(publishedReports, totalViews, totalRatings, averageRating),
            series,
            topReports);
    }

    public async Task<ReportAnalyticsDto> GetReportAnalyticsAsync(
        Guid currentUserId, Guid reportId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var orgId = await GetCallerOrgIdAsync(currentUserId, ct);
        var (fromUtc, toUtc) = NormalizeRange(from, to);

        var report = await _db.Reports
            .AsNoTracking()
            .Where(r => r.Id == reportId && r.OrganizationId == orgId)
            .Select(r => new { r.Id, r.TitleAr, r.TitleEn })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Report not found.");

        var totalViews = await _db.ReportViews
            .AsNoTracking()
            .Where(v => v.ReportId == reportId
                     && v.ViewedAt >= fromUtc && v.ViewedAt <= toUtc)
            .LongCountAsync(ct);

        var ratingsInRange = _db.ReportRatings
            .AsNoTracking()
            .Where(r => r.ReportId == reportId
                     && r.CreatedAt >= fromUtc && r.CreatedAt <= toUtc);

        var totalRatings = await ratingsInRange.CountAsync(ct);
        var averageRating = totalRatings == 0
            ? 0m
            : Math.Round((decimal)await ratingsInRange.AverageAsync(r => (double)r.Rating, ct), 2);

        var bucketed = await _db.ReportViews
            .AsNoTracking()
            .Where(v => v.ReportId == reportId
                     && v.ViewedAt >= fromUtc && v.ViewedAt <= toUtc)
            .GroupBy(v => v.ViewedAt.Date)
            .Select(g => new { Date = g.Key, Count = (long)g.Count() })
            .ToListAsync(ct);

        var series = ExpandDaily(bucketed.ToDictionary(b => DateOnly.FromDateTime(b.Date), b => b.Count),
                                 DateOnly.FromDateTime(fromUtc),
                                 DateOnly.FromDateTime(toUtc));

        return new ReportAnalyticsDto(
            report.Id, report.TitleAr, report.TitleEn, fromUtc, toUtc,
            totalViews, totalRatings, averageRating, series);
    }

    /// Resolve cover object keys to browser-loadable URLs. Covers live in the
    /// public GCS bucket — same rules as <see cref="ReportService.TryPublicUrl"/>.
    private (string? CoverImageUrl, CoverImagesDto? CoverImages) ResolveCover(
        string? coverImageUrl, string? coverImageBaseKey)
    {
        var baseKey = coverImageBaseKey;
        // Some rows only persisted the medium-variant key — derive the base
        // prefix so we can still emit the full srcset trio.
        if (string.IsNullOrWhiteSpace(baseKey)
            && !string.IsNullOrWhiteSpace(coverImageUrl)
            && coverImageUrl.EndsWith($"/{CoverImageVariants.MediumName}", StringComparison.Ordinal))
        {
            baseKey = coverImageUrl[..coverImageUrl.LastIndexOf('/')];
        }

        if (!string.IsNullOrWhiteSpace(baseKey))
        {
            try
            {
                var thumb = _files.GetPublicUrl($"{baseKey}/{CoverImageVariants.ThumbName}");
                var medium = _files.GetPublicUrl($"{baseKey}/{CoverImageVariants.MediumName}");
                var full = _files.GetPublicUrl($"{baseKey}/{CoverImageVariants.FullName}");
                return (medium, new CoverImagesDto(thumb, medium, full));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to build public cover variant URLs for baseKey={BaseKey}", baseKey);
            }
        }

        if (string.IsNullOrWhiteSpace(coverImageUrl))
            return (null, null);

        // Already a full URL (older API rows or manual backfills).
        if (coverImageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || coverImageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return (coverImageUrl, null);

        try { return (_files.GetPublicUrl(coverImageUrl), null); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to build public cover URL for objectKey={ObjectKey}", coverImageUrl);
            return (null, null);
        }
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

    /// Inclusive-day windowing: `from` snaps to 00:00 UTC, `to` snaps to
    /// 23:59:59.999 UTC so that "today" at any point during the day
    /// captures every minute of it. We also clamp the window to a
    /// reasonable max (366 days) to keep the daily series bounded —
    /// the chart can't render a year of points cleanly anyway.
    private static (DateTime From, DateTime To) NormalizeRange(DateTime from, DateTime to)
    {
        var fromUtc = DateTime.SpecifyKind(from.Date, DateTimeKind.Utc);
        var toUtc = DateTime.SpecifyKind(to.Date, DateTimeKind.Utc).AddDays(1).AddTicks(-1);

        if (fromUtc > toUtc)
            (fromUtc, toUtc) = (toUtc.Date, fromUtc.AddDays(1).AddTicks(-1));

        if ((toUtc - fromUtc).TotalDays > 366)
            fromUtc = toUtc.Date.AddDays(-365);

        return (fromUtc, toUtc);
    }

    /// Backfill missing days as zero so the chart renders a continuous
    /// line. Bucketed input only contains days that had at least one
    /// view; the chart wants every day in the range.
    private static IReadOnlyList<DailyViewsPointDto> ExpandDaily(
        Dictionary<DateOnly, long> bucketed, DateOnly from, DateOnly to)
    {
        var days = (to.DayNumber - from.DayNumber) + 1;
        var result = new List<DailyViewsPointDto>(days);
        for (var i = 0; i < days; i++)
        {
            var d = from.AddDays(i);
            result.Add(new DailyViewsPointDto(d, bucketed.TryGetValue(d, out var v) ? v : 0));
        }
        return result;
    }
}
