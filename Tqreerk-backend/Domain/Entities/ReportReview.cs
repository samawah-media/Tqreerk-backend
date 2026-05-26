using Taqreerk.Domain.Common;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Domain.Entities;

/// One row per review action. Created when a reviewer finishes a decision
/// (approve / reject / return for edit). The claim itself is tracked on the
/// Report (ClaimedByReviewerId / ClaimedAt) and isn't an entity here — only
/// completed reviews leave a paper trail in this table.
///
/// We use BaseEntity (not SoftDeletable) on purpose: review history is
/// audit-grade and shouldn't be soft-deleted. If a report is deleted the
/// reviews go with it via cascade.
public class ReportReview : BaseEntity
{
    public Guid ReportId { get; set; }
    public Guid ReviewerUserId { get; set; }
    public ReviewDecision Decision { get; set; }

    /// Notes the org sees. Required when decision is Rejected or ReturnedForEdit.
    public string? ReviewNotes { get; set; }

    /// Notes only the admin team sees — for handoffs and context between
    /// reviewers. Optional always.
    public string? InternalNotes { get; set; }

    public DateTime ReviewedAt { get; set; } = DateTime.UtcNow;

    /// How long the reviewer held the claim before submitting the decision.
    /// Computed as (ReviewedAt - ClaimedAt) in seconds at decision time.
    /// Null if the report wasn't claimed (shouldn't happen in normal flow).
    public int? ReviewDurationSeconds { get; set; }

    public Report Report { get; set; } = null!;
    public User Reviewer { get; set; } = null!;
}
