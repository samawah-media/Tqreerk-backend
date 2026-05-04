namespace Taqreerk.Application.DTOs.Admin;

/// Row in the review queue table. Lean — the workspace fetches the full
/// detail separately when the reviewer opens a report.
public record ReviewQueueItemDto(
    Guid Id,
    string Title,
    string Slug,
    /// "PendingReview" or "UnderReview" — same enum names as Report.Status.
    string Status,
    string? ReportType,
    string OriginalLanguage,
    int? PageCount,
    /// When the org submitted for review. Used for FIFO ordering and the
    /// "متأخر" badge on rows older than 24h.
    DateTime? SubmittedForReviewAt,
    /// Set only when status is UnderReview. The frontend uses this to show
    /// "you're reviewing this" vs "another reviewer has it".
    Guid? ClaimedByReviewerId,
    string? ClaimedByReviewerName,
    DateTime? ClaimedAt,
    Guid OrganizationId,
    string OrganizationNameAr,
    Guid? SectorId,
    string? SectorNameAr,
    /// Was this report sent back for edits before? Tells the reviewer to
    /// expect a previous review record they may want to read.
    bool HasPriorReviews
);

/// Filter shape for the queue. All optional. The controller maps query
/// params one-to-one onto this record.
public record ReviewQueueRequest(
    Guid? SectorId = null,
    Guid? OrganizationId = null,
    /// When true, only show reports the calling reviewer has currently claimed.
    /// When false / null, show the full pending pool (everyone's view).
    bool? AssignedToMe = null,
    /// "oldest" (default — FIFO), "newest", "priority". Priority is reserved
    /// for a future PR; today it's a synonym of "oldest".
    string? Sort = null,
    /// Comma-separated list of report statuses to include (e.g.
    /// "Approved,Published"). Matches the Report.Status enum names.
    /// When null or empty, the queue defaults to its historical behaviour:
    /// PendingReview + the calling reviewer's UnderReview claims. Setting
    /// this to a value lets staff see reports past the review stage
    /// (Approved / ProcessingAi / Published / Rejected / ReturnedForEdit)
    /// from the same admin queue page.
    string? Status = null,
    int Page = 1,
    int PageSize = 20
);

/// Full report metadata for the workspace page. Includes a signed PDF URL
/// and a snapshot of any prior reviews so the reviewer can read the
/// previous notes alongside the new submission.
public record ReportForReviewDto(
    Guid Id,
    string Title,
    string Slug,
    string Status,
    string? Description,
    string? ReportType,
    string OriginalLanguage,
    int? PublicationYear,
    DateOnly? PublicationDate,
    int? PageCount,
    string? FileUrl,
    string? CoverImageUrl,
    DateTime CreatedAt,
    DateTime? SubmittedForReviewAt,
    Guid? ClaimedByReviewerId,
    string? ClaimedByReviewerName,
    DateTime? ClaimedAt,
    Guid OrganizationId,
    string OrganizationNameAr,
    string OrganizationNameEn,
    Guid? SectorId,
    string? SectorNameAr,
    Guid? CountryId,
    string? CountryNameAr,
    /// Latest-first list of completed reviews on this report. Empty for
    /// first-time submissions.
    IReadOnlyList<ReviewHistoryItemDto> History
);

public record ReviewHistoryItemDto(
    Guid Id,
    string Decision,
    string ReviewerName,
    string? ReviewNotes,
    DateTime ReviewedAt,
    int? ReviewDurationSeconds
);

/// Approval is the lightest decision — notes are optional. The body still
/// carries an `internalNotes` slot for handoffs between admin team members
/// (the org never sees it).
public record ApproveDecisionRequest(
    string? ReviewNotes = null,
    string? InternalNotes = null
);

/// Rejection terminates the report's lifecycle. Notes shown to the org are
/// required so they understand why; we enforce ≥10 chars in the service.
public record RejectDecisionRequest(
    string ReviewNotes,
    /// Optional categorisation for analytics (e.g. "duplicate", "low_quality").
    /// Free-form for now — can become an enum once we have data on common
    /// reasons. Stored on the review row as InternalNotes when set.
    string? RejectionReasonCode = null,
    string? InternalNotes = null
);

/// Return-for-edit sends the report back to the org so they can upload a
/// new version. Notes are required so the org knows what to fix.
public record ReturnForEditDecisionRequest(
    string ReviewNotes,
    string? InternalNotes = null
);
