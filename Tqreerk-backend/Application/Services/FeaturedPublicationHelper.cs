using Microsoft.EntityFrameworkCore;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

/// Shared helpers for editorial featured placements — keeps approval,
/// admin curation and expiry worker in sync on Report.IsFeatured.
internal static class FeaturedPublicationHelper
{
    public const string OutputCacheTag = "featured";

    public static async Task SyncReportIsFeaturedAsync(
        TaqreerkDbContext db, Guid reportId, DateTime now, CancellationToken ct)
    {
        var hasActive = await db.FeaturedReports.AnyAsync(f =>
            f.ReportId == reportId
            && f.IsActive
            && (f.FeaturedFrom == null || f.FeaturedFrom <= now)
            && (f.FeaturedUntil == null || f.FeaturedUntil > now), ct);

        var report = await db.Reports.FirstOrDefaultAsync(r => r.Id == reportId, ct);
        if (report is not null)
            report.IsFeatured = hasActive;
    }

    /// Pins (or re-activates) a report at the front of HomepageCarousel.
    public static void PinToCarouselFront(
        TaqreerkDbContext db,
        Guid reportId,
        DateTime now,
        TimeSpan duration,
        Guid actingUserId,
        IList<FeaturedReport> carouselRows)
    {
        foreach (var row in carouselRows)
            row.Position++;

        var existing = carouselRows.FirstOrDefault(f => f.ReportId == reportId);
        if (existing is not null)
        {
            existing.IsActive = true;
            existing.FeaturedFrom = now;
            existing.FeaturedUntil = now.Add(duration);
            existing.Position = 0;
            existing.CreatedByUserId = actingUserId;
            return;
        }

        db.FeaturedReports.Add(new FeaturedReport
        {
            ReportId = reportId,
            Section = FeaturedSection.HomepageCarousel,
            Position = 0,
            FeaturedFrom = now,
            FeaturedUntil = now.Add(duration),
            IsActive = true,
            CreatedByUserId = actingUserId,
        });
    }
}
