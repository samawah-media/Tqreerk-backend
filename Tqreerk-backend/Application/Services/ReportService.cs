using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Reports;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class ReportService : IReportService
{
    private const long MaxFileSizeBytes = 50 * 1024 * 1024; // 50 MB — matches Feature 6 plan
    private const long MaxCoverImageBytes = 5 * 1024 * 1024; // 5 MB — typical thumbnail
    private const string ReportFolder = "reports";
    private const string CoverFolder = "covers";
    private static readonly string[] AllowedContentTypes =
    [
        "application/pdf",
    ];
    private static readonly string[] AllowedCoverContentTypes =
    [
        "image/png",
        "image/jpeg",
        "image/jpg",
        "image/webp",
    ];

    private readonly TaqreerkDbContext _db;
    private readonly IFileStorage _files;
    private readonly ILogger<ReportService> _logger;

    public ReportService(
        TaqreerkDbContext db,
        IFileStorage files,
        ILogger<ReportService> logger)
    {
        _db = db;
        _files = files;
        _logger = logger;
    }

    public async Task<ReportDetailDto> CreateAsync(
        Guid currentUserId,
        CreateReportRequest req,
        UploadedFile reportFile,
        UploadedFile? coverImage,
        CancellationToken ct = default)
    {
        if (!AllowedContentTypes.Contains(reportFile.ContentType, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Only PDF files are accepted.");

        if (reportFile.Content.CanSeek && reportFile.Content.Length > MaxFileSizeBytes)
            throw new ArgumentException("File exceeds the 50 MB size limit.");

        if (string.IsNullOrWhiteSpace(req.Title))
            throw new ArgumentException("Title is required.");

        if (req.PublicationDate.HasValue && req.PublicationDate.Value > DateOnly.FromDateTime(DateTime.UtcNow))
            throw new ArgumentException("Publication date cannot be in the future.");

        if (coverImage is not null)
        {
            if (!AllowedCoverContentTypes.Contains(coverImage.ContentType, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("Cover image must be PNG, JPEG, or WEBP.");
            if (coverImage.Content.CanSeek && coverImage.Content.Length > MaxCoverImageBytes)
                throw new ArgumentException("Cover image exceeds the 5 MB size limit.");
        }

        var orgId = await GetCallerOrgIdAsync(currentUserId, ct);

        if (req.SectorId.HasValue && !await _db.Sectors.AnyAsync(s => s.Id == req.SectorId.Value, ct))
            throw new ArgumentException("Unknown sector.");
        if (req.CountryId.HasValue && !await _db.Countries.AnyAsync(c => c.Id == req.CountryId.Value, ct))
            throw new ArgumentException("Unknown country.");

        var slug = await GenerateUniqueSlugAsync(req.Title, ct);
        var storedReport = await _files.UploadAsync(
            reportFile.Content, reportFile.OriginalFileName, reportFile.ContentType,
            $"{ReportFolder}/{orgId}", ct);

        // Cover image is optional. Upload separately under covers/{orgId}/...
        // so we can sign it on its own when surfacing thumbnails. We don't
        // wrap the two uploads in a transaction — if the cover upload fails
        // after the PDF is in place we still save the report and log the
        // failure rather than ditch the whole upload.
        string? coverKey = null;
        if (coverImage is not null)
        {
            try
            {
                var storedCover = await _files.UploadAsync(
                    coverImage.Content, coverImage.OriginalFileName, coverImage.ContentType,
                    $"{CoverFolder}/{orgId}", ct);
                coverKey = storedCover.ObjectKey;
                _logger.LogInformation(
                    "Cover image uploaded for org {OrgId}: {ObjectKey} ({Size} bytes, {ContentType})",
                    orgId, storedCover.ObjectKey, storedCover.SizeBytes, storedCover.ContentType);
            }
            catch (Exception ex)
            {
                // Don't fail the whole report upload because of a cover-image
                // glitch — the PDF is the load-bearing artefact. The user can
                // re-upload a cover later via report edit (PR 7).
                _logger.LogWarning(ex,
                    "Cover image upload failed for org {OrgId} (filename={Name}, type={Type}); proceeding without cover.",
                    orgId, coverImage.OriginalFileName, coverImage.ContentType);
            }
        }
        else
        {
            _logger.LogInformation("No cover image provided for new report in org {OrgId}", orgId);
        }

        // The report goes straight into the admin review queue. Status =
        // PendingReview, SubmittedForReviewAt set now. AI pipeline is NOT
        // triggered here — it fires when an admin approves the report (see
        // ReportAiService.EnqueueIngestAsync called from the future
        // AdminReviewsController.ApproveAsync, PR A4).
        var now = DateTime.UtcNow;
        var report = new Report
        {
            OrganizationId = orgId,
            UploadedByUserId = currentUserId,
            Title = req.Title.Trim(),
            Slug = slug,
            Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description!.Trim(),
            ReportType = string.IsNullOrWhiteSpace(req.ReportType) ? null : req.ReportType!.Trim(),
            OriginalLanguage = string.IsNullOrWhiteSpace(req.OriginalLanguage) ? "ar" : req.OriginalLanguage!.Trim().ToLowerInvariant(),
            PublicationYear = req.PublicationYear,
            PublicationDate = req.PublicationDate,
            SectorId = req.SectorId,
            CountryId = req.CountryId,
            FileUrl = storedReport.ObjectKey,
            CoverImageUrl = coverKey,
            Status = ReportStatus.PendingReview,
            SubmittedForReviewAt = now,
            SourceType = ReportSourceType.Organization,
        };
        _db.Reports.Add(report);
        await _db.SaveChangesAsync(ct);

        return await BuildDetailAsync(report.Id, ct)
            ?? throw new InvalidOperationException("Report saved but reload failed.");
    }

    public async Task<PagedResult<ReportListItemDto>> ListMineAsync(
        Guid currentUserId,
        int page,
        int pageSize,
        string? query,
        CancellationToken ct = default)
    {
        var orgId = await GetCallerOrgIdAsync(currentUserId, ct);

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var q = _db.Reports
            .AsNoTracking()
            .Where(r => r.OrganizationId == orgId);

        if (!string.IsNullOrWhiteSpace(query))
        {
            var like = $"%{query.Trim()}%";
            q = q.Where(r => EF.Functions.ILike(r.Title, like)
                          || (r.Description != null && EF.Functions.ILike(r.Description, like)));
        }

        var total = await q.CountAsync(ct);

        // Pull raw rows then sign cover URLs in a second pass — signing is
        // async and can't run inside the EF projection.
        var raw = await q
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new
            {
                r.Id,
                r.Title,
                r.Slug,
                r.Status,
                r.ReportType,
                r.OriginalLanguage,
                r.PublicationYear,
                r.PageCount,
                r.ViewsCount,
                r.DownloadsCount,
                r.AvgRating,
                r.IsFeatured,
                r.CoverImageUrl,
                r.SectorId,
                SectorNameAr = r.Sector != null ? r.Sector.NameAr : null,
                r.CountryId,
                CountryNameAr = r.Country != null ? r.Country.NameAr : null,
                r.CreatedAt,
            })
            .ToListAsync(ct);

        var rows = new List<ReportListItemDto>(raw.Count);
        foreach (var r in raw)
        {
            rows.Add(new ReportListItemDto(
                r.Id,
                r.Title,
                r.Slug,
                r.Status.ToString(),
                r.ReportType,
                r.OriginalLanguage,
                r.PublicationYear,
                r.PageCount,
                r.ViewsCount,
                r.DownloadsCount,
                r.AvgRating,
                r.IsFeatured,
                await TrySignAsync(r.CoverImageUrl, ct),
                r.SectorId,
                r.SectorNameAr,
                r.CountryId,
                r.CountryNameAr,
                r.CreatedAt
            ));
        }

        return new PagedResult<ReportListItemDto>(rows, total, page, pageSize);
    }

    private async Task<string?> TrySignAsync(string? objectKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(objectKey)) return null;
        try { return await _files.GetReadUrlAsync(objectKey, ct: ct); }
        catch { return null; }
    }

    public async Task<ReportDetailDto> GetAsync(Guid currentUserId, Guid reportId, CancellationToken ct = default)
    {
        var orgId = await GetCallerOrgIdAsync(currentUserId, ct);

        var dto = await BuildDetailAsync(reportId, ct)
            ?? throw new KeyNotFoundException("Report not found.");

        if (dto.OrganizationId != orgId)
            throw new UnauthorizedAccessException("This report belongs to another organization.");

        return dto;
    }

    public async Task DeleteAsync(Guid currentUserId, Guid reportId, CancellationToken ct = default)
    {
        var orgId = await GetCallerOrgIdAsync(currentUserId, ct);

        var report = await _db.Reports.FirstOrDefaultAsync(r => r.Id == reportId, ct)
            ?? throw new KeyNotFoundException("Report not found.");
        if (report.OrganizationId != orgId)
            throw new UnauthorizedAccessException("This report belongs to another organization.");

        // Soft delete via the global filter — set DeletedAt; keep the GCS object
        // around for now in case we need to recover it. Hard cleanup is a later
        // background job that runs after a 30-day grace period.
        report.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<ReportDetailDto> ResubmitAsync(
        Guid currentUserId,
        Guid reportId,
        UploadedFile reportFile,
        UploadedFile? coverImage,
        CancellationToken ct = default)
    {
        if (!AllowedContentTypes.Contains(reportFile.ContentType, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Only PDF files are accepted.");
        if (reportFile.Content.CanSeek && reportFile.Content.Length > MaxFileSizeBytes)
            throw new ArgumentException("File exceeds the 50 MB size limit.");
        if (coverImage is not null)
        {
            if (!AllowedCoverContentTypes.Contains(coverImage.ContentType, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("Cover image must be PNG, JPEG, or WEBP.");
            if (coverImage.Content.CanSeek && coverImage.Content.Length > MaxCoverImageBytes)
                throw new ArgumentException("Cover image exceeds the 5 MB size limit.");
        }

        var orgId = await GetCallerOrgIdAsync(currentUserId, ct);

        var report = await _db.Reports.FirstOrDefaultAsync(r => r.Id == reportId, ct)
            ?? throw new KeyNotFoundException("Report not found.");
        if (report.OrganizationId != orgId)
            throw new UnauthorizedAccessException("This report belongs to another organization.");

        // Resubmit only makes sense for reports the reviewer kicked back.
        // Letting an org "resubmit" a Published or PendingReview report
        // would silently overwrite its file — that's edit territory, not
        // resubmit. Block it so the only way to reach this code is the
        // ReturnedForEdit flow.
        if (report.Status != ReportStatus.ReturnedForEdit)
        {
            throw new InvalidOperationException(
                "Only reports that were returned for edit can be resubmitted.");
        }

        // Upload the new file. We keep the OLD file in GCS too — the
        // version history isn't a separate table yet (Plan §Feature 7
        // mentions report_versions but it's out of scope for this PR), so
        // leaving the old object around is the safest "no data loss"
        // option until that lands.
        var storedReport = await _files.UploadAsync(
            reportFile.Content, reportFile.OriginalFileName, reportFile.ContentType,
            $"{ReportFolder}/{orgId}", ct);
        report.FileUrl = storedReport.ObjectKey;

        if (coverImage is not null)
        {
            try
            {
                var storedCover = await _files.UploadAsync(
                    coverImage.Content, coverImage.OriginalFileName, coverImage.ContentType,
                    $"{CoverFolder}/{orgId}", ct);
                report.CoverImageUrl = storedCover.ObjectKey;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Resubmit: cover image upload failed for org {OrgId}; keeping previous cover.",
                    orgId);
            }
        }

        // Back to the queue. Bump SubmittedForReviewAt so FIFO ordering
        // treats the resubmission as fresh — the org effectively sent a
        // new report and the original wait time shouldn't penalise the
        // re-review (or skip ahead unfairly, depending on perspective;
        // we match the plan's "treat resubmits as fresh" guidance).
        report.Status = ReportStatus.PendingReview;
        report.SubmittedForReviewAt = DateTime.UtcNow;
        report.ClaimedByReviewerId = null;
        report.ClaimedAt = null;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Org user {UserId} resubmitted report {ReportId} after edit",
            currentUserId, report.Id);

        return await BuildDetailAsync(report.Id, ct)
            ?? throw new InvalidOperationException("Report saved but reload failed.");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<Guid> GetCallerOrgIdAsync(Guid userId, CancellationToken ct)
    {
        var orgId = await _db.OrganizationMembers
            .Where(m => m.UserId == userId && m.IsActive)
            .Select(m => (Guid?)m.OrganizationId)
            .FirstOrDefaultAsync(ct);

        if (!orgId.HasValue)
            throw new UnauthorizedAccessException("Caller is not a member of any organization.");

        return orgId.Value;
    }

    private async Task<ReportDetailDto?> BuildDetailAsync(Guid reportId, CancellationToken ct)
    {
        var row = await _db.Reports
            .AsNoTracking()
            .Where(r => r.Id == reportId)
            .Select(r => new
            {
                r.Id,
                r.OrganizationId,
                OrgNameAr = r.Organization.NameAr,
                r.UploadedByUserId,
                r.Title,
                r.Slug,
                r.Description,
                r.ReportType,
                r.OriginalLanguage,
                r.PublicationYear,
                r.PublicationDate,
                r.PageCount,
                r.FileUrl,
                r.CoverImageUrl,
                r.ViewsCount,
                r.DownloadsCount,
                r.AvgRating,
                r.RatingsCount,
                r.IsFeatured,
                r.Status,
                r.SourceType,
                r.SectorId,
                SectorNameAr = r.Sector != null ? r.Sector.NameAr : null,
                r.CountryId,
                CountryNameAr = r.Country != null ? r.Country.NameAr : null,
                r.CreatedAt,
            })
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;

        // Resolve signed read URLs for both the PDF and the cover image.
        // Best-effort — if signing fails (e.g. the bucket isn't configured
        // locally) we return null so the frontend can fall back to its
        // gradient placeholder rather than 500 the whole request.
        string? signedUrl = null;
        if (!string.IsNullOrWhiteSpace(row.FileUrl))
        {
            try { signedUrl = await _files.GetReadUrlAsync(row.FileUrl, ct: ct); }
            catch { signedUrl = null; }
        }

        string? coverUrl = null;
        if (!string.IsNullOrWhiteSpace(row.CoverImageUrl))
        {
            try { coverUrl = await _files.GetReadUrlAsync(row.CoverImageUrl, ct: ct); }
            catch { coverUrl = null; }
        }

        // Latest review note — drives the org-side dashboard banner when
        // the report was returned for edit or rejected. We only fetch the
        // single most-recent row to keep this cheap.
        var latestReview = await _db.ReportReviews
            .AsNoTracking()
            .Where(rr => rr.ReportId == row.Id)
            .OrderByDescending(rr => rr.CreatedAt)
            .Select(rr => new { rr.Decision, rr.ReviewNotes, rr.ReviewedAt })
            .FirstOrDefaultAsync(ct);

        return new ReportDetailDto(
            row.Id,
            row.OrganizationId,
            row.OrgNameAr,
            row.UploadedByUserId,
            row.Title,
            row.Slug,
            row.Description,
            row.ReportType,
            row.OriginalLanguage,
            row.PublicationYear,
            row.PublicationDate,
            row.PageCount,
            signedUrl,
            coverUrl,
            row.ViewsCount,
            row.DownloadsCount,
            row.AvgRating,
            row.RatingsCount,
            row.IsFeatured,
            row.Status.ToString(),
            row.SourceType.ToString(),
            row.SectorId,
            row.SectorNameAr,
            row.CountryId,
            row.CountryNameAr,
            latestReview?.Decision.ToString(),
            latestReview?.ReviewNotes,
            latestReview?.ReviewedAt,
            row.CreatedAt
        );
    }

    /// Slug = transliterated/sanitized title + 6-char shortid. Conflict-loop
    /// (rare; Title-derived slugs already have entropy from the suffix) appends
    /// a fresh shortid until uniqueness is achieved.
    ///
    /// IgnoreQueryFilters bypasses the soft-delete query filter so we still
    /// see deleted rows here. The DB-level UNIQUE index on `Slug` does not
    /// include a `WHERE deleted_at IS NULL` predicate, so a re-used slug from
    /// a soft-deleted row would still violate the constraint at INSERT time.
    private async Task<string> GenerateUniqueSlugAsync(string title, CancellationToken ct)
    {
        var baseSlug = ToSlug(title);
        if (string.IsNullOrEmpty(baseSlug)) baseSlug = "report";

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var candidate = $"{baseSlug}-{ShortId()}";
            var taken = await _db.Reports
                .IgnoreQueryFilters()
                .AnyAsync(r => r.Slug == candidate, ct);
            if (!taken) return candidate;
        }
        // Extremely unlikely; fall back to a pure GUID-based slug.
        return $"report-{Guid.NewGuid():N}".Substring(0, 24);
    }

    private static string ToSlug(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        // Keep Arabic letters, ASCII letters, digits; replace everything else
        // with a hyphen. Collapse runs of hyphens.
        var sb = new StringBuilder(input.Length);
        foreach (var c in input.Trim().Normalize(NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else sb.Append('-');
        }
        var slug = sb.ToString().ToLower(CultureInfo.InvariantCulture);
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        if (slug.Length > 80) slug = slug[..80].TrimEnd('-');
        return slug;
    }

    private static string ShortId()
    {
        // 6 lowercase alphanumeric chars from a fresh GUID. Plenty for our
        // collision domain and short enough to keep slugs readable.
        return Guid.NewGuid().ToString("N").Substring(0, 6);
    }
}
