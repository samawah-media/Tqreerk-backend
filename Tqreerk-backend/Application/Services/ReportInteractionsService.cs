using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Reports;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class ReportInteractionsService : IReportInteractionsService
{
    /// View dedupe window per (report, ip). Longer than a tab refresh,
    /// shorter than a normal reading session — keeps the counter close
    /// to "real distinct sessions" without a session table.
    private static readonly TimeSpan ViewDedupeWindow = TimeSpan.FromHours(1);

    private readonly TaqreerkDbContext _db;

    public ReportInteractionsService(TaqreerkDbContext db)
    {
        _db = db;
    }

    public async Task<ReportInteractionStateDto> RateAsync(
        Guid userId, Guid reportId, RateReportRequest req, CancellationToken ct = default)
    {
        var report = await _db.Reports
            .FirstOrDefaultAsync(r => r.Id == reportId && r.Status == ReportStatus.Published, ct)
            ?? throw new KeyNotFoundException("Report not found.");

        var existing = await _db.ReportRatings
            .FirstOrDefaultAsync(x => x.ReportId == reportId && x.UserId == userId, ct);

        if (existing is null)
        {
            _db.ReportRatings.Add(new ReportRating
            {
                ReportId = reportId,
                UserId = userId,
                Rating = req.Stars,
                Review = req.Review,
            });
        }
        else
        {
            existing.Rating = req.Stars;
            existing.Review = req.Review;
        }

        await _db.SaveChangesAsync(ct);
        await RecomputeRatingAggregateAsync(report, ct);
        return await BuildStateAsync(userId, reportId, ct);
    }

    public async Task<ReportInteractionStateDto> UnrateAsync(
        Guid userId, Guid reportId, CancellationToken ct = default)
    {
        var report = await _db.Reports
            .FirstOrDefaultAsync(r => r.Id == reportId && r.Status == ReportStatus.Published, ct)
            ?? throw new KeyNotFoundException("Report not found.");

        var row = await _db.ReportRatings
            .FirstOrDefaultAsync(x => x.ReportId == reportId && x.UserId == userId, ct);
        if (row is not null)
        {
            _db.ReportRatings.Remove(row);
            await _db.SaveChangesAsync(ct);
            await RecomputeRatingAggregateAsync(report, ct);
        }
        return await BuildStateAsync(userId, reportId, ct);
    }

    public async Task<ReportInteractionStateDto> SaveAsync(
        Guid userId, Guid reportId, CancellationToken ct = default)
    {
        await EnsureReportExistsAsync(reportId, ct);

        var exists = await _db.SavedReports
            .AnyAsync(x => x.UserId == userId && x.ReportId == reportId, ct);
        if (!exists)
        {
            _db.SavedReports.Add(new SavedReport
            {
                UserId = userId,
                ReportId = reportId,
            });
            await _db.SaveChangesAsync(ct);
        }
        return await BuildStateAsync(userId, reportId, ct);
    }

    public async Task<ReportInteractionStateDto> UnsaveAsync(
        Guid userId, Guid reportId, CancellationToken ct = default)
    {
        await EnsureReportExistsAsync(reportId, ct);

        var row = await _db.SavedReports
            .FirstOrDefaultAsync(x => x.UserId == userId && x.ReportId == reportId, ct);
        if (row is not null)
        {
            _db.SavedReports.Remove(row);
            await _db.SaveChangesAsync(ct);
        }
        return await BuildStateAsync(userId, reportId, ct);
    }

    public async Task<ReportInteractionStateDto> RecommendAsync(
        Guid userId, Guid reportId, string? shareChannel, CancellationToken ct = default)
    {
        await EnsureReportExistsAsync(reportId, ct);

        // No unique index here — dedupe in code. A second recommend call
        // is treated as "update the share-channel annotation" rather than
        // appending a duplicate row.
        var row = await _db.ReportRecommendations
            .FirstOrDefaultAsync(x => x.ReportId == reportId && x.UserId == userId, ct);
        if (row is null)
        {
            _db.ReportRecommendations.Add(new ReportRecommendation
            {
                ReportId = reportId,
                UserId = userId,
                ShareChannel = shareChannel,
            });
        }
        else
        {
            row.ShareChannel = shareChannel;
        }
        await _db.SaveChangesAsync(ct);
        return await BuildStateAsync(userId, reportId, ct);
    }

    public async Task<ReportInteractionStateDto> UnrecommendAsync(
        Guid userId, Guid reportId, CancellationToken ct = default)
    {
        await EnsureReportExistsAsync(reportId, ct);

        // Remove every row this user has recommended this report under
        // (defensive — should be at most one given the create path above).
        var rows = await _db.ReportRecommendations
            .Where(x => x.ReportId == reportId && x.UserId == userId)
            .ToListAsync(ct);
        if (rows.Count > 0)
        {
            _db.ReportRecommendations.RemoveRange(rows);
            await _db.SaveChangesAsync(ct);
        }
        return await BuildStateAsync(userId, reportId, ct);
    }

    public async Task RecordViewAsync(
        Guid reportId, Guid? userId, string? ipAddress, string? userAgent,
        CancellationToken ct = default)
    {
        var report = await _db.Reports
            .FirstOrDefaultAsync(r => r.Id == reportId && r.Status == ReportStatus.Published, ct);
        if (report is null) return; // anonymous endpoint — soft-fail on bad ids

        // Per-IP+report dedupe in a 1h window. Using IP rather than the
        // user id catches repeat anonymous visitors too.
        if (!string.IsNullOrWhiteSpace(ipAddress))
        {
            var since = DateTime.UtcNow - ViewDedupeWindow;
            var recent = await _db.ReportViews
                .AsNoTracking()
                .AnyAsync(v => v.ReportId == reportId
                            && v.IpAddress == ipAddress
                            && v.ViewedAt >= since, ct);
            if (recent) return;
        }

        _db.ReportViews.Add(new ReportView
        {
            ReportId = reportId,
            UserId = userId,
            IpAddress = ipAddress,
            UserAgent = userAgent,
        });
        report.ViewsCount += 1;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<MyReportInteractionDto> GetMyStateAsync(
        Guid userId, Guid reportId, CancellationToken ct = default)
    {
        await EnsureReportExistsAsync(reportId, ct);

        var saved = await _db.SavedReports
            .AsNoTracking()
            .AnyAsync(x => x.UserId == userId && x.ReportId == reportId, ct);
        var recommended = await _db.ReportRecommendations
            .AsNoTracking()
            .AnyAsync(x => x.UserId == userId && x.ReportId == reportId, ct);
        var rating = await _db.ReportRatings
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.ReportId == reportId)
            .Select(x => new { x.Rating, x.Review })
            .FirstOrDefaultAsync(ct);

        return new MyReportInteractionDto(
            SavedByMe: saved,
            RecommendedByMe: recommended,
            MyRating: rating?.Rating,
            MyReview: rating?.Review);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task EnsureReportExistsAsync(Guid reportId, CancellationToken ct)
    {
        var exists = await _db.Reports
            .AsNoTracking()
            .AnyAsync(r => r.Id == reportId && r.Status == ReportStatus.Published, ct);
        if (!exists) throw new KeyNotFoundException("Report not found.");
    }

    /// Recompute the running aggregates on the report row. Single GROUP BY
    /// query — cheap at our scale, keeps reads lock-free without a snapshot
    /// job. The caller should already have the report loaded.
    private async Task RecomputeRatingAggregateAsync(Report report, CancellationToken ct)
    {
        var stats = await _db.ReportRatings
            .AsNoTracking()
            .Where(r => r.ReportId == report.Id)
            .GroupBy(_ => 1)
            .Select(g => new { Avg = g.Average(x => (decimal)x.Rating), Count = g.Count() })
            .FirstOrDefaultAsync(ct);

        if (stats is null)
        {
            report.AvgRating = 0m;
            report.RatingsCount = 0;
        }
        else
        {
            report.AvgRating = Math.Round(stats.Avg, 2);
            report.RatingsCount = stats.Count;
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task<ReportInteractionStateDto> BuildStateAsync(
        Guid userId, Guid reportId, CancellationToken ct)
    {
        var snap = await _db.Reports
            .AsNoTracking()
            .Where(r => r.Id == reportId)
            .Select(r => new
            {
                r.AvgRating,
                r.RatingsCount,
                r.ViewsCount,
                RecommendationCount = r.Recommendations.Count,
            })
            .FirstAsync(ct);

        var mine = await GetMyStateAsync(userId, reportId, ct);

        return new ReportInteractionStateDto(
            ReportId: reportId,
            AvgRating: snap.AvgRating,
            RatingsCount: snap.RatingsCount,
            ViewsCount: snap.ViewsCount,
            RecommendationCount: snap.RecommendationCount,
            Mine: mine);
    }
}
