using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.DTOs.Reports;

namespace Taqreerk.Application.Interfaces;

public interface IReviewService
{
    /// Paginated review queue. By default returns reports in PendingReview
    /// plus the caller's own UnderReview claims (so a reviewer sees what
    /// they're working on alongside the unclaimed pool).
    Task<PagedResult<ReviewQueueItemDto>> GetQueueAsync(
        Guid reviewerUserId,
        ReviewQueueRequest req,
        CancellationToken ct = default);

    /// Atomically claim a report for review. Race-safe via SELECT FOR UPDATE.
    /// Throws InvalidOperationException (→ 409) if another reviewer already
    /// holds the claim.
    Task<ReportForReviewDto> ClaimAsync(
        Guid reviewerUserId,
        Guid reportId,
        CancellationToken ct = default);

    /// Drop the claim back into the queue. Only the current claim-holder
    /// can release. Throws UnauthorizedAccessException (→ 401) otherwise.
    Task ReleaseAsync(
        Guid reviewerUserId,
        Guid reportId,
        CancellationToken ct = default);

    /// Fetch a report for the workspace page. The caller must either hold
    /// the claim (UnderReview) or have read access via being staff (the
    /// controller already gates this). Returns prior review history too.
    Task<ReportForReviewDto> GetForReviewAsync(
        Guid reviewerUserId,
        Guid reportId,
        CancellationToken ct = default);

    /// Approve the claimed report. Writes the review row, flips status
    /// through Approved → ProcessingAi, and enqueues the AI ingest job.
    /// Throws UnauthorizedAccessException if the caller doesn't hold the
    /// active claim.
    Task<ReportForReviewDto> ApproveAsync(
        Guid reviewerUserId,
        Guid reportId,
        ApproveDecisionRequest req,
        CancellationToken ct = default);

    /// Reject the claimed report (terminal — the org cannot re-submit).
    /// Requires reviewNotes (≥ 10 chars).
    Task<ReportForReviewDto> RejectAsync(
        Guid reviewerUserId,
        Guid reportId,
        RejectDecisionRequest req,
        CancellationToken ct = default);

    /// Return the report to the org for edits. Status flips to
    /// ReturnedForEdit; the org can re-upload from the dashboard which
    /// flips it back to PendingReview. Requires reviewNotes (≥ 10 chars).
    Task<ReportForReviewDto> ReturnForEditAsync(
        Guid reviewerUserId,
        Guid reportId,
        ReturnForEditDecisionRequest req,
        CancellationToken ct = default);

    /// Soft-delete a report (sets <c>DeletedAt</c>). Staff with
    /// <c>reports:delete</c> only — no active claim required. Removes any
    /// featured-placement rows for the report.
    Task DeleteAsync(Guid adminUserId, Guid reportId, CancellationToken ct = default);
}
