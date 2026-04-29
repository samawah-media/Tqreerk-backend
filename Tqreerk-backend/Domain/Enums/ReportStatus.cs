namespace Taqreerk.Domain.Enums;

/// Report workflow states. Persisted as strings, so reordering is fine but
/// renames require a data backfill. The order below mirrors the typical
/// lifecycle of an organization-uploaded report.
public enum ReportStatus
{
    /// Org is still composing the report — never sent for review.
    Draft,

    /// Org submitted for moderation; sitting in the admin review queue.
    PendingReview,

    /// A content reviewer has claimed the report and is reading it now.
    UnderReview,

    /// Reviewer asked for edits; report is back on the org's side, locked
    /// until they upload a new version.
    ReturnedForEdit,

    /// Reviewer approved. AI pipeline (ingest → summarize → translate) is
    /// queued or running.
    Approved,

    /// AI pipeline currently processing this report.
    ProcessingAi,

    /// Visible in the public library.
    Published,

    /// Removed from the public library by the org (soft archive).
    Archived,

    /// Reviewer rejected outright. Final state — no resubmission.
    Rejected
}
