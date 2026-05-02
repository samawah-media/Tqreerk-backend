using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.DTOs.Reports;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class ReviewService : IReviewService
{
    private const int MinNotesLength = 10;

    private readonly TaqreerkDbContext _db;
    private readonly IFileStorage _files;
    private readonly IReportAiService _ai;
    private readonly IAdminActionLogger _audit;
    private readonly ILogger<ReviewService> _logger;

    public ReviewService(
        TaqreerkDbContext db,
        IFileStorage files,
        IReportAiService ai,
        IAdminActionLogger audit,
        ILogger<ReviewService> logger)
    {
        _db = db;
        _files = files;
        _ai = ai;
        _audit = audit;
        _logger = logger;
    }

    public async Task<PagedResult<ReviewQueueItemDto>> GetQueueAsync(
        Guid reviewerUserId,
        ReviewQueueRequest req,
        CancellationToken ct = default)
    {
        var page = Math.Max(1, req.Page);
        var pageSize = Math.Clamp(req.PageSize, 1, 100);

        // The queue shows two buckets together:
        //   1. Reports waiting in PendingReview (the unclaimed pool).
        //   2. Reports the calling reviewer is currently working on
        //      (UnderReview, claimed by them).
        // Other reviewers' active claims are intentionally hidden so the pool
        // doesn't look "full" of work that isn't actually available.
        var q = _db.Reports
            .AsNoTracking()
            .Where(r =>
                r.Status == ReportStatus.PendingReview
                || (r.Status == ReportStatus.UnderReview
                    && r.ClaimedByReviewerId == reviewerUserId));

        if (req.AssignedToMe == true)
        {
            q = q.Where(r =>
                r.Status == ReportStatus.UnderReview
                && r.ClaimedByReviewerId == reviewerUserId);
        }

        if (req.SectorId is { } sectorId)
            q = q.Where(r => r.SectorId == sectorId);

        if (req.OrganizationId is { } orgId)
            q = q.Where(r => r.OrganizationId == orgId);

        // FIFO is the default — fairness for orgs that submit early. The
        // optional "newest" mode is for reviewers who want to triage the
        // freshest material; "priority" is reserved for when we add an
        // explicit priority column later (it's a synonym of "oldest" today).
        q = (req.Sort ?? "oldest") switch
        {
            "newest" => q.OrderByDescending(r => r.SubmittedForReviewAt ?? r.CreatedAt),
            _ => q.OrderBy(r => r.SubmittedForReviewAt ?? r.CreatedAt),
        };

        var total = await q.CountAsync(ct);

        var pageQ = q.Skip((page - 1) * pageSize).Take(pageSize);

        // Pull joined data via projection — single SQL with the org/sector
        // names, plus an EXISTS subquery for HasPriorReviews so we can mark
        // resubmissions in the queue without loading the history.
        var rows = await pageQ
            .Select(r => new
            {
                r.Id,
                r.Title,
                r.Slug,
                r.Status,
                r.ReportType,
                r.OriginalLanguage,
                r.PageCount,
                r.SubmittedForReviewAt,
                r.ClaimedByReviewerId,
                ClaimedByReviewerName = r.ClaimedByReviewer != null ? r.ClaimedByReviewer.FullName : null,
                r.ClaimedAt,
                r.OrganizationId,
                OrganizationNameAr = r.Organization.NameAr,
                r.SectorId,
                SectorNameAr = r.Sector != null ? r.Sector.NameAr : null,
                HasPriorReviews = _db.ReportReviews.Any(rr => rr.ReportId == r.Id),
            })
            .ToListAsync(ct);

        var items = rows
            .Select(r => new ReviewQueueItemDto(
                r.Id,
                r.Title,
                r.Slug,
                r.Status.ToString(),
                r.ReportType,
                r.OriginalLanguage,
                r.PageCount,
                r.SubmittedForReviewAt,
                r.ClaimedByReviewerId,
                r.ClaimedByReviewerName,
                r.ClaimedAt,
                r.OrganizationId,
                r.OrganizationNameAr,
                r.SectorId,
                r.SectorNameAr,
                r.HasPriorReviews
            ))
            .ToList();

        return new PagedResult<ReviewQueueItemDto>(items, total, page, pageSize);
    }

    public async Task<ReportForReviewDto> ClaimAsync(
        Guid reviewerUserId,
        Guid reportId,
        CancellationToken ct = default)
    {
        // Race-safe claim via SELECT … FOR UPDATE inside a transaction. Two
        // reviewers clicking "Claim" on the same report at the same time
        // serialize through the row lock; the loser sees PendingReview is no
        // longer the status and falls through to the InvalidOperationException
        // (→ 409 Conflict via the global exception middleware).
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Idempotent: if the calling reviewer already holds the claim we
        // just return the report — no need to fail when they re-claim.
        var report = await _db.Reports
            .FromSqlInterpolated($@"
                SELECT * FROM reports
                WHERE ""Id"" = {reportId}
                  AND ""DeletedAt"" IS NULL
                FOR UPDATE")
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Report not found.");

        var alreadyMine =
            report.Status == ReportStatus.UnderReview
            && report.ClaimedByReviewerId == reviewerUserId;

        if (!alreadyMine)
        {
            if (report.Status != ReportStatus.PendingReview)
            {
                // Most likely scenario: another reviewer claimed it a beat
                // before us. The frontend turns the 409 into a friendly
                // toast and refreshes the queue.
                throw new InvalidOperationException(
                    "This report is no longer available to claim.");
            }

            report.Status = ReportStatus.UnderReview;
            report.ClaimedByReviewerId = reviewerUserId;
            report.ClaimedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Reviewer {ReviewerId} claimed report {ReportId}",
            reviewerUserId, reportId);

        return await BuildReportForReviewAsync(reportId, ct)
            ?? throw new InvalidOperationException("Report disappeared after claim.");
    }

    public async Task ReleaseAsync(
        Guid reviewerUserId,
        Guid reportId,
        CancellationToken ct = default)
    {
        var report = await _db.Reports.FirstOrDefaultAsync(r => r.Id == reportId, ct)
            ?? throw new KeyNotFoundException("Report not found.");

        if (report.Status != ReportStatus.UnderReview
            || report.ClaimedByReviewerId != reviewerUserId)
        {
            // Either it's not currently claimed, or someone else holds it.
            // Don't reveal which — both 401 from the reviewer's perspective.
            throw new UnauthorizedAccessException(
                "You can only release a report you currently hold.");
        }

        report.Status = ReportStatus.PendingReview;
        report.ClaimedByReviewerId = null;
        report.ClaimedAt = null;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Reviewer {ReviewerId} released report {ReportId}",
            reviewerUserId, reportId);
    }

    public async Task<ReportForReviewDto> GetForReviewAsync(
        Guid reviewerUserId,
        Guid reportId,
        CancellationToken ct = default)
    {
        var dto = await BuildReportForReviewAsync(reportId, ct)
            ?? throw new KeyNotFoundException("Report not found.");

        // Visibility rules: any platform staff can read any report's details
        // for the queue/history workflow. But if the report is currently
        // claimed by someone else, we still let staff read it — they just
        // can't decide on it (the decision methods below check the claim).
        _ = reviewerUserId;
        return dto;
    }

    // ── decisions ────────────────────────────────────────────────────────────

    public async Task<ReportForReviewDto> ApproveAsync(
        Guid reviewerUserId,
        Guid reportId,
        ApproveDecisionRequest req,
        CancellationToken ct = default)
    {
        var report = await LoadClaimedReportAsync(reviewerUserId, reportId, ct);

        // Order matters: write the audit row first (with the approved
        // decision), then advance the status, THEN enqueue the AI ingest
        // job. EnqueueIngestAsync writes its own AiJob row + sets the
        // Report.Status to PendingReview again (its own state machine
        // marker — see ReportAiService) which is wrong for our workflow,
        // so we override the status to ProcessingAi at the end.
        var review = WriteReviewRow(report, reviewerUserId, ReviewDecision.Approved,
            req.ReviewNotes, req.InternalNotes);

        report.Status = ReportStatus.Approved;
        report.ClaimedByReviewerId = null;
        report.ClaimedAt = null;
        await _db.SaveChangesAsync(ct);

        // AI kickoff. EnqueueIngestAsync writes a Pending Ingestion row
        // into ai_jobs (with step=ingest+summarize for the AI worker) and
        // bumps Report.Status to ProcessingAi internally.
        //
        // We split the exceptions deliberately:
        //   - Structural failures (report missing, file_url null, FK
        //     violation) are real bugs in the approval flow — let them
        //     bubble up so the admin sees a 500 with a clear message
        //     instead of a silently approved report sitting at Approved
        //     with no AI jobs.
        //   - Quota exceedance is a recoverable, business-level state —
        //     log it, leave Report.Status at Approved, and let staff
        //     re-trigger from the admin UI later.
        try
        {
            await _ai.EnqueueIngestAsync(report.Id, ct);
        }
        catch (QuotaExceededException ex)
        {
            _logger.LogWarning(ex,
                "AI pipeline kickoff for report {ReportId} blocked by quota; status stays at Approved",
                report.Id);
        }
        // Anything else (KeyNotFoundException / InvalidOperationException /
        // DbUpdateException / etc.) propagates so the caller sees the cause.

        _logger.LogInformation(
            "Reviewer {ReviewerId} APPROVED report {ReportId} (review {ReviewId})",
            reviewerUserId, report.Id, review.Id);

        await _audit.LogAsync(
            adminUserId: reviewerUserId,
            actionType: "report.approved",
            targetEntityType: "report",
            targetEntityId: report.Id,
            reason: req.ReviewNotes,
            afterState: new { status = report.Status.ToString(), reviewId = review.Id },
            ct: ct);

        return await BuildReportForReviewAsync(report.Id, ct)
            ?? throw new InvalidOperationException("Report disappeared after decision.");
    }

    public async Task<ReportForReviewDto> RejectAsync(
        Guid reviewerUserId,
        Guid reportId,
        RejectDecisionRequest req,
        CancellationToken ct = default)
    {
        RequireNotes(req.ReviewNotes, "Review notes are required for rejection.");

        var report = await LoadClaimedReportAsync(reviewerUserId, reportId, ct);

        // Stash the rejection-reason code on InternalNotes if present —
        // there's no dedicated column today, but we want it captured for
        // analytics later. Format: "[reason: code] free-form text".
        var internalNotes = string.IsNullOrWhiteSpace(req.RejectionReasonCode)
            ? req.InternalNotes
            : $"[reason: {req.RejectionReasonCode.Trim()}] {req.InternalNotes ?? string.Empty}".Trim();

        var review = WriteReviewRow(report, reviewerUserId, ReviewDecision.Rejected,
            req.ReviewNotes, internalNotes);

        report.Status = ReportStatus.Rejected;
        report.ClaimedByReviewerId = null;
        report.ClaimedAt = null;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Reviewer {ReviewerId} REJECTED report {ReportId} (review {ReviewId}, reason {Reason})",
            reviewerUserId, report.Id, review.Id, req.RejectionReasonCode ?? "(none)");

        await _audit.LogAsync(
            adminUserId: reviewerUserId,
            actionType: "report.rejected",
            targetEntityType: "report",
            targetEntityId: report.Id,
            reason: req.ReviewNotes,
            afterState: new
            {
                status = report.Status.ToString(),
                reviewId = review.Id,
                rejectionReasonCode = req.RejectionReasonCode,
            },
            ct: ct);

        return await BuildReportForReviewAsync(report.Id, ct)
            ?? throw new InvalidOperationException("Report disappeared after decision.");
    }

    public async Task<ReportForReviewDto> ReturnForEditAsync(
        Guid reviewerUserId,
        Guid reportId,
        ReturnForEditDecisionRequest req,
        CancellationToken ct = default)
    {
        RequireNotes(req.ReviewNotes, "Review notes are required when returning for edit.");

        var report = await LoadClaimedReportAsync(reviewerUserId, reportId, ct);

        var review = WriteReviewRow(report, reviewerUserId, ReviewDecision.ReturnedForEdit,
            req.ReviewNotes, req.InternalNotes);

        report.Status = ReportStatus.ReturnedForEdit;
        report.ClaimedByReviewerId = null;
        report.ClaimedAt = null;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Reviewer {ReviewerId} RETURNED report {ReportId} for edit (review {ReviewId})",
            reviewerUserId, report.Id, review.Id);

        await _audit.LogAsync(
            adminUserId: reviewerUserId,
            actionType: "report.returned_for_edit",
            targetEntityType: "report",
            targetEntityId: report.Id,
            reason: req.ReviewNotes,
            afterState: new { status = report.Status.ToString(), reviewId = review.Id },
            ct: ct);

        return await BuildReportForReviewAsync(report.Id, ct)
            ?? throw new InvalidOperationException("Report disappeared after decision.");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// Loads a report and ensures the calling reviewer is the active
    /// claim-holder. Used by all three decision actions — the claim is the
    /// authority that lets you finalise.
    private async Task<Report> LoadClaimedReportAsync(
        Guid reviewerUserId,
        Guid reportId,
        CancellationToken ct)
    {
        var report = await _db.Reports.FirstOrDefaultAsync(r => r.Id == reportId, ct)
            ?? throw new KeyNotFoundException("Report not found.");

        if (report.Status != ReportStatus.UnderReview
            || report.ClaimedByReviewerId != reviewerUserId)
        {
            // Don't leak whether someone else holds it — both look the
            // same to the UI: "you can't decide on a report you don't hold".
            throw new UnauthorizedAccessException(
                "You can only decide on reports you currently hold.");
        }

        return report;
    }

    /// Persist the audit row + return it (still untracked-saved; SaveChanges
    /// happens once on the calling action so a failure rolls back together).
    private ReportReview WriteReviewRow(
        Report report,
        Guid reviewerUserId,
        ReviewDecision decision,
        string? reviewNotes,
        string? internalNotes)
    {
        var now = DateTime.UtcNow;
        var durationSeconds = report.ClaimedAt.HasValue
            ? (int?)Math.Max(0, (now - report.ClaimedAt.Value).TotalSeconds)
            : null;

        var review = new ReportReview
        {
            ReportId = report.Id,
            ReviewerUserId = reviewerUserId,
            Decision = decision,
            ReviewNotes = string.IsNullOrWhiteSpace(reviewNotes) ? null : reviewNotes.Trim(),
            InternalNotes = string.IsNullOrWhiteSpace(internalNotes) ? null : internalNotes.Trim(),
            ReviewedAt = now,
            ReviewDurationSeconds = durationSeconds,
        };
        _db.ReportReviews.Add(review);
        return review;
    }

    private static void RequireNotes(string? notes, string message)
    {
        if (string.IsNullOrWhiteSpace(notes) || notes.Trim().Length < MinNotesLength)
        {
            // The 10-char minimum is enforced server-side as a defence in
            // depth — the modal UI also blocks short notes, but a raw API
            // caller could otherwise send "ok" and breeze through.
            throw new ArgumentException(message);
        }
    }

    private async Task<ReportForReviewDto?> BuildReportForReviewAsync(
        Guid reportId,
        CancellationToken ct)
    {
        var row = await _db.Reports
            .AsNoTracking()
            .Where(r => r.Id == reportId)
            .Select(r => new
            {
                r.Id,
                r.Title,
                r.Slug,
                r.Status,
                r.Description,
                r.ReportType,
                r.OriginalLanguage,
                r.PublicationYear,
                r.PublicationDate,
                r.PageCount,
                r.FileUrl,
                r.CoverImageUrl,
                r.CreatedAt,
                r.SubmittedForReviewAt,
                r.ClaimedByReviewerId,
                ClaimedByReviewerName = r.ClaimedByReviewer != null ? r.ClaimedByReviewer.FullName : null,
                r.ClaimedAt,
                r.OrganizationId,
                OrganizationNameAr = r.Organization.NameAr,
                OrganizationNameEn = r.Organization.NameEn,
                r.SectorId,
                SectorNameAr = r.Sector != null ? r.Sector.NameAr : null,
                r.CountryId,
                CountryNameAr = r.Country != null ? r.Country.NameAr : null,
            })
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;

        var history = await _db.ReportReviews
            .AsNoTracking()
            .Where(rr => rr.ReportId == reportId)
            .OrderByDescending(rr => rr.CreatedAt)
            .Select(rr => new ReviewHistoryItemDto(
                rr.Id,
                rr.Decision.ToString(),
                rr.Reviewer.FullName,
                rr.ReviewNotes,
                rr.ReviewedAt,
                rr.ReviewDurationSeconds
            ))
            .ToListAsync(ct);

        var fileUrl = await TrySignAsync(row.FileUrl, ct);
        var coverUrl = await TrySignAsync(row.CoverImageUrl, ct);

        return new ReportForReviewDto(
            row.Id,
            row.Title,
            row.Slug,
            row.Status.ToString(),
            row.Description,
            row.ReportType,
            row.OriginalLanguage,
            row.PublicationYear,
            row.PublicationDate,
            row.PageCount,
            fileUrl,
            coverUrl,
            row.CreatedAt,
            row.SubmittedForReviewAt,
            row.ClaimedByReviewerId,
            row.ClaimedByReviewerName,
            row.ClaimedAt,
            row.OrganizationId,
            row.OrganizationNameAr,
            row.OrganizationNameEn,
            row.SectorId,
            row.SectorNameAr,
            row.CountryId,
            row.CountryNameAr,
            history
        );
    }

    private async Task<string?> TrySignAsync(string? objectKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(objectKey)) return null;
        try { return await _files.GetReadUrlAsync(objectKey, ct: ct); }
        catch { return null; }
    }
}
