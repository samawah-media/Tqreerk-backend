using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Me;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class MeService : IMeService
{
    private readonly TaqreerkDbContext _db;
    private readonly IFileStorage _files;

    public MeService(TaqreerkDbContext db, IFileStorage files)
    {
        _db = db;
        _files = files;
    }

    public async Task<IReadOnlyList<MySavedReportDto>> ListSavedReportsAsync(
        Guid userId, int take = 20, CancellationToken ct = default)
    {
        if (take < 1) take = 1;
        if (take > 100) take = 100;

        // Newest saves first. Soft-deleted reports are filtered by the
        // global query filter on Report — they won't appear here even if
        // the saved_reports row still exists.
        var rows = await _db.SavedReports
            .AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.SavedAt)
            .Take(take)
            .Select(s => new
            {
                s.Report.Id,
                s.Report.TitleAr,
                s.Report.TitleEn,
                s.Report.Slug,
                s.Report.CoverImageUrl,
                SectorNameAr = s.Report.Sector != null ? s.Report.Sector.NameAr : null,
                CountryNameAr = s.Report.Country != null ? s.Report.Country.NameAr : null,
                s.Report.PublicationYear,
                s.Report.PageCount,
                s.Report.ViewsCount,
                OrganizationNameAr = s.Report.Organization != null ? s.Report.Organization.NameAr : null,
                OrganizationLogoUrl = s.Report.Organization != null ? s.Report.Organization.LogoUrl : null,
                s.SavedAt,
            })
            .ToListAsync(ct);

        // Resolve cover + logo object keys to short-lived signed HTTPS
        // URLs the browser can render. Best-effort: a sign failure on
        // one row drops that single image to null and the card falls
        // back to the gradient placeholder.
        var dtos = new List<MySavedReportDto>(rows.Count);
        foreach (var r in rows)
        {
            var cover = await ResolveAsync(r.CoverImageUrl, ct);
            var logo = await ResolveAsync(r.OrganizationLogoUrl, ct);
            dtos.Add(new MySavedReportDto(
                r.Id, r.TitleAr, r.TitleEn, r.Slug, cover,
                r.SectorNameAr, r.CountryNameAr,
                r.PublicationYear, r.PageCount, r.ViewsCount,
                r.OrganizationNameAr, logo,
                r.SavedAt));
        }
        return dtos;
    }

    private async Task<string?> ResolveAsync(string? objectKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(objectKey)) return null;
        try { return await _files.GetReadUrlAsync(objectKey, ct: ct); }
        catch { return null; }
    }

    public async Task<IReadOnlyList<MySavedReportDto>> ListRecommendationsAsync(
        Guid userId, int take = 20, CancellationToken ct = default)
    {
        if (take < 1) take = 1;
        if (take > 50) take = 50;

        // Pull the user's sector interests up front. No interests → no
        // recommendations: returning empty here keeps the SQL plan simple
        // and lets the frontend render its empty state without a special
        // "no interests" signal — the empty list is the signal.
        var interestSectorIds = await _db.UserInterests
            .AsNoTracking()
            .Where(i => i.UserId == userId && i.SectorId != null)
            .Select(i => i.SectorId!.Value)
            .Distinct()
            .ToListAsync(ct);

        if (interestSectorIds.Count == 0)
            return Array.Empty<MySavedReportDto>();

        // Reports the user has already saved are excluded — they're
        // already in /my/library and don't need re-recommending. We
        // intentionally DO NOT exclude viewed reports: a quick read
        // doesn't mean the user wants the report off their feed, and
        // surfacing it again is a useful nudge to save for later.
        var savedReportIds = _db.SavedReports
            .AsNoTracking()
            .Where(s => s.UserId == userId)
            .Select(s => s.ReportId);

        var rows = await _db.Reports
            .AsNoTracking()
            .Where(r =>
                r.Status == ReportStatus.Published &&
                r.SectorId != null &&
                interestSectorIds.Contains(r.SectorId.Value) &&
                !savedReportIds.Contains(r.Id))
            // Highest-rated first, then most-popular as the tiebreaker.
            // Reports with no ratings yet (AvgRating = 0) drop to the
            // bottom — that's intentional, we'd rather surface vetted
            // content first.
            .OrderByDescending(r => r.AvgRating)
            .ThenByDescending(r => r.ViewsCount)
            .Take(take)
            .Select(r => new
            {
                r.Id,
                r.TitleAr,
                r.TitleEn,
                r.Slug,
                r.CoverImageUrl,
                SectorNameAr = r.Sector != null ? r.Sector.NameAr : null,
                CountryNameAr = r.Country != null ? r.Country.NameAr : null,
                r.PublicationYear,
                r.PageCount,
                r.ViewsCount,
                OrganizationNameAr = r.Organization != null ? r.Organization.NameAr : null,
                OrganizationLogoUrl = r.Organization != null ? r.Organization.LogoUrl : null,
                // Reuse MySavedReportDto for the response shape — there's
                // no SavedAt on a never-saved report, so we fall back to
                // PublishedAt (or CreatedAt) for the timestamp slot. The
                // dashboard sort isn't by this column so the surrogate is
                // harmless.
                Stamp = r.PublishedAt ?? r.CreatedAt,
            })
            .ToListAsync(ct);

        var dtos = new List<MySavedReportDto>(rows.Count);
        foreach (var r in rows)
        {
            var cover = await ResolveAsync(r.CoverImageUrl, ct);
            var logo = await ResolveAsync(r.OrganizationLogoUrl, ct);
            dtos.Add(new MySavedReportDto(
                r.Id, r.TitleAr, r.TitleEn, r.Slug, cover,
                r.SectorNameAr, r.CountryNameAr,
                r.PublicationYear, r.PageCount, r.ViewsCount,
                r.OrganizationNameAr, logo,
                r.Stamp));
        }
        return dtos;
    }

    public async Task<IReadOnlyList<MyActivityItemDto>> ListActivityAsync(
        Guid userId, int take = 10, CancellationToken ct = default)
    {
        if (take < 1) take = 1;
        if (take > 50) take = 50;

        // Left-join with reports so rows whose ResourceId no longer
        // resolves (deleted/unpublished report) still render — the
        // frontend falls back to the action type alone.
        var rows = await (
            from u in _db.UsageTracking.AsNoTracking()
            where u.UserId == userId
            orderby u.ConsumedAt descending
            join r in _db.Reports.AsNoTracking()
                on u.ResourceId equals (Guid?)r.Id into rj
            from r in rj.DefaultIfEmpty()
            select new
            {
                u.Id,
                u.ActionType,
                u.ResourceId,
                ReportTitleAr = r != null ? r.TitleAr : null,
                ReportTitleEn = r != null ? r.TitleEn : null,
                ReportSlug = r != null ? r.Slug : null,
                u.Metadata,
                OccurredAt = u.ConsumedAt,
            })
            .Take(take)
            .ToListAsync(ct);

        // Resolve the *additional* report IDs referenced from metadata
        // (e.g. AiCompare's secondary report set) in a single round trip,
        // then attach the resolved title/slug to each row. Rows with no
        // extra IDs just carry an empty list.
        var extraIds = new HashSet<Guid>();
        var perRowExtras = new Dictionary<Guid, List<Guid>>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Metadata)) continue;
            var ids = TryParseReportIds(row.Metadata);
            if (ids is null || ids.Count == 0) continue;
            // Exclude the primary ResourceId — it's already in ReportTitle.
            var extras = ids.Where(id => id != row.ResourceId).ToList();
            if (extras.Count == 0) continue;
            perRowExtras[row.Id] = extras;
            foreach (var id in extras) extraIds.Add(id);
        }

        var lookup = extraIds.Count == 0
            ? new Dictionary<Guid, (string TitleAr, string TitleEn, string Slug)>()
            : await _db.Reports
                .AsNoTracking()
                .Where(r => extraIds.Contains(r.Id))
                .Select(r => new { r.Id, r.TitleAr, r.TitleEn, r.Slug })
                .ToDictionaryAsync(r => r.Id, r => (r.TitleAr, r.TitleEn, r.Slug), ct);

        return rows
            .Select(row =>
            {
                var related = perRowExtras.TryGetValue(row.Id, out var extras)
                    ? extras
                        .Where(id => lookup.ContainsKey(id))
                        .Select(id => new ActivityRelatedReportDto(
                            id, lookup[id].TitleAr, lookup[id].TitleEn, lookup[id].Slug))
                        .ToList()
                    : new List<ActivityRelatedReportDto>();

                return new MyActivityItemDto(
                    row.Id,
                    row.ActionType,
                    row.ResourceId,
                    row.ReportTitleAr,
                    row.ReportTitleEn,
                    row.ReportSlug,
                    row.OccurredAt,
                    row.Metadata,
                    related);
            })
            .ToList();
    }

    /// Pull `reportIds` out of a usage_tracking.metadata JSON blob.
    /// Returns null when the blob isn't valid JSON or has no list there.
    private static IReadOnlyList<Guid>? TryParseReportIds(string metadata)
    {
        try
        {
            using var doc = JsonDocument.Parse(metadata);
            if (!doc.RootElement.TryGetProperty("reportIds", out var arr)) return null;
            if (arr.ValueKind != JsonValueKind.Array) return null;
            var ids = new List<Guid>(arr.GetArrayLength());
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String && Guid.TryParse(el.GetString(), out var g))
                    ids.Add(g);
            }
            return ids;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<PlanFeaturesDto> GetPlanFeaturesAsync(Guid userId, CancellationToken ct = default)
    {
        // Resolve the caller's active subscription + plan in one go.
        // If the row is missing, that's a misconfiguration (registration
        // should auto-link to the free plan) — surface it loudly so the
        // dev sees the broken backfill instead of returning a misleading
        // empty plan.
        var plan = await _db.Subscriptions
            .AsNoTracking()
            .Where(s => s.UserId == userId && s.Status == SubscriptionStatus.Active)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => s.Plan)
            .FirstOrDefaultAsync(ct);

        if (plan is null)
            throw new InvalidOperationException(
                $"User {userId} has no active subscription. Registration should auto-link to the free plan; check the backfill ran.");

        // This-month usage. Same window UsageService uses to enforce
        // the cap — first day of the current UTC month → start of next.
        var now = DateTime.UtcNow;
        var periodStart = new DateOnly(now.Year, now.Month, 1);
        var periodStartUtc = new DateTime(periodStart.Year, periodStart.Month, periodStart.Day, 0, 0, 0, DateTimeKind.Utc);
        var resetsAt = periodStartUtc.AddMonths(1);

        var consumed = await _db.UsageTracking
            .AsNoTracking()
            .Where(u => u.UserId == userId && u.BillingPeriodStart == periodStart)
            .GroupBy(u => u.ActionType)
            .Select(g => new { ActionType = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var consumedByAction = consumed.ToDictionary(
            x => x.ActionType.ToString(),
            x => x.Count,
            StringComparer.Ordinal);

        return new PlanFeaturesDto(
            plan.Id,
            plan.NameAr,
            plan.NameEn,
            plan.TargetType.ToString(),
            new PlanLimitsDto(
                plan.IndividualReadsLimit,
                plan.IndividualSavedReportsLimit,
                plan.IndividualDownloadsLimit,
                plan.UserLimit,
                plan.ReportsUploadLimit,
                plan.FeaturedReportsMonthly,
                plan.AiSummarizeLimit,
                plan.AiKeyFindingsLimit,
                plan.AiTranslateLimit,
                plan.AiSimilarSuggestionsLimit,
                plan.AiCompareLimit,
                plan.AiCompareMaxReports),
            new PlanFlagsDto(
                plan.HasNotifications,
                plan.HasAdvancedSearch,
                plan.HasInteractions,
                plan.HasExclusiveContent,
                plan.HasTrendAnalysis,
                plan.HasIndicatorExtraction,
                plan.HasSmartRecommendations,
                plan.HasKnowledgeGraph,
                plan.HasSmartAlerts,
                plan.HasOpportunityDiscovery,
                plan.HasSectoralAnalysis),
            new PlanTiersDto(
                plan.AiAccessLevel,
                plan.AdvancedSearchPrecision,
                plan.OrgPageTier,
                plan.SupportTier,
                plan.DashboardTier,
                plan.NotificationsTier,
                plan.UpdatesCadence),
            new UsageSnapshotDto(
                periodStartUtc,
                resetsAt,
                consumedByAction));
    }
}
